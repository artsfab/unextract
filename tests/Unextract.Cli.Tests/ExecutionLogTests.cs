using System.IO.Pipes;
using System.Text;
using Unextract.Windows;

namespace Unextract.Cli.Tests;

// 実ファイルの新規作成・共有を確認する。自作GUID fixtureは削除せず保存する。
public class ExecutionLogTests
{
    [Theory]
    [InlineData("existing.jsonl")]
    [InlineData("archive.zip")]
    [InlineData("entries.txt")]
    public void J10_ExistingFileIsNotOverwrittenOrTruncated(string fileName)
    {
        var path = Path.Combine(Fixture(), fileName);
        var original = Encoding.UTF8.GetBytes("existing 日本語\n");
        File.WriteAllBytes(path, original);
        var creation = ExecutionLog.Create(path);
        Assert.Null(creation.Log);
        Assert.Equal(LogCreationFailureKind.AlreadyExists, creation.Failure!.Kind);
        Assert.NotEmpty(creation.Failure.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("missing-parent")]
    [InlineData("directory")]
    [InlineData("invalid")]
    [InlineData("empty")]
    public void J10_UncreatablePathIsTypedWithoutCreatingParents(string scenario)
    {
        var directory = Fixture();
        var missing = Path.Combine(directory, "missing");
        var path = scenario switch
        {
            "missing-parent" => Path.Combine(missing, "log.jsonl"),
            "directory" => directory,
            "invalid" => Path.Combine(directory, "invalid\0.jsonl"),
            "empty" => string.Empty,
            _ => throw new ArgumentException(scenario),
        };
        var creation = ExecutionLog.Create(path);
        Assert.Null(creation.Log);
        Assert.Equal(LogCreationFailureKind.CreateFailed, creation.Failure!.Kind);
        Assert.NotEmpty(creation.Failure.Message);
        Assert.False(Directory.Exists(missing));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData(@"\\.\NUL")]
    [InlineData("CON")]
    public void J10_DeviceIsNotAcceptedAsLog(string path)
    {
        var creation = ExecutionLog.Create(path);
        Assert.Null(creation.Log);
        Assert.Equal(LogCreationFailureKind.CreateFailed, creation.Failure!.Kind);
        Assert.NotEmpty(creation.Failure.Message);
    }

    [Fact]
    public async Task J10_PipeIsNotAcceptedAsLogAndReceivesNothing()
    {
        var name = $"unextract-J10-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        var creation = ExecutionLog.Create($@"\\.\pipe\{name}");
        Assert.Null(creation.Log);
        Assert.Equal(LogCreationFailureKind.CreateFailed, creation.Failure!.Kind);
        await connected.WaitAsync(TimeSpan.FromSeconds(10));
        var buffer = new byte[16];
        Assert.Equal(0, await server.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void J10_LogIsReadableAtEveryRecordAndRefusesWriteAndDeleteUntilClose()
    {
        var path = Path.Combine(Fixture(), "log.jsonl");
        var creation = ExecutionLog.Create(path);
        Assert.Null(creation.Failure);
        var log = Assert.IsType<ExecutionLog>(creation.Log);
        var probe = new WindowsFileSystemProbe();
        using var stdout = new MemoryStream();
        try
        {
            var writer = new MachineOutputWriter(stdout, log);
            var run = new MachineRunRecord("delete", "strict", "archive.zip", "C:\\target", 1, 1, false);
            writer.WriteRun(run);
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Assert.Equal(MachineOutput.Serialize(run), ReadAvailable(reader));
            Assert.Throws<IOException>(() =>
            {
                using var denied = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            });
            var deletion = probe.OpenForDeletion(path);
            try
            {
                Assert.False(deletion.Succeeded);
                Assert.Equal(32, deletion.Error);
            }
            finally
            {
                if (deletion.Succeeded) deletion.Value.Dispose();
            }
            var entry = new MachineEntryRecord(1, "file", false, 0, "DELETED");
            writer.WriteEntry(entry);
            Assert.Equal(MachineOutput.Serialize(entry), ReadAvailable(reader));
            var result = new MachineResultRecord("completed", 0);
            Assert.True(writer.WriteResult(result));
            Assert.Equal(MachineOutput.Serialize(result), ReadAvailable(reader));
            reader.Position = 0;
            Assert.Equal(stdout.ToArray(), ReadAvailable(reader));
            Assert.Equal(LogCreationFailureKind.AlreadyExists, ExecutionLog.Create(path).Failure!.Kind);
        }
        finally
        {
            log.Dispose();
        }
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(stdout.Length, writer.Length);
        }
        var afterClose = probe.OpenForDeletion(path);
        try
        {
            Assert.True(afterClose.Succeeded);
        }
        finally
        {
            if (afterClose.Succeeded) afterClose.Value.Dispose();
        }
        Assert.Equal(stdout.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task J10_ConcurrentCreateNewHasOneOwnerAndDoesNotChangeWinnerContent()
    {
        var path = Path.Combine(Fixture(), "raced.jsonl");
        using var gate = new Barrier(2);
        LogCreationResult Create()
        {
            if (!gate.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("creation gate");
            return ExecutionLog.Create(path);
        }
        var results = await Task.WhenAll(Task.Run(Create), Task.Run(Create));
        try
        {
            var winner = Assert.Single(results, result => result.Log is not null).Log!;
            var loser = Assert.Single(results, result => result.Failure is not null);
            Assert.Equal(LogCreationFailureKind.AlreadyExists, loser.Failure!.Kind);
            using var stdout = new MemoryStream();
            var writer = new MachineOutputWriter(stdout, winner);
            Assert.True(writer.WriteResult(new MachineResultRecord("completed", 0)));
            var before = ReadShared(path);
            Assert.Equal(stdout.ToArray(), before);
            Assert.Equal(LogCreationFailureKind.AlreadyExists, ExecutionLog.Create(path).Failure!.Kind);
            Assert.Equal(before, ReadShared(path));
        }
        finally
        {
            foreach (var result in results) result.Log?.Dispose();
        }
        Assert.True(File.Exists(path));
    }

    private static byte[] ReadShared(string path)
    {
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadAvailable(reader);
    }

    private static byte[] ReadAvailable(Stream reader)
    {
        using var output = new MemoryStream();
        reader.CopyTo(output);
        return output.ToArray();
    }

    private static string Fixture() => Directory.CreateDirectory(Path.Combine(
        AppContext.BaseDirectory, "fixtures", $"ExecutionLog-{Guid.NewGuid():N}")).FullName;
}
