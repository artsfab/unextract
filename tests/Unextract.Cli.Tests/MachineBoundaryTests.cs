using System.Text;
using System.Text.Json;
using Unextract.Core.Commands;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;

namespace Unextract.Cli.Tests;

// J13: CLIから実処理器までの配送失敗・完了境界。削除は既存FakeFileSystemで模擬する。
public class MachineBoundaryTests
{
    public static IEnumerable<object[]> ContinuingFailures()
    {
        foreach (var delete in new[] { false, true })
        foreach (var fast in new[] { false, true })
        foreach (var destination in delete ? new[] { "log", "stdout" } : new[] { "stdout" })
        foreach (var operation in new[] { "write", "partial", "flush" })
        foreach (var record in new[] { "run", "entry:1", "entry:3", "entry:5" })
            yield return [delete, fast, destination, operation, record];
    }

    [Theory]
    [MemberData(nameof(ContinuingFailures))]
    public void J13_ContinuingDeliveryFailureStopsAtClosedEntry(bool delete, bool fast,
        string destination, string operation, string record)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "a"), ("d/", null), ("b.txt", "b"), ("e/", null), ("c.txt", "c"));
        foreach (var name in new[] { "a", "b", "c" }) f.File(name + ".txt", name);
        var trace = new DeliveryTrace(f.Fs);
        var stdout = new RecordedStream("stdout", trace, destination == "stdout" ? record : null, operation);
        var log = delete ? new RecordedStream("log", trace, destination == "log" ? record : null, operation) : null;
        var run = Run(f, f.Args(zip, delete, fast, log: delete ? f.PathFor("injected.jsonl") : null), trace, stdout, log);

        Assert.Equal(ExitStatus.Error, run.Status);
        var processed = record switch { "run" => 0, "entry:1" => 1, "entry:3" => 2, _ => 3 };
        var failed = destination == "stdout" ? stdout : log!;
        AssertFailedDestinationRetired(failed, trace);
        Assert.Equal(processed, delete ? f.Fs.DeletionCloseCount : f.Fs.ComparisonCloseCount);
        Assert.Equal(delete ? processed : 0, f.Fs.Deleted.Count);
        Assert.Equal(0, delete ? f.Fs.ComparisonOpenCount : f.Fs.DeletionOpenCount);
        if (processed < 3)
        {
            var next = new[] { "a.txt", "b.txt", "c.txt" }[processed];
            Assert.DoesNotContain(f.Fs.Calls, c => c.Contains(next, StringComparison.Ordinal));
        }
        if (processed == 0)
            Assert.DoesNotContain(f.Fs.Calls, c => c.StartsWith("OpenEnumeration ", StringComparison.Ordinal));

        var healthy = destination == "log" ? stdout : log;
        if (healthy is not null)
        {
            Assert.Equal(string.Empty, run.Stderr);
            var end = AssertErrorResult(healthy, delete, processed > 0);
            if (delete)
            {
                var directories = Math.Max(0, processed - 1);
                AssertCounts(end, processed, directories, 0, 5 - processed - directories);
            }
            else Assert.False(end.TryGetProperty("counts", out _));
            if (destination == "log") Assert.DoesNotContain(Records(healthy), e => Label(e) == record);
        }
        else
        {
            Assert.Contains("機械出力を記録できません", run.Stderr);
        }

        // ログは先行するためstdout失敗なら当該行がログにある。flush失敗もLF済みの行は残る。
        var before = record == "run" ? Array.Empty<int>() : processed switch
        {
            1 => [],
            2 => delete ? [1] : [1, 2],
            _ => delete ? [1, 3] : [1, 2, 3, 4],
        };
        Assert.Equal(operation == "flush" && record != "run"
            ? before.Append(int.Parse(record[6..])) : before, EntryIndexes(failed));
        if (delete && destination == "stdout")
            Assert.Contains(Records(log!), e => Label(e) == record);
        AssertPartialLine(failed, operation);
        AssertClosedBeforeDelivery(trace, delete);
        AssertModeReads(f, fast);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void J13_SuccessOrdersBothDeliveriesBeforeNextProcessing(bool delete, bool fast)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "a"), ("b.txt", "b"));
        f.File("a.txt", "a");
        f.File("b.txt", "b");
        var trace = new DeliveryTrace(f.Fs);
        var stdout = new RecordedStream("stdout", trace);
        var log = delete ? new RecordedStream("log", trace) : null;
        var run = Run(f, f.Args(zip, delete, fast, log: delete ? f.PathFor("injected.jsonl") : null), trace, stdout, log);
        Assert.Equal(ExitStatus.Success, run.Status);
        Assert.Equal(string.Empty, run.Stderr);
        if (log is not null) Assert.Equal(stdout.ToArray(), log.ToArray());
        var events = trace.Events;
        var open = delete ? "OpenDeletion" : "OpenComparison";
        var close = delete ? "Close deletion" : "Close comparison";
        AssertOrdered(events, "stdout flush run", $"{open} ", "a.txt", $"{close} ", "a.txt",
            delete ? "log write entry:1" : "stdout write entry:1", "stdout flush entry:1",
            $"{open} ", "b.txt", $"{close} ", "b.txt", "stdout flush entry:2", "Close root ", "stdout write result");
        if (delete)
        {
            foreach (var label in new[] { "run", "entry:1", "entry:2", "result" })
                AssertOrdered(events, $"log write {label}", $"log flush {label}", $"stdout write {label}", $"stdout flush {label}");
            AssertOrdered(events, "stdout flush result", "log close");
        }
        AssertClosedBeforeDelivery(trace, delete);
        AssertModeReads(f, fast);
    }

    public static IEnumerable<object[]> TerminalFailures()
    {
        foreach (var delete in new[] { false, true })
        foreach (var fast in new[] { false, true })
        foreach (var destination in delete ? new[] { "log", "stdout" } : new[] { "stdout" })
        foreach (var operation in new[] { "write", "partial", "flush" })
            yield return [delete, fast, destination, operation];
    }

    [Theory]
    [MemberData(nameof(TerminalFailures))]
    public void J13_TerminalFailureNeverRewritesOrAddsResult(bool delete, bool fast, string destination, string operation)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "a"));
        f.File("a.txt", "a");
        var trace = new DeliveryTrace(f.Fs);
        var stdout = new RecordedStream("stdout", trace, destination == "stdout" ? "result" : null, operation);
        var log = delete ? new RecordedStream("log", trace, destination == "log" ? "result" : null, operation) : null;
        var run = Run(f, f.Args(zip, delete, fast, log: delete ? f.PathFor("injected.jsonl") : null), trace, stdout, log);
        Assert.Equal(ExitStatus.Error, run.Status);
        if (delete) Assert.Equal(string.Empty, run.Stderr);
        else Assert.Contains("機械出力を記録できません", run.Stderr);
        Assert.Equal(1, delete ? f.Fs.Deleted.Count : f.Fs.ComparisonCloseCount);
        foreach (var stream in log is null ? new[] { stdout } : new[] { stdout, log })
        {
            Assert.Equal(stream == stdout && destination == "log" ? 0 : 1, stream.Attempts.Count(a => a == "write result"));
            Assert.DoesNotContain(Records(stream), e => e.TryGetProperty("error", out _));
            foreach (var end in Records(stream).Where(e => Label(e) == "result"))
            {
                Assert.Equal("completed", end.GetProperty("outcome").GetString());
                Assert.Equal(0, end.GetProperty("exit_code").GetInt32());
            }
        }
        var failed = destination == "stdout" ? stdout : log!;
        AssertFailedDestinationRetired(failed, trace);
        AssertPartialLine(failed, operation);
        if (delete && destination == "stdout") Assert.Single(Records(log!), e => Label(e) == "result");
        if (destination == "log") Assert.DoesNotContain(Records(stdout), e => Label(e) == "result");
        AssertClosedBeforeDelivery(trace, delete);
    }

    public static IEnumerable<object[]> StopFailures()
    {
        foreach (var fast in new[] { false, true })
        foreach (var possibly in new[] { false, true })
        foreach (var destination in new[] { "log", "stdout" })
        foreach (var operation in new[] { "write", "partial", "flush" })
            yield return [fast, possibly, destination, operation];
    }

    [Theory]
    [MemberData(nameof(StopFailures))]
    public void J13_StopDeliveryFailureRetainsUncertaintyAndProcessedCounts(bool fast, bool possibly,
        string destination, string operation)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "a"), ("d/", null), ("b.txt", "b"), ("c.txt", "c"));
        f.File("a.txt", "a");
        var stop = f.File("b.txt", "b");
        if (possibly) stop.ThrowAfterDisposition = true;
        else stop.Errors[FakeOp.VolumeFileId] = 23;
        f.File("c.txt", "c");
        var trace = new DeliveryTrace(f.Fs);
        var stdout = new RecordedStream("stdout", trace, destination == "stdout" ? "entry:3" : null, operation);
        var log = new RecordedStream("log", trace, destination == "log" ? "entry:3" : null, operation);
        var run = Run(f, f.Args(zip, true, fast, log: f.PathFor("injected.jsonl")), trace, stdout, log);
        Assert.Equal(ExitStatus.Error, run.Status);
        Assert.Equal(string.Empty, run.Stderr);
        var healthy = destination == "log" ? stdout : log;
        var end = AssertErrorResult(healthy, true, true);
        var error = end.GetProperty("error");
        Assert.Equal(possibly, error.GetProperty("possibly_deleted").GetBoolean());
        Assert.Equal(3, error.GetProperty("entry_index").GetInt32());
        Assert.Equal("b.txt", error.GetProperty("entry_name").GetString());
        AssertCounts(end, 1, 1, 0, 1);
        Assert.Equal(possibly ? 2 : 1, f.Fs.Deleted.Count);
        Assert.DoesNotContain(f.Fs.Calls, c => c.Contains("c.txt", StringComparison.Ordinal));
        AssertFailedDestinationRetired(destination == "log" ? log : stdout, trace);
        if (destination == "stdout" || operation == "flush")
        {
            var entry = Assert.Single(Records(log), e => Label(e) == "entry:3");
            Assert.Equal("STOPPED", entry.GetProperty("status").GetString());
            Assert.Equal(possibly, entry.GetProperty("possibly_deleted").GetBoolean());
        }
        AssertClosedBeforeDelivery(trace, true);
    }

    [Theory]
    [InlineData(false, "log")]
    [InlineData(false, "stdout")]
    [InlineData(true, "log")]
    [InlineData(true, "stdout")]
    public void J13_SelectedFilesKeepGapsSeparateFromUnprocessed(bool fast, string destination)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("ignored.txt", "x"), ("a.txt", "a"), ("d/", null), ("b.txt", "b"), ("e/", null), ("c.txt", "c"));
        f.File("a.txt", "a");
        f.File("b.txt", "b");
        f.File("c.txt", "c");
        var trace = new DeliveryTrace(f.Fs);
        var stdout = new RecordedStream("stdout", trace, destination == "stdout" ? "entry:4" : null, "partial");
        var log = new RecordedStream("log", trace, destination == "log" ? "entry:4" : null, "partial");
        var run = Run(f, f.Args(zip, true, fast, f.PathFor("injected.jsonl"), f.Entries("c.txt\nb.txt\na.txt\n")), trace, stdout, log);
        Assert.Equal(ExitStatus.Error, run.Status);
        AssertCounts(AssertErrorResult(destination == "log" ? stdout : log, true, true), 2, 0, 3, 1);
        Assert.Equal(new[] { 2 }, EntryIndexes(destination == "log" ? stdout : log).Where(i => i != 4));
        Assert.DoesNotContain(f.Fs.Calls, c => c.Contains("ignored.txt", StringComparison.Ordinal) || c.Contains("c.txt", StringComparison.Ordinal));
        Assert.Equal(2, f.Fs.Deleted.Count);
        AssertClosedBeforeDelivery(trace, true);
    }

    public static IEnumerable<object[]> BothFailures()
    {
        foreach (var fast in new[] { false, true })
        foreach (var stderrFails in new[] { false, true })
        foreach (var first in new[] { "log", "stdout" })
        foreach (var record in new[] { "run", "entry:1" })
            yield return [fast, stderrFails, first, record];
    }

    [Theory]
    [MemberData(nameof(BothFailures))]
    public void J13_BothDestinationsAndFallbackFailureStillStop(bool fast, bool stderrFails, string first, string record)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "a"), ("b.txt", "b"));
        f.File("a.txt", "a");
        f.File("b.txt", "b");
        var trace = new DeliveryTrace(f.Fs);
        var stdout = new RecordedStream("stdout", trace, first == "stdout" ? record : "result", "partial");
        var log = new RecordedStream("log", trace, first == "log" ? record : "result", "partial");
        using var stderr = new RecordedErrorWriter(stderrFails);
        var run = Run(f, f.Args(zip, true, fast, f.PathFor("injected.jsonl")), trace, stdout, log, stderr: stderr);
        Assert.Equal(ExitStatus.Error, run.Status);
        Assert.Equal(1, stderr.Attempts);
        if (record == "run")
        {
            Assert.Empty(f.Fs.Deleted);
            Assert.DoesNotContain(f.Fs.Calls, c => c.StartsWith("OpenEnumeration ", StringComparison.Ordinal));
        }
        else Assert.Single(f.Fs.Deleted);
        Assert.DoesNotContain(f.Fs.Calls, c => c.Contains("b.txt", StringComparison.Ordinal));
        AssertFailedDestinationRetired(stdout, trace);
        AssertFailedDestinationRetired(log, trace);
        Assert.Empty(EntryIndexes(stdout));
        Assert.Equal(record == "entry:1" && first == "stdout" ? new[] { 1 } : [], EntryIndexes(log));
        Assert.DoesNotContain(Records(log), e => Label(e) == "result");
        AssertPartialLine(stdout, "partial");
        AssertPartialLine(log, "partial");
        AssertClosedBeforeDelivery(trace, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J13_OutputFailureSurvivesPreparedCloseFailure(bool fast)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "a"), ("b.txt", "b"));
        f.File("a.txt", "a");
        f.File("b.txt", "b");
        var trace = new DeliveryTrace(f.Fs);
        var stdout = new RecordedStream("stdout", trace);
        var log = new RecordedStream("log", trace, "entry:1", "partial");
        var run = Run(f, f.Args(zip, true, fast, f.PathFor("injected.jsonl")), trace, stdout, log, new FailingRootProbe(f.Fs));
        Assert.Equal(ExitStatus.Error, run.Status);
        AssertCounts(AssertErrorResult(stdout, true, true), 1, 0, 0, 1);
        Assert.DoesNotContain(f.Fs.Calls, c => c.Contains("b.txt", StringComparison.Ordinal));
        AssertFailedDestinationRetired(log, trace);
        using var released = new FileStream(zip, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J13_LogCloseFailureLeavesDeliveredResultUnchanged(bool fast)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "a"));
        f.File("a.txt", "a");
        var trace = new DeliveryTrace(f.Fs);
        var stdout = new RecordedStream("stdout", trace);
        var log = new RecordedStream("log", trace, failClose: true);
        var run = Run(f, f.Args(zip, true, fast, f.PathFor("injected.jsonl")), trace, stdout, log);
        Assert.Equal(ExitStatus.Error, run.Status);
        Assert.Equal(string.Empty, run.Stderr);
        Assert.Equal(stdout.ToArray(), log.ToArray());
        var end = Assert.Single(Records(log), e => Label(e) == "result");
        Assert.Equal("completed", end.GetProperty("outcome").GetString());
        Assert.Equal(0, end.GetProperty("exit_code").GetInt32());
        Assert.Equal(1, log.Closes);
        AssertOrdered(trace.Events, "stdout flush result", "log close");
    }

    private static MachineCliRun Run(MachineCliFixture f, string[] args, DeliveryTrace trace, RecordedStream stdout,
        RecordedStream? log, IFileSystemProbe? probe = null, RecordedErrorWriter? stderr = null)
    {
        using var ownError = stderr is null ? new RecordedErrorWriter(false) : null;
        var error = stderr ?? ownError!;
        var env = new CliEnvironment(f.Probe(probe), f.Fs, () => f.Locations, new UnreadablePrompt(), ShowProgress: true);
        var status = CliApplication.RunMachine(args, stdout, error, env,
            log is null ? null : _ => new(new ExecutionLog(log), null));
        trace.Drain();
        Assert.Equal(0, f.Fs.OpenHandleCount);
        Assert.True(stdout.CanWrite);
        Assert.Equal(0, stdout.Closes);
        if (log is not null) Assert.Equal(1, log.Closes);
        return new(status, stdout.ToArray(), error.ToString(), Records(stdout));
    }

    // 未終端の最後の行は検査対象外。LF済みの全行はそれぞれJSONとして検査する。
    private static JsonElement[] Records(RecordedStream stream)
    {
        var bytes = stream.ToArray();
        Assert.All(bytes, b => Assert.InRange(b, (byte)0, (byte)127));
        Assert.DoesNotContain((byte)'\r', bytes);
        return Encoding.ASCII.GetString(bytes).Split('\n').SkipLast(1).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
            return json.RootElement.Clone();
        }).ToArray();
    }

    private static string Label(JsonElement record) => record.GetProperty("type").GetString() == "entry"
        ? $"entry:{record.GetProperty("index").GetInt32()}" : record.GetProperty("type").GetString()!;
    private static IEnumerable<int> EntryIndexes(RecordedStream stream) => Records(stream)
        .Where(e => e.GetProperty("type").GetString() == "entry").Select(e => e.GetProperty("index").GetInt32());

    private static JsonElement AssertErrorResult(RecordedStream stream, bool delete, bool started)
    {
        var end = Assert.Single(Records(stream), e => Label(e) == "result");
        Assert.Equal("internal_error", end.GetProperty("outcome").GetString());
        Assert.Equal(1, end.GetProperty("exit_code").GetInt32());
        var error = end.GetProperty("error");
        Assert.Equal("internal", error.GetProperty("stage").GetString());
        Assert.Equal("OUTPUT_FAILED", error.GetProperty("code").GetString());
        if (delete) Assert.Equal(started, error.GetProperty("deletion_started").GetBoolean());
        else Assert.False(error.TryGetProperty("deletion_started", out _));
        return end;
    }

    private static void AssertCounts(JsonElement end, int deleted, int directory, int excluded, int unprocessed)
    {
        var counts = end.GetProperty("counts");
        Assert.Equal(deleted, counts.GetProperty("deleted").GetInt32());
        Assert.Equal(directory, counts.GetProperty("directory").GetInt32());
        Assert.Equal(excluded, counts.GetProperty("not_selected").GetInt32());
        Assert.Equal(unprocessed, counts.GetProperty("unprocessed").GetInt32());
        foreach (var key in new[] { "modified", "missing", "skipped_special_file", "delete_failed" })
            Assert.Equal(0, counts.GetProperty(key).GetInt32());
    }

    private static void AssertFailedDestinationRetired(RecordedStream stream, DeliveryTrace trace)
    {
        Assert.NotNull(stream.FailedAt);
        Assert.Equal(stream.FailedAt!.Value + 1, stream.Attempts.Count);
        var failure = trace.Events.FindIndex(e => e == $"{stream.Name} failed");
        Assert.True(failure >= 0);
        Assert.DoesNotContain(trace.Events.Skip(failure + 1), e => e.StartsWith(stream.Name + " write ", StringComparison.Ordinal)
            || e.StartsWith(stream.Name + " flush ", StringComparison.Ordinal));
    }

    private static void AssertPartialLine(RecordedStream stream, string operation)
    {
        if (operation != "partial") return;
        var bytes = stream.ToArray();
        Assert.NotEmpty(bytes);
        Assert.NotEqual((byte)'\n', bytes[^1]);
        var tail = Encoding.ASCII.GetString(bytes).Split('\n')[^1];
        // JsonDocumentの構文エラーはJsonExceptionの内部派生型で報告される。
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(tail));
    }

    private static void AssertClosedBeforeDelivery(DeliveryTrace trace, bool delete)
    {
        foreach (var snapshot in trace.Snapshots)
        {
            Assert.Equal(0, snapshot.EntryHandles);
            if (snapshot.Label == "result") Assert.Equal(0, snapshot.AllHandles);
        }
        foreach (var snapshot in trace.Snapshots.Where(s => s.EntryName is not null && !s.Directory))
        {
            var write = trace.Events.FindIndex(e => e.EndsWith(" write " + snapshot.Label, StringComparison.Ordinal));
            var preceding = trace.Events.Take(write).ToArray();
            // ファイル名まで照合し、前のエントリのcloseを根拠にしない。
            Assert.Contains(preceding, e => e.StartsWith(delete ? "Close deletion " : "Close comparison ", StringComparison.Ordinal)
                && e.EndsWith("\\" + snapshot.EntryName, StringComparison.Ordinal));
        }
    }

    private static void AssertModeReads(MachineCliFixture f, bool fast)
    {
        if (fast) Assert.DoesNotContain(f.Fs.Calls, c => c.StartsWith("Read ", StringComparison.Ordinal));
    }

    private static void AssertOrdered(List<string> events, params string[] fragments)
    {
        var cursor = 0;
        foreach (var fragment in fragments)
        {
            var next = events.FindIndex(cursor, e => e.Contains(fragment, StringComparison.Ordinal));
            Assert.True(next >= cursor, $"Missing/out of order: {fragment}\n{string.Join('\n', events)}");
            // path断片は直前と同じFS呼び出し内で照合する。
            cursor = fragment.EndsWith(' ') ? next : next + 1;
        }
    }

    // 各I/Oの直前にfakeの記録をdrainし、同一時間順にまとめる。fakeや製品へのフック追加は不要。
    private sealed class DeliveryTrace(FakeFileSystem fs)
    {
        private int _cursor;
        public List<string> Events { get; } = [];
        public List<(string Label, int EntryHandles, int AllHandles, string? EntryName, bool Directory)> Snapshots { get; } = [];
        public void Drain()
        {
            Events.AddRange(fs.Calls.Skip(_cursor));
            _cursor = fs.Calls.Count;
        }
        public void Record(string name, string operation, string label, string? entryName, bool directory)
        {
            Drain();
            Events.Add($"{name} {operation} {label}");
            Snapshots.Add((label, fs.OpenComparisonCount + fs.OpenDeletionHandleCount, fs.OpenHandleCount, entryName, directory));
        }
    }

    private sealed class RecordedStream(string name, DeliveryTrace trace, string? failRecord = null,
        string failOperation = "write", bool failClose = false) : MemoryStream
    {
        private string _label = "";
        private string? _entryName;
        private bool _directory;
        public string Name { get; } = name;
        public List<string> Attempts { get; } = [];
        public int? FailedAt { get; private set; }
        public int Closes { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            using var record = JsonDocument.Parse(buffer.ToArray());
            _label = Label(record.RootElement);
            _entryName = record.RootElement.TryGetProperty("name", out var entryName) ? entryName.GetString() : null;
            _directory = record.RootElement.TryGetProperty("directory", out var directory) && directory.GetBoolean();
            Attempt("write");
            if (_label == failRecord && failOperation is "write" or "partial")
            {
                if (failOperation == "partial") base.Write(buffer[..(buffer.Length / 2)]);
                Fail();
            }
            base.Write(buffer);
        }
        public override void Flush()
        {
            Attempt("flush");
            if (_label == failRecord && failOperation == "flush") Fail();
            base.Flush();
        }
        private void Attempt(string operation)
        {
            trace.Record(Name, operation, _label, _entryName, _directory);
            Attempts.Add($"{operation} {_label}");
        }
        private void Fail()
        {
            FailedAt = Attempts.Count - 1;
            trace.Events.Add($"{Name} failed");
            throw new IOException("injected output failure");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Closes++;
                trace.Drain();
                trace.Events.Add($"{Name} close");
            }
            base.Dispose(disposing);
            if (disposing && failClose) throw new IOException("injected close failure");
        }
    }

    private sealed class RecordedErrorWriter(bool fail) : StringWriter
    {
        public int Attempts { get; private set; }
        public override void WriteLine(string? value)
        {
            Attempts++;
            if (fail) throw new IOException("injected stderr failure");
            base.WriteLine(value);
        }
    }

    private sealed class UnreadablePrompt : IConfirmationPrompt
    {
        public bool IsInteractive => throw new InvalidOperationException("must not query prompt");
        public string? Ask(string prompt) => throw new InvalidOperationException("must not read stdin");
    }

    private sealed class FailingRootProbe(IFileSystemProbe inner) : IFileSystemProbe
    {
        public ProbeResult<TargetConfirmation> ConfirmTargetFinalComponent(string path) => inner.ConfirmTargetFinalComponent(path);
        public ProbeResult<IDirectoryHandle> OpenTargetRoot(string path)
        {
            var result = inner.OpenTargetRoot(path);
            return result.Succeeded ? ProbeResult<IDirectoryHandle>.Ok(new Handle(result.Value)) : result;
        }
        public ProbeResult<IDirectoryHandle> OpenDirectoryForEnumeration(string path) => inner.OpenDirectoryForEnumeration(path);
        public ProbeResult<IComparisonHandle> OpenForComparison(string path) => inner.OpenForComparison(path);
        public ProbeResult<VolumeFileId> GetFileIdentity(string path) => inner.GetFileIdentity(path);
        private sealed class Handle(IDirectoryHandle inner) : IDirectoryHandle
        {
            public ProbeResult<DirectoryHandleInfo> GetInfo() => inner.GetInfo();
            public ProbeResult<string> GetFileSystemName() => inner.GetFileSystemName();
            public IDirectoryEnumeration Enumerate() => inner.Enumerate();
            public void Dispose()
            {
                inner.Dispose();
                throw new IOException("injected root close failure");
            }
        }
    }
}
