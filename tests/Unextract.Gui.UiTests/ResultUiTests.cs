using FlaUI.Core.AutomationElements;
namespace Unextract.Gui.UiTests;

// Failure and unknown results on the screen, reached with abnormal fake CLI outputs (not only by screenshots).
public sealed class ResultUiTests : UiTestBase
{
    [Fact]
    public void AnAnalysisFailureShowsTheCliErrorAndKeepsTheTargetUnanalyzed()
    {
        var ui = Start(kind: "result-analysis");
        string archive = Path.Combine(ui.Fixtures, "a.zip"), target = Path.Combine(ui.Fixtures, "out");
        Files.Zip(archive, ("f1.txt", "x"));
        Directory.CreateDirectory(target);
        ui.FakeAnalysisFailure(archive, target);
        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out");
        ui.AnalyzeSelected();
        Assert.StartsWith("解析終了: 成功 0、失敗 1", ui.Status());
        Assert.Equal("解析失敗", ui.TextOf("TargetRowStateText", ui.TargetRow(target)));
        Assert.Contains("解析失敗", ui.TextOf("TargetRowSummaryText", ui.TargetRow(target)));
        Assert.Equal("解析失敗", ui.StateOf(target));
        string detail = ui.DetailOf(target);
        Assert.Contains("解析結果は採用していません。", detail);
        Assert.Contains("code=TARGET_NOT_FOUND", detail);
        Assert.Contains("fake failure", detail);
        Assert.Null(ui.Find("ResultList"));
        // Still unanalyzed: editable, and the guide asks for an analysis.
        Assert.True(ui.DetailButton("EditTargetButton").IsEnabled);
        Assert.StartsWith("3. ", ui.TextOf("NextStepText"));
    }

    [Fact]
    public void ADeleteErrorStopsTheBatchAndAnUnknownOutputIsShownAsUnknownWithTheLog()
    {
        var ui = Start(kind: "result-delete");
        string[] names = ["a", "b", "c"];
        string Archive(string n) => Path.Combine(ui.Fixtures, n + ".zip");
        string Target(string n) => Path.Combine(ui.Fixtures, "out-" + n);
        Item[] items = [new(1, "f1.txt", "MATCHED", 1), new(2, "f2.txt", "MODIFIED", 1)];
        foreach (string n in names)
        {
            Files.Zip(Archive(n), ("f1.txt", "x"), ("f2.txt", "y"));
            Directory.CreateDirectory(Target(n));
            ui.FakeAnalyze(Archive(n), Target(n), "strict", items);
        }
        ui.FakeDelete(Archive("a"), Target("a"), "strict", 2, 1, [new(1, "f1.txt", "DELETE_FAILED", 1)], notSelected: 1);
        ui.FakeDeleteRaw(Archive("c"), Target("c"), [Jsonl.Run("delete", "strict", Archive("c"), Target("c"), 2, 1, true), "{\"v\":2,\"type\":\"entry\"}"], exitCode: 1);
        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out-{{archive.name}}");
        ui.AnalyzeSelected();

        // a fails with DELETE_FAILED: the batch stops, b and c are not started.
        var confirm = ui.OpenDeleteConfirmation();
        ui.Invoke("DeletionConfirmDeleteButton", confirm);
        ui.WaitStatus("削除終了");
        ui.WaitIdle();
        Assert.Contains("後続のTargetは開始していません", ui.Status());
        Assert.Single(ui.Invocations, i => i.Operation == "delete");
        Assert.StartsWith("削除実行済み: エラー終了", ui.StateOf(Target("a")));
        Assert.Contains("DELETE_FAILED", ui.DetailOf(Target("a")));
        Assert.Contains("直前の削除: エラー終了", ui.TextOf("TargetRowSummaryText", ui.TargetRow(Target("a"))));
        foreach (string n in new[] { "b", "c" })
        {
            Assert.StartsWith("解析済み (Strict)", ui.StateOf(Target(n)));
            Assert.Contains("直近の一括削除: 未実行", ui.StateOf(Target(n)));
            Assert.Contains("直近の一括削除: 未実行", ui.TextOf("TargetRowSummaryText", ui.TargetRow(Target(n))));
        }

        // c's output has an unknown record version: the result is unknown and the log is the place to check.
        ui.SelectionMenu("ClearAllButton");
        ui.Toggle(ui.TargetCheck(Target("c")));
        confirm = ui.OpenDeleteConfirmation();
        ui.Invoke("DeletionConfirmDeleteButton", confirm);
        ui.WaitStatus("削除終了");
        ui.WaitIdle();
        Assert.StartsWith("削除実行済み: 結果不明", ui.StateOf(Target("c")));
        string detail = ui.DetailOf(Target("c"));
        Assert.Contains("結果を確定できません。削除されたファイルがある可能性があります。", detail);
        Assert.Contains("実行ログ: " + ui.LogsDirectory, detail);
        Assert.Equal("削除実行済み", ui.TextOf("TargetRowStateText", ui.TargetRow(Target("c"))));
        Assert.Contains("直前の削除: 結果不明", ui.TextOf("TargetRowSummaryText", ui.TargetRow(Target("c"))));
        Assert.Contains("再解析が正常に完了するまで削除できません", ui.TextOf("TargetActionHintText", ui.Main));
    }
}
