using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Unextract.Gui.Models;
using Unextract.Gui.Services;
using Unextract.Gui.ViewModels;
using static Unextract.Gui.Tests.JsonlReceiverTests;
using static Unextract.Gui.Tests.SearchSessionTests;

namespace Unextract.Gui.Tests;

// The GUI side of delete --prune-dirs (docs/spec/gui.md#prune-dirs). Receiver: dir records only after every selected file has an
// entry and none STOPPED, only when --prune-dirs was passed, run.prune_dirs/dirs_selected matching the launch, the dir fields and
// their order, the directory counts of completed and early ends, and a stop in the directory stage. Report: the folder counts and
// keep reasons of a normal end, an interruption after every file entry (the one unknown is a folder that is not named), an
// interruption before that (the file inference stays and no folder was deleted), and a stop in the directory stage. View model:
// off by default, locked while busy, switching keeps analysis results, fixed in the plan and the confirmation, passed as
// --prune-dirs. The bundled real CLI on a self-made ZIP and target removes only the emptied ancestor of a deleted candidate.
public sealed class PruneDirsGuiTests
{
    private static readonly CliJob Prune = Job(CliOperation.Delete) with { PruneDirs = true };
    private static readonly CliJob NoPrune = Job(CliOperation.Delete);

    private static string Dir(string path, string status = "DIR_DELETED", string? keep = null)
    {
        var dir = new JsonObject { ["v"] = 2, ["type"] = "dir", ["path"] = path, ["status"] = status };
        if (keep is not null) dir["keep_reason"] = keep;
        if (status == "STOPPED")
        {
            dir["reason"] = JsonNode.Parse("{\"step\":\"dispose\",\"code\":\"FUTURE_CODE\",\"message\":\"表示\"}");
            dir["possibly_deleted"] = true;
        }
        return dir.ToJsonString() + "\n";
    }

    private static JsonObject Counts(int deleted = 0, int failed = 0, int notSelected = 0, int unprocessed = 0,
        int dirDeleted = 0, int dirKept = 0, int dirMissing = 0, int dirUnprocessed = 0, bool dirs = true)
    {
        var counts = new JsonObject
        {
            ["deleted"] = deleted, ["modified"] = 0, ["missing"] = 0, ["skipped_special_file"] = 0, ["directory"] = 0,
            ["delete_failed"] = failed, ["not_selected"] = notSelected, ["unprocessed"] = unprocessed,
        };
        if (dirs)
        {
            counts["dir_deleted"] = dirDeleted;
            counts["dir_kept"] = dirKept;
            counts["dir_missing"] = dirMissing;
            counts["dir_unprocessed"] = dirUnprocessed;
        }
        return counts;
    }

    private static string Result(string outcome, int exit, JsonObject counts, string? error = null)
    {
        var result = new JsonObject { ["v"] = 2, ["type"] = "result", ["outcome"] = outcome, ["exit_code"] = exit, ["counts"] = counts };
        if (error is not null) result["error"] = JsonNode.Parse(error);
        return result.ToJsonString() + "\n";
    }

    private static string Without(string line, string field)
    {
        var root = JsonNode.Parse(line)!.AsObject();
        Assert.True(root.Remove(field));
        return root.ToJsonString() + "\n";
    }

    private static string Del(string status, int index) => Entry(status, index, index, $"file{index}");
    private static string Failed(int index) => Change(Del("DELETE_FAILED", index), "reason", JsonNode.Parse("{\"code\":\"DELETE_OPEN_REFUSED\",\"message\":\"表示\"}"));
    private static string StoppedEntry(int index) => Change(Change(Del("STOPPED", index), "reason",
        JsonNode.Parse("{\"step\":\"open\",\"code\":\"FUTURE_CODE\",\"message\":\"表示\"}")), "possibly_deleted", JsonValue.Create(false));

