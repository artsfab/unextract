using Unextract.Core.Analysis;
using Unextract.Core.Commands;
using Unextract.Core.Deletion;
using Unextract.Core.Display;
using Unextract.Core.Entries;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;
using static Unextract.Core.Tests.Fakes.PipelineHarness;

namespace Unextract.Core.Tests;

// analyze・delete の実行の流れ (docs/spec/cli.md#arguments、docs/SPEC.md#execution、docs/spec/cli.md#output) を Prepare と確認を含めて偽ファイルシステムで確認する
// (P・A・S02・S03・L・O13〜O15 の Core の部分)。
public class CommandTests
{
    private static readonly byte[] Hello = Bytes("hello");

    public static TheoryData<RunMode> BothModes => new() { RunMode.Strict, RunMode.Fast };

    // 全カテゴリー (MATCHED、MODIFIED (内容違い・サイズ違い)、MISSING、SKIPPED_SPECIAL_FILE、DIRECTORY) と ZIP にない target ファイル。
    private static CommandHarness AllCategories(RunMode mode)
    {
        var h = new CommandHarness(MakeZip(
            ("same.txt", Hello), ("changed.txt", Hello), ("size.txt", Hello), ("missing.txt", Hello), ("dir.txt", Hello), ("d/", null), ("d/ads.txt", Hello)))
        {
            Mode = mode,
        };
        h.File("same.txt");
        h.File("changed.txt", Bytes("hellO"));
        h.File("size.txt", Bytes("hello!"));
        h.Fs.AddDirectory(@"C:\target\dir.txt");
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\ads.txt").ExtraStreams.Add(new StreamEntry(":x:$DATA", 1));
        h.File("unrelated.txt");
        return h;
    }

    private static IReadOnlyList<string> Header(RunMode mode) => ReportText.Header(CommandHarness.ArchivePath, @"\\?\C:\target", mode);

    // A01: 全カテゴリーと ZIP にない target ファイル。終了 0。全エントリの結果行と合計行。ZIP にないファイルは出ない。target 不変。
    // 削除の能力を使う呼び出しが0回 (CommandHarness が確かめる)。全バイト比較は内容比較候補ごとに1回。
    // A02: 同じ fixture を --fast。SAME_SIZE (MATCHED と内容違い)、MODIFIED (サイズ違いのみ)。ZIP の Open()・target の読み取りが無い。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void A01_A02_AnalyzeIsNonDestructive(RunMode mode)
    {
        var h = AllCategories(mode);
        var before = Snapshot(h.Fs);

        var run = h.Analyze();

        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
        Assert.Empty(run.Error);
        Assert.Equal(before, Snapshot(h.Fs));
        Assert.Empty(h.Fs.Deleted);
        var lines = run.OutputLines;
        Assert.Equal(Header(mode), lines.Take(Header(mode).Count));
        Assert.Contains(ReportText.Heading, lines);
        Assert.DoesNotContain(lines, l => l.Contains("unrelated", StringComparison.Ordinal));
        Assert.Equal(AnalyzeOutput.Closing, lines[^1]);
        if (mode == RunMode.Fast)
        {
            Assert.Equal(ReportText.FastWarning, lines[0]);
            Assert.Equal("合計: 7 エントリ (SAME_SIZE 2、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 2、DIRECTORY 1)", lines[^2]);
            Assert.Empty(h.Contents!.Calls);
            Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Read ", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal("合計: 7 エントリ (MATCHED 1、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 2、DIRECTORY 1)", lines[^2]);

            // 内容比較候補 (same.txt、changed.txt) ごとに ZIP の Open() が1回。
            Assert.Equal(["Open 0", "Open 1"], h.Contents!.Calls.Where(c => c.StartsWith("Open ", StringComparison.Ordinal)));
        }
    }

    private static Dictionary<string, string> Snapshot(FakeFileSystem fs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(FakeNode node)
        {
            result[fs.FinalPathOf(node)] = $"{node.IsDirectory}|{Convert.ToHexString(node.Content)}|{node.Attributes}|{node.LastWriteTime}|{node.ChangeTime}";
            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(fs.Get(CommandHarness.TargetPath));
        Walk(fs.Get(CommandHarness.ArchivePath));
        return result;
    }

    // A03: 判定中の FATAL (100 件中 40 件目): CRC 不一致、比較対象の共有違反、列挙失敗 → 終了 1。判定済み 39 件、FATAL #40、未判定 60 件
    // (stdout)。原因と「analyze は削除を行いません (削除0件)」(stderr)。target 不変。
    [Theory]
    [InlineData("crc")]
    [InlineData("sharing")]
    [InlineData("enumeration")]
    public void A03_FatalDuringAnalyze(string kind)
    {
        var entries = Enumerable.Range(1, 100)
            .Select(i => (i == 40 ? "s/f040.txt" : $"f{i:D3}.txt", (byte[]?)Hello))
            .ToArray();
        var zip = MakeZip(entries);
        if (kind == "crc")
        {
            var patcher = new ZipPatcher(zip);
            zip = patcher.SetCrc32(39, patcher.GetCrc32(39) ^ 1).ToArray();
        }

        var h = new CommandHarness(zip);
        var dir = h.Fs.AddDirectory(@"C:\target\s");
        for (var i = 1; i <= 100; i++)
        {
            var node = h.File(i == 40 ? @"s\f040.txt" : $"f{i:D3}.txt");
            if (i == 40 && kind == "sharing")
            {
                node.Errors[FakeOp.OpenComparison] = 32;
            }
        }

        if (kind == "enumeration")
        {
            dir.Errors[FakeOp.Enumerate] = 1117;
        }

        var before = Snapshot(h.Fs);

        var run = h.Analyze();

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.Equal(1, ExitCodes.ToProcessExitCode(run.Outcome.Status));
        Assert.Equal(39, run.OutputLines.Count(l => l.StartsWith("MATCHED ", StringComparison.Ordinal)));
        Assert.Equal(
            ["判定済み: 39 エントリ (MATCHED 39、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0)", "FATAL: 1 エントリ (#40)", "未判定: 60 エントリ"],
            run.OutputLines.TakeLast(3));
        Assert.StartsWith("FATAL: エントリ #40 \"s/f040.txt\": ", run.ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal(AnalyzeOutput.FatalClosing, run.ErrorLines[1]);
        Assert.Equal(before, Snapshot(h.Fs));
    }

    // A04: 先頭に MATCHED が複数、後方に ZIP 名不正 (Prepare の FATAL) → 終了 1。target のエントリに触れない。判定済み 0、未判定 N。
    // O11: 結果表示に至る Fast の analyze では FATAL でもヘッダーの警告を出す。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void A04_PrepareFatal_DoesNotTouchTarget(RunMode mode)
    {
        var h = new CommandHarness(ZipFixture.Create(new FixtureEntry("a.txt", Hello), new FixtureEntry("b.txt", Hello), new FixtureEntry("bad|name.txt", Hello)))
        {
            Mode = mode,
        };
        h.File("a.txt");
        h.File("b.txt");

        var run = h.Analyze();

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.False(h.TouchedTargetEntries());
        Assert.Equal([.. Header(mode), "判定済み: 0 エントリ", "未判定: 3 エントリ"], run.OutputLines);
        Assert.Equal(["FATAL: エントリ #3 \"bad|name.txt\": Windows で使えない文字を含みます", AnalyzeOutput.FatalClosing], run.ErrorLines);
    }

    // P06 (Core): target の入力エラー (最終成分が reparse、存在しない) では ZIP 事前検証にも分類にも進まない。結果表示に至らないため
    // Fast の警告も出ない (O11)。
    [Theory]
    [InlineData("reparse", RunMode.Strict)]
    [InlineData("missing", RunMode.Strict)]
    [InlineData("reparse", RunMode.Fast)]
    [InlineData("missing", RunMode.Fast)]
    public void P06_TargetInputError_StopsBeforeAnalysis(string kind, RunMode mode)
    {
        var fs = new FakeFileSystem();
        if (kind == "reparse")
        {
            fs.AddJunction(CommandHarness.TargetPath, fs.AddDirectory(@"C:\real"));
        }

        var h = new CommandHarness(MakeZip(("a.txt", Hello)), fs, createTarget: false) { Mode = mode };

        var analyze = h.Analyze();
        var delete = h.Delete();

        Assert.Equal(ExitStatus.Error, analyze.Outcome.Status);
        Assert.Null(analyze.Outcome.Analysis);
        Assert.Empty(analyze.Output);
        Assert.StartsWith("入力エラー: target ", analyze.ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal(AnalyzeOutput.FatalClosing, analyze.ErrorLines[1]);

        Assert.Equal(ExitStatus.Error, delete.Outcome.Status);
        Assert.Empty(delete.Output);
        Assert.Equal(DeleteOutput.PrepareAborted, delete.ErrorLines[^1]);
        Assert.False(h.TouchedTargetEntries());
        Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("GetFileIdentity", StringComparison.Ordinal));
        Assert.Null(h.Fs.Find(@"C:\target\a.txt"));
    }

    // P09: ZIP 自身の個体の取得失敗 → analyze・delete とも FATAL、削除0件、target のエントリに触れない。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void P09_ArchiveIdentityFailure_IsFatal(RunMode mode)
    {
        var h = new CommandHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello))) { Mode = mode };
        h.File("a.txt");
        h.Fs.Get(CommandHarness.ArchivePath).Errors[FakeOp.GetFileIdentity] = 5;

