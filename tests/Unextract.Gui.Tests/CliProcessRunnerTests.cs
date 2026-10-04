using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using Unextract.Gui.Models;
using Unextract.Gui.Services;
using static Unextract.Gui.Tests.JsonlReceiverTests;

namespace Unextract.Gui.Tests;

public sealed class CliProcessRunnerTests
{
    private static CliLocation Location => new(DeploymentTests.GuiOutputDirectory);

    [Fact]
    public void ArgumentsUseLiteralAbsolutePathsAndFixedBundledExecutable()
    {
        var job = Job(CliOperation.Delete, CliMode.Fast) with { Archive = @"C:\日本語 空白\a&$(literal).zip" };
        job.Validate();
        var info = CliProcessRunner.StartInfo(Location.ExecutablePath, job);
        Assert.Equal(Location.ExecutablePath, info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardOutput && info.RedirectStandardError);
        Assert.Empty(info.Arguments);
        Assert.Equal(new[] { "delete", job.Archive, "--target", job.Target, "--fast", "--entries", job.Entries!,
            "--yes", "--jsonl", "--log", job.Log! }, info.ArgumentList);
        Assert.DoesNotContain("--yes", CliProcessRunner.StartInfo(Location.ExecutablePath, Job()).ArgumentList);
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(1, true, false)]
    [InlineData(7, true, false)]
    [InlineData(0, false, false)]
    public async Task SuccessRequiresRealExitAndMatchingCompletedResult(int exit, bool result, bool success)
    {
        var job = Job();
        using var child = new FakeChild(Run(job) + Entry() + (result ? Completed(job) : ""), exit: exit);
        var runner = Runner(child);
        var output = await runner.RunAsync(job);
        Assert.Equal(success, output.Succeeded);
        Assert.True(output.ExitConfirmed && output.StdoutEof && output.StderrEof);
        Assert.Equal(exit, output.ExitCode);
        Assert.Equal(result, output.Output.Result is not null);
        Assert.Equal(0, child.Kills);
    }

    [Fact]
    public async Task CompletedDeleteFailureIsAnErrorAndStderrDoesNotChangeSuccess()
    {
        var job = Job(CliOperation.Delete);
        string failure = Change(Entry("DELETE_FAILED"), "reason", System.Text.Json.Nodes.JsonNode.Parse("{\"code\":\"FUTURE\",\"message\":\"表示\"}"));
        string result = Line(new { v = 1, type = "result", outcome = "completed", exit_code = 1,
            counts = new { deleted = 0, modified = 0, missing = 0, skipped_special_file = 0,
                directory = 0, delete_failed = 1, not_selected = 0, unprocessed = 0 } });
        using var child = new FakeChild(Run(job) + failure + result, exit: 1);
        var output = await Runner(child).RunAsync(job);
        Assert.True(output.Output.IsCompatible);
        Assert.False(output.Succeeded);
        using var diagnosticChild = new FakeChild(Run(Job()) + Entry() + Completed(Job()), "contract-external diagnostic");
        output = await Runner(diagnosticChild).RunAsync(Job());
        Assert.True(output.Succeeded);
        Assert.Equal("contract-external diagnostic", output.Stderr);
    }

    [Fact]
    public async Task ActualExitAndBothEofsAreRequiredAndProgressNeedsNoUiCallbacks()
    {
        var job = Job();
        using var output = new DelayedEofStream(Run(job) + Entry() + Completed(job));
        using var error = new DelayedEofStream("diagnostic");
        using var child = new FakeChild(output, error) { Exit = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var runner = Runner(child);
        var task = runner.RunAsync(job);
        await Task.WhenAll(output.Drained.Task, error.Drained.Task).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, runner.Progress.ReceivedEntries);
        Assert.Equal(1, runner.Progress.Run!.Selected);
        Assert.False(task.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(job));
        child.Exit.SetResult();
        output.Eof.SetResult();
        Assert.False(task.IsCompleted);
        error.Eof.SetResult();
        Assert.True((await task.WaitAsync(TimeSpan.FromSeconds(10))).Succeeded);
    }

    [Fact]
    public async Task InvalidOutputStillDrainsAndBoundsDiagnostics()
    {
        using var stdout = new TrackingStream(Encoding.ASCII.GetBytes("invalid\n" + new string('x', 2 * 1024 * 1024)));
        using var stderr = new TrackingStream(Encoding.UTF8.GetBytes(new string('顔', 2 * 1024 * 1024)));
        using var child = new FakeChild(stdout, stderr);
        var result = await Runner(child).RunAsync(Job(CliOperation.Delete));
        Assert.False(result.Succeeded);
        Assert.False(result.Output.IsCompatible);
        Assert.True(result.StdoutEof && result.StderrEof);
        Assert.Equal(stdout.Length, stdout.Position);
        Assert.Equal(stderr.Length, stderr.Position);
        Assert.True(result.StderrTruncated);
        Assert.StartsWith(new string('顔', CliProcessRunner.StderrCharacterLimit), result.Stderr, StringComparison.Ordinal);
        Assert.Contains("省略", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, child.Kills);
    }

