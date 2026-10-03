using Unextract.Core.Analysis;
using Unextract.Core.Results;

namespace Unextract.Core.Display;

// analyze の結果表示 (SPEC §10.2、PLAN.md §5.3)。ヘッダー (ReportText.Header) の後に続く行を作る純粋関数。
// Format は標準出力、FormatFatal は標準エラー出力の行。
public static class AnalyzeOutput
{
    public const string Closing = "analyze は削除しません。削除は unextract delete で行います (delete は実行時の状態を改めて検証します)。";

    public const string FatalClosing = "解析を中止しました。analyze は削除を行いません (削除0件)。";

    private static readonly Classification[] StrictCategories =
    [
        Classification.Matched,
        Classification.Modified,
        Classification.Missing,
        Classification.SkippedSpecialFile,
        Classification.Directory,
    ];

    // Fast は削除対象のカテゴリーだけを入れ替える (MATCHED の位置に SAME_SIZE。SPEC §10.2、DEC-21)。
    private static readonly Classification[] FastCategories =
    [
        Classification.SameSize,
        Classification.Modified,
        Classification.Missing,
        Classification.SkippedSpecialFile,
        Classification.Directory,
    ];

    public static IReadOnlyList<Classification> Categories(RunMode mode) => mode == RunMode.Fast ? FastCategories : StrictCategories;

    // 正常完走: 見出し、カテゴリー順 (カテゴリー内は ZIP の順) の結果行、合計行、analyze は削除しないことの案内。
    // 判定中の FATAL: 判定済みの結果行と件数、FATAL の原因が1件、未判定の件数 (パスは列挙しない)。
    // target に触れる前の FATAL (ZIP 事前検証など): 結果行と見出しを出さず、判定済み 0 と未判定の件数。
    public static IReadOnlyList<string> Format(AnalysisResult result, RunMode mode)
    {
        ArgumentNullException.ThrowIfNull(result);

        var categories = Categories(mode);
        var lines = new List<string>();
        if (result.FatalBeforeClassification)
        {
            lines.Add("判定済み: 0 エントリ");
            lines.Add($"未判定: {result.UnclassifiedCount} エントリ");
            return lines;
        }

        lines.Add(ReportText.Heading);
        foreach (var category in categories)
        {
            foreach (var entry in result.Results.Where(r => r.Classification == category))
            {
                lines.Add(ReportText.Line(category.ToDisplayString(), entry.Entry.Name, entry.Target, ReportText.SkipSuffix(entry.SkipReason)));
            }
        }

        var counts = string.Join("、", categories.Select(c => $"{c.ToDisplayString()} {result.Count(c)}"));
        if (result.Fatal is { } fatal)
        {
            lines.Add($"判定済み: {result.Results.Count} エントリ ({counts})");
            if (fatal.Entry is { } entry)
            {
                lines.Add($"FATAL: 1 エントリ (#{entry.Number})");
            }

            lines.Add($"未判定: {result.UnclassifiedCount} エントリ");
            return lines;
        }

        lines.Add($"合計: {result.TotalEntries} エントリ ({counts})");
        lines.Add(Closing);
        return lines;
    }

    // FATAL の原因と、analyze は削除しないこと。FATAL がなければ空。
    public static IReadOnlyList<string> FormatFatal(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Fatal is { } fatal
            ? [$"FATAL: {fatal.Describe()}", FatalClosing]
            : [];
    }
}
