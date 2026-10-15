using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Unextract.Windows.Tests.Helper;
using Xunit.Abstractions;
using static Unextract.Windows.Tests.Integration.RealRun;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests.Integration;

// delete --prune-dirs のディレクトリ段階の実 NTFS テスト (docs/TESTING.md の D 系の Win。docs/spec/filesystem.md#prune-flow、docs/RATIONALE.md#prune-dirs の観測)。実際に削除する。
// - 削除してよいのは、各テストが自分で作った一意な fixture の中のファイルとディレクトリだけ。削除の指示の直前に毎回 DeletionGuard が
//   ディレクトリ削除用ハンドルの最終パスと親を確かめる (RealRun.Delete)。fixture のディレクトリ自体は削除しない。
// - junction は同じ fixture 内の別ディレクトリを指すものだけを作る。「別プロセス」は HelperProcess で行う。
public class PruneDirectoryIntegrationTests(ITestOutputHelper output)
{
    private const FileAttributes Hidden = FileAttributes.Hidden;

    private static readonly byte[] A = Bytes("alpha content");

    public static TheoryData<RunMode> BothModes => new() { RunMode.Strict, RunMode.Fast };

    private sealed record Fixture(string Dir, string Target, string Zip, DeletionGuard Guard)
    {
        public string Path(string relative) => System.IO.Path.Combine(Target, relative);
    }

    // fixture: dir/archive.zip、dir/target/。target に ZIP の内容どおりのファイル (ディレクトリエントリはディレクトリ) を置く。
    private static Fixture Create(params (string Name, byte[]? Content)[] entries)
    {
        var dir = CreateDirectory();
        var target = Directory.CreateDirectory(System.IO.Path.Combine(dir, "target")).FullName;
        foreach (var (entry, content) in entries)
        {
            var path = System.IO.Path.Combine(target, entry.Replace('/', '\\'));
            if (content is null)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }
        }