        var analyze = h.Analyze();
        var delete = h.Delete();

        Assert.Equal(FatalKind.ArchiveIdentityFailed, analyze.Outcome.Analysis!.Fatal!.Kind);
        Assert.Equal(2, analyze.Outcome.Analysis.UnclassifiedCount);
        Assert.Equal(["判定済み: 0 エントリ", "未判定: 2 エントリ"], analyze.OutputLines.TakeLast(2));
        Assert.Equal(ExitStatus.Error, delete.Outcome.Status);
        Assert.StartsWith("FATAL: ZIP 自身の File ID を取得できません", delete.ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal(DeleteOutput.PrepareAborted, delete.ErrorLines[1]);
        Assert.False(h.TouchedTargetEntries());
        Assert.True(h.Exists("a.txt"));
    }

    // P02・Z 系・R01〜R06 のモード違いの再利用: 同じ種類の fixture を analyze と delete --yes で Strict と --fast の両方で実行し、
    // 事前検証の結果 (FATAL の種類・原因エントリ、または受理) と標準エラー出力が両モード・両操作で同じこと、FATAL では target のエントリに
    // 触れず削除0件であることを確かめる。受理される fixture では、削除対象の分類が Strict は MATCHED、Fast は SAME_SIZE になる。
    public static TheoryData<string> PrevalidationReuseIds =>
    [
        "Z01", "Z02", "Z02-O04", "Z03-case", "Z03-file-parent", "Z04", "Z04a", "Z04b", "Z05", "Z06", "Z07",
        "Z08-zip64", "Z08-data-descriptor", "Z08-sfx", "R01-allowed", "R01-exceeded", "R02-name", "R02-depth", "R03", "R04",
        "R05", "R06",
    ];

    [Theory]
    [MemberData(nameof(PrevalidationReuseIds))]
    public void P02_ModeReuse_PrevalidationIsTheSameInBothModesAndOperations(string id)
    {
        var errors = new List<string>();
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            var (zip, arrange, limits, expected, index) = PrevalidationFixture(id);

            var analyzeHarness = new CommandHarness(zip) { Mode = mode, Limits = limits ?? Limits.Default };
            arrange(analyzeHarness.Fs);
            var analyze = analyzeHarness.Analyze();

            var deleteHarness = new CommandHarness(zip) { Mode = mode, Limits = limits ?? Limits.Default };
            arrange(deleteHarness.Fs);
            var prompt = new ScriptedPrompt(true, "y");
            var delete = deleteHarness.Delete(prompt: prompt);

            var analysis = analyze.Outcome.Analysis!;
            Assert.Equal(mode == RunMode.Fast, analyze.OutputLines[0] == ReportText.FastWarning);
            if (expected is { } kind)
            {
                Assert.Equal(kind, analysis.Fatal?.Kind);
                Assert.Equal(index, analysis.Fatal!.Entry!.Index);
                Assert.Equal(ExitStatus.Error, analyze.Outcome.Status);
                Assert.Equal(ExitStatus.Error, delete.Outcome.Status);
                Assert.Null(delete.Outcome.Report);
                Assert.Empty(delete.Output);
                Assert.Empty(prompt.Asked);
                Assert.False(analyzeHarness.TouchedTargetEntries());
                Assert.False(deleteHarness.TouchedTargetEntries());
                Assert.Empty(deleteHarness.Fs.Deleted);
                Assert.Equal(analyze.ErrorLines[0], delete.ErrorLines[0]);
                Assert.Equal(DeleteOutput.PrepareAborted, delete.ErrorLines[1]);
                errors.Add(analyze.Error);
            }
            else
            {
                Assert.Null(analysis.Fatal);
                var candidates = analysis.Results.Where(r => r.Classification is Classification.Matched or Classification.SameSize).ToList();
                Assert.NotEmpty(candidates);
                Assert.All(candidates, c => Assert.Equal(Candidate(mode), c.Classification));
                Assert.Equal(ExitStatus.Success, delete.Outcome.Status);
                Assert.Equal(
                    candidates.Select(c => c.Entry.Name),
                    delete.Outcome.Report!.Results.Where(r => r.Status == DeleteStatus.Deleted).Select(r => r.Entry.Name));
            }

            if (id == "Z02-O04")
            {
                // O04: 表示できない名前 (制御文字) は FATAL の原因としてエスケープ表記とエントリ番号で表示する (両モード)。
                Assert.Contains("エントリ #2 \"a\\u{0001}.txt\"", analyze.Error, StringComparison.Ordinal);
            }
        }

