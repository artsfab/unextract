using Unextract.Core.Analysis;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Tests.Fakes;
using static Unextract.Core.Tests.Fakes.PipelineHarness;

namespace Unextract.Core.Tests;

// analyze の表示 (docs/spec/cli.md#result-lines・docs/spec/cli.md#analyze-output、docs/spec/cli.md#output) と結果行の表示用エスケープ (テスト O08〜O12)。
public class AnalyzeOutputTests
{
    private static readonly byte[] Hello = Bytes("hello");

    public static TheoryData<RunMode> BothModes => new() { RunMode.Strict, RunMode.Fast };

    // 全カテゴリーの fixture (MATCHED (Fast は SAME_SIZE)、MODIFIED (内容違い・サイズ違い)、MISSING、SKIPPED_SPECIAL_FILE、DIRECTORY)。
    private static PipelineHarness AllCategories(RunMode mode)
    {
        var harness = new PipelineHarness(MakeZip(
            ("m.txt", Hello),
            ("content.txt", Hello),
            ("size.txt", Hello),
            ("missing.txt", Hello),
            ("dir.txt", Hello),
            ("docs/", null),
            ("m2.txt", Hello)))
        { Mode = mode };
        harness.Fs.AddFile(@"C:\target\m.txt", Bytes("hello"));
        harness.Fs.AddFile(@"C:\target\content.txt", Bytes("hellO"));
        harness.Fs.AddFile(@"C:\target\size.txt", Bytes("hello!"));
        harness.Fs.AddDirectory(@"C:\target\dir.txt");
        harness.Fs.AddFile(@"C:\target\m2.txt", Bytes("hello"));
        harness.Fs.AddFile(@"C:\target\unrelated.txt", Bytes("x"));
        return harness;
    }