        var zip = WriteZip(System.IO.Path.Combine(dir, "archive.zip"), Zip(entries));
        return new Fixture(dir, target, zip, new DeletionGuard(dir));
    }

    private static IEnumerable<(string Path, DirectoryStatus Status, DirectoryKeepReason? Keep)> Outcomes(Result result) =>
        result.Report.Prune!.Results.Select(r => (r.Directory.Path, r.Status, r.KeepReason));

    private void Log(Result result)
    {
        output.WriteLine("--- stdout ---");
        output.WriteLine(result.Output);
        output.WriteLine("--- stderr ---");
        output.WriteLine(result.Error);
    }

    // D21 (Win): ファイルを削除した後に、空になったディレクトリを深い順に削除する。実行前から空のもの・HIDDEN も削除し、
    // ZIP にないファイルが残るものは空でない、READONLY は属性として残す。終了は成功。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void D21_DeletesEmptiedDirectories(RunMode mode)
    {
        var f = Create(("a/b/f.txt", A), ("a/g.txt", A), ("keep/k.txt", A), ("empty/", null), ("ro/", null), ("hid/", null));
        File.WriteAllBytes(f.Path(@"keep\other.txt"), A);
        File.SetAttributes(f.Path("ro"), FileAttributes.Directory | FileAttributes.ReadOnly);
        File.SetAttributes(f.Path("hid"), FileAttributes.Directory | Hidden);

        var result = Delete(f.Zip, f.Target, f.Guard, mode, pruneDirs: true);
        Log(result);

        Assert.Equal(ExitStatus.Success, result.Status);
        Assert.Equal(
            [
                (@"a\b", DirectoryStatus.Deleted, null),
                ("a", DirectoryStatus.Deleted, null),
                ("keep", DirectoryStatus.Kept, (DirectoryKeepReason?)DirectoryKeepReason.NotEmpty),
                ("empty", DirectoryStatus.Deleted, null),
                ("ro", DirectoryStatus.Kept, DirectoryKeepReason.Attributes),
                ("hid", DirectoryStatus.Deleted, null),
            ],
            Outcomes(result));
        Assert.False(Directory.Exists(f.Path("a")));
        Assert.False(Directory.Exists(f.Path("empty")));
        Assert.False(Directory.Exists(f.Path("hid")));
        Assert.True(File.Exists(f.Path(@"keep\other.txt")));
        Assert.True(Directory.Exists(f.Path("ro")));
        Assert.True(File.Exists(f.Zip));
        Assert.Contains(@"DIR_DELETED", result.Output, StringComparison.Ordinal);
    }

    // D22 (Win): junction (親・最終成分) はたどらずに残し、ADS を持つディレクトリも残す。junction の先には触れない。
    [Fact]
    public void D22_ReparseAndAdsAreKept()
    {
        var f = Create(("ads/", null));
        var elsewhere = Directory.CreateDirectory(System.IO.Path.Combine(f.Dir, "elsewhere")).FullName;
        Directory.CreateDirectory(System.IO.Path.Combine(elsewhere, "sub"));
        CreateJunction(f.Path("j"), elsewhere);
        File.WriteAllText(f.Path("ads") + ":Zone.Identifier", "[ZoneTransfer]");
        var zip = WriteZip(System.IO.Path.Combine(f.Dir, "archive2.zip"), Zip(("j/sub/", null), ("ads/", null)));

        var result = Delete(zip, f.Target, f.Guard, pruneDirs: true);
        Log(result);

        Assert.Equal(ExitStatus.Success, result.Status);
        Assert.Equal(
            [
                (@"j\sub", DirectoryStatus.Kept, (DirectoryKeepReason?)DirectoryKeepReason.ParentReparsePoint),
                ("j", DirectoryStatus.Kept, DirectoryKeepReason.ReparsePoint),
                ("ads", DirectoryStatus.Kept, DirectoryKeepReason.AlternateDataStream),
            ],
            Outcomes(result));
        Assert.True(Directory.Exists(System.IO.Path.Combine(elsewhere, "sub")));
        Assert.True(Directory.Exists(f.Path("ads")));
    }

    // D23 (Win): 別プロセスの作業フォルダーは FILE_SHARE_DELETE を許さないので削除用オープンが 32 になり、識別確認が一致して
    // DIR_KEPT (使用中) で続行する。終了状態は変えない。
    [Fact]
    public void D23_OtherProcessWorkingDirectoryIsKeptAsInUse()
    {
        var f = Create(("w/", null), ("v/", null));
        Result result;
        using (HelperProcess.Hold("hold-cwd", f.Path("w")))
        {
            result = Delete(f.Zip, f.Target, f.Guard, pruneDirs: true);
        }

        Log(result);
        Assert.Equal(ExitStatus.Success, result.Status);
        Assert.Equal(
            [("w", DirectoryStatus.Kept, (DirectoryKeepReason?)DirectoryKeepReason.InUse), ("v", DirectoryStatus.Deleted, null)],
            Outcomes(result));
        Assert.True(Directory.Exists(f.Path("w")));
        Assert.Contains(@"(使用中)", result.Output, StringComparison.Ordinal);
    }

    // D24 (Win): FILE_SHARE_DELETE を許すハンドル (変更通知の監視) とは共存し、こちらのハンドルを閉じた時点で名前が消える。
    [Fact]
    public void D24_ShareDeleteWatcherDoesNotPreventDeletion()
    {
        var f = Create(("d/", null));
        using var watcher = new FileSystemWatcher(f.Path("d")) { EnableRaisingEvents = true };

        var result = Delete(f.Zip, f.Target, f.Guard, pruneDirs: true);
        Log(result);

        Assert.Equal([("d", DirectoryStatus.Deleted, (DirectoryKeepReason?)null)], Outcomes(result));
        Assert.False(Directory.Exists(f.Path("d")));
    }

    // D25 (Win): 空の確認の後 (削除の指示の直前) に作られた項目は、NTFS が 145 で拒否する。DeletePending は false のままで、
    // 空でないとして残し、作られたファイルは残る。
    [Fact]
    public void D25_ItemCreatedAfterEmptyCheckIsRejectedByNtfs()
    {
        var f = Create(("p/", null));
        var hooks = new DeleteHooks
        {
            BeforeDirectoryDisposition = (path, _) => File.WriteAllBytes(f.Path(@"p\late.txt"), A),
        };

        var result = Delete(f.Zip, f.Target, f.Guard, hooks: hooks, pruneDirs: true);
        Log(result);

        Assert.Equal(ExitStatus.Success, result.Status);
        Assert.Equal([("p", DirectoryStatus.Kept, (DirectoryKeepReason?)DirectoryKeepReason.NotEmpty)], Outcomes(result));
        Assert.True(File.Exists(f.Path(@"p\late.txt")));
    }

    // D26 (Win): ディレクトリ削除用ハンドルを開いている間、別プロセスはそのディレクトリを改名できない (DELETE を共有しない)。
    [Fact]
    public void D26_RenameIsBlockedWhileTheDirectoryHandleIsOpen()
    {
        var f = Create(("r/", null));
        var code = -1;
        var hooks = new DeleteHooks
        {
            BeforeDirectoryDisposition = (path, _) => code = HelperProcess.Try("try-rename-dir", f.Path("r")),
        };

        var result = Delete(f.Zip, f.Target, f.Guard, hooks: hooks, pruneDirs: true);
        Log(result);
        output.WriteLine($"rename while open: Win32 {code}");

        Assert.NotEqual(0, code);
        Assert.False(Directory.Exists(f.Path("r") + ".renamed"));
        Assert.Equal([("r", DirectoryStatus.Deleted, (DirectoryKeepReason?)null)], Outcomes(result));
    }

    // D27 (Win): 他者が開いたままのファイルも、ファイル段階で POSIX semantics で削除されれば名前が消え、親は空として削除できる。
    [Fact]
    public void D27_ParentOfPosixDeletedFileHeldByAnotherHandleIsDeleted()
    {
        var f = Create(("d/f.txt", A));
        Result result;
        using (new FileStream(f.Path(@"d\f.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            result = Delete(f.Zip, f.Target, f.Guard, pruneDirs: true);
        }

        Log(result);
        Assert.Equal(DeleteStatus.Deleted, result.ResultOf("d/f.txt").Status);
        Assert.Equal([("d", DirectoryStatus.Deleted, (DirectoryKeepReason?)null)], Outcomes(result));
        Assert.False(Directory.Exists(f.Path("d")));
    }

    // D28: ディレクトリ削除用ハンドルの構成は docs/spec/filesystem.md#handles の表の行と完全に一致する。
    [Fact]
    public void D28_DirectoryDeletionHandleSpecMatchesTable()
    {
        Assert.Equal(
            new HandleSpec(0x00010000 | 0x0001 | 0x0080 | 0x00100000, 0x1 | 0x2, 0x02000000 | 0x00200000 | 0x00100000),
            HandleSpecs.DirectoryDeletion);
    }

    // D29 (Win): --prune-dirs なしでは、空になったディレクトリも削除せず、ディレクトリ削除用に開かない。
    [Fact]
    public void D29_WithoutPruneDirsDirectoriesRemain()
    {
        var f = Create(("d/f.txt", A), ("e/", null));

        var result = Delete(f.Zip, f.Target, f.Guard);
        Log(result);

        Assert.Equal(ExitStatus.Success, result.Status);
        Assert.Null(result.Report.Prune);
        Assert.True(Directory.Exists(f.Path("d")));
        Assert.True(Directory.Exists(f.Path("e")));
        Assert.Empty(result.Probe!.DirectoriesOpened);
    }
}
