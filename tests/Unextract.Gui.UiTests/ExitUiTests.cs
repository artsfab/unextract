using Xunit.Abstractions;

namespace Unextract.Gui.UiTests;

// Closing the window while a CLI job runs: analysis is cancelled and the GUI exits; a delete is never killed,
// the current Target finishes, no later Target starts, and only then does the window close.
public sealed class ExitUiTests(ITestOutputHelper output) : UiTestBase(output)
{
    [UiFact]
    public void ClosingDuringAnalysisCancelsItAndExitsWithZero()
    {
        var ui = Start();
        string archive = Path.Combine(ui.Fixtures, "a.zip"), target = Path.Combine(ui.Fixtures, "out");
        Files.Zip(archive, ("f1.txt", "x"), ("f2.txt", "y"));
        Directory.CreateDirectory(target);
        ui.FakeAnalyze(archive, target, "strict", [new(1, "f1.txt", "MATCHED", 1), new(2, "f2.txt", "MATCHED", 1)], wait: true, waitAfter: 1);
        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out");
        ui.Invoke("AnalyzeSelectedButton");
        Wait.Until(() => ui.Invocations.Count == 1, "the fake analysis to start");
        Wait.Until(() => ui.Find("CancelAnalysisButton") is not null, "the analysis to be running");

        ui.Main.Close();
        Wait.Until(() => ui.HasExited, "the GUI to exit after cancelling the analysis");
        Assert.Equal(0, ui.App.ExitCode);
        Assert.Single(ui.Invocations);
    }

    [UiFact]
    public void ClosingDuringADeleteWaitsForTheCurrentTargetAndStartsNoLaterOne()
    {
        var ui = Start();
        string a = Path.Combine(ui.Fixtures, "a.zip"), b = Path.Combine(ui.Fixtures, "b.zip");
        string ta = Path.Combine(ui.Fixtures, "out-a"), tb = Path.Combine(ui.Fixtures, "out-b");
        Files.Zip(a, ("f1.txt", "x"));
        Files.Zip(b, ("g1.txt", "y"));
        Directory.CreateDirectory(ta);
        Directory.CreateDirectory(tb);
        ui.FakeAnalyze(a, ta, "strict", [new(1, "f1.txt", "MATCHED", 1)]);
        ui.FakeAnalyze(b, tb, "strict", [new(1, "g1.txt", "MATCHED", 1)]);
        string release = ui.FakeDelete(a, ta, "strict", 1, 1, [new(1, "f1.txt", "DELETED", 1)], notSelected: 0, wait: true, waitAfter: 1);
        ui.FakeDelete(b, tb, "strict", 1, 1, [new(1, "g1.txt", "DELETED", 1)], notSelected: 0);

        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out-{{archive.name}}");
        ui.AnalyzeSelected();
        var confirm = ui.OpenDeleteConfirmation();
        ui.Invoke("DeletionConfirmDeleteButton", confirm);
        Wait.Until(() => ui.Invocations.Count(i => i.Operation == "delete") == 1, "the first delete to start");
        Wait.Until(() => ui.Status().StartsWith("", StringComparison.Ordinal) && ui.TextOf("OverallProgressText").StartsWith("1 / 2", StringComparison.Ordinal), "the first Target to be running");
        ui.ViewTarget(ta);
        Wait.Until(() => ui.TextOf("TargetStateText") == "削除中", "the Target to be deleting");

        // First close request: the wait message, the window stays.
        ui.Main.Close();
        Wait.Until(() => ui.Status() == "現在のTargetの処理が終わり次第終了します。", "the waiting message");
        // A repeated request neither closes the window nor kills the CLI.
        ui.Main.Close();
        Assert.False(ui.HasExited);
        Assert.NotNull(ui.Find("StatusText"));
        Assert.Equal("現在のTargetの処理が終わり次第終了します。", ui.Status());
        Assert.Single(ui.Invocations, i => i.Operation == "delete");

        // Releasing the current CLI lets it finish normally; the second Target never starts, then the window closes.
        ui.Scenario.Release(release);
        Wait.Until(() => ui.HasExited, "the GUI to exit after the current Target finished");
        Assert.Equal(0, ui.App.ExitCode);
        var deletes = ui.Invocations.Where(i => i.Operation == "delete").ToArray();
        Assert.Single(deletes);
        Assert.Equal(a, deletes[0].Args[1]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ui.TempDirectory));
        // The first Target's log exists (written by the CLI it ran); no log was prepared for the second.
        Assert.Single(Directory.EnumerateFiles(ui.LogsDirectory));
    }
}