    // Files 2 and 4 of 5 are selected; the directory stage has 3 targets.
    private static string Files => Run(Prune, 5, 2, dirs: 3) + Del("DELETED", 2) + Failed(4);
    private static string Dirs => Dir(@"a\b") + Dir("a", "DIR_KEPT", "NOT_EMPTY") + Dir("c", "DIR_MISSING");
    private static string Done => Result("completed", 1, Counts(deleted: 1, failed: 1, notSelected: 3, dirDeleted: 1, dirKept: 1, dirMissing: 1));

    [Fact]
    public void DirRecordsFollowEveryFileEntryAndCompletedCountsMatchThem()
    {
        var output = Receive(Files + Dirs + Done, Prune);
        Assert.True(output.IsCompatible, output.IssueMessage);
        Assert.Equal(true, output.Run!.PruneDirs);
        Assert.Equal(3, output.Run.DirsSelected);
        Assert.Equal(new[] { @"a\b", "a", "c" }, output.Dirs.Select(d => d.Path));
        Assert.Equal("NOT_EMPTY", output.Dirs[1].KeepReason);
        Assert.Equal(1, output.Result!.Counts!["dir_kept"]);

        // No directory target at all, and a run without --prune-dirs (prune_dirs false, no dirs_selected).
        var none = Receive(Run(Prune, 1, 1, dirs: 0) + Del("DELETED", 1) + Result("completed", 0, Counts(deleted: 1)), Prune);
        Assert.True(none.IsCompatible, none.IssueMessage);
        Assert.Empty(none.Dirs);
        var plain = Receive(Run(NoPrune, 1) + Del("DELETED", 1) + Result("completed", 0, Counts(deleted: 1, dirs: false)), NoPrune);
        Assert.True(plain.IsCompatible, plain.IssueMessage);
        Assert.Equal(false, plain.Run!.PruneDirs);
        Assert.Null(plain.Run.DirsSelected);
    }

    [Fact]
    public void AStopInTheDirectoryStageEndsWithTheStoppedDirAndItsPath()
    {
        string error = "{\"stage\":\"entry\",\"step\":\"dispose\",\"code\":\"FUTURE_CODE\",\"possibly_deleted\":true,\"dir_path\":\"a\",\"message\":\"表示\"}";
        string stopped = Files + Dir(@"a\b") + Dir("a", "STOPPED") +
            Result("stopped", 1, Counts(deleted: 1, failed: 1, notSelected: 3, dirDeleted: 1, dirUnprocessed: 1), error);
        var output = Receive(stopped, Prune);
        Assert.True(output.IsCompatible, output.IssueMessage);
        Assert.Equal("a", output.Result!.Error!.DirPath);
        Assert.True(output.Dirs[^1].PossiblyDeleted);
    }

