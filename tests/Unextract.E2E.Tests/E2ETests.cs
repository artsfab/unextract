using System.Runtime.CompilerServices;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.E2E.Tests;

// E2E (X 系、docs/PLAN_TESTS.md)。ビルド済みの unextract.exe (または UNEXTRACT_E2E_EXE の exe) を別プロセスとして起動し、
// 終了コード・stdout・stderr・ファイルシステムの結果を検証する。stdin・stderr は常にリダイレクトされるため、exe からは
// 非対話で、進捗は表示されない。TTY が必要な項目 (対話の [y/N]、進捗の1行上書き、コードページごとの表示) は
// docs/MANUAL_TESTS.md の M 系で扱う。実削除 (--yes) の前には必ず E2EFixture.RunDeleting の領域外ガードを通す。
public class E2ETests
{
    private const int Success = 0;
    private const int Error = 1;
    private const int Cancelled = 2;

    private const string NoCandidates = "削除候補はありません。";
    private const string AbortedNoDeletion = "中止しました。削除0件。";
    private const string FatalNoDeletion = "削除開始前に中止しました。削除0件。";

    // 全カテゴリーを含む fixture。ZIP にない target ファイル (unrelated.txt、docs\unrelated-in-docs.txt) も置く。
    private static E2EFixture AllCategories([CallerMemberName] string testName = "")
    {
        var fixture = E2EFixture.Create(testName);
        fixture.WriteZip(ZipFixture.Create(
            new FixtureEntry("same1.txt", E2EFixture.Bytes("hello1")),
            new FixtureEntry("docs/"),
            new FixtureEntry("docs/deep.txt", E2EFixture.Bytes("deep")),
            new FixtureEntry("changed.txt", E2EFixture.Bytes("hello")),
            new FixtureEntry("longer.txt", E2EFixture.Bytes("abc")),
            new FixtureEntry("missing.txt", E2EFixture.Bytes("x")),
            new FixtureEntry("folder.txt", E2EFixture.Bytes("x"))));
        fixture.WriteTarget("same1.txt", "hello1")
            .WriteTarget("docs/deep.txt", "deep")
            .WriteTarget("changed.txt", "hellO")
            .WriteTarget("longer.txt", "abcdef")
            .CreateTargetDirectory("folder.txt")
            .WriteTarget("unrelated.txt", "not in zip")
            .WriteTarget("docs/unrelated-in-docs.txt", "not in zip");
        return fixture;
    }

    private static void AssertAllCategoriesReport(ProcessResult result)
    {
        var report = Report.Parse(result);
        report.AssertCategory("MATCHED", "same1.txt", "docs/deep.txt");
        report.AssertCategory("MODIFIED", "changed.txt", "longer.txt");
        report.AssertCategory("MISSING", "missing.txt");
        report.AssertCategory("SKIPPED_SPECIAL_FILE", "folder.txt");
        report.AssertCategory("DIRECTORY", "docs/");
        Assert.Contains(
            "合計: 7 エントリ (MATCHED 2、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1)",
            result.OutputLines);
        Assert.DoesNotContain("unrelated", result.StandardOutput, StringComparison.Ordinal);
    }

    // X01: --dry-run は target と ZIP を変えず、各カテゴリーの全パスと件数を stdout に出す。ZIP にないファイルは出さない。
    [Fact]
    public void X01_DryRunChangesNothingAndListsAllCategories()
    {
        var fixture = AllCategories();
        var target = E2EFixture.Snapshot(fixture.Target);
        var zip = E2EFixture.Describe(fixture.ArchivePath);

        var result = fixture.Run(stdin: null, "--dry-run");

        Assert.True(result.ExitCode == Success, result.ToString());
        AssertAllCategoriesReport(result);
        Assert.Equal("--dry-run のため削除しません。", result.OutputLines[^1]);
        Assert.Equal(target, E2EFixture.Snapshot(fixture.Target));
        Assert.Equal(zip, E2EFixture.Describe(fixture.ArchivePath));
    }

