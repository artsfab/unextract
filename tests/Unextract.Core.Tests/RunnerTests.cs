using Unextract.Core.Analysis;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;
using static Unextract.Core.Tests.Fakes.PipelineHarness;

namespace Unextract.Core.Tests;

// 実行の流れ (SPEC §2、§3) と、--dry-run と通常実行の一致 (テスト Y01) を確認する。
public class RunnerTests
{
    private static readonly byte[] Hello = Bytes("hello");

    private sealed class ScriptedPrompt(bool interactive, params string?[] answers) : IConfirmationPrompt
    {
        private readonly Queue<string?> _answers = new(answers);

        public List<string> Asked { get; } = [];

        public bool IsInteractive => interactive;

        public string? Ask(string prompt)
        {
            Asked.Add(prompt);
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }
    }

    private sealed class RecordingDeletion : IDeletionPhase
    {
        public List<IReadOnlyList<MatchedFile>> Calls { get; } = [];

        public List<DeletionRequest> Requests { get; } = [];

        public DeletionReport Delete(DeletionRequest request)
        {
            Requests.Add(request);
            Calls.Add(request.Candidates);
            return new DeletionReport(request.Candidates.Select(c => c.Entry).ToList(), [], null, 0);
        }
    }

    // Output は標準出力、Error は標準エラー出力に相当する (SPEC §10)。
    private sealed record Run(RunOutcome Outcome, string Output, string Error, FakeFileSystem Fs, RecordingContentProvider Contents, ScriptedPrompt Prompt, RecordingDeletion Deletion);

    private static Run Execute(
        byte[] zip, Action<FakeFileSystem> arrange, bool dryRun, ScriptedPrompt? prompt = null, bool yes = false, Limits? limits = null, RunMode mode = RunMode.Strict)
    {
        using var harness = new PipelineHarness(zip);
        arrange(harness.Fs);
        prompt ??= new ScriptedPrompt(true, "n");
        var deletion = new RecordingDeletion();
        var output = new StringWriter();
        var error = new StringWriter();
        var contents = harness.Contents;
        var outcome = UnextractRunner.Run(new RunRequest(
            harness.Source, ArchivePath, TargetPath, dryRun, yes, harness.Fs, TargetLocationPolicy.None,
            limits ?? Limits.Default, prompt, deletion, output, error, Contents: contents, Mode: mode));
        return new Run(outcome, output.ToString(), error.ToString(), harness.Fs, contents, prompt, deletion);
    }

    private static void Standard(FakeFileSystem fs)
    {
        fs.AddFile(@"C:\target\same.txt", Bytes("hello"));
        fs.AddFile(@"C:\target\changed.txt", Bytes("hellO"));
        fs.AddDirectory(@"C:\target\d");
        fs.AddFile(@"C:\target\d\ads.txt", Bytes("hello")).ExtraStreams.Add(new StreamEntry(":x:$DATA", 1));
    }

    private static byte[] StandardZip() =>
        MakeZip(("same.txt", Hello), ("changed.txt", Hello), ("missing.txt", Hello), ("d/", null), ("d/ads.txt", Hello));

