using Unextract.Core.Analysis;
using Unextract.Core.Results;
using Unextract.Core.Target;

namespace Unextract.Core.Deletion;

// テスト用の差し込み口 (PLAN.md §4)。製品 CLI からは設定しない。handle は削除用ハンドルで、テストはこれを閉じない。
public sealed class DeletionHooks
{
    // 削除用オープンの直前 (再オープン直前)。
    public Action<MatchedFile>? BeforeOpen { get; init; }

    // 同一性の再検証が全て一致した直後。
    public Action<MatchedFile, IDeletionHandle>? AfterRevalidation { get; init; }

    // 2回目の全バイト比較で、target の最初の読み取りの直前 (再比較中)。
    public Action<MatchedFile, IDeletionHandle>? DuringRecompare { get; init; }

    // 最終確認の直前。
    public Action<MatchedFile, IDeletionHandle>? BeforeFinalCheck { get; init; }

    // 最終確認の直後、削除の指示の直前。
    public Action<MatchedFile, IDeletionHandle>? BeforeDisposition { get; init; }
}

// 削除用オープン (段階1) の失敗の分類 (PLAN.md §4 の対応表)。表に無いコードは全て停止とする。
internal static class DeletionOpenErrors
{
    public const int AccessDenied = 5;
    public const int SharingViolation = 32;

    // 32 と 5 だけが識別確認に進む。2・3 (対象・途中のパスの消失)、reparse・名前解決・クラウド関連、その他全ては停止。
    public static bool RequiresIdentityCheck(int error) => error is SharingViolation or AccessDenied;
}

// 削除フェーズ (SPEC §3 の 6、§8.3、§8.4)。各 MATCHED について、期待パスで削除用ハンドルを1回だけ開き、
// 同じハンドルで 2. 同一性の再検証 → 3. 2回目の全バイト比較 (Strict のみ) → 4. 最終確認 → 5. 削除の指示 → 6. 成立確認 を行う。
// オープンの後はパスを使わない (識別確認は段階1が失敗したときだけで、削除はしない)。
// 削除用ハンドルは各対象の処理の中で using によって閉じるため、停止・例外を含むどの経路でも、その対象の処理が終わった時点で閉じられている。
// 停止した後の対象は処理しない。既に削除したファイルは戻さない。
public sealed class DeletionPhase : IDeletionPhase
{
    // FILE_DISPOSITION_FLAG_DELETE (0x1) | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS (0x2)。
    // FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE (0x10) は含めない (DEC-10、read-only を最後の防壁として残す)。
    public const uint DispositionFlags = 0x3;

    private const uint FileAttributeReparsePoint = 0x400;

    private readonly IDeletionProbe _probe;
    private readonly DeletionHooks _hooks;

    public DeletionPhase(IDeletionProbe probe, DeletionHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _hooks = hooks ?? new DeletionHooks();
    }

    public DeletionReport Delete(DeletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidates = request.Candidates;
        var deleted = new List<ZipEntryRef>();
        var failed = new List<DeleteFailure>();
        for (var i = 0; i < candidates.Count; i++)
        {
            request.Progress?.Invoke(i + 1, candidates.Count);

            var file = candidates[i];
            var outcome = DeleteOne(file, request);
            switch (outcome.Kind)
            {
                case OutcomeKind.Deleted:
                    deleted.Add(file.Entry);
                    break;
                case OutcomeKind.Failed:
                    failed.Add(new DeleteFailure(file.Entry, outcome.Reason!));
                    break;
                default:
                    var stop = new DeletionStop(file.Entry, outcome.Reason!, outcome.PossiblyDeleted);
                    return new DeletionReport(deleted, failed, stop, candidates.Count - i - 1);
            }
        }

        return new DeletionReport(deleted, failed, null, 0);
    }