    // X02: --yes は MATCHED だけを削除する。MODIFIED・ZIP にないファイル・ディレクトリ・ZIP は残る。
    [Fact]
    public void X02_YesDeletesOnlyMatched()
    {
        var fixture = AllCategories();
        var before = E2EFixture.Snapshot(fixture.Target);
        var zip = E2EFixture.Describe(fixture.ArchivePath);

        var result = fixture.RunDeleting(stdin: null, "--yes");

        Assert.True(result.ExitCode == Success, result.ToString());
        AssertAllCategoriesReport(result);
        Assert.Contains("DELETE_FAILED (0):", result.OutputLines);
        Assert.Equal("削除済み 2、DELETE_FAILED 0、未処理 0", result.OutputLines[^1]);
        Assert.Equal(string.Empty, result.StandardError);

        var expected = new SortedDictionary<string, string>(before, StringComparer.Ordinal);
        expected.Remove("same1.txt");
        expected.Remove(@"docs\deep.txt");
        var after = E2EFixture.Snapshot(fixture.Target);

        // ファイルの削除でディレクトリの更新日時は変わるため、ディレクトリは存在だけを比べる。
        Assert.Equal(expected.Keys, after.Keys);
        foreach (var (path, description) in expected.Where(e => !e.Value.StartsWith("dir ", StringComparison.Ordinal)))
        {
            Assert.Equal(description, after[path]);
        }

        Assert.True(Directory.Exists(Path.Combine(fixture.Target, "docs")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Target, "folder.txt")));
        Assert.Equal(zip, E2EFixture.Describe(fixture.ArchivePath));
    }

    // X03: --yes なしで stdin がリダイレクトされている (空のまま閉じる / n を渡す) と非対話として中止する (SPEC §2、E-2 の実測)。
    [Theory]
    [InlineData(null)]
    [InlineData("n\n")]
    [InlineData("y\n")]
    public void X03_NonInteractiveWithoutYesCancels(string? stdin)
    {
        var fixture = AllCategories();
        var target = E2EFixture.Snapshot(fixture.Target);
        var zip = E2EFixture.Describe(fixture.ArchivePath);

        // 誤って削除フェーズに入った場合に備えて、--yes と同じ領域外ガードを通す。
        var result = fixture.RunDeleting(stdin);

        Assert.True(result.ExitCode == Cancelled, result.ToString());
        AssertAllCategoriesReport(result);
        Assert.Contains("標準入力が対話的でなく --yes も無いため、確認できません。", result.OutputLines);
        Assert.Equal(AbortedNoDeletion, result.OutputLines[^1]);
        Assert.DoesNotContain("[y/N]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(target, E2EFixture.Snapshot(fixture.Target));
        Assert.Equal(zip, E2EFixture.Describe(fixture.ArchivePath));
    }

    // CRC-32 だけを書き換えた bad.txt (target にサイズ一致のファイルあり) を3番目に置いた fixture。
    private static E2EFixture CrcMismatch([CallerMemberName] string testName = "")
    {
        var fixture = E2EFixture.Create(testName);
        var zip = ZipFixture.Create(
            new FixtureEntry("a.txt", E2EFixture.Bytes("hello")),
            new FixtureEntry("b.txt", E2EFixture.Bytes("hello")),
            new FixtureEntry("bad.txt", E2EFixture.Bytes("payload")),
            new FixtureEntry("c.txt", E2EFixture.Bytes("hello")),
            new FixtureEntry("d.txt", E2EFixture.Bytes("hello")));
        var patcher = new ZipPatcher(zip);
        patcher.SetCrc32(2, patcher.GetCrc32(2) ^ 0xFFFFFFFF);
        fixture.WriteZip(patcher.ToArray());
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt", "d.txt" })
        {
            fixture.WriteTarget(name, "hello");
        }

        fixture.WriteTarget("bad.txt", "payload");
        return fixture;
    }

    // X04: 先頭に MATCHED があっても、後方の内容比較候補の CRC 不一致で全体 FATAL、--yes でも削除0件。
    // FATAL の原因は stderr、判定済みのパスと判定済み・未判定の件数は stdout (SPEC §10)。未判定のパスは出さない。
    [Fact]
    public void X04_FatalAfterMatchedDeletesNothing()
    {
        var fixture = CrcMismatch();
        var target = E2EFixture.Snapshot(fixture.Target);
        var zip = E2EFixture.Describe(fixture.ArchivePath);

        var result = fixture.RunDeleting(stdin: null, "--yes");

        Assert.True(result.ExitCode == Error, result.ToString());
        Assert.Equal(["FATAL: エントリ #3 \"bad.txt\": エントリの CRC-32 が一致しません", FatalNoDeletion], result.ErrorLines);
        Assert.Equal("判定済み: 2 エントリ", result.OutputLines[0]);
        Assert.Equal("未判定: 2 エントリ", result.OutputLines[^1]);
        var report = Report.Parse(result);
        report.AssertCategory("MATCHED", "a.txt", "b.txt");
        foreach (var category in Report.Categories.Skip(1))
        {
            report.AssertCategory(category);
        }

        Assert.DoesNotContain("c.txt", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("d.txt", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("FATAL", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(target, E2EFixture.Snapshot(fixture.Target));
        Assert.Equal(zip, E2EFixture.Describe(fixture.ArchivePath));
    }

    // X05: 引数の不正は入力エラー。stderr に入力エラーと使い方、stdout は空。何も作成・変更しない。
    [Theory]
    [InlineData("target=")]
    [InlineData("no-archive")]
    [InlineData("unknown-option")]
    [InlineData("duplicate-option")]
    [InlineData("two-archives")]
    [InlineData("empty-target-value")]
    public void X05_ArgumentErrors(string kind)
    {
        var fixture = AllCategories();
        var all = E2EFixture.Snapshot(fixture.Directory);
        string[] args = kind switch
        {
            "target=" => [fixture.ArchivePath, "--target=" + fixture.Target],
            "no-archive" => ["--target", fixture.Target, "--dry-run"],
            "unknown-option" => [fixture.ArchivePath, "--target", fixture.Target, "--force"],
            "duplicate-option" => [fixture.ArchivePath, "--target", fixture.Target, "--dry-run", "--dry-run"],
            "two-archives" => [fixture.ArchivePath, fixture.ArchivePath, "--target", fixture.Target],
            "empty-target-value" => [fixture.ArchivePath, "--target", string.Empty],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var result = UnextractProcess.Run(fixture.Directory, null, args);

        Assert.True(result.ExitCode == Error, result.ToString());
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.StartsWith("入力エラー: ", result.ErrorLines[0], StringComparison.Ordinal);
        Assert.Contains("使い方: unextract <archive.zip> --target <dir> [--dry-run] [--fast] [--yes|-y]", result.ErrorLines);
        Assert.Equal(all, E2EFixture.Snapshot(fixture.Directory));
    }

    // X06: target が存在しない・ファイルである、ZIP が存在しない (拒否対象ではない通常の入力の誤り)。終了コード 1、stderr。
    // 存在しない target は作成しない。
    [Theory]
    [InlineData("target-missing")]
    [InlineData("target-is-file")]
    [InlineData("archive-missing")]
    public void X06_InputErrors(string kind)
    {
        var fixture = AllCategories();
        var all = E2EFixture.Snapshot(fixture.Directory);
        var missingTarget = Path.Combine(fixture.Directory, "no-such-target");
        var (archive, target) = kind switch
        {
            "target-missing" => (fixture.ArchivePath, missingTarget),
            "target-is-file" => (fixture.ArchivePath, Path.Combine(fixture.Target, "same1.txt")),
            "archive-missing" => (Path.Combine(fixture.Directory, "no-such.zip"), fixture.Target),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var result = UnextractProcess.Run(fixture.Directory, null, archive, "--target", target, "--yes");

        Assert.True(result.ExitCode == Error, result.ToString());
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(FatalNoDeletion, result.ErrorLines[^1]);
        Assert.StartsWith(kind == "archive-missing" ? "FATAL: " : "入力エラー: ", result.ErrorLines[0], StringComparison.Ordinal);
        Assert.False(Path.Exists(missingTarget));
        Assert.Equal(all, E2EFixture.Snapshot(fixture.Directory));
    }

    // X07: 削除候補0件 (全て MODIFIED・MISSING)、空 ZIP は、--yes なしでもプロンプトなしで成功する。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void X07_NoCandidatesSucceedsWithoutPrompt(bool emptyZip)
    {
        var fixture = E2EFixture.Create();
        if (emptyZip)
        {
            fixture.WriteZip(ZipFixture.Create());
            fixture.WriteTarget("unrelated.txt", "not in zip");
        }
        else
        {
            fixture.WriteZip(ZipFixture.Create(
                new FixtureEntry("changed.txt", E2EFixture.Bytes("hello")),
                new FixtureEntry("missing.txt", E2EFixture.Bytes("x"))));
            fixture.WriteTarget("changed.txt", "hellO");
        }

        var target = E2EFixture.Snapshot(fixture.Target);

        var result = fixture.Run(stdin: null);

        Assert.True(result.ExitCode == Success, result.ToString());
        Assert.Equal(NoCandidates, result.OutputLines[^1]);
        Assert.DoesNotContain("[y/N]", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("標準入力が対話的でなく", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains(
            emptyZip
                ? "合計: 0 エントリ (MATCHED 0、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0)"
                : "合計: 2 エントリ (MATCHED 0、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0)",
            result.OutputLines);
        Assert.Equal(target, E2EFixture.Snapshot(fixture.Target));
    }

    // X08: UTF-8 フラグ付きの日本語名。stdout (UTF-8 で読む) にそのまま出て、--yes で MATCHED の日本語名ファイルが削除される。
    [Fact]
    public void X08_JapaneseNamesWithUtf8Flag()
    {
        var fixture = E2EFixture.Create();
        var zip = ZipFixture.Create(
            new FixtureEntry("資料/報告書.txt", E2EFixture.Bytes("内容")),
            new FixtureEntry("資料/写真一覧.csv", E2EFixture.Bytes("a,b")),
            new FixtureEntry("ファイル名.txt", E2EFixture.Bytes("x")));
        var patcher = new ZipPatcher(zip);
        for (var i = 0; i < patcher.Count; i++)
        {
            Assert.True((patcher.GetFlags(i) & 0x800) != 0, $"fixture のエントリ {i} に UTF-8 フラグがない");
        }

        fixture.WriteZip(zip);
        fixture.WriteTarget("資料/報告書.txt", "内容")
            .WriteTarget("ファイル名.txt", "y")
            .WriteTarget("無関係.txt", "not in zip");

        var dryRun = fixture.Run(stdin: null, "--dry-run");

        Assert.True(dryRun.ExitCode == Success, dryRun.ToString());
        var report = Report.Parse(dryRun);
        report.AssertCategory("MATCHED", "資料/報告書.txt");
        report.AssertCategory("MODIFIED", "ファイル名.txt");
        report.AssertCategory("MISSING", "資料/写真一覧.csv");
        Assert.DoesNotContain('�', dryRun.StandardOutput);
        Assert.DoesNotContain("無関係", dryRun.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, dryRun.StandardError);

        var deleting = fixture.RunDeleting(stdin: null, "--yes");

        Assert.True(deleting.ExitCode == Success, deleting.ToString());
        Assert.Equal(Report.AnalysisPart(dryRun), Report.AnalysisPart(deleting));
        Assert.Equal("削除済み 1、DELETE_FAILED 0、未処理 0", deleting.OutputLines[^1]);
        Assert.False(File.Exists(Path.Combine(fixture.Target, "資料", "報告書.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Target, "ファイル名.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Target, "無関係.txt")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Target, "資料")));
    }

    // X09: UTF-8 フラグなしで CP437 の名前バイト (café░.txt = 63 61 66 82 B0 2E 74 78 74) を持つ ZIP は CP437 として照合される。
    // 単一ファイル exe でも CodePagesEncodingProvider が動くことを UNEXTRACT_E2E_EXE の実行で確かめる。
    [Fact]
    public void X09_Cp437NamesWithoutUtf8Flag()
    {
        const string name = "café░.txt";
        var fixture = E2EFixture.Create();
        var zip = ZipFixture.Create(
            [new FixtureEntry(name, E2EFixture.Bytes("c")), new FixtureEntry("plain.txt", E2EFixture.Bytes("p"))],
            ZipFixture.Cp437);
        var patcher = new ZipPatcher(zip);
        Assert.True((patcher.GetFlags(0) & 0x800) == 0, "fixture のエントリに UTF-8 フラグがある");
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0x82, 0xB0, 0x2E, 0x74, 0x78, 0x74 }, ZipFixture.Cp437.GetBytes(name));

        fixture.WriteZip(zip);
        fixture.WriteTarget(name, "c").WriteTarget("plain.txt", "p");

        var dryRun = fixture.Run(stdin: null, "--dry-run");

        Assert.True(dryRun.ExitCode == Success, dryRun.ToString());
        Report.Parse(dryRun).AssertCategory("MATCHED", name, "plain.txt");
        Assert.DoesNotContain('�', dryRun.StandardOutput);

        var deleting = fixture.RunDeleting(stdin: null, "-y");

        Assert.True(deleting.ExitCode == Success, deleting.ToString());
        Assert.Equal("削除済み 2、DELETE_FAILED 0、未処理 0", deleting.OutputLines[^1]);
        Assert.False(File.Exists(Path.Combine(fixture.Target, name)));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "plain.txt")));
    }

    // X10: 解析結果の一覧と削除の結果は stdout、FATAL・入力エラーは stderr。stderr がリダイレクトされているため進捗は出ない。
    [Fact]
    public void X10_StdoutAndStderrAreSeparated()
    {
        var normal = AllCategories(nameof(X10_StdoutAndStderrAreSeparated) + "-normal");
        var dryRun = normal.Run(stdin: null, "--dry-run");
        var deleting = normal.RunDeleting(stdin: null, "--yes");
        var fatal = CrcMismatch(nameof(X10_StdoutAndStderrAreSeparated) + "-fatal").Run(stdin: null, "--dry-run");
        var input = UnextractProcess.Run(normal.Directory, null, normal.ArchivePath, "--target");

        foreach (var result in new[] { dryRun, deleting, fatal, input })
        {
            Assert.DoesNotContain("Checking", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("Deleting", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
            // 進捗の1行上書き (改行を伴わない CR) がない。
            Assert.DoesNotMatch("\r(?!\n)", result.StandardError);
        }

        Assert.True(dryRun.ExitCode == Success && dryRun.StandardError.Length == 0, dryRun.ToString());
        Assert.Contains("MATCHED (2):", dryRun.OutputLines);

        Assert.True(deleting.ExitCode == Success && deleting.StandardError.Length == 0, deleting.ToString());
        Assert.Contains("削除済み 2、DELETE_FAILED 0、未処理 0", deleting.OutputLines);

        Assert.True(fatal.ExitCode == Error, fatal.ToString());
        Assert.StartsWith("FATAL: ", fatal.ErrorLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("FATAL", fatal.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("MATCHED (2):", fatal.OutputLines);
        Assert.DoesNotContain("MATCHED", fatal.StandardError, StringComparison.Ordinal);

        Assert.True(input.ExitCode == Error && input.StandardOutput.Length == 0, input.ToString());
        Assert.StartsWith("入力エラー: ", input.ErrorLines[0], StringComparison.Ordinal);
    }

    // X11: 同じ入力で --dry-run と --yes の解析結果の一覧 (stdout の解析部分) が一致する (SPEC §2)。
    [Fact]
    public void X11_DryRunAndYesShowTheSameAnalysis()
    {
        var fixture = AllCategories();

        var dryRun = fixture.Run(stdin: null, "--dry-run");
        var deleting = fixture.RunDeleting(stdin: null, "--yes");

        Assert.True(dryRun.ExitCode == Success, dryRun.ToString());
        Assert.True(deleting.ExitCode == Success, deleting.ToString());
        var analysis = Report.AnalysisPart(dryRun);
        Assert.Equal(analysis, Report.AnalysisPart(deleting));
        Assert.Equal(["--dry-run のため削除しません。"], dryRun.OutputLines[analysis.Count..]);
        Assert.Equal(["DELETE_FAILED (0):", "削除済み 2、DELETE_FAILED 0、未処理 0"], deleting.OutputLines[analysis.Count..]);
    }

    // Fast の警告 (PLAN.md §4 の「Fast モード」)。
    private const string FastWarning =
        "警告: --fast のため、パスとサイズだけで判定しています。内容が一致することと、ZIP から正常に展開できることは確認していません。";

    // X01 の fixture の --fast での一覧: SAME_SIZE に X01 の MATCHED と内容違い (同サイズ) の MODIFIED、MODIFIED にサイズ違いだけ。
    private static void AssertAllCategoriesFastReport(ProcessResult result)
    {
        Assert.Equal(FastWarning, result.OutputLines[0]);
        var report = Report.Parse(result);
        report.AssertCategory("SAME_SIZE", "same1.txt", "docs/deep.txt", "changed.txt");
        report.AssertCategory("MODIFIED", "longer.txt");
        report.AssertCategory("MISSING", "missing.txt");
        report.AssertCategory("SKIPPED_SPECIAL_FILE", "folder.txt");
        report.AssertCategory("DIRECTORY", "docs/");
        Assert.False(report.Has("MATCHED"));
        Assert.DoesNotContain("MATCHED", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "合計: 7 エントリ (SAME_SIZE 3、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1)",
            result.OutputLines);
        Assert.DoesNotContain("unrelated", result.StandardOutput, StringComparison.Ordinal);
    }

    // X13: X01 と同じ fixture で --fast --dry-run、続けて --fast --yes。target・ZIP は dry-run の前後で不変。stdout の先頭行が警告、
    // SAME_SIZE に X01 の MATCHED と内容違い (同サイズ) の MODIFIED、MODIFIED にサイズ違いだけ。MATCHED は出ない。stderr は空。
    // stdout の解析部分 (先頭の警告から合計行まで) が --fast --yes と一致する。X01 (Strict) の stdout には警告と SAME_SIZE が無い。
    [Fact]
    public void X13_FastDryRunShowsSameSizeAndWarning()
    {
        var fixture = AllCategories();
        var target = E2EFixture.Snapshot(fixture.Target);
        var zip = E2EFixture.Describe(fixture.ArchivePath);

        var strict = fixture.Run(stdin: null, "--dry-run");
        var dryRun = fixture.Run(stdin: null, "--fast", "--dry-run");

        Assert.True(dryRun.ExitCode == Success, dryRun.ToString());
        AssertAllCategoriesFastReport(dryRun);
        Assert.Equal("--dry-run のため削除しません。", dryRun.OutputLines[^1]);
        Assert.Equal(string.Empty, dryRun.StandardError);
        Assert.Equal(target, E2EFixture.Snapshot(fixture.Target));
        Assert.Equal(zip, E2EFixture.Describe(fixture.ArchivePath));

        Assert.True(strict.ExitCode == Success, strict.ToString());
        Assert.DoesNotContain(FastWarning, strict.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("SAME_SIZE", strict.StandardOutput, StringComparison.Ordinal);

        var deleting = fixture.RunDeleting(stdin: null, "--fast", "--yes");

        Assert.True(deleting.ExitCode == Success, deleting.ToString());
        Assert.Equal(Report.AnalysisPart(dryRun), Report.AnalysisPart(deleting));
        Assert.Equal(FastWarning, Report.AnalysisPart(dryRun)[0]);
    }

    // X14: X01 と同じ fixture で --fast --yes → SAME_SIZE のファイル (X01 の MATCHED と内容違いの MODIFIED) だけが削除され、サイズ違いの
    // MODIFIED・SKIPPED・ZIP にないファイル・ディレクトリ・ZIP (SHA-256 不変) は残る。[y/N] は出ない。
    // X04 と同じ fixture で --fast --yes → FATAL にならず、CRC を書き換えたエントリの target も SAME_SIZE として削除され、終了コード 0。
    [Fact]
    public void X14_FastYesDeletesSameSize()
    {
        var fixture = AllCategories();
        var before = E2EFixture.Snapshot(fixture.Target);
        var zip = E2EFixture.Describe(fixture.ArchivePath);

        var result = fixture.RunDeleting(stdin: null, "--fast", "--yes");

        Assert.True(result.ExitCode == Success, result.ToString());
        AssertAllCategoriesFastReport(result);
        Assert.DoesNotContain("[y/N]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Single(result.OutputLines, l => l == FastWarning);
        Assert.Equal("削除済み 3、DELETE_FAILED 0、未処理 0", result.OutputLines[^1]);
        Assert.Equal(string.Empty, result.StandardError);

        var expected = new SortedDictionary<string, string>(before, StringComparer.Ordinal);
        expected.Remove("same1.txt");
        expected.Remove(@"docs\deep.txt");
        expected.Remove("changed.txt");
        var after = E2EFixture.Snapshot(fixture.Target);
        Assert.Equal(expected.Keys, after.Keys);
        foreach (var (path, description) in expected.Where(e => !e.Value.StartsWith("dir ", StringComparison.Ordinal)))
        {
            Assert.Equal(description, after[path]);
        }

        Assert.True(Directory.Exists(Path.Combine(fixture.Target, "docs")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Target, "folder.txt")));
        Assert.Equal(zip, E2EFixture.Describe(fixture.ArchivePath));

        var crc = CrcMismatch(nameof(X14_FastYesDeletesSameSize) + "-crc");
        var crcZip = E2EFixture.Describe(crc.ArchivePath);

        var crcResult = crc.RunDeleting(stdin: null, "--fast", "--yes");

        Assert.True(crcResult.ExitCode == Success, crcResult.ToString());
        Assert.Equal(FastWarning, crcResult.OutputLines[0]);
        Report.Parse(crcResult).AssertCategory("SAME_SIZE", "a.txt", "b.txt", "bad.txt", "c.txt", "d.txt");
        Assert.Equal("削除済み 5、DELETE_FAILED 0、未処理 0", crcResult.OutputLines[^1]);
        Assert.Equal(string.Empty, crcResult.StandardError);
        Assert.False(File.Exists(Path.Combine(crc.Target, "bad.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(crc.Target));
        Assert.Equal(crcZip, E2EFixture.Describe(crc.ArchivePath));
    }

    // X15: --fast --fast → 入力エラー (stdout 空)。--fast --yes で ZIP 名不正 → 事前検証の FATAL (stdout の先頭行が警告で判定済み・未判定の件数、
    // stderr に FATAL と削除0件)。--fast で削除候補0件 (サイズ違いの MODIFIED と MISSING だけ)、--yes なし、stdin 空 → 終了コード 0、
    // stdout の先頭行が警告、「削除候補はありません。」、プロンプトなし。
    [Fact]
    public void X15_FastInputErrorFatalAndNoCandidates()
    {
        var duplicate = AllCategories(nameof(X15_FastInputErrorFatalAndNoCandidates) + "-duplicate");
        var all = E2EFixture.Snapshot(duplicate.Directory);

        var input = duplicate.Run(stdin: null, "--fast", "--fast");

        Assert.True(input.ExitCode == Error, input.ToString());
        Assert.Equal(string.Empty, input.StandardOutput);
        Assert.StartsWith("入力エラー: ", input.ErrorLines[0], StringComparison.Ordinal);
        Assert.Contains("使い方: unextract <archive.zip> --target <dir> [--dry-run] [--fast] [--yes|-y]", input.ErrorLines);
        Assert.DoesNotContain(FastWarning, input.StandardError, StringComparison.Ordinal);
        Assert.Equal(all, E2EFixture.Snapshot(duplicate.Directory));

        var invalid = E2EFixture.Create(nameof(X15_FastInputErrorFatalAndNoCandidates) + "-fatal");
        invalid.WriteZip(ZipFixture.Create(
            new FixtureEntry("ok.txt", E2EFixture.Bytes("hello")),
            new FixtureEntry("bad|name.txt", E2EFixture.Bytes("hello"))));
        invalid.WriteTarget("ok.txt", "hello");
        var invalidTarget = E2EFixture.Snapshot(invalid.Target);

        var fatal = invalid.RunDeleting(stdin: null, "--fast", "--yes");

        Assert.True(fatal.ExitCode == Error, fatal.ToString());
        Assert.Equal(FastWarning, fatal.OutputLines[0]);
        Assert.Equal("判定済み: 0 エントリ", fatal.OutputLines[1]);
        Assert.Equal("未判定: 1 エントリ", fatal.OutputLines[^1]);
        Assert.StartsWith("FATAL: エントリ #2 ", fatal.ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal(FatalNoDeletion, fatal.ErrorLines[^1]);
        Assert.DoesNotContain(FastWarning, fatal.StandardError, StringComparison.Ordinal);
        Assert.Equal(invalidTarget, E2EFixture.Snapshot(invalid.Target));

        var none = E2EFixture.Create(nameof(X15_FastInputErrorFatalAndNoCandidates) + "-none");
        none.WriteZip(ZipFixture.Create(
            new FixtureEntry("longer.txt", E2EFixture.Bytes("abc")),
            new FixtureEntry("missing.txt", E2EFixture.Bytes("x"))));
        none.WriteTarget("longer.txt", "abcdef");
        var noneTarget = E2EFixture.Snapshot(none.Target);

        var noCandidates = none.Run(stdin: null, "--fast");

        Assert.True(noCandidates.ExitCode == Success, noCandidates.ToString());
        Assert.Equal(FastWarning, noCandidates.OutputLines[0]);
        Assert.Equal(NoCandidates, noCandidates.OutputLines[^1]);
        Assert.Contains(
            "合計: 2 エントリ (SAME_SIZE 0、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0)",
            noCandidates.OutputLines);
        Assert.DoesNotContain("[y/N]", noCandidates.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("標準入力が対話的でなく", noCandidates.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, noCandidates.StandardError);
        Assert.Equal(noneTarget, E2EFixture.Snapshot(none.Target));
    }

    // X12: 終了コードの網羅 (成功 0 / エラー 1 / 中止 2)。各コードは X01〜X11 でも確認しているが、ここで1回ずつまとめて確かめる。
    [Fact]
    public void X12_ExitCodes()
    {
        var fixture = AllCategories();
        var target = E2EFixture.Snapshot(fixture.Target);

        Assert.Equal(Success, fixture.Run(stdin: null, "--dry-run").ExitCode);
        Assert.Equal(Error, fixture.Run(stdin: null, "--bogus").ExitCode);
        Assert.Equal(Cancelled, fixture.RunDeleting(stdin: null).ExitCode);
        Assert.Equal(target, E2EFixture.Snapshot(fixture.Target));
    }
}
