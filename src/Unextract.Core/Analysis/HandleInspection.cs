using Unextract.Core.Results;
using Unextract.Core.Target;

namespace Unextract.Core.Analysis;

// 開いたハンドルの照合の基準 (SPEC §6.1 の手順4・5)。Id は列挙項目の File ID と target ルートのボリュームシリアル、
// ExpectedPath は target の最終パス + "\" + ZIP の成分 (\\?\ 形式)。ParentFileId は delete だけが照合する
// 「手順1でたどった親ディレクトリの File ID」(analyze は null)。delete ではこれが列挙由来の基準 (SPEC §8.2) になる。
internal sealed record IdentityBaseline(VolumeFileId Id, FileId? ParentFileId, string ExpectedPath);

// 照合・検査の失敗 (analyze は FATAL、delete は STOP)。
internal readonly record struct HandleFailure(FatalKind Kind, string? Detail);

// delete の M0 (SPEC §8.2)。削除用ハンドルを開いた直後、内容比較の前に同じハンドルから取得した値。
// エントリの処理の間だけ持ち、最終確認 (SPEC §8.3 の手順7) の比較基準に使う。エントリをまたいで保持しない。
// Directory と DeletePending が false であることは M0 の前提 (記録しない)。
internal sealed record HandleState(
    VolumeFileId Id,
    FileId ParentFileId,
    string FinalPath,
    long EndOfFile,
    long LastWriteTime,
    long ChangeTime,
    uint Attributes,
    uint NumberOfLinks,
    IReadOnlyList<StreamEntry> Streams,
    uint ReparseTag);

internal enum InspectionKind
{
    // 安全な通常ファイルでサイズが Length と一致 (内容比較候補、SPEC §5.1)。
    Candidate,
    SkippedSpecialFile,
    Modified,
    Failed,
}

// SPEC §6.1 の手順6・7 (§7 の特殊判定とサイズ) の結果。Candidate と Modified のとき、ハンドルから取得した値を持つ。
internal readonly record struct Inspection(
    InspectionKind Kind,
    SkipReason? SkipReason,
    HandleFailure? Failure,
    StandardInformation Standard,
    BasicInformation Basic,
    AttributeTagInformation Tag,
    IReadOnlyList<StreamEntry>? Streams)
{
    public static Inspection Skip(SkipReason reason) => new(InspectionKind.SkippedSpecialFile, reason, null, default, default, default, null);

    public static Inspection Fail(FatalKind kind, string? detail = null) =>
        new(InspectionKind.Failed, null, new HandleFailure(kind, detail), default, default, default, null);
}

// 比較用ハンドル (analyze) と削除用ハンドル (delete) に共通の、開いたハンドル上の照合と検査 (SPEC §6.1 の手順4〜7、§7、§8.3 の手順4・5・7)。
// IDeletionHandle は IComparisonHandle を継承するため、両方のハンドルに同じ関数を使う。パスは使わない。
internal static class HandleInspector
{
    // File ID とボリュームシリアル、最終パスを基準と照合する。取得の失敗も失敗。
    // delete の親 File ID 照合は、hardlink の非削除判定を可能にした後で行う。
    public static (VolumeFileId Id, string FinalPath, HandleFailure? Failure) VerifyIdentity(
        IComparisonHandle handle, IdentityBaseline baseline)
    {
        var id = handle.GetVolumeFileId();
        if (!id.Succeeded)
        {
            return Failed(FatalKind.TargetInfoFailed, id.Describe());
        }

        if (id.Value != baseline.Id)
        {
            return Failed(FatalKind.ComparisonFileIdMismatch);
        }

        // 最終パスと期待パスの序数比較 (\\?\ 形式のまま)。
        var finalPath = handle.GetFinalPath();
        if (!finalPath.Succeeded)
        {
            return Failed(FatalKind.TargetInfoFailed, finalPath.Describe());
        }

        if (!string.Equals(finalPath.Value, baseline.ExpectedPath, StringComparison.Ordinal))
        {
            return Failed(FatalKind.FinalPathMismatch, $"期待パス {baseline.ExpectedPath}、最終パス {finalPath.Value}");
        }

        return (id.Value, finalPath.Value, null);

        static (VolumeFileId, string, HandleFailure?) Failed(FatalKind kind, string? detail = null) =>
            (default, string.Empty, new HandleFailure(kind, detail));
    }

