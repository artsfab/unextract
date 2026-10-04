using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;

namespace Unextract.Gui.UiTests;

// The log folder button and the folder picker. That Explorer really opens, and choosing a folder in the OS
// dialog, are left to the human acceptance check (docs/MANUAL_TESTS.md#m14).

public sealed class SaveFolderUiTests : UiTestBase
{
    [Fact]
    public void LogFolderButtonExplainsThatTheFolderDoesNotExistYetAndShowsItsPath()
    {
        var ui = Start(kind: "logfolder");
        Assert.False(Directory.Exists(ui.LogsDirectory));
        ui.Invoke("OpenLogFolderButton");
        var box = ui.WaitModal("実行ログ");
        string text = DeletionUiTests.AllText(ui, box);
        Assert.Contains("実行ログの保存フォルダーはまだありません。", text);
        Assert.Contains(ui.LogsDirectory, text);
        Assert.StartsWith(ui.DataRoot, ui.LogsDirectory, StringComparison.OrdinalIgnoreCase);
        ui.Dismiss(box);
        Wait.Until(() => ui.Modal() is null, "the message box to close");
        Assert.False(Directory.Exists(ui.LogsDirectory), "opening the button must not create the folder");
    }

    [Fact]
    public void CancellingTheFolderPickerKeepsTheSearchDirectory()
    {
        var ui = Start(kind: "picker");
        ui.SetText("SearchDirectoryBox", ui.Fixtures);
        ui.Invoke("ChooseFolderButton");
        // The OS folder picker is a separate top-level window of the GUI process, modal to the main window.
        Window? Picker() => ui.Main.ModalWindows.FirstOrDefault() ?? ui.TopLevelWindows().FirstOrDefault(w => w.ClassName == "#32770");
        var picker = Wait.Until(Picker, "the folder picker dialog (windows: " +
            string.Join(", ", ui.TopLevelWindows().Select(w => $"{w.Title}|{w.ClassName}")) + ")");
        picker.SetForeground();
        FlaUI.Core.Input.Keyboard.Type(VirtualKeyShort.ESCAPE);
        Wait.Until(() => Picker() is null, "the folder picker to close on Escape");
        Assert.Equal(ui.Fixtures, ui.Get("SearchDirectoryBox").AsTextBox().Text);
        Assert.False(File.Exists(ui.SettingsPath), "cancelling must not save a directory");
        Assert.True(ui.Get("SearchButton").IsEnabled);
    }
}