    public static IEnumerable<object[]> CorruptPruneOutputs()
    {
        string run = Run(Prune, 5, 2, dirs: 3);
        string plainRun = Run(NoPrune, 5, 2);
        string files = Del("DELETED", 2) + Failed(4);
        yield return ["dir without --prune-dirs", false, plainRun + files + Dir("a")];
        yield return ["prune_dirs false for a prune launch", true, Change(Without(run, "dirs_selected"), "prune_dirs", JsonValue.Create(false))];
        yield return ["prune_dirs true for a plain launch", false, Change(Change(plainRun, "prune_dirs", JsonValue.Create(true)), "dirs_selected", JsonValue.Create(1))];
        yield return ["delete run without prune_dirs", false, Without(plainRun, "prune_dirs")];
        yield return ["prune run without dirs_selected", true, Without(run, "dirs_selected")];
        yield return ["dirs_selected without prune_dirs", false, Change(plainRun, "dirs_selected", JsonValue.Create(0))];
        yield return ["prune_dirs as a string", true, Change(run, "prune_dirs", JsonValue.Create("true"))];
        yield return ["dir before the last file", true, run + Del("DELETED", 2) + Dir("a")];
        yield return ["dir after a STOPPED file", true, Run(Prune, 5, 1, dirs: 3) + StoppedEntry(2) + Dir("a")];
        yield return ["entry after a dir", true, Run(Prune, 5, 1, dirs: 3) + Del("DELETED", 2) + Dir("a") + Failed(4)];
        yield return ["more dirs than dirs_selected", true, Run(Prune, 5, 2, dirs: 1) + files + Dir("a") + Dir("b")];
        yield return ["duplicate dir path", true, run + files + Dir("a") + Dir("a")];
        yield return ["empty dir path", true, run + files + Dir("")];
        yield return ["dir path missing", true, run + files + Without(Dir("a"), "path")];
        yield return ["file status on a dir", true, run + files + Dir("a", "DELETED")];
        yield return ["DIR_KEPT without keep_reason", true, run + files + Dir("a", "DIR_KEPT")];
        yield return ["keep_reason on DIR_DELETED", true, run + files + Dir("a", "DIR_DELETED", "NOT_EMPTY")];
        yield return ["STOPPED dir without reason", true, run + files + Without(Dir("a", "STOPPED"), "reason")];
        yield return ["STOPPED dir without possibly_deleted", true, run + files + Without(Dir("a", "STOPPED"), "possibly_deleted")];
        yield return ["possibly_deleted on DIR_DELETED", true, run + files + Change(Dir("a"), "possibly_deleted", JsonValue.Create(false))];
        yield return ["dir after a STOPPED dir", true, run + files + Dir("a", "STOPPED") + Dir("b")];
        yield return ["completed without dir counts", true, run + files + Dirs + Result("completed", 1, Counts(deleted: 1, failed: 1, notSelected: 3, dirs: false))];
        yield return ["completed dir counts differ", true, run + files + Dirs + Result("completed", 1, Counts(deleted: 1, failed: 1, notSelected: 3, dirDeleted: 2, dirMissing: 1))];
        yield return ["completed before every dir", true, run + files + Dir(@"a\b") + Result("completed", 1, Counts(deleted: 1, failed: 1, notSelected: 3, dirDeleted: 1))];
        yield return ["completed with dir_unprocessed", true, run + files + Dirs + Result("completed", 1, Counts(deleted: 1, failed: 1, notSelected: 3, dirDeleted: 1, dirKept: 1, dirMissing: 1, dirUnprocessed: 1))];
        yield return ["stopped without a STOPPED dir", true, run + files + Dir("a") +
            Result("stopped", 1, Counts(deleted: 1, failed: 1, notSelected: 3, dirDeleted: 1, dirUnprocessed: 2), "{\"stage\":\"entry\",\"code\":\"X\"}")];
        yield return ["stopped dir counts missing", true, run + files + Dir("a", "STOPPED") +
            Result("stopped", 1, Counts(deleted: 1, failed: 1, notSelected: 3, dirs: false), "{\"stage\":\"entry\",\"code\":\"X\",\"dir_path\":\"a\"}")];
        yield return ["dir_path not a string", true, run + files + Dir("a", "STOPPED") +
            Result("stopped", 1, Counts(deleted: 1, failed: 1, notSelected: 3, dirUnprocessed: 2), "{\"stage\":\"entry\",\"code\":\"X\",\"dir_path\":1}")];
    }

    [Theory]
    [MemberData(nameof(CorruptPruneOutputs))]
    public void InvalidPruneOutputIsNeverAdopted(string description, bool prune, string text)
    {
        var output = Receive(text, prune ? Prune : NoPrune);
        Assert.False(output.IsCompatible, description);
        Assert.Equal(ProtocolIssue.InvalidOutput, output.Issue);
    }

    [Fact]
    public void AnalyzeRunsCarryNoPruneFields()
    {
        var job = Job();
        Assert.False(Receive(Change(Run(job), "prune_dirs", JsonValue.Create(false)), job).IsCompatible);
        Assert.Throws<ArgumentException>(() => (job with { PruneDirs = true }).Validate());
    }

    [Fact]
    public void ThePruneOptionIsPassedBeforeYesOnlyWhenSet()
    {
        var info = CliProcessRunner.StartInfo(@"C:\cli\unextract.exe", Prune);
        Assert.Equal(new[] { "delete", Prune.Archive, "--target", Prune.Target, "--entries", Prune.Entries!, "--prune-dirs",
            "--yes", "--jsonl", "--log", Prune.Log! }, info.ArgumentList);
        Assert.DoesNotContain("--prune-dirs", CliProcessRunner.StartInfo(@"C:\cli\unextract.exe", NoPrune).ArgumentList);
    }

