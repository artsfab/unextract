using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Unextract.Gui.UiTests;

// Operations on the real GUI through UI Automation patterns (Invoke, Value, Toggle, SelectionItem). The window is a
// flat work list (archive rows followed by their Target rows) and a details pane for the viewed row; the helpers
// below keep that structure out of the tests, which speak in the requirement's terms (view, select, analyze).
internal static class Flows
{
    public const string FastWarningText =
        "Fastは内容の一致を確認しません。同じパス・同じサイズの変更されたファイルも削除候補になり、ZIPから正常に展開できることも確認しません。";

    public static string Status(this GuiSession ui) => ui.TextOf("StatusText");

    // Every busy section of the GUI ends with the settings becoming editable again.
    public static void WaitIdle(this GuiSession ui, string what = "the GUI to become idle") =>
        Wait.Until(() => ui.Get("DeleteSelectedButton").IsEnabled, what);

    public static void WaitStatus(this GuiSession ui, string prefix) =>
        Wait.Until(() => ui.Status().StartsWith(prefix, StringComparison.Ordinal), $"the status '{prefix}...' (now '{ui.Status()}')");

    public static void Search(this GuiSession ui, string? directory = null)
    {
        ui.SetText("SearchDirectoryBox", directory ?? ui.Fixtures);
        ui.Invoke("SearchButton");
        ui.WaitStatus("検索完了");
        ui.WaitIdle();
    }

    // One Target per archive from the given template, through the bulk addition dialog.
    public static void BulkAdd(this GuiSession ui, string template = "{{archive.dir}}")
    {
        ui.Invoke("BulkAddButton");
        var dialog = ui.WaitModal("Target設定");
        ui.SetText("TargetEditorTemplateBox", template, dialog);
        Wait.Until(() => ui.Get("TargetEditorOkButton", dialog).IsEnabled, "the bulk rule to be accepted");
        ui.Invoke("TargetEditorOkButton", dialog);
        Wait.Until(() => ui.Modal() is null, "the bulk addition dialog to close");
        ui.WaitStatus("Target追加");
        ui.WaitIdle();
    }

    // "Add a Target to this archive" in the details of the viewed archive: template typed, registered with its OK button.
    public static void AddTarget(this GuiSession ui, string archiveFileName, string template)
    {
        ui.ViewArchive(archiveFileName);
        ui.Invoke("AddTargetButton", ui.Main);
        var dialog = ui.WaitModal("Target設定");
        ui.SetText("TargetEditorTemplateBox", template, dialog);
        ui.Invoke("TargetEditorOkButton", dialog);
        Wait.Until(() => ui.Modal() is null, "the Target settings dialog to close");
        ui.WaitIdle();
    }

    public static AutomationElement ArchiveRow(this GuiSession ui, string fileName) => ui.Row(fileName);

    public static AutomationElement TargetRow(this GuiSession ui, string targetPath) => ui.Row(Models.Escape(targetPath));

    // Viewing = making the row the list's current row; the details pane follows. It never changes the selection.
    public static void ViewArchive(this GuiSession ui, string fileName)
    {
        ui.ArchiveRow(fileName).Patterns.SelectionItem.Pattern.Select();
        Wait.Until(() => ui.Find("ArchiveDetailNameText") is { } name && name.AsTextBox().Text == fileName, $"the details of '{fileName}'");
    }

    public static void ViewTarget(this GuiSession ui, string targetPath)
    {
        ui.TargetRow(targetPath).Patterns.SelectionItem.Pattern.Select();
        string shown = Models.Escape(targetPath);
        Wait.Until(() => ui.Find("TargetPathText") is { } path && path.AsTextBox().Text == shown, $"the details of '{shown}'");
    }

    public static AutomationElement ArchiveCheck(this GuiSession ui, string fileName) => ui.Get("ArchiveCheckBox", ui.ArchiveRow(fileName));

    public static AutomationElement TargetCheck(this GuiSession ui, string targetPath) => ui.Get("TargetCheckBox", ui.TargetRow(targetPath));

    // The state line of the Target's details (viewing it first).
    public static string StateOf(this GuiSession ui, string targetPath)
    {
        ui.ViewTarget(targetPath);
        return ui.TextOf("TargetStateText", ui.Main);
    }

    public static string DetailOf(this GuiSession ui, string targetPath)
    {
        ui.ViewTarget(targetPath);
        return ui.Find("TargetAnalysisDetailText", ui.Main)?.AsTextBox().Text ?? "";
    }

