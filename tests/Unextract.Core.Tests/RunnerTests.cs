using Unextract.Core.Analysis;
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

    private static Run Execute(byte[] zip, Action<FakeFileSystem> arrange, bool dryRun, ScriptedPrompt? prompt = null, bool yes = false, Limits? limits = null)
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
            limits ?? Limits.Default, prompt, deletion, output, error, Contents: contents));
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

    // Y01: --dry-run と通常実行 (確認で n) は、初回分類・FATAL 判定・表示が一致し、同じパイプライン (同じ呼び出し列) を通る
    [Theory]
    [InlineData("normal")]
    [InlineData("crc-fatal")]
    [InlineData("open-fatal")]
    [InlineData("prevalidation-fatal")]
    public void Y01_DryRunAndCancelledRun_Agree(string scenario)
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

        var dry = Execute(zip, arrange, dryRun: true);
        var normal = Execute(zip, arrange, dryRun: false);

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

        if (scenario == "normal")
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
    [InlineData("name")]
    [InlineData("limit")]
    public void P02_PrevalidationFatal_DoesNotEnumerateTarget(string kind)
    {
        var zip = kind == "name"
            ? ZipFixture.Create(new FixtureEntry("same.txt", Hello), new FixtureEntry("bad|name.txt", Hello))
            : StandardZip();
        var limits = kind == "limit" ? Limits.Default with { MaxEntries = 3 } : null;

        var run = Execute(zip, Standard, dryRun: false, prompt: new ScriptedPrompt(true, "y"), limits: limits);

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

    // P05 (Core): 削除候補0件なら確認プロンプトなしで正常終了
    [Fact]
    public void NoCandidates_EndsWithoutPrompt()
    {
        var prompt = new ScriptedPrompt(true, "y");

        var run = Execute(MakeZip(("missing.txt", Hello)), _ => { }, dryRun: false, prompt: prompt);

        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
        Assert.Empty(prompt.Asked);
        Assert.Empty(run.Deletion.Calls);
    }

    // target の入力エラーでは ZIP 事前検証にも分類にも進まない
    [Fact]
    public void TargetInputError_StopsBeforeAnalysis()
    {
        var run = Execute(StandardZip(), fs => fs.Get(@"C:\target").MakeReparse(), dryRun: true);

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
