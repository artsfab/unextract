namespace Unextract.Gui.UiTests;

public sealed class LockUiTests : UiTestBase
{
    // Everything that configures the session at the window level.
    private static readonly string[] LockedTopLevel =
    [
        "SearchDirectoryBox", "SearchButton", "ChooseFolderButton", "RecursiveCheckBox", "StrictRadio", "FastRadio",
        "BulkAddButton", "SelectionMenuButton", "AnalyzeSelectedButton", "DeleteSelectedButton",
    ];

    // The row check boxes, and the operations in the details of the archive and of the Target (viewing them is allowed).
    private static void AssertLocked(GuiSession ui, string archiveName, string targetPath, bool locked)
    {
        foreach (string id in LockedTopLevel) Assert.True(!locked == ui.Get(id).IsEnabled, $"{id} enabled={ui.Get(id).IsEnabled}");
        Assert.Equal(!locked, ui.ArchiveCheck(archiveName).IsEnabled);
        Assert.Equal(!locked, ui.TargetCheck(targetPath).IsEnabled);
        ui.ViewArchive(archiveName);
        Assert.Equal(!locked, ui.DetailButton("AddTargetButton").IsEnabled);
        Assert.Equal(!locked, ui.DetailButton("AnalyzeArchiveButton").IsEnabled);
        ui.ViewTarget(targetPath);
        Assert.Equal(!locked, ui.DetailButton("AnalyzeTargetButton").IsEnabled);
        Assert.Equal(!locked, ui.DetailButton("RemoveTargetButton").IsEnabled);
        Assert.Equal(!locked, ui.DetailButton("RefreshTargetButton").IsEnabled);
        if (locked) Assert.Contains("処理の実行中は", ui.TextOf("TargetActionHintText", ui.Main));
    }

    private (GuiSession Ui, string Archive, string Target) Prepare(string kind)
    {
        var ui = Start(kind: kind);
        string archive = Path.Combine(ui.Fixtures, "a.zip"), target = Path.Combine(ui.Fixtures, "out");
        Files.Zip(archive, ("f1.txt", "x"), ("f2.txt", "y"));
        Directory.CreateDirectory(target);
        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out");
        return (ui, archive, target);
    }

    [Fact]
    public void WhileAnalyzingTheConfigurationIsLockedButBrowsingAndCancellingWork()
    {
        var (ui, archive, target) = Prepare("lock-analysis");
        string release = ui.FakeAnalyze(archive, target, "strict", [new(1, "f1.txt", "MATCHED", 1), new(2, "f2.txt", "MODIFIED", 1)], wait: true, waitAfter: 2);
        AssertLocked(ui, "a.zip", target, locked: false);
        Assert.True(ui.DetailButton("EditTargetButton").IsEnabled, "an unanalyzed Target can be edited");
        Assert.Null(ui.Find("CancelAnalysisButton"));

        ui.Invoke("AnalyzeSelectedButton");
        Wait.Until(() => ui.Find("CancelAnalysisButton") is not null, "the cancel button");
        Wait.Until(() => ui.Invocations.Count == 1, "the fake CLI to start");
        Wait.Until(() => ui.TextOf("EntryProgressText").Contains("1 / 2", StringComparison.Ordinal), "the entry progress");
        Assert.Contains("1 / 1 Targets", ui.TextOf("OverallProgressText"));
        Assert.Contains("解析中です", ui.TextOf("NextStepText"));
        AssertLocked(ui, "a.zip", target, locked: true);
        Assert.Equal("解析中", ui.StateOf(target));
        Assert.False(ui.DetailButton("EditTargetButton").IsEnabled);
        Assert.True(ui.Get("CancelAnalysisButton").IsEnabled);

        // Browsing stays available: filter, viewing other rows, and the log folder button.
        Assert.True(ui.Get("ArchiveFilterBox").IsEnabled);
        ui.SetText("ArchiveFilterBox", "a.z");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 1 / 検索 1", StringComparison.Ordinal), "the filter to apply");
        ui.SetText("ArchiveFilterBox", "");
        ui.ViewArchive("a.zip");
        ui.ViewTarget(target);
        Assert.True(ui.Get("OpenLogFolderButton").IsEnabled);

        ui.Invoke("CancelAnalysisButton");
        ui.WaitStatus("解析終了");
        ui.WaitIdle();
        Assert.Null(ui.Find("CancelAnalysisButton"));
        AssertLocked(ui, "a.zip", target, locked: false);
        Assert.Equal("キャンセルされました（未解析）", ui.StateOf(target));
        Assert.True(ui.DetailButton("EditTargetButton").IsEnabled);
        Assert.Single(ui.Invocations);
        ui.Scenario.Release(release);
    }

