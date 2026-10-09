using System.Diagnostics;
using System.Text;
using System.Runtime.CompilerServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Unextract.Gui.UiTests;

internal static class Startup
{
    [ModuleInitializer]
    public static void Initialize() => UiEnvironment.RequirePerMonitorV2();
}

// Polls a condition until it holds, with a deadline; never a fixed sleep. Waits longer than one second are written to the
// test output with what was awaited; a timeout names the exception types (and counts) seen while polling.
internal static class Wait
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Reported = TimeSpan.FromSeconds(1);

    // The current test's output (UiTestBase); UI tests run one at a time.
    public static Action<string>? Log { get; set; }

    public static T Until<T>(Func<T?> probe, string what, TimeSpan? limit = null) where T : class
    {
        var started = DateTime.UtcNow;
        var deadline = started + (limit ?? Default);
        var seen = new Dictionary<string, int>();
        Exception? last = null;
        while (true)
        {
            try
            {
                if (probe() is { } value)
                {
                    var elapsed = DateTime.UtcNow - started;
                    if (elapsed > Reported) Log?.Invoke($"wait {elapsed.TotalSeconds:F1} s: {what}");
                    return value;
                }
                last = null;
            }
            catch (Exception e)
            {
                // UI Automation reports transient states (element gone, not ready) as various exception types: retry until the deadline.
                last = e;
                seen[e.GetType().Name] = seen.GetValueOrDefault(e.GetType().Name) + 1;
            }
            if (DateTime.UtcNow > deadline)
            {
                string exceptions = seen.Count == 0 ? "" : "\nExceptions while waiting: " + string.Join(", ", seen.Select(p => $"{p.Key} x{p.Value}"));
                throw new TimeoutException($"Timed out after {(DateTime.UtcNow - started).TotalSeconds:F1} s waiting for: {what}{exceptions}" +
                    (last is null ? "" : "\nLast: " + last.Message));
            }
            Thread.Sleep(30);
        }
    }

    public static void Until(Func<bool> condition, string what, TimeSpan? limit = null) =>
        Until<object>(() => condition() ? new object() : null, what, limit);
}