    private Outcome DeleteOne(MatchedFile file, DeletionRequest request)
    {
        var dispositionRequested = false;
        try
        {
            _hooks.BeforeOpen?.Invoke(file);

            // 1. 期待パスで削除用ハンドルを開く。パスを使うのはここだけ。
            var opened = _probe.OpenForDeletion(file.ExpectedPath);
            if (!opened.Succeeded)
            {
                return OpenFailed(file, opened.Error, opened.Describe());
            }

            using var handle = opened.Value;

            // 2. 同一性の再検証。
            if (Revalidate(handle, file.Snapshot) is { } mismatch)
            {
                return Outcome.Stop($"同一性の再検証で不一致: {mismatch}");
            }

            _hooks.AfterRevalidation?.Invoke(file, handle);

            // 3. 2回目の全バイト比較。初回と同じ比較器で、違反は種類を問わず停止 (SPEC §5.2、§8.4)。
            // Fast はこの手順だけを行わない (SPEC §15.4)。1、2、4、5、6 は両モードで同じ。
            if (request.Mode == RunMode.Strict)
            {
                var content = request.Contents.GetContent(file.Entry.Index);
                IComparisonHandle reader = _hooks.DuringRecompare is { } during ? new FirstReadHook(handle, () => during(file, handle)) : handle;
                var compared = request.Comparer.Compare(content, reader, ComparisonPass.Recheck);
                switch (compared.Verdict)
                {
                    case ContentVerdict.Fatal:
                        var detail = compared.Detail is null ? string.Empty : $" ({compared.Detail})";
                        return Outcome.Stop($"2回目の全バイト比較で異常: {FatalKindText.Describe(compared.FatalKind!.Value)}{detail}");
                    case ContentVerdict.Mismatch:
                        return Outcome.Stop("2回目の全バイト比較で内容が一致しません");
                }
            }

            // 4. 最終確認。2回目の比較の間に起き得る属性変更・ADS 作成・hardlink 追加・削除保留を検出する。
            _hooks.BeforeFinalCheck?.Invoke(file, handle);
            if (FinalCheck(handle, file.Snapshot) is { } changed)
            {
                return Outcome.Stop($"最終確認で不一致: {changed}");
            }

            // 5. 同じハンドルへの削除の指示。失敗は種類を問わず停止 (DEC-12)。
            _hooks.BeforeDisposition?.Invoke(file, handle);
            dispositionRequested = true;
            var disposition = handle.SetDispositionEx(DispositionFlags);
            if (!disposition.Succeeded)
            {
                // 指示の失敗では DeletePending は false のままのはずだが (PoC 1〜3)、確かめられなければ「削除された可能性あり」とする。
                var after = handle.GetStandardInformation();
                var possibly = !after.Succeeded || after.Value.DeletePending;
                return Outcome.Stop($"削除の指示が失敗: {disposition.Describe()}", possibly);
            }

            // 6. 成立確認。API の成功だけでは成立としない (DEC-11)。
            var confirmed = handle.GetStandardInformation();
            if (!confirmed.Succeeded)
            {
                return Outcome.Stop($"削除の成立を確認できません: {confirmed.Describe()}", possiblyDeleted: true);
            }

            if (!confirmed.Value.DeletePending)
            {
                return Outcome.Stop("削除の成立を確認できません: 削除の指示は成功を返したが DeletePending が false", possiblyDeleted: true);
            }

            return Outcome.Deleted;
        }
        catch (Exception ex)
        {
            // 想定外の例外 (フックからの例外を含む) は停止。削除用ハンドルは using で閉じられている。
            return Outcome.Stop(
                $"想定外の例外 ({ex.GetType().Name}: {ex.Message})",
                possiblyDeleted: dispositionRequested);
        }
    }

