using Unextract.Core.Analysis;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Tests.Fakes;
using static Unextract.Core.Tests.Fakes.PipelineHarness;

namespace Unextract.Core.Tests;

// 表示 (SPEC §10、テスト O01〜O03)。
public class AnalysisReportTests
{
    // O01: 全カテゴリーを含む正常完走 → 各カテゴリーの全パスと件数
    [Fact]
    public void O01_CompletedRun_ListsEveryCategoryWithAllPaths()
    {
        var zip = MakeZip(("same.txt", Bytes("hello")), ("changed.txt", Bytes("hello")), ("missing.txt", Bytes("x")), ("d/", null), ("d/link", Bytes("x")));
        using var harness = new PipelineHarness(zip);
        harness.Fs.AddFile(@"C:\target\same.txt", Bytes("hello"));
        harness.Fs.AddFile(@"C:\target\changed.txt", Bytes("hellO"));
        harness.Fs.AddDirectory(@"C:\target\d");
        harness.Fs.AddFile(@"C:\target\d\link", Bytes("x")).Links = 2;

        var lines = AnalysisReport.Format(harness.Run());

        Assert.Equal(
            [
                "MATCHED (1):",
                "  same.txt",
                "MODIFIED (1):",
                "  changed.txt",
                "MISSING (1):",
                "  missing.txt",
                "SKIPPED_SPECIAL_FILE (1):",
                "  d/link",
                "DIRECTORY (1):",
                "  d/",
                "合計: 5 エントリ (MATCHED 1、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1)",
            ],
            lines);
    }

    // O05: O01 と同じ全カテゴリーの fixture を Strict と --fast で。Strict は SAME_SIZE を件数0としても出さない。
    // Fast は MATCHED の位置に SAME_SIZE を表示し、MATCHED を出さない。共通カテゴリーの表示は従来どおり (SPEC §10)。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void O05_CategoriesByMode(RunMode mode)
    {
        var zip = MakeZip(("same.txt", Bytes("hello")), ("changed.txt", Bytes("hello")), ("size.txt", Bytes("hello")), ("missing.txt", Bytes("x")), ("d/", null), ("d/link", Bytes("x")));
        using var harness = new PipelineHarness(zip) { Mode = mode };
        harness.Fs.AddFile(@"C:\target\same.txt", Bytes("hello"));
        harness.Fs.AddFile(@"C:\target\changed.txt", Bytes("hellO"));
        harness.Fs.AddFile(@"C:\target\size.txt", Bytes("hello!"));
        harness.Fs.AddDirectory(@"C:\target\d");
        harness.Fs.AddFile(@"C:\target\d\link", Bytes("x")).Links = 2;

        var lines = AnalysisReport.Format(harness.Run(), mode);

        string[] expected = mode == RunMode.Strict
            ?
            [
                "MATCHED (1):",
                "  same.txt",
                "MODIFIED (2):",
                "  changed.txt",
                "  size.txt",
                "MISSING (1):",
                "  missing.txt",
                "SKIPPED_SPECIAL_FILE (1):",
                "  d/link",
                "DIRECTORY (1):",
                "  d/",
                "合計: 6 エントリ (MATCHED 1、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1)",
            ]
            :
            [
                AnalysisReport.FastWarning,
                "SAME_SIZE (2):",
                "  same.txt",
                "  changed.txt",
                "MODIFIED (1):",
                "  size.txt",
                "MISSING (1):",
                "  missing.txt",
                "SKIPPED_SPECIAL_FILE (1):",
                "  d/link",
                "DIRECTORY (1):",
                "  d/",
                "合計: 6 エントリ (SAME_SIZE 2、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1)",
            ];
        Assert.Equal(expected, lines);
        Assert.DoesNotContain(lines, l => l.Contains(mode == RunMode.Strict ? "SAME_SIZE" : "MATCHED", StringComparison.Ordinal));
    }

    // O05 (FATAL): O02 と同じ FATAL を両モードで (FATAL の原因は内容に依存しない共有違反)。Fast は判定済みのカテゴリーが SAME_SIZE
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void O05_FatalAtEntry40Of100_ByMode(RunMode mode)
    {
        var entries = Enumerable.Range(1, 100).Select(i => ($"f{i:D3}.txt", (byte[]?)Bytes("hello"))).ToArray();
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
        var lines = AnalysisReport.Format(result, mode);

        var offset = mode == RunMode.Fast ? 1 : 0;
        if (mode == RunMode.Fast)
        {
            Assert.Equal(AnalysisReport.FastWarning, lines[0]);
        }

        Assert.Equal("判定済み: 39 エントリ", lines[offset]);
        Assert.Equal(mode == RunMode.Fast ? "SAME_SIZE (39):" : "MATCHED (39):", lines[offset + 1]);
        Assert.Equal(Enumerable.Range(1, 39).Select(i => $"  f{i:D3}.txt"), lines.Skip(offset + 2).Take(39));
        Assert.Equal(
            ["MODIFIED (0):", "MISSING (0):", "SKIPPED_SPECIAL_FILE (0):", "DIRECTORY (0):", "未判定: 60 エントリ"],
            lines.Skip(offset + 41));
        Assert.Equal(
            "FATAL: エントリ #40 \"f040.txt\": 存在を確認したファイルを開けません (他のプログラムが使用中の場合を含む) (OpenComparison が失敗 (Win32 エラー 32))",
            AnalysisReport.FormatFatal(result)[0]);
    }