    [Fact]
    public async Task StartEvidenceDistinguishesPreparationOsFailureAndUnknown()
    {
        using var child = new FakeChild("");
        var runner = Runner(child);
        var result = await runner.RunAsync(Job() with { Archive = "relative.zip" });
        Assert.Equal(CliStartState.BeforeStartFailure, result.StartState);
        Assert.Equal(0, child.Starts);
        runner = new CliProcessRunner(new CliLocation(Fixture()));
        Assert.Equal(CliStartState.BeforeStartFailure, (await runner.RunAsync(Job())).StartState);
        child.StartFailure = new Win32Exception(2);
        Assert.Equal(CliStartState.NotStarted, (await Runner(child).RunAsync(Job())).StartState);
        child.StartFailure = new InvalidOperationException("uncertain start");
        child.HasProcess = false;
        result = await Runner(child).RunAsync(Job(CliOperation.Delete));
        Assert.Equal(CliStartState.Unknown, result.StartState);
        Assert.False(result.ExitConfirmed);
        child.HasProcess = true;
        result = await Runner(child).RunAsync(Job(CliOperation.Delete));
        Assert.Equal(CliStartState.Unknown, result.StartState);
        Assert.True(result.ExitConfirmed && result.StdoutEof && result.StderrEof);
        Assert.False(result.Succeeded);
        Assert.Equal(0, child.Kills);
        child.StartFailure = null;
        child.StartValue = false;
        Assert.Equal(CliStartState.NotStarted, (await Runner(child).RunAsync(Job())).StartState);
    }

    [Fact]
    public async Task UnconfirmedProcessBlocksFurtherStartsInTheSession()
    {
        using var child = new FakeChild("") { StartFailure = new InvalidOperationException("unknown"), HasProcess = false };
        var runner = Runner(child);
        var result = await runner.RunAsync(Job(CliOperation.Delete));
        Assert.Equal(CliStartState.Unknown, result.StartState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Job()));
        Assert.Equal(1, child.Starts);