    // 段階1の失敗 (SPEC §8.4、PLAN.md §4)。32・5 は識別確認で「スナップショットと一致する通常ファイルに見える」ときだけ DELETE_FAILED。
    // 識別確認は拒否された削除用オープンとは別のオープンであり、同じ個体を見たことも、拒否の理由も保証しない (SPEC §8.4 の限界)。
    // 一致しても削除はせず、残して次へ進むだけなので、この限界は誤削除につながらない。不一致・失敗は停止。
    private Outcome OpenFailed(MatchedFile file, int error, string description)
    {
        if (!DeletionOpenErrors.RequiresIdentityCheck(error))
        {
            return Outcome.Stop($"削除用に開けません ({description})");
        }

        var identity = _probe.CheckIdentity(file.ExpectedPath);
        if (!identity.Succeeded)
        {
            return Outcome.Stop($"削除用に開けません ({description})。識別確認も失敗 ({identity.Describe()})");
        }

        if (IdentityMismatch(identity.Value, file.Snapshot) is { } mismatch)
        {
            return Outcome.Stop($"削除用に開けません ({description})。識別確認でスナップショットと不一致: {mismatch}");
        }

        return Outcome.Failed(
            $"削除用に開けません ({description})。識別確認の時点ではスナップショットと一致する通常ファイルに見えるため、削除せずに残しました");
    }

    // SPEC §8.4 の識別確認の比較項目: File ID (ボリュームシリアルを含む)、親 File ID、最終パス、ディレクトリでない、reparse でない、
    // DeletePending が false。
    internal static string? IdentityMismatch(IdentityCheckInfo info, TargetSnapshot snapshot)
    {
        if (info.Id != new VolumeFileId(snapshot.VolumeSerialNumber, snapshot.FileId))
        {
            return "File ID";
        }

        if (info.ParentFileId != snapshot.ParentFileId)
        {
            return "親 File ID";
        }

        if (!string.Equals(info.FinalPath, snapshot.FinalPath, StringComparison.Ordinal))
        {
            return "最終パス";
        }

        if (info.IsDirectory)
        {
            return "ディレクトリ";
        }

        if (IsReparse(info.Attributes, info.ReparseTag))
        {
            return "reparse point";
        }

        if (info.DeletePending)
        {
            return "削除保留中";
        }

        return null;
    }

    // 段階2: File ID とボリュームシリアル、親 File ID、最終パス、EndOfFile、LastWriteTime、ChangeTime、属性、リンク数、
    // ストリーム一覧、reparse 状態がスナップショットと完全に一致し、Directory と DeletePending が false であること。
    // 親 File ID と最終パスは、片方だけでは検出できない差し替えがあるため、どちらも省略しない (DEC-13)。
    // 取得の失敗も不一致として扱う (停止)。
    private static string? Revalidate(IDeletionHandle handle, TargetSnapshot snapshot)
    {
        var id = handle.GetVolumeFileId();
        if (!id.Succeeded)
        {
            return id.Describe();
        }

        if (id.Value != new VolumeFileId(snapshot.VolumeSerialNumber, snapshot.FileId))
        {
            return "File ID";
        }

        var parent = handle.GetParentFileId();
        if (!parent.Succeeded)
        {
            return parent.Describe();
        }

        if (parent.Value != snapshot.ParentFileId)
        {
            return "親 File ID";
        }

        var finalPath = handle.GetFinalPath();
        if (!finalPath.Succeeded)
        {
            return finalPath.Describe();
        }

        if (!string.Equals(finalPath.Value, snapshot.FinalPath, StringComparison.Ordinal))
        {
            return "最終パス";
        }

        var standard = handle.GetStandardInformation();
        if (!standard.Succeeded)
        {
            return standard.Describe();
        }

        if (standard.Value.IsDirectory)
        {
            return "ディレクトリ";
        }

        if (standard.Value.DeletePending)
        {
            return "削除保留中";
        }

        if (standard.Value.EndOfFile != snapshot.EndOfFile)
        {
            return "EndOfFile";
        }

        if (standard.Value.NumberOfLinks != snapshot.NumberOfLinks)
        {
            return "リンク数";
        }

        var basic = handle.GetBasicInformation();
        if (!basic.Succeeded)
        {
            return basic.Describe();
        }

        if (basic.Value.LastWriteTime != snapshot.LastWriteTime)
        {
            return "LastWriteTime";
        }

        if (basic.Value.ChangeTime != snapshot.ChangeTime)
        {
            return "ChangeTime";
        }

        if (basic.Value.Attributes != snapshot.Attributes)
        {
            return "属性";
        }

        var tag = handle.GetAttributeTagInformation();
        if (!tag.Succeeded)
        {
            return tag.Describe();
        }

        if (tag.Value.ReparseTag != snapshot.ReparseTag || IsReparse(tag.Value.Attributes, tag.Value.ReparseTag))
        {
            return "reparse 状態";
        }

        var streams = handle.GetStreams();
        if (!streams.Succeeded)
        {
            return streams.Describe();
        }

        if (!streams.Value.SequenceEqual(snapshot.Streams))
        {
            return "ストリーム一覧";
        }

        return null;
    }

