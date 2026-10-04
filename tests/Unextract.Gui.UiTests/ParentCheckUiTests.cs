using FlaUI.Core.Definitions;

namespace Unextract.Gui.UiTests;

public sealed class ParentCheckUiTests : UiTestBase
{
    private GuiSession Prepare(string kind)
    {
        var ui = Start(kind: kind);
        foreach (string name in new[] { "a.zip", "b.zip", "c.zip" }) Files.Zip(Path.Combine(ui.Fixtures, name), ("x.txt", "x"));
        ui.Search();
        // a: two Targets, b: one Target, c: none.
        ui.AddTarget("a.zip", @"{{archive.dir}}\ta1");
        ui.AddTarget("a.zip", @"{{archive.dir}}\ta2");
        ui.AddTarget("b.zip", @"{{archive.dir}}\tb1");
        return ui;
    }

    [Fact]
    public void ParentCheckFollowsItsTargetsAndDrivesThemBack()
    {
        var ui = Prepare("parent-check");
        string t1 = Path.Combine(ui.Fixtures, "ta1"), t2 = Path.Combine(ui.Fixtures, "ta2");
        var parent = ui.ArchiveCheck("a.zip");
        // Targets start selected.
        Assert.Equal(ToggleState.On, ui.ToggleOf(parent));
        Assert.Equal(ToggleState.On, ui.ToggleOf(ui.TargetCheck(t1)));
        Assert.Equal(ToggleState.On, ui.ToggleOf(ui.TargetCheck(t2)));

        ui.Toggle(parent);
        Wait.Until(() => ui.ToggleOf(parent) == ToggleState.Off, "the parent to turn off");
        Assert.Equal(ToggleState.Off, ui.ToggleOf(ui.TargetCheck(t1)));
        Assert.Equal(ToggleState.Off, ui.ToggleOf(ui.TargetCheck(t2)));

        ui.Toggle(ui.TargetCheck(t1));
        Wait.Until(() => ui.ToggleOf(parent) == ToggleState.Indeterminate, "the parent to become indeterminate");
        Assert.Equal(ToggleState.On, ui.ToggleOf(ui.TargetCheck(t1)));
        Assert.Equal(ToggleState.Off, ui.ToggleOf(ui.TargetCheck(t2)));

        // From the indeterminate state the parent sets every Target to one common state.
        ui.Toggle(parent);
        Wait.Until(() => ui.ToggleOf(parent) != ToggleState.Indeterminate, "the parent to leave the indeterminate state");
        var common = ui.ToggleOf(parent);
        Assert.Equal(common, ui.ToggleOf(ui.TargetCheck(t1)));
        Assert.Equal(common, ui.ToggleOf(ui.TargetCheck(t2)));

        ui.Toggle(ui.TargetCheck(t2));
        ui.Toggle(ui.TargetCheck(t1));
        Wait.Until(() => ui.ToggleOf(parent) != ToggleState.Indeterminate, "the parent to follow both Targets");
    }

    [Fact]
    public void ParentWithoutTargetsIsDisabledAndUnchecked()
    {
        var ui = Prepare("parent-none");
        var parent = ui.ArchiveCheck("c.zip");
        Assert.False(parent.IsEnabled);
        Assert.Equal(ToggleState.Off, ui.ToggleOf(parent));
        Assert.True(ui.ArchiveCheck("b.zip").IsEnabled);
        Assert.Equal("Targets: 0", ui.TextOf("ArchiveTargetCountText", ui.ArchiveRow("c.zip")));
    }

    [Fact]
    public void SelectingAndClearingVisibleArchivesNeverReachesHiddenOnes()
    {
        var ui = Prepare("parent-visible");
        ui.SetText("ArchiveFilterBox", "a.zip");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 1 / 検索 3", StringComparison.Ordinal), "the filter to apply");
        ui.SelectionMenu("ClearVisibleButton");
        Wait.Until(() => ui.ToggleOf(ui.ArchiveCheck("a.zip")) == ToggleState.Off, "visible archives to be cleared");
        ui.SetText("ArchiveFilterBox", "");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 3 / 検索 3", StringComparison.Ordinal), "the filter to clear");
        Assert.Equal(ToggleState.Off, ui.ToggleOf(ui.ArchiveCheck("a.zip")));
        Assert.Equal(ToggleState.On, ui.ToggleOf(ui.ArchiveCheck("b.zip")));

        ui.SetText("ArchiveFilterBox", "b.zip");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 1 / 検索 3", StringComparison.Ordinal), "the filter to apply");
        ui.SelectionMenu("ClearVisibleButton");
        ui.SetText("ArchiveFilterBox", "a.zip");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 1 / 検索 3", StringComparison.Ordinal), "the filter to apply");
        ui.SelectionMenu("SelectVisibleButton");
        ui.SetText("ArchiveFilterBox", "");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 3 / 検索 3", StringComparison.Ordinal), "the filter to clear");
        Assert.Equal(ToggleState.On, ui.ToggleOf(ui.ArchiveCheck("a.zip")));
        Assert.Equal(ToggleState.Off, ui.ToggleOf(ui.ArchiveCheck("b.zip")));

        // "All" buttons reach hidden archives too.
        ui.SetText("ArchiveFilterBox", "c.zip");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 1 / 検索 3", StringComparison.Ordinal), "the filter to apply");
        ui.SelectionMenu("SelectAllButton");
        ui.SetText("ArchiveFilterBox", "");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 3 / 検索 3", StringComparison.Ordinal), "the filter to clear");
        Assert.Equal(ToggleState.On, ui.ToggleOf(ui.ArchiveCheck("b.zip")));
        ui.SelectionMenu("ClearAllButton");
        Wait.Until(() => ui.ToggleOf(ui.ArchiveCheck("a.zip")) == ToggleState.Off && ui.ToggleOf(ui.ArchiveCheck("b.zip")) == ToggleState.Off, "all to be cleared");
    }
}
