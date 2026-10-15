using Unextract.Core.Analysis;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Deletion;

// ディレクトリ段階の1件の結果 (docs/spec/cli.md#delete-output、docs/spec/machine-output.md#records の dir)。
public enum DirectoryStatus
{
    Deleted,
    Kept,
    Missing,
    Stopped,
}

// DIR_KEPT の理由 (docs/spec/filesystem.md#prune-flow、docs/spec/filesystem.md#prune-failures)。
public enum DirectoryKeepReason
{
    NotEmpty,
    ParentReparsePoint,
    ReparsePoint,
    Attributes,
    AlternateDataStream,
    InUse,
}

public static class DirectoryStatusExtensions
{
    public static string ToDisplayString(this DirectoryStatus status) => status switch
    {
        DirectoryStatus.Deleted => "DIR_DELETED",
        DirectoryStatus.Kept => "DIR_KEPT",
        DirectoryStatus.Missing => "DIR_MISSING",
        DirectoryStatus.Stopped => "STOPPED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

// ディレクトリ段階の対象1件。Components は target ルートからの ZIP の成分 (1つ以上)。
public sealed record PruneDirectory(IReadOnlyList<string> Components)
{
    // target ルートからの相対パス (成分を "\" で連結。末尾に区切りを付けない。docs/spec/machine-output.md#records の dir の path)。
    public string Path { get; } = string.Join('\\', Components);
}

// 処理したディレクトリ1件の結果。Target は期待パス (\\?\ 形式、表示用)。Reason と PossiblyDeleted は STOPPED のとき。
public sealed record DirectoryResult(
    PruneDirectory Directory,
    string Target,
    DirectoryStatus Status,
    DirectoryKeepReason? KeepReason = null,
    string? Reason = null,
    bool PossiblyDeleted = false,
    DeleteFailure? Failure = null);

// ディレクトリ段階の結果。Results は処理した順 (STOP なら最後が STOPPED)。Targets は対象の件数。
// ファイル段階が STOP した場合は段階を開始せず、Results は空 (全件が未処理)。
public sealed record PruneReport(IReadOnlyList<DirectoryResult> Results, int Targets)
{
    public DirectoryResult? Stop => Results.Count > 0 && Results[^1].Status == DirectoryStatus.Stopped ? Results[^1] : null;

    // 未処理: 段階を開始しなかった場合の全件と、STOP より後の件数 (STOP の対象を除く)。
    public int NotProcessedCount => Targets - Results.Count;

    public int Count(DirectoryStatus status) => Results.Count(r => r.Status == status);
}

// ディレクトリ段階の対象と順序 (docs/spec/filesystem.md#prune-set)。target に触れずに、Prepare を通過したエントリだけから決める。
public static class PruneSet
{
    // targets は処理対象 (ZIP の順)。--entries なしでは全エントリで、明示ディレクトリエントリのパスと全エントリの祖先成分。
    // --entries ありでは選んだファイルエントリの祖先成分だけ。target ルート自身は含めない。同じパスは1回だけ。
    // 順序は深い順、同じ深さではそのパスを最初に生じさせたエントリのアーカイブの順。
    public static IReadOnlyList<PruneDirectory> Build(IReadOnlyList<ValidatedZipEntry> targets, bool entriesSelected)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<(PruneDirectory Directory, int Order)>();
        for (var order = 0; order < targets.Count; order++)
        {
            var entry = targets[order];
            if (entriesSelected && entry.IsDirectory)
            {
                continue;
            }

            var depth = entry.IsDirectory ? entry.Components.Count : entry.Components.Count - 1;
            for (var count = 1; count <= depth; count++)
            {
                var directory = new PruneDirectory(entry.Components.Take(count).ToArray());
                if (seen.Add(directory.Path))
                {
                    found.Add((directory, order));
                }
            }
        }

        return found
            .OrderByDescending(d => d.Directory.Components.Count)
            .ThenBy(d => d.Order)
            .Select(d => d.Directory)
            .ToList();
    }
}

// ディレクトリ段階 (docs/spec/filesystem.md#prune-flow、docs/spec/filesystem.md#prune-failures)。対象を1件ずつ、その時点の target の状態で検証し、
// 空のディレクトリだけをその場で削除する。各ディレクトリでディレクトリ削除用ハンドルを1回だけ開き、同じハンドルで照合 → 検査と D0 →
// 空の確認 → 最終確認 → 削除の指示 → 成立確認 を行い、閉じてから次へ進む。再帰削除はしない。パスベースの削除 API は使わない。
// 実名確認はファイル段階の列挙結果を使わず、この段階で新しく行う (同じディレクトリは段階の中で1回だけ列挙する)。
internal sealed class PruneRun
{
    // ディレクトリの許可集合 (docs/spec/filesystem.md#prune-flow の手順2): DIRECTORY とファイルの許可集合。
    internal const uint AllowedAttributes = TargetResolver.FileAttributeDirectory | FileAttributeRules.AllowedMask;

