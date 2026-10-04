using Unextract.Core.Analysis;
using Unextract.Core.Commands;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Unextract.Core.Tests.Fakes;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;
using static Unextract.Core.Tests.Fakes.PipelineHarness;

namespace Unextract.Core.Tests;

// J07: 同期通知は処理済みの事実を渡す。配送成功・失敗にかかわらず、そのハンドルは閉じている。
public class CommandNotificationTests
{
    [Theory]
    [InlineData(false, RunMode.Strict)]
    [InlineData(false, RunMode.Fast)]
    [InlineData(true, RunMode.Strict)]
    [InlineData(true, RunMode.Fast)]
    public void J07_PreparedThenCloseThenNotificationThenNextOpen(bool delete, RunMode mode)
    {
        var h = Create(mode, MakeZip(("a.txt", Bytes("hello")), ("b.txt", Bytes("hello"))), out var stream);
        h.File("a.txt");
        h.File("b.txt");
        PreparedCommandInfo? info = null;
        var notified = new List<ZipEntryRef>();
        void Notify(ZipEntryRef entry, long length)
        {
            Assert.NotNull(info);
            Assert.Equal(5, length);
            Assert.Equal(1, h.Fs.OpenHandleCount); // 保持中の root だけ。
            Assert.True(stream.CanRead); // ZIP は実行終了まで保持。
            Assert.Equal(entry.Number, delete ? h.Fs.DeletionCloseCount : h.Fs.ComparisonCloseCount);
            notified.Add(entry);
            h.Fs.Calls.Add($"Notify {entry.Number}");
        }
        h.Notifications = new CommandNotifications(
            OnPrepared: prepared =>
            {
                Assert.False(h.TouchedTargetEntries());
                Assert.Null(h.Contents);
                Assert.Equal(1, h.Fs.OpenHandleCount);
                Assert.True(stream.CanRead);
                info = prepared;
                h.Fs.Calls.Add("Prepared");
            },
            OnAnalysisResult: result => Notify(result.Entry, result.Length),
            OnDeleteResult: result => Notify(result.Entry, result.Length));

        RunSilently(h, delete);

        Assert.Equal(new PreparedCommandInfo(CommandHarness.ArchivePath, @"\\?\C:\target", mode, 2, 2, false), info);
        Assert.Equal(new[] { 1, 2 }, notified.Select(entry => entry.Number));
        var kind = delete ? "deletion" : "comparison";
        var open = delete ? "OpenDeletion" : "OpenComparison";
        AssertOrdered(h.Fs.Calls, "Prepared", $@"{open} \\?\C:\target\a.txt", $@"Close {kind} \\?\C:\target\a.txt",
            "Notify 1", $@"{open} \\?\C:\target\b.txt", $@"Close {kind} \\?\C:\target\b.txt", "Notify 2", @"Close root \\?\C:\target");
        AssertClosed(h, stream);
        if (mode == RunMode.Fast)
        {
            Assert.Empty(h.Contents!.Calls);
            Assert.DoesNotContain(h.Fs.Calls, call => call.StartsWith("Read ", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void J07_AnalyzeNotifiesDirectoryAndRawNamesInZipOrder(RunMode mode)
    {
        const string rawName = "日本語\u202E\U0001F600.txt";
        var h = Create(mode, MakeZip(("d/", null), (rawName, Bytes("hello")), ("missing", Bytes("hi")),
            ("modified", Bytes("x")), ("target-dir", Bytes("content")), ("empty", [])), out var stream);
        h.File(rawName);
        h.File("modified", Bytes("different size"));
        h.Fs.AddDirectory(@"C:\target\target-dir");
        h.File("empty", []);
        var notified = new List<EntryResult>();
        h.Notifications = new CommandNotifications(OnAnalysisResult: result =>
        {
            Assert.Equal(1, h.Fs.OpenHandleCount);
            notified.Add(result);
        });

        var run = h.Analyze();

        Assert.Equal(new[] { Classification.Directory, Candidate(mode), Classification.Missing, Classification.Modified,
            Classification.SkippedSpecialFile, Candidate(mode) }, notified.Select(result => result.Classification));
        Assert.Equal(new long[] { 0, 5, 2, 1, 7, 0 }, notified.Select(result => result.Length));
        Assert.Equal(Enumerable.Range(1, 6), notified.Select(result => result.Entry.Number));
        Assert.Equal(rawName, notified[1].Entry.Name);
        Assert.Equal(SkipReason.Directory, notified[4].SkipReason);
        Assert.Equal(run.Outcome.Analysis!.Results, notified);
        for (var i = 0; i < notified.Count; i++)
        {
            Assert.Same(run.Outcome.Analysis.Results[i], notified[i]);
        }
        AssertSilent(run.Output, run.Error, h);
        AssertClosed(h, stream);
    }

    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void J07_AnalyzeFatalCauseIsNotNotified(RunMode mode)
    {
        var h = Create(mode, MakeZip(("d/", null), ("a.txt", Bytes("hello")), ("b.txt", Bytes("hello")), ("c.txt", Bytes("hello"))), out var stream);
        h.File("a.txt");
        h.File("b.txt").Errors[FakeOp.Basic] = 1117;
        h.File("c.txt");
        var notified = new List<EntryResult>();
        h.Notifications = new CommandNotifications(OnAnalysisResult: notified.Add);

        var run = h.Analyze();

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.Equal(new[] { 1, 2 }, notified.Select(result => result.Entry.Number));
        Assert.Equal(3, run.Outcome.Analysis!.Fatal!.Entry!.Number);
        Assert.Equal(1, run.Outcome.Analysis.UnclassifiedCount);
        Assert.Equal(run.Outcome.Analysis.Results, notified);
        Assert.DoesNotContain(h.Fs.Calls, call => call.EndsWith("c.txt", StringComparison.Ordinal));
        AssertSilent(run.Output, run.Error, h);
        AssertClosed(h, stream);
    }

    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void J07_DeleteDirectoriesAreCountsAndStopIsNotified(RunMode mode)
    {
        var h = Create(mode, MakeZip(("d1/", null), ("a.txt", Bytes("hello")), ("d2/", null),
            ("b.txt", Bytes("hello")), ("d3/", null), ("c.txt", Bytes("hello"))), out var stream);
        h.File("a.txt");
        h.File("b.txt").DispositionHasNoEffect = true;
        h.File("c.txt");
        var notified = new List<DeleteEntryResult>();
        var directories = new List<int>();
        h.Notifications = new CommandNotifications(OnDeleteResult: result =>
        {
            Assert.Equal(1, h.Fs.OpenHandleCount);
            notified.Add(result);
        }, OnDirectoryCount: count =>
        {
            Assert.Equal(1, h.Fs.OpenHandleCount);
            directories.Add(count);
        });

        var run = h.Delete(prompt: new UnreadablePrompt());

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.Equal(new[] { 2, 4 }, notified.Select(result => result.Entry.Number));
        Assert.All(notified, result => Assert.Equal(5, result.Length));
        Assert.Equal(new[] { 1, 2 }, directories);
        Assert.Equal(2, run.Outcome.Report!.DirectoryCount);
        Assert.Equal(2, run.Outcome.Report.NotProcessedCount);
        Assert.Same(notified[^1], run.Outcome.Report.Stop);
        Assert.True(notified[^1].PossiblyDeleted);
        Assert.DoesNotContain(h.Fs.Calls, call => call.EndsWith("c.txt", StringComparison.Ordinal));
        AssertSilent(run.Output, run.Error, h);
        AssertClosed(h, stream);
    }

    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void J07_DeleteSelectionKeepsZipNumbersAndDoesNotNotifyUnselected(RunMode mode)
    {
        var h = Create(mode, MakeZip(("d/", null), ("a.txt", Bytes("hello")), ("unused/x.txt", Bytes("hello")),
            ("b.txt", Bytes("hello"))), out var stream);
        h.File("a.txt").Errors[FakeOp.OpenDeletion] = 32;
        h.File("b.txt");
        // 処理対象外の親へ到達すれば失敗する。
        h.Fs.AddDirectory(@"C:\target\unused").Errors[FakeOp.OpenEnumeration] = 1117;
        var notified = new List<DeleteEntryResult>();
        PreparedCommandInfo? info = null;
        h.Notifications = new CommandNotifications(OnPrepared: value => info = value, OnDeleteResult: notified.Add,
            OnDirectoryCount: _ => throw new InvalidOperationException("unselected directory"));

        var run = h.Delete(entriesPath: CommandHarness.WriteEntries("b.txt\na.txt\n"), prompt: new UnreadablePrompt());

        Assert.Equal(new PreparedCommandInfo(CommandHarness.ArchivePath, @"\\?\C:\target", mode, 4, 2, true), info);
        Assert.Equal(new[] { 2, 4 }, notified.Select(result => result.Entry.Number));
        Assert.Equal(new[] { DeleteStatus.DeleteFailed, DeleteStatus.Deleted }, notified.Select(result => result.Status));
        Assert.Equal(0, run.Outcome.Report!.DirectoryCount);
        Assert.Equal(0, run.Outcome.Report.NotProcessedCount);
        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.Equal(run.Outcome.Report.Results, notified);
        Assert.DoesNotContain(h.Fs.Calls, call => call.Contains("unused", StringComparison.Ordinal));
        AssertSilent(run.Output, run.Error, h);
        AssertClosed(h, stream);
    }

    [Theory]
    [InlineData(false, RunMode.Strict)]
    [InlineData(false, RunMode.Fast)]
    [InlineData(true, RunMode.Strict)]
    [InlineData(true, RunMode.Fast)]
    public void J07_PreparedNotificationFailurePreventsProcessing(bool delete, RunMode mode)
    {
        var h = Create(mode, MakeZip(("a.txt", Bytes("hello"))), out var stream);
        h.File("a.txt");
        var failure = new IOException("injected prepared notification failure");
        var started = false;
        h.Notifications = new CommandNotifications(OnPrepared: _ => throw failure);

        var thrown = Assert.Throws<IOException>(() =>
        {
            if (delete) h.Delete(deletionStarting: () => started = true);
            else h.Analyze();
        });

        Assert.Same(failure, thrown);
        Assert.False(started);
        Assert.False(h.TouchedTargetEntries());
        Assert.Empty(h.Fs.Deleted);
        Assert.Null(h.Contents);
        AssertClosed(h, stream);
    }

    [Theory]
    [InlineData(false, RunMode.Strict)]
    [InlineData(false, RunMode.Fast)]
    [InlineData(true, RunMode.Strict)]
    [InlineData(true, RunMode.Fast)]
    public void J07_EntryNotificationFailureCountsProcessedEntryAndStopsBeforeNextParent(bool delete, RunMode mode)
    {
        var zip = MakeZip(("d1/", null), ("a.txt", Bytes("hello")), ("d2/", null),
            ("b.txt", Bytes("hello")), ("d3/", null), ("next/c.txt", Bytes("hello")));
        var h = Create(mode, zip, out var stream);
        h.File("a.txt");
        h.File("b.txt");
        h.Fs.AddDirectory(@"C:\target\next");
        h.File(@"next\c.txt");
        var failure = new IOException("injected entry notification failure");
        var processed = new List<ZipEntryRef>();
        var directoryCount = 0;
        var started = false;
        PreparedCommandInfo? info = null;
        void Notify(ZipEntryRef entry)
        {
            Assert.Equal(1, h.Fs.OpenHandleCount);
            processed.Add(entry); // 配送の成否より先に処理済みの事実を保存。
            if (entry.Name == "b.txt") throw failure;
        }
        h.Notifications = new CommandNotifications(OnPrepared: value => info = value,
            OnAnalysisResult: result => Notify(result.Entry), OnDeleteResult: result => Notify(result.Entry),
            OnDirectoryCount: count => directoryCount = count);

        var thrown = Assert.Throws<IOException>(() =>
        {
            if (delete) h.Delete(deletionStarting: () => started = true);
            else h.Analyze();
        });

        Assert.Same(failure, thrown); // DeleteCommand の人間向け catch に吸収されない。
        Assert.Equal(delete, started);
        Assert.Equal(delete ? new[] { 2, 4 } : new[] { 1, 2, 3, 4 }, processed.Select(entry => entry.Number));
        Assert.Equal(delete ? 2 : 0, directoryCount);
        Assert.Equal(2, info!.SelectedEntries - processed.Count - directoryCount);
        Assert.Equal(delete ? 2 : 0, h.Fs.Deleted.Count);
        Assert.True(h.Exists(@"next\c.txt"));
        Assert.DoesNotContain(h.Fs.Calls, call => call.Contains(@"\next", StringComparison.Ordinal));
        Assert.False(h.Contents!.Touched(5));
        AssertClosed(h, stream);
    }

    [Theory]
    [InlineData(RunMode.Strict, false)]
    [InlineData(RunMode.Fast, false)]
    [InlineData(RunMode.Strict, true)]
    [InlineData(RunMode.Fast, true)]
    public void J07_StopNotificationFailureKeepsStopFacts(RunMode mode, bool possiblyDeleted)
    {
        var h = Create(mode, MakeZip(("a.txt", Bytes("hello")), ("next/b.txt", Bytes("hello"))), out var stream);
        var a = h.File("a.txt");
        if (possiblyDeleted) a.ThrowAfterDisposition = true;
        else a.Errors[FakeOp.Streams] = 1117;
        h.Fs.AddDirectory(@"C:\target\next");
        h.File(@"next\b.txt");
        DeleteEntryResult? stop = null;
        var failure = new IOException("injected stop notification failure");
        var started = false;
        h.Notifications = new CommandNotifications(OnDeleteResult: result =>
        {
            stop = result;
            Assert.Equal(1, h.Fs.OpenHandleCount);
            throw failure;
        });

        Assert.Same(failure, Assert.Throws<IOException>(() => h.Delete(deletionStarting: () => started = true)));

        Assert.True(started);
        Assert.Equal(DeleteStatus.Stopped, stop!.Status);
        Assert.Equal(possiblyDeleted, stop.PossiblyDeleted);
        Assert.Equal(5, stop.Length);
        Assert.NotNull(stop.Failure);
        Assert.DoesNotContain(h.Fs.Calls, call => call.Contains(@"\next", StringComparison.Ordinal));
        AssertClosed(h, stream);
    }

    [Theory]
    [InlineData(false, RunMode.Strict)]
    [InlineData(false, RunMode.Fast)]
    [InlineData(true, RunMode.Strict)]
    [InlineData(true, RunMode.Fast)]
    public void J07_DirectoryNotificationFailureStopsBeforeFirstFile(bool delete, RunMode mode)
    {
        var h = Create(mode, MakeZip(("d/", null), ("next/a.txt", Bytes("hello"))), out var stream);
        h.Fs.AddDirectory(@"C:\target\next");
        h.File(@"next\a.txt");
        var failure = new IOException("injected directory notification failure");
        h.Notifications = new CommandNotifications(OnAnalysisResult: result =>
        {
            Assert.Equal(Classification.Directory, result.Classification);
            Assert.Equal(0, result.Length);
            throw failure;
        }, OnDirectoryCount: count =>
        {
            Assert.Equal(1, count);
            throw failure;
        });

        Assert.Same(failure, Assert.Throws<IOException>(() => RunSilently(h, delete)));

        Assert.False(h.TouchedTargetEntries());
        Assert.Empty(h.Contents!.Calls);
        AssertClosed(h, stream);
    }

    [Theory]
    [InlineData(false, RunMode.Strict)]
    [InlineData(false, RunMode.Fast)]
    [InlineData(true, RunMode.Strict)]
    [InlineData(true, RunMode.Fast)]
    public void J07_EmptyAndAllDirectoryRunsCompleteWithoutTargetEntries(bool delete, RunMode mode)
    {
        foreach (var count in new[] { 0, 2 })
        {
            var zip = count == 0 ? MakeZip() : MakeZip(("a/", null), ("b/", null));
            var h = Create(mode, zip, out var stream);
            var preparedCount = 0;
            var analyzed = 0;
            var directories = 0;
            var started = 0;
            h.Notifications = new CommandNotifications(OnPrepared: info =>
            {
                preparedCount++;
                Assert.Equal(count, info.TotalEntries);
                Assert.Equal(count, info.SelectedEntries);
            }, OnAnalysisResult: _ => analyzed++, OnDeleteResult: _ => throw new InvalidOperationException("file notification"),
                OnDirectoryCount: value => directories = value);

            if (delete)
            {
                var run = h.Delete(prompt: new UnreadablePrompt(), deletionStarting: () => started++);
                Assert.Equal(ExitStatus.Success, run.Outcome.Status);
                Assert.Equal(count, run.Outcome.Report!.DirectoryCount);
                Assert.Equal(0, run.Outcome.Report.NotProcessedCount);
                AssertSilent(run.Output, run.Error, h);
            }
            else
            {
                var run = h.Analyze();
                Assert.Equal(ExitStatus.Success, run.Outcome.Status);
                Assert.Equal(count, run.Outcome.Analysis!.Results.Count);
                AssertSilent(run.Output, run.Error, h);
            }

            Assert.Equal(1, preparedCount);
            Assert.Equal(delete ? 0 : count, analyzed);
            Assert.Equal(delete ? count : 0, directories);
            Assert.Equal(delete ? 1 : 0, started);
            Assert.False(h.TouchedTargetEntries());
            Assert.Empty(h.Contents!.Calls);
            AssertClosed(h, stream);
        }
    }

    [Theory]
    [InlineData(false, RunMode.Strict)]
    [InlineData(false, RunMode.Fast)]
    [InlineData(true, RunMode.Strict)]
    [InlineData(true, RunMode.Fast)]
    public void J07_PrepareFailuresDoNotNotify(bool delete, RunMode mode)
    {
        foreach (var invalidZip in new[] { false, true })
        {
            var h = Create(mode, MakeZip((invalidZip ? "../a.txt" : "a.txt", Bytes("hello"))), out var stream);
            if (!invalidZip) h.Fs.Get(TargetPath).Errors[FakeOp.OpenTargetRoot] = 5;
            h.Notifications = new CommandNotifications(OnPrepared: _ => throw new InvalidOperationException("prepared"),
                OnAnalysisResult: _ => throw new InvalidOperationException("analysis"),
                OnDeleteResult: _ => throw new InvalidOperationException("delete"));

            if (delete)
            {
                var run = h.Delete(prompt: new UnreadablePrompt());
                Assert.Equal(ExitStatus.Error, run.Outcome.Status);
                Assert.NotNull(run.Outcome.PreparationFailure);
                AssertSilent(run.Output, run.Error, h);
            }
            else
            {
                var run = h.Analyze();
                Assert.Equal(ExitStatus.Error, run.Outcome.Status);
                Assert.NotNull(run.Outcome.PreparationFailure);
                Assert.Equal(invalidZip, run.Outcome.Analysis is not null);
                AssertSilent(run.Output, run.Error, h);
            }
            Assert.False(h.TouchedTargetEntries());
            AssertClosed(h, stream);
        }
    }

    [Theory]
    [InlineData(false, RunMode.Strict)]
    [InlineData(false, RunMode.Fast)]
    [InlineData(true, RunMode.Strict)]
    [InlineData(true, RunMode.Fast)]
    public void J07_LengthIs64BitDeclarationWithoutAdditionalIo(bool delete, RunMode mode)
    {
        const long length = 5_000_000_000;
        var zip = new ZipPatcher(MakeZip(("a.txt", Bytes("hello")))).SetDeclaredLength(0, length).ToArray();
        var baseline = new CommandHarness(zip) { Mode = mode };
        baseline.File("a.txt", Bytes("different size"));
        if (delete) baseline.Delete();
        else baseline.Analyze();
        var h = Create(mode, zip, out var stream);
        h.File("a.txt", Bytes("different size"));
        var notifications = 0;
        h.Notifications = new CommandNotifications(OnAnalysisResult: result =>
        {
            notifications++;
            Assert.Equal(Classification.Modified, result.Classification);
            Assert.Equal(length, result.Length);
        }, OnDeleteResult: result =>
        {
            notifications++;
            Assert.Equal(DeleteStatus.Modified, result.Status);
            Assert.Equal(length, result.Length);
        });

        RunSilently(h, delete);

        Assert.Equal(1, notifications);
        Assert.Equal(baseline.Fs.Calls, h.Fs.Calls);
        Assert.Equal(baseline.Contents!.Calls, h.Contents!.Calls);
        Assert.Empty(h.Contents.Calls);
        AssertClosed(h, stream);
    }

    [Theory]
    [InlineData(false, RunMode.Strict)]
    [InlineData(false, RunMode.Fast)]
    [InlineData(true, RunMode.Strict)]
    [InlineData(true, RunMode.Fast)]
    public void J07_FinalNotificationAndPreparedDisposeFailuresPropagate(bool delete, RunMode mode)
    {
        foreach (var disposeFailure in new[] { false, true })
        {
            var zip = MakeZip(("a.txt", Bytes("hello")));
            var failure = new IOException("injected final failure");
            var stream = new FailingDisposeStream(zip, disposeFailure ? failure : null);
            var h = new CommandHarness(zip) { Mode = mode, OpenArchive = _ => ZipArchiveSource.Open(stream) };
            h.File("a.txt");
            var notified = 0;
            void Notify()
            {
                Assert.Equal(1, h.Fs.OpenHandleCount);
                notified++;
                if (!disposeFailure) throw failure;
            }
            h.Notifications = new CommandNotifications(OnAnalysisResult: _ => Notify(), OnDeleteResult: _ => Notify());

            Assert.Same(failure, Assert.Throws<IOException>(() => RunSilently(h, delete)));

            Assert.Equal(1, notified);
            Assert.Equal(delete ? 1 : 0, h.Fs.Deleted.Count);
            Assert.Equal(0, h.Fs.OpenHandleCount);
            Assert.False(stream.CanRead);
        }
    }

    [Fact]
    public void J07_NotificationDeleteDoesNotInferConsent()
    {
        var h = new CommandHarness(MakeZip(("a.txt", Bytes("hello")))) { Notifications = new CommandNotifications() };
        h.File("a.txt");
        Assert.Throws<ArgumentException>(() => h.Delete(yes: false, prompt: new UnreadablePrompt()));
        Assert.Empty(h.Fs.Calls);
        Assert.Empty(h.Fs.Deleted);
    }

    private static CommandHarness Create(RunMode mode, byte[] zip, out MemoryStream stream)
    {
        var input = new MemoryStream(zip);
        stream = input;
        return new CommandHarness(zip) { Mode = mode, OpenArchive = _ => ZipArchiveSource.Open(input) };
    }

    private static void RunSilently(CommandHarness h, bool delete)
    {
        if (delete)
        {
            var run = h.Delete(prompt: new UnreadablePrompt());
            Assert.Equal(ExitStatus.Success, run.Outcome.Status);
            AssertSilent(run.Output, run.Error, h);
        }
        else
        {
            var run = h.Analyze();
            Assert.Equal(ExitStatus.Success, run.Outcome.Status);
            AssertSilent(run.Output, run.Error, h);
        }
    }

    private static void AssertSilent(string output, string error, CommandHarness h)
    {
        Assert.Empty(output);
        Assert.Empty(error);
        Assert.Empty(h.Progress);
    }

    private static void AssertClosed(CommandHarness h, MemoryStream stream)
    {
        Assert.False(stream.CanRead);
        Assert.Equal(0, h.Fs.OpenHandleCount);
        Assert.Equal(h.Fs.ComparisonOpenCount, h.Fs.ComparisonCloseCount);
        Assert.Equal(h.Fs.DeletionOpenCount, h.Fs.DeletionCloseCount);
    }

    private static void AssertOrdered(List<string> calls, params string[] expected)
    {
        var previous = -1;
        foreach (var call in expected)
        {
            var index = calls.IndexOf(call);
            Assert.True(index > previous, $"expected after index {previous}: {call}\n{string.Join('\n', calls)}");
            previous = index;
        }
    }

    private sealed class UnreadablePrompt : IConfirmationPrompt
    {
        public bool IsInteractive => throw new InvalidOperationException("prompt IsInteractive must not be read");

        public string? Ask(string prompt) => throw new InvalidOperationException("stdin must not be read");
    }

    private sealed class FailingDisposeStream(byte[] bytes, Exception? failure) : MemoryStream(bytes)
    {
        private bool _disposed;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && !_disposed)
            {
                _disposed = true;
                if (failure is not null) throw failure;
            }
        }
    }
}
