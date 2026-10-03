using Unextract.Core.Analysis;
using Unextract.Core.Deletion;

namespace Unextract.Core.Display;

// delete の表示 (SPEC §10.3、PLAN.md §5.4)。純粋関数。結果行・要約は標準出力、STOP の原因とエラーで終わる理由は標準エラー出力。
public static class DeleteOutput
{
    public const string Cancelled = "中止しました。削除0件。";

    public const string NotInteractive = "標準入力が対話的でなく --yes も無いため、確認できません。";

    public const string PrepareAborted = "削除開始前に中止しました。削除0件。";

    // ヘッダーの後の処理対象の件数 (--entries の指定の有無を含む)。
    public static string Targets(int totalEntries, int targetEntries, bool entriesSelected) =>
        entriesSelected ? $"対象: {totalEntries} エントリ中 {targetEntries} エントリ (--entries)" : $"対象: 全 {totalEntries} エントリ";

    // 確認プロンプト (SPEC §3.2、§10.3)。fileEntries は処理対象のファイルエントリの件数 (削除される最大件数)。
    // Fast では [y/N] の直前に警告を置く (SPEC §10.4 の (3))。
    public static string ConfirmationPrompt(int fileEntries, RunMode mode)
    {
        var warning = mode == RunMode.Fast ? ReportText.FastWarning + Environment.NewLine : string.Empty;
        return warning
            + $"最大 {fileEntries} 件のファイルエントリを1件ずつ検証し、条件を満たしたものをその場で完全に削除します。" + Environment.NewLine
            + "途中で停止した場合、それまでに削除したファイルは元に戻りません。" + Environment.NewLine
            + "続行しますか? [y/N] ";
    }

    // 処理したファイルエントリ1件の結果行。SKIPPED_SPECIAL_FILE は理由、DELETE_FAILED は " : <理由>" を続ける。
    public static string Line(DeleteEntryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var suffix = result.Status switch
        {
            DeleteStatus.SkippedSpecialFile => ReportText.SkipSuffix(result.SkipReason),
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

        if (report.Stop is { } stop)
        {
            var name = SafeDisplay.EscapeForList(stop.Entry.Name, out _);
            var target = stop.PossiblyDeleted ? $"{name} は削除された可能性があります。" : $"{name} は削除していません。";
            lines.Add($"途中で停止しました。それまでに削除した {deleted} 件は元に戻りません。{target}未処理の {report.NotProcessedCount} 件には触れていません。");
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

        return failed > 0 ? [$"DELETE_FAILED が {failed} 件あるため、エラーとして終了します (削除済み {deleted})。"] : [];
    }
}
