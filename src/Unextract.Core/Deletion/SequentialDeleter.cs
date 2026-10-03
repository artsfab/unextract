using Unextract.Core.Analysis;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Deletion;

// delete の1エントリの結果 (SPEC §10.3)。
public enum DeleteStatus
{
    Deleted,
    Modified,
    Missing,
    SkippedSpecialFile,
    DeleteFailed,
    Stopped,
}

public static class DeleteStatusExtensions
{
    public static string ToDisplayString(this DeleteStatus status) => status switch
    {
        DeleteStatus.Deleted => "DELETED",
        DeleteStatus.Modified => "MODIFIED",
        DeleteStatus.Missing => "MISSING",
        DeleteStatus.SkippedSpecialFile => "SKIPPED_SPECIAL_FILE",
        DeleteStatus.DeleteFailed => "DELETE_FAILED",
        DeleteStatus.Stopped => "STOPPED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

// 処理したファイルエントリ1件の結果。Target は期待パス (\\?\ 形式、表示用)。Reason は DELETE_FAILED と STOPPED の理由。
// PossiblyDeleted は STOPPED の対象が「削除された可能性あり」か (削除の指示の後に成立を確認できなかった場合など)。
public sealed record DeleteEntryResult(
    ZipEntryRef Entry,
    string Target,
    DeleteStatus Status,
    SkipReason? SkipReason = null,
    string? Reason = null,
    bool PossiblyDeleted = false);

// delete の結果。Results は処理したファイルエントリの結果 (処理した順。STOP なら最後が STOPPED)。
// DirectoryCount は処理したディレクトリエントリの件数 (結果行を出さない)。NotProcessedCount は STOP の後に処理しなかった件数。
public sealed record DeleteReport(IReadOnlyList<DeleteEntryResult> Results, int DirectoryCount, int NotProcessedCount)
{
    public DeleteEntryResult? Stop => Results.Count > 0 && Results[^1].Status == DeleteStatus.Stopped ? Results[^1] : null;

    public int Count(DeleteStatus status) => Results.Count(r => r.Status == status);
}

// テスト用の差し込み口 (PLAN.md §4 の H1〜H5)。製品 CLI からは設定しない。handle は削除用ハンドルで、テストはこれを閉じない。
public sealed class DeleteHooks
{
    // H1: エントリの解決 (と事前判定) の後、削除用オープンの直前。引数は期待パス。
    public Action<ZipEntryRef, string>? BeforeOpen { get; init; }

    // H2: 削除用オープンの直後、照合の前。
    public Action<ZipEntryRef, IDeletionHandle>? AfterOpen { get; init; }

    // H3: 全バイト比較中 = target の最初の読み取りの直前 (Strict のみ)。
    public Action<ZipEntryRef, IDeletionHandle>? DuringCompare { get; init; }

    // H4: 最終確認の直前。
    public Action<ZipEntryRef, IDeletionHandle>? BeforeFinalCheck { get; init; }

    // H5: 削除の指示の直前 (最終確認の後)。
    public Action<ZipEntryRef, IDeletionHandle>? BeforeDisposition { get; init; }
}

// 逐次削除の入力。Entries は処理対象 (全エントリ、または --entries で選んだエントリ。ZIP の順)。
// Progress は (n, total) で各エントリの処理の前に呼ばれる (Processing n / total)。OnResult はファイルエントリの結果ごとに呼ばれる。
public sealed record DeleteRequest(
    IReadOnlyList<ValidatedZipEntry> Entries,
    IZipContentProvider Contents,
    IFileSystemProbe Probe,
    IDeletionProbe DeletionProbe,
    TargetRoot Root,
    VolumeFileId ArchiveIdentity,
    Limits Limits,
    RunMode Mode = RunMode.Strict,
    Action<int, int>? Progress = null,
    Action<DeleteEntryResult>? OnResult = null,
    DeleteHooks? Hooks = null);

// 削除用オープンの失敗の分類 (PLAN.md §4 の対応表)。表に無いコードは全て STOP とする。
internal static class DeletionOpenErrors
{
    public const int AccessDenied = 5;
    public const int SharingViolation = 32;

    // 32 と 5 だけが識別確認に進む。2・3 (対象・途中のパスの消失)、reparse・名前解決・クラウド関連、その他全ては STOP。
    public static bool RequiresIdentityCheck(int error) => error is SharingViolation or AccessDenied;

    public static string Describe(int error) => error switch
    {
        SharingViolation => "Win32 エラー 32: 他のプログラムが使用中 (共有違反)",
        AccessDenied => "Win32 エラー 5: アクセス拒否",
        _ => $"Win32 エラー {error}",
    };
}

// delete のエントリ処理 (SPEC §3.4、§8.3、§8.4)。処理対象を ZIP の順に1件ずつ、その時点の target の状態で検証し、
// 条件を満たしたファイルをその場で削除する。各エントリで削除用ハンドルを1回だけ開き、同じハンドルで照合 → 検査と M0 →
// (Strict) 1回の全バイト比較 → 最終確認 (M0 の全項目) → 削除の指示 → 成立確認 を行い、閉じてから次へ進む。
// 比較用ハンドルは開かない。全件の削除候補とその状態を保持しない (列挙由来の基準と M0 はエントリの処理の間だけ持つ)。
// STOP したら、その対象を削除せず、以後のエントリは処理しない。既に削除したファイルは戻さない (rollback しない)。
public static class SequentialDeleter
{
    // FILE_DISPOSITION_FLAG_DELETE (0x1) | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS (0x2)。
    // FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE (0x10) は含めない (DEC-10、read-only を最後の防壁として残す)。
    public const uint DispositionFlags = 0x3;

    public static DeleteReport Run(DeleteRequest request) => new DeleteRun(request).Execute();
}

internal sealed class DeleteRun
{
    private readonly DeleteRequest _request;
    private readonly DeleteHooks _hooks;
    private readonly ContentComparer _comparer;
    private readonly TargetResolver _target;

    public DeleteRun(DeleteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _hooks = request.Hooks ?? new DeleteHooks();
        _comparer = new ContentComparer(request.Limits);

        // 探す名前は処理対象のファイルエントリからだけ集める。指定外のエントリは解決も列挙もしない (SPEC §3.3)。
        _target = new TargetResolver(request.Probe, request.Root, request.Entries);
    }

    internal ContentComparer Comparer => _comparer;

    internal RealNameResolver Resolver => _target.Names;

    public DeleteReport Execute()
    {
        var entries = _request.Entries;
        var results = new List<DeleteEntryResult>();
        var directories = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            _request.Progress?.Invoke(i + 1, entries.Count);

            var entry = entries[i];
            if (entry.IsDirectory)
            {
                // ディレクトリは削除しない (SPEC §1)。結果行を出さず、件数だけを要約に出す。
                directories++;
                continue;
            }

            var result = ProcessFile(entry);
            results.Add(result);
            _request.OnResult?.Invoke(result);
            if (result.Status == DeleteStatus.Stopped)
            {
                return new DeleteReport(results, directories, entries.Count - i - 1);
            }
        }

        return new DeleteReport(results, directories, 0);
    }

    // 1エントリの処理。削除用ハンドルは using によって、STOP・例外を含むどの経路でもこの処理の中で閉じる。
    private DeleteEntryResult ProcessFile(ValidatedZipEntry entry)
    {
        var reference = new ZipEntryRef(entry.Entry.Index, entry.Entry.FullName);
        var expectedPath = _request.Root.ExpectedPath(entry.Components);
        var dispositionRequested = false;

        DeleteEntryResult Result(DeleteStatus status, SkipReason? skip = null) => new(reference, expectedPath, status, skip);

        DeleteEntryResult Stop(string reason, bool possiblyDeleted = false) =>
            new(reference, expectedPath, DeleteStatus.Stopped, null, reason, possiblyDeleted);

        try
        {
            // 1. 解決: 親成分と最終成分の実名確認 (列挙結果は後続のエントリに再利用する。DEC-25)。
            var resolution = _target.Resolve(entry);
            switch (resolution.Kind)
            {
                case ResolutionKind.Failed:
                    return Stop(Describe(resolution.FatalKind!.Value, resolution.Detail));
                case ResolutionKind.Missing:
                    return Result(DeleteStatus.Missing);
                case ResolutionKind.ParentReparse:
                    return Result(DeleteStatus.SkippedSpecialFile, SkipReason.ParentReparsePoint);
            }

            // 2. 事前判定 (D1、SPEC §7、DEC-28): 列挙項目の属性がディレクトリ・reparse・許可外なら、削除用ハンドルを開かない。
            // この判定は削除しない側にだけ働く。通過した対象もハンドル上で全ての判定を改めて行う。
            var item = resolution.Item;
            if (PreCheck(item) is { } preSkip)
            {
                return Result(DeleteStatus.SkippedSpecialFile, preSkip);
            }

            // 列挙由来の基準 (SPEC §8.2): 列挙項目の File ID、target ルートのボリュームシリアル、たどった親の File ID、期待パス。
            var baseline = new IdentityBaseline(
                new VolumeFileId(_request.Root.Id.VolumeSerialNumber, item.FileId), resolution.ParentFileId, expectedPath);

            // 3. オープン: パスを使うのはここだけ (失敗時の識別確認を除く)。
            _hooks.BeforeOpen?.Invoke(reference, expectedPath);
            var opened = _request.DeletionProbe.OpenForDeletion(expectedPath);
            if (!opened.Succeeded)
            {
                return OpenFailed(reference, expectedPath, baseline, opened.Error);
            }

            using var handle = opened.Value;
            _hooks.AfterOpen?.Invoke(reference, handle);

            // 4. 照合: ボリュームシリアルと File ID、最終パスを列挙由来の基準と照合する。
            var identity = HandleInspector.VerifyIdentity(handle, baseline);
            if (identity.Failure is { } failure)
            {
                return Stop(Describe(failure.Kind, failure.Detail));
            }

            var standard = handle.GetStandardInformation();
            if (!standard.Succeeded)
            {
                return Stop(Describe(FatalKind.TargetInfoFailed, standard.Describe()));
            }

            // USN の親 ID は hardlink では開いた名前の親とは限らない。リンク数2以上を確認した
            // 非ディレクトリ・非削除保留の対象だけ、特殊判定で必ず非削除にしてここで戻る。
            // その他は従来どおり親 ID の取得・照合が必須。取得した standard は特殊判定でも同じ値を使う。
            if (!standard.Value.IsDirectory && !standard.Value.DeletePending && standard.Value.NumberOfLinks >= 2)
            {
                var special = HandleInspector.Inspect(handle, identity.Id, entry.Entry.Length, _request.ArchiveIdentity,
                    stopOnDeletePending: true, standardInformation: standard.Value);
                if (special.Failure is { } specialFailure)
                {
                    return Stop(Describe(specialFailure.Kind, specialFailure.Detail));
                }

                // reparse の優先順位を維持する。リンク数2以上を既に確認したので、削除候補にはしない。
                return Result(DeleteStatus.SkippedSpecialFile, special.SkipReason ?? SkipReason.HardLink);
            }

            var parent = handle.GetParentFileId();
            if (!parent.Succeeded)
            {
                return Stop(Describe(FatalKind.TargetInfoFailed, parent.Describe()));
            }

            if (parent.Value != resolution.ParentFileId)
            {
                return Stop(Describe(FatalKind.ParentFileIdMismatch, null));
            }

            // 5. 検査と M0: §7 の特殊判定 (DeletePending を含む) とサイズ。M0 は内容比較の前に同じハンドルから取得する。
            var inspection = HandleInspector.Inspect(handle, identity.Id, entry.Entry.Length, _request.ArchiveIdentity,
                stopOnDeletePending: true, standardInformation: standard.Value);
            switch (inspection.Kind)
            {
                case InspectionKind.Failed:
                    return Stop(Describe(inspection.Failure!.Value.Kind, inspection.Failure.Value.Detail));
                case InspectionKind.SkippedSpecialFile:
                    return Result(DeleteStatus.SkippedSpecialFile, inspection.SkipReason);
                case InspectionKind.Modified:
                    return Result(DeleteStatus.Modified);
            }

            var m0 = HandleInspector.State(identity.Id, parent.Value, identity.FinalPath, inspection);

            // 6. 全バイト比較 (Strict のみ): 同じハンドルから読み、このエントリについて1回だけ比較する。Fast は読まない (DEC-34)。
            IComparisonHandle reader = _hooks.DuringCompare is { } during ? new FirstReadHook(handle, () => during(reference, handle)) : handle;
            var compared = _comparer.Verify(_request.Mode, _request.Contents, entry.Entry.Index, reader);
            switch (compared.Verdict)
            {
                case ContentVerdict.Fatal:
                    // §5.2 の 1〜5 の違反 (ZIP 側の異常)、target の読み取り失敗、実測展開量の合計の超過。
                    return Stop($"全バイト比較で異常: {Describe(compared.FatalKind!.Value, compared.Detail)}");
                case ContentVerdict.Mismatch:
                    // §5.2 の 6 だけが不成立 (内容が異なる): 削除せず続行する。
                    return Result(DeleteStatus.Modified);
            }

            // 7. 最終確認: M0 の全項目を再取得して照合し、Directory と DeletePending が false であることを確かめる。
            _hooks.BeforeFinalCheck?.Invoke(reference, handle);
            if (HandleInspector.FinalCheck(handle, m0) is { } changed)
            {
                return Stop($"最終確認で不一致: {changed}");
            }

            // 8. 削除: 同じハンドルへの削除の指示。失敗は種類を問わず STOP (DEC-12)。
            _hooks.BeforeDisposition?.Invoke(reference, handle);
            dispositionRequested = true;
            var disposition = handle.SetDispositionEx(SequentialDeleter.DispositionFlags);
            if (!disposition.Succeeded)
            {
                // 指示の失敗では DeletePending は false のままのはずだが (PoC 1〜3)、確かめられなければ「削除された可能性あり」とする。
                var after = handle.GetStandardInformation();
                var possibly = !after.Succeeded || after.Value.DeletePending;
                return Stop($"削除の指示が失敗: {disposition.Describe()}", possibly);
            }

            // 9. 成立確認: 同じハンドルの DeletePending が true。API の成功だけでは成立としない (DEC-11)。
            var confirmed = handle.GetStandardInformation();
            if (!confirmed.Succeeded)
            {
                return Stop($"削除の成立を確認できません: {confirmed.Describe()}", possiblyDeleted: true);
            }

            if (!confirmed.Value.DeletePending)
            {
                return Stop("削除の成立を確認できません: 削除の指示は成功を返したが DeletePending が false", possiblyDeleted: true);
            }

            return Result(DeleteStatus.Deleted);
        }
        catch (Exception ex)
        {
            // 想定外の例外 (フックからの例外を含む) は STOP。削除用ハンドルは using で閉じられている。
            return Stop($"想定外の例外 ({ex.GetType().Name}: {ex.Message})", possiblyDeleted: dispositionRequested);
        }
    }

    // 削除用オープンの失敗 (SPEC §8.4、PLAN.md §4)。32・5 は識別確認で「列挙由来の基準と一致する通常ファイルに見える」ときだけ
    // DELETE_FAILED。識別確認は拒否された削除用オープンとは別のオープンであり、同じ個体を見たことも拒否の理由も保証しない。
    // 一致しても削除はせず、残して次へ進むだけなので、この限界は誤削除につながらない。不一致・失敗・判定不能は STOP。
    private DeleteEntryResult OpenFailed(ZipEntryRef entry, string expectedPath, IdentityBaseline baseline, int error)
    {
        var description = DeletionOpenErrors.Describe(error);
        if (!DeletionOpenErrors.RequiresIdentityCheck(error))
        {
            return new(entry, expectedPath, DeleteStatus.Stopped, null, $"削除用に開けません ({description})");
        }

        var identity = _request.DeletionProbe.CheckIdentity(expectedPath);
        if (!identity.Succeeded)
        {
            return new(entry, expectedPath, DeleteStatus.Stopped, null, $"削除用に開けません ({description})。識別確認も失敗 ({identity.Describe()})");
        }

        if (IdentityMismatch(identity.Value, baseline) is { } mismatch)
        {
            return new(entry, expectedPath, DeleteStatus.Stopped, null, $"削除用に開けません ({description})。識別確認で列挙時の項目と不一致: {mismatch}");
        }

        return new(
            entry,
            expectedPath,
            DeleteStatus.DeleteFailed,
            null,
            $"削除用に開けません ({description})。識別確認の時点では同じファイルに見えるため、削除せずに残しました。内容は確認していません");
    }

    // 識別確認の比較項目 (SPEC §8.4): File ID とボリュームシリアル、親 File ID、最終パスを列挙由来の基準と比較し、
    // ディレクトリでない、reparse でない、DeletePending が false であることを確かめる。
    internal static string? IdentityMismatch(IdentityCheckInfo info, IdentityBaseline baseline)
    {
        if (info.Id != baseline.Id)
        {
            return "File ID";
        }

        if (info.ParentFileId != baseline.ParentFileId)
        {
            return "親 File ID";
        }

        if (!string.Equals(info.FinalPath, baseline.ExpectedPath, StringComparison.Ordinal))
        {
            return "最終パス";
        }

        if (info.IsDirectory)
        {
            return "ディレクトリ";
        }

        if (TargetResolver.IsReparse(info.Attributes, info.ReparseTag))
        {
            return "reparse point";
        }

        if (info.DeletePending)
        {
            return "削除保留中";
        }

        return null;
    }

    // 事前判定 (D1) の理由。ディレクトリ (0x10) → reparse (0x400 または reparse tag ≠ 0) → 許可集合外の属性 の順。
    internal static SkipReason? PreCheck(DirectoryItem item)
    {
        const uint FileAttributeDirectory = 0x10;
        if ((item.Attributes & FileAttributeDirectory) != 0)
        {
            return SkipReason.Directory;
        }

        if (TargetResolver.IsReparse(item.Attributes, item.ReparseTag))
        {
            return SkipReason.ReparsePoint;
        }

        return FileAttributeRules.IsSpecial(item.Attributes) ? SkipReason.Attributes : null;
    }

    private static string Describe(FatalKind kind, string? detail) =>
        detail is null ? FatalKindText.Describe(kind) : $"{FatalKindText.Describe(kind)} ({detail})";

    // 全バイト比較で target を最初に読む直前にフックを呼ぶ (テスト用の H3)。それ以外は削除用ハンドルにそのまま委ねる。
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

        // 削除用ハンドルは ProcessFile の using が閉じる。
        public void Dispose()
        {
        }
    }
}
