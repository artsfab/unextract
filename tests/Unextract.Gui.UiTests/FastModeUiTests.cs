using FlaUI.Core.WindowsAPI;

namespace Unextract.Gui.UiTests;

public sealed class FastModeUiTests : UiTestBase
{
    [Fact]
    public void FastShowsTheWarningAndChangingTheModeAsksBeforeDiscardingResults()
    {
        var ui = Start(kind: "fast");
        string archive = Path.Combine(ui.Fixtures, "a.zip"), target = Path.Combine(ui.Fixtures, "out");
        Files.Zip(archive, ("f.txt", "abc"));
        Directory.CreateDirectory(target);
        ui.FakeAnalyze(archive, target, "fast", [new(1, "f.txt", "SAME_SIZE", 3)]);

        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out");
        Assert.True(ui.IsChecked(ui.Get("StrictRadio")));
        Assert.Null(ui.Find("FastWarningText"));

        // The mode radio buttons handle Click, which the UIA select pattern does not raise: use the real mouse.
        ui.FocusMain();
        ui.ClickUntil("FastRadio", () => ui.Find("FastWarningText") is not null, "the Fast warning");
        Wait.Until(() => ui.Find("FastWarningText") is not null, $"the Fast warning (Fast radio selected: {ui.IsChecked(ui.Get("FastRadio"))})");
        Assert.Equal(Flows.FastWarningText, ui.TextOf("FastWarningText"));
        Assert.True(ui.IsChecked(ui.Get("FastRadio")));

        ui.AnalyzeSelected();
        Assert.Contains("--fast", ui.Invocations.Single(i => i.Operation == "analyze").Args);
        Assert.StartsWith("解析済み (Fast): Same Size 1", ui.StateOf(target), StringComparison.Ordinal);

        // Changing the mode with results asks first. Cancel keeps both the mode and the results.
        ui.ClickUntil("StrictRadio", () => ui.Modal() is not null, "the mode change confirmation");
        var box = ui.WaitModal("モード変更の確認");
        Assert.Contains("既存の解析結果をすべて破棄します", DeletionUiTests.AllText(ui, box));
        ui.PressMessageBox(box, "2");
        Wait.Until(() => ui.Modal() is null, "the message box to close");
        Wait.Until(() => ui.IsChecked(ui.Get("FastRadio")), "Fast to remain selected");
        Assert.NotNull(ui.Find("FastWarningText"));
        Assert.StartsWith("解析済み (Fast)", ui.StateOf(target), StringComparison.Ordinal);

        // The deletion confirmation restates both Fast non-guarantees and the mode.
        var dialog = ui.OpenDeleteConfirmation();
        string body = ui.BodyOf(dialog);
        Assert.StartsWith("Fast", ui.TextOf("DeletionConfirmMode", dialog));
        Assert.Equal(Flows.FastWarningText, ui.TextOf("DeletionConfirmFastWarning", dialog));
        Assert.Contains("内容は比較しません", body);
        DeletionUiTests.PressKey(dialog, VirtualKeyShort.ESCAPE);
        Wait.Until(() => ui.Modal() is null, "the confirmation to close");

        // OK discards every result and the warning disappears.
        ui.ClickUntil("StrictRadio", () => ui.Modal() is not null, "the mode change confirmation");
        box = ui.WaitModal("モード変更の確認");
        ui.PressMessageBox(box, "1");
        Wait.Until(() => ui.Modal() is null, "the message box to close");
        Wait.Until(() => ui.IsChecked(ui.Get("StrictRadio")), "Strict to be selected");
        Wait.Until(() => ui.Find("FastWarningText") is null, "the Fast warning to disappear");
        Assert.Equal("未解析", ui.StateOf(target));
        Assert.DoesNotContain(ui.Invocations, i => i.Operation == "delete");
    }
}
