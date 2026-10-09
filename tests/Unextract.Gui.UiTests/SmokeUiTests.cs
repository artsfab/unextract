using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit.Abstractions;

namespace Unextract.Gui.UiTests;

public sealed class SmokeUiTests(ITestOutputHelper output) : UiTestBase(output)
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    private static readonly string[] MainParts =
    [
        "SearchDirectoryBox", "SearchButton", "ChooseFolderButton", "RecursiveCheckBox", "StrictRadio", "FastRadio", "ArchiveFilterBox",
        "BulkAddButton", "SelectionMenuButton", "ArchiveList", "NextStepText", "AnalyzeSelectedButton", "DeleteSelectedButton",
        "StatusText", "DiagnosticsToggle", "OpenLogFolderButton",
    ];

    // The published process at its start-up size: the main controls have an area inside the window. The size is not changed
    // (the layout of every state and size is checked on the STA by ScreenRenderTests); the window's DPI goes to the output.
    [UiFact]
    public void AtStartupTheMainControlsAreInsideTheWindow()
    {
        var ui = Start();
        uint dpi = GetDpiForWindow((nint)ui.Main.Properties.NativeWindowHandle.Value);
        Rectangle window = ui.Main.BoundingRectangle;
        Output.WriteLine($"window DPI: {dpi} ({dpi * 100 / 96}%), window {window}");
        foreach (string id in MainParts)
        {
            Rectangle rect = ui.Get(id).BoundingRectangle;
            Assert.True(rect.Width > 0 && rect.Height > 0, $"{id} has an empty rectangle {rect}");
            Assert.True(rect.Left >= window.Left && rect.Right <= window.Right && rect.Top >= window.Top && rect.Bottom <= window.Bottom,
                $"{id} {rect} is outside the window {window}");
        }
    }

    [UiFact]
    public void SearchWritesSettingsToTheDataRootOnly()
    {
        var ui = Start();
        Files.Zip(Path.Combine(ui.Fixtures, "a.zip"), ("x.txt", "x"));
        Files.Zip(Path.Combine(ui.Fixtures, "sub", "b.zip"), ("y.txt", "y"));
        Assert.Contains("同梱CLI", ui.TextOf("CliStatusText"));
        ui.SetText("SearchDirectoryBox", ui.Fixtures);
        ui.Invoke("SearchButton");
        Wait.Until(() => ui.TextOf("StatusText").StartsWith("検索完了", StringComparison.Ordinal), "the search to finish");
        ui.Row("a.zip");
        ui.Row("b.zip");
        Assert.Contains(ui.Fixtures.Replace("\\", "\\\\"), File.ReadAllText(ui.SettingsPath), StringComparison.Ordinal);
        Assert.False(Directory.Exists(ui.LogsDirectory));
    }

    [UiTheory]
    [InlineData("relative-root")]
    [InlineData("")]
    public void MisconfiguredDataRootStopsStartupWithoutFallingBack(string value)
    {
        var ui = Start(dataRootValue: value);
        var box = Wait.Until(() => ui.TopLevelWindows().FirstOrDefault(), "the error message box");
        Assert.Contains(box.FindAllDescendants(), e => e.Name.Contains("UNEXTRACT_GUI_TEST_DATA_ROOT", StringComparison.Ordinal));
        box.FindFirstDescendant(ui.Conditions.ByControlType(ControlType.Button))!.AsButton().Invoke();
        Wait.Until(() => ui.HasExited, "the GUI to exit");
        Assert.Equal(1, ui.App.ExitCode);
        Assert.False(File.Exists(ui.SettingsPath));
        Assert.False(Directory.Exists(ui.LogsDirectory));
    }
}