    // 段階4: リンク数、ストリーム一覧、属性、DeletePending をもう一度確認する。取得の失敗も不一致として扱う (停止)。
    private static string? FinalCheck(IDeletionHandle handle, TargetSnapshot snapshot)
    {
        var standard = handle.GetStandardInformation();
        if (!standard.Succeeded)
        {
            return standard.Describe();
        }

        if (standard.Value.DeletePending)
        {
            return "削除保留中";
        }

        if (standard.Value.NumberOfLinks != snapshot.NumberOfLinks)
        {
            return "リンク数";
        }

        var streams = handle.GetStreams();
        if (!streams.Succeeded)
        {
            return streams.Describe();
        }

        if (!streams.Value.SequenceEqual(snapshot.Streams))
        {
            return "ストリーム一覧";
        }

        var basic = handle.GetBasicInformation();
        if (!basic.Succeeded)
        {
            return basic.Describe();
        }

        if (basic.Value.Attributes != snapshot.Attributes)
        {
            return "属性";
        }

        return null;
    }

    private static bool IsReparse(uint attributes, uint reparseTag) =>
        (attributes & FileAttributeReparsePoint) != 0 || reparseTag != 0;

    private enum OutcomeKind
    {
        Deleted,
        Failed,
        Stop,
    }

    private readonly record struct Outcome(OutcomeKind Kind, string? Reason, bool PossiblyDeleted)
    {
        public static Outcome Deleted => new(OutcomeKind.Deleted, null, false);

        public static Outcome Failed(string reason) => new(OutcomeKind.Failed, reason, false);

        public static Outcome Stop(string reason, bool possiblyDeleted = false) => new(OutcomeKind.Stop, reason, possiblyDeleted);
    }

    // 2回目の比較で target を最初に読む直前にフックを呼ぶ (テスト用の「再比較中」)。それ以外は削除用ハンドルにそのまま委ねる。
    private sealed class FirstReadHook(IDeletionHandle inner, Action hook) : IComparisonHandle
    {
        private bool _called;

        public ProbeResult<VolumeFileId> GetVolumeFileId() => inner.GetVolumeFileId();

        public ProbeResult<StandardInformation> GetStandardInformation() => inner.GetStandardInformation();

        public ProbeResult<BasicInformation> GetBasicInformation() => inner.GetBasicInformation();

        public ProbeResult<AttributeTagInformation> GetAttributeTagInformation() => inner.GetAttributeTagInformation();

        public ProbeResult<IReadOnlyList<StreamEntry>> GetStreams() => inner.GetStreams();

        public ProbeResult<FileId> GetParentFileId() => inner.GetParentFileId();

        public ProbeResult<string> GetFinalPath() => inner.GetFinalPath();

        public ProbeResult<int> Read(Span<byte> buffer)
        {
            if (!_called)
            {
                _called = true;
                hook();
            }

            return inner.Read(buffer);
        }

        // 削除用ハンドルは DeleteOne の using が閉じる。
        public void Dispose()
        {
        }
    }
}