    // A details-pane button of the viewed Target or archive.
    public static AutomationElement DetailButton(this GuiSession ui, string id) => ui.Get(id, ui.Main);

    // The four selection operations are in the "change selection" menu (a popup window of the GUI process).
    public static void SelectionMenu(this GuiSession ui, string id)
    {
        ui.Invoke("SelectionMenuButton");
        var item = Wait.Until(() => ui.TopLevelWindows().Select(w => w.FindFirstDescendant(ui.Conditions.ByAutomationId(id))).FirstOrDefault(e => e is not null),
            $"the menu item '{id}'");
        item.Patterns.Invoke.Pattern.Invoke();
        Wait.Until(() => ui.TopLevelWindows().All(w => w.FindFirstDescendant(ui.Conditions.ByAutomationId(id)) is null), "the menu to close");
    }

    // Analyzes every selected, unanalyzed Target and waits until the queue is finished.
    public static void AnalyzeSelected(this GuiSession ui)
    {
        ui.Invoke("AnalyzeSelectedButton");
        ui.WaitStatus("解析終了");
        ui.WaitIdle();
    }

    // Opens the deletion confirmation (also the "nothing to delete" form of the same window).
    public static Window OpenDeleteConfirmation(this GuiSession ui)
    {
        ui.Invoke("DeleteSelectedButton");
        return ui.WaitModal();
    }

    // Scenario helpers: the fake CLI answers by operation, archive and target. A waiting response emits
    // `waitAfter` lines, then blocks until the returned file is created (ui.Scenario.Release).
    public static string FakeAnalyze(this GuiSession ui, string archive, string target, string mode, Item[] items,
        bool wait = false, int waitAfter = 1, int? maxUses = null)
    {
        string release = ui.Scenario.Add("analyze", FakeResults.Analysis(archive, target, mode, items), archive: archive, target: target,
            maxUses: maxUses, wait: wait, waitAfterLines: waitAfter);
        ui.SaveScenario();
        return release;
    }

    public static void FakeAnalysisFailure(this GuiSession ui, string archive, string target, int? maxUses = null)
    {
        ui.Scenario.Add("analyze", [Jsonl.InputError("TARGET_NOT_FOUND", "fake failure")], exitCode: 1, archive: archive, target: target, maxUses: maxUses);
        ui.SaveScenario();
    }

    public static string FakeDelete(this GuiSession ui, string archive, string target, string mode, int entriesTotal, int selected,
        Item[] processed, int notSelected, bool wait = false, int waitAfter = 1)
    {
        // Like the real CLI, the process exit code equals result.exit_code (1 when an entry ends DELETE_FAILED).
        int exitCode = processed.Any(i => i.Status == "DELETE_FAILED") ? 1 : 0;
        string release = ui.Scenario.Add("delete", FakeResults.Delete(archive, target, mode, entriesTotal, selected, processed, notSelected),
            exitCode: exitCode, archive: archive, target: target, wait: wait, waitAfterLines: waitAfter);
        ui.SaveScenario();
        return release;
    }

    // A delete with arbitrary output lines and exit code (interrupted output, incompatible records, ...).
    public static void FakeDeleteRaw(this GuiSession ui, string archive, string target, string[] lines, int exitCode)
    {
        ui.Scenario.Add("delete", lines, exitCode: exitCode, archive: archive, target: target);
        ui.SaveScenario();
    }

    // The whole text of the deletion confirmation: the warnings and numbers (names) and the full list (the read-only body).
    public static string BodyOf(this GuiSession ui, Window dialog) =>
        DeletionUiTests.AllText(ui, dialog) + "\n" + ui.Get("DeletionConfirmBody", dialog).AsTextBox().Text;

    public static void CloseConfirmation(this GuiSession ui, Window dialog)
    {
        ui.Invoke("DeletionConfirmCancelButton", dialog);
        Wait.Until(() => ui.Modal() is null, "the confirmation to close");
    }

    // Presses the OK button of a standard message box (id 1 for OK/Cancel boxes) or its only button.
    public static void Dismiss(this GuiSession ui, Window box) =>
        box.FindFirstDescendant(ui.Conditions.ByControlType(ControlType.Button))!.AsButton().Invoke();
}

internal static class Models
{
    // The GUI's display conversion for control and format characters, as the tests expect to see it.
    public static string Escape(string value)
    {
        var text = new System.Text.StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            var category = System.Text.Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                or System.Globalization.UnicodeCategory.Surrogate)
                text.Append($"\\u{{{rune.Value:X4}}}");
            else text.Append(rune.ToString());
        }
        return text.ToString();
    }
}
