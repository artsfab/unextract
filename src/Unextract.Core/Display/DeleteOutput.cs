using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Zip;

namespace Unextract.Core.Display;

// delete の表示 (docs/spec/cli.md#delete-output、docs/spec/cli.md#delete-output)。純粋関数。結果行・要約は標準出力、STOP の原因とエラーで終わる理由は標準エラー出力。
public static class DeleteOutput
{
    public const string Cancelled = "中止しました。削除0件。";

    public const string NotInteractive = "標準入力が対話的でなく --yes も無いため、確認できません。";

    public const string PrepareAborted = "削除開始前に中止しました。削除0件。";

    // ヘッダーの後の処理対象の件数 (--entries の指定の有無を含む)。
    public static string Targets(int totalEntries, int targetEntries, bool entriesSelected) =>
        entriesSelected ? $"対象: {totalEntries} エントリ中 {targetEntries} エントリ (--entries)" : $"対象: 全 {totalEntries} エントリ";

    // --prune-dirs のヘッダー (対象の行の後)。directories はディレクトリ段階の対象の件数。
    public static string PruneTargets(int directories) => $"フォルダー: 空のフォルダーも削除します (--prune-dirs、対象 {directories} 件)";

    // 確認プロンプト (docs/spec/cli.md#confirmation、docs/spec/cli.md#delete-output)。fileEntries は処理対象のファイルエントリの件数 (削除される最大件数)。
    // Fast では [y/N] の直前に警告を置く (docs/spec/cli.md#streams の (3))。directories は --prune-dirs のときだけディレクトリ段階の対象の件数。
    public static string ConfirmationPrompt(int fileEntries, RunMode mode, ArchiveFormat format = ArchiveFormat.Zip, int? directories = null)
    {
        var warning = mode == RunMode.Fast ? ReportText.FastWarningFor(format) + Environment.NewLine : string.Empty;
        var prune = directories is { } count
            ? $"ファイルの処理を最後まで終えた後、アーカイブ由来のパスにある空のフォルダー (最大 {count} 件) も1件ずつ検証して削除します。" + Environment.NewLine
            : string.Empty;
        return warning
            + $"最大 {fileEntries} 件のファイルエントリを1件ずつ検証し、条件を満たしたものをその場で完全に削除します。" + Environment.NewLine
            + prune
            + "途中で停止した場合、それまでに削除したファイルは元に戻りません。" + Environment.NewLine
            + "続行しますか? [y/N] ";
    }

    // ディレクトリ段階の1件の結果行。Entry の列を置かず Target だけを表示し、DIR_KEPT には理由を付ける。
    public static string DirectoryLine(DirectoryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var suffix = result.KeepReason is { } reason ? $" ({Describe(reason)})" : string.Empty;
        return ReportText.TargetLine(result.Status.ToDisplayString(), result.Target, suffix);
    }

