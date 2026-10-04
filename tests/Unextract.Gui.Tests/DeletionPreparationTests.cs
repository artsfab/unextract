using System.IO;
using System.Text;
using Unextract.Gui.Models;
using Unextract.Gui.Services;
using Unextract.Gui.ViewModels;
using static Unextract.Gui.Tests.AnalysisQueueTests;
using static Unextract.Gui.Tests.JsonlReceiverTests;
using static Unextract.Gui.Tests.SearchSessionTests;

namespace Unextract.Gui.Tests;

public sealed class DeletionPreparationTests
{
    private static readonly (string, string, long)[] Analysis =
    [
        ("sub/Ａ顔.txt", "MATCHED", 10), ("sub/changed.txt", "MODIFIED", 3), ("Case/File.TXT", "MATCHED", 5),
        ("gone.txt", "MISSING", 7), ("sub/", "DIRECTORY", 0),
    ];

    [Fact]
    public async Task PlanFixesSelectedEligibleTargetsAndCandidatesIgnoringFilterAndShowsExclusions()
    {
        var f = await Fixture(@"D:\one", @"D:\two", @"D:\three");
        var a = f.Model.Archives[0];
        await f.Model.AnalyzeTargetAsync(a, a.Targets[0]);
        await f.Model.AnalyzeTargetAsync(a, a.Targets[1]);
        // The third was never analyzed; B is entirely unanalyzed. Hide A with the display filter: no effect on the plan.
        f.Model.ArchiveFilter = "B.zip";
        var plan = f.Model.PlanDeletion()!;
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(new[] { @"D:\one", @"D:\two" }, plan.Items.Select(i => i.TargetPath));
        Assert.Equal(new[] { 1, 2 }, plan.Items.Select(i => i.Number));
        Assert.Equal(4, plan.Excluded.Count);
        Assert.All(plan.Excluded, x => Assert.Contains("未解析", x.Reason, StringComparison.Ordinal));
        var first = plan.Items[0];
        // Strict: MATCHED files only, in ZIP order, with the raw FullName, index and 64-bit length.
        Assert.Equal(new[] { "sub/Ａ顔.txt", "Case/File.TXT" }, first.Candidates.Select(c => c.Name));
        Assert.Equal(new[] { 1, 3 }, first.Candidates.Select(c => c.Index));
        Assert.Equal<UInt128>(15, first.CandidateLength);
        Assert.Equal(4, plan.TotalCandidates);
        // Later changes do not alter what was planned.
        a.Targets[0].IsSelected = false;
        Assert.Equal(2, plan.Items.Count);
        Assert.Same(first.Snapshot, a.Targets[0].Snapshot);
    }

    [Fact]
    public async Task ExclusionsCoverMissingZeroCandidatesAndConsumedTargets()
    {
        var f = await Fixture(@"D:\one");
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Delete ? DeleteOk(job)
            : job.Archive.EndsWith("A.zip", StringComparison.Ordinal) ? Ok(job, ("only-directory/", "DIRECTORY", 0)) : Ok(job, Analysis));
        await f.Model.AnalyzeSelectedAsync();
        var plan = f.Model.PlanDeletion()!;
        Assert.Single(plan.Items);
        Assert.Equal("削除候補が0件です。", Assert.Single(plan.Excluded).Reason);

        f.Search.Observation = new(TargetPresence.Missing);
        var b = f.Model.Archives[1].Targets[0];
        await f.Model.RefreshTargetAsync(b);
        plan = f.Model.PlanDeletion()!;
        Assert.Empty(plan.Items);
        Assert.Contains(plan.Excluded, x => x.Reason == "Targetが存在しません。");
        f.Search.Observation = new(TargetPresence.Present);
        await f.Model.RefreshTargetAsync(b);