    // O02: 100 件中 40 件目の FATAL → 判定済み 39 件のパス、原因エントリと原因、未判定 60 件は件数のみ、削除0件
    [Fact]
    public void O02_FatalAtEntry40Of100()
    {
        var entries = Enumerable.Range(1, 100).Select(i => ($"f{i:D3}.txt", (byte[]?)Bytes("hello"))).ToArray();
        using var harness = new PipelineHarness(MakeZip(entries));
        for (var i = 1; i <= 100; i++)
        {
            var node = harness.Fs.AddFile($@"C:\target\f{i:D3}.txt", Bytes("hello"));
            if (i == 40)
            {
                node.Errors[FakeOp.OpenComparison] = 32;
            }
        }

        var result = harness.Run();
        var lines = AnalysisReport.Format(result);

        Assert.Equal(39, result.Results.Count);
        Assert.Equal(60, result.UnclassifiedCount);
        Assert.Equal("判定済み: 39 エントリ", lines[0]);
        Assert.Equal("MATCHED (39):", lines[1]);
        Assert.Equal(Enumerable.Range(1, 39).Select(i => $"  f{i:D3}.txt"), lines.Skip(2).Take(39));
        Assert.Equal("未判定: 60 エントリ", lines[^1]);
        Assert.DoesNotContain(lines, l => l.Contains("f041", StringComparison.Ordinal) || l.Contains("f100", StringComparison.Ordinal));

        // 原因エントリと原因、削除0件は標準エラー出力の行 (SPEC §10)。
        Assert.Equal(
            [
                "FATAL: エントリ #40 \"f040.txt\": 存在を確認したファイルを開けません (他のプログラムが使用中の場合を含む) (OpenComparison が失敗 (Win32 エラー 32))",
                "削除開始前に中止しました。削除0件。",
            ],
            AnalysisReport.FormatFatal(result));
    }

    // O03 (表示関数の範囲): DELETE_FAILED のパスと理由、停止原因のパスと理由、削除済み・DELETE_FAILED・未処理の件数
    [Fact]
    public void O03_DeletionReport()
    {
        var report = new DeletionReport(
            [new ZipEntryRef(0, "a.txt"), new ZipEntryRef(1, "b.txt")],
            [new DeleteFailure(new ZipEntryRef(2, "busy.txt"), "使用中 (Win32 エラー 32)")],
            new DeletionStop(new ZipEntryRef(4, "d/\u202Eevil.txt"), "成立を確認できません", PossiblyDeleted: true),
            NotProcessedCount: 3);

        Assert.Equal(
            [
                "DELETE_FAILED (1):",
                "  busy.txt: 使用中 (Win32 エラー 32)",
                "削除済み 2、DELETE_FAILED 1、未処理 3",
            ],
            AnalysisReport.FormatDeletion(report));
        Assert.Equal(
            [
                "停止: d/\\u{202E}evil.txt: 成立を確認できません (削除された可能性あり)",
                "以後の削除を停止しました。削除済みのファイルは戻りません。(削除済み 2、DELETE_FAILED 1、未処理 3)",
            ],
            AnalysisReport.FormatDeletionErrors(report));

        // 停止がなく DELETE_FAILED だけがある場合は、エラーで終わる理由を件数とともに出す (SPEC §2、§10、DEC-18)。
        var failedOnly = report with { Stop = null, NotProcessedCount = 0 };
        Assert.Equal(
            ["DELETE_FAILED が 1 件あるため、エラーとして終了します (削除済み 2)。"],
            AnalysisReport.FormatDeletionErrors(failedOnly));
        Assert.Empty(AnalysisReport.FormatDeletionErrors(report with { Stop = null, Failed = [] }));
        Assert.Equal("Deleting 3 / 10", AnalysisReport.DeletingProgress(3, 10));
    }

    // 事前検証の FATAL (target に触れる前): 判定済み0件、原因エントリ、未判定はそれ以外の件数
    [Fact]
    public void PrevalidationFatal_ShowsCauseAndUnclassifiedCount()
    {
        var result = AnalysisResult.BeforeClassification(10, new FatalError(FatalKind.ReservedName, new ZipEntryRef(3, "CON")));

        var lines = AnalysisReport.Format(result);

        Assert.Equal("判定済み: 0 エントリ", lines[0]);
        Assert.Equal("FATAL: エントリ #4 \"CON\": Windows の予約名を含みます", AnalysisReport.FormatFatal(result)[0]);
        Assert.Equal("未判定: 9 エントリ", lines[^1]);
        Assert.Empty(result.DeletionCandidates);
    }

    // 表示する名前はエスケープする (テスト O04 と同じ規則)
    [Fact]
    public void Names_AreEscaped()
    {
        var zip = MakeZip(("a\u200Fb.txt", Bytes("x")));
        using var harness = new PipelineHarness(zip);

        var lines = AnalysisReport.Format(harness.Run());

        Assert.Contains("  a\\u{200F}b.txt", lines);
    }
}
