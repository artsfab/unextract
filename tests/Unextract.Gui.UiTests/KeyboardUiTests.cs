using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit.Abstractions;

namespace Unextract.Gui.UiTests;

public sealed class KeyboardUiTests(ITestOutputHelper output) : UiTestBase(output)
{
    private static string Describe(AutomationElement? e) =>
        e is null ? "(none)" : $"{(e.AutomationId.Length > 0 ? e.AutomationId : "-")}|{e.ControlType}|{e.Name}";

    private static string FocusId(GuiSession ui) => ui.FocusedElement()?.AutomationId ?? "";

    // Real key presses go to the foreground window: wait until the focus has actually moved.
    private static string PressAndWaitForFocusChange(GuiSession ui, VirtualKeyShort key, bool shift = false)
    {
        string before = Describe(ui.FocusedElement());
        if (shift) Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, key);
        else Keyboard.Type(key);
        return Wait.Until(() => { string now = Describe(ui.FocusedElement()); return now != before ? now : null; },
            $"the focus to leave '{before}' after {key}");
    }

    private static void TabUntil(GuiSession ui, Func<AutomationElement, bool> reached, string what, int limit = 60)
    {
        // Real keys go to the foreground window: make sure the GUI (not a window that was closing) is in front.
        if (ui.Modal() is null) ui.FocusMain();
        var visited = new List<string> { Describe(ui.FocusedElement()) };
        for (int i = 0; i < limit && !(ui.FocusedElement() is { } f && reached(f)); i++) visited.Add(PressAndWaitForFocusChange(ui, VirtualKeyShort.TAB));
        Assert.True(ui.FocusedElement() is { } focused && reached(focused), $"Tab did not reach {what}. Visited: {string.Join(" > ", visited)}");
    }

    private static void TabTo(GuiSession ui, string automationId) => TabUntil(ui, e => e.AutomationId == automationId, $"'{automationId}'");

    private static void Focus(GuiSession ui, string automationId)
    {
        ui.FocusMain();
        ui.Get(automationId).Focus();
        Wait.Until(() => FocusId(ui) == automationId, $"the focus on '{automationId}'");
    }

    private static void Key(VirtualKeyShort key) => Keyboard.Type(key);

    [UiFact]
    public void TabOrderOfTheSettingsAreaFollowsTheVisualOrderAndTheModeIsOneStop()
    {
        var ui = Start();
        Focus(ui, "SearchDirectoryBox");
        var order = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            PressAndWaitForFocusChange(ui, VirtualKeyShort.TAB);
            order.Add(FocusId(ui));
        }
        // Search row: input -> Search -> Choose folder -> recursive; the two mode radio buttons are one stop (the selected
        // mode); then the work list's filter and tools.
        Assert.Equal(["SearchButton", "ChooseFolderButton", "RecursiveCheckBox", "StrictRadio", "ArchiveFilterBox", "BulkAddButton", "SelectionMenuButton"], order);

        // Reverse direction is the exact inverse.
        var back = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            PressAndWaitForFocusChange(ui, VirtualKeyShort.TAB, shift: true);
            back.Add(FocusId(ui));
        }
        Assert.Equal(["BulkAddButton", "ArchiveFilterBox", "StrictRadio", "RecursiveCheckBox", "ChooseFolderButton", "SearchButton"], back);
    }

    [UiFact]
    public void TheWholeFocusCycleReachesEveryAreaAndNeverStopsOnTheWindowItself()
    {
        var ui = Start();
        string archive = Path.Combine(ui.Fixtures, "a.zip"), target = Path.Combine(ui.Fixtures, "out");
        Files.Zip(archive, ("f1.txt", "x"));
        Directory.CreateDirectory(target);
        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out");
        ui.ViewTarget(target);
        Focus(ui, "SearchDirectoryBox");
        var cycle = new List<string> { Describe(ui.FocusedElement()) };
        var types = new List<ControlType>();
        for (int i = 0; i < 80; i++)
        {
            cycle.Add(PressAndWaitForFocusChange(ui, VirtualKeyShort.TAB));
            types.Add(ui.FocusedElement()!.ControlType);
            if (FocusId(ui) == "SearchDirectoryBox") break;
        }
        foreach (string step in cycle) Output.WriteLine(step);
        Assert.Equal("SearchDirectoryBox", FocusId(ui));
        Assert.DoesNotContain(ControlType.Window, types);
        Assert.DoesNotContain(ControlType.Pane, types);
        // The list is one stop (its current row); the details, the batch actions and the status area follow in visual order.
        Assert.Single(cycle, s => s.Contains("|ListItem|" + Models.Escape(target), StringComparison.Ordinal));
        int Position(string id) => cycle.FindIndex(s => s.StartsWith(id + "|", StringComparison.Ordinal));
        string[] expected = ["SelectionMenuButton", "TargetPathText", "AnalyzeTargetButton", "EditTargetButton", "RefreshTargetButton", "RemoveTargetButton",
            "AnalyzeSelectedButton", "DeleteSelectedButton", "DiagnosticsToggle", "OpenLogFolderButton"];
        foreach (string id in expected) Assert.True(Position(id) > 0, $"'{id}' is not in the cycle");
        Assert.Equal(expected, expected.OrderBy(Position).ToArray());
        Assert.DoesNotContain(cycle, s => s.StartsWith("ArchiveCheckBox|", StringComparison.Ordinal) || s.StartsWith("TargetCheckBox|", StringComparison.Ordinal));
    }

    [UiFact]
    public void TheWholeFlowWorksWithTheKeyboardOnlyIncludingTheExplicitApproval()
    {
        var ui = Start();
        string archive = Path.Combine(ui.Fixtures, "日本語 a.zip"), target = Path.Combine(ui.Fixtures, "out");
        Files.Zip(archive, ("f1.txt", "x"), ("f2.txt", "y"));
        Directory.CreateDirectory(target);
        ui.FakeAnalyze(archive, target, "strict", [new(1, "f1.txt", "MATCHED", 1), new(2, "f2.txt", "MODIFIED", 1)]);
        ui.FakeDelete(archive, target, "strict", 2, 1, [new(1, "f1.txt", "DELETED", 1)], notSelected: 1);

        // 1-3. Search with Enter in the directory box (the path is set as a value: Japanese text; everything else is real keys).
        Focus(ui, "SearchDirectoryBox");
        ui.SetText("SearchDirectoryBox", ui.Fixtures);
        Key(VirtualKeyShort.RETURN);
        ui.WaitStatus("検索完了");
        ui.WaitIdle();
        Assert.StartsWith("2. ", ui.TextOf("NextStepText"));

        // 4. Into the list (its current row), view the archive with the arrow keys, then add a Target from its details.
        TabUntil(ui, e => e.ControlType == ControlType.ListItem, "the work list");
        Key(VirtualKeyShort.HOME);
        Wait.Until(() => ui.Find("ArchiveDetailNameText") is not null, "the archive details");
        TabTo(ui, "AddTargetButton");
        Key(VirtualKeyShort.SPACE);
        var dialog = ui.WaitModal("Target設定");
        Wait.Until(() => FocusId(ui) == "TargetEditorTemplateBox", "the focus in the template box");
        ui.SetText("TargetEditorTemplateBox", @"{{archive.dir}}\out", dialog);
        Key(VirtualKeyShort.RETURN);
        Wait.Until(() => ui.Modal() is null, "the dialog to close on Enter");
        ui.WaitIdle();
        Assert.Equal("Targets: 1", ui.TextOf("ArchiveTargetCountText", ui.ArchiveRow("日本語 a.zip")));

        // 7. Back to the list: Down views the Target; Space toggles its batch selection, viewing does not.
        ui.FocusMain();
        Focus(ui, "SelectionMenuButton");
        TabUntil(ui, e => e.ControlType == ControlType.ListItem, "the work list");
        Key(VirtualKeyShort.DOWN);
        Wait.Until(() => ui.Find("TargetPathText") is { } t && t.AsTextBox().Text == target, "the Target details");
        var check = ui.TargetCheck(target);
        Assert.Equal(ToggleState.On, ui.ToggleOf(check));
        Key(VirtualKeyShort.SPACE);
        Wait.Until(() => ui.ToggleOf(check) == ToggleState.Off, "the Target to be deselected");
        Key(VirtualKeyShort.SPACE);
        Wait.Until(() => ui.ToggleOf(check) == ToggleState.On, "the Target to be selected again");

        // 5-6. Analyze and look at the result.
        TabTo(ui, "AnalyzeSelectedButton");
        Key(VirtualKeyShort.SPACE);
        ui.WaitStatus("解析終了");
        ui.WaitIdle();
        Assert.StartsWith("解析済み (Strict): Matched 1", ui.TextOf("TargetStateText"));
        Assert.StartsWith("4. ", ui.TextOf("NextStepText"));

        // 8. The confirmation: Escape and Enter (default = cancel) close it without starting anything.
        TabTo(ui, "DeleteSelectedButton");
        Key(VirtualKeyShort.SPACE);
        var confirm = ui.WaitModal("削除の確認");
        Wait.Until(() => FocusId(ui) == "DeletionConfirmCancelButton", "the focus on the cancel button");
        Key(VirtualKeyShort.ESCAPE);
        Wait.Until(() => ui.Modal() is null, "the confirmation to close on Escape");
        Wait.Until(() => FocusId(ui) == "DeleteSelectedButton", "the focus to return to the delete button");
        Key(VirtualKeyShort.SPACE);
        confirm = ui.WaitModal("削除の確認");
        Wait.Until(() => FocusId(ui) == "DeletionConfirmCancelButton", "the focus on the cancel button");
        Key(VirtualKeyShort.RETURN);
        Wait.Until(() => ui.Modal() is null, "the confirmation to close on Enter");
        Assert.DoesNotContain(ui.Invocations, i => i.Operation == "delete");

        // 8-9. Explicit approval: Shift+Tab from the cancel button reaches the delete button (visual order), Space approves.
        Wait.Until(() => FocusId(ui) == "DeleteSelectedButton", "the focus to return to the delete button");
        Key(VirtualKeyShort.SPACE);
        confirm = ui.WaitModal("削除の確認");
        Wait.Until(() => FocusId(ui) == "DeletionConfirmCancelButton", "the focus on the cancel button");
        PressAndWaitForFocusChange(ui, VirtualKeyShort.TAB, shift: true);
        Assert.Equal("DeletionConfirmDeleteButton", FocusId(ui));
        Key(VirtualKeyShort.SPACE);
        ui.WaitStatus("削除終了");
        ui.WaitIdle();

        // 10. The result.
        var delete = Assert.Single(ui.Invocations, i => i.Operation == "delete");
        Assert.Equal(["f1.txt"], delete.EntryLines);
        Assert.StartsWith("削除実行済み: 削除完了", ui.TextOf("TargetStateText"));
        Assert.Contains("削除成功: 1 件", ui.Get("TargetAnalysisDetailText").AsTextBox().Text);
    }

    [UiFact]
    public void ArrowKeysChangeTheModeLikeARadioGroupWithTheSameConfirmation()
    {
        var ui = Start();
        string archive = Path.Combine(ui.Fixtures, "a.zip"), target = Path.Combine(ui.Fixtures, "out");
        Files.Zip(archive, ("f.txt", "x"));
        Directory.CreateDirectory(target);
        ui.FakeAnalyze(archive, target, "strict", [new(1, "f.txt", "MATCHED", 1)]);
        Focus(ui, "StrictRadio");
        Key(VirtualKeyShort.RIGHT);
        Wait.Until(() => ui.Find("FastWarningText") is not null, "the Fast warning");
        Assert.True(ui.IsChecked(ui.Get("FastRadio")));
        Assert.Equal("FastRadio", FocusId(ui));
        Key(VirtualKeyShort.LEFT);
        Wait.Until(() => ui.Find("FastWarningText") is null, "Strict again");

        // With results the arrow asks first; cancelling keeps the mode.
        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out");
        ui.AnalyzeSelected();
        Focus(ui, "StrictRadio");
        Key(VirtualKeyShort.RIGHT);
        var box = ui.WaitModal("モード変更の確認");
        ui.PressMessageBox(box, "2");
        Wait.Until(() => ui.Modal() is null, "the message box to close");
        Assert.True(ui.IsChecked(ui.Get("StrictRadio")));
        Assert.StartsWith("解析済み (Strict)", ui.StateOf(target), StringComparison.Ordinal);
    }

    [UiFact]
    public void TargetEditorRegistersWithEnterCancelsWithEscapeAndRefusesAnInvalidTemplate()
    {
        var ui = Start();
        string archive = Path.Combine(ui.Fixtures, "a.zip");
        Files.Zip(archive, ("f1.txt", "x"));
        ui.Search();
        string Targets() => ui.TextOf("ArchiveTargetCountText", ui.ArchiveRow("a.zip"));

        Window OpenEditor()
        {
            ui.ViewArchive("a.zip");
            ui.Invoke("AddTargetButton", ui.Main);
            var editor = ui.WaitModal("Target設定");
            editor.SetForeground();
            return editor;
        }

        // Invalid: the registration button is disabled and Enter does not register or close the dialog.
        var dialog = OpenEditor();
        ui.SetText("TargetEditorTemplateBox", @"D:\bad\..\path", dialog);
        ui.Get("TargetEditorTemplateBox", dialog).Focus();
        Wait.Until(() => !ui.Get("TargetEditorOkButton", dialog).IsEnabled, "the registration button to be disabled");
        Assert.DoesNotContain("解決先", ui.TextOf("TargetEditorPreview", dialog));
        Key(VirtualKeyShort.RETURN);
        Assert.NotNull(ui.Modal());
        Assert.Equal("Targets: 0", Targets());

        // Escape closes without registering.
        Key(VirtualKeyShort.ESCAPE);
        Wait.Until(() => ui.Modal() is null, "the dialog to close on Escape");
        Assert.Equal("Targets: 0", Targets());

        // A valid template: the preview shows the resolved path and Enter registers.
        dialog = OpenEditor();
        ui.SetText("TargetEditorTemplateBox", @"{{archive.dir}}\kept", dialog);
        ui.Get("TargetEditorTemplateBox", dialog).Focus();
        Wait.Until(() => ui.TextOf("TargetEditorPreview", dialog) == "解決先: " + Path.Combine(ui.Fixtures, "kept"), "the preview to show the resolved path");
        Assert.True(ui.Get("TargetEditorOkButton", dialog).IsEnabled);
        Key(VirtualKeyShort.RETURN);
        Wait.Until(() => ui.Modal() is null, "the dialog to close on Enter");
        ui.WaitIdle();
        Assert.Equal("Targets: 1", Targets());
    }
}