        plan = f.Model.PlanDeletion()!;
        var result = await f.Model.DeleteAsync(plan, approved: true);
        Assert.Equal(DeletionStop.None, result.Stop);
        Assert.True(b.HasDeleteStarted);
        plan = f.Model.PlanDeletion()!;
        Assert.Empty(plan.Items);
        Assert.Contains(plan.Excluded, x => x.Reason.Contains("削除実行済み", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmationNamesCountsSizeIrreversibilityAndFastRisksAndDeclineStartsNothing()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        var plan = f.Model.PlanDeletion()!;
        string text = f.Model.ConfirmationText(plan);
        Assert.Contains("Target数: 2", text, StringComparison.Ordinal);
        Assert.Contains("解析時点の最大件数", text, StringComparison.Ordinal);
        Assert.Contains("4", text, StringComparison.Ordinal);
        Assert.Contains("削除されないファイルがあり得ます", text, StringComparison.Ordinal);
        Assert.Contains("削除候補サイズ（論理サイズの合計）: 30 bytes", text, StringComparison.Ordinal);
        Assert.Contains("ごみ箱は使わず、完全に削除します", text, StringComparison.Ordinal);
        Assert.Contains("元に戻りません", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fastは内容", text, StringComparison.Ordinal);

        var jobs = f.Runner.Jobs.Count;
        var declined = await f.Model.DeleteAsync(plan, approved: false);
        Assert.Equal(DeletionStop.Declined, declined.Stop);
        Assert.Equal(jobs, f.Runner.Jobs.Count);
        Assert.Empty(f.Entries.Created);
        Assert.Empty(f.Logs.Reserved);
        Assert.All(f.Model.Archives, a => { Assert.False(a.Targets[0].HasDeleteStarted); Assert.True(a.Targets[0].HasSuccessfulAnalysis); });

        Assert.True(f.Model.SetMode(CliMode.Fast, discardApproved: true));
        await f.Model.AnalyzeSelectedAsync();
        string fast = f.Model.ConfirmationText(f.Model.PlanDeletion()!);
        Assert.Contains("内容の一致を確認しません", fast, StringComparison.Ordinal);
        Assert.Contains("同じパス・同じサイズ", fast, StringComparison.Ordinal);
        Assert.Contains("正常に展開できる", fast, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovedBatchPassesExactEntriesLogAndModeAndCleansUpOwnedEntriesAfterEachProcess()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        // A file that was not a candidate at analysis time is never added, whatever it looks like now.
        var plan = f.Model.PlanDeletion()!;
        var deleteJobs = new List<(string Entries, string[] Names, string Log)>();
        f.Runner.Handler = (job, _) =>
        {
            if (job.Operation == CliOperation.Delete)
            {
                // The temporary file must still exist while the CLI runs.
                Assert.Contains(job.Entries!, f.Entries.Live);
                deleteJobs.Add((job.Entries!, f.Entries.Created[^1], job.Log!));
                Assert.Equal(plan.Items[deleteJobs.Count - 1].Number, f.Logs.Reserved[^1].Number);
            }
            return Task.FromResult(job.Operation == CliOperation.Delete ? DeleteOk(job) : Ok(job, Analysis));
        };
        var result = await f.Model.DeleteAsync(plan, approved: true);
        Assert.Equal(new DeletionBatchResult(DeletionStop.None, 2, 2, 2), result);
        Assert.Equal(2, deleteJobs.Count);
        Assert.All(deleteJobs, d => Assert.Equal(new[] { "sub/Ａ顔.txt", "Case/File.TXT" }, d.Names));
        Assert.Equal(new[] { @"E:\logs\delete-1.jsonl", @"E:\logs\delete-2.jsonl" }, deleteJobs.Select(d => d.Log));
        Assert.Single(f.Logs.Reserved.Select(r => r.Start).Distinct());
        var jobs = f.Runner.Jobs.Where(j => j.Operation == CliOperation.Delete).ToArray();
        Assert.All(jobs, j => { Assert.Equal(CliMode.Strict, j.Mode); Assert.NotNull(j.Entries); Assert.NotNull(j.Log); });
        Assert.Equal(new[] { @"D:\archives\A.zip", @"D:\archives\B.zip" }, jobs.Select(j => j.Archive));
        // Entries were removed only after the process ended, and only our own files.
        Assert.Empty(f.Entries.Live);
        Assert.Equal(deleteJobs.Select(d => d.Entries), f.Entries.Deleted);
        Assert.All(f.Model.Archives, a =>
        {
            var t = a.Targets[0];
            Assert.True(t.HasDeleteStarted);
            Assert.True(t.SnapshotConsumed);
            Assert.Contains("削除完了", t.StateText, StringComparison.Ordinal);
            Assert.False(t.CanEdit);
        });
        Assert.False(f.Model.IsBusy);
        Assert.False(f.Model.IsDeleting);
        // Re-delete before re-analysis is impossible; a new successful analysis re-enables it.
        Assert.Empty(f.Model.PlanDeletion()!.Items);
        Assert.Equal(DeletionStop.StalePlan, (await f.Model.DeleteAsync(plan, approved: true)).Stop);
        await f.Model.AnalyzeTargetAsync(f.Model.Archives[0], f.Model.Archives[0].Targets[0]);
        Assert.Single(f.Model.PlanDeletion()!.Items);
        Assert.True(f.Model.Archives[0].Targets[0].HasDeleteStarted);
    }

    [Fact]
    public async Task UnstartedCliKeepsAnalysisRerunnableAndStopsTheQueueWithoutMarkingDeleted()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Delete ? NotStartedResult(job) : Ok(job));
        var plan = f.Model.PlanDeletion()!;
        var result = await f.Model.DeleteAsync(plan, approved: true);
        Assert.Equal(DeletionStop.NotStarted, result.Stop);
        Assert.Equal(0, result.Launched);
        Assert.Single(f.Runner.Jobs, j => j.Operation == CliOperation.Delete);
        var first = f.Model.Archives[0].Targets[0];
        var second = f.Model.Archives[1].Targets[0];
        Assert.False(first.HasDeleteStarted);
        Assert.True(first.HasSuccessfulAnalysis);
        Assert.Contains("削除処理は開始されていません", first.StateText, StringComparison.Ordinal);
        Assert.Contains("未実行", second.StateText, StringComparison.Ordinal);
        Assert.Empty(f.Entries.Live);
        Assert.Equal(2, f.Model.PlanDeletion()!.Items.Count);
        // The same retained analysis can be deleted again without re-analysis.
        f.Runner.Handler = (job, _) => Task.FromResult(DeleteOk(job));
        var again = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(2, again.Completed);
    }

    [Fact]
    public async Task PreparationFailuresAreUnstartedStopTheQueueAndKeepAnalysis()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        int analyses = f.Runner.Jobs.Count;
        f.Logs.Error = "ログの場所を作れません";
        var result = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(DeletionStop.NotStarted, result.Stop);
        Assert.Equal(analyses, f.Runner.Jobs.Count);
        Assert.Empty(f.Entries.Created);
        var first = f.Model.Archives[0].Targets[0];
        Assert.Contains("実行ログの保存場所", first.StateText, StringComparison.Ordinal);
        Assert.Contains("ログは作成していません", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.False(first.HasDeleteStarted);

        f.Logs.Error = null;
        f.Entries.Error = "disk full";
        f.Entries.OwnedOnError = true;
        result = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(DeletionStop.NotStarted, result.Stop);
        Assert.Equal(analyses, f.Runner.Jobs.Count);
        Assert.Contains("一時entriesファイル", first.StateText, StringComparison.Ordinal);
        // Our partially written file is cleaned, and nothing else.
        Assert.Empty(f.Entries.Live);
        Assert.Single(f.Entries.Deleted);
        Assert.False(first.HasDeleteStarted);
        Assert.True(first.HasSuccessfulAnalysis);
        Assert.Equal(2, f.Model.PlanDeletion()!.Items.Count);
    }

    [Fact]
    public async Task EntriesFileLimitIsInclusiveAndOverrunIsUnstartedWithAnalysisKeptAndLaterTargetsUnrun()
    {
        var f = await Fixture(@"D:\one");
        string big = new string('あ', 44_739_242);
        // 3 bytes x 44,739,242 + 'a' + LF = exactly 134,217,728 bytes: allowed. One more byte: not started.
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Delete ? DeleteOk(job, [(1, big + "a", 1)], 1)
            : Large(job, job.Archive.EndsWith("A.zip", StringComparison.Ordinal) ? big + "a" : big + "aa"));
        await f.Model.AnalyzeSelectedAsync();
        Assert.All(f.Model.Archives, a => Assert.True(a.Targets[0].HasSuccessfulAnalysis));
        var result = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(DeletionStop.NotStarted, result.Stop);
        Assert.Equal(new DeletionBatchResult(DeletionStop.NotStarted, 2, 1, 1), result);
        var a1 = f.Model.Archives[0].Targets[0];
        var b1 = f.Model.Archives[1].Targets[0];
        Assert.True(a1.HasDeleteStarted);
        Assert.False(b1.HasDeleteStarted);
        Assert.True(b1.HasSuccessfulAnalysis);
        Assert.Contains("削除対象一覧がCLIの上限を超えるため、削除処理を開始できません", b1.StateText, StringComparison.Ordinal);
        Assert.Contains("再解析では解消しません", b1.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Single(f.Entries.Created);
        Assert.Single(f.Runner.Jobs, j => j.Operation == CliOperation.Delete);
        Assert.Equal(EntriesFormat.MaxBytes, 3L * 44_739_242 + 1 + 1);
    }

    [Fact]
    public async Task UnusableNamesAreNotStartedInsteadOfBeingReplaced()
    {
        var f = await Fixture(@"D:\one");
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Delete ? DeleteOk(job) : Large(job, "bad\uD800name"));
        await f.Model.AnalyzeSelectedAsync();
        var result = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(DeletionStop.NotStarted, result.Stop);
        Assert.Empty(f.Entries.Created);
        Assert.Contains("UTF-8", f.Model.Archives[0].Targets[0].StateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorEndStopsLaterTargetsAndCleanupFailureOnlyAddsANotice()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        f.Entries.DeleteError = "locked by another program";
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Delete ? ErrorEnd(job) : Ok(job));
        var result = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(new DeletionBatchResult(DeletionStop.Error, 2, 1, 0), result);
        var first = f.Model.Archives[0].Targets[0];
        var second = f.Model.Archives[1].Targets[0];
        Assert.True(first.HasDeleteStarted);
        Assert.Contains("エラー終了", first.StateText, StringComparison.Ordinal);
        Assert.Contains("locked by another program", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains(f.Model.Diagnostics, d => d.Message.Contains("locked by another program", StringComparison.Ordinal));
        Assert.False(second.HasDeleteStarted);
        Assert.Contains("未実行", second.StateText, StringComparison.Ordinal);
        Assert.True(second.HasSuccessfulAnalysis);
    }

    [Fact]
    public async Task EntriesFileIsKeptWhileProcessExitIsUnconfirmed()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Delete ? ExitUnknown(job) : Ok(job));
        var result = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(DeletionStop.Error, result.Stop);
        Assert.Single(f.Entries.Live);
        Assert.Empty(f.Entries.Deleted);
        Assert.True(f.Model.Archives[0].Targets[0].HasDeleteStarted);
        Assert.Contains("終了を確認できない", f.Model.Archives[0].Targets[0].AnalysisDetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitRequestFinishesTheCurrentTargetWithoutKillingAndStartsNoMore()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runner.Handler = (job, _) => job.Operation == CliOperation.Delete ? gate.Task : Task.FromResult(Ok(job));
        var running = f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.True(f.Model.IsDeleting);
        Assert.True(f.Model.IsJobRunning);
        Assert.False(f.Model.IsAnalyzing);
        // Everything is locked, and the only cancel path (analysis) does nothing for a delete.
        Assert.Equal(AnalysisBatchResult.None, await f.Model.AnalyzeSelectedAsync());
        Assert.False(f.Model.SetMode(CliMode.Fast, discardApproved: true));
        f.Model.CancelAnalysis();
        f.Model.RequestExit();
        Assert.Contains("現在のTargetの処理が終わり次第終了します", f.Model.Status, StringComparison.Ordinal);
        Assert.False(running.IsCompleted);
        gate.SetResult(DeleteOk(f.Runner.Jobs[^1]));
        var result = await running;
        Assert.Equal(new DeletionBatchResult(DeletionStop.ExitRequested, 2, 1, 1), result);
        Assert.Single(f.Runner.Jobs, j => j.Operation == CliOperation.Delete);
        Assert.Contains("未実行", f.Model.Archives[1].Targets[0].StateText, StringComparison.Ordinal);
        Assert.False(f.Model.Archives[1].Targets[0].HasDeleteStarted);
    }