    private const int ErrorHandleEof = 38;
    private const int ErrorDirNotEmpty = 145;

    private readonly DeleteRequest _request;
    private readonly DeleteHooks _hooks;
    private readonly TargetResolver _target;

    public PruneRun(DeleteRequest request, DeleteHooks hooks, IReadOnlyList<PruneDirectory> directories)
    {
        _request = request;
        _hooks = hooks;
        _target = new TargetResolver(request.Root, new RealNameResolver(request.Probe, request.Root,
            RealNameResolver.CollectWantedNames(directories.Select(d => d.Components))));
    }

    internal RealNameResolver Resolver => _target.Names;

    public DirectoryResult Process(PruneDirectory directory)
    {
        var expectedPath = _request.Root.ExpectedPath(directory.Components);
        var dispositionRequested = false;

        DirectoryResult Result(DirectoryStatus status, DirectoryKeepReason? keep = null) => new(directory, expectedPath, status, keep);

        DirectoryResult Stop(string reason, DeleteFailure failure, bool possiblyDeleted = false) =>
            new(directory, expectedPath, DirectoryStatus.Stopped, null, reason, possiblyDeleted, failure);

        DirectoryResult StopFor(FatalKind kind, string? detail, EntryStep step, int? win32Error = null) =>
            Stop(Describe(kind, detail), new DeleteFailure(kind, step, win32Error));

        try
        {
            // 1. 解決: 親成分と最終成分の実名確認。
            var resolution = _target.Resolve(directory.Components);
            switch (resolution.Kind)
            {
                case ResolutionKind.Failed:
                    return StopFor(resolution.FatalKind!.Value, resolution.Detail, EntryStep.Resolve, resolution.Win32Error);
                case ResolutionKind.Missing:
                    return Result(DirectoryStatus.Missing);
                case ResolutionKind.ParentReparse:
                    return Result(DirectoryStatus.Kept, DirectoryKeepReason.ParentReparsePoint);
            }

            // 最終成分も親成分と同じ表で分類する: reparse → 想定外の種類 (STOP) → 通常ファイル (DIR_MISSING)。
            var item = resolution.Item;
            if (TargetResolver.IsReparse(item.Attributes, item.ReparseTag))
            {
                return Result(DirectoryStatus.Kept, DirectoryKeepReason.ReparsePoint);
            }

            if ((item.Attributes & TargetResolver.FileAttributeDevice) != 0)
            {
                return StopFor(FatalKind.UnexpectedTargetType, $"属性 0x{item.Attributes:X}", EntryStep.Resolve);
            }

            if ((item.Attributes & TargetResolver.FileAttributeDirectory) == 0)
            {
                return Result(DirectoryStatus.Missing);
            }

            // 2. 事前判定: 許可集合外の属性 (READONLY を含む) なら開かない。
            if (!IsAllowed(item.Attributes))
            {
                return Result(DirectoryStatus.Kept, DirectoryKeepReason.Attributes);
            }

            var baseline = new IdentityBaseline(
                new VolumeFileId(_request.Root.Id.VolumeSerialNumber, item.FileId), resolution.ParentFileId, expectedPath);

            // 3. オープン: パスを使うのはここだけ (失敗時の識別確認を除く)。
            _hooks.BeforeDirectoryOpen?.Invoke(directory.Path, expectedPath);
            var opened = _request.DeletionProbe.OpenDirectoryForDeletion(expectedPath);
            if (!opened.Succeeded)
            {
                return OpenFailed(directory, expectedPath, baseline, opened.Error);
            }

            using var handle = opened.Value;

            // 4. 照合: ボリュームシリアルと File ID、最終パス、親 File ID、Directory。
            var identity = HandleInspector.VerifyIdentity(handle, baseline);
            if (identity.Failure is { } failure)
            {
                return Stop(Describe(failure.Kind, failure.Detail), new DeleteFailure(failure.Kind, failure.Step, failure.Win32Error));
            }

            var parent = handle.GetParentFileId();
            if (!parent.Succeeded)
            {
                return StopFor(FatalKind.TargetInfoFailed, parent.Describe(), EntryStep.Verify, parent.Error);
            }

            if (parent.Value != resolution.ParentFileId)
            {
                return StopFor(FatalKind.ParentFileIdMismatch, null, EntryStep.Verify);
            }

            var standard = handle.GetStandardInformation();
            if (!standard.Succeeded)
            {
                return StopFor(FatalKind.TargetInfoFailed, standard.Describe(), EntryStep.Verify, standard.Error);
            }

            if (!standard.Value.IsDirectory)
            {
                return StopFor(FatalKind.UnexpectedTargetType, "ディレクトリではありません", EntryStep.Verify);
            }

            // 5. 検査と D0: 削除保留 (STOP)、reparse・属性・ADS (DIR_KEPT)。
            if (standard.Value.DeletePending)
            {
                return StopFor(FatalKind.TargetDeletePending, null, EntryStep.Inspect);
            }

            var tag = handle.GetAttributeTagInformation();
            if (!tag.Succeeded)
            {
                return StopFor(FatalKind.TargetInfoFailed, tag.Describe(), EntryStep.Inspect, tag.Error);
            }

            if (TargetResolver.IsReparse(tag.Value.Attributes, tag.Value.ReparseTag))
            {
                return Result(DirectoryStatus.Kept, DirectoryKeepReason.ReparsePoint);
            }

            if (!IsAllowed(tag.Value.Attributes))
            {
                return Result(DirectoryStatus.Kept, DirectoryKeepReason.Attributes);
            }

            var streams = HasStreams(handle);
            if (!streams.Succeeded)
            {
                return StopFor(FatalKind.TargetInfoFailed, streams.Describe(), EntryStep.Inspect, streams.Error);
            }

            if (streams.Value)
            {
                return Result(DirectoryStatus.Kept, DirectoryKeepReason.AlternateDataStream);
            }

            var d0 = new DirectoryState(identity.Id, parent.Value, identity.FinalPath, tag.Value.Attributes, tag.Value.ReparseTag);

            // 6. 空の確認: 同じハンドルで列挙し、1件でもあれば残す。見つけた名前は保持しない。
            var first = handle.Enumerate().Next();
            switch (first.Kind)
            {
                case DirectoryEnumerationStepKind.Item:
                    return Result(DirectoryStatus.Kept, DirectoryKeepReason.NotEmpty);
                case DirectoryEnumerationStepKind.Failed:
                    return StopFor(FatalKind.EnumerationFailed, $"{first.Operation} が失敗 (Win32 エラー {first.Error})", EntryStep.Inspect, first.Error);
            }

            // 7. 最終確認: D0 の全項目と、Directory が true・DeletePending が false。
            _hooks.BeforeDirectoryFinalCheck?.Invoke(directory.Path, handle);
            if (FinalCheck(handle, d0) is { } changed)
            {
                var diagnostic = changed.Kind == FinalCheckFailureKind.InformationFailed
                    ? new DeleteFailure(FatalKind.TargetInfoFailed, EntryStep.FinalCheck, changed.Win32Error)
                    : new DeleteFailure(DeleteFailureKind.FinalCheckMismatch, EntryStep.FinalCheck);
                return Stop($"最終確認で不一致: {changed.Detail}", diagnostic);
            }

            // 8. 削除: ファイルと同じ flags (0x3) を同じハンドルへ。空の確認の後に項目が作られていれば NTFS が 145 で拒否する。
            _hooks.BeforeDirectoryDisposition?.Invoke(directory.Path, handle);
            dispositionRequested = true;
            var disposition = handle.SetDispositionEx(SequentialDeleter.DispositionFlags);
            if (!disposition.Succeeded)
            {
                var after = handle.GetStandardInformation();
                var notPending = after.Succeeded && !after.Value.DeletePending;
                if (disposition.Error == ErrorDirNotEmpty && notPending)
                {
                    return Result(DirectoryStatus.Kept, DirectoryKeepReason.NotEmpty);
                }

                return Stop($"削除の指示が失敗: {disposition.Describe()}",
                    new DeleteFailure(DeleteFailureKind.DispositionFailed, EntryStep.Dispose, disposition.Error), !notPending);
            }

            // 9. 成立確認: 同じハンドルの DeletePending が true。
            var confirmed = handle.GetStandardInformation();
            if (!confirmed.Succeeded)
            {
                return Stop($"削除の成立を確認できません: {confirmed.Describe()}",
                    new DeleteFailure(DeleteFailureKind.DeletionUnconfirmed, EntryStep.Confirm, confirmed.Error), possiblyDeleted: true);
            }

            if (!confirmed.Value.DeletePending)
            {
                return Stop("削除の成立を確認できません: 削除の指示は成功を返したが DeletePending が false",
                    new DeleteFailure(DeleteFailureKind.DeletionUnconfirmed, EntryStep.Confirm), possiblyDeleted: true);
            }

            return Result(DirectoryStatus.Deleted);
        }
        catch (Exception ex)
        {
            // 想定外の例外 (フックからの例外を含む) は STOP。ディレクトリ削除用ハンドルは using で閉じられている。
            return Stop($"想定外の例外 ({ex.GetType().Name}: {ex.Message})",
                new DeleteFailure(DeleteFailureKind.UnexpectedException, null), possiblyDeleted: dispositionRequested);
        }
    }

