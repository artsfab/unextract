using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace Unextract.Gui.UiTests;

// Screen pictures of what the STA renders cannot draw (OS message boxes), taken only when run-gui-ui-tests.ps1 -Shots sets
// UNEXTRACT_GUI_SHOTS (outside the fixtures). Ordinary runs take none. docs/TESTING.md#gui-review.
internal static class UiShots
{
    public static void Save(AutomationElement element, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("UNEXTRACT_GUI_SHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        using var image = Capture.Element(element);
        image.ToFile(Path.Combine(directory, name + ".png"));
    }
}