        Assert.True(errors.Count is 0 or 2);
        Assert.Equal(errors.FirstOrDefault(), errors.LastOrDefault());
    }

    private static (byte[] Zip, Action<FakeFileSystem> Arrange, Limits? Limits, FatalKind? Expected, int Index) PrevalidationFixture(string id)
    {
        const long GiB = 1L << 30;
        Action<FakeFileSystem> none = _ => { };
        static byte[] Zip(params FixtureEntry[] entries) => ZipFixture.Create(entries);
        static Action<FakeFileSystem> Files(params string[] names) => fs =>
        {
            foreach (var name in names)
            {
                var parts = name.Split('\\');
                for (var i = 1; i < parts.Length; i++)
                {
                    var dir = @"C:\target\" + string.Join('\\', parts[..i]);
                    if (fs.Find(dir) is null)
                    {
                        fs.AddDirectory(dir);
                    }
                }

                fs.AddFile(@"C:\target\" + name, Bytes("hello"));
            }
        };

        var ok = new FixtureEntry("ok.txt", Hello);
        switch (id)
        {
            case "Z01":
                return (Zip(ok, new FixtureEntry("../evil.txt", Hello)), none, null, FatalKind.DotDotComponent, 1);
            case "Z02":
                return (Zip(ok, new FixtureEntry("CON.txt", Hello)), none, null, FatalKind.ReservedName, 1);
            case "Z02-O04":
                return (Zip(ok, new FixtureEntry("a\u0001.txt", Hello)), none, null, FatalKind.ControlCharacter, 1);
            case "Z03-case":
                return (Zip(new FixtureEntry("a.txt", Hello), new FixtureEntry("A.txt", Hello)), none, null, FatalKind.CaseInsensitiveCollision, 1);
            case "Z03-file-parent":
                return (Zip(new FixtureEntry("a", Hello), new FixtureEntry("a/b.txt", Hello)), none, null, FatalKind.FileUsedAsParent, 1);
            case "Z04":
                return (Zip(ok, new FixtureEntry("link", Bytes("ok.txt"), unchecked((int)0xA1FF0000))), none, null, FatalKind.UnsupportedEntryType, 1);
            case "Z04a":
                return (Zip(ok, new FixtureEntry("a.txt", Hello, FakeEntries.Attributes(0x4000))), none, null, FatalKind.FileEntryWithDirectoryType, 1);
            case "Z04b":
                return (Zip(ok, new FixtureEntry("d/", null, FakeEntries.Attributes(0x8000))), none, null, FatalKind.DirectoryEntryWithFileType, 1);
            case "Z05":
                return (
                    Zip(
                        new FixtureEntry("a.txt", Hello, FakeEntries.Attributes(0x8000, 0x1 | 0x2 | 0x4 | 0x20)),
                        new FixtureEntry("b.txt", Hello),
                        new FixtureEntry("d/", null, FakeEntries.Attributes(0x4000))),
                    Files("a.txt", "b.txt"), null, null, 0);
            case "Z06":
                return (ZipFixture.Create([new FixtureEntry("caf\u00e9\u2591.txt", Hello)], ZipFixture.Cp437), Files("caf\u00e9\u2591.txt"), null, null, 0);
            case "Z07":
                return (
                    new ZipPatcher(Zip(ok, new FixtureEntry("x.txt", Hello))).SetName(1, [0x61, 0xFF, 0xFE, 0x2E, 0x74], utf8Flag: true).ToArray(),
                    none, null, FatalKind.NameContainsReplacementCharacter, 1);
            case "Z08-zip64":
                return (new ZipPatcher(Zip(new FixtureEntry("d/"), new FixtureEntry("d/a.txt", Hello))).ForceZip64(1).ToArray(), Files(@"d\a.txt"), null, null, 0);
            case "Z08-data-descriptor":
                return (ZipFixture.Create([new FixtureEntry("d/"), new FixtureEntry("d/a.txt", Hello)], nonSeekable: true), Files(@"d\a.txt"), null, null, 0);
            case "Z08-sfx":
                return (
                    new ZipPatcher(Zip(new FixtureEntry("d/"), new FixtureEntry("d/a.txt", Hello))).Prepend(new byte[4096], adjustOffsets: true).ToArray(),
                    Files(@"d\a.txt"), null, null, 0);
            case "R01-allowed":
                return (Zip(ok, new FixtureEntry("b.txt", Hello)), Files("ok.txt"), Limits.Default with { MaxEntries = 2 }, null, 0);
            case "R01-exceeded":
                return (Zip(ok, new FixtureEntry("b.txt", Hello), new FixtureEntry("c.txt", Hello)), none, Limits.Default with { MaxEntries = 2 }, FatalKind.TooManyEntries, 2);
            case "R02-name":
                return (Zip(ok, new FixtureEntry("abcdefg.txt", Hello)), none, Limits.Default with { MaxNameLength = 10 }, FatalKind.NameTooLong, 1);
            case "R02-depth":
                return (Zip(ok, new FixtureEntry("a/b/c.txt", Hello)), none, Limits.Default with { MaxDepth = 2 }, FatalKind.PathTooDeep, 1);
            case "R03":
                // ok.txt: 6 × 2 + 128 = 140、b.txt: 5 × 2 + 128 = 138。合計 278 に対して上限 277。
                return (Zip(ok, new FixtureEntry("b.txt", Hello)), none, Limits.Default with { MaxMetadataBytes = 277 }, FatalKind.MetadataTooLarge, 1);
            case "R04":
                return (
                    Zip(ok, new FixtureEntry("b.txt", Hello), new FixtureEntry("c.txt", Hello)), none,
                    Limits.Default with { MaxEntries = 100, MaxMetadataBytes = 278 }, FatalKind.MetadataTooLarge, 2);
            case "R05":
                return (new ZipPatcher(Zip(ok, new FixtureEntry("big.bin", Hello))).SetDeclaredLength(1, (16 * GiB) + 1).ToArray(), none, null, FatalKind.EntryTooLarge, 1);
            case "R06":
                var patcher = new ZipPatcher(Zip(Enumerable.Range(0, 5).Select(i => new FixtureEntry($"f{i}.bin", Hello)).ToArray()));
                for (var i = 0; i < 4; i++)
                {
                    patcher.SetDeclaredLength(i, 16 * GiB);
                }

                patcher.SetDeclaredLength(4, 1);
                return (patcher.ToArray(), none, null, FatalKind.TotalDeclaredLengthTooLarge, 4);
            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, null);
        }
    }

    // P05: 空 ZIP、空 target、削除対象0件 → analyze 終了 0。delete --yes 終了 0、削除0件。delete (確認あり) では確認プロンプトが表示される (案 A)。
    [Theory]
    [InlineData(true, RunMode.Strict)]
    [InlineData(false, RunMode.Strict)]
    [InlineData(true, RunMode.Fast)]
    [InlineData(false, RunMode.Fast)]
    public void P05_NothingToDelete(bool emptyZip, RunMode mode)
    {
        var zip = emptyZip ? ZipFixture.Create() : MakeZip(("missing.txt", Hello), ("changed.txt", Hello));
        var h = new CommandHarness(zip) { Mode = mode };
        h.File("changed.txt", Bytes("hello!"));

        Assert.Equal(ExitStatus.Success, h.Analyze().Outcome.Status);

        var yes = h.Delete(yes: true);
        Assert.Equal(ExitStatus.Success, yes.Outcome.Status);
        Assert.Empty(h.Fs.Deleted);
        Assert.Contains(
            emptyZip
                ? "要約: 削除済み 0、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0、DELETE_FAILED 0、処理対象外 0、未処理 0"
                : "要約: 削除済み 0、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0、DELETE_FAILED 0、処理対象外 0、未処理 0",
            yes.OutputLines);

        var prompt = new ScriptedPrompt(true, "n");
        var asked = h.Delete(yes: false, prompt: prompt);
        Assert.Equal(ExitStatus.UserCancelled, asked.Outcome.Status);
        var text = Assert.Single(prompt.Asked);
        Assert.Contains($"最大 {(emptyZip ? 0 : 2)} 件のファイルエントリ", text, StringComparison.Ordinal);
    }

    // P10: 確認で n・空入力・EOF・その他、非対話で --yes なし → 終了 2、削除0件。target のエントリに触れない。中止の文言。
    // S02: y / Y の確認、--yes で逐次処理を開始する。
    [Theory]
    [InlineData(true, "y", false, true)]
    [InlineData(true, "Y", false, true)]
    [InlineData(true, "", false, false)]
    [InlineData(true, null, false, false)]
    [InlineData(true, "n", false, false)]
    [InlineData(true, "yes", false, false)]
    [InlineData(true, " y", false, false)]
    [InlineData(false, "y", false, false)]
    [InlineData(false, null, true, true)]
    [InlineData(true, null, true, true)]
    public void P10_S02_Confirmation(bool interactive, string? answer, bool yes, bool deletes)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            var h = new CommandHarness(MakeZip(("same.txt", Hello))) { Mode = mode };
            h.File("same.txt");
            var prompt = new ScriptedPrompt(interactive, answer);
            var start = h.Fs.Calls.Count;

            var run = h.Delete(yes: yes, prompt: prompt);

            Assert.Equal(!deletes, h.Exists("same.txt"));
            Assert.Equal(interactive && !yes ? 1 : 0, prompt.Asked.Count);
            if (deletes)
            {
                Assert.Equal(ExitStatus.Success, run.Outcome.Status);
                continue;
            }

            Assert.Equal(ExitStatus.UserCancelled, run.Outcome.Status);
            Assert.Equal(2, ExitCodes.ToProcessExitCode(run.Outcome.Status));
            Assert.Null(run.Outcome.Report);
            Assert.False(h.TouchedTargetEntries(start));
            Assert.Equal(DeleteOutput.Cancelled, run.OutputLines[^1]);
            Assert.Equal(!interactive, run.Output.Contains(DeleteOutput.NotInteractive, StringComparison.Ordinal));
            Assert.Empty(run.Error);
        }
    }

    // S02・S03: 確認は Prepare の全段階の後、最初の target エントリの処理 (target ルート以外の列挙・削除用オープン) の前に1回だけ。
    // 確認待ちの間に開いているのは target ルートだけ (偽 FS のハンドル。ZIP は別に保持)。確認の後に Prepare の検査は行わない。
    [Fact]
    public void S02_S03_ConfirmationTiming()
    {
        var h = new CommandHarness(MakeZip(("d/a.txt", Hello), ("b.txt", Hello)));
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\a.txt");
        h.File("b.txt");
        var atConfirmation = -1;
        var openHandles = -1;

        var run = h.Delete(yes: false, prompt: new ScriptedPrompt(true, "y"), awaitingConfirmation: () =>
        {
            atConfirmation = h.Fs.Calls.Count;
            openHandles = h.Fs.OpenHandleCount;
        });

        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
        Assert.Equal(1, openHandles);
        var before = h.Fs.Calls.Take(atConfirmation).ToList();
        Assert.Contains(before, c => c.StartsWith("OpenTargetRoot ", StringComparison.Ordinal));
        Assert.Contains(before, c => c.StartsWith("GetFileIdentity ", StringComparison.Ordinal));
        Assert.DoesNotContain(before, c =>
            c.StartsWith("Enumerate ", StringComparison.Ordinal)
            || c.StartsWith("OpenEnumeration ", StringComparison.Ordinal)
            || c.StartsWith("OpenDeletion ", StringComparison.Ordinal)
            || c.StartsWith("OpenComparison ", StringComparison.Ordinal));
        var after = h.Fs.Calls.Skip(atConfirmation).ToList();
        Assert.DoesNotContain(after, c => c.StartsWith("ConfirmTarget", StringComparison.Ordinal) || c.StartsWith("OpenTargetRoot", StringComparison.Ordinal)
            || c.StartsWith("GetFileIdentity", StringComparison.Ordinal));
        Assert.Contains(after, c => c.StartsWith("Enumerate ", StringComparison.Ordinal));
    }

    // P11: delete で Prepare の各段階 (ZIP、entries、拒否位置、target ルート、ZIP 全体検査、ZIP 自身の個体、entries の照合) を1つずつ
    // 失敗させる → いずれも削除0件、確認プロンプトなし、列挙・削除用オープン0回、「削除開始前に中止しました。削除0件。」。
    // (引数の段階は CLI のテスト。)
    [Theory]
    [InlineData("archive", "FATAL: ZIP として読み取れません")]
    [InlineData("entries-missing", "入力エラー: --entries: ファイルを読めません")]
    [InlineData("entries-format", "入力エラー: --entries の 2 行目: 空行です")]
    [InlineData("protected", "入力エラー: 拒否対象のフォルダー")]
    [InlineData("target", "入力エラー: target が存在しません")]
    [InlineData("zip-validation", "FATAL: エントリ #3 \"CON\": Windows の予約名を含みます")]
    [InlineData("identity", "FATAL: ZIP 自身の File ID を取得できません")]
    [InlineData("entries-match", "入力エラー: --entries の 2 行目: ZIP に一致するエントリがありません (\"nothing.txt\")")]
    public void P11_EachPrepareStage_DeletesNothing(string stage, string message)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            var names = stage == "zip-validation" ? new[] { "a.txt", "b.txt", "CON" } : new[] { "a.txt", "b.txt" };
            var fs = new FakeFileSystem();
            if (stage != "target")
            {
                fs.AddDirectory(CommandHarness.TargetPath);
            }

            var h = new CommandHarness(ZipFixture.Create(names.Select(n => new FixtureEntry(n, Hello)).ToArray()), fs, createTarget: stage != "target") { Mode = mode };
            if (stage != "target")
            {
                h.File("a.txt");
                h.File("b.txt");
            }

            string? entries = null;
            switch (stage)
            {
                case "archive":
                    h.OpenArchive = _ => ZipArchiveSource.Open(new MemoryStream(Bytes("not a zip")));
                    break;
                case "entries-missing":
                    entries = Path.Combine(TestFiles.NewDirectory(), $"missing-{Guid.NewGuid():N}.txt");
                    break;
                case "entries-format":
                    entries = CommandHarness.WriteEntries("a.txt\n\nb.txt\n");
                    break;
                case "protected":
                    h.Locations = new TargetLocationPolicyResult(null, new FatalError(FatalKind.ProtectedLocationUnresolved, Detail: "Windows"));
                    break;
                case "identity":
                    h.Fs.Get(CommandHarness.ArchivePath).Errors[FakeOp.GetFileIdentity] = 5;
                    break;
                case "entries-match":
                    entries = CommandHarness.WriteEntries("a.txt\nnothing.txt\n");
                    break;
            }

            var prompt = new ScriptedPrompt(true, "y");
            var run = h.Delete(entriesPath: entries, yes: false, prompt: prompt);

            var failure = Assert.IsType<PrepareFailure>(run.Outcome.PreparationFailure);
            var expectedStage = stage switch
            {
                "archive" => PrepareStage.Archive,
                "entries-missing" or "entries-format" => PrepareStage.Entries,
                "protected" => PrepareStage.ProtectedLocations,
                "target" => PrepareStage.TargetRoot,
                "zip-validation" => PrepareStage.ZipValidation,
                "identity" => PrepareStage.ArchiveIdentity,
                "entries-match" => PrepareStage.EntriesMatch,
                _ => throw new ArgumentOutOfRangeException(nameof(stage)),
            };
            Assert.Equal(expectedStage, failure.Stage);
            Assert.Equal(stage is "archive" or "zip-validation" or "identity", failure.IsFatal);
            Assert.Same(run.Outcome.PrepareError, failure.Fatal);
            Assert.Equal(run.ErrorLines[0], failure.Message);
            Assert.Equal(stage switch { "zip-validation" => 3, "identity" or "entries-match" => 2, _ => (int?)null }, failure.TotalEntries);
            if (stage is "entries-missing" or "entries-format" or "entries-match")
            {
                var entriesError = Assert.IsType<EntriesError>(failure.EntriesError);
                Assert.Equal(stage switch { "entries-missing" => EntriesErrorKind.Unreadable, "entries-format" => EntriesErrorKind.EmptyLine, _ => EntriesErrorKind.NoMatch }, entriesError.Kind);
                Assert.Equal(stage == "entries-missing" ? (int?)null : 2, entriesError.LineNumber);
                Assert.Null(failure.Fatal);
            }
            else
            {
                Assert.Null(failure.EntriesError);
            }

            Assert.Equal(ExitStatus.Error, run.Outcome.Status);
            Assert.Null(run.Outcome.Report);
            Assert.Empty(prompt.Asked);
            Assert.Empty(run.Output);
            Assert.StartsWith(message, run.ErrorLines[0], StringComparison.Ordinal);
            Assert.Equal([DeleteOutput.PrepareAborted], run.ErrorLines.Skip(1));
            Assert.False(h.TouchedTargetEntries());
            Assert.Empty(h.Fs.Deleted);
        }
    }

    // L10: entries の誤りがファイルの最後の行にあり、先頭の行は全て MATCHED → 入力エラー、削除0件 (途中まで削除しない)。
    // L13: 指定外のエントリに ZIP 名不正・宣言合計の超過がある → FATAL、削除0件 (ZIP 全体の事前検査は --entries に関係なく行う)。
    // L17: entries ファイルがディレクトリ → 入力エラー、削除0件。
    [Theory]
    [InlineData("last-line")]
    [InlineData("unselected-bad-name")]
    [InlineData("unselected-total")]
    [InlineData("directory")]
    public void L10_L13_L17_EntriesErrors_DeleteNothing(string kind)
    {
        const long GiB = 1L << 30;
        var zip = MakeZip(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello));
        string entries;
        switch (kind)
        {
            case "unselected-bad-name":
                zip = ZipFixture.Create(new FixtureEntry("a.txt", Hello), new FixtureEntry("b.txt", Hello), new FixtureEntry("x<y.txt", Hello));
                entries = CommandHarness.WriteEntries("a.txt\nb.txt\n");
                break;
            case "unselected-total":
                var patcher = new ZipPatcher(MakeZip(("a.txt", Hello), ("b.txt", Hello), ("big1.bin", Hello), ("big2.bin", Hello), ("big3.bin", Hello), ("big4.bin", Hello)));
                for (var i = 2; i < 6; i++)
                {
                    patcher.SetDeclaredLength(i, 16 * GiB);
                }

                zip = patcher.ToArray();
                entries = CommandHarness.WriteEntries("a.txt\nb.txt\n");
                break;
            case "directory":
                entries = TestFiles.NewDirectory();
                break;
            default:
                entries = CommandHarness.WriteEntries("a.txt\nb.txt\nC.txt\n");
                break;
        }

        var h = new CommandHarness(zip);
        h.File("a.txt");
        h.File("b.txt");
        h.File("c.txt");

        var run = h.Delete(entriesPath: entries);

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.False(h.TouchedTargetEntries());
        Assert.Empty(h.Fs.Deleted);
        Assert.Equal(DeleteOutput.PrepareAborted, run.ErrorLines[^1]);
        var expected = kind switch
        {
            "last-line" => "入力エラー: --entries の 3 行目: ZIP に一致するエントリがありません (\"C.txt\")。大小文字だけが違うエントリ \"c.txt\" があります",
            "unselected-bad-name" => "FATAL: エントリ #3 \"x<y.txt\": Windows で使えない文字を含みます",
            "unselected-total" => "FATAL: エントリ #6 \"big4.bin\": 宣言展開量の合計が上限を超えています",
            _ => "入力エラー: --entries: ファイルを読めません",
        };
        Assert.StartsWith(expected, run.ErrorLines[0], StringComparison.Ordinal);
    }

    // L11 (Runner): 正常な entries で delete --yes → 指定したエントリだけを処理する。ヘッダーに --entries の件数。
    // L12: 指定したエントリが MISSING / MODIFIED / SKIPPED → それぞれの結果で続行。STOP・DELETE_FAILED が無ければ終了 0。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void L11_L12_EntriesSelectProcessingScope(RunMode mode)
    {
        var h = new CommandHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello), ("missing.txt", Hello), ("size.txt", Hello), ("dir.txt", Hello), ("keep/k.txt", Hello)))
        {
            Mode = mode,
        };
        h.File("a.txt");
        h.File("b.txt");
        h.File("size.txt", Bytes("hello!"));
        h.Fs.AddDirectory(@"C:\target\dir.txt");
        h.Fs.AddDirectory(@"C:\target\keep");
        h.File(@"keep\k.txt");
        var entries = CommandHarness.WriteEntries("dir.txt\r\nsize.txt\r\nmissing.txt\r\nb.txt\r\n");

        var run = h.Delete(entriesPath: entries);

        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
        Assert.Contains("対象: 6 エントリ中 4 エントリ (--entries)", run.OutputLines);
        Assert.Equal("要約: 削除済み 1、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 1、DIRECTORY 0、DELETE_FAILED 0、処理対象外 2、未処理 0", run.OutputLines[^1]);
        Assert.Equal(
            [("b.txt", DeleteStatus.Deleted), ("missing.txt", DeleteStatus.Missing), ("size.txt", DeleteStatus.Modified), ("dir.txt", DeleteStatus.SkippedSpecialFile)],
            run.Outcome.Report!.Results.Select(r => (r.Entry.Name, r.Status)));
        Assert.True(h.Exists("a.txt"));
        Assert.True(h.Exists(@"keep\k.txt"));
        Assert.DoesNotContain(h.Fs.Calls, c => c.Contains(@"\keep", StringComparison.Ordinal) || c.EndsWith(@"\a.txt", StringComparison.Ordinal));
        Assert.Equal([(1, 4), (2, 4), (3, 4), (4, 4)], h.Progress);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public void EntriesSummary_DistinguishesExcludedFromUnprocessedAfterStop(RunMode mode)
    {
        var h = new CommandHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello), ("d.txt", Hello), ("keep/k.txt", Hello))) { Mode = mode };
        h.File("a.txt");
        h.File("b.txt").Errors[FakeOp.Streams] = 1117;
        h.File("c.txt");
        h.File("d.txt");
        h.Fs.AddDirectory(@"C:\target\keep");
        h.File(@"keep\k.txt");

        var run = h.Delete(entriesPath: CommandHarness.WriteEntries("a.txt\nb.txt\nc.txt\n"));

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.Equal("要約: 削除済み 1、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0、DELETE_FAILED 0、処理対象外 2、未処理 1", run.OutputLines[^2]);
        Assert.Equal(1, run.Outcome.Report!.NotProcessedCount);
        Assert.False(h.Exists("a.txt"));
        Assert.True(h.Exists("b.txt"));
        Assert.True(h.Exists("c.txt"));
        Assert.True(h.Exists("d.txt"));
        Assert.True(h.Exists(@"keep\k.txt"));
        Assert.DoesNotContain(run.OutputLines, l => l.Contains("c.txt", StringComparison.Ordinal) || l.Contains("d.txt", StringComparison.Ordinal) || l.Contains("keep/", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Fs.Calls, c => c.Contains(@"\keep", StringComparison.Ordinal) || c.EndsWith(@"\c.txt", StringComparison.Ordinal) || c.EndsWith(@"\d.txt", StringComparison.Ordinal));
    }

    // O13: 各ファイルエントリの結果が処理順に1行ずつ stdout に出る。DIRECTORY・処理対象外は行なし。要約の件数が結果行と一致。
    // O14 (Core): Fast は delete のヘッダーの先頭行と確認の直前の行に警告。--yes・非対話では確認と直前の警告は出ず、ヘッダーの警告だけ。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void O13_O14_SequentialOutputAndWarnings(RunMode mode)
    {
        var h = AllCategories(mode);
        var prompt = new ScriptedPrompt(true, "y");

        var run = h.Delete(yes: false, prompt: prompt);

        var fast = mode == RunMode.Fast;
        var lines = run.OutputLines;
        Assert.Equal([.. Header(mode), "対象: 全 7 エントリ", ReportText.Heading], lines.Take(Header(mode).Count + 2));
        Assert.Equal(
            [
                @"DELETED               same.txt -> C:\target\same.txt",
                fast ? @"DELETED               changed.txt -> C:\target\changed.txt" : @"MODIFIED              changed.txt -> C:\target\changed.txt",
                @"MODIFIED              size.txt -> C:\target\size.txt",
                @"MISSING               missing.txt -> C:\target\missing.txt",
                @"SKIPPED_SPECIAL_FILE  dir.txt -> C:\target\dir.txt (ディレクトリ)",
                @"SKIPPED_SPECIAL_FILE  d/ads.txt -> C:\target\d\ads.txt (ADS)",
            ],
            lines.Skip(Header(mode).Count + 2).Take(6));
        Assert.Equal(
            fast
                ? "要約: 削除済み 2、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 2、DIRECTORY 1、DELETE_FAILED 0、処理対象外 0、未処理 0"
                : "要約: 削除済み 1、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 2、DIRECTORY 1、DELETE_FAILED 0、処理対象外 0、未処理 0",
            lines[^1]);
        Assert.Empty(run.Error);

        var text = Assert.Single(prompt.Asked);
        var expected = DeleteOutput.ConfirmationPrompt(6, mode);
        Assert.Equal(expected, text);
        Assert.Equal(fast, text.StartsWith(ReportText.FastWarning + Environment.NewLine, StringComparison.Ordinal));
        Assert.EndsWith("続行しますか? [y/N] ", text, StringComparison.Ordinal);
        Assert.Equal(fast ? 1 : 0, lines.Count(l => l == ReportText.FastWarning));

        // --yes と非対話: 確認と直前の警告は出ず、ヘッダーの警告だけ。
        foreach (var (interactive, yes) in new[] { (true, true), (false, false) })
        {
            var other = new ScriptedPrompt(interactive, "y");
            var r = AllCategories(mode).Delete(yes: yes, prompt: other);
            Assert.Empty(other.Asked);
            Assert.Equal(fast ? 1 : 0, r.OutputLines.Count(l => l == ReportText.FastWarning));
            Assert.Equal(fast, r.OutputLines[0] == ReportText.FastWarning);
        }
    }

    // O15: STOP・DELETE_FAILED・削除された可能性あり。stdout に STOPPED 行・要約・「元に戻りません」「削除していません」(または
    // 「削除された可能性があります」)・「未処理の g 件には触れていません」。stderr に停止の原因。DELETE_FAILED の行に理由 (内容未確認を含む)。
    // STOP なしで DELETE_FAILED があればエラー終了の理由。
    [Theory]
    [InlineData("stop")]
    [InlineData("possibly")]
    [InlineData("delete-failed")]
    public void O15_StopAndDeleteFailedOutput(string kind)
    {
        var h = new CommandHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello), ("d.txt", Hello)));
        h.File("a.txt");
        var b = h.File("b.txt");
        h.File("c.txt");
        h.File("d.txt");
        switch (kind)
        {
            case "stop": b.Errors[FakeOp.Streams] = 1117; break;
            case "possibly": b.DispositionHasNoEffect = true; break;
            case "delete-failed": b.Errors[FakeOp.OpenDeletion] = 32; break;
        }

        var run = h.Delete();

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        var lines = run.OutputLines;
        if (kind == "delete-failed")
        {
            Assert.Contains(
                @"DELETE_FAILED         b.txt -> C:\target\b.txt : 削除用に開けません (Win32 エラー 32: 他のプログラムが使用中 (共有違反))。識別確認の時点では同じファイルに見えるため、削除せずに残しました。内容は確認していません",
                lines);
            Assert.Equal("要約: 削除済み 3、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0、DELETE_FAILED 1、処理対象外 0、未処理 0", lines[^1]);
            Assert.Equal(["DELETE_FAILED が 1 件あるため、エラーとして終了します (削除済み 3)。"], run.ErrorLines);
            Assert.True(h.Exists("b.txt"));
            Assert.False(h.Exists("d.txt"));
            return;
        }

        Assert.Contains(@"STOPPED               b.txt -> C:\target\b.txt", lines);
        Assert.DoesNotContain(lines, l => l.Contains("c.txt", StringComparison.Ordinal));
        Assert.Equal("要約: 削除済み 1、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0、DELETE_FAILED 0、処理対象外 0、未処理 2", lines[^2]);
        var possibly = kind == "possibly";
        Assert.Equal(
            $"途中で停止しました。それまでに削除した 1 件は元に戻りません。b.txt は{(possibly ? "削除された可能性があります" : "削除していません")}。未処理の 2 件には触れていません。",
            lines[^1]);
        Assert.Equal(2, run.ErrorLines.Count);
        Assert.StartsWith("停止: エントリ #2 \"b.txt\": ", run.ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal(possibly, run.ErrorLines[0].EndsWith(" (削除された可能性あり)", StringComparison.Ordinal));
        Assert.Equal("以後の処理を停止しました (削除済み 1、DELETE_FAILED 0、未処理 2)。", run.ErrorLines[1]);
        Assert.True(h.Exists("c.txt"));
        Assert.True(h.Exists("d.txt"));
    }

    // S01 (Runner): A01 の fixture で delete --yes → 終了 0。対象だけが削除され、他は残る。ZIP は変わらない。
    // A07 (Core): 競合のない同じ fixture で analyze → delete --yes。analyze の MATCHED (Fast は SAME_SIZE) と delete の DELETED が同じエントリの集合
    // (保証ではなく、競合なし・権限差なしの条件での確認)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void S01_A07_AnalyzeThenDelete(RunMode mode)
    {
        var h = AllCategories(mode);
        var archive = Convert.ToHexString(h.Fs.Get(CommandHarness.ArchivePath).Content);

        var analysis = h.Analyze().Outcome.Analysis!;
        var delete = h.Delete();

        Assert.Equal(ExitStatus.Success, delete.Outcome.Status);
        var candidates = analysis.Results.Where(r => r.Classification == Candidate(mode)).Select(r => r.Entry.Name);
        var deleted = delete.Outcome.Report!.Results.Where(r => r.Status == DeleteStatus.Deleted).Select(r => r.Entry.Name);
        Assert.Equal(candidates, deleted);
        Assert.Equal(
            analysis.Results.Where(r => r.Classification == Classification.Missing).Select(r => r.Entry.Name),
            delete.Outcome.Report.Results.Where(r => r.Status == DeleteStatus.Missing).Select(r => r.Entry.Name));
        Assert.Equal(
            analysis.Results.Where(r => r.Classification == Classification.SkippedSpecialFile).Select(r => r.Entry.Name),
            delete.Outcome.Report.Results.Where(r => r.Status == DeleteStatus.SkippedSpecialFile).Select(r => r.Entry.Name));
        Assert.True(h.Exists("unrelated.txt"));
        Assert.True(h.Exists("d"));
        Assert.Equal(archive, Convert.ToHexString(h.Fs.Get(CommandHarness.ArchivePath).Content));
    }

    // 逐次処理を始めた後の、エントリの処理の外の想定外の例外 (結果の表示の失敗など) は、削除0件とは言わずに報告してエラー終了する。
    [Theory]
    [InlineData(RunMode.Strict, "DELETED")]
    [InlineData(RunMode.Fast, "DELETED")]
    [InlineData(RunMode.Strict, "要約:")]
    [InlineData(RunMode.Fast, "要約:")]
    public void ExceptionOutsideEntryProcessing_IsReportedWithoutClaimingZeroDeletions(RunMode mode, string trigger)
    {
        var h = new CommandHarness(MakeZip(("a.txt", Hello)));
        h.File("a.txt");
        var output = new ThrowingWriter(trigger);
        var error = new StringWriter();
        var context = new CommandContext(
            h.Fs, () => h.Locations, Limits.Default, output, error, OpenArchive: _ => ZipArchiveSource.Open(new MemoryStream(MakeZip(("a.txt", Hello)))));

        var started = false;
        var outcome = DeleteCommand.Run(new DeleteCommandRequest(
            CommandHarness.ArchivePath, CommandHarness.TargetPath, mode, null, true, h.Fs, new ScriptedPrompt(true), context,
            DeletionStarting: () => started = true));

        Assert.Equal(ExitStatus.Error, outcome.Status);
        Assert.True(started);
        Assert.False(h.Exists("a.txt"));
        if (trigger == "要約:")
        {
            Assert.Equal(1, outcome.Report!.Count(DeleteStatus.Deleted));
            Assert.Contains("逐次処理後の報告または終了処理に失敗しました (削除済み 1 件)。削除したファイルは元に戻りません。", error.ToString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(outcome.Report);
            Assert.Contains("逐次処理の途中で中止しました。それまでに削除したファイルは元に戻りません。", error.ToString(), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("削除0件", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, h.Fs.OpenHandleCount);
    }

    // 中断時の表示と実削除 (旧 M10 の I1・I2、docs/spec/cli.md#interruption) の人間向け出力の側: 結果行は、そのファイルの削除用ハンドルを
    // 閉じた後、次のファイルを開く前に書く。結果行を書けなければ後続のファイルを開かない (書けなかった1件は削除済みで、表示されない)。
    // 機械出力の同じ順序は J07・J13 が確かめる。
    [Theory]
    [InlineData(RunMode.Strict, false)]
    [InlineData(RunMode.Fast, false)]
    [InlineData(RunMode.Strict, true)]
    [InlineData(RunMode.Fast, true)]
    public void ResultLineFollowsCloseAndPrecedesNextOpen(RunMode mode, bool failAtB)
    {
        var zip = MakeZip(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello));
        var h = new CommandHarness(zip);
        h.File("a.txt");
        h.File("b.txt");
        h.File("c.txt");
        var output = new ResultLineRecorder(h.Fs.Calls, failAtB ? "b.txt" : null);
        var error = new StringWriter();
        var context = new CommandContext(h.Fs, () => h.Locations, Limits.Default, output, error,
            OpenArchive: _ => ZipArchiveSource.Open(new MemoryStream(zip)));

        var outcome = DeleteCommand.Run(new DeleteCommandRequest(
            CommandHarness.ArchivePath, CommandHarness.TargetPath, mode, null, true, h.Fs, new ScriptedPrompt(true), context));

        string[] order = failAtB
            ? [@"OpenDeletion \\?\C:\target\a.txt", @"Close deletion \\?\C:\target\a.txt", "Line DELETED a.txt",
                @"OpenDeletion \\?\C:\target\b.txt", @"Close deletion \\?\C:\target\b.txt", "Line DELETED b.txt (failed)"]
            : [@"OpenDeletion \\?\C:\target\a.txt", @"Close deletion \\?\C:\target\a.txt", "Line DELETED a.txt",
                @"OpenDeletion \\?\C:\target\b.txt", @"Close deletion \\?\C:\target\b.txt", "Line DELETED b.txt",
                @"OpenDeletion \\?\C:\target\c.txt", @"Close deletion \\?\C:\target\c.txt", "Line DELETED c.txt"];
        Assert.Equal(order, h.Fs.Calls.Where(call => call.StartsWith("OpenDeletion ", StringComparison.Ordinal)
            || call.StartsWith("Close deletion ", StringComparison.Ordinal) || call.StartsWith("Line ", StringComparison.Ordinal)));
        Assert.False(h.Exists("a.txt"));
        Assert.False(h.Exists("b.txt"));
        Assert.Equal(failAtB, h.Exists("c.txt"));
        Assert.Equal(failAtB ? ExitStatus.Error : ExitStatus.Success, outcome.Status);
        if (failAtB)
        {
            Assert.Contains("逐次処理の途中で中止しました。それまでに削除したファイルは元に戻りません。", error.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(0, h.Fs.OpenHandleCount);
    }

    [Fact]
    public void SummaryOutputFailure_PreservesPossiblyDeletedStop()
    {
        var zip = MakeZip(("a.txt", Hello));
        var h = new CommandHarness(zip);
        h.File("a.txt").DispositionHasNoEffect = true;
        var output = new ThrowingWriter("要約:");
        var error = new StringWriter();
        var context = new CommandContext(h.Fs, () => h.Locations, Limits.Default, output, error,
            OpenArchive: _ => ZipArchiveSource.Open(new MemoryStream(zip)));

        var outcome = DeleteCommand.Run(new DeleteCommandRequest(
            CommandHarness.ArchivePath, CommandHarness.TargetPath, RunMode.Strict, null, true, h.Fs, new ScriptedPrompt(true), context));

        Assert.Equal(ExitStatus.Error, outcome.Status);
        Assert.True(outcome.Report!.Stop!.PossiblyDeleted);
        Assert.Contains("削除された可能性あり", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("削除0件", error.ToString(), StringComparison.Ordinal);
        Assert.True(h.Exists("a.txt"));
        Assert.Equal(0, h.Fs.OpenHandleCount);
    }

    [Theory]
    [InlineData(RunMode.Strict, false)]
    [InlineData(RunMode.Fast, false)]
    [InlineData(RunMode.Strict, true)]
    [InlineData(RunMode.Fast, true)]
    public void ErrorOutputFailure_ResumesAfterWrittenStopCause(RunMode mode, bool possiblyDeleted)
    {
        var zip = MakeZip(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello));
        var h = new CommandHarness(zip);
        h.File("a.txt");
        var b = h.File("b.txt");
        h.File("c.txt");
        if (possiblyDeleted)
        {
            b.DispositionHasNoEffect = true;
        }
        else
        {
            b.Errors[FakeOp.Streams] = 1117;
        }
        var output = new StringWriter();
        var error = new FailOnceWriter("以後の処理を停止しました");
        var context = new CommandContext(h.Fs, () => h.Locations, Limits.Default, output, error,
            OpenArchive: _ => ZipArchiveSource.Open(new MemoryStream(zip)));

        var outcome = DeleteCommand.Run(new DeleteCommandRequest(
            CommandHarness.ArchivePath, CommandHarness.TargetPath, mode, null, true, h.Fs, new ScriptedPrompt(true), context));

        Assert.True(error.Failed);
        Assert.Equal(ExitStatus.Error, outcome.Status);
        Assert.Equal(1, outcome.Report!.Count(DeleteStatus.Deleted));
        Assert.Equal("b.txt", outcome.Report.Stop!.Entry.Name);
        Assert.Equal(possiblyDeleted, outcome.Report.Stop.PossiblyDeleted);
        Assert.Equal(1, outcome.Report.NotProcessedCount);
        Assert.False(h.Exists("a.txt"));
        Assert.True(h.Exists("b.txt"));
        Assert.True(h.Exists("c.txt"));
        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("停止: エントリ #2 \"b.txt\":", lines[0], StringComparison.Ordinal);
        Assert.Single(lines, line => line.StartsWith("停止:", StringComparison.Ordinal));
        Assert.Single(lines, line => line.StartsWith("以後の処理を停止しました", StringComparison.Ordinal));
        Assert.Equal("以後の処理を停止しました (削除済み 1、DELETE_FAILED 0、未処理 1)。", lines[^1]);
        Assert.DoesNotContain("削除0件", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, h.Fs.OpenHandleCount);
    }

    [Theory]
    [InlineData(RunMode.Strict, "stop")]
    [InlineData(RunMode.Fast, "stop")]
    [InlineData(RunMode.Strict, "possibly")]
    [InlineData(RunMode.Fast, "possibly")]
    [InlineData(RunMode.Strict, "delete-failed")]
    [InlineData(RunMode.Fast, "delete-failed")]
    public void DisposeFailure_DoesNotRepeatCompletedErrorReport(RunMode mode, string kind)
    {
        var zip = MakeZip(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello));
        var h = new CommandHarness(zip);
        h.File("a.txt");
        var b = h.File("b.txt");
        h.File("c.txt");
        switch (kind)
        {
            case "stop": b.Errors[FakeOp.Streams] = 1117; break;
            case "possibly": b.DispositionHasNoEffect = true; break;
            case "delete-failed": b.Errors[FakeOp.OpenDeletion] = 32; break;
        }
        var output = new StringWriter();
        var error = new StringWriter();
        var stream = new DisposeFailingStream(zip);
        var context = new CommandContext(h.Fs, () => h.Locations, Limits.Default, output, error,
            OpenArchive: _ => ZipArchiveSource.Open(stream));

        var outcome = DeleteCommand.Run(new DeleteCommandRequest(
            CommandHarness.ArchivePath, CommandHarness.TargetPath, mode, null, true, h.Fs, new ScriptedPrompt(true), context));

        Assert.True(stream.Disposed);
        Assert.False(stream.CanRead);
        Assert.Equal(ExitStatus.Error, outcome.Status);
        var deleteFailed = kind == "delete-failed";
        Assert.Equal(deleteFailed ? 2 : 1, outcome.Report!.Count(DeleteStatus.Deleted));
        Assert.Equal(deleteFailed ? 1 : 0, outcome.Report.Count(DeleteStatus.DeleteFailed));
        Assert.Equal(deleteFailed ? 0 : 1, outcome.Report.NotProcessedCount);
        if (deleteFailed)
        {
            Assert.Null(outcome.Report.Stop);
        }
        else
        {
            Assert.Equal("b.txt", outcome.Report.Stop!.Entry.Name);
            Assert.Equal(kind == "possibly", outcome.Report.Stop.PossiblyDeleted);
        }
        Assert.False(h.Exists("a.txt"));
        Assert.True(h.Exists("b.txt"));
        Assert.Equal(!deleteFailed, h.Exists("c.txt"));
        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var expectedErrors = DeleteOutput.Errors(outcome.Report);
        Assert.Equal(expectedErrors, lines.Take(expectedErrors.Count));
        Assert.Equal(expectedErrors.Count + 2, lines.Length);
        foreach (var expected in expectedErrors)
        {
            Assert.Single(lines, line => line == expected);
        }
        Assert.Contains("injected dispose failure", lines[expectedErrors.Count], StringComparison.Ordinal);
        Assert.DoesNotContain("削除0件", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, h.Fs.OpenHandleCount);
    }

    private sealed class FailOnceWriter(string trigger) : StringWriter
    {
        public bool Failed { get; private set; }

        public override void WriteLine(string? value)
        {
            if (!Failed && value?.StartsWith(trigger, StringComparison.Ordinal) == true)
            {
                Failed = true;
                throw new IOException("injected error output failure");
            }
            base.WriteLine(value);
        }
    }

    private sealed class DisposeFailingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && !Disposed)
            {
                Disposed = true;
                throw new IOException("injected dispose failure");
            }
        }
    }

    // 結果行 (状態名 + 空白 + Entry) を書くたびに、偽 FS の呼び出し記録へ "Line <状態名> <Entry>" を足す。failAt の Entry の行は書けない。
    private sealed class ResultLineRecorder(List<string> calls, string? failAt) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            var parts = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (parts is ["DELETED" or "MODIFIED" or "MISSING", _, ..])
            {
                if (parts[1] == failAt)
                {
                    calls.Add($"Line {parts[0]} {parts[1]} (failed)");
                    throw new IOException("injected result line failure");
                }

                calls.Add($"Line {parts[0]} {parts[1]}");
            }

            base.WriteLine(value);
        }
    }

    private sealed class ThrowingWriter(string trigger) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            if (value?.StartsWith(trigger, StringComparison.Ordinal) == true)
            {
                throw new IOException("injected");
            }

            base.WriteLine(value);
        }
    }
}
