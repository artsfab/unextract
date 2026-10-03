using System.Runtime.CompilerServices;
using System.Text;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.E2E.Tests;

// E2E (X 系、docs/PLAN_TESTS.md §11)。ビルド済みの unextract.exe (または UNEXTRACT_E2E_EXE の exe) を別プロセスとして起動し、
// 終了コード・stdout・stderr・ファイルシステムの結果を検証する。stdin・stderr は常にリダイレクトされるため、exe からは
// 非対話で、進捗は表示されない。TTY が必要な項目 (対話の [y/N]) は PTY テスト (X28) と docs/MANUAL_TESTS.md の M 系で扱う。
// delete の実行の前には必ず E2EFixture の領域外ガードを通す (誤って削除に入った場合に備えて、中止の実行でも通す)。
public class E2ETests
{
    private const int Success = 0;
    private const int Error = 1;
    private const int Cancelled = 2;

    private const string AbortedNoDeletion = "中止しました。削除0件。";
    private const string PrepareAborted = "削除開始前に中止しました。削除0件。";
    private const string AnalyzeAborted = "解析を中止しました。analyze は削除を行いません (削除0件)。";
    private const string NotInteractive = "標準入力が対話的でなく --yes も無いため、確認できません。";
    private const string Heading = "Status                Entry -> Target";

    // PLAN.md §5.2 の指定を独立した期待値として保持する (製品定数は参照しない)。
    private const string Warning =
        "警告: --fast のため、パスとサイズだけで判定しています。内容が一致することと、ZIP から正常に展開できることは確認していません。";

    private static readonly string[] Usage =
    [
        "使い方: unextract analyze <archive.zip> --target <dir> [--fast]",
        "        unextract delete <archive.zip> --target <dir> [--fast] [--entries <file>] [--yes|-y]",
    ];

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

    private static string[] Header(E2EFixture fixture, bool fast = false) =>
    [
        .. fast ? new[] { Warning } : [],
        $"Archive: {fixture.ArchivePath}",
        $"Target:  {fixture.Target}",
        fast ? "Mode:    Fast" : "Mode:    Strict",
        "凡例: Target は target 内の対応する場所です。MISSING の場合は実在しない期待位置を示します。Target は確認用で、--entries には Entry を書きます。",
    ];

