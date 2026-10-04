using System.IO;

namespace Unextract.Gui.Services;

// Where the settings file and the execution logs go. Only the composition root reads the environment.
internal sealed record GuiDataLocations(string? SettingsPath, string? LogDirectory, string? Error);

// Internal switch for the UI E2E tests, not a user feature: when UNEXTRACT_GUI_TEST_DATA_ROOT is set, the
// settings and logs move under that root. A bad value never falls back to the normal location.
internal static class GuiDataRoot
{
    public const string VariableName = "UNEXTRACT_GUI_TEST_DATA_ROOT";

    // value == null: variable not set, keep the normal locations (both results null, no error).
    public static GuiDataLocations Resolve(string? value)
    {
        if (value is null) return new(null, null, null);
        try
        {
            if (Path.IsPathFullyQualified(value) && Directory.Exists(value))
                return new(Path.Combine(value, "settings.json"), Path.Combine(value, "logs"), null);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        return new(null, null, $"{VariableName} は絶対パスの既存ディレクトリでなければなりません。");
    }
}
