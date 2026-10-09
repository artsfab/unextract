using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Unextract.Core.Analysis;
using Unextract.Core.Commands;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;
using ProtectedLocationsResult = Unextract.Windows.ProtectedLocationsResult;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.Cli.Tests;

// J12: CLIの通知・終端・ログ所有の結合。ZIP/log/entriesだけが実fixture、targetの削除は偽FSで模擬する。
// 両操作/両モードの通知の接続、全 outcome と終了コード、ShowProgress 有効時の stderr 空・確認入力の非参照、Prepare の各段階 (両モード)・
// 引数/ログ作成の失敗の非接触、空/全 DIRECTORY・entries の欠番選択と未処理。ログ作成後の Prepare 失敗、存在しない ZIP/entries とログの
// 同一パス、正常時の byte 一致・終了後の解放、Prepared の close 例外時の ZIP 解放と成功 result の抑制、OUTPUT_FAILED と開始の有無、
// 終端の log write/close 失敗・stderr 失敗でも実終了 1 (docs/spec/machine-output.md#invocation、#result、#log)。
public class MachineCliTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void J12_NormalRunUsesOnlyRecordsAndPreservesModeAndCounts(bool delete, bool fast)
    {
        var f = new MachineCliFixture();
        f.File("same.txt", "hello");
        f.File("changed.txt", "HELLO");
        f.File("size.txt", "longer");
        var zip = f.Zip(("same.txt", "hello"), ("changed.txt", "hello"), ("size.txt", "hello"),
            ("missing.txt", "x"), ("d/", null));
        var log = delete ? f.PathFor("run.jsonl") : null;
        var run = f.Run(f.Args(zip, delete, fast, log));

        Assert.Equal(ExitStatus.Success, run.Status);
        Assert.Equal(string.Empty, run.Stderr);
        Assert.Equal(delete ? 6 : 7, run.Records.Length);
        var start = run.Records[0];
        Assert.Equal("run", start.GetProperty("type").GetString());
        Assert.Equal(delete ? "delete" : "analyze", start.GetProperty("operation").GetString());
        Assert.Equal(fast ? "fast" : "strict", start.GetProperty("mode").GetString());
        Assert.Equal(zip, start.GetProperty("archive").GetString());
        Assert.Equal(MachineCliFixture.Target, start.GetProperty("target").GetString());
        Assert.Equal(5, start.GetProperty("entries_total").GetInt32());
        Assert.Equal(5, start.GetProperty("selected").GetInt32());
        Assert.False(start.GetProperty("entries_option").GetBoolean());
        Assert.Equal(new[] { delete ? "DELETED" : fast ? "SAME_SIZE" : "MATCHED",
            fast ? delete ? "DELETED" : "SAME_SIZE" : "MODIFIED", "MODIFIED", "MISSING" },
            run.Records.Skip(1).Take(4).Select(e => e.GetProperty("status").GetString()));
        Assert.Equal(new[] { 1, 2, 3, 4 }, run.Records.Skip(1).Take(4).Select(e => e.GetProperty("index").GetInt32()));
        if (!delete)
        {
            Assert.Equal("DIRECTORY", run.Records[5].GetProperty("status").GetString());
            Assert.True(run.Records[5].GetProperty("directory").GetBoolean());
            Assert.Equal(0, run.Records[5].GetProperty("length").GetInt64());
            Assert.Empty(f.Fs.Deleted);
            Assert.Equal(0, f.Fs.DeletionOpenCount);
        }
        else
        {
            Assert.Equal(0, f.Fs.ComparisonOpenCount);
            Assert.Equal(run.Bytes, System.IO.File.ReadAllBytes(log!));
            AssertLogReleased(log!);
        }
        var end = AssertResult(run, "completed", 0);
        Assert.False(end.TryGetProperty("error", out _));
        var counts = end.GetProperty("counts");
        Assert.Equal(fast ? 1 : 2, counts.GetProperty("modified").GetInt32());
        Assert.Equal(1, counts.GetProperty("missing").GetInt32());
        Assert.Equal(1, counts.GetProperty("directory").GetInt32());
        Assert.Equal(0, counts.GetProperty("skipped_special_file").GetInt32());
        Assert.Equal(fast ? 2 : 1, counts.GetProperty(delete ? "deleted" : fast ? "same_size" : "matched").GetInt32());
        if (delete)
        {
            Assert.Equal(0, counts.GetProperty("unprocessed").GetInt32());
            Assert.Equal(0, counts.GetProperty("not_selected").GetInt32());
            Assert.Equal(0, counts.GetProperty("delete_failed").GetInt32());
        }
        else
        {
            Assert.False(counts.TryGetProperty(fast ? "matched" : "same_size", out _));
            Assert.False(counts.TryGetProperty("undetermined", out _));
        }
    }

    public static TheoryData<string[]> UsageArguments => new()
    {
        new[] { "--jsonl", "analyze", "a.zip", "--target", "dir" },
        new[] { "a.zip", "--target", "dir", "--jsonl" },
        new[] { "delete", "a.zip", "--target", "dir", "--jsonl" },
        new[] { "delete", "--dry-run", "--jsonl" },
        new[] { "delete", "--jsonl", "--jsonl" },
        new[] { "analyze", "a.zip", "--target", "dir", "--jsonl", "--log", "log" },
        new[] { "delete", "a.zip", "--target", "dir", "--jsonl", "--yes", "--log" },
        new[] { "delete", "a.zip", "--target", "dir", "--jsonl", "--yes", "--unknown" },
    };

    [Theory]
    [MemberData(nameof(UsageArguments))]
    public void J12_UsageDoesNotResolveEnvironmentOrCreateLog(string[] args)
    {
        var f = new MachineCliFixture();
        var logCalls = 0;
        var run = f.Run(args, createLog: _ =>
        {
            logCalls++;
            throw new InvalidOperationException("must not create a log");
        });
        Assert.True(CliApplication.IsMachineMode(args));
        Assert.Single(run.Records);
        var end = AssertResult(run, "input_error", 1, "USAGE", "usage");
        Assert.False(end.TryGetProperty("counts", out _));
        Assert.Equal(0, logCalls);
        Assert.Equal(0, f.LocationCalls);
        Assert.Empty(f.Fs.Calls);
    }

    [Theory]
    [InlineData("archive", false, "fatal", "ARCHIVE_OPEN_FAILED")]
    [InlineData("archive", true, "fatal", "ARCHIVE_OPEN_FAILED")]
    [InlineData("unreadable", false, "fatal", "ARCHIVE_UNREADABLE")]
    [InlineData("unreadable", true, "fatal", "ARCHIVE_UNREADABLE")]
    [InlineData("locations", false, "input_error", "PROTECTED_LOCATION_UNRESOLVED")]
    [InlineData("locations", true, "input_error", "PROTECTED_LOCATION_UNRESOLVED")]
    [InlineData("target", false, "input_error", "TARGET_NOT_FOUND")]
    [InlineData("target", true, "input_error", "TARGET_NOT_FOUND")]
    [InlineData("validation", false, "fatal", "DOT_DOT_COMPONENT")]
    [InlineData("validation", true, "fatal", "DOT_DOT_COMPONENT")]
    [InlineData("identity", false, "fatal", "ARCHIVE_IDENTITY_FAILED")]
    [InlineData("identity", true, "fatal", "ARCHIVE_IDENTITY_FAILED")]
    [InlineData("entries", true, "input_error", "ENTRIES_NO_LINES")]
    [InlineData("match", true, "input_error", "ENTRIES_NO_MATCH")]
    public void J12_PrepareFailuresHaveNoRunAndCreatedLogIncludesResult(string stage, bool delete, string outcome, string code)
    {
        // Prepare の失敗は run より前に終わるので、Fast でも同じ result になる (E2E の J15 は Strict だけ)。
        foreach (var fast in new[] { false, true })
        {
            var f = new MachineCliFixture();
            var zip = stage == "validation" ? f.Zip(("../bad.txt", "x")) : f.Zip(("a.txt", "hello"));
            string? entries = null;
            switch (stage)
            {
                case "archive": zip = f.PathFor("absent.zip"); break;
                case "unreadable": System.IO.File.WriteAllText(zip, "invalid zip"); break;
                case "locations": f.Locations = new(null, new FatalError(FatalKind.ProtectedLocationUnresolved)); break;
                case "target": f.Fs.Remove(f.Fs.Get(MachineCliFixture.Target)); break;
                case "identity": f.Fs.Get(f.FakePathFor(zip)).Errors[FakeOp.GetFileIdentity] = 5; break;
                case "entries": entries = f.Entries(string.Empty); break;
                case "match": entries = f.Entries("unknown.txt\n"); break;
            }
            var log = delete ? f.PathFor("run.jsonl") : null;
            var run = f.Run(f.Args(zip, delete, fast, log: log, entries: entries));
            Assert.Single(run.Records);
            var end = AssertResult(run, outcome, 1, code, "prepare");
            if (!delete && stage is "validation" or "identity")
            {
                Assert.Equal(new[] { "undetermined" }, end.GetProperty("counts").EnumerateObject().Select(p => p.Name));
                Assert.Equal(1, end.GetProperty("counts").GetProperty("undetermined").GetInt32());
            }
            else Assert.False(end.TryGetProperty("counts", out _));
            AssertNoEntriesTouched(f);
            if (log is not null)
            {
                Assert.Equal(run.Bytes, System.IO.File.ReadAllBytes(log));
                AssertLogReleased(log);
            }
            using var releasedZip = new FileStream(zip, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
    }

    [Theory]
    [InlineData("existing", "LOG_ALREADY_EXISTS")]
    [InlineData("archive", "LOG_ALREADY_EXISTS")]
    [InlineData("entries", "LOG_ALREADY_EXISTS")]
    [InlineData("missing-parent", "LOG_CREATE_FAILED")]
    public void J12_LogCreationFailurePrecedesPrepareAndPreservesFiles(string kind, string code)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "hello"));
        var entries = f.Entries("a.txt\n");
        var log = kind switch
        {
            "archive" => zip,
            "entries" => entries,
            "missing-parent" => f.PathFor("absent/run.jsonl"),
            _ => f.Entries("keep this", "existing.jsonl"),
        };
        var original = kind == "missing-parent" ? null : System.IO.File.ReadAllBytes(log);
        var run = f.Run(f.Args(zip, true, log: log, entries: entries));
        Assert.Single(run.Records);
        AssertResult(run, "input_error", 1, code, "prepare");
        Assert.Equal(0, f.LocationCalls);
        Assert.Empty(f.Fs.Calls);
        if (original is not null) Assert.Equal(original, System.IO.File.ReadAllBytes(log));
        else Assert.False(System.IO.File.Exists(log));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J12_LogCreatedAtAbsentArchiveOrEntriesPathRecordsSharingFailure(bool entriesPath)
    {
        var f = new MachineCliFixture();
        var zip = entriesPath ? f.Zip(("a.txt", "hello")) : f.PathFor("absent.zip");
        var log = entriesPath ? f.PathFor("absent-entries.txt") : zip;
        var run = f.Run(f.Args(zip, true, log: log, entries: entriesPath ? log : null));
        Assert.Single(run.Records);
        AssertResult(run, entriesPath ? "input_error" : "fatal", 1,
            entriesPath ? "ENTRIES_UNREADABLE" : "ARCHIVE_OPEN_FAILED", "prepare");
        Assert.Equal(run.Bytes, System.IO.File.ReadAllBytes(log));
        AssertNoEntriesTouched(f);
        Assert.Equal(0, f.LocationCalls);
        AssertLogReleased(log);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void J12_EmptyOrDirectoryOnlyZipCompletesWithoutOpeningFiles(bool delete, bool directories)
    {
        var f = new MachineCliFixture();
        var zip = directories ? f.Zip(("d/", null), ("e/", null)) : f.Zip();
        var run = f.Run(f.Args(zip, delete));
        Assert.Equal(delete || !directories ? 2 : 4, run.Records.Length);
        var end = AssertResult(run, "completed", 0);
        Assert.Equal(directories ? 2 : 0, end.GetProperty("counts").GetProperty("directory").GetInt32());
        AssertNoEntriesTouched(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J12_EntriesSelectionCountsNotSelectedAndStoppedRemainderSeparately(bool stop)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("d/", null), ("excluded.txt", "hello"), ("first.txt", "hello"), ("last.txt", "hello"));
        var first = f.File("first.txt", "hello");
        f.File("last.txt", "hello");
        if (stop) first.Errors[FakeOp.OpenDeletion] = 87;
        var run = f.Run(f.Args(zip, true, entries: f.Entries("last.txt\nfirst.txt\n")));
        Assert.Equal(2, run.Records[0].GetProperty("selected").GetInt32());
        Assert.True(run.Records[0].GetProperty("entries_option").GetBoolean());
        Assert.Equal(stop ? new[] { 3 } : new[] { 3, 4 },
            run.Records.Where(e => e.GetProperty("type").GetString() == "entry").Select(e => e.GetProperty("index").GetInt32()));
        var end = AssertResult(run, stop ? "stopped" : "completed", stop ? 1 : 0,
            stop ? "OPEN_FAILED" : null, stop ? "entry" : null);
        Assert.Equal(2, end.GetProperty("counts").GetProperty("not_selected").GetInt32());
        Assert.Equal(stop ? 1 : 0, end.GetProperty("counts").GetProperty("unprocessed").GetInt32());
        Assert.DoesNotContain(f.Fs.Calls, c => c.Contains("excluded.txt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, 32, "completed", "DELETE_OPEN_REFUSED")]
    [InlineData(false, 87, "stopped", "OPEN_FAILED")]
    [InlineData(false, -1, "stopped", "UNEXPECTED_EXCEPTION")]
    [InlineData(true, 32, "completed", "DELETE_OPEN_REFUSED")]
    [InlineData(true, 87, "stopped", "OPEN_FAILED")]
    [InlineData(true, -1, "stopped", "UNEXPECTED_EXCEPTION")]
    public void J12_DeleteFailedAndStoppedKeepExistingBoundary(bool fast, int failure, string outcome, string code)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("first.txt", "hello"), ("last.txt", "hello"));
        var first = f.File("first.txt", "hello");
        f.File("last.txt", "hello");
        if (failure < 0) first.ThrowAfterDisposition = true;
        else first.Errors[FakeOp.OpenDeletion] = failure;
        var run = f.Run(f.Args(zip, true, fast));
        var end = AssertResult(run, outcome, 1, outcome == "stopped" ? code : null, outcome == "stopped" ? "entry" : null);
        var entry = run.Records[1];
        Assert.Equal(code, entry.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal(outcome == "stopped" ? "STOPPED" : "DELETE_FAILED", entry.GetProperty("status").GetString());
        if (outcome == "stopped")
        {
            Assert.Equal(failure < 0, entry.GetProperty("possibly_deleted").GetBoolean());
            Assert.Equal(failure < 0, end.GetProperty("error").GetProperty("possibly_deleted").GetBoolean());
            Assert.Equal(1, end.GetProperty("counts").GetProperty("unprocessed").GetInt32());
            Assert.Equal(3, run.Records.Length);
        }
        else
        {
            Assert.False(end.TryGetProperty("error", out _));
            Assert.False(entry.TryGetProperty("possibly_deleted", out _));
            Assert.Equal(1, end.GetProperty("counts").GetProperty("delete_failed").GetInt32());
            Assert.Equal(1, end.GetProperty("counts").GetProperty("deleted").GetInt32());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J12_AnalyzeFatalReportsCauseWithoutEntry(bool fast)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("first.txt", "hello"), ("bad.txt", "hello"), ("last.txt", "hello"));
        f.File("first.txt", "hello");
        f.File("bad.txt", "hello").Errors[FakeOp.OpenComparison] = 5;
        f.File("last.txt", "hello");
        var run = f.Run(f.Args(zip, false, fast));
        Assert.Equal(3, run.Records.Length);
        var end = AssertResult(run, "fatal", 1, "COMPARISON_OPEN_FAILED", "entry");
        Assert.Equal(2, end.GetProperty("error").GetProperty("entry_index").GetInt32());
        Assert.Equal(5, end.GetProperty("error").GetProperty("win32_error").GetInt32());
        Assert.Equal(1, end.GetProperty("counts").GetProperty("undetermined").GetInt32());
        Assert.DoesNotContain(f.Fs.Calls, c => c.Contains("last.txt", StringComparison.Ordinal));
        Assert.Equal(0, f.Fs.DeletionOpenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J12_PrepareUnexpectedExceptionHasNoRunOrCounts(bool delete)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "hello"));
        f.ResolveFailure = new IOException("bad\u202E");
        var run = f.Run(f.Args(zip, delete));
        Assert.Single(run.Records);
        var end = AssertResult(run, "internal_error", 1, "UNEXPECTED_EXCEPTION", "internal");
        Assert.False(end.TryGetProperty("counts", out _));
        var error = end.GetProperty("error");
        if (delete) Assert.False(error.GetProperty("deletion_started").GetBoolean());
        else Assert.False(error.TryGetProperty("deletion_started", out _));
        AssertNoEntriesTouched(f);
        Assert.Contains(@"\u{202E}", error.GetProperty("message").GetString());
        using var released = new FileStream(zip, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J12_PreparedDisposeFailurePrecedesResultAndReleasesArchive(bool delete)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "hello"));
        f.File("a.txt", "hello");
        var probe = new FailingRootDisposeProbe(f.Fs);
        var run = f.Run(f.Args(zip, delete), probe);
        Assert.Equal(3, run.Records.Length);
        var end = AssertResult(run, "internal_error", 1, "UNEXPECTED_EXCEPTION", "internal");
        if (delete)
        {
            Assert.True(end.GetProperty("error").GetProperty("deletion_started").GetBoolean());
            Assert.Equal(1, end.GetProperty("counts").GetProperty("deleted").GetInt32());
            Assert.Equal(0, end.GetProperty("counts").GetProperty("unprocessed").GetInt32());
        }
        else Assert.False(end.TryGetProperty("counts", out _));
        using var released = new FileStream(zip, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J12_AnalyzeProcessorExceptionIsInternalWithoutDeletionFields(bool fast)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "hello"));
        f.File("a.txt", "hello");
        f.Fs.BeforeOpenComparison = _ => throw new IOException("injected analyzer failure");
        var run = f.Run(f.Args(zip, false, fast));
        Assert.Equal(2, run.Records.Length);
        var end = AssertResult(run, "internal_error", 1, "UNEXPECTED_EXCEPTION", "internal");
        Assert.False(end.TryGetProperty("counts", out _));
        Assert.False(end.GetProperty("error").TryGetProperty("deletion_started", out _));
        Assert.False(end.GetProperty("error").TryGetProperty("step", out _));
        Assert.Equal(0, f.Fs.DeletionOpenCount);
    }

    [Fact]
    public void J12_LogWriteFailureIsReportedAsOutputFailedBeforeDeletionStarts()
    {
        var f = new MachineCliFixture();
        var zip = f.Zip(("a.txt", "hello"));
        f.File("a.txt", "hello");
        var stream = new FailingOutputStream(failWrite: 1);
        var run = f.Run(f.Args(zip, true, log: f.PathFor("injected.jsonl")),
            createLog: _ => new(new ExecutionLog(stream), null));
        Assert.Single(run.Records);
        var end = AssertResult(run, "internal_error", 1, "OUTPUT_FAILED", "internal");
        Assert.False(end.GetProperty("error").GetProperty("deletion_started").GetBoolean());
        AssertNoEntriesTouched(f);
        Assert.Equal(1, stream.Writes);
        Assert.Equal(1, stream.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J12_TerminalLogWriteOrCloseFailureReturnsErrorWithoutAnotherResult(bool closeFailure)
    {
        var f = new MachineCliFixture();
        var zip = f.Zip();
        var stream = new FailingOutputStream(failWrite: closeFailure ? 0 : 2, failClose: closeFailure);
        var run = f.Run(f.Args(zip, true, log: f.PathFor("injected.jsonl")),
            createLog: _ => new(new ExecutionLog(stream), null));
        Assert.Equal(ExitStatus.Error, run.Status);
        Assert.Equal(string.Empty, run.Stderr);
        Assert.Equal(1, stream.Closes);
        Assert.Equal(2, stream.Writes);
        if (closeFailure)
        {
            var end = Assert.Single(run.Records, e => e.GetProperty("type").GetString() == "result");
            Assert.Equal("completed", end.GetProperty("outcome").GetString());
            Assert.Equal(0, end.GetProperty("exit_code").GetInt32());
            Assert.Equal(run.Bytes, stream.ToArray());
        }
        else Assert.Single(run.Records); // runだけ。終端の配送を再試行しない。
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J12_AllOutputUnavailableStillReturnsErrorWhenStderrAlsoFails(bool stderrFailure)
    {
        using var stdout = new FailingOutputStream(failWrite: 1);
        using var stderr = new FailingErrorWriter(stderrFailure);
        var status = CliApplication.RunMachine(["delete", "--jsonl"], stdout, stderr);
        Assert.Equal(ExitStatus.Error, status);
        Assert.Equal(1, stdout.Writes);
        Assert.True(stdout.CanWrite);
        Assert.Equal(1, stderr.Attempts);
        if (!stderrFailure) Assert.Contains("機械出力を記録できません", stderr.ToString());
    }

    private sealed class FailingOutputStream(int failWrite = 0, bool failClose = false) : MemoryStream
    {
        public int Writes { get; private set; }
        public int Closes { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            if (Writes == failWrite) throw new IOException("injected write failure");
            base.Write(buffer);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Closes++;
            base.Dispose(disposing);
            if (disposing && failClose) throw new IOException("injected log close failure");
        }
    }

    private sealed class FailingErrorWriter(bool fail) : StringWriter
    {
        public int Attempts { get; private set; }
        public override void WriteLine(string? value)
        {
            Attempts++;
            if (fail) throw new IOException("injected stderr failure");
            base.WriteLine(value);
        }
    }

    private static JsonElement AssertResult(MachineCliRun run, string outcome, int exitCode, string? code = null, string? stage = null)
    {
        Assert.Equal(string.Empty, run.Stderr);
        var end = Assert.Single(run.Records, e => e.GetProperty("type").GetString() == "result");
        Assert.Equal(end, run.Records[^1]);
        Assert.Equal(outcome, end.GetProperty("outcome").GetString());
        Assert.Equal(exitCode, end.GetProperty("exit_code").GetInt32());
        Assert.Equal(exitCode, ExitCodes.ToProcessExitCode(run.Status));
        if (code is not null) Assert.Equal(code, end.GetProperty("error").GetProperty("code").GetString());
        if (stage is not null) Assert.Equal(stage, end.GetProperty("error").GetProperty("stage").GetString());
        return end;
    }

    private static void AssertNoEntriesTouched(MachineCliFixture f) => Assert.DoesNotContain(f.Fs.Calls, c =>
        c.StartsWith("OpenEnumeration ", StringComparison.Ordinal) || c.StartsWith("Enumerate ", StringComparison.Ordinal)
        || c.StartsWith("OpenComparison ", StringComparison.Ordinal) || c.StartsWith("OpenDeletion ", StringComparison.Ordinal)
        || c.StartsWith("CheckIdentity ", StringComparison.Ordinal) || c.StartsWith("Disposition ", StringComparison.Ordinal));

    private static void AssertLogReleased(string path)
    {
        using var released = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class FailingRootDisposeProbe(IFileSystemProbe inner) : IFileSystemProbe
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

internal sealed record MachineCliRun(ExitStatus Status, byte[] Bytes, string Stderr, JsonElement[] Records);

// P8/P9で共用するCLI結合fixture。実ZIPは既存の公開入口で開き、同じ内容の個体は偽FSに用意する。
// 実fixtureの場所はビルド出力の下 (CIでは D:\a\... など C: 以外もある) で、偽FSは C: だけを持つため、
// probe に渡る実fixtureのパスは偽FSの FakeDirectory へ読み替える (Probe)。
internal sealed class MachineCliFixture
{
    internal const string Target = @"C:\target";
    internal const string FakeDirectory = @"C:\in";
    private readonly string _directory;
    public FakeFileSystem Fs { get; } = new();
    public int LocationCalls { get; private set; }
    public ProtectedLocationsResult Locations { get; set; } = new(TargetLocationPolicy.None, null);
    public Exception? ResolveFailure { get; set; }

    public MachineCliFixture()
    {
        _directory = TestFixtures.Create();
        Fs.AddDirectory(Target);
        Fs.AddDirectory(FakeDirectory);
    }

    public string PathFor(string name) => Path.Combine(_directory, name);

    // 実fixture内のパスに対応する偽FS上のパス。実fixture外のパス (target など) はそのまま。
    public string FakePathFor(string path) =>
        path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? FakeDirectory + path[_directory.Length..]
            : path;

    // CLIへ渡すprobe。inner (既定は Fs) の前で実fixtureのパスを偽FS上のパスへ読み替える。
    public IFileSystemProbe Probe(IFileSystemProbe? inner = null) => new FakePathProbe(inner ?? Fs, FakePathFor);
    public FakeNode File(string name, string content) => Fs.AddFile(Path.Combine(Target, name), Encoding.UTF8.GetBytes(content));
    public string Entries(string content, string name = "entries.txt")
    {
        var path = PathFor(name);
        System.IO.File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }
    public string Zip(params (string Name, string? Content)[] entries)
    {
        var path = PathFor("archive.zip");
        using (var stream = new FileStream(path, FileMode.CreateNew))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                if (content is not null)
                {
                    using var body = entry.Open();
                    body.Write(Encoding.UTF8.GetBytes(content));
                }
            }
        }
        Fs.AddFile(FakePathFor(path), System.IO.File.ReadAllBytes(path));
        return path;
    }
    public string[] Args(string zip, bool delete, bool fast = false, string? log = null, string? entries = null)
    {
        List<string> args = [delete ? "delete" : "analyze", zip, "--target", Target, "--jsonl"];
        if (delete) args.Add("--yes");
        if (fast) args.Add("--fast");
        if (log is not null) args.AddRange(["--log", log]);
        if (entries is not null) args.AddRange(["--entries", entries]);
        return args.ToArray();
    }
    public MachineCliRun Run(string[] args, IFileSystemProbe? probe = null, Func<string, LogCreationResult>? createLog = null)
    {
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();
        var environment = new CliEnvironment(Probe(probe), Fs, () =>
        {
            LocationCalls++;
            if (ResolveFailure is { } failure) throw failure;
            return Locations;
        }, new UnreadablePrompt(), ShowProgress: true);
        var status = CliApplication.RunMachine(args, stdout, stderr, environment, createLog);
        Assert.True(stdout.CanWrite); // 借用stdoutはcloseしない。
        Assert.Equal(0, Fs.OpenHandleCount);
        var bytes = stdout.ToArray();
        Assert.NotEmpty(bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.All(bytes, b => Assert.InRange(b, (byte)0, (byte)127));
        Assert.DoesNotContain((byte)'\r', bytes);
        var records = Encoding.ASCII.GetString(bytes).Split('\n').SkipLast(1).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
            return json.RootElement.Clone();
        }).ToArray();
        return new(status, bytes, stderr.ToString(), records);
    }
    private sealed class UnreadablePrompt : IConfirmationPrompt
    {
        public bool IsInteractive => throw new InvalidOperationException("must not read IsInteractive");
        public string? Ask(string prompt) => throw new InvalidOperationException("must not read stdin");
    }

    private sealed class FakePathProbe(IFileSystemProbe inner, Func<string, string> map) : IFileSystemProbe
    {
        public ProbeResult<TargetConfirmation> ConfirmTargetFinalComponent(string path) => inner.ConfirmTargetFinalComponent(map(path));
        public ProbeResult<IDirectoryHandle> OpenTargetRoot(string path) => inner.OpenTargetRoot(map(path));
        public ProbeResult<IDirectoryHandle> OpenDirectoryForEnumeration(string path) => inner.OpenDirectoryForEnumeration(map(path));
        public ProbeResult<IComparisonHandle> OpenForComparison(string path) => inner.OpenForComparison(map(path));
        public ProbeResult<VolumeFileId> GetFileIdentity(string path) => inner.GetFileIdentity(map(path));
    }
}
