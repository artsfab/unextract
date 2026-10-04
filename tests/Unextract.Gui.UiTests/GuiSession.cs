using System.Diagnostics;
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

// Polls a condition until it holds, with a deadline; never a fixed sleep.
internal static class Wait
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(20);

    public static T Until<T>(Func<T?> probe, string what, TimeSpan? limit = null) where T : class
    {
        var deadline = DateTime.UtcNow + (limit ?? Default);
        Exception? last = null;
        while (true)
        {
            try { if (probe() is { } value) return value; last = null; }
            catch (Exception e)
            {
                // UI Automation reports transient states (element gone, not ready) as various exception types: retry until the deadline.
                last = e;
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}" + (last is null ? "" : "\n" + last.Message));
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

    // dataRootValue: null = the fixture's data directory; otherwise the raw environment value (for misconfiguration tests).
    public GuiSession(bool fake, string kind, string? dataRootValue = null, bool setDataRoot = true)
    {
        Root = UiEnvironment.NewFixture(kind);
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

    // A row of a list. Rows of the virtualized work list outside the visible area are realized first (ItemContainer and
    // VirtualizedItem patterns) and scrolled into view.
    public AutomationElement Row(string name) => Wait.Until(() =>
    {
        var row = Main.FindFirstDescendant(Conditions.ByControlType(ControlType.ListItem).And(Conditions.ByName(name)));
        if (row is null && Find("ArchiveList") is { } list && list.Patterns.ItemContainer.PatternOrDefault is { } container)
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

    // Normal close only. Pending fake-CLI releases are created first so a blocked CLI can finish; if the GUI still does not
    // exit, the process id is reported and the process is left alone.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        string? failure = null;
        try
        {
            Scenario.ReleaseAll();
            if (!App.HasExited)
            {
                // Normal close requests only, repeated every few seconds; the process is never killed.
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
                if (!App.HasExited) failure = $"The GUI (process {App.ProcessId}) did not exit after normal close requests; it was left running. Last error: {last}";
            }
        }
        catch (Exception e)
        {
            failure = $"Cleanup of the GUI (process {App.ProcessId}) failed: {e.Message}";
        }
        _automation.Dispose();
        if (failure is not null) throw new InvalidOperationException(failure);
    }
}

// Base of the UI test classes: one test instance per test, so sessions are closed (normally) after each test.
[Collection("ui")]
public abstract class UiTestBase : IDisposable
{
    private readonly List<GuiSession> _sessions = [];

    internal GuiSession Start(bool fake = true, string kind = "ui", string? dataRootValue = null)
    {
        var session = new GuiSession(fake, kind, dataRootValue);
        _sessions.Add(session);
        return session;
    }

    public void Dispose()
    {
        var failures = new List<Exception>();
        foreach (var session in _sessions)
        {
            try { session.Dispose(); } catch (Exception e) { failures.Add(e); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}