    // Y01: --dry-run と通常実行 (確認で n) は、初回分類・FATAL 判定・表示が一致し、同じパイプライン (同じ呼び出し列) を通る。
    // 一致は同じモード同士の規定 (SPEC §2、§15.1)。Fast では crc-fatal の fixture が FATAL にならず SAME_SIZE になる (C15)。
    [Theory]
    [InlineData("normal", RunMode.Strict)]
    [InlineData("crc-fatal", RunMode.Strict)]
    [InlineData("open-fatal", RunMode.Strict)]
    [InlineData("prevalidation-fatal", RunMode.Strict)]
    [InlineData("normal", RunMode.Fast)]
    [InlineData("crc-fatal", RunMode.Fast)]
    [InlineData("open-fatal", RunMode.Fast)]
    [InlineData("prevalidation-fatal", RunMode.Fast)]
    public void Y01_DryRunAndCancelledRun_Agree(string scenario, RunMode mode)
    {
        var zip = StandardZip();
        Action<FakeFileSystem> arrange = Standard;
        switch (scenario)
        {
            case "crc-fatal":
                var patcher = new ZipPatcher(zip);
                zip = patcher.SetCrc32(0, patcher.GetCrc32(0) ^ 1).ToArray();
                break;
            case "open-fatal":
                arrange = fs =>
                {
                    Standard(fs);
                    fs.Get(@"C:\target\changed.txt").Errors[FakeOp.OpenComparison] = 32;
                };
                break;
            case "prevalidation-fatal":
                zip = ZipFixture.Create(new FixtureEntry("ok.txt", Hello), new FixtureEntry("CON.txt", Hello));
                break;
        }

        var dry = Execute(zip, arrange, dryRun: true, mode: mode);
        var normal = Execute(zip, arrange, dryRun: false, mode: mode);

        Assert.Equal(dry.Fs.Calls, normal.Fs.Calls);
        Assert.Equal(dry.Contents.Calls, normal.Contents.Calls);
        Assert.Equal(dry.Outcome.ReportLines, normal.Outcome.ReportLines);
        Assert.Equal(dry.Outcome.Analysis!.Fatal, normal.Outcome.Analysis!.Fatal);
        Assert.Equal(
            dry.Outcome.Analysis.Results.Select(r => (r.Entry, r.Classification, r.SkipReason)),
            normal.Outcome.Analysis.Results.Select(r => (r.Entry, r.Classification, r.SkipReason)));
        Assert.Empty(dry.Deletion.Calls);
        Assert.Empty(normal.Deletion.Calls);
        Assert.Empty(dry.Prompt.Asked);

        if (scenario == "normal" || (scenario == "crc-fatal" && mode == RunMode.Fast))
        {
            Assert.Equal(ExitStatus.Success, dry.Outcome.Status);
            Assert.Equal(ExitStatus.UserCancelled, normal.Outcome.Status);
            Assert.Single(normal.Prompt.Asked);
            Assert.Contains("--dry-run のため削除しません。", dry.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(ExitStatus.Error, dry.Outcome.Status);
            Assert.Equal(ExitStatus.Error, normal.Outcome.Status);
            Assert.Empty(normal.Prompt.Asked);
        }
    }

    // P02 (ZIP 名不正・resource limits 超過): 事前検証の FATAL では target の項目に触れず (列挙しない)、削除0件
    [Theory]
    [InlineData("name", RunMode.Strict)]
    [InlineData("limit", RunMode.Strict)]
    [InlineData("name", RunMode.Fast)]
    [InlineData("limit", RunMode.Fast)]
    public void P02_PrevalidationFatal_DoesNotEnumerateTarget(string kind, RunMode mode)
    {
        var zip = kind == "name"
            ? ZipFixture.Create(new FixtureEntry("same.txt", Hello), new FixtureEntry("bad|name.txt", Hello))
            : StandardZip();
        var limits = kind == "limit" ? Limits.Default with { MaxEntries = 3 } : null;

        var run = Execute(zip, Standard, dryRun: false, prompt: new ScriptedPrompt(true, "y"), limits: limits, mode: mode);

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.Equal(kind == "name" ? FatalKind.InvalidCharacter : FatalKind.TooManyEntries, run.Outcome.Analysis!.Fatal!.Kind);
        Assert.DoesNotContain(run.Fs.Calls, c => c.StartsWith("Enumerate", StringComparison.Ordinal));
        Assert.Equal(0, run.Fs.ComparisonOpenCount);
        Assert.Empty(run.Deletion.Calls);
        Assert.Contains("削除0件", run.Error, StringComparison.Ordinal);
        Assert.Contains("FATAL: ", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("FATAL", run.Output, StringComparison.Ordinal);
    }

    // §2: y / Y だけで開始。空入力・EOF・その他は中止。非対話で --yes なしは中止。--yes は確認だけを省略する。
    [Theory]
    [InlineData(true, "y", false, true)]
    [InlineData(true, "Y", false, true)]
    [InlineData(true, "", false, false)]
    [InlineData(true, null, false, false)]
    [InlineData(true, "yes", false, false)]
    [InlineData(true, " y", false, false)]
    [InlineData(true, "n", false, false)]
    [InlineData(false, "y", false, false)]
    [InlineData(false, null, true, true)]
    public void Confirmation_StartsOnlyOnExplicitYes(bool interactive, string? answer, bool yes, bool deletes)
    {
        var prompt = new ScriptedPrompt(interactive, answer);

        var run = Execute(StandardZip(), Standard, dryRun: false, prompt: prompt, yes: yes);

        Assert.Equal(deletes ? 1 : 0, run.Deletion.Calls.Count);
        Assert.Equal(deletes ? ExitStatus.Success : ExitStatus.UserCancelled, run.Outcome.Status);
        if (deletes)
        {
            Assert.Equal(["same.txt"], run.Deletion.Calls[0].Select(m => m.Entry.Name));
        }

        if (!interactive || yes)
        {
            Assert.Empty(prompt.Asked);
        }
    }

    // P05 (Core): 削除候補0件・空 ZIP なら確認プロンプトなしで正常終了 (両モード)
    [Theory]
    [InlineData(false, RunMode.Strict)]
    [InlineData(true, RunMode.Strict)]
    [InlineData(false, RunMode.Fast)]
    [InlineData(true, RunMode.Fast)]
    public void NoCandidates_EndsWithoutPrompt(bool emptyZip, RunMode mode)
    {
        var prompt = new ScriptedPrompt(true, "y");
        var zip = emptyZip ? ZipFixture.Create() : MakeZip(("missing.txt", Hello));

        var run = Execute(zip, _ => { }, dryRun: false, prompt: prompt, mode: mode);

        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
        Assert.Empty(prompt.Asked);
        Assert.Empty(run.Deletion.Calls);
        Assert.Contains("削除候補はありません。", run.Output, StringComparison.Ordinal);
    }

    // モード違いの再利用 (PLAN_TESTS): Z01〜Z08、R01〜R06、O04 の単体テストは mode を受け取らない関数 (ZipPrevalidator、ZipArchiveSource、
    // SafeDisplay) を対象にしている。同じ種類の fixture を runner 経由で Strict と --fast の両方で実行し、事前検証の結果
    // (FATAL の種類・原因エントリ、または受理) と標準エラー出力が両モードで同じこと、FATAL では target を列挙しないことを確かめる。
    // 受理される fixture では、削除候補の分類が Strict は MATCHED、Fast は SAME_SIZE になる (期待値の読み替え)。
    public static TheoryData<string> PrevalidationReuseIds =>
    [
        "Z01", "Z02", "Z02-O04", "Z03-case", "Z03-file-parent", "Z04", "Z04a", "Z04b", "Z05", "Z06", "Z07",
        "Z08-zip64", "Z08-data-descriptor", "Z08-sfx", "R01-allowed", "R01-exceeded", "R02-name", "R02-depth", "R03", "R04",
        "R05", "R06",
    ];

    [Theory]
    [MemberData(nameof(PrevalidationReuseIds))]
    public void ModeReuse_PrevalidationIsTheSameInBothModes(string id)
    {
        var (zip, arrange, limits, expected, index) = PrevalidationFixture(id);

        var strict = Execute(zip, arrange, dryRun: false, limits: limits, mode: RunMode.Strict);
        var fast = Execute(zip, arrange, dryRun: false, limits: limits, mode: RunMode.Fast);

        Assert.Equal(strict.Outcome.Analysis!.Fatal, fast.Outcome.Analysis!.Fatal);
        Assert.Equal(strict.Error, fast.Error);
        foreach (var (run, mode) in new[] { (strict, RunMode.Strict), (fast, RunMode.Fast) })
        {
            var analysis = run.Outcome.Analysis!;
            Assert.Equal(mode == RunMode.Fast, run.Outcome.ReportLines[0] == AnalysisReport.FastWarning);
            if (expected is { } kind)
            {
                Assert.Equal(kind, analysis.Fatal?.Kind);
                Assert.Equal(index, analysis.Fatal!.Entry!.Index);
                Assert.Equal(ExitStatus.Error, run.Outcome.Status);
                Assert.DoesNotContain(run.Fs.Calls, c => c.StartsWith("Enumerate", StringComparison.Ordinal));
                Assert.Empty(run.Deletion.Calls);
                Assert.Empty(run.Prompt.Asked);
                Assert.Contains("削除0件", run.Error, StringComparison.Ordinal);
            }
            else
            {
                Assert.Null(analysis.Fatal);
                Assert.NotEmpty(analysis.DeletionCandidates);
                Assert.All(analysis.DeletionCandidates, c => Assert.Equal(
                    Candidate(mode), analysis.Results.Single(r => r.Entry == c.Entry).Classification));
            }
        }

        if (id == "Z02-O04")
        {
            // O04: 表示できない名前 (制御文字) は FATAL の原因としてエスケープ表記とエントリ番号で表示する (両モード)。
            Assert.Contains("エントリ #2 \"a\\u{0001}.txt\"", fast.Error, StringComparison.Ordinal);
        }
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

    // target の入力エラー (P06・P07 の最終成分が reparse) では ZIP 事前検証にも分類にも進まない (両モード)
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void TargetInputError_StopsBeforeAnalysis(RunMode mode)
    {
        var run = Execute(StandardZip(), fs => fs.Get(@"C:\target").MakeReparse(), dryRun: true, mode: mode);

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.Equal(FatalKind.TargetIsReparsePoint, run.Outcome.InputError?.Kind);
        Assert.Null(run.Outcome.Analysis);
        Assert.Contains("入力エラー", run.Error, StringComparison.Ordinal);
        Assert.Empty(run.Output);
    }

    // 削除フェーズには、初回分類で使った比較器と同じインスタンスが渡る (PLAN.md §1。比較基準の共有)
    [Fact]
    public void DeletionPhase_ReceivesTheComparerUsedForInitialClassification()
    {
        var run = Execute(StandardZip(), Standard, dryRun: false, prompt: new ScriptedPrompt(true, "y"));

        var request = Assert.Single(run.Deletion.Requests);

        // same.txt と changed.txt (サイズ一致) の2件を、渡された比較器が初回比較している。
        Assert.Equal(2, request.Comparer.InitialComparisons);
        Assert.Equal(0, request.Comparer.RecheckComparisons);
        Assert.Same(run.Contents, request.Contents);
    }

    // ZIP 自身の File ID を取得できなければ、分類に進まず全体 FATAL
    [Fact]
    public void ArchiveIdentityFailure_IsFatalBeforeClassification()
    {
        var run = Execute(StandardZip(), fs =>
        {
            Standard(fs);
            fs.Get(ArchivePath).Errors[FakeOp.GetFileIdentity] = 5;
        }, dryRun: true);

        Assert.Equal(FatalKind.ArchiveIdentityFailed, run.Outcome.Analysis!.Fatal!.Kind);
        Assert.Equal(5, run.Outcome.Analysis.UnclassifiedCount);
        Assert.DoesNotContain(run.Fs.Calls, c => c.StartsWith("Enumerate", StringComparison.Ordinal));
    }

    // O06: --fast の結果表示に至る全実行 (通常実行 y、--dry-run、削除候補0件、ZIP 事前検証の FATAL、初回分類中の FATAL、--yes) で、
    // PLAN.md §4 の警告の文言が標準出力の解析結果の一覧の先頭行に出る。同じ各実行を Strict で行うと出ない。
    // 結果表示に至らない終了 (target の入力エラー) では両モードとも出ない。ZIP を開けない場合は CLI のテスト (Cli.Tests) で確かめる。
    [Theory]
    [InlineData("normal-y", RunMode.Fast)]
    [InlineData("dry-run", RunMode.Fast)]
    [InlineData("no-candidates", RunMode.Fast)]
    [InlineData("prevalidation-fatal", RunMode.Fast)]
    [InlineData("classification-fatal", RunMode.Fast)]
    [InlineData("yes", RunMode.Fast)]
    [InlineData("input-error", RunMode.Fast)]
    [InlineData("normal-y", RunMode.Strict)]
    [InlineData("dry-run", RunMode.Strict)]
    [InlineData("no-candidates", RunMode.Strict)]
    [InlineData("prevalidation-fatal", RunMode.Strict)]
    [InlineData("classification-fatal", RunMode.Strict)]
    [InlineData("yes", RunMode.Strict)]
    [InlineData("input-error", RunMode.Strict)]
    public void O06_FastWarningHeader(string kind, RunMode mode)
    {
        var zip = kind switch
        {
            "no-candidates" => MakeZip(("missing.txt", Hello)),
            "prevalidation-fatal" => ZipFixture.Create(new FixtureEntry("ok.txt", Hello), new FixtureEntry("CON.txt", Hello)),
            _ => StandardZip(),
        };
        Action<FakeFileSystem> arrange = kind switch
        {
            "classification-fatal" => fs =>
            {
                Standard(fs);
                fs.Get(@"C:\target\changed.txt").Errors[FakeOp.OpenComparison] = 32;
            },
            "input-error" => fs => fs.Get(@"C:\target").MakeReparse(),
            _ => Standard,
        };

        var run = Execute(zip, arrange, dryRun: kind == "dry-run", prompt: new ScriptedPrompt(true, "y"), yes: kind == "yes", mode: mode);

        var stdout = run.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var showsResult = kind != "input-error";
        Assert.Equal(showsResult, run.Outcome.Analysis is not null);
        if (mode == RunMode.Fast && showsResult)
        {
            Assert.Equal(AnalysisReport.FastWarning, stdout[0]);
            Assert.Equal(AnalysisReport.FastWarning, run.Outcome.ReportLines[0]);
            Assert.Single(stdout, l => l == AnalysisReport.FastWarning);
        }
        else
        {
            Assert.DoesNotContain(AnalysisReport.FastWarning, run.Output, StringComparison.Ordinal);
        }

        // 警告は標準出力だけに出す (SPEC §10)。
        Assert.DoesNotContain(AnalysisReport.FastWarning, run.Error, StringComparison.Ordinal);
        Assert.Equal(
            "警告: --fast のため、パスとサイズだけで判定しています。内容が一致することと、ZIP から正常に展開できることは確認していません。",
            AnalysisReport.FastWarning);
    }

    // O07: --fast の対話的な通常実行では、確認プロンプトの経路 (偽プロンプトに渡る文字列) で警告が [y/N] の直前の行に出る。
    // Strict では出ない。--fast --yes と --fast の非対話 (--yes なし) ではプロンプトを出さないため出ず、ヘッダーの警告 (O06) だけになる。
    [Theory]
    [InlineData(RunMode.Fast, true, false)]
    [InlineData(RunMode.Strict, true, false)]
    [InlineData(RunMode.Fast, true, true)]
    [InlineData(RunMode.Fast, false, false)]
    public void O07_FastWarningBeforeConfirmation(RunMode mode, bool interactive, bool yes)
    {
        var prompt = new ScriptedPrompt(interactive, "n");

        var run = Execute(StandardZip(), Standard, dryRun: false, prompt: prompt, yes: yes, mode: mode);

        if (interactive && !yes)
        {
            var asked = Assert.Single(prompt.Asked);
            var lines = asked.Split(Environment.NewLine);
            Assert.EndsWith("[y/N] ", lines[^1], StringComparison.Ordinal);
            if (mode == RunMode.Fast)
            {
                Assert.Equal([AnalysisReport.FastWarning, "2 件のファイルを削除します。よろしいですか? [y/N] "], lines);
            }
            else
            {
                Assert.Equal(["1 件のファイルを削除します。よろしいですか? [y/N] "], lines);
                Assert.DoesNotContain(AnalysisReport.FastWarning, asked, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.Empty(prompt.Asked);
            Assert.Single(run.Output.Split(Environment.NewLine), l => l == AnalysisReport.FastWarning);
        }
    }
}

file static class FakeNodeTestExtensions
{
    // 入力エラーの例として target を reparse にする。
    public static void MakeReparse(this FakeNode node)
    {
        node.Attributes |= 0x400;
        node.ReparseTag = FakeNode.ReparseTagMountPoint;
    }
}
