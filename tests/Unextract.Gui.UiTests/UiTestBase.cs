using System.Text;
using FlaUI.Core.Capturing;
using Unextract.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace Unextract.Gui.UiTests;

// Base of the UI test classes: one test instance per test, so sessions are closed after each test.
// - Each started session writes its fixture and the results directory to the test output.
// - On a failed test body (UiFact/UiTheory), the diagnostics are saved under <results>\diagnostics\<test>-<id>\ while
//   the GUI is still in the failing state: the whole desktop, the UI Automation tree of every top-level window of the
//   GUI process, the fake CLI's scenario and record, and the GUI's data root (settings, logs).
// - Closing is a normal close request first. A GUI that does not exit fails the test with its process id; after the
//   diagnostics, the process tree that the session started (and only that) is terminated, so that later tests do not
//   meet a leftover window. Diagnostics have a deadline, and a failed capture never skips the termination.
// - The fixtures of the sessions belong to this test (an explicit FixtureOwner: they live longer than the test method) and
//   are deleted by the shared deletion routine after every GUI has exited, passed or failed. When a GUI could not be
//   confirmed gone, nothing is deleted and the remaining paths are reported.
[Collection("ui")]
public abstract class UiTestBase : IDisposable
{
    private static readonly TimeSpan DiagnosticsLimit = TimeSpan.FromSeconds(30);
    private readonly List<GuiSession> _sessions = [];
    private readonly ITestOutputHelper _output;

    // Set by UiTestInvoker before the test body runs: the display name names the diagnostics folder, the method name the fixtures.
    internal string TestName { get; set; } = "";
    internal string MethodName { get; set; } = "";
    private FixtureOwner? _fixtures;

    protected UiTestBase(ITestOutputHelper output)
    {
        _output = output;
        Wait.Log = output.WriteLine;
    }

    protected ITestOutputHelper Output => _output;

    internal GuiSession Start(bool fake = true, string? dataRootValue = null)
    {
        _fixtures ??= new FixtureOwner(MethodName.Length > 0 ? MethodName : GetType().Name);
        var session = new GuiSession(_fixtures.Create(), fake, dataRootValue);
        _sessions.Add(session);
        _output.WriteLine($"fixture: {session.Root}");
        _output.WriteLine($"results: {UiEnvironment.Results}");
        return session;
    }

    // Called by UiTestInvoker after a failed test body, before Dispose. Never throws.
    internal void SaveFailureDiagnostics(Exception failure) => SaveDiagnostics("test failure", failure.ToString());

    private void SaveDiagnostics(string reason, string detail)
    {
        string directory = "";
        try
        {
            string name = new(TestName.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ' ' or '(' or ')' or ',' or '"' ? '_' : c).ToArray());
            if (name.Length > 100) name = name[..100];
            directory = Directory.CreateDirectory(Path.Combine(UiEnvironment.Results, "diagnostics", $"{name}-{Guid.NewGuid():N}"[..(name.Length + 9)])).FullName;
            File.WriteAllText(Path.Combine(directory, "failure.txt"), $"{reason}\n\n{detail}\n", new UTF8Encoding(false));
            var capture = Task.Run(() =>
            {
                var problems = new StringBuilder();
                Try(problems, "desktop", () => Capture.Screen().ToFile(Path.Combine(directory, "desktop.png")));
                for (int i = 0; i < _sessions.Count; i++)
                {
                    var session = _sessions[i];
                    string sessionDirectory = Directory.CreateDirectory(Path.Combine(directory, $"session-{i + 1}")).FullName;
                    File.WriteAllText(Path.Combine(sessionDirectory, "fixture.txt"), session.Root + "\n");
                    Try(problems, $"session {i + 1} UI Automation tree", () => session.WriteTree(Path.Combine(sessionDirectory, "uia-tree.txt")));
                    Try(problems, $"session {i + 1} fake CLI record", () => session.CopyScenario(Path.Combine(sessionDirectory, "fake-cli")));
                    Try(problems, $"session {i + 1} data root", () => session.CopyDataRoot(Path.Combine(sessionDirectory, "data")));
                }
                return problems.ToString();
            });
            string problems = capture.Wait(DiagnosticsLimit) ? capture.Result : $"diagnostics did not finish within {DiagnosticsLimit.TotalSeconds} s\n";
            if (problems.Length > 0) File.AppendAllText(Path.Combine(directory, "failure.txt"), "\nDiagnostics problems:\n" + problems);
            _output.WriteLine($"diagnostics ({reason}): {directory}" + (problems.Length > 0 ? "\n" + problems : ""));
        }
        catch (Exception e)
        {
            _output.WriteLine($"diagnostics ({reason}) failed: {directory}: {e}");
        }
    }

    private static void Try(StringBuilder problems, string what, Action action)
    {
        try { action(); }
        catch (Exception e) { problems.AppendLine($"{what}: {e.GetType().Name}: {e.Message}"); }
    }

    public void Dispose()
    {
        var failures = new List<Exception>();
        bool allExited = true;
        foreach (var session in _sessions)
        {
            string? closeFailure = session.CloseNormally();
            if (closeFailure is null)
            {
                session.Dispose();
                continue;
            }

            // The diagnostics first (the stuck state), then terminate the session's own process tree. Separated by finally,
            // so that a failed capture never skips the termination.
            try { SaveDiagnostics("the GUI did not exit", closeFailure); }
            finally
            {
                string? terminateFailure = session.Terminate();
                allExited &= terminateFailure is null;
                failures.Add(new InvalidOperationException(closeFailure + (terminateFailure is null
                    ? " It was terminated after the diagnostics."
                    : $" {terminateFailure} Fixture left: {session.Root}")));
                session.Dispose();
            }
        }
        Wait.Log = null;
        if (_fixtures is not null)
        {
            if (allExited)
            {
                try { _fixtures.Cleanup(); }
                catch (Exception e) { failures.Add(e); }
            }
            else
            {
                failures.Add(new InvalidOperationException("A GUI could not be confirmed gone; its fixtures were not deleted: " + string.Join(", ", _fixtures.Owned)));
            }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}
