using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Unextract.Gui.UiTests;

public sealed class SmokeUiTests : UiTestBase
{
    [Fact]
    public void SearchWritesSettingsToTheDataRootOnly()
    {
        var ui = Start(kind: "smoke-search");
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

    [Theory]
    [InlineData("relative-root")]
    [InlineData("")]
    public void MisconfiguredDataRootStopsStartupWithoutFallingBack(string value)
    {
        var ui = Start(kind: "smoke-badroot", dataRootValue: value);
        var box = Wait.Until(() => ui.TopLevelWindows().FirstOrDefault(), "the error message box");
        Assert.Contains(box.FindAllDescendants(), e => e.Name.Contains("UNEXTRACT_GUI_TEST_DATA_ROOT", StringComparison.Ordinal));
        box.FindFirstDescendant(ui.Conditions.ByControlType(ControlType.Button))!.AsButton().Invoke();
        Wait.Until(() => ui.HasExited, "the GUI to exit");
        Assert.Equal(1, ui.App.ExitCode);
        Assert.False(File.Exists(ui.SettingsPath));
        Assert.False(Directory.Exists(ui.LogsDirectory));
    }
}