    [Fact]
    public void WhileDeletingTheConfigurationIsLockedAndADeletedTargetStaysLockedUntilReanalysis()
    {
        var (ui, archive, target) = Prepare("lock-delete");
        Item[] items = [new(1, "f1.txt", "MATCHED", 1), new(2, "f2.txt", "MODIFIED", 1)];
        ui.FakeAnalyze(archive, target, "strict", items);
        string release = ui.FakeDelete(archive, target, "strict", 2, 1, [new(1, "f1.txt", "DELETED", 1)], notSelected: 1, wait: true, waitAfter: 1);
        ui.AnalyzeSelected();
        Assert.StartsWith("解析済み (Strict): Matched 1", ui.StateOf(target), StringComparison.Ordinal);
        Assert.False(ui.DetailButton("EditTargetButton").IsEnabled, "an analyzed Target cannot be edited");
        Assert.Contains("解析済みのTargetは編集できません", ui.TextOf("TargetActionHintText", ui.Main));

        var dialog = ui.OpenDeleteConfirmation();
        ui.Invoke("DeletionConfirmDeleteButton", dialog);
        Wait.Until(() => ui.Invocations.Count(i => i.Operation == "delete") == 1, "the fake delete to start");
        Wait.Until(() => ui.StateOf(target) == "削除中", "the Target to be deleting");
        AssertLocked(ui, "a.zip", target, locked: true);
        Assert.Equal("1 / 1 Targets", ui.TextOf("OverallProgressText"));
        Assert.Contains("削除は途中で取り消せません", ui.TextOf("NextStepText"));
        // Browsing works while the delete runs; there is no way to cancel a delete.
        Assert.Null(ui.Find("CancelAnalysisButton"));
        Assert.True(ui.Get("ArchiveFilterBox").IsEnabled);

        ui.Scenario.Release(release);
        ui.WaitStatus("削除終了");
        ui.WaitIdle();
        AssertLocked(ui, "a.zip", target, locked: false);
        Assert.StartsWith("削除実行済み: 削除完了", ui.StateOf(target), StringComparison.Ordinal);
        Assert.Equal("削除実行済み", ui.TextOf("TargetRowStateText", ui.TargetRow(target)));
        Assert.Equal("再解析", ui.DetailButton("AnalyzeTargetButton").Name);
        // Delete history: not editable, and not deletable again until a successful re-analysis.
        Assert.False(ui.DetailButton("EditTargetButton").IsEnabled);
        Assert.True(ui.DetailButton("RemoveTargetButton").IsEnabled);
        var none = ui.OpenDeleteConfirmation();
        Assert.Equal("削除", none.Title);
        Assert.Contains("削除実行済みです。再解析が正常に完了するまで削除できません。", ui.BodyOf(none));
        Assert.Null(ui.Find("DeletionConfirmDeleteButton", none));
        ui.CloseConfirmation(none);
        Assert.Single(ui.Invocations, i => i.Operation == "delete");

        // A successful re-analysis makes it deletable again (the edit lock from the delete history stays).
        ui.FakeAnalyze(archive, target, "strict", items);
        ui.Invoke("AnalyzeTargetButton", ui.Main);
        ui.WaitStatus("解析終了");
        ui.WaitIdle();
        Assert.StartsWith("解析済み (Strict): Matched 1", ui.StateOf(target), StringComparison.Ordinal);
        Assert.Contains("直前の削除: 削除完了", ui.StateOf(target));
        Assert.False(ui.DetailButton("EditTargetButton").IsEnabled);
        var again = ui.OpenDeleteConfirmation();
        Assert.Equal("削除の確認", again.Title);
        Assert.Contains("削除対象のTarget（1 件", ui.BodyOf(again));
        DeletionUiTests.PressKey(again, FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Wait.Until(() => ui.Modal() is null, "the confirmation to close");
    }

    [Fact]
    public void ResearchAsksBeforeDiscardingTheSession()
    {
        var (ui, archive, target) = Prepare("lock-research");
        // The session has an archive with a Target: a second search must ask first.
        Assert.Equal("Targets: 1", ui.TextOf("ArchiveTargetCountText", ui.ArchiveRow("a.zip")));
        ui.Invoke("SearchButton");
        var box = ui.WaitModal("再検索の確認");
        Assert.Contains("現在のArchive・Target設定・選択・解析結果を破棄します", DeletionUiTests.AllText(ui, box));
        ui.PressMessageBox(box, "2");
        Wait.Until(() => ui.Modal() is null, "the message box to close");
        Assert.Equal("Targets: 1", ui.TextOf("ArchiveTargetCountText", ui.ArchiveRow("a.zip")));
        Assert.Equal(archive, Path.Combine(ui.Fixtures, "a.zip"));
        Assert.StartsWith("Target追加", ui.Status());

        ui.Invoke("SearchButton");
        box = ui.WaitModal("再検索の確認");
        ui.PressMessageBox(box, "1");
        Wait.Until(() => ui.Modal() is null, "the message box to close");
        ui.WaitStatus("検索完了");
        ui.WaitIdle();
        Assert.Equal("Targets: 0", ui.TextOf("ArchiveTargetCountText", ui.ArchiveRow("a.zip")));
        Assert.Null(ui.Main.FindFirstDescendant(ui.Conditions.ByName(Models.Escape(target))));
        Assert.Empty(ui.Invocations);
        Assert.True(Directory.Exists(target));
    }
}
