namespace Unextract.E2E.Tests;

// stdout の結果行 (SPEC §10.1) を読む。各行は「状態名を 20 桁に左詰め」+ 空白2個 + Entry + " -> " + Target (+ 理由)。
// Entry は ZIP の FullName を変換せずに表示したもの (> はエントリ名に現れないため、最初の " -> " で区切れる)。
public sealed class Report
{
    public static readonly string[] Statuses =
        ["MATCHED", "SAME_SIZE", "MODIFIED", "MISSING", "SKIPPED_SPECIAL_FILE", "DIRECTORY", "DELETED", "DELETE_FAILED", "STOPPED"];

    private const int StatusWidth = 20;
    private const string Separator = " -> ";

    private readonly List<(string Status, string Entry, string Target)> _lines = [];

    private Report(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Length <= StatusWidth + 2 || line[StatusWidth] != ' ' || line[StatusWidth + 1] != ' ')
            {
                continue;
            }

            var status = line[..StatusWidth].TrimEnd();
            if (!Statuses.Contains(status))
            {
                continue;
            }

            var rest = line[(StatusWidth + 2)..];
            var separator = rest.IndexOf(Separator, StringComparison.Ordinal);
            Assert.True(separator >= 0, $"結果行に \" -> \" が無い: {line}");
            _lines.Add((status, rest[..separator], rest[(separator + Separator.Length)..]));
        }
    }

    public static Report Parse(ProcessResult result) => new(result.OutputLines);

    public static Report Parse(string output) => new(output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));

    // そのモード・操作で出た状態名の全て (結果行の順)。
    public IReadOnlyList<string> StatusesInOrder => _lines.Select(l => l.Status).ToList();

    public IReadOnlyList<string> Entries(string status) => _lines.Where(l => l.Status == status).Select(l => l.Entry).ToList();

    public IReadOnlyList<string> AllEntries => _lines.Select(l => l.Entry).ToList();

    public string TargetOf(string entry) => _lines.Single(l => l.Entry == entry).Target;

    public bool Has(string status) => _lines.Any(l => l.Status == status);

    // その状態名の結果行の Entry が、この順でちょうどこれだけあること。
    public void AssertStatus(string status, params string[] entries) => Assert.Equal(entries, Entries(status));
}