    [Fact]
    public async Task StalePlanAfterReanalysisOrModeChangeIsRefusedAndNothingStarts()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        var plan = f.Model.PlanDeletion()!;
        await f.Model.AnalyzeTargetAsync(f.Model.Archives[0], f.Model.Archives[0].Targets[0]);
        Assert.Equal(DeletionStop.StalePlan, (await f.Model.DeleteAsync(plan, approved: true)).Stop);
        plan = f.Model.PlanDeletion()!;
        Assert.True(f.Model.SetMode(CliMode.Fast, discardApproved: true));
        Assert.Equal(DeletionStop.StalePlan, (await f.Model.DeleteAsync(plan, approved: true)).Stop);
        Assert.DoesNotContain(f.Runner.Jobs, j => j.Operation == CliOperation.Delete);
        Assert.Empty(f.Entries.Created);
        Assert.Equal(DeletionStop.NothingToDelete, (await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true)).Stop);
    }

    [Fact]
    public void RealEntriesFileHasExactRawNamesUtf8WithoutBomAndLfAndOnlyOwnedFilesAreRemoved()
    {
        string dir = ArchiveSearchTests.Fixture("entries-store");
        var store = new EntriesStore(dir);
        string[] names = ["Case/File.TXT", "日本語/顔-\U0001F642.txt", "back\\slash", " leading and trailing ", "a\u200Bb"];
        var created = store.Create(names);
        Assert.Null(created.Error);
        var owned = Assert.IsType<OwnedEntriesFile>(created.Owned);
        Assert.Equal(dir, Path.GetDirectoryName(owned.Path));
        Assert.Matches(@"^unextract-entries-[0-9a-f]{32}\.txt$", Path.GetFileName(owned.Path));
        byte[] bytes = File.ReadAllBytes(owned.Path);
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(string.Concat(names.Select(n => n + "\n")), new UTF8Encoding(false, true).GetString(bytes));
        Assert.Equal((long)bytes.Length, EntriesFormat.TryMeasure(names, out long measured) ? measured : -1);

        // A second file is a different unique name; a not-owned path is never deleted.
        var other = store.Create(["x"]);
        Assert.NotEqual(owned.Path, other.Owned!.Path);
        string foreign = Path.Combine(dir, "foreign.txt");
        File.WriteAllText(foreign, "keep");
        var impostor = new OwnedEntriesFile(foreign);
        Assert.NotNull(store.Delete(impostor));
        Assert.True(File.Exists(foreign));

        // Deleting reports a locked file with its path and keeps ownership for a later attempt.
        using (new FileStream(owned.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            string? error = store.Delete(owned);
            Assert.NotNull(error);
            Assert.Contains(owned.Path, error, StringComparison.Ordinal);
            Assert.True(File.Exists(owned.Path));
        }
        Assert.Null(store.Delete(owned));
        Assert.False(File.Exists(owned.Path));
        Assert.Null(store.Delete(other.Owned!));
    }

    [Fact]
    public void PartiallyWrittenEntriesFileIsOwnedForCleanupAndAnUnwritableDirectoryOwnsNothing()
    {
        string dir = ArchiveSearchTests.Fixture("entries-store-failure");
        var store = new EntriesStore(dir);
        var partial = store.Create(["ok", "bad\uD800name"]);
        Assert.NotNull(partial.Error);
        var owned = Assert.IsType<OwnedEntriesFile>(partial.Owned);
        Assert.True(File.Exists(owned.Path));
        Assert.Null(store.Delete(owned));
        Assert.False(File.Exists(owned.Path));

        string notADirectory = Path.Combine(dir, "plain-file");
        File.WriteAllText(notADirectory, "x");
        var failed = new EntriesStore(notADirectory).Create(["x"]);
        Assert.Null(failed.Owned);
        Assert.NotNull(failed.Error);
    }

    [Fact]
    public void LogNamesUseBatchStartAndNumberAliasExistingNamesAndNeverCreateTheFile()
    {
        string dir = Path.Combine(ArchiveSearchTests.Fixture("logs"), "nested", "logs");
        var logs = new LogLocation(dir);
        var start = new DateTime(2026, 10, 8, 3, 4, 5, DateTimeKind.Utc);
        var first = logs.Reserve(start, 1);
        Assert.Null(first.Error);
        Assert.Equal(Path.Combine(dir, "delete-20261008-030405-001.jsonl"), first.Path);
        Assert.True(Directory.Exists(dir));
        Assert.False(File.Exists(first.Path));
        Assert.Equal(Path.Combine(dir, "delete-20261008-030405-1234.jsonl"), logs.Reserve(start, 1234).Path);
        // A same-named log (restart, second instance) is never reused: an alias with a unique suffix is chosen.
        File.WriteAllText(first.Path!, "existing log");
        var alias = logs.Reserve(start, 1);
        Assert.NotEqual(first.Path, alias.Path);
        Assert.Matches(@"delete-20261008-030405-001-[0-9a-f]{32}\.jsonl$", alias.Path!);
        Assert.Equal("existing log", File.ReadAllText(first.Path!));
        Assert.False(File.Exists(alias.Path));
        Assert.Equal(Path.Combine(dir, "delete-20261008-030405-002.jsonl"), logs.Reserve(start.ToLocalTime(), 2).Path);

        string blocker = Path.Combine(ArchiveSearchTests.Fixture("logs-blocked"), "file");
        File.WriteAllText(blocker, "x");
        var blocked = new LogLocation(Path.Combine(blocker, "logs")).Reserve(start, 1);
        Assert.Null(blocked.Path);
        Assert.NotNull(blocked.Error);
    }

    internal static async Task<Setup> Fixture(params string[] targets)
    {
        var runner = new FakeRunner();
        var search = new FakeSearch();
        var entries = new FakeEntries();
        var logs = new FakeLogs();
        var model = new MainViewModel(new("cli", true, "available"), search, new FakeSettings(), runner, entries, logs)
            { SearchDirectory = @"D:\archives" };
        Assert.True(await model.SearchAsync());
        foreach (string target in targets) await model.AddTargetsAsync(target);
        runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Analyze ? Ok(job, Analysis) : DeleteOk(job));
        return new(model, runner, search, entries, logs);
    }

    private static CliJobResult Large(CliJob job, string name)
    {
        var entries = new[] { new CliEntry(1, name, false, 1, job.Mode == CliMode.Strict ? "MATCHED" : "SAME_SIZE", null, null, null) };
        var counts = new Dictionary<string, int> { [job.Mode == CliMode.Strict ? "matched" : "same_size"] = 1, ["modified"] = 0,
            ["missing"] = 0, ["skipped_special_file"] = 0, ["directory"] = 0 };
        var run = new CliRun("analyze", job.ModeValue, job.Archive, job.Target, 1, 1, false);
        var output = new CliOutput(run, entries, new("completed", 0, counts, null), ProtocolIssue.None, null);
        return new(CliStartState.Started, true, 0, true, true, false, output, "", false, null);
    }

    // The approved candidates of the Analysis fixture as the CLI reports them: FullName and ZIP index (5 entries).
    private static readonly (int Index, string Name, long Length)[] AnalysisCandidates = [(1, "sub/Ａ顔.txt", 10), (3, "Case/File.TXT", 5)];

    internal static CliJobResult DeleteOk(CliJob job) => DeleteOk(job, AnalysisCandidates, Analysis.Length);

    // A normal delete of every given entry. Built directly (not through JSON) so very long names stay cheap.
    internal static CliJobResult DeleteOk(CliJob job, IReadOnlyList<(int Index, string Name, long Length)> deleted, int total)
    {
        var entries = deleted.Select(d => new CliEntry(d.Index, d.Name, false, d.Length, "DELETED", null, null, null)).ToArray();
        var counts = new Dictionary<string, int> { ["deleted"] = entries.Length, ["modified"] = 0, ["missing"] = 0,
            ["skipped_special_file"] = 0, ["directory"] = 0, ["delete_failed"] = 0, ["not_selected"] = total - entries.Length, ["unprocessed"] = 0 };
        var run = new CliRun("delete", job.ModeValue, job.Archive, job.Target, total, entries.Length, true);
        var output = new CliOutput(run, entries, new("completed", 0, counts, null), ProtocolIssue.None, null);
        return new(CliStartState.Started, true, 0, true, true, false, output, "", false, null);
    }

    private static CliJobResult ErrorEnd(CliJob job)
    {
        var receiver = new JsonlReceiver(job);
        receiver.Feed(Encoding.ASCII.GetBytes(Run(job, Analysis.Length, 2) + Line(new { v = 1, type = "result", outcome = "internal_error", exit_code = 1,
            error = new { stage = "internal", code = "INTERNAL", deletion_started = true, message = "boom" },
            counts = new { deleted = 0, modified = 0, missing = 0, skipped_special_file = 0, directory = 0, delete_failed = 0, not_selected = 3, unprocessed = 2 } })));
        return new(CliStartState.Started, true, 1, true, true, false, receiver.Finish(), "", false, null);
    }

    private static CliJobResult StartUnknown(CliJob job) =>
        new(CliStartState.Unknown, false, null, false, false, false, new JsonlReceiver(job).Finish(), "", false, "CLIの起動成否を確認できません: test");

    private static CliJobResult ExitUnknown(CliJob job) =>
        new(CliStartState.Started, false, null, true, true, false, new JsonlReceiver(job).Finish(), "", false, "CLIの実終了を確認できません。");

    private static CliJobResult NotStartedResult(CliJob job) =>
        new(CliStartState.NotStarted, false, null, false, false, false, new JsonlReceiver(job).Finish(), "", false, "OSはCLIプロセスを開始しませんでした。");

    internal sealed record Setup(MainViewModel Model, FakeRunner Runner, FakeSearch Search, FakeEntries Entries, FakeLogs Logs);

    internal sealed class FakeEntries : IEntriesStore
    {
        public List<string[]> Created { get; } = [];
        public HashSet<string> Live { get; } = [];
        public List<string> Deleted { get; } = [];
        public string? Error { get; set; }
        public bool OwnedOnError { get; set; }
        public string? DeleteError { get; set; }
        private int _next;

        public EntriesCreation Create(IReadOnlyList<string> names)
        {
            if (Error is not null && !OwnedOnError) return new(null, Error);
            var owned = new OwnedEntriesFile($@"C:\temp\unextract-entries-{++_next:D32}.txt");
            Live.Add(owned.Path);
            Created.Add(names.ToArray());
            return new(owned, Error);
        }

        public string? Delete(OwnedEntriesFile file)
        {
            if (DeleteError is not null) return DeleteError;
            Assert.True(Live.Remove(file.Path));
            Deleted.Add(file.Path);
            return null;
        }
    }

    internal sealed class FakeLogs : ILogLocation
    {
        public string Directory => @"E:\logs";
        public string? Error { get; set; }
        public List<(DateTime Start, int Number)> Reserved { get; } = [];
        public LogReservation Reserve(DateTime batchStartUtc, int number)
        {
            if (Error is not null) return new(null, Directory, Error);
            Reserved.Add((batchStartUtc, number));
            return new($@"E:\logs\delete-{number}.jsonl", Directory, null);
        }
    }

    [Fact]
    public async Task BundledCliDeletesOnlyApprovedMatchedFilesFromRealEntriesAndWritesTheNamedLog()
    {
        string fixture = ArchiveSearchTests.Fixture("delete-real");
        string archive = Path.Combine(fixture, "内容 空白.zip");
        string target = Path.Combine(fixture, "target dir");
        string logs = Path.Combine(fixture, "logs");
        string temp = Directory.CreateDirectory(Path.Combine(fixture, "temp")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "Case"));
        byte[] bytes = [1, 2, 3];
        string unicode = "Ａ顔-\U0001F642.txt";
        File.WriteAllBytes(Path.Combine(target, unicode), bytes);
        File.WriteAllBytes(Path.Combine(target, "Case", "File.TXT"), bytes);
        File.WriteAllBytes(Path.Combine(target, "changed.txt"), [3, 2, 1]);
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (string name in new[] { unicode, "Case/File.TXT", "changed.txt", "missing.txt" })
            {
                using var content = zip.CreateEntry(name).Open();
                content.Write(bytes);
            }
        }
        var runner = new CliProcessRunner(new CliLocation(DeploymentTests.GuiOutputDirectory));
        var search = new FakeSearch { Result = new([new(archive, new FileInfo(archive).Length, DateTime.UtcNow)], []) };
        var model = new MainViewModel(new("cli", true, "ok"), search, new FakeSettings(), runner, new EntriesStore(temp), new LogLocation(logs))
            { SearchDirectory = fixture };
        Assert.True(await model.SearchAsync());
        await model.AddTargetsAsync(target);
        Assert.Equal(1, (await model.AnalyzeSelectedAsync().WaitAsync(TimeSpan.FromSeconds(60))).Succeeded);
        // A file that becomes identical to the ZIP after analysis was not a candidate and must not be touched.
        File.WriteAllBytes(Path.Combine(target, "changed.txt"), bytes);
        var plan = model.PlanDeletion()!;
        Assert.Equal(new[] { unicode, "Case/File.TXT" }, plan.Items.Single().Candidates.Select(c => c.Name));
        var result = await model.DeleteAsync(plan, approved: true).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(new DeletionBatchResult(DeletionStop.None, 1, 1, 1), result);
        Assert.False(File.Exists(Path.Combine(target, unicode)));
        Assert.False(File.Exists(Path.Combine(target, "Case", "File.TXT")));
        Assert.True(File.Exists(Path.Combine(target, "changed.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
        string log = Assert.Single(Directory.EnumerateFiles(logs));
        Assert.Matches(@"^delete-\d{8}-\d{6}-001\.jsonl$", Path.GetFileName(log));
        Assert.Equal(log, model.Archives[0].Targets[0].LogPath);
        Assert.Contains("\"type\":\"result\"", File.ReadAllText(log), StringComparison.Ordinal);
        Assert.Contains("削除完了", model.Archives[0].Targets[0].StateText, StringComparison.Ordinal);
        Assert.True(model.Archives[0].Targets[0].HasDeleteStarted);
        Assert.Empty(model.PlanDeletion()!.Items);
    }

    [Fact]
    public async Task NormalEndShowsDeletedCountAndSizeAndKeepsAnalysisConsumedWithoutAutoReanalysis()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        int jobs = f.Runner.Jobs.Count;
        var result = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(DeletionStop.None, result.Stop);
        var target = f.Model.Archives[0].Targets[0];
        Assert.Contains("削除完了", target.StateText, StringComparison.Ordinal);
        Assert.Contains("削除成功: 2 件", target.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains("削除したファイルの論理サイズ合計: 15 bytes", target.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains(@"実行ログ: E:\logs\delete-1.jsonl", target.AnalysisDetailText, StringComparison.Ordinal);
        // The old analysis stays on screen but is consumed; nothing re-analyses or retries on its own.
        Assert.True(target.HasSuccessfulAnalysis);
        Assert.True(target.SnapshotConsumed);
        Assert.Equal(jobs + 2, f.Runner.Jobs.Count);
        Assert.DoesNotContain(f.Runner.Jobs.Skip(jobs), j => j.Operation == CliOperation.Analyze);
    }

    [Fact]
    public async Task MissingResultNamesTheUnknownCandidateStopsTheQueueAndNeverRetries()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Analyze ? Ok(job, Analysis)
            : Raw(job, 1, Run(job, 5, 2) + Entry("DELETED", 1, 10, "sub/Ａ顔.txt")));
        var result = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(new DeletionBatchResult(DeletionStop.Error, 2, 1, 0), result);
        var first = f.Model.Archives[0].Targets[0];
        var second = f.Model.Archives[1].Targets[0];
        Assert.True(first.HasDeleteStarted);
        Assert.Contains("エラー終了（結果が不完全）", first.StateText, StringComparison.Ordinal);
        Assert.Contains("不明（削除された可能性あり）: #3 Case/File.TXT", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains(@"実行ログ: E:\logs\delete-1.jsonl", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.False(second.HasDeleteStarted);
        Assert.Contains("未実行", second.StateText, StringComparison.Ordinal);
        Assert.Single(f.Runner.Jobs, j => j.Operation == CliOperation.Delete);
        // Re-delete before a successful re-analysis is refused for the started target.
        Assert.Single(f.Model.PlanDeletion()!.Items);
    }

    [Fact]
    public async Task UnreadableOutputIsUnknownResultWithTheLogPathAndOutputWithoutRunDeletedNothing()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Analyze ? Ok(job, Analysis) : Raw(job, 0, "garbage\n"));
        await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        var first = f.Model.Archives[0].Targets[0];
        Assert.Contains("結果不明", first.StateText, StringComparison.Ordinal);
        // No run record arrived, so the CLI is not known to have created the log: the path is shown as planned only.
        Assert.Contains(UnconfirmedLog + @"E:\logs\delete-1.jsonl", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.False(first.LogConfirmed);
        Assert.True(first.HasDeleteStarted);
        Assert.Contains("未実行", f.Model.Archives[1].Targets[0].StateText, StringComparison.Ordinal);

        var g = await Fixture(@"D:\one");
        await g.Model.AnalyzeSelectedAsync();
        g.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Analyze ? Ok(job, Analysis)
            : Raw(job, 1, Line(new { v = 1, type = "result", outcome = "input_error", exit_code = 1,
                error = new { stage = "prepare", code = "LOG_ALREADY_EXISTS", message = "ログが既にあります" } })));
        await g.Model.DeleteAsync(g.Model.PlanDeletion()!, approved: true);
        var one = g.Model.Archives[0].Targets[0];
        Assert.Contains("削除0件", one.StateText, StringComparison.Ordinal);
        Assert.Contains("code=LOG_ALREADY_EXISTS", one.AnalysisDetailText, StringComparison.Ordinal);
        // The planned name may now be another process's log: it is never presented as this delete's log.
        Assert.Contains(UnconfirmedLog + @"E:\logs\delete-1.jsonl", one.AnalysisDetailText, StringComparison.Ordinal);
        Assert.DoesNotContain(@"実行ログ: E:\logs\delete-1.jsonl", one.AnalysisDetailText, StringComparison.Ordinal);
        // The process had started, so the analysis is consumed even though nothing was deleted; no automatic retry.
        Assert.True(one.HasDeleteStarted);
        Assert.Single(g.Runner.Jobs, j => j.Operation == CliOperation.Delete);
    }

    private const string UnconfirmedLog = "実行ログ（予定した保存先。CLIが作成したことは確認できていません）: ";

    [Fact]
    public async Task ConfirmationListsEveryTargetToDeleteAndMarksSelectedTargetsHiddenByTheFilter()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        f.Model.ArchiveFilter = "B.zip";
        var plan = f.Model.PlanDeletion()!;
        // The hidden selection stays in the plan; it is only made visible in the confirmation.
        Assert.Equal(new[] { true, false }, plan.Items.Select(i => i.Hidden));
        Assert.Equal(1, plan.HiddenCount);
        string text = f.Model.ConfirmationText(plan);
        Assert.Contains("表示フィルタで非表示の選択済みTarget 1 件を含みます（対象から外していません）", text, StringComparison.Ordinal);
        Assert.Contains("削除対象のTarget（2 件、この順に実行）:", text, StringComparison.Ordinal);
        string[] lines = text.Split('\n');
        Assert.Contains(lines, l => l.StartsWith(@"1. D:\archives\A.zip → D:\one: 候補 2 ファイル / 15 bytes", StringComparison.Ordinal) &&
            l.EndsWith("［表示フィルタで非表示］", StringComparison.Ordinal));
        Assert.Contains(lines, l => l == @"2. D:\archives\B.zip → D:\one: 候補 2 ファイル / 15 bytes");
        f.Model.ArchiveFilter = "";
        text = f.Model.ConfirmationText(f.Model.PlanDeletion()!);
        Assert.DoesNotContain("非表示", text, StringComparison.Ordinal);
        Assert.Contains(@"1. D:\archives\A.zip → D:\one", text, StringComparison.Ordinal);
        // Every exclusion is listed too, not only the first few.
        var many = await Fixture(Enumerable.Range(1, 60).Select(i => $@"D:\t{i:D2}").ToArray());
        text = many.Model.ConfirmationText(many.Model.PlanDeletion()!);
        Assert.Contains(@"D:\archives\B.zip → D:\t60: 未解析", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ほか", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SynchronousRunnerRefusalIsUnstartedButAnUnexpectedRunnerErrorCountsAsStartedAndKeepsTheEntriesFile()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        f.Runner.Refusal = "CLIは実行中、または以前のプロセスの終了を確認できていません。";
        var refused = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(DeletionStop.NotStarted, refused.Stop);
        var first = f.Model.Archives[0].Targets[0];
        Assert.False(first.HasDeleteStarted);
        Assert.True(first.HasSuccessfulAnalysis);
        Assert.Contains("削除処理は開始されていません", first.StateText, StringComparison.Ordinal);
        Assert.Empty(f.Entries.Live);
        f.Runner.Refusal = null;

        // An exception from the running job: whether a process exists is unknown, so it is the started side.
        f.Runner.Handler = (job, _) => job.Operation == CliOperation.Delete
            ? Task.FromException<CliJobResult>(new InvalidOperationException("unexpected")) : Task.FromResult(Ok(job, Analysis));
        var result = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(new DeletionBatchResult(DeletionStop.Error, 2, 1, 0), result);
        Assert.True(first.HasDeleteStarted);
        Assert.True(first.SnapshotConsumed);
        Assert.Equal(AnalysisPhase.Idle, first.Phase);
        Assert.Contains("結果不明", first.StateText, StringComparison.Ordinal);
        Assert.Contains("unexpected", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains("一時entriesファイルを残しました", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Single(f.Entries.Live);
        Assert.False(f.Model.Archives[1].Targets[0].HasDeleteStarted);
        Assert.False(f.Model.IsBusy);
    }

    [Fact]
    public async Task UnknownStartIsTreatedAsStartedAndKeepsTheEntriesFile()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Delete ? StartUnknown(job) : Ok(job, Analysis));
        var result = await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal(new DeletionBatchResult(DeletionStop.Error, 2, 1, 0), result);
        var first = f.Model.Archives[0].Targets[0];
        Assert.True(first.HasDeleteStarted);
        Assert.Contains("結果不明", first.StateText, StringComparison.Ordinal);
        Assert.Contains("起動成否を確認できません", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Single(f.Entries.Live);
        Assert.DoesNotContain(f.Model.PlanDeletion()!.Items, i => i.Target == first);
    }

    [Fact]
    public async Task LatestDeleteResultSurvivesReanalysisAndModeChangeAndIsReplacedOnlyByTheNextDelete()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Analyze ? Ok(job, Analysis)
            : Raw(job, 1, Run(job, 5, 2) + Entry("DELETED", 1, 10, "sub/Ａ顔.txt")));
        await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        var first = f.Model.Archives[0].Targets[0];
        var second = f.Model.Archives[1].Targets[0];
        Assert.Contains("不明（削除された可能性あり）: #3", first.AnalysisDetailText, StringComparison.Ordinal);

        // Re-analysis (success or failure) keeps the latest delete result and its log path.
        await f.Model.AnalyzeTargetAsync(f.Model.Archives[0], first);
        Assert.False(first.SnapshotConsumed);
        Assert.Contains("直前の削除: エラー終了（結果が不完全）", first.StateText, StringComparison.Ordinal);
        Assert.Contains("不明（削除された可能性あり）: #3", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains(@"実行ログ: E:\logs\delete-1.jsonl", first.AnalysisDetailText, StringComparison.Ordinal);
        f.Runner.Handler = (job, _) => Task.FromResult(Failed(job));
        await f.Model.AnalyzeTargetAsync(f.Model.Archives[0], first);
        Assert.False(first.HasSuccessfulAnalysis);
        Assert.Contains("直前の削除: エラー終了（結果が不完全）", first.StateText, StringComparison.Ordinal);
        Assert.Contains(@"E:\logs\delete-1.jsonl", first.AnalysisDetailText, StringComparison.Ordinal);
        // A mode change discards every analysis result, not the latest delete result.
        Assert.True(f.Model.SetMode(CliMode.Fast, discardApproved: true));
        Assert.False(second.HasSuccessfulAnalysis);
        Assert.Contains("未実行", second.StateText, StringComparison.Ordinal);
        Assert.Contains(@"E:\logs\delete-1.jsonl", first.AnalysisDetailText, StringComparison.Ordinal);

        // The next delete of the target replaces it (one result only, no history).
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Analyze ? Ok(job, Analysis) : DeleteOk(job));
        await f.Model.AnalyzeSelectedAsync();
        await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Contains("削除完了", first.StateText, StringComparison.Ordinal);
        Assert.DoesNotContain("不明（削除された可能性あり）", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.DoesNotContain("エラー終了", first.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Single(first.AnalysisDetailText.Split('\n'), l => l.StartsWith("実行ログ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnrunAndUnstartedStepsAreBatchStateAndKeepThePreviousDeleteResultAndItsLog()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        Assert.Contains("解析済み 2 Targets / 削除候補 4 ファイル", f.Model.AnalysisSummaryText, StringComparison.Ordinal);
        var a = f.Model.Archives[0].Targets[0];
        var b = f.Model.Archives[1].Targets[0];
        // Batch 1: both CLIs run. B's delete result and log path are delete-2.
        await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Contains(@"実行ログ: E:\logs\delete-2.jsonl", b.AnalysisDetailText, StringComparison.Ordinal);

        // Batch 2 after re-analysis: A ends in error, so B is not started. That is batch state only.
        await ReanalyzeAll(f);
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Delete ? ErrorEnd(job) : Ok(job, Analysis));
        await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Contains("直近の一括削除: 未実行", b.StateText, StringComparison.Ordinal);
        Assert.Contains("前のTargetの後で停止したため", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains("直前の削除: 削除完了", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains("削除成功: 2 件", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains(@"実行ログ: E:\logs\delete-2.jsonl", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Equal(@"E:\logs\delete-2.jsonl", b.LogPath);
        Assert.False(b.SnapshotConsumed);
        // A consumed snapshot is no longer counted as a deletion candidate.
        Assert.True(a.SnapshotConsumed);
        Assert.Contains("解析済み 1 Targets / 削除候補 2 ファイル", f.Model.AnalysisSummaryText, StringComparison.Ordinal);
        Assert.Contains("削除実行済みで再解析待ちの 1 Targets は含みません", f.Model.AnalysisSummaryText, StringComparison.Ordinal);
        Assert.Contains("解析時の削除候補（削除実行済みのため、再解析するまで削除には使いません）", a.AnalysisDetailText, StringComparison.Ordinal);
        // B's analysis is still usable for deletion (not consumed): the plain candidate line, not the consumed one.
        Assert.Contains("\n削除候補: 2 ファイル", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.DoesNotContain("解析時の削除候補", b.AnalysisDetailText, StringComparison.Ordinal);

        // Batch 3: B's CLI certainly does not start. The new batch state replaces "unrun"; the delete result stays.
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Delete ? NotStartedResult(job) : Ok(job, Analysis));
        await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Contains("直近の一括削除: CLIを起動できず", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.DoesNotContain("未実行", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains(@"実行ログ: E:\logs\delete-2.jsonl", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains("直前の削除: 削除完了", b.StateText, StringComparison.Ordinal);
        Assert.False(b.SnapshotConsumed);
        Assert.True(b.HasSuccessfulAnalysis);

        // Batch 4: B's CLI runs. Its result replaces the delete result and the batch state is gone.
        f.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Delete ? ErrorEnd(job) : Ok(job, Analysis));
        await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.DoesNotContain("直近の一括削除", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.DoesNotContain("削除完了", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Contains(@"実行ログ: E:\logs\delete-1.jsonl", b.AnalysisDetailText, StringComparison.Ordinal);
        Assert.Single(b.AnalysisDetailText.Split('\n'), l => l.StartsWith("実行ログ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreparationFailureNoticeStaysWithTheBatchStateAndTheEarlierResultStays()
    {
        var f = await Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        await ReanalyzeAll(f);
        var a = f.Model.Archives[0].Targets[0];
        // A partly written entries file that cannot be removed: the notice belongs to this unstarted step.
        f.Entries.Error = "disk full";
        f.Entries.OwnedOnError = true;
        f.Entries.DeleteError = "locked";
        await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        string[] lines = a.AnalysisDetailText.Split('\n');
        int batch = Array.FindIndex(lines, l => l.StartsWith("直近の一括削除: 一時entriesファイル", StringComparison.Ordinal));
        int notice = Array.FindIndex(lines, l => l.Contains("locked", StringComparison.Ordinal));
        int previous = Array.FindIndex(lines, l => l == "直前の削除: 削除完了");
        Assert.True(batch >= 0 && notice > batch && previous > notice, a.AnalysisDetailText);
        Assert.Contains(@"実行ログ: E:\logs\delete-1.jsonl", a.AnalysisDetailText, StringComparison.Ordinal);
    }

    // Explicit re-analysis of every target ("selected unanalyzed" skips targets that still hold a consumed snapshot).
    private static async Task ReanalyzeAll(Setup f)
    {
        foreach (var archive in f.Model.Archives)
            foreach (var target in archive.Targets)
                await f.Model.AnalyzeTargetAsync(archive, target);
    }

    private static CliJobResult Raw(CliJob job, int exit, string text)
    {
        var receiver = new JsonlReceiver(job);
        receiver.Feed(Encoding.ASCII.GetBytes(text));
        return new(CliStartState.Started, true, exit, true, true, false, receiver.Finish(), "", false, null);
    }
}
