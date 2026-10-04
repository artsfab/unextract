using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Unextract.Gui.UiTests;

// Viewing a row (the details pane) is not selecting it for the batch: browsing, filtering and removing the viewed
// Target never change the check boxes.
public sealed class SelectionUiTests : UiTestBase
{
    [Fact]
    public void ViewingFilteringAndRemovingTheViewedTargetNeverChangeTheSelection()
    {
        var ui = Start(kind: "selection");
        foreach (string name in new[] { "a.zip", "b.zip" }) Files.Zip(Path.Combine(ui.Fixtures, name), ("x.txt", "x"));
        string ta = Path.Combine(ui.Fixtures, "out-a"), tb = Path.Combine(ui.Fixtures, "out-b"), ta2 = Path.Combine(ui.Fixtures, "a2");
        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out-{{archive.name}}");
        ui.AddTarget("a.zip", @"{{archive.dir}}\a2");
        ui.Toggle(ui.TargetCheck(tb));
        Wait.Until(() => ui.ToggleOf(ui.TargetCheck(tb)) == ToggleState.Off, "b's Target to be unselected");
        void AssertSelection()
        {
            Assert.Equal(ToggleState.On, ui.ToggleOf(ui.TargetCheck(ta)));
            Assert.Equal(ToggleState.Off, ui.ToggleOf(ui.TargetCheck(tb)));
        }

        // Viewing rows one after another.
        ui.ViewTarget(tb);
        ui.ViewArchive("a.zip");
        ui.ViewTarget(ta);
        ui.ViewArchive("b.zip");
        AssertSelection();

        // A filter hiding the viewed Target keeps it in the details and says so.
        ui.ViewTarget(ta);
        ui.SetText("ArchiveFilterBox", "b.zip");
        Wait.Until(() => ui.Find("ViewedHiddenText") is not null, "the note that the viewed item is hidden");
        Assert.Equal(ta, ui.Get("TargetPathText").AsTextBox().Text);
        ui.SetText("ArchiveFilterBox", "");
        Wait.Until(() => ui.Find("ViewedHiddenText") is null, "the note to disappear");
        AssertSelection();

        // Removing the viewed Target shows its archive; the other Targets keep their selection.
        ui.ViewTarget(ta2);
        ui.Invoke("RemoveTargetButton", ui.Main);
        Wait.Until(() => ui.Find("ArchiveDetailNameText") is { } n && n.AsTextBox().Text == "a.zip", "the archive details after the removal");
        Assert.Equal("Targets: 1", ui.TextOf("ArchiveTargetCountText", ui.ArchiveRow("a.zip")));
        AssertSelection();
        Assert.True(Directory.Exists(ui.Fixtures), "removing a Target from the list deletes nothing");
        Assert.Empty(ui.Invocations);
    }
}