    internal static bool IsAllowed(uint attributes) => (attributes & ~AllowedAttributes) == 0;

    // オープンの失敗 (docs/spec/filesystem.md#prune-failures)。32・5 は識別確認で「列挙由来の基準と一致する、削除保留でない通常のディレクトリに見える」
    // ときだけ DIR_KEPT (使用中)。一致しても削除はしない。不一致・失敗・判定不能とその他のエラーは STOP。
    private DirectoryResult OpenFailed(PruneDirectory directory, string expectedPath, IdentityBaseline baseline, int error)
    {
        var description = DeletionOpenErrors.Describe(error);
        if (!DeletionOpenErrors.RequiresIdentityCheck(error))
        {
            return new(directory, expectedPath, DirectoryStatus.Stopped, null, $"削除用に開けません ({description})",
                Failure: new DeleteFailure(DeleteFailureKind.OpenFailed, EntryStep.Open, error));
        }

        var identity = _request.DeletionProbe.CheckIdentity(expectedPath);
        if (!identity.Succeeded)
        {
            return new(directory, expectedPath, DirectoryStatus.Stopped, null, $"削除用に開けません ({description})。識別確認も失敗 ({identity.Describe()})",
                Failure: new DeleteFailure(DeleteFailureKind.IdentityCheckFailed, EntryStep.Open, identity.Error));
        }

        if (DeleteRun.IdentityMismatch(identity.Value, baseline, expectDirectory: true) is { } mismatch)
        {
            return new(directory, expectedPath, DirectoryStatus.Stopped, null, $"削除用に開けません ({description})。識別確認で列挙時の項目と不一致: {mismatch}",
                Failure: new DeleteFailure(DeleteFailureKind.IdentityCheckMismatch, EntryStep.Open));
        }

        return new(directory, expectedPath, DirectoryStatus.Kept, DirectoryKeepReason.InUse);
    }

