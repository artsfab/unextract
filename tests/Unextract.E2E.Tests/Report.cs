namespace Unextract.E2E.Tests;

// stdout の解析結果の一覧 (SPEC §10) を読む。各カテゴリーは「名前 (件数):」の行と、続く「  パス」の行。
public sealed class Report
{
    public static readonly string[] Categories = ["MATCHED", "MODIFIED", "MISSING", "SKIPPED_SPECIAL_FILE", "DIRECTORY"];

    private readonly Dictionary<string, (int Count, List<string> Paths)> _sections = [];

    private Report(string[] lines)
    {
        string? current = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("  ", StringComparison.Ordinal) && current is not null)
            {
                _sections[current].Paths.Add(line[2..]);
                continue;
            }

            current = null;
            foreach (var category in Categories)
            {
                var prefix = category + " (";
                if (line.StartsWith(prefix, StringComparison.Ordinal) && line.EndsWith("):", StringComparison.Ordinal))
                {
                    current = category;
                    _sections[category] = (int.Parse(line[prefix.Length..^2], System.Globalization.CultureInfo.InvariantCulture), []);
                }
            }
        }
    }

    public static Report Parse(ProcessResult result) => new(result.OutputLines);

    public IReadOnlyList<string> Paths(string category) => _sections[category].Paths;

    public int Count(string category) => _sections[category].Count;

    // 解析結果の一覧の部分: 先頭から「合計:」または「未判定:」の行まで (それ以降は dry-run・確認・削除フェーズの行)。
    public static IReadOnlyList<string> AnalysisPart(ProcessResult result)
    {
        var lines = result.OutputLines;
        var end = Array.FindIndex(lines, l => l.StartsWith("合計:", StringComparison.Ordinal) || l.StartsWith("未判定:", StringComparison.Ordinal));
        Assert.True(end >= 0, $"解析結果の一覧の終わりが見つからない\n{result}");
        return lines[..(end + 1)];
    }

    // 件数とパスが一致し、件数とパスの数も一致すること。
    public void AssertCategory(string category, params string[] paths)
    {
        Assert.Equal(paths, Paths(category));
        Assert.Equal(paths.Length, Count(category));
    }
}