    // Report: approved candidates 2 and 4 (ZIP order); the analysis had 5 entries.
    private static readonly CliEntry[] Approved = [Candidate(2), Candidate(4)];
    private static CliEntry Candidate(int index) => new(index, $"file{index}", false, index, "MATCHED", null, null, null);

    private static DeleteReport Report(string text, int exit, bool resultExpected = true)
    {
        var output = Receive(text, Prune);
        Assert.True(output.IsCompatible, output.IssueMessage);
        Assert.Equal(resultExpected, output.Result is not null);
        return DeleteReport.Create(Approved, 5, @"C:\target", new(CliStartState.Started, true, exit, true, true, false, output, "", false, null));
    }

    [Fact]
    public void CompletedRunShowsFolderCountsAndWhyFoldersWereKept()
    {
        string text = Run(Prune, 5, 2, dirs: 4) + Del("DELETED", 2) + Del("DELETED", 4) + Dir(@"a\b") +
            Dir(@"a\c", "DIR_KEPT", "IN_USE") + Dir("a", "DIR_KEPT", "NOT_EMPTY") + Dir("d", "DIR_KEPT", "NOT_EMPTY") +
            Result("completed", 0, Counts(deleted: 2, notSelected: 3, dirDeleted: 1, dirKept: 3));
        var report = Report(text, 0);
        Assert.True(report.Succeeded);
        Assert.False(report.DirUnknown);
        Assert.Contains("空のフォルダー: 削除 1 件 / 残した 3 件 / 存在しない 0 件", report.Detail, StringComparison.Ordinal);
        Assert.Contains("残したフォルダーの理由: 空でない 2 / 使用中 1", report.Detail, StringComparison.Ordinal);

        // Without --prune-dirs nothing about folders is said.
        var plain = Receive(Run(NoPrune, 5, 2) + Del("DELETED", 2) + Del("DELETED", 4) + Result("completed", 0, Counts(deleted: 2, notSelected: 3, dirs: false)), NoPrune);
        var plainReport = DeleteReport.Create(Approved, 5, @"C:\target", new(CliStartState.Started, true, 0, true, true, false, plain, "", false, null));
        Assert.True(plainReport.Succeeded);
        Assert.DoesNotContain("フォルダー", plainReport.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInterruptionAfterEveryFileEntryLeavesOneUnnamedFolderUnknown()
    {
        string files = Run(Prune, 5, 2, dirs: 3) + Del("DELETED", 2) + Del("DELETED", 4);
        var report = Report(files + Dir(@"a\b"), 1, resultExpected: false);
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.True(report.DirUnknown);
        Assert.Null(report.Unknown);
        Assert.Equal(0, report.Unprocessed);
        Assert.Contains("ファイルの結果は確定しています", report.Detail, StringComparison.Ordinal);
        Assert.Contains("空のフォルダー1件が削除された可能性があります", report.Detail, StringComparison.Ordinal);
        Assert.Contains(@"最後に受信したフォルダー: a\b", report.Detail, StringComparison.Ordinal);

        report = Report(files, 1, resultExpected: false);
        Assert.True(report.DirUnknown);
        Assert.Contains("フォルダーの結果は1件も受信していません", report.Detail, StringComparison.Ordinal);

        // Every dir received: nothing is unknown.
        report = Report(files + Dir(@"a\b") + Dir("a") + Dir("c"), 1, resultExpected: false);
        Assert.False(report.DirUnknown);
        Assert.Contains("結果もすべて受信しています", report.Detail, StringComparison.Ordinal);

        // internal_error after the stage started, with the folder the CLI reports: that report is used, no inference.
        string reported = Result("internal_error", 1, Counts(deleted: 2, notSelected: 3, dirDeleted: 1, dirUnprocessed: 2),
            "{\"stage\":\"internal\",\"code\":\"OUTPUT_FAILED\",\"deletion_started\":true,\"possibly_deleted\":true,\"dir_path\":\"a\"}");
        report = Report(files + Dir(@"a\b") + reported, 1);
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.False(report.DirUnknown);
        Assert.Contains("CLIが報告したフォルダー: a（削除された可能性あり）", report.Detail, StringComparison.Ordinal);
        Assert.Contains("dir_path=a", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIは対象を報告していません", report.Detail, StringComparison.Ordinal);

        // internal_error from a non-STOPPED dir record (no dir_path): the next folder is the unknown one, unnamed.
        string unnamed = Result("internal_error", 1, Counts(deleted: 2, notSelected: 3, dirDeleted: 2, dirUnprocessed: 1),
            "{\"stage\":\"internal\",\"code\":\"OUTPUT_FAILED\",\"deletion_started\":true}");
        report = Report(files + Dir(@"a\b") + unnamed, 1);
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.True(report.DirUnknown);
        Assert.Contains("空のフォルダー1件が削除された可能性があります", report.Detail, StringComparison.Ordinal);
        Assert.Contains(@"最後に受信したフォルダー: a\b", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInterruptionBeforeTheLastFileKeepsTheFileInferenceAndNoFolderWasDeleted()
    {
        var report = Report(Run(Prune, 5, 2, dirs: 3) + Del("DELETED", 2), 1, resultExpected: false);
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.False(report.DirUnknown);
        Assert.Equal(4, report.Unknown!.Index);
        Assert.Contains("フォルダーの処理（ディレクトリ段階）は開始していません", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AStopInTheDirectoryStageNamesTheFolderAndItsCounts()
    {
        string text = Run(Prune, 5, 2, dirs: 3) + Del("DELETED", 2) + Del("DELETED", 4) + Dir(@"a\b") + Dir("a", "STOPPED") +
            Result("stopped", 1, Counts(deleted: 2, notSelected: 3, dirDeleted: 1, dirUnprocessed: 1),
                "{\"stage\":\"entry\",\"step\":\"dispose\",\"code\":\"FUTURE_CODE\",\"possibly_deleted\":true,\"dir_path\":\"a\",\"message\":\"表示\"}");
        var report = Report(text, 1);
        Assert.Equal(DeleteReportKind.WithResult, report.Kind);
        Assert.False(report.DirUnknown);
        Assert.Contains("停止したフォルダー: a [FUTURE_CODE]（削除された可能性あり）", report.Detail, StringComparison.Ordinal);
        Assert.Contains("フォルダー削除済み 1", report.Detail, StringComparison.Ordinal);
        Assert.Contains("フォルダー未処理 1", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIが報告したフォルダー", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIは対象を報告していません", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSettingIsOffLockedWhileBusyKeepsAnalysisAndIsFixedInThePlan()
    {
        var f = await DeletionPreparationTests.Fixture(@"D:\one");
        Assert.False(f.Model.PruneDirs);
        await f.Model.AnalyzeSelectedAsync();
        var snapshots = f.Model.Archives.Select(a => a.Targets[0].Snapshot).ToArray();
        f.Model.PruneDirs = true;
        Assert.True(f.Model.PruneDirs);
        Assert.Equal(snapshots, f.Model.Archives.Select(a => a.Targets[0].Snapshot));

        var plan = f.Model.PlanDeletion()!;
        Assert.True(plan.PruneDirs);
        Assert.Contains("空のフォルダーも削除します", f.Model.ConfirmationText(plan), StringComparison.Ordinal);
        Assert.Contains("もとから空だったものを含む", f.Model.ConfirmationText(plan), StringComparison.Ordinal);
        // A change after planning does not alter the plan.
        f.Model.PruneDirs = false;
        Assert.Contains("フォルダーは削除しません", f.Model.ConfirmationText(f.Model.PlanDeletion()!), StringComparison.Ordinal);

        // The approved plan (ON) runs with --prune-dirs although the current setting is OFF when it starts,
        // and while the batch runs, the setting cannot change.
        var release = new TaskCompletionSource();
        bool? duringRun = null;
        f.Runner.Handler = async (job, _) =>
        {
            f.Model.PruneDirs = true;
            duringRun = f.Model.PruneDirs;
            await release.Task;
            return DeletionPreparationTests.DeleteOk(job);
        };
        Assert.False(f.Model.PruneDirs);
        var running = f.Model.DeleteAsync(plan, approved: true);
        while (duringRun is null) await Task.Delay(10);
        Assert.True(f.Model.IsBusy);
        Assert.False(duringRun);
        release.SetResult();
        Assert.Equal(DeletionStop.None, (await running).Stop);
        var deletes = f.Runner.Jobs.Where(j => j.Operation == CliOperation.Delete).ToArray();
        Assert.Equal(2, deletes.Length);
        Assert.All(deletes, j => Assert.True(j.PruneDirs));
        Assert.All(f.Runner.Jobs.Where(j => j.Operation == CliOperation.Analyze), j => Assert.False(j.PruneDirs));
    }

    [Fact]
    public async Task TheBundledCliRemovesOnlyTheEmptiedAncestorOfADeletedCandidate()
    {
        byte[] bytes = [1, 2, 3];
        string root = ArchiveSearchTests.Fixture();
        string archive = Path.Combine(root, "a.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            foreach (string name in new[] { "d/sub/x.txt", "d/keep/y.txt", "e/" })
            {
                var entry = zip.CreateEntry(name);
                if (!name.EndsWith('/')) { using var stream = entry.Open(); stream.Write(bytes); }
            }
        string target = Directory.CreateDirectory(Path.Combine(root, "t")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "d", "sub"));
        Directory.CreateDirectory(Path.Combine(target, "d", "keep"));
        Directory.CreateDirectory(Path.Combine(target, "e"));
        File.WriteAllBytes(Path.Combine(target, "d", "sub", "x.txt"), bytes);
        File.WriteAllBytes(Path.Combine(target, "d", "keep", "y.txt"), [9, 9, 9]);
        string logs = Path.Combine(root, "logs");
        string temp = Directory.CreateDirectory(Path.Combine(root, "temp")).FullName;
        var runner = new CliProcessRunner(new CliLocation(DeploymentTests.GuiOutputDirectory));
        var search = new FakeSearch { Result = new([new(archive, new FileInfo(archive).Length, DateTime.UtcNow)], []) };
        var model = new MainViewModel(new("cli", true, "ok"), search, new FakeSettings(), runner, new EntriesStore(temp), new LogLocation(logs))
            { SearchDirectory = root };
        Assert.True(await model.SearchAsync());
        await model.AddTargetsAsync(target);
        var limit = TimeSpan.FromSeconds(90);
        Assert.Equal(1, (await model.AnalyzeSelectedAsync().WaitAsync(limit)).Succeeded);
        model.PruneDirs = true;
        var plan = model.PlanDeletion()!;
        Assert.Equal(new DeletionBatchResult(DeletionStop.None, 1, 1, 1), await model.DeleteAsync(plan, approved: true).WaitAsync(limit));
        // d/sub emptied and removed; d still holds keep (the modified file), and keep and the explicit e/ are not ancestors of a
        // candidate, so they are not targets with --entries.
        Assert.False(Directory.Exists(Path.Combine(target, "d", "sub")));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(target, "d", "keep", "y.txt")));
        Assert.True(Directory.Exists(Path.Combine(target, "e")));
        var state = model.Archives[0].Targets[0];
        Assert.Contains("空のフォルダー: 削除 1 件 / 残した 1 件 / 存在しない 0 件", state.DeleteDetail, StringComparison.Ordinal);
        Assert.Contains("残したフォルダーの理由: 空でない 1", state.DeleteDetail, StringComparison.Ordinal);
        string log = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(logs)), Encoding.ASCII);
        Assert.Contains("\"prune_dirs\":true", log, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"dir\"", log, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
    }
}
