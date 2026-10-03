using Unextract.Core.Analysis;

namespace Unextract.Core.Display;

// analyze と delete に共通の表示の部品 (SPEC §10、PLAN.md §5)。純粋関数。
public static class ReportText
{
    // Fast の警告 (SPEC §15.6、PLAN.md §5.2、DEC-20)。analyze・delete のヘッダーの先頭行と、delete の [y/N] の直前で同じ文言を使う。
    public const string FastWarning =
        "警告: --fast のため、パスとサイズだけで判定しています。内容が一致することと、ZIP から正常に展開できることは確認していません。";

    public const string Legend =
        "凡例: Target は target 内の対応する場所です。MISSING の場合は実在しない期待位置を示します。Target は確認用で、--entries には Entry を書きます。";

    // 結果行の見出し。状態名の列 (20 桁) + 空白2個の後に Entry が始まる。
    public const string Heading = "Status                Entry -> Target";

    public const string EscapedMark = " [表示用にエスケープ済み: --entries へそのまま転記できません]";

    private const int StatusWidth = 20;
    private const string DevicePrefix = @"\\?\";
    private const string UncDevicePrefix = @"\\?\UNC\";

    public static string CheckingProgress(int current, int total) => $"Checking {current} / {total}";

    public static string ProcessingProgress(int current, int total) => $"Processing {current} / {total}";

    // ヘッダー (PLAN.md §5.2)。Fast では警告を最初の行に置く。archivePath は指定されたまま (表示用エスケープのみ)、
    // targetFinalPath は保持用ハンドルの最終パス (\\?\ を除いて表示する)。
    public static IReadOnlyList<string> Header(string archivePath, string targetFinalPath, RunMode mode)
    {
        var lines = new List<string>();
        if (mode == RunMode.Fast)
        {
            lines.Add(FastWarning);
        }

        lines.Add($"Archive: {SafeDisplay.EscapeForList(archivePath, out _)}");
        lines.Add($"Target:  {SafeDisplay.EscapeForList(WithoutDevicePrefix(targetFinalPath), out _)}");
        lines.Add($"Mode:    {(mode == RunMode.Fast ? "Fast" : "Strict")}");
        lines.Add(Legend);
        return lines;
    }

    // 結果行 (SPEC §10.1、PLAN.md §5.1): 状態名を 20 桁に左詰め + 空白2個 + Entry + " -> " + Target + suffix。
    // Entry は ZIP の FullName を変換しない (\ を \\ にしない)。危険な文字を含む Entry は、行末に転記できない印を付ける。
    // target は期待パス (\\?\ 形式) で、\\?\ を除いて表示する。suffix は SKIPPED_SPECIAL_FILE の理由、DELETE_FAILED の理由など。
    public static string Line(string status, string entry, string target, string? suffix = null)
    {
        var shownEntry = SafeDisplay.EscapeForList(entry, out var escaped);
        var shownTarget = SafeDisplay.EscapeForList(WithoutDevicePrefix(target), out _);
        var line = $"{status.PadRight(StatusWidth)}  {shownEntry} -> {shownTarget}{suffix}";
        return escaped ? line + EscapedMark : line;
    }

    // SKIPPED_SPECIAL_FILE の理由の表示 (PLAN.md §5.1)。
    public static string Describe(SkipReason reason) => reason switch
    {
        SkipReason.ParentReparsePoint => "親が reparse",
        SkipReason.Directory => "ディレクトリ",
        SkipReason.ReparsePoint => "reparse",
        SkipReason.HardLink => "hardlink",
        SkipReason.AlternateDataStream => "ADS",
        SkipReason.ArchiveItself => "ZIP 自身",
        SkipReason.Attributes => "属性",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static string SkipSuffix(SkipReason? reason) => reason is { } r ? $" ({Describe(r)})" : string.Empty;

    // \\?\C:\... を C:\... に、\\?\UNC\server\share を \\server\share にする (表示用)。
    public static string WithoutDevicePrefix(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith(UncDevicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[UncDevicePrefix.Length..];
        }

        return path.StartsWith(DevicePrefix, StringComparison.Ordinal) ? path[DevicePrefix.Length..] : path;
    }
}