        using var started = new FakeChild(Run(Job(), 0) + Completed(Job(), 0))
        {
            Exit = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        started.Exit.SetException(new InvalidOperationException("wait failed"));
        runner = Runner(started);
        result = await runner.RunAsync(Job());
        Assert.Equal(CliStartState.Started, result.StartState);
        Assert.False(result.ExitConfirmed);
        Assert.True(result.StdoutEof && result.StderrEof);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Output.Result);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Job()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationTerminatesOnlyAnalysisAndStillCollectsExitAndOutput(bool delete)
    {
        var job = Job(delete ? CliOperation.Delete : CliOperation.Analyze);
        using var child = new FakeChild(Run(job, 0) + Completed(job, 0))
        {
            Exit = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var cancellation = new CancellationTokenSource();
        var runner = Runner(child);
        var task = runner.RunAsync(job, cancellation.Token);
        await child.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await child.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.Equal(delete ? 0 : 1, child.Kills);
        if (delete)
        {
            Assert.False(task.IsCompleted);
            child.Exit.SetResult();
        }
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(!delete, result.Cancelled);
        Assert.Equal(delete, result.Succeeded);
        Assert.True(result.ExitConfirmed && result.StdoutEof && result.StderrEof);
    }

    [Fact]
    public async Task AlreadyCancelledAnalysisDoesNotStart()
    {
        using var child = new FakeChild("");
        var result = await Runner(child).RunAsync(Job(), new CancellationToken(true));
        Assert.Equal(CliStartState.BeforeStartFailure, result.StartState);
        Assert.True(result.Cancelled);
        Assert.Equal(0, child.Starts);
    }

    [Fact]
    public async Task StreamReadFailureCannotPretendToBeEofOrSuccess()
    {
        using var child = new FakeChild(new FailingStream(), new MemoryStream(Encoding.UTF8.GetBytes("still drained")));
        var result = await Runner(child).RunAsync(Job());
        Assert.False(result.StdoutEof);
        Assert.True(result.StderrEof && result.ExitConfirmed);
        Assert.False(result.Output.IsCompatible);
        Assert.Equal("still drained", result.Stderr);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RealProcessImmediatelyExitsAndBothLargePipesDrainAfterInvalidRecord()
    {
        var job = Job(CliOperation.Delete);
        var immediate = ScriptRunner("exit 1");
        var result = await immediate.RunAsync(job).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.ExitConfirmed && result.StdoutEof && result.StderrEof);
        Assert.Equal(1, result.ExitCode);
        var flood = ScriptRunner("$o = [Console]::OpenStandardOutput()\n$e = [Console]::OpenStandardError()\n" +
            "$bad = [Text.Encoding]::ASCII.GetBytes('bad' + [char]10)\n$o.Write($bad,0,$bad.Length)\n" +
            "$b = [Text.Encoding]::ASCII.GetBytes(('x' * 16383) + [char]10)\n" +
            "for($i=0;$i -lt 128;$i++){ $o.Write($b,0,$b.Length); $e.Write($b,0,$b.Length) }\n$o.Flush()\n$e.Flush()\nexit 0");
        result = await flood.RunAsync(job, new CancellationToken(true)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.ExitConfirmed && result.StdoutEof && result.StderrEof);
        Assert.False(result.Output.IsCompatible);
        Assert.True(result.StderrTruncated);
        Assert.False(result.Cancelled);
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealProcessCancellationHonorsOperation(bool delete)
    {
        var job = Job(delete ? CliOperation.Delete : CliOperation.Analyze);
        string fixture = Fixture();
        string release = Path.Combine(fixture, "release.txt");
        string script = Emit(Run(job, 0)) +
            $"while(!(Test-Path -LiteralPath '{release.Replace("'", "''", StringComparison.Ordinal)}')){{ [Threading.Thread]::Sleep(20) }}\n" +
            Emit(Completed(job, 0)) + "exit 0";
        var runner = ScriptRunner(script);
        using var cancel = new CancellationTokenSource();
        var task = runner.RunAsync(job, cancel.Token);
        try
        {
            await WaitForRun(runner, task);
            cancel.Cancel();
            if (delete) Assert.False(task.IsCompleted);
        }
        finally
        {
            // Release our synthetic child even when an assertion fails; never kill a delete job.
            File.WriteAllText(release, "release owned process");
        }
        var result = await task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(!delete, result.Cancelled);
        Assert.Equal(delete, result.Succeeded);
        Assert.True(result.ExitConfirmed && result.StdoutEof && result.StderrEof);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task RealCompletedResultIsRetainedWhenExitCodeDisagrees(int exit)
    {
        var job = Job();
        var runner = ScriptRunner(Emit(Run(job) + Entry() + Completed(job)) + $"exit {exit}");
        var result = await runner.RunAsync(job).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(exit == 0, result.Succeeded);
        Assert.True(result.Output.IsCompatible);
        Assert.True(result.ExitConfirmed && result.StdoutEof && result.StderrEof);
        Assert.Equal(exit, result.ExitCode);
        Assert.Equal(0, result.Output.Result!.ExitCode);
        Assert.Equal("completed", result.Output.Result.Outcome);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("mode")]
    [InlineData("fragment")]
    public async Task RealIncompatibleOrUnterminatedOutputIsAnErrorWithoutTerminatingDelete(string kind)
    {
        var job = Job(CliOperation.Delete);
        string run = Run(job, 0), result = Completed(job, 0);
        if (kind == "version") run = Change(run, "v", System.Text.Json.Nodes.JsonValue.Create(2));
        if (kind == "mode") run = Change(run, "mode", System.Text.Json.Nodes.JsonValue.Create("fast"));
        if (kind == "fragment") result = result.TrimEnd('\n');
        var runner = ScriptRunner(Emit(run + result) + "exit 0");
        var output = await runner.RunAsync(job, new CancellationToken(true)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(output.ExitConfirmed && output.StdoutEof && output.StderrEof);
        Assert.Equal(0, output.ExitCode);
        Assert.False(output.Succeeded);
        Assert.False(output.Cancelled);
        Assert.Equal(kind == "fragment", output.Output.IsCompatible);
        Assert.Null(output.Output.Result);
    }

    [Fact]
    public async Task BundledCliAnalyzesOwnedEmptyArchiveAndRejectsMissingArchive()
    {
        string fixture = Fixture();
        string archive = Path.Combine(fixture, "空 白.zip");
        string target = Path.Combine(fixture, "target");
        Directory.CreateDirectory(target);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { }
        byte[] before = File.ReadAllBytes(archive);
        var zipTime = File.GetLastWriteTimeUtc(archive);
        var runner = new CliProcessRunner(Location);
        foreach (var mode in new[] { CliMode.Strict, CliMode.Fast })
        {
            var result = await runner.RunAsync(new(CliOperation.Analyze, mode, archive, target)).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(result.Succeeded, result.ProcessError ?? result.Output.IssueMessage);
            Assert.Equal(0, result.Output.Run!.Selected);
            Assert.Empty(result.Output.Entries);
            Assert.Equal(before, File.ReadAllBytes(archive));
            Assert.Equal(zipTime, File.GetLastWriteTimeUtc(archive));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        var failed = await runner.RunAsync(new(CliOperation.Analyze, CliMode.Strict, Path.Combine(fixture, "missing.zip"), target));
        Assert.False(failed.Succeeded);
        Assert.True(failed.Output.IsCompatible, failed.Output.IssueMessage);
        Assert.Equal("fatal", failed.Output.Result!.Outcome);
        Assert.Null(failed.Output.Run);
        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task BundledCliReceivesEveryEntryInBothModesWithoutModifyingFixture()
    {
        string fixture = Fixture();
        string archive = Path.Combine(fixture, "内容.zip");
        string target = Path.Combine(fixture, "target");
        Directory.CreateDirectory(target);
        string matched = "顔-\U0001F642.txt", changed = "changed.txt", missing = "missing.txt";
        byte[] bytes = [1, 2, 3];
        File.WriteAllBytes(Path.Combine(target, matched), bytes);
        File.WriteAllBytes(Path.Combine(target, changed), [3, 2, 1]);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { matched, changed, missing })
            {
                using var content = zip.CreateEntry(name).Open();
                content.Write(bytes);
            }
            zip.CreateEntry("dir/");
        }
        var snapshot = Snapshot();
        var runner = new CliProcessRunner(Location);
        foreach (var mode in new[] { CliMode.Strict, CliMode.Fast })
        {
            var result = await runner.RunAsync(new(CliOperation.Analyze, mode, archive, target)).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(result.Succeeded, result.ProcessError ?? result.Output.IssueMessage);
            Assert.Equal(new[] { 1, 2, 3, 4 }, result.Output.Entries.Select(x => x.Index));
            Assert.Equal(new[] { matched, changed, missing, "dir/" }, result.Output.Entries.Select(x => x.Name));
            Assert.Equal(mode == CliMode.Strict ? new[] { "MATCHED", "MODIFIED", "MISSING", "DIRECTORY" }
                : new[] { "SAME_SIZE", "SAME_SIZE", "MISSING", "DIRECTORY" }, result.Output.Entries.Select(x => x.Status));
            Assert.Equal(snapshot, Snapshot());
        }
        (string Path, long Length, string Hash, DateTime Time)[] Snapshot() => Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => (path, new FileInfo(path).Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), File.GetLastWriteTimeUtc(path))).ToArray();
    }

