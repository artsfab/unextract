using System.IO;
using System.IO.Compression;
using System.Text;
using Unextract.Gui.Models;
using Unextract.Gui.Services;
using Unextract.Gui.ViewModels;
using static Unextract.Gui.Tests.JsonlReceiverTests;
using static Unextract.Gui.Tests.SearchSessionTests;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.Gui.Tests;

// The analysis queue, with a simulated runner and real-format JSONL through the receiver: the sequential queue (Archive order, then
// Target registration order; one at a time; continuing after a failure; independent of display filters; Archive, selection and
// single scopes); adopting only a normal completion (an interrupted entry, a mismatched exit code, an incompatible version or an
// unknown code is not adopted and goes through DisplayText); retiring the snapshot when a re-analysis starts, never reviving it after
// a failure or cancellation; nothing starts after a cancellation; settings locked while running while viewing and filtering go on;
// re-observing a missing Target and not running it; the mode-change confirmation discarding all results (candidates of the other mode
// are never reused); 64-bit length totals; empty and all-DIRECTORY ZIPs; progress from run.selected, the received count and the
// Target position; class and path filters independent of selection; a runner refusal stopping the queue. The bundled real CLI
// analyzes a self-made ZIP and target in Strict and Fast, the snapshot is adopted, and every file keeps its path, size, SHA-256 and
// last write time. A list of 300,000 entries keeps only the filtered indexes and builds no rows.
public sealed class AnalysisQueueTests
{
    [Fact]
    public async Task QueueRunsOneByOneInArchiveAndRegistrationOrderAndContinuesAfterFailure()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one", @"D:\two");
        runner.Handler = (job, _) => Task.FromResult(job.Target == @"D:\one" && job.Archive.EndsWith("A.zip", StringComparison.Ordinal)
            ? Failed(job) : Ok(job, ("a.txt", "MATCHED", 10), ("b.txt", "MODIFIED", 5)));
        var result = await model.AnalyzeSelectedAsync();
        Assert.Equal(new[] { (@"D:\archives\A.zip", @"D:\one"), (@"D:\archives\A.zip", @"D:\two"),
            (@"D:\archives\B.zip", @"D:\one"), (@"D:\archives\B.zip", @"D:\two") },
            runner.Jobs.Select(j => (j.Archive, j.Target)));
        Assert.Equal(1, runner.MaxConcurrent);
        Assert.All(runner.Jobs, j => Assert.Equal(CliOperation.Analyze, j.Operation));
        Assert.Equal(new AnalysisBatchResult(4, 3, 1, 0, false, 0), result);
        var first = model.Archives[0].Targets[0];
        Assert.False(first.HasSuccessfulAnalysis);
        Assert.Equal("解析失敗", first.StateText);
        Assert.Contains("CLIを実行しました。", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.True(model.Archives[0].Targets[1].HasSuccessfulAnalysis);
        Assert.False(model.IsBusy);
        Assert.False(model.IsAnalyzing);
    }