    // X16: 全カテゴリーと ZIP にない target ファイルを含む fixture で analyze → 終了 0。target 全体と ZIP が不変。stdout にヘッダー・凡例・
    // 全エントリの結果行・合計行。ZIP にないファイルは出力されない。stderr は空。
    [Fact]
    public void X16_AnalyzeChangesNothingAndListsAllEntries()
    {
        var fixture = AllCategories();
        var target = E2EFixture.Snapshot(fixture.Target);
        var zip = E2EFixture.Describe(fixture.ArchivePath);

        var result = fixture.Run("analyze", stdin: null);

        Assert.True(result.ExitCode == Success, result.ToString());
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(
            [
                .. Header(fixture),
                Heading,
                $@"MATCHED               same1.txt -> {fixture.Target}\same1.txt",
                $@"MATCHED               docs/deep.txt -> {fixture.Target}\docs\deep.txt",
                $@"MODIFIED              changed.txt -> {fixture.Target}\changed.txt",
                $@"MODIFIED              longer.txt -> {fixture.Target}\longer.txt",
                $@"MISSING               missing.txt -> {fixture.Target}\missing.txt",
                $@"SKIPPED_SPECIAL_FILE  folder.txt -> {fixture.Target}\folder.txt (ディレクトリ)",
                $@"DIRECTORY             docs/ -> {fixture.Target}\docs",
                "合計: 7 エントリ (MATCHED 2、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1)",
                "analyze は削除しません。削除は unextract delete で行います (delete は実行時の状態を改めて検証します)。",
            ],
            result.OutputLines);
        Assert.DoesNotContain("unrelated", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(target, E2EFixture.Snapshot(fixture.Target));
        Assert.Equal(zip, E2EFixture.Describe(fixture.ArchivePath));
    }

    // X17: X16 と同じ fixture で delete --yes → 終了 0。MATCHED だけが削除され、他は残る (ZIP の SHA-256 不変)。
    // stdout に結果行 (処理順) と「要約: 削除済み 2、...、処理対象外 0、未処理 0」。
    [Fact]
    public void X17_DeleteYesDeletesOnlyVerifiedFiles()
    {
        var fixture = AllCategories();
        var before = E2EFixture.Snapshot(fixture.Target);
        var zip = E2EFixture.Describe(fixture.ArchivePath);

        var result = fixture.RunDeleting(stdin: null, "--yes");

        Assert.True(result.ExitCode == Success, result.ToString());
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(
            [
                .. Header(fixture),
                "対象: 全 7 エントリ",
                Heading,
                $@"DELETED               same1.txt -> {fixture.Target}\same1.txt",
                $@"DELETED               docs/deep.txt -> {fixture.Target}\docs\deep.txt",
                $@"MODIFIED              changed.txt -> {fixture.Target}\changed.txt",
                $@"MODIFIED              longer.txt -> {fixture.Target}\longer.txt",
                $@"MISSING               missing.txt -> {fixture.Target}\missing.txt",
                $@"SKIPPED_SPECIAL_FILE  folder.txt -> {fixture.Target}\folder.txt (ディレクトリ)",
                "要約: 削除済み 2、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1、DELETE_FAILED 0、処理対象外 0、未処理 0",
            ],
            result.OutputLines);

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

    // X18: delete (--yes なし)。stdin は (1) 何も書かずに閉じる、(2) n、(3) y → いずれも非対話として中止。終了 2、削除0件。
    // stdout に非対話の文言と「中止しました。削除0件。」、[y/N] は表示されない。
    [Theory]
    [InlineData(null)]
    [InlineData("n\n")]
    [InlineData("y\n")]
    public void X18_NonInteractiveWithoutYesCancels(string? stdin)
    {
        var fixture = AllCategories();
        var target = E2EFixture.Snapshot(fixture.Target);
        var zip = E2EFixture.Describe(fixture.ArchivePath);

        var result = fixture.RunDeleting(stdin);

        Assert.True(result.ExitCode == Cancelled, result.ToString());
        Assert.Equal([.. Header(fixture), "対象: 全 7 エントリ", NotInteractive, AbortedNoDeletion], result.OutputLines);
        Assert.DoesNotContain("[y/N]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(target, E2EFixture.Snapshot(fixture.Target));
        Assert.Equal(zip, E2EFixture.Describe(fixture.ArchivePath));
    }

    // 先頭に MATCHED 2件、3番目に Central Directory の CRC-32 だけを書き換えた bad.txt (target にサイズ一致のファイルあり)、後方に MATCHED 2件。
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

    // X19: analyze → 終了 1、判定済み 2・FATAL #3・未判定 2 (stdout)、原因 (stderr)、target 不変。
    // delete --yes → 終了 1、1・2 番目は削除、3 番目は STOPPED で残る、4・5 番目は未処理で残る。stdout に部分削除の明示、stderr に停止の原因。
    [Fact]
    public void X19_StopAfterDeletionsKeepsEarlierDeletionsAndLaterFiles()
    {
        var fixture = CrcMismatch();
        var target = E2EFixture.Snapshot(fixture.Target);

        var analyze = fixture.Run("analyze", stdin: null);

        Assert.True(analyze.ExitCode == Error, analyze.ToString());
        Assert.Equal(
            [
                "判定済み: 2 エントリ (MATCHED 2、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0)",
                "FATAL: 1 エントリ (#3)",
                "未判定: 2 エントリ",
            ],
            analyze.OutputLines[^3..]);
        Report.Parse(analyze).AssertStatus("MATCHED", "a.txt", "b.txt");
        Assert.DoesNotContain("c.txt", analyze.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(["FATAL: エントリ #3 \"bad.txt\": エントリの CRC-32 が一致しません", AnalyzeAborted], analyze.ErrorLines);
        Assert.Equal(target, E2EFixture.Snapshot(fixture.Target));

        var delete = fixture.RunDeleting(stdin: null, "--yes");

        Assert.True(delete.ExitCode == Error, delete.ToString());
        var report = Report.Parse(delete);
        Assert.Equal(["DELETED", "DELETED", "STOPPED"], report.StatusesInOrder);
        Assert.Equal(["a.txt", "b.txt", "bad.txt"], report.AllEntries);
        Assert.Equal(
            [
                "要約: 削除済み 2、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0、DELETE_FAILED 0、処理対象外 0、未処理 2",
                "途中で停止しました。それまでに削除した 2 件は元に戻りません。bad.txt は削除していません。未処理の 2 件には触れていません。",
            ],
            delete.OutputLines[^2..]);
        Assert.Equal(
            [
                "停止: エントリ #3 \"bad.txt\": 全バイト比較で異常: エントリの CRC-32 が一致しません",
                "以後の処理を停止しました (削除済み 2、DELETE_FAILED 0、未処理 2)。",
            ],
            delete.ErrorLines);
        Assert.False(File.Exists(Path.Combine(fixture.Target, "a.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "b.txt")));
        foreach (var name in new[] { "bad.txt", "c.txt", "d.txt" })
        {
            Assert.True(File.Exists(Path.Combine(fixture.Target, name)), name);
        }
    }

    // X20: 引数の不正 (旧形式、--dry-run、サブコマンドなし・不明、--target=dir、ZIP なし・2つ、重複、--entries の値なし、analyze --yes)
    // → 終了 1。stderr に入力エラーと使い方 (旧形式・--dry-run は案内付き)。stdout は空。fixture 全体が不変。
    [Theory]
    [InlineData("old-form")]
    [InlineData("old-form-yes")]
    [InlineData("dry-run")]
    [InlineData("old-dry-run")]
    [InlineData("no-subcommand")]
    [InlineData("unknown-subcommand")]
    [InlineData("target-equals")]
    [InlineData("no-archive")]
    [InlineData("two-archives")]
    [InlineData("duplicate-target")]
    [InlineData("entries-no-value")]
    [InlineData("analyze-yes")]
    public void X20_ArgumentErrors(string kind)
    {
        var fixture = AllCategories();
        var before = E2EFixture.Snapshot(fixture.Directory);
        string[] args = kind switch
        {
            "old-form" => [fixture.ArchivePath, "--target", fixture.Target],
            "old-form-yes" => [fixture.ArchivePath, "--target", fixture.Target, "--yes"],
            "dry-run" => ["delete", fixture.ArchivePath, "--target", fixture.Target, "--dry-run"],
            "old-dry-run" => [fixture.ArchivePath, "--target", fixture.Target, "--dry-run"],
            "no-subcommand" => [],
            "unknown-subcommand" => ["remove", fixture.ArchivePath, "--target", fixture.Target],
            "target-equals" => ["delete", fixture.ArchivePath, $"--target={fixture.Target}", "--yes"],
            "no-archive" => ["delete", "--target", fixture.Target, "--yes"],
            "two-archives" => ["delete", fixture.ArchivePath, fixture.ArchivePath, "--target", fixture.Target, "--yes"],
            "duplicate-target" => ["delete", fixture.ArchivePath, "--target", fixture.Target, "--target", fixture.Target, "--yes"],
            "entries-no-value" => ["delete", fixture.ArchivePath, "--target", fixture.Target, "--yes", "--entries"],
            "analyze-yes" => ["analyze", fixture.ArchivePath, "--target", fixture.Target, "--yes"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        fixture.CheckGuard();

        var result = fixture.RunRaw(stdin: null, args);

        Assert.True(result.ExitCode == Error, result.ToString());
        Assert.Equal(string.Empty, result.StandardOutput);
        var lines = result.ErrorLines;
        Assert.StartsWith("入力エラー: ", lines[0], StringComparison.Ordinal);
        Assert.Equal(Usage, lines[1..]);
        if (kind is "old-form" or "old-form-yes" or "no-subcommand" or "unknown-subcommand")
        {
            Assert.Contains("旧形式 (unextract <archive.zip> --target <dir>) は廃止しました", lines[0], StringComparison.Ordinal);
        }

        if (kind is "dry-run" or "old-dry-run")
        {
            Assert.Equal("入力エラー: --dry-run は廃止しました。削除せずに結果を確認するには unextract analyze を使ってください。", lines[0]);
        }

        Assert.Equal(before, E2EFixture.Snapshot(fixture.Directory));
    }

    // X21: --entries の正常 (2件だけを指定して delete --yes) と入力エラー (最後の行が未知エントリ / UTF-16 で保存 / 空行)。
    // 正常: 指定の2件だけが処理され、指定外の MATCHED は残る。入力エラー: 終了 1、削除0件、stderr に行番号付きの入力エラー。
    [Theory]
    [InlineData("ok")]
    [InlineData("unknown-last-line")]
    [InlineData("utf16")]
    [InlineData("empty-line")]
    public void X21_Entries(string kind)
    {
        var fixture = AllCategories();
        var before = E2EFixture.Snapshot(fixture.Target);
        var entries = Path.Combine(fixture.Directory, "entries.txt");
        byte[] content = kind switch
        {
            "ok" => Encoding.UTF8.GetBytes("docs/deep.txt\r\nchanged.txt\r\n"),
            "unknown-last-line" => Encoding.UTF8.GetBytes("same1.txt\ndocs/deep.txt\nDOCS/deep.txt\n"),
            "utf16" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("same1.txt\r\n")],
            _ => Encoding.UTF8.GetBytes("same1.txt\n\ndocs/deep.txt\n"),
        };
        File.WriteAllBytes(entries, content);

        var result = fixture.RunDeleting(stdin: null, "--entries", entries, "--yes");

        if (kind == "ok")
        {
            Assert.True(result.ExitCode == Success, result.ToString());
            Assert.Contains("対象: 7 エントリ中 2 エントリ (--entries)", result.OutputLines);
            var report = Report.Parse(result);
            Assert.Equal(["docs/deep.txt", "changed.txt"], report.AllEntries);
            Assert.Equal(["DELETED", "MODIFIED"], report.StatusesInOrder);
            Assert.False(File.Exists(Path.Combine(fixture.Target, "docs", "deep.txt")));
            Assert.True(File.Exists(Path.Combine(fixture.Target, "same1.txt")));
            return;
        }

        Assert.True(result.ExitCode == Error, result.ToString());
        Assert.Equal(string.Empty, result.StandardOutput);
        var expected = kind switch
        {
            "unknown-last-line" =>
                "入力エラー: --entries の 3 行目: ZIP に一致するエントリがありません (\"DOCS/deep.txt\")。大小文字だけが違うエントリ \"docs/deep.txt\" があります",
            "utf16" => "入力エラー: --entries: UTF-16 で保存されています。UTF-8 で保存してください",
            _ => "入力エラー: --entries の 2 行目: 空行です",
        };
        Assert.Equal([expected, PrepareAborted], result.ErrorLines);
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
    }

    // X22: target が存在しない、target がファイル、ZIP が存在しない。delete --yes → 終了 1。stderr に入力エラー (ZIP が開けない場合は FATAL)
    // と削除0件の明示。stdout は空。存在しない target は作成されない。
    [Theory]
    [InlineData("missing-target")]
    [InlineData("file-target")]
    [InlineData("missing-archive")]
    public void X22_InputErrors(string kind)
    {
        var fixture = AllCategories();
        var before = E2EFixture.Snapshot(fixture.Directory);
        var target = kind switch
        {
            "missing-target" => Path.Combine(fixture.Directory, "no-such-target"),
            "file-target" => Path.Combine(fixture.Target, "same1.txt"),
            _ => fixture.Target,
        };
        var archive = kind == "missing-archive" ? Path.Combine(fixture.Directory, "missing.zip") : fixture.ArchivePath;
        fixture.CheckGuard();

        var result = fixture.RunRaw(stdin: null, "delete", archive, "--target", target, "--yes");

        Assert.True(result.ExitCode == Error, result.ToString());
        Assert.Equal(string.Empty, result.StandardOutput);
        var expected = kind switch
        {
            "missing-target" => "入力エラー: target が存在しません",
            "file-target" => "入力エラー: target がディレクトリではありません",
            _ => "FATAL: ZIP を開けません",
        };
        Assert.StartsWith(expected, result.ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal(PrepareAborted, result.ErrorLines[^1]);
        Assert.False(Directory.Exists(Path.Combine(fixture.Directory, "no-such-target")));
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Directory));
    }

    // X23: UTF-8 フラグ付きの日本語名の ZIP、UTF-8 フラグなしで CP437 の名前バイト (café░.txt) の ZIP で analyze → stdout の Entry をそのまま
    // entries に書いて delete --entries ... --yes → stdout (UTF-8) に名前がそのまま出て (U+FFFD なし)、entries が受理され、MATCHED のファイルだけが
    // 削除される。UNEXTRACT_E2E_EXE の単一ファイル exe でも同じ。
    [Theory]
    [InlineData("utf8")]
    [InlineData("cp437")]
    public void X23_DisplayedEntriesCanBeUsedAsEntries(string kind)
    {
        var fixture = E2EFixture.Create($"{nameof(X23_DisplayedEntriesCanBeUsedAsEntries)}-{kind}");
        if (kind == "utf8")
        {
            fixture.WriteZip(ZipFixture.Create(
                new FixtureEntry("日本語/ファイル.txt", E2EFixture.Bytes("こんにちは")),
                new FixtureEntry("日本語/変更.txt", E2EFixture.Bytes("abc")),
                new FixtureEntry("其の他.txt", E2EFixture.Bytes("keep"))));
            fixture.WriteTarget("日本語/ファイル.txt", "こんにちは").WriteTarget("日本語/変更.txt", "abd").WriteTarget("其の他.txt", "keep");
        }
        else
        {
            fixture.WriteZip(ZipFixture.Create([new FixtureEntry("café░.txt", E2EFixture.Bytes("x")), new FixtureEntry("other.txt", E2EFixture.Bytes("y"))], ZipFixture.Cp437));
            fixture.WriteTarget("café░.txt", "x").WriteTarget("other.txt", "y");
        }

        var analyze = fixture.Run("analyze", stdin: null);

        Assert.True(analyze.ExitCode == Success, analyze.ToString());
        Assert.DoesNotContain("�", analyze.StandardOutput, StringComparison.Ordinal);
        var report = Report.Parse(analyze);
        var matched = report.Entries("MATCHED");
        Assert.Equal(kind == "utf8" ? ["日本語/ファイル.txt", "其の他.txt"] : ["café░.txt", "other.txt"], matched);
        var selected = matched.Take(1).ToList();
        var entries = Path.Combine(fixture.Directory, "entries.txt");
        File.WriteAllText(entries, string.Join("\n", selected) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var delete = fixture.RunDeleting(stdin: null, "--entries", entries, "--yes");

        Assert.True(delete.ExitCode == Success, delete.ToString());
        Report.Parse(delete).AssertStatus("DELETED", [.. selected]);
        Assert.False(File.Exists(Path.Combine(fixture.Target, selected[0].Replace('/', '\\'))));
        Assert.True(File.Exists(Path.Combine(fixture.Target, matched[1].Replace('/', '\\'))));
    }

    // X24: analyze、delete --yes、STOP する delete、入力エラーの各実行 → 結果行・要約は stdout、FATAL・STOP の原因・入力エラーは stderr。
    // stderr がリダイレクトされているため進捗 (Checking、Processing、改行を伴わない CR) は出ない。
    [Fact]
    public void X24_StdoutAndStderrAreSeparatedWithoutProgress()
    {
        var all = AllCategories();
        var crc = CrcMismatch();
        ProcessResult[] results =
        [
            all.Run("analyze", stdin: null),
            all.RunDeleting(stdin: null, "--yes"),
            crc.RunDeleting(stdin: null, "--yes"),
            all.RunRaw(stdin: null, "delete", all.ArchivePath),
        ];

        Assert.Equal([Success, Success, Error, Error], results.Select(r => r.ExitCode));
        foreach (var result in results)
        {
            foreach (var text in new[] { result.StandardOutput, result.StandardError })
            {
                Assert.DoesNotContain("Checking", text, StringComparison.Ordinal);
                Assert.DoesNotContain("Processing", text, StringComparison.Ordinal);
                Assert.DoesNotContain('\r', text.Replace("\r\n", "\n", StringComparison.Ordinal));
            }
        }

        Assert.Equal(string.Empty, results[0].StandardError);
        Assert.Equal(string.Empty, results[1].StandardError);
        Assert.DoesNotContain("停止:", results[2].StandardOutput, StringComparison.Ordinal);
        Assert.StartsWith("停止: ", results[2].ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal(string.Empty, results[3].StandardOutput);
    }

    // X25: 削除対象0件 (全て MODIFIED・MISSING)、空 ZIP で delete --yes / delete (--yes なし、stdin 空) → --yes: 終了 0、削除0件、要約。
    // --yes なし: 非対話として中止 (終了 2)、削除0件 (案 A では削除対象0件でも確認が必要なため)。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void X25_NothingToDelete(bool emptyZip)
    {
        var fixture = E2EFixture.Create($"{nameof(X25_NothingToDelete)}-{(emptyZip ? "empty" : "modified")}");
        if (emptyZip)
        {
            fixture.WriteZip(ZipFixture.Create());
        }
        else
        {
            fixture.WriteZip(ZipFixture.Create(new FixtureEntry("changed.txt", E2EFixture.Bytes("hello")), new FixtureEntry("missing.txt", E2EFixture.Bytes("x"))));
            fixture.WriteTarget("changed.txt", "hello!");
        }

        var before = E2EFixture.Snapshot(fixture.Target);

        var yes = fixture.RunDeleting(stdin: null, "--yes");
        var cancelled = fixture.RunDeleting(stdin: null);

        Assert.True(yes.ExitCode == Success, yes.ToString());
        Assert.Equal(
            emptyZip
                ? "要約: 削除済み 0、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0、DELETE_FAILED 0、処理対象外 0、未処理 0"
                : "要約: 削除済み 0、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0、DELETE_FAILED 0、処理対象外 0、未処理 0",
            yes.OutputLines[^1]);
        Assert.True(cancelled.ExitCode == Cancelled, cancelled.ToString());
        Assert.Equal([NotInteractive, AbortedNoDeletion], cancelled.OutputLines[^2..]);
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
    }

    // X26: Fast: X16 の fixture で analyze --fast、delete --fast --yes。X19 の fixture で delete --fast --yes。
    // analyze --fast: 先頭行が警告、SAME_SIZE に MATCHED と内容違い、MODIFIED にサイズ違いだけ、MATCHED は出ない。
    // delete --fast --yes: 先頭行が警告、SAME_SIZE に相当するファイルだけを削除。X19 の fixture: STOP せず CRC を書き換えたエントリの target も削除、終了 0。
    [Fact]
    public void X26_Fast()
    {
        var fixture = AllCategories();
        var before = E2EFixture.Snapshot(fixture.Target);

        var analyze = fixture.Run("analyze", stdin: null, "--fast");

        Assert.True(analyze.ExitCode == Success, analyze.ToString());
        Assert.Equal(Header(fixture, fast: true), analyze.OutputLines[..5]);
        var report = Report.Parse(analyze);
        report.AssertStatus("SAME_SIZE", "same1.txt", "docs/deep.txt", "changed.txt");
        report.AssertStatus("MODIFIED", "longer.txt");
        Assert.False(report.Has("MATCHED"));
        Assert.Contains("合計: 7 エントリ (SAME_SIZE 3、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 1)", analyze.OutputLines);
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));

        var delete = fixture.RunDeleting(stdin: null, "--fast", "--yes");

        Assert.True(delete.ExitCode == Success, delete.ToString());
        Assert.Equal(Warning, delete.OutputLines[0]);
        Assert.Single(delete.OutputLines, l => l == Warning);
        Report.Parse(delete).AssertStatus("DELETED", "same1.txt", "docs/deep.txt", "changed.txt");
        Assert.True(File.Exists(Path.Combine(fixture.Target, "longer.txt")));

        var crc = CrcMismatch();
        var fast = crc.RunDeleting(stdin: null, "--fast", "--yes");

        Assert.True(fast.ExitCode == Success, fast.ToString());
        Report.Parse(fast).AssertStatus("DELETED", "a.txt", "b.txt", "bad.txt", "c.txt", "d.txt");
        Assert.Equal(string.Empty, fast.StandardError);
        Assert.Empty(Directory.EnumerateFiles(crc.Target));
        Assert.True(File.Exists(crc.ArchivePath));
    }

    // X27: 終了コード 0 / 1 / 2 (SPEC §2)。各コードは X16〜X26 でも確認しているが、ここで1回ずつまとめて確かめる。
    [Fact]
    public void X27_ExitCodes()
    {
        var fixture = AllCategories();

        Assert.Equal(Success, fixture.Run("analyze", stdin: null).ExitCode);
        Assert.Equal(Cancelled, fixture.RunDeleting(stdin: null).ExitCode);
        Assert.Equal(Error, fixture.RunRaw(stdin: null, fixture.ArchivePath, "--target", fixture.Target).ExitCode);
        Assert.Equal(Error, CrcMismatch().Run("analyze", stdin: null).ExitCode);
        Assert.Equal(Success, fixture.RunDeleting(stdin: null, "--yes").ExitCode);
    }
}