// One GUI process started from a published package, isolated from the user's profile:
// its own data root (settings, logs), its own TMP/TEMP, and its own scenario for the fake CLI.
internal sealed class GuiSession : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private Window? _main;
    private AutomationElement? _workList;
    private bool _disposed;

    public string Root { get; }
    public string DataRoot { get; }
    public string TempDirectory { get; }
    public string Fixtures { get; }
    public Scenario Scenario { get; }
    public Application App { get; }
    public string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public string LogsDirectory => Path.Combine(DataRoot, "logs");
    public ConditionFactory Conditions => _automation.ConditionFactory;

    // root: a new fixture directory owned by the test (UiTestBase deletes it after the GUI has exited).
    // dataRootValue: null = the fixture's data directory; otherwise the raw environment value (for misconfiguration tests).
    public GuiSession(string root, bool fake, string? dataRootValue = null, bool setDataRoot = true)
    {
        Root = root;
        DataRoot = Directory.CreateDirectory(Path.Combine(Root, "data")).FullName;
        TempDirectory = Directory.CreateDirectory(Path.Combine(Root, "tmp")).FullName;
        Fixtures = Directory.CreateDirectory(Path.Combine(Root, "files")).FullName;
        Scenario = new Scenario(Path.Combine(Root, "scenario"));
        Scenario.Save();
        string package = fake ? UiEnvironment.FakePackage : UiEnvironment.RealPackage;
        var info = new ProcessStartInfo(Path.Combine(package, "unextract-gui.exe")) { UseShellExecute = false, WorkingDirectory = Root };
        info.Environment["TMP"] = TempDirectory;
        info.Environment["TEMP"] = TempDirectory;
        info.Environment["UNEXTRACT_FAKE_CLI_SCENARIO"] = Scenario.Path;
        info.Environment.Remove(GuiDataRootVariable);
        if (setDataRoot) info.Environment[GuiDataRootVariable] = dataRootValue ?? DataRoot;
        App = Application.Launch(info);
    }

    private const string GuiDataRootVariable = "UNEXTRACT_GUI_TEST_DATA_ROOT";

    // Applies edits made to the scenario after the GUI started (the fake CLI reads the file at every launch).
    public void SaveScenario() => Scenario.Save();

    public Window Main => _main ??= Wait.Until(() => App.GetAllTopLevelWindows(_automation)
        .FirstOrDefault(w => w.Title == "unextract GUI"), "the main window");

    public AutomationElement? Find(string id, AutomationElement? scope = null) =>
        (scope ?? Main).FindFirstDescendant(Conditions.ByAutomationId(id));

    public AutomationElement Get(string id, AutomationElement? scope = null) =>
        Wait.Until(() => Find(id, scope), $"element '{id}'");

    public string TextOf(string id, AutomationElement? scope = null) => Get(id, scope).Name;

    public Window[] TopLevelWindows() => App.GetAllTopLevelWindows(_automation);

    public AutomationElement? FocusedElement() => _automation.FocusedElement();

    public Window? Modal() => Main.ModalWindows.FirstOrDefault();

    public Window WaitModal(string? title = null) =>
        Wait.Until(() => Main.ModalWindows.FirstOrDefault(w => title is null || w.Title == title), $"a modal window '{title}'");

    // Message box buttons by their language-independent ids: OK = 1, Cancel = 2.
    public void PressMessageBox(Window box, string id) => Get(id, box).AsButton().Invoke();

    public void SetText(string id, string value, AutomationElement? scope = null) => Get(id, scope).AsTextBox().Text = value;

    public void Invoke(string id, AutomationElement? scope = null) => Get(id, scope).AsButton().Invoke();

    // The work list (one element for the window's lifetime).
    public AutomationElement WorkList => _workList ??= Get("ArchiveList");

    // A row of the work list, searched within the list only (not the whole window). Rows of the virtualized list outside the
    // visible area are realized first (ItemContainer and VirtualizedItem patterns) and scrolled into view.
    public AutomationElement Row(string name) => Wait.Until(() =>
    {
        var list = WorkList;
        var row = list.FindFirstDescendant(Conditions.ByControlType(ControlType.ListItem).And(Conditions.ByName(name)));
        if (row is null && list.Patterns.ItemContainer.PatternOrDefault is { } container)
        {
            var item = container.FindItemByProperty(null, list.Automation.PropertyLibrary.Element.Name, name);
            if (item is not null)
            {
                item.Patterns.VirtualizedItem.PatternOrDefault?.Realize();
                item.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
                row = item;
            }
        }
        return row;
    }, $"list row '{name}'");

    // Radio buttons are exposed through the selection item pattern.
    public bool IsChecked(AutomationElement radio) => radio.Patterns.SelectionItem.Pattern.IsSelected.Value;

    public ToggleState ToggleOf(AutomationElement checkBox) => checkBox.Patterns.Toggle.Pattern.ToggleState.Value;

    public void Toggle(AutomationElement checkBox) => checkBox.Patterns.Toggle.Pattern.Toggle();

    public void Expand(AutomationElement expander)
    {
        var pattern = expander.Patterns.ExpandCollapse.Pattern;
        if (pattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded) pattern.Expand();
    }

    public void Collapse(AutomationElement expander)
    {
        var pattern = expander.Patterns.ExpandCollapse.Pattern;
        if (pattern.ExpandCollapseState.Value != ExpandCollapseState.Collapsed) pattern.Collapse();
    }

    public void FocusMain() => Main.SetForeground();

    // A real mouse click (the mode radio buttons act on Click, which UIA patterns do not raise). A real click can
    // miss when the window is still changing focus, so it is repeated until its effect is visible.
    public void ClickUntil(string id, Func<bool> done, string what)
    {
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            if (Modal() is null) FocusMain();
            Get(id).Click();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                try { if (done()) return; } catch (Exception) { }
                Thread.Sleep(50);
            }
        }
        throw new TimeoutException($"Clicking '{id}' did not lead to: {what}");
    }

    public void Press(VirtualKeyShort key) => Keyboard.Type(key);

    public IReadOnlyList<FakeInvocation> Invocations => Scenario.Invocations();

    public bool HasExited => App.HasExited;

    // Normal close requests only, repeated every few seconds. Pending fake-CLI releases are created first so a blocked CLI
    // can finish. Returns null when the GUI exited, otherwise the failure (with the process id).
    public string? CloseNormally()
    {
        try
        {
            Scenario.ReleaseAll();
            if (App.HasExited) return null;
            string last = "";
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var nextRequest = DateTime.MinValue;
            while (!App.HasExited && DateTime.UtcNow < deadline)
            {
                if (DateTime.UtcNow >= nextRequest)
                {
                    nextRequest = DateTime.UtcNow.AddSeconds(3);
                    try
                    {
                        foreach (var modal in Main.ModalWindows) modal.Close();
                        Main.Patterns.Window.Pattern.Close();
                    }
                    catch (Exception e) { last = e.Message; }
                }
                Thread.Sleep(50);
            }
            return App.HasExited ? null : $"The GUI (process {App.ProcessId}) did not exit after normal close requests. Last error: {last}";
        }
        catch (Exception e)
        {
            return $"Closing the GUI (process {App.ProcessId}) failed: {e.Message}";
        }
    }

    // Terminates the process tree this session started (never any other process) and waits for it. Returns null when the
    // GUI is gone, otherwise why it could not be confirmed.
    public string? Terminate()
    {
        try
        {
            using var process = Process.GetProcessById(App.ProcessId);
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(TimeSpan.FromSeconds(15)) ? null : $"The GUI (process {App.ProcessId}) did not end after termination.";
        }
        catch (ArgumentException)
        {
            return null; // already gone
        }
        catch (Exception e)
        {
            return App.HasExited ? null : $"Terminating the GUI (process {App.ProcessId}) failed: {e.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _automation.Dispose();
    }

    // Diagnostics: the UI Automation tree of every top-level window of the GUI process (names escaped like the GUI shows them).
    public void WriteTree(string path)
    {
        var text = new StringBuilder();
        int count = 0;
        foreach (var window in TopLevelWindows()) Dump(window, 0);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));

        void Dump(AutomationElement element, int depth)
        {
            if (++count > 5000) return;
            static string Read(Func<object?> read)
            {
                try { return Models.Escape(Convert.ToString(read(), System.Globalization.CultureInfo.InvariantCulture) ?? ""); }
                catch (Exception e) { return $"<{e.GetType().Name}>"; }
            }
            string name = Read(() => element.Name);
            if (name.Length > 300) name = name[..300] + "...";
            text.Append(' ', depth * 2).Append(Read(() => element.ControlType)).Append(" id='").Append(Read(() => element.AutomationId))
                .Append("' name='").Append(name).Append("' rect=").Append(Read(() => element.BoundingRectangle))
                .Append(" enabled=").Append(Read(() => element.IsEnabled)).Append(" offscreen=").Append(Read(() => element.IsOffscreen)).Append('\n');
            if (depth >= 40) return;
            AutomationElement[] children;
            try { children = element.FindAllChildren(); }
            catch (Exception e) { text.Append(' ', depth * 2 + 2).Append($"<children: {e.GetType().Name}>\n"); return; }
            foreach (var child in children) Dump(child, depth + 1);
        }
    }

    public void CopyScenario(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string file in new[] { Scenario.Path, Scenario.RecordPath })
            if (File.Exists(file)) File.Copy(file, System.IO.Path.Combine(directory, System.IO.Path.GetFileName(file)));
    }

    public void CopyDataRoot(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories))
        {
            string destination = System.IO.Path.Combine(directory, System.IO.Path.GetRelativePath(DataRoot, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = File.Create(destination);
            source.CopyTo(target);
        }
    }
}