    public static string Describe(DirectoryKeepReason reason) => reason switch
    {
        DirectoryKeepReason.NotEmpty => "空でない",
        DirectoryKeepReason.ParentReparsePoint => "親が reparse",
        DirectoryKeepReason.ReparsePoint => "reparse",
        DirectoryKeepReason.Attributes => "属性",
        DirectoryKeepReason.AlternateDataStream => "ADS",
        DirectoryKeepReason.InUse => "使用中",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    // 処理したファイルエントリ1件の結果行。SKIPPED_SPECIAL_FILE は理由、DELETE_FAILED は " : <理由>" を続ける。
    public static string Line(DeleteEntryResult result, ArchiveFormat format = ArchiveFormat.Zip)
    {
        ArgumentNullException.ThrowIfNull(result);

        var suffix = result.Status switch
        {
            DeleteStatus.SkippedSpecialFile => ReportText.SkipSuffix(result.SkipReason, format),
            DeleteStatus.DeleteFailed => $" : {SafeDisplay.EscapeForList(result.Reason ?? string.Empty, out _)}",
            _ => string.Empty,
        };
        return ReportText.Line(result.Status.ToDisplayString(), result.Entry.Name, result.Target, suffix);
    }

    // 終了時の要約 (標準出力)。STOP した場合は、削除済みのファイルが戻らないこと、STOP の対象を削除していないこと (または削除された
    // 可能性があること)、未処理のエントリに触れていないことを明示する。excludedEntries は選択対象外の ZIP エントリ数。
    public static IReadOnlyList<string> Summary(DeleteReport report, int excludedEntries = 0)
    {
        ArgumentNullException.ThrowIfNull(report);

        var deleted = report.Count(DeleteStatus.Deleted);
        var lines = new List<string>
        {
            $"要約: 削除済み {deleted}、MODIFIED {report.Count(DeleteStatus.Modified)}、MISSING {report.Count(DeleteStatus.Missing)}、"
                + $"SKIPPED_SPECIAL_FILE {report.Count(DeleteStatus.SkippedSpecialFile)}、DIRECTORY {report.DirectoryCount}、"
                + $"DELETE_FAILED {report.Count(DeleteStatus.DeleteFailed)}、処理対象外 {excludedEntries}、未処理 {report.NotProcessedCount}",
        };

        if (report.Prune is { } prune)
        {
            lines.Add($"フォルダー: 削除済み {prune.Count(DirectoryStatus.Deleted)}、DIR_KEPT {prune.Count(DirectoryStatus.Kept)}、"
                + $"DIR_MISSING {prune.Count(DirectoryStatus.Missing)}、未処理 {prune.NotProcessedCount}");
        }

        if (report.Stop is { } stop)
        {
            var name = SafeDisplay.EscapeForList(stop.Entry.Name, out _);
            var target = stop.PossiblyDeleted ? $"{name} は削除された可能性があります。" : $"{name} は削除していません。";
            lines.Add($"途中で停止しました。それまでに削除した {deleted} 件は元に戻りません。{target}未処理の {report.NotProcessedCount} 件には触れていません。");
        }
        else if (report.Prune is { Stop: { } directoryStop } stopped)
        {
            var name = SafeDisplay.EscapeForList(ReportText.WithoutDevicePrefix(directoryStop.Target), out _);
            var target = directoryStop.PossiblyDeleted ? $"{name} は削除された可能性があります。" : $"{name} は削除していません。";
            lines.Add($"フォルダーの処理中に停止しました。それまでに削除したファイル {deleted} 件とフォルダー {stopped.Count(DirectoryStatus.Deleted)} 件は元に戻りません。"
                + $"{target}未処理のフォルダー {stopped.NotProcessedCount} 件には触れていません。");
        }

        return lines;
    }

    // エラーで終わる理由 (標準エラー出力): STOP の原因、または STOP なしで DELETE_FAILED があること。どちらも無ければ空。
    public static IReadOnlyList<string> Errors(DeleteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var deleted = report.Count(DeleteStatus.Deleted);
        var failed = report.Count(DeleteStatus.DeleteFailed);
        if (report.Stop is { } stop)
        {
            var possibly = stop.PossiblyDeleted ? " (削除された可能性あり)" : string.Empty;
            return
            [
                $"停止: エントリ #{stop.Entry.Number} \"{stop.Entry.DisplayName}\": {SafeDisplay.Escape(stop.Reason ?? string.Empty)}{possibly}",
                $"以後の処理を停止しました (削除済み {deleted}、DELETE_FAILED {failed}、未処理 {report.NotProcessedCount})。",
            ];
        }

        if (report.Prune is { Stop: { } directoryStop } prune)
        {
            var possibly = directoryStop.PossiblyDeleted ? " (削除された可能性あり)" : string.Empty;
            return
            [
                $"停止: フォルダー \"{SafeDisplay.EscapeForList(ReportText.WithoutDevicePrefix(directoryStop.Target), out _)}\": {SafeDisplay.Escape(directoryStop.Reason ?? string.Empty)}{possibly}",
                $"以後の処理を停止しました (削除済み {deleted}、DELETE_FAILED {failed}、フォルダー削除済み {prune.Count(DirectoryStatus.Deleted)}、フォルダー未処理 {prune.NotProcessedCount})。",
            ];
        }

        return failed > 0 ? [$"DELETE_FAILED が {failed} 件あるため、エラーとして終了します (削除済み {deleted})。"] : [];
    }
}