    // O08: 状態名 20 桁 + 空白2 + Entry + " -> " + Target。Entry は 23 桁目から。カテゴリー順・カテゴリー内 ZIP 順。
    // 0件のカテゴリーは行なし、合計行には全カテゴリー (0件を含む)。O11: Strict に SAME_SIZE なし、Fast に MATCHED なし。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void O08_O11_LineFormatAndCategoryOrder(RunMode mode)
    {
        using var harness = AllCategories(mode);

        var lines = AnalyzeOutput.Format(harness.Run(), mode);

        var candidate = mode == RunMode.Fast ? "SAME_SIZE" : "MATCHED";
        var expected = new List<string> { "Status                Entry -> Target" };
        if (mode == RunMode.Fast)
        {
            expected.Add(@"SAME_SIZE             m.txt -> C:\target\m.txt");
            expected.Add(@"SAME_SIZE             content.txt -> C:\target\content.txt");
            expected.Add(@"SAME_SIZE             m2.txt -> C:\target\m2.txt");
            expected.Add(@"MODIFIED              size.txt -> C:\target\size.txt");
        }
        else
        {
            expected.Add(@"MATCHED               m.txt -> C:\target\m.txt");
            expected.Add(@"MATCHED               m2.txt -> C:\target\m2.txt");
            expected.Add(@"MODIFIED              content.txt -> C:\target\content.txt");
            expected.Add(@"MODIFIED              size.txt -> C:\target\size.txt");
        }

        expected.Add(@"MISSING               missing.txt -> C:\target\missing.txt");
        expected.Add(@"SKIPPED_SPECIAL_FILE  dir.txt -> C:\target\dir.txt (ディレクトリ)");
        expected.Add(@"DIRECTORY             docs/ -> C:\target\docs");
        expected.Add(mode == RunMode.Fast
            ? "合計: 7 エントリ (SAME_SIZE 3、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1)"
            : "合計: 7 エントリ (MATCHED 2、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1)");
        expected.Add(AnalyzeOutput.Closing);
        Assert.Equal(expected, lines);
        Assert.All(lines.Skip(1).SkipLast(2), l => Assert.NotEqual(' ', l[22]));
        Assert.DoesNotContain(lines, l => l.Contains("unrelated", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith(mode == RunMode.Fast ? "MATCHED" : "SAME_SIZE", StringComparison.Ordinal));
        Assert.StartsWith(candidate, lines[1], StringComparison.Ordinal);
    }

    // O08: 0件のカテゴリーは行を出さないが、合計行には 0 として出す。
    [Fact]
    public void O08_EmptyCategoriesHaveNoLinesButAreCounted()
    {
        using var harness = new PipelineHarness(MakeZip(("missing.txt", Hello)));

        var lines = AnalyzeOutput.Format(harness.Run(), RunMode.Strict);

        Assert.Equal(
            [
                ReportText.Heading,
                @"MISSING               missing.txt -> C:\target\missing.txt",
                "合計: 1 エントリ (MATCHED 0、MODIFIED 0、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0)",
                AnalyzeOutput.Closing,
            ],
            lines);
    }

    // O09: Entry は変換しない (\ を \\ にしない、先頭空白・日本語・CP437 由来の文字をそのまま)。Target は \\?\ を除いた target の最終パス + 成分。
    [Fact]
    public void O09_EntryIsShownVerbatim()
    {
        using var harness = new PipelineHarness(MakeZip((@"bin\a.dll", Hello), (" lead.txt", Hello), ("日本語/名前.txt", Hello), ("café░.txt", Hello)));

        var lines = AnalyzeOutput.Format(harness.Run(), RunMode.Strict);

        Assert.Contains(@"MISSING               bin\a.dll -> C:\target\bin\a.dll", lines);
        Assert.Contains(@"MISSING                lead.txt -> C:\target\ lead.txt", lines);
        Assert.Contains(@"MISSING               日本語/名前.txt -> C:\target\日本語\名前.txt", lines);
        Assert.Contains(@"MISSING               café░.txt -> C:\target\café░.txt", lines);
        Assert.DoesNotContain(lines, l => l.Contains(ReportText.EscapedMark, StringComparison.Ordinal));
    }

    // O10: 書式文字・C1 制御・U+2028 を含む名前は Entry・Target ともエスケープ表示し、転記できない印を付ける。
    [Theory]
    [InlineData("a\u200Bb.txt", "a\\u{200B}b.txt")]
    [InlineData("a\u202Eb.txt", "a\\u{202E}b.txt")]
    [InlineData("a\u0085b.txt", "a\\u{0085}b.txt")]
    [InlineData("a\u2028b.txt", "a\\u{2028}b.txt")]
    public void O10_DangerousCharactersAreEscapedAndMarked(string name, string shown)
    {
        using var harness = new PipelineHarness(MakeZip((name, Hello)));

        var lines = AnalyzeOutput.Format(harness.Run(), RunMode.Strict);

        Assert.Equal($@"MISSING               {shown} -> C:\target\{shown}{ReportText.EscapedMark}", lines[1]);
    }

    // O04: FATAL の原因の表示は既存のエスケープ (\ と " も変換する) のまま。
    [Fact]
    public void O04_FatalCauseUsesExistingEscape()
    {
        var fatal = new FatalError(FatalKind.ControlCharacter, new ZipEntryRef(2, "a\u0001\\b\"c"));

        Assert.Equal("エントリ #3 \"a\\u{0001}\\\\b\\\"c\": 制御文字を含みます", fatal.Describe());
    }

    // O11: Fast はヘッダーの先頭行に警告。Strict では出ない。ヘッダーの形式 (docs/spec/cli.md#warning)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void O11_HeaderAndFastWarning(RunMode mode)
    {
        var header = ReportText.Header(@"in\a.zip", @"\\?\C:\work\target", mode);

        var expected = new List<string>();
        if (mode == RunMode.Fast)
        {
            expected.Add(ReportText.FastWarning);
        }

        expected.Add(@"Archive: in\a.zip");
        expected.Add(@"Target:  C:\work\target");
        expected.Add(mode == RunMode.Fast ? "Mode:    Fast" : "Mode:    Strict");
        expected.Add(ReportText.Legend);
        Assert.Equal(expected, header);
    }

    // O12 (判定中の FATAL): 判定済みの結果行・判定済み件数・FATAL 1 エントリ (#n)・未判定 (stdout)、原因と削除0件 (stderr)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void O12_FatalDuringClassification(RunMode mode)
    {
        var entries = Enumerable.Range(1, 100).Select(i => ($"f{i:D3}.txt", (byte[]?)Hello)).ToArray();
        using var harness = new PipelineHarness(MakeZip(entries)) { Mode = mode };
        for (var i = 1; i <= 100; i++)
        {
            var node = harness.Fs.AddFile($@"C:\target\f{i:D3}.txt", Bytes("hello"));
            if (i == 40)
            {
                node.Errors[FakeOp.OpenComparison] = 32;
            }
        }

        var result = harness.Run();
        var lines = AnalyzeOutput.Format(result, mode);

        var candidate = mode == RunMode.Fast ? "SAME_SIZE" : "MATCHED";
        Assert.Equal(ReportText.Heading, lines[0]);
        Assert.Equal(39, lines.Count(l => l.StartsWith(candidate + " ", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.Contains("f040.txt", StringComparison.Ordinal) || l.Contains("f041.txt", StringComparison.Ordinal));
        var counts = mode == RunMode.Fast
            ? "SAME_SIZE 39、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0"
            : "MATCHED 39、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0";
        Assert.Equal([$"判定済み: 39 エントリ ({counts})", "FATAL: 1 エントリ (#40)", "未判定: 60 エントリ"], lines.TakeLast(3));
        Assert.DoesNotContain(AnalyzeOutput.Closing, lines);

        var errors = AnalyzeOutput.FormatFatal(result);
        Assert.Equal(2, errors.Count);
        Assert.StartsWith("FATAL: エントリ #40 \"f040.txt\": 存在を確認したファイルを開けません", errors[0], StringComparison.Ordinal);
        Assert.Contains("Win32 エラー 32", errors[0], StringComparison.Ordinal);
        Assert.Equal(AnalyzeOutput.FatalClosing, errors[1]);
    }

    // O12 (Prepare の FATAL): 結果行と見出しを出さず、判定済み 0 と未判定 (全件)。原因は stderr。
    [Fact]
    public void O12_FatalBeforeClassification()
    {
        var result = AnalysisResult.BeforeClassification(10, new FatalError(FatalKind.ReservedName, new ZipEntryRef(3, "CON")));

        Assert.Equal(["判定済み: 0 エントリ", "未判定: 10 エントリ"], AnalyzeOutput.Format(result, RunMode.Strict));
        Assert.Equal(
            ["FATAL: エントリ #4 \"CON\": Windows の予約名を含みます", AnalyzeOutput.FatalClosing],
            AnalyzeOutput.FormatFatal(result));
    }

    [Fact]
    public void FormatFatal_IsEmptyWhenCompleted()
    {
        using var harness = new PipelineHarness(MakeZip(("a.txt", Hello)));

        Assert.Empty(AnalyzeOutput.FormatFatal(harness.Run()));
    }

    // SKIPPED_SPECIAL_FILE の理由の表示 (docs/spec/cli.md#result-lines)。
    [Theory]
    [InlineData(SkipReason.ParentReparsePoint, " (親が reparse)")]
    [InlineData(SkipReason.Directory, " (ディレクトリ)")]
    [InlineData(SkipReason.ReparsePoint, " (reparse)")]
    [InlineData(SkipReason.HardLink, " (hardlink)")]
    [InlineData(SkipReason.AlternateDataStream, " (ADS)")]
    [InlineData(SkipReason.ArchiveItself, " (ZIP 自身)")]
    [InlineData(SkipReason.Attributes, " (属性)")]
    public void SkipReason_IsShown(SkipReason reason, string suffix)
    {
        Assert.Equal(suffix, ReportText.SkipSuffix(reason));
        Assert.Equal(string.Empty, ReportText.SkipSuffix(null));
    }

    [Theory]
    [InlineData(@"\\?\C:\a\b", @"C:\a\b")]
    [InlineData(@"\\?\UNC\server\share\x", @"\\server\share\x")]
    [InlineData(@"C:\a", @"C:\a")]
    public void DevicePrefix_IsRemovedForDisplay(string path, string shown) =>
        Assert.Equal(shown, ReportText.WithoutDevicePrefix(path));
}