    // FileStreamInfo: ストリームを持たないディレクトリでは ERROR_HANDLE_EOF (38) で失敗するので、38 はストリームなしとする。
    // ディレクトリにはデータストリームが無いため、返った項目は全て名前付きストリームとして扱う。
    private static ProbeResult<bool> HasStreams(IHandleInformation handle)
    {
        var streams = handle.GetStreams();
        if (streams.Succeeded)
        {
            return ProbeResult<bool>.Ok(streams.Value.Count > 0);
        }

        return streams.Error == ErrorHandleEof ? ProbeResult<bool>.Ok(false) : ProbeResult<bool>.Fail(streams.Error, streams.Operation!);
    }

    // 最終確認 (docs/spec/filesystem.md#prune-flow の手順7)。D0 の全項目を同じハンドルで再取得して比較する。タイムスタンプは比較しない。
    private static FinalCheckFailure? FinalCheck(IDirectoryDeletionHandle handle, DirectoryState d0)
    {
        var id = handle.GetVolumeFileId();
        if (!id.Succeeded)
        {
            return InfoFailed(id.Describe(), id.Error);
        }

        if (id.Value != d0.Id)
        {
            return Mismatch("File ID");
        }

        var parent = handle.GetParentFileId();
        if (!parent.Succeeded)
        {
            return InfoFailed(parent.Describe(), parent.Error);
        }

        if (parent.Value != d0.ParentFileId)
        {
            return Mismatch("親 File ID");
        }

        var finalPath = handle.GetFinalPath();
        if (!finalPath.Succeeded)
        {
            return InfoFailed(finalPath.Describe(), finalPath.Error);
        }

        if (!string.Equals(finalPath.Value, d0.FinalPath, StringComparison.Ordinal))
        {
            return Mismatch("最終パス");
        }

        var standard = handle.GetStandardInformation();
        if (!standard.Succeeded)
        {
            return InfoFailed(standard.Describe(), standard.Error);
        }

        if (!standard.Value.IsDirectory)
        {
            return Mismatch("ディレクトリでない");
        }

        if (standard.Value.DeletePending)
        {
            return Mismatch("削除保留中");
        }

        var tag = handle.GetAttributeTagInformation();
        if (!tag.Succeeded)
        {
            return InfoFailed(tag.Describe(), tag.Error);
        }

        if (tag.Value.Attributes != d0.Attributes)
        {
            return Mismatch("属性");
        }

        if (tag.Value.ReparseTag != d0.ReparseTag || TargetResolver.IsReparse(tag.Value.Attributes, tag.Value.ReparseTag))
        {
            return Mismatch("reparse 状態");
        }

        var streams = HasStreams(handle);
        if (!streams.Succeeded)
        {
            return InfoFailed(streams.Describe(), streams.Error);
        }

        if (streams.Value)
        {
            return Mismatch("ストリームの有無");
        }

        return null;

        static FinalCheckFailure InfoFailed(string detail, int error) => new(FinalCheckFailureKind.InformationFailed, detail, error);

        static FinalCheckFailure Mismatch(string detail) => new(FinalCheckFailureKind.Mismatch, detail);
    }

    private static string Describe(FatalKind kind, string? detail) =>
        detail is null ? FatalKindText.Describe(kind) : $"{FatalKindText.Describe(kind)} ({detail})";

    // D0 (docs/spec/filesystem.md#prune-flow の手順5)。ストリームなし・Directory が true・DeletePending が false は前提 (記録しない)。
    private sealed record DirectoryState(VolumeFileId Id, FileId ParentFileId, string FinalPath, uint Attributes, uint ReparseTag);
}
