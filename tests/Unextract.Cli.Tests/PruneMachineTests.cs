using System.Text.Json;
using Unextract.Core.Analysis;
using Unextract.Core.CommandLine;
using Unextract.Core.Commands;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Unextract.Core.Tests.Fakes;

namespace Unextract.Cli.Tests;

// D31〜D34: --prune-dirs の機械可読出力 (docs/spec/machine-output.md#records の run.prune_dirs・dirs_selected と dir、#result の counts と dir_path)。
// ZIP は実fixture、target は偽FS (MachineCliFixture)。
public class PruneMachineTests
{
    private static string[] PruneArgs(MachineCliFixture f, string zip) => [.. f.Args(zip, delete: true), "--prune-dirs"];

    private static string[] Names(JsonElement value) => value.EnumerateObject().Select(p => p.Name).ToArray();

    // D31: run に prune_dirs と dirs_selected、ファイルの entry の後にディレクトリの dir を処理の順に出し、result の counts にフォルダーの件数を加える。
    // DIR_KEPT・DIR_MISSING は完了・終了コード 0 のまま。
    [Fact]
    public void D31_RunDirRecordsAndCounts()
    {
        var f = new MachineCliFixture();
        f.Fs.AddDirectory(@"C:\target\a");
        f.Fs.AddDirectory(@"C:\target\a\b");
        f.File(@"a\b\f.txt", "hello");
        f.Fs.AddDirectory(@"C:\target\k");
        f.File(@"k\other.txt", "x");
        var zip = f.Zip(("a/b/f.txt", "hello"), ("k/", null), ("m/", null));

        var run = f.Run(PruneArgs(f, zip));

        Assert.Equal(ExitStatus.Success, run.Status);
        var start = run.Records[0];
        Assert.True(start.GetProperty("prune_dirs").GetBoolean());
        Assert.Equal(4, start.GetProperty("dirs_selected").GetInt32());
        Assert.Equal("entry", run.Records[1].GetProperty("type").GetString());
        var dirs = run.Records.Where(r => r.GetProperty("type").GetString() == "dir").ToArray();
        Assert.Equal(
            [(@"a\b", "DIR_DELETED", null), ("a", "DIR_DELETED", null), ("k", "DIR_KEPT", "NOT_EMPTY"), ("m", "DIR_MISSING", (string?)null)],
            dirs.Select(d => (d.GetProperty("path").GetString()!, d.GetProperty("status").GetString()!,
                d.TryGetProperty("keep_reason", out var keep) ? keep.GetString() : null)));
        Assert.Equal(["v", "type", "path", "status", "keep_reason"], Names(dirs[2]));
        Assert.Equal(["v", "type", "path", "status"], Names(dirs[0]));
        var result = run.Records[^1];
        Assert.Equal("completed", result.GetProperty("outcome").GetString());
        var counts = result.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("dir_deleted").GetInt32());
        Assert.Equal(1, counts.GetProperty("dir_kept").GetInt32());
        Assert.Equal(1, counts.GetProperty("dir_missing").GetInt32());
        Assert.Equal(0, counts.GetProperty("dir_unprocessed").GetInt32());
        Assert.Null(f.Fs.Find(@"C:\target\a"));
    }

    // D32: ディレクトリ段階の STOP は stopped・終了コード1で、dir の STOPPED に reason と possibly_deleted、result.error に dir_path を付ける。
    [Fact]
    public void D32_DirectoryStopRecord()
    {
        var f = new MachineCliFixture();
        f.Fs.AddDirectory(@"C:\target\a").Errors[FakeOp.Disposition] = 87;
        f.Fs.AddDirectory(@"C:\target\b");
        var zip = f.Zip(("a/", null), ("b/", null));

        var run = f.Run(PruneArgs(f, zip));

        Assert.Equal(ExitStatus.Error, run.Status);
        var stop = run.Records.Single(r => r.GetProperty("type").GetString() == "dir");
        Assert.Equal("STOPPED", stop.GetProperty("status").GetString());
        Assert.False(stop.GetProperty("possibly_deleted").GetBoolean());
        Assert.Equal("DISPOSITION_FAILED", stop.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("dispose", stop.GetProperty("reason").GetProperty("step").GetString());
        var result = run.Records[^1];
        Assert.Equal("stopped", result.GetProperty("outcome").GetString());
        Assert.Equal(1, result.GetProperty("exit_code").GetInt32());
        var error = result.GetProperty("error");
        Assert.Equal("entry", error.GetProperty("stage").GetString());
        Assert.Equal("a", error.GetProperty("dir_path").GetString());
        Assert.False(error.TryGetProperty("entry_index", out _));
        Assert.Equal(1, result.GetProperty("counts").GetProperty("dir_unprocessed").GetInt32());
        Assert.NotNull(f.Fs.Find(@"C:\target\b"));
    }

    // D33: --prune-dirs なしの delete は run.prune_dirs が false で dirs_selected・dir・counts の dir_* を出さない。
    [Fact]
    public void D33_WithoutPruneDirs()
    {
        var f = new MachineCliFixture();
        f.Fs.AddDirectory(@"C:\target\a");
        var zip = f.Zip(("a/", null));

        var run = f.Run(f.Args(zip, delete: true));

        Assert.Equal(ExitStatus.Success, run.Status);
        Assert.False(run.Records[0].GetProperty("prune_dirs").GetBoolean());
        Assert.False(run.Records[0].TryGetProperty("dirs_selected", out _));
        Assert.DoesNotContain(run.Records, r => r.GetProperty("type").GetString() == "dir");
        Assert.False(run.Records[^1].GetProperty("counts").TryGetProperty("dir_deleted", out _));
        Assert.NotNull(f.Fs.Find(@"C:\target\a"));
    }

    // D34: ディレクトリ段階の途中の内部エラーでは、処理済みのフォルダーを数え (配送に失敗した対象も処理済み)、残りを dir_unprocessed とする。
    // ディレクトリの STOPPED の配送失敗では dir_path と possibly_deleted を付ける。
    [Fact]
    public void D34_InternalErrorCountsDirectoryProgress()
    {
        var progress = new MachineDeleteProgress();
        progress.Prepared(new PreparedCommandInfo("a.zip", @"\\?\C:\target", RunMode.Strict, 2, 2, false, true, 3));
        var directory = new PruneDirectory(["a"]);
        progress.Observe(new DirectoryResult(directory, @"\\?\C:\target\a", DirectoryStatus.Deleted));
        progress.Observe(new DirectoryResult(new PruneDirectory(["b"]), @"\\?\C:\target\b", DirectoryStatus.Stopped, null, "x", true,
            new DeleteFailure(DeleteFailureKind.DeletionUnconfirmed, EntryStep.Confirm)));

        var record = MachineOutput.InternalError(CommandKind.Delete, new IOException("pipe"), deletionStarted: true, progress);

        Assert.Equal(1, record.Counts!.DirDeleted);
        Assert.Equal(0, record.Counts.DirKept);
        Assert.Equal(1, record.Counts.DirUnprocessed);
        Assert.Equal("b", record.Error!.DirPath);
        Assert.True(record.Error.PossiblyDeleted);
    }
}