    private static async Task WaitForRun(CliProcessRunner runner, Task task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (runner.Progress.Run is null)
        {
            Assert.False(task.IsCompleted, "Child terminated before run was observed.");
            await Task.Delay(10, timeout.Token);
        }
    }
    private static string Fixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "gui-process-" + Guid.NewGuid().ToString("N"), "日本語 空白");
        Directory.CreateDirectory(path);
        return path;
    }
    private static string Emit(string text) => "$b = [Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.ASCII.GetBytes(text)) +
        "')\n$o = [Console]::OpenStandardOutput()\n$o.Write($b,0,$b.Length)\n$o.Flush()\n";
    private static CliProcessRunner ScriptRunner(string script)
    {
        string path = Path.Combine(Fixture(), "child.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(true));
        return new CliProcessRunner(Location)
        {
            CreateProcess = _ =>
            {
                var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) info.ArgumentList.Add(arg);
                return new ChildProcess(info);
            },
        };
    }
    private static CliProcessRunner Runner(IChildProcess child) => new(Location) { CreateProcess = _ => child };

    private sealed class FakeChild : IChildProcess
    {
        public FakeChild(string output, string error = "", int exit = 0)
            : this(new MemoryStream(Encoding.ASCII.GetBytes(output)), new MemoryStream(Encoding.UTF8.GetBytes(error))) { ExitCode = exit; }
        public FakeChild(Stream output, Stream error) { StandardOutput = output; StandardError = error; }
        public int ExitCode { get; }
        public bool HasProcess { get; set; } = true;
        public bool StartValue { get; set; } = true;
        public Exception? StartFailure { get; set; }
        public int Starts { get; private set; }
        public int Kills { get; private set; }
        public Stream StandardOutput { get; }
        public Stream StandardError { get; }
        public TaskCompletionSource? Exit { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Start()
        {
            Starts++;
            if (StartFailure is not null) throw StartFailure;
            Started.TrySetResult();
            return StartValue;
        }
        public Task WaitForExitAsync() { Waiting.TrySetResult(); return Exit?.Task ?? Task.CompletedTask; }
        public void CancelAnalysis() { Kills++; Exit?.TrySetResult(); }
        public void Dispose() { /* Tests own streams, which remain inspectable after collection. */ }
    }
    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes);
    private sealed class DelayedEofStream(string text) : MemoryStream(Encoding.ASCII.GetBytes(text))
    {
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Eof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            if (read != 0) return read;
            Drained.TrySetResult();
            await Eof.Task;
            return 0;
        }
    }
    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("injected stream failure"));
    }
}
