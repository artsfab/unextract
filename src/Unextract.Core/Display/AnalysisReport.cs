using Unextract.Core.Analysis;
using Unextract.Core.Results;

namespace Unextract.Core.Display;

// 結果表示の組み立て (SPEC §10、テスト O01〜O03)。分類結果から行を作る純粋関数。名前は SafeDisplay でエスケープする。
// Format と FormatDeletion は標準出力 (解析結果の一覧と削除フェーズの結果)、FormatFatal と FormatDeletionErrors は
// 標準エラー出力 (FATAL・停止の原因とエラーで終わる理由) の行を返す。
public static class AnalysisReport
{
    private static readonly Classification[] Categories =
    [
        Classification.Matched,
        Classification.Modified,
        Classification.Missing,
        Classification.SkippedSpecialFile,
        Classification.Directory,
    ];

    public static string CheckingProgress(int current, int total) => $"Checking {current} / {total}";

    public static string DeletingProgress(int current, int total) => $"Deleting {current} / {total}";

    // 正常完走: 各カテゴリーの全パスと件数。FATAL: 判定済みのパスと件数、未判定の件数 (パスは列挙しない)。
    public static IReadOnlyList<string> Format(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = new List<string>();
        if (result.Fatal is not null)
        {
            lines.Add($"判定済み: {result.Results.Count} エントリ");
        }

        foreach (var category in Categories)
        {
            var entries = result.Results.Where(r => r.Classification == category).ToList();
            lines.Add($"{category.ToDisplayString()} ({entries.Count}):");
            lines.AddRange(entries.Select(r => "  " + DisplayPath(r.Entry)));
        }

        if (result.Fatal is not null)
        {
            lines.Add($"未判定: {result.UnclassifiedCount} エントリ");
            return lines;
        }

        lines.Add(
            $"合計: {result.TotalEntries} エントリ ("
            + string.Join("、", Categories.Select(c => $"{c.ToDisplayString()} {result.Count(c)}"))
            + ")");
        return lines;
    }

    // 削除開始前の FATAL: 原因エントリと原因、削除0件。FATAL がなければ空。
    public static IReadOnlyList<string> FormatFatal(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Fatal is { } fatal
            ? [$"FATAL: {fatal.Describe()}", "削除開始前に中止しました。削除0件。"]
            : [];
    }

    // 削除フェーズの結果 (SPEC §10): DELETE_FAILED のパスと理由、削除済み・DELETE_FAILED・未処理の件数。
    public static IReadOnlyList<string> FormatDeletion(DeletionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string> { $"DELETE_FAILED ({report.Failed.Count}):" };
        lines.AddRange(report.Failed.Select(f => $"  {DisplayPath(f.Entry)}: {SafeDisplay.Escape(f.Reason)}"));
        lines.Add($"削除済み {report.Deleted.Count}、DELETE_FAILED {report.Failed.Count}、未処理 {report.NotProcessedCount}");
        return lines;
    }

    // 削除フェーズでエラーになる理由 (SPEC §2、§10): 停止の原因のパスと理由、または停止なしで DELETE_FAILED があること。
    public static IReadOnlyList<string> FormatDeletionErrors(DeletionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>();
        if (report.Stop is { } stop)
        {
            var possibly = stop.PossiblyDeleted ? " (削除された可能性あり)" : string.Empty;
            lines.Add($"停止: {DisplayPath(stop.Entry)}: {SafeDisplay.Escape(stop.Reason)}{possibly}");
            lines.Add(
                $"以後の削除を停止しました。削除済みのファイルは戻りません。"
                + $"(削除済み {report.Deleted.Count}、DELETE_FAILED {report.Failed.Count}、未処理 {report.NotProcessedCount})");
        }
        else if (report.Failed.Count > 0)
        {
            lines.Add($"DELETE_FAILED が {report.Failed.Count} 件あるため、エラーとして終了します (削除済み {report.Deleted.Count})。");
        }

        return lines;
    }

    private static string DisplayPath(ZipEntryRef entry) => entry.DisplayName;
}