    [Fact]
    public async Task OnlyNormalCompletionIsAdoptedAndPartialEntriesAreNeverCandidates()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one");
        var target = model.Archives[0].Targets[0];
        // Entries arrived, but there is no result and the exit is abnormal.
        runner.Handler = (job, _) => Task.FromResult(Feed(job, 3, Line(new { v = 1, type = "run", operation = "analyze", mode = "strict",
            archive = job.Archive, target = job.Target, entries_total = 2, selected = 2, entries_option = false }) + Entry("MATCHED", 1)));
        await model.AnalyzeTargetAsync(model.Archives[0], target);
        Assert.False(target.HasSuccessfulAnalysis);
        Assert.Null(target.Snapshot);
        Assert.Contains("終了コード: 3", target.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains("resultを受信できませんでした", target.AnalysisDetailText, StringComparison.Ordinal);

        // A completed result whose exit code disagrees is an error: the real exit code wins and the result stays as detail.
        runner.Handler = (job, _) => Task.FromResult(Feed(job, 1, Run(job) + Entry() + Completed(job)));
        await model.AnalyzeTargetAsync(model.Archives[0], target);
        Assert.False(target.HasSuccessfulAnalysis);
        Assert.Contains("終了コード(1)とresult.exit_code(0)", target.AnalysisDetailText, StringComparison.Ordinal);

        // Incompatible output (unknown version) is an error even with exit 0.
        runner.Handler = (job, _) => Task.FromResult(Feed(job, 0, Line(new { v = 2, type = "run" })));
        await model.AnalyzeTargetAsync(model.Archives[0], target);
        Assert.False(target.HasSuccessfulAnalysis);
        Assert.Contains("非互換な出力", target.AnalysisDetailText, StringComparison.Ordinal);

        // Unknown error codes are shown, never branched on.
        runner.Handler = (job, _) => Task.FromResult(Feed(job, 1, Line(new { v = 1, type = "result", outcome = "input_error",
            exit_code = 1, error = new { stage = "usage", code = "FUTURE_CODE", message = "未知\u202Eの説明" } })));
        await model.AnalyzeTargetAsync(model.Archives[0], target);
        Assert.Contains("code=FUTURE_CODE", target.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains("未知\\u{202E}の説明", target.AnalysisDetailText, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202E', target.AnalysisDetailText);
    }

    [Fact]
    public async Task ReanalysisRetiresTheSnapshotAtStartAndFailureOrCancelNeverRestoresIt()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one");
        var archive = model.Archives[0];
        var target = archive.Targets[0];
        await model.AnalyzeTargetAsync(archive, target);
        var old = target.Snapshot;
        Assert.NotNull(old);
        Assert.Equal("再解析", target.AnalyzeButtonText);

        var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = (_, _) => gate.Task;
        var running = model.AnalyzeTargetAsync(archive, target);
        Assert.Null(target.Snapshot);
        Assert.False(target.HasSuccessfulAnalysis);
        Assert.Equal("解析中", target.StateText);
        gate.SetResult(Failed(runner.Jobs[^1]));
        await running;
        Assert.Null(target.Snapshot);
        Assert.Equal("解析失敗", target.StateText);

        runner.Handler = (job, token) => Task.FromResult(Cancelled(job));
        await model.AnalyzeTargetAsync(archive, target);
        Assert.Null(target.Snapshot);
        Assert.Contains("キャンセル", target.StateText, StringComparison.Ordinal);

        runner.Handler = null;
        await model.AnalyzeTargetAsync(archive, target);
        Assert.NotSame(old, target.Snapshot);
        Assert.True(target.HasSuccessfulAnalysis);
    }

    [Fact]
    public async Task CancelStopsTheCurrentJobAndNeverStartsLaterTargets()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one", @"D:\two");
        runner.Handler = async (job, token) =>
        {
            if (job.Target == @"D:\one" && job.Archive.EndsWith("A.zip", StringComparison.Ordinal)) return Ok(job);
            runner.Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { });
            return Cancelled(job);
        };
        var running = model.AnalyzeSelectedAsync();
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(model.IsAnalyzing);
        Assert.Equal(2, runner.Jobs.Count);
        model.CancelAnalysis();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Cancelled);
        Assert.Equal(2, runner.Jobs.Count);
        Assert.Equal(new AnalysisBatchResult(4, 1, 0, 0, true, 2), result);
        Assert.True(model.Archives[0].Targets[0].HasSuccessfulAnalysis);
        Assert.Contains("キャンセル", model.Archives[0].Targets[1].StateText, StringComparison.Ordinal);
        Assert.False(model.Archives[0].Targets[1].HasSuccessfulAnalysis);
        Assert.Equal("未解析", model.Archives[1].Targets[0].StateText);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task WholeSessionIsLockedWhileRunningButBrowsingAndCancelRemainAvailable()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one");
        var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = (_, _) => gate.Task;
        var running = model.AnalyzeSelectedAsync();
        Assert.True(model.IsBusy);
        Assert.False(model.IsSettingsEditable);
        Assert.False(model.Archives[0].Targets[0].CanEdit);
        Assert.False(model.Archives[0].Targets[0].CanAnalyze);
        model.Archives[0].Targets[0].IsSelected = false;
        Assert.True(model.Archives[0].Targets[0].IsSelected);
        model.SelectTargets(false, false);
        Assert.True(model.Archives[0].Targets[0].IsSelected);
        Assert.False(model.SetMode(CliMode.Fast, discardApproved: true));
        Assert.Equal(CliMode.Strict, model.Mode);
        Assert.Equal(AnalysisBatchResult.None, await model.AnalyzeSelectedAsync());
        Assert.Single(runner.Jobs);
        Assert.False(model.RemoveTarget(model.Archives[0], model.Archives[0].Targets[0]));
        Assert.False(await model.SearchAsync(true));
        model.ArchiveFilter = "B.zip";
        model.View(model.Archives[1].Targets[0]);
        Assert.Single(model.VisibleArchives);
        Assert.Same(model.Archives[1].Targets[0], model.Viewed);
        gate.SetResult(Ok(runner.Jobs[0]));
        await running;
        Assert.True(model.IsSettingsEditable);
        // Cancelling when nothing runs is harmless.
        model.CancelAnalysis();
    }

    [Fact]
    public async Task QueueScopesIgnoreFilterAndExcludeAnalyzedTargetsButExplicitReanalysisIsPossible()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one", @"D:\two");
        model.ArchiveFilter = "B.zip";
        model.Archives[0].Targets[1].IsSelected = false;
        await model.AnalyzeSelectedAsync();
        Assert.Equal(3, runner.Jobs.Count);
        Assert.DoesNotContain(runner.Jobs, j => j.Archive.EndsWith("A.zip", StringComparison.Ordinal) && j.Target == @"D:\two");
        runner.Jobs.Clear();
        // Archive scope: all its unanalyzed targets, regardless of selection.
        await model.AnalyzeArchiveAsync(model.Archives[0]);
        Assert.Equal(new[] { @"D:\two" }, runner.Jobs.Select(j => j.Target));
        runner.Jobs.Clear();
        Assert.Equal(AnalysisBatchResult.None, await model.AnalyzeArchiveAsync(model.Archives[0]));
        Assert.Equal(AnalysisBatchResult.None, await model.AnalyzeSelectedAsync());
        Assert.Empty(runner.Jobs);
        await model.AnalyzeTargetAsync(model.Archives[0], model.Archives[0].Targets[0]);
        Assert.Single(runner.Jobs);
        Assert.True(model.Archives[0].Targets[0].HasSuccessfulAnalysis);
    }

    [Fact]
    public async Task MissingTargetIsReevaluatedAtStartAndNotRunWhileStillMissing()
    {
        var search = new FakeSearch { Observation = new(TargetPresence.Missing) };
        var runner = new FakeRunner();
        var model = await Session(search, runner);
        await model.AddTargetsAsync(@"D:\later", model.Archives[0]);
        var target = model.Archives[0].Targets[0];
        Assert.False(target.CanAnalyze);
        var result = await model.AnalyzeSelectedAsync();
        Assert.Equal(new AnalysisBatchResult(1, 0, 0, 1, false, 0), result);
        Assert.Empty(runner.Jobs);
        Assert.Contains("存在しない", target.StateText, StringComparison.Ordinal);
        search.Observation = new(TargetPresence.Present);
        result = await model.AnalyzeSelectedAsync();
        Assert.Equal(1, result.Succeeded);
        Assert.Single(runner.Jobs);
        Assert.True(target.HasSuccessfulAnalysis);
    }

    [Fact]
    public async Task ModeChangeNeedsApprovalThenDiscardsEveryResultAndNeverReusesCandidates()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one");
        Assert.Equal(CliMode.Strict, model.Mode);
        Assert.Equal("", model.ModeWarning);
        await model.AnalyzeSelectedAsync();
        Assert.All(runner.Jobs, j => Assert.Equal(CliMode.Strict, j.Mode));
        Assert.Contains("MATCHED", model.Archives[0].Targets[0].Snapshot!.CandidateStatus, StringComparison.Ordinal);
        Assert.False(model.SetMode(CliMode.Fast));
        Assert.Equal(CliMode.Strict, model.Mode);
        Assert.True(model.Archives.All(a => a.Targets[0].HasSuccessfulAnalysis));
        Assert.True(model.SetMode(CliMode.Fast, discardApproved: true));
        Assert.Equal(CliMode.Fast, model.Mode);
        Assert.Contains("内容の一致を確認しません", model.ModeWarning, StringComparison.Ordinal);
        Assert.Contains("同じパス・同じサイズ", model.ModeWarning, StringComparison.Ordinal);
        Assert.Contains("正常に展開できる", model.ModeWarning, StringComparison.Ordinal);
        Assert.All(model.Archives, a => { Assert.False(a.Targets[0].HasSuccessfulAnalysis); Assert.Equal("未解析", a.Targets[0].StateText); });
        Assert.Equal("解析済みのTargetはありません。", model.AnalysisSummaryText);
        runner.Jobs.Clear();
        await model.AnalyzeSelectedAsync();
        Assert.All(runner.Jobs, j => Assert.Equal(CliMode.Fast, j.Mode));
        var snapshot = model.Archives[0].Targets[0].Snapshot!;
        Assert.Equal(CliMode.Fast, snapshot.Mode);
        Assert.Equal("SAME_SIZE", snapshot.CandidateStatus);
        Assert.StartsWith("解析済み (Fast): Same Size", model.Archives[0].Targets[0].StateText, StringComparison.Ordinal);
        // Asking for the current mode is not a change and needs no approval.
        Assert.True(model.SetMode(CliMode.Fast));
    }

    [Fact]
    public async Task ModeChangeWithoutResultsNeedsNoApproval()
    {
        var model = await Prepared(new FakeRunner(), @"D:\one");
        Assert.True(model.SetMode(CliMode.Fast));
        Assert.True(model.SetMode(CliMode.Strict));
        Assert.False(model.SetMode((CliMode)99));
        Assert.Equal(CliMode.Strict, model.Mode);
    }

    [Fact]
    public async Task SummariesCountSixtyFourBitLengthsWithoutOverflowAndNameLogicalSize()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one");
        runner.Handler = (job, _) => Task.FromResult(Ok(job, ("big1", "MATCHED", long.MaxValue), ("big2", "MATCHED", long.MaxValue),
            ("big3", "MATCHED", long.MaxValue), ("bad", "MODIFIED", 7), ("gone", "MISSING", 3), ("dir/", "DIRECTORY", 0)));
        await model.AnalyzeSelectedAsync();
        var snapshot = model.Archives[0].Targets[0].Snapshot!;
        Assert.Equal(3, snapshot.CandidateCount);
        Assert.Equal((UInt128)long.MaxValue * 3, snapshot.CandidateLength);
        Assert.Equal("Matched 3 / Modified 1 / Missing 1 / Skipped 0 / Directory 1", snapshot.SummaryText());
        Assert.Contains("解析済み 2 Targets", model.AnalysisSummaryText, StringComparison.Ordinal);
        Assert.Contains("削除候補サイズ（論理サイズの合計）", model.AnalysisSummaryText, StringComparison.Ordinal);
        Assert.Contains(((UInt128)long.MaxValue * 6).ToString("N0"), model.AnalysisSummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("空き容量", model.AnalysisSummaryText + model.AnalysisSummaryNote, StringComparison.Ordinal);
        Assert.Contains("物理的に解放される容量ではなく", model.AnalysisSummaryNote, StringComparison.Ordinal);
        model.RemoveTarget(model.Archives[1], model.Archives[1].Targets[0]);
        Assert.Contains("解析済み 1 Targets", model.AnalysisSummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyAndAllDirectoryArchivesAreNormalAnalysesWithZeroCandidates()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one");
        runner.Handler = (job, _) => Task.FromResult(job.Archive.EndsWith("A.zip", StringComparison.Ordinal)
            ? Ok(job) : Ok(job, ("d1/", "DIRECTORY", 0), ("d1/d2/", "DIRECTORY", 0)));
        await model.AnalyzeSelectedAsync();
        Assert.All(model.Archives, a =>
        {
            var snapshot = Assert.IsType<AnalysisSnapshot>(a.Targets[0].Snapshot);
            Assert.Equal(0, snapshot.CandidateCount);
            Assert.Equal<UInt128>(0, snapshot.CandidateLength);
        });
        Assert.Empty(model.Archives[0].Targets[0].VisibleResults);
        Assert.Equal(2, model.Archives[1].Targets[0].VisibleResults.Count);
        Assert.Contains("削除候補: 0 ファイル", model.Archives[0].Targets[0].AnalysisDetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressUsesRunSelectedAndReceivedEntriesAndTargetCounts()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one", @"D:\two");
        model.ProgressInterval = TimeSpan.FromMilliseconds(10);
        var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = (job, _) => runner.Jobs.Count == 2 ? gate.Task : Task.FromResult(Ok(job));
        var running = model.AnalyzeSelectedAsync();
        for (int i = 0; i < 500 && runner.Jobs.Count < 2; i++) await Task.Delay(10);
        Assert.Equal("2 / 4 Targets", model.OverallProgressText);
        Assert.Equal(@"D:\archives\A.zip", model.CurrentArchiveText);
        Assert.Equal(@"D:\two", model.CurrentTargetText);
        model.RefreshProgress();
        Assert.StartsWith("準備中", model.EntryProgressText, StringComparison.Ordinal);
        runner.Progress = new(CliStartState.Started, new("analyze", "strict", "a", "t", 842, 842, false), 421);
        model.RefreshProgress();
        Assert.Equal("確認中 421 / 842 エントリ", model.EntryProgressText);
        gate.SetResult(Ok(runner.Jobs[1]));
        await running;
        Assert.Equal("", model.OverallProgressText);
        Assert.Equal("", model.EntryProgressText);
    }

    [Fact]
    public async Task ResultFiltersAreIndependentOfSelectionAndKeepRawNamesAndClassifications()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one");
        runner.Handler = (job, _) => Task.FromResult(Ok(job, ("src/Ａ顔\u202E.txt", "MATCHED", 1_234_567), ("src/b.txt", "MODIFIED", 2),
            ("docs/c.txt", "MATCHED", 3), ("docs/gone.txt", "MISSING", 4), ("docs/", "DIRECTORY", 0)));
        await model.AnalyzeSelectedAsync();
        var target = model.Archives[0].Targets[0];
        Assert.Equal(new[] { "", "MATCHED", "MODIFIED", "MISSING", "SKIPPED_SPECIAL_FILE", "DIRECTORY" }, target.ResultCategories.Select(c => c.Value));
        Assert.Equal("MATCHED (2)", target.ResultCategories[1].Label);
        Assert.Equal(5, target.VisibleResults.Count);
        target.ResultCategory = "MATCHED";
        Assert.Equal(new[] { 1, 3 }, target.VisibleResults.Select(r => r.Entry.Index));
        target.ResultPathFilter = "SRC/";
        Assert.Equal(new[] { 1 }, target.VisibleResults.Select(r => r.Entry.Index));
        Assert.Equal("表示 1 / 5", target.ResultCountText);
        // Display conversion applies to the row only; the raw FullName is untouched.
        var row = target.VisibleResults[0];
        Assert.Equal("src/Ａ顔\\u{202E}.txt", row.DisplayName);
        Assert.Equal("src/Ａ顔\u202E.txt", row.Entry.Name);
        Assert.Equal(1234567L.ToString("N0"), row.LengthText);
        Assert.Equal(0, target.VisibleResults.IndexOf(row));
        Assert.Equal(-1, target.VisibleResults.IndexOf(new EntryRow(runner.Last!.Output.Entries[2])));
        Assert.True(target.IsSelected);
        Assert.True(model.Archives[0].IsSelected);
        target.ResultCategory = "";
        target.ResultPathFilter = "";
        Assert.Equal(5, target.VisibleResults.Count);
        // A new analysis starts with a clean view and does not keep a category from another snapshot.
        target.ResultCategory = "MISSING";
        await model.AnalyzeTargetAsync(model.Archives[0], target);
        Assert.Equal("", target.ResultCategory);
    }

    [Fact]
    public async Task EntryNamesInTheListAreShownWithoutInvisibleControlCharacters()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one");
        runner.Handler = (job, _) => Task.FromResult(Ok(job, ("a\u200B\u2066b.txt", "MATCHED", 1)));
        await model.AnalyzeSelectedAsync();
        string shown = model.Archives[0].Targets[0].VisibleResults[0].DisplayName;
        Assert.Equal("a\\u{200B}\\u{2066}b.txt", shown);
    }

    [Fact]
    public async Task RunnerRefusalStopsTheQueueAndMissingCliExplainsWithoutStartingAnything()
    {
        var runner = new FakeRunner();
        var model = await Prepared(runner, @"D:\one");
        runner.Handler = (_, _) => throw new InvalidOperationException("CLIは実行中、または以前のプロセスの終了を確認できていません。");
        var result = await model.AnalyzeSelectedAsync();
        Assert.Equal(1, result.Failed);
        Assert.Single(runner.Jobs);
        Assert.Equal(1, result.Unstarted);
        Assert.Equal("未解析", model.Archives[1].Targets[0].StateText);
        // The real runner refuses synchronously; that also stops the queue without starting anything.
        runner.Handler = null;
        runner.Jobs.Clear();
        runner.Refusal = "CLIは実行中、または以前のプロセスの終了を確認できていません。";
        result = await model.AnalyzeSelectedAsync();
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Unstarted);
        Assert.Empty(runner.Jobs);
        Assert.Contains("以前のプロセスの終了を確認できていません", model.Archives[0].Targets[0].AnalysisDetailText, StringComparison.Ordinal);
        runner.Refusal = null;

        var noCli = new MainViewModel(new("missing", false, "同梱CLIが見つかりません。"), new FakeSearch(), new FakeSettings(), runner)
            { SearchDirectory = @"D:\archives" };
        await noCli.SearchAsync();
        await noCli.AddTargetsAsync(@"D:\one");
        runner.Jobs.Clear();
        runner.Handler = null;
        Assert.Equal(AnalysisBatchResult.None, await noCli.AnalyzeSelectedAsync());
        Assert.Empty(runner.Jobs);
        Assert.Contains("同梱CLI", noCli.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BundledCliAnalysisThroughTheViewModelAdoptsAllEntriesAndLeavesFixtureUnchanged()
    {
        string fixture = ArchiveSearchTests.Fixture();
        string archive = Path.Combine(fixture, "内容 空白.zip");
        string target = Path.Combine(fixture, "target dir");
        Directory.CreateDirectory(target);
        byte[] bytes = [1, 2, 3];
        File.WriteAllBytes(Path.Combine(target, "same.txt"), bytes);
        File.WriteAllBytes(Path.Combine(target, "changed.txt"), [3, 2, 1]);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "same.txt", "changed.txt", "missing.txt" })
            {
                using var content = zip.CreateEntry(name).Open();
                content.Write(bytes);
            }
            zip.CreateEntry("dir/");
        }
        var before = Snapshot();
        var runner = new CliProcessRunner(new CliLocation(DeploymentTests.GuiOutputDirectory));
        var search = new FakeSearch { Result = new([new(archive, new FileInfo(archive).Length, DateTime.UtcNow)], []) };
        var model = new MainViewModel(new("cli", true, "ok"), search, new FakeSettings(), runner) { SearchDirectory = fixture };
        Assert.True(await model.SearchAsync());
        await model.AddTargetsAsync(target);
        var item = model.Archives[0].Targets[0];
        var strict = await model.AnalyzeSelectedAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, strict.Succeeded);
        Assert.Equal(new[] { "MATCHED", "MODIFIED", "MISSING", "DIRECTORY" }, item.Snapshot!.Entries.Select(e => e.Status));
        Assert.Equal(3, item.Snapshot.Entries.Count(e => e.Name != "dir/") );
        Assert.Equal((UInt128)3, item.Snapshot.CandidateLength);
        Assert.True(model.SetMode(CliMode.Fast, discardApproved: true));
        var fast = await model.AnalyzeSelectedAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, fast.Succeeded);
        Assert.Equal(new[] { "SAME_SIZE", "SAME_SIZE", "MISSING", "DIRECTORY" }, item.Snapshot!.Entries.Select(e => e.Status));
        Assert.Equal(before, Snapshot());
        (string Path, long Length, string Hash, DateTime Time)[] Snapshot() => Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => (path, new FileInfo(path).Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), File.GetLastWriteTimeUtc(path))).ToArray();
    }

    [Fact]
    public void FormattedSizesKeepTheExactLogicalByteCount()
    {
        Assert.Equal("0 bytes", SizeFormat.Bytes(0));
        Assert.Equal("1,023 bytes", SizeFormat.Bytes(1023));
        Assert.Equal("1,048,576 bytes (1 MiB)", SizeFormat.Bytes(1024 * 1024));
        Assert.StartsWith("18,446,744,073,709,551,615 bytes (16 EiB)", SizeFormat.Bytes(ulong.MaxValue), StringComparison.Ordinal);
    }

    private static async Task<MainViewModel> Prepared(FakeRunner runner, params string[] targets)
    {
        var model = await Session(new FakeSearch(), runner);
        foreach (string target in targets) await model.AddTargetsAsync(target);
        return model;
    }

    internal static CliJobResult Ok(CliJob job, params (string Name, string Status, long Length)[] entries)
    {
        var text = new StringBuilder(Run(job, entries.Length));
        var counts = new Dictionary<string, int>();
        string candidate = job.Mode == CliMode.Strict ? "matched" : "same_size";
        foreach (string key in new[] { candidate, "modified", "missing", "skipped_special_file", "directory" }) counts[key] = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            string status = entries[i].Status == "MATCHED" && job.Mode == CliMode.Fast ? "SAME_SIZE" : entries[i].Status;
            text.Append(Entry(status, i + 1, entries[i].Length, entries[i].Name));
            counts[status.ToLowerInvariant()]++;
        }
        text.Append(Line(new { v = 1, type = "result", outcome = "completed", exit_code = 0, counts }));
        return Feed(job, 0, text.ToString());
    }

    private static CliJobResult Feed(CliJob job, int exit, string text)
    {
        var receiver = new JsonlReceiver(job);
        receiver.Feed(Encoding.ASCII.GetBytes(text));
        return new(CliStartState.Started, true, exit, true, true, false, receiver.Finish(), "", false, null);
    }

    internal static CliJobResult Failed(CliJob job) => Feed(job, 1, Line(new
    {
        v = 1, type = "result", outcome = "input_error", exit_code = 1,
        error = new { stage = "prepare", code = "TARGET_NOT_FOUND", message = "Targetがありません" },
    }));

    internal static CliJobResult Cancelled(CliJob job) =>
        new(CliStartState.Started, true, 1, true, true, true, new JsonlReceiver(job).Finish(), "", false, null);

    internal sealed class FakeRunner : ICliProcessRunner
    {
        private int _active;
        public List<CliJob> Jobs { get; } = [];
        public Func<CliJob, CancellationToken, Task<CliJobResult>>? Handler { get; set; }
        public CliProgress Progress { get; set; } = new(CliStartState.BeforeStartFailure, null, 0);
        public CliJobResult? Last { get; private set; }
        public int MaxConcurrent { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Like CliProcessRunner, a refusal is thrown synchronously, before any process exists.
        public string? Refusal { get; set; }

        public Task<CliJobResult> RunAsync(CliJob job, CancellationToken cancellation = default)
        {
            if (Refusal is not null) throw new InvalidOperationException(Refusal);
            return RunCoreAsync(job, cancellation);
        }

        private async Task<CliJobResult> RunCoreAsync(CliJob job, CancellationToken cancellation)
        {
            Jobs.Add(job);
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _active));
            try
            {
                var result = Handler is null ? Ok(job, ("a.txt", "MATCHED", 10)) : await Handler(job, cancellation);
                Last = result;
                return result;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    [Fact]
    public void LargeSnapshotFiltersByIndexWithoutMaterializingRows()
    {
        const int total = 300_000;
        var entries = new List<CliEntry>(total);
        for (int i = 1; i <= total; i++)
            entries.Add(new(i, $"dir{i % 100}/file{i}.bin", false, i, i % 3 == 0 ? "MODIFIED" : "MATCHED", null, null, null));
        var run = new CliRun("analyze", "strict", @"C:\a.zip", @"C:\t", total, total, false);
        var output = new CliOutput(run, entries, new("completed", 0, null, null), ProtocolIssue.None, null);
        var snapshot = AnalysisSnapshot.Create(CliMode.Strict, output);
        Assert.Equal(total - total / 3, snapshot.CandidateCount);
        var rows = new EntryRowList(snapshot.Entries, Enumerable.Range(0, total).Where(i => i % 1000 == 0).ToArray());
        Assert.Equal(total / 1000, rows.Count);
        Assert.Equal(1, rows[0].Entry.Index);
        Assert.Equal(299_001, rows[^1].Entry.Index);
        Assert.Equal(rows.Count - 1, rows.IndexOf(rows[^1]));
    }
}
