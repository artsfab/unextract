using System.Text;
using System.Text.Json;

namespace Unextract.Cli.Tests;

public class MachineOutputWriterTests
{
    private static readonly MachineRunRecord Run = new("delete", "strict", "archive.zip", "C:\\target", 1, 1, false);
    private static readonly MachineEntryRecord Entry = new(1, "日本語.txt", false, 100, "DELETED");
    private static readonly MachineResultRecord Completed = new("completed", 0);
    private static readonly MachineResultRecord OutputFailed = new("internal_error", 1,
        Error: new MachineError("internal", "OUTPUT_FAILED", DeletionStarted: true));

    [Fact]
    public void J09_SameBytesAreSentLogFirstWithFlushAtEveryRecord()
    {
        var events = new List<string>();
        using var stdout = new RecordingStream("stdout", events);
        var logStream = new RecordingStream("log", events);
        using (var log = new ExecutionLog(logStream))
        {
            var writer = new MachineOutputWriter(stdout, log);
            writer.WriteRun(Run);
            writer.WriteEntry(Entry);
            Assert.True(writer.WriteResult(Completed));
            Assert.Null(writer.LastFailure);
            Assert.True(writer.HasWritableDestination);
            Assert.Equal(Enumerable.Repeat(new[] { "log.write", "log.flush", "stdout.write", "stdout.flush" }, 3).SelectMany(x => x), events);
            Assert.Equal(logStream.Bytes, stdout.Bytes);
            Assert.Equal(MachineOutput.Serialize(Run).Concat(MachineOutput.Serialize(Entry)).Concat(MachineOutput.Serialize(Completed)), stdout.Bytes);
        }
        Assert.Equal(1, logStream.CloseCalls);
        Assert.Equal(0, stdout.CloseCalls);
        Assert.Equal("log.close", events[^1]);
        // 借用 stdout は result とログclose の後も利用できる。
        stdout.WriteByte((byte)'x');
    }

    [Fact]
    public void J09_StdoutOnlyIsFlushedAndNeverOwned()
    {
        var events = new List<string>();
        using var stdout = new RecordingStream("stdout", events);
        var writer = new MachineOutputWriter(stdout);
        writer.WriteRun(Run);
        Assert.True(writer.WriteResult(Completed));
        Assert.Equal(["stdout.write", "stdout.flush", "stdout.write", "stdout.flush"], events);
        Assert.Equal(0, stdout.CloseCalls);
    }