    // 手順6・7: §7 の特殊判定とサイズ。最初に Directory を判定し、ディレクトリなら他の情報を取得しない。
    // ディレクトリでない対象では、以降の取得 API が1つでも失敗したら (ERROR_HANDLE_EOF を含む) 失敗。
    // stopOnDeletePending は delete のときだけ true (DeletePending なら失敗。analyze は判定項目にしない)。
    public static Inspection Inspect(IComparisonHandle handle, VolumeFileId id, long length, VolumeFileId archiveIdentity, bool stopOnDeletePending,
        StandardInformation? standardInformation = null)
    {
        var standard = standardInformation is { } supplied
            ? ProbeResult<StandardInformation>.Ok(supplied)
            : handle.GetStandardInformation();
        if (!standard.Succeeded)
        {
            return Inspection.Fail(FatalKind.TargetInfoFailed, standard.Describe());
        }

        if (standard.Value.IsDirectory)
        {
            return Inspection.Skip(SkipReason.Directory);
        }

        if (stopOnDeletePending && standard.Value.DeletePending)
        {
            return Inspection.Fail(FatalKind.TargetDeletePending);
        }

        // 判定に使う情報を全て取得してから判定する。
        var basic = handle.GetBasicInformation();
        if (!basic.Succeeded)
        {
            return Inspection.Fail(FatalKind.TargetInfoFailed, basic.Describe());
        }

        var tag = handle.GetAttributeTagInformation();
        if (!tag.Succeeded)
        {
            return Inspection.Fail(FatalKind.TargetInfoFailed, tag.Describe());
        }

        var streams = handle.GetStreams();
        if (!streams.Succeeded)
        {
            return Inspection.Fail(FatalKind.TargetInfoFailed, streams.Describe());
        }

        if (SpecialReason(id, archiveIdentity, standard.Value, basic.Value, tag.Value, streams.Value) is { } reason)
        {
            return Inspection.Skip(reason);
        }

        // サイズが Length と異なれば MODIFIED (ZIP 内容を読まない)。
        var kind = standard.Value.EndOfFile == length ? InspectionKind.Candidate : InspectionKind.Modified;
        return new Inspection(kind, null, null, standard.Value, basic.Value, tag.Value, streams.Value);
    }

    // M0 (SPEC §8.2): 照合 (手順4) と検査 (手順5) で同じハンドルから取得した値。
    public static HandleState State(VolumeFileId id, FileId parentFileId, string finalPath, Inspection inspection) => new(
        id,
        parentFileId,
        finalPath,
        inspection.Standard.EndOfFile,
        inspection.Basic.LastWriteTime,
        inspection.Basic.ChangeTime,
        inspection.Basic.Attributes,
        inspection.Standard.NumberOfLinks,
        inspection.Streams!,
        inspection.Tag.ReparseTag);

    // delete の最終確認 (SPEC §8.3 の手順7): 同じハンドルで M0 の全項目 (File ID とボリュームシリアル、親 File ID、最終パス、
    // EndOfFile、LastWriteTime、ChangeTime、属性、リンク数、ストリーム一覧、reparse 状態) を再取得して完全に一致すること、
    // Directory と DeletePending が false であることを確かめる。親 File ID と最終パスは、片方だけでは検出できない差し替えが
    // あるため、どちらも省略しない (DEC-13、DEC-29)。不一致の項目名 (取得の失敗はその説明) を返す。一致すれば null。
    public static string? FinalCheck(IComparisonHandle handle, HandleState m0)
    {
        var id = handle.GetVolumeFileId();
        if (!id.Succeeded)
        {
            return id.Describe();
        }

        if (id.Value != m0.Id)
        {
            return "File ID";
        }

        var parent = handle.GetParentFileId();
        if (!parent.Succeeded)
        {
            return parent.Describe();
        }

        if (parent.Value != m0.ParentFileId)
        {
            return "親 File ID";
        }

        var finalPath = handle.GetFinalPath();
        if (!finalPath.Succeeded)
        {
            return finalPath.Describe();
        }

        if (!string.Equals(finalPath.Value, m0.FinalPath, StringComparison.Ordinal))
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

        if (standard.Value.EndOfFile != m0.EndOfFile)
        {
            return "EndOfFile";
        }

        if (standard.Value.NumberOfLinks != m0.NumberOfLinks)
        {
            return "リンク数";
        }

        var basic = handle.GetBasicInformation();
        if (!basic.Succeeded)
        {
            return basic.Describe();
        }

        if (basic.Value.LastWriteTime != m0.LastWriteTime)
        {
            return "LastWriteTime";
        }

        if (basic.Value.ChangeTime != m0.ChangeTime)
        {
            return "ChangeTime";
        }

        if (basic.Value.Attributes != m0.Attributes)
        {
            return "属性";
        }

        var tag = handle.GetAttributeTagInformation();
        if (!tag.Succeeded)
        {
            return tag.Describe();
        }

        if (tag.Value.ReparseTag != m0.ReparseTag || TargetResolver.IsReparse(tag.Value.Attributes, tag.Value.ReparseTag))
        {
            return "reparse 状態";
        }

        var streams = handle.GetStreams();
        if (!streams.Succeeded)
        {
            return streams.Describe();
        }

        if (!streams.Value.SequenceEqual(m0.Streams))
        {
            return "ストリーム一覧";
        }

        return null;
    }

    // SPEC §7 の特殊判定 (Directory は判定済み)。どれか1つでも該当すれば SKIPPED_SPECIAL_FILE。
    private static SkipReason? SpecialReason(
        VolumeFileId id,
        VolumeFileId archiveIdentity,
        StandardInformation standard,
        BasicInformation basic,
        AttributeTagInformation tag,
        IReadOnlyList<StreamEntry> streams)
    {
        if (TargetResolver.IsReparse(basic.Attributes | tag.Attributes, tag.ReparseTag))
        {
            return SkipReason.ReparsePoint;
        }

        if (standard.NumberOfLinks >= 2)
        {
            return SkipReason.HardLink;
        }

        if (streams.Any(s => s.Name != StreamEntry.DefaultDataStream))
        {
            return SkipReason.AlternateDataStream;
        }

        if (id == archiveIdentity)
        {
            return SkipReason.ArchiveItself;
        }

        if (FileAttributeRules.IsSpecial(basic.Attributes))
        {
            return SkipReason.Attributes;
        }

        return null;
    }
}
