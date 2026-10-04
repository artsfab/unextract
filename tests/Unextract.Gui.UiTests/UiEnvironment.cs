using System.Runtime.InteropServices;

namespace Unextract.Gui.UiTests;

// Paths handed over by scripts/run-gui-ui-tests.ps1. The tests never publish anything themselves and never fall
// back to a build output: a missing path is a failure, not a skipped test.
internal static class UiEnvironment
{
    public const string RealPackageVariable = "UNEXTRACT_UI_GUI_PACKAGE";
    public const string FakePackageVariable = "UNEXTRACT_UI_FAKE_PACKAGE";

    // Published GUI with the real bundled CLI (cli\unextract.exe).
    public static string RealPackage { get; } = Package(RealPackageVariable);
    // The same GUI files with cli\unextract.exe replaced by the fake CLI.
    public static string FakePackage { get; } = Package(FakePackageVariable);

    private static string Package(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value) || !File.Exists(Path.Combine(value, "unextract-gui.exe")) ||
            !File.Exists(Path.Combine(value, "cli", "unextract.exe")))
            throw new InvalidOperationException($"{variable} must name a GUI package directory with unextract-gui.exe and cli\\unextract.exe (run scripts\\run-gui-ui-tests.ps1). Value: '{value}'");
        return value;
    }

    // Fixtures live where scripts/clean-test-fixtures.ps1 looks: tests/*/bin/*/*/fixtures. Tests never delete them.
    public static string NewFixture(string kind)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "ui-" + kind + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static readonly nint PerMonitorAwareV2 = -4;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);

    // The test host reads window rectangles from UI Automation, so it must see physical pixels like the GUI does.
    public static void RequirePerMonitorV2()
    {
        // Already per-monitor V2 (e.g. from a manifest) is fine; failing to switch is a test failure.
        if (AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorAwareV2)) return;
        if (!SetProcessDpiAwarenessContext(PerMonitorAwareV2) &&
            !AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorAwareV2))
            throw new InvalidOperationException("Could not switch the test host to Per-Monitor V2 DPI awareness: " + Marshal.GetLastWin32Error());
    }
}