    [Theory]
    [InlineData(true, "write")]
    [InlineData(true, "partial")]
    [InlineData(true, "flush")]
    [InlineData(false, "write")]
    [InlineData(false, "partial")]
    [InlineData(false, "flush")]
    public void J09_FailedDestinationIsNeverReusedAndRecoveryUsesOnlyHealthyDestination(bool failLog, string failure)
    {
        var events = new List<string>();
        using var stdout = new RecordingStream("stdout", events);
        var logStream = new RecordingStream("log", events);
        using var log = new ExecutionLog(logStream);
        var broken = failLog ? logStream : stdout;
        broken.Failure = failure;
        var writer = new MachineOutputWriter(stdout, log);
        var exception = Assert.Throws<MachineOutputException>(() => writer.WriteEntry(Entry));
        Assert.Equal(failLog ? MachineOutputDestination.Log : MachineOutputDestination.Stdout, exception.Destination);
        Assert.Equal(failure == "flush" ? MachineOutputOperation.Flush : MachineOutputOperation.Write, exception.Operation);
        Assert.IsType<IOException>(exception.InnerException);
        Assert.Same(exception, writer.LastFailure);
        var failedBytes = broken.Bytes;
        var failedCalls = broken.WriteCalls + broken.FlushCalls;
        if (failLog)
        {
            Assert.Equal(0, stdout.WriteCalls);
            Assert.Equal(0, stdout.FlushCalls);
        }
        else Assert.Equal(["log.write", "log.flush", "stdout.write"], events.Take(3));
        // 呼び出し側の誤った継続でも run/entry を配送しない。
        Assert.Same(exception, Assert.Throws<MachineOutputException>(() => writer.WriteEntry(Entry)));
        Assert.Same(exception, Assert.Throws<MachineOutputException>(() => writer.WriteRun(Run)));
        Assert.True(writer.WriteResult(OutputFailed));
        Assert.Equal(failedCalls, broken.WriteCalls + broken.FlushCalls);
        Assert.Equal(failedBytes, broken.Bytes);
        var healthy = failLog ? stdout : logStream;
        var expected = failLog ? MachineOutput.Serialize(OutputFailed)
            : MachineOutput.Serialize(Entry).Concat(MachineOutput.Serialize(OutputFailed)).ToArray();
        Assert.Equal(expected, healthy.Bytes);
        Assert.Equal(failure == "write" ? 0 : failure == "partial" ? 11 : MachineOutput.Serialize(Entry).Length, failedBytes.Length);
        if (failure == "partial") Assert.DoesNotContain((byte)'\n', failedBytes);
        log.Dispose();
        Assert.Equal(failedCalls, broken.WriteCalls + broken.FlushCalls);
        Assert.Equal(failedBytes, broken.Bytes);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("partial")]
    [InlineData("flush")]
    public void J09_WithoutLogNoResultIsAppendedToFailedStdout(string failure)
    {
        var events = new List<string>();
        using var stdout = new RecordingStream("stdout", events) { Failure = failure };
        var writer = new MachineOutputWriter(stdout);
        var exception = Assert.Throws<MachineOutputException>(() => writer.WriteEntry(Entry));
        var bytes = stdout.Bytes;
        var calls = events.ToArray();
        Assert.False(writer.HasWritableDestination);
        Assert.False(writer.WriteResult(OutputFailed));
        Assert.Same(exception, writer.LastFailure);
        Assert.Equal(bytes, stdout.Bytes);
        Assert.Equal(calls, events);
        Assert.Throws<InvalidOperationException>(() => writer.WriteResult(OutputFailed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void J09_RecoveryFailureDoesNotRetryOrRecurse(bool firstFailureIsLog)
    {
        var events = new List<string>();
        using var stdout = new RecordingStream("stdout", events);
        var logStream = new RecordingStream("log", events);
        using var log = new ExecutionLog(logStream);
        (firstFailureIsLog ? logStream : stdout).Failure = "partial";
        var writer = new MachineOutputWriter(stdout, log);
        Assert.Throws<MachineOutputException>(() => writer.WriteEntry(Entry));
        (firstFailureIsLog ? stdout : logStream).Failure = "write";
        Assert.False(writer.WriteResult(OutputFailed));
        Assert.False(writer.HasWritableDestination);
        Assert.Equal(firstFailureIsLog ? MachineOutputDestination.Stdout : MachineOutputDestination.Log, writer.LastFailure!.Destination);
        var calls = events.ToArray();
        Assert.Throws<InvalidOperationException>(() => writer.WriteResult(OutputFailed));
        Assert.Throws<InvalidOperationException>(() => writer.WriteEntry(Entry));
        Assert.Equal(calls, events);
    }

    [Theory]
    [InlineData(true, "write")]
    [InlineData(true, "partial")]
    [InlineData(true, "flush")]
    [InlineData(false, "write")]
    [InlineData(false, "partial")]
    [InlineData(false, "flush")]
    public void J09_TerminalFailureRequestsExitOneAndForbidsAnotherResult(bool failLog, string failure)
    {
        var events = new List<string>();
        using var stdout = new RecordingStream("stdout", events);
        var logStream = new RecordingStream("log", events);
        using var log = new ExecutionLog(logStream);
        (failLog ? logStream : stdout).Failure = failure;
        var writer = new MachineOutputWriter(stdout, log);
        Assert.False(writer.WriteResult(Completed));
        Assert.NotNull(writer.LastFailure);
        var delivered = logStream.Bytes;
        var calls = events.ToArray();
        Assert.Throws<InvalidOperationException>(() => writer.WriteResult(OutputFailed));
        Assert.Throws<InvalidOperationException>(() => writer.WriteRun(Run));
        Assert.Equal(calls, events);
        Assert.Equal(delivered, logStream.Bytes);
        if (failLog) Assert.Empty(stdout.Bytes);
        else
        {
            using var document = JsonDocument.Parse(delivered);
            Assert.Equal("completed", document.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("exit_code").GetInt32());
            Assert.Equal(1, Encoding.ASCII.GetString(delivered).Count(c => c == '\n'));
        }
    }

    [Fact]
    public void J09_CloseFailureIsTypedAfterOneResultAndIsNotRetried()
    {
        var events = new List<string>();
        using var stdout = new RecordingStream("stdout", events);
        var logStream = new RecordingStream("log", events) { FailClose = true };
        var log = new ExecutionLog(logStream);
        var writer = new MachineOutputWriter(stdout, log);
        Assert.True(writer.WriteResult(Completed));
        var bytes = logStream.Bytes;
        var exception = Assert.Throws<MachineOutputException>(log.Dispose);
        Assert.Equal(MachineOutputDestination.Log, exception.Destination);
        Assert.Equal(MachineOutputOperation.Close, exception.Operation);
        Assert.IsType<IOException>(exception.InnerException);
        log.Dispose();
        Assert.Equal(1, logStream.CloseCalls);
        Assert.Equal(bytes, logStream.Bytes);
        Assert.Equal(bytes, stdout.Bytes);
        Assert.Throws<InvalidOperationException>(() => writer.WriteResult(OutputFailed));
        Assert.Equal(0, stdout.CloseCalls);
    }

    // write の失敗は0byte/部分byte、flushの失敗は全byte受領後に注入する。
    // 失敗後も書ける stream としておき、製品側で再利用を拒否したことを直接確認する。
    private sealed class RecordingStream(string name, List<string> events) : Stream
    {
        private readonly MemoryStream _buffer = new();
        public string? Failure { get; set; }
        public bool FailClose { get; init; }
        public int WriteCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public byte[] Bytes => _buffer.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            events.Add(name + ".write");
            WriteCalls++;
            if (Failure is "write" or "partial")
            {
                var partial = Failure == "partial";
                Failure = null;
                if (partial) _buffer.Write(buffer[..11]);
                throw new IOException("injected write failure");
            }
            _buffer.Write(buffer);
        }
        public override void Flush()
        {
            events.Add(name + ".flush");
            FlushCalls++;
            if (Failure == "flush")
            {
                Failure = null;
                throw new IOException("injected flush failure");
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                events.Add(name + ".close");
                CloseCalls++;
                _buffer.Dispose();
                if (FailClose) throw new IOException("injected close failure");
            }
            base.Dispose(disposing);
        }
    }
}
