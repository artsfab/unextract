using Unextract.Core.Deletion;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests;

// 削除用ハンドルと識別確認の Windows 層 (SPEC §8.1、§8.3、§8.4)。実際に削除するのは fixture 内のファイルだけで、
// 削除の指示の前に必ず DeletionGuard で削除用ハンドルの最終パスと親を確かめる。
public class DeletionHandleTests
{
    // SPEC §8.1 の表の「削除用」の行と完全一致: GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE、FILE_SHARE_READ、
    // FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL (BACKUP_SEMANTICS なし)。識別確認は FILE_READ_ATTRIBUTES のみ。
    [Fact]
    public void HandleSpecs_MatchSpecTable()
    {
        Assert.Equal(new HandleSpec(0x80000000 | 0x00010000 | 0x0080 | 0x00100000, 0x1, 0x00200000 | 0x00100000), HandleSpecs.Deletion);
        Assert.Equal(0u, HandleSpecs.Deletion.Flags & HandleSpecs.FileFlagBackupSemantics);
        Assert.Equal(0x0080u, HandleSpecs.IdentityCheck.Access);
        Assert.Equal(0x02000000u | 0x00200000u | 0x00100000u, HandleSpecs.IdentityCheck.Flags);
        Assert.Equal(0x3u, DeletionPhase.DispositionFlags);
    }

    // 削除用ハンドルはディレクトリを開けない (5)。識別確認はディレクトリも開け、Directory = true を返す
    [Fact]
    public void Directory_CannotBeOpenedForDeletion()
    {
        var dir = CreateDirectory();
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName;
        var probe = new WindowsFileSystemProbe();

        var opened = probe.OpenForDeletion(sub);
        Assert.False(opened.Succeeded);
        Assert.Equal(5, opened.Error);

        var identity = probe.CheckIdentity(sub);
        Assert.True(identity.Succeeded, identity.Describe());
        Assert.True(identity.Value.IsDirectory);
        Assert.True(Directory.Exists(sub));
    }

    // 識別確認は比較用ハンドルと同じ File ID・親 File ID・最終パスを返す (FILE_READ_ATTRIBUTES だけのハンドルでも親 File ID が取れる)
    [Fact]
    public void CheckIdentity_MatchesComparisonHandle()
    {
        var dir = CreateDirectory();
        var file = WriteFile(dir, "a.txt", "hello");
        var probe = new WindowsFileSystemProbe();

        var identity = probe.CheckIdentity(file);
        using var comparison = probe.OpenForComparison(file).Value!;

        Assert.True(identity.Succeeded, identity.Describe());
        Assert.Equal(comparison.GetVolumeFileId().Value, identity.Value.Id);
        Assert.Equal(comparison.GetParentFileId().Value, identity.Value.ParentFileId);
        Assert.Equal(comparison.GetFinalPath().Value, identity.Value.FinalPath);
        Assert.False(identity.Value.IsDirectory);
        Assert.False(identity.Value.DeletePending);
    }

    // 0x3 以外の flags は API を呼ばずに失敗 (87) し、DeletePending は false のまま、ファイルは残る
    [Theory]
    [InlineData(0x1u)]
    [InlineData(0x13u)]
    [InlineData(0x0u)]
    public void SetDispositionEx_RejectsFlagsOtherThan0x3(uint flags)
    {
        var dir = CreateDirectory($"{nameof(SetDispositionEx_RejectsFlagsOtherThan0x3)}-{flags:X}");
        var file = WriteFile(dir, "a.txt", "hello");

        using (var handle = new WindowsFileSystemProbe().OpenForDeletion(file).Value!)
        {
            var result = handle.SetDispositionEx(flags);
            Assert.False(result.Succeeded);
            Assert.Equal(87, result.Error);
            Assert.False(handle.GetStandardInformation().Value.DeletePending);
        }

        Assert.True(File.Exists(file));
    }

    // 削除用ハンドルへの 0x3 の指示 → 同じハンドルで DeletePending = true、クローズで名前が消える。
    // 削除用ハンドルを開いている間、他者は書き込みで開けない (32)。
    [Fact]
    public void SetDispositionEx_DeletesThroughTheSameHandle()
    {
        var dir = CreateDirectory();
        var file = WriteFile(dir, "a.txt", "hello");
        var guard = new DeletionGuard(dir);

        using (var handle = new WindowsFileSystemProbe().OpenForDeletion(file).Value!)
        {
            var write = Assert.Throws<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
            Assert.Equal(32, Win32Code(write));

            guard.Check(handle);
            Assert.True(handle.SetDispositionEx(DeletionPhase.DispositionFlags).Succeeded);
            Assert.True(handle.GetStandardInformation().Value.DeletePending);
        }

        Assert.False(File.Exists(file));
        Assert.Equal(1, guard.CheckCount);
    }

    // read-only のファイルでは 0x3 の指示が 5 で失敗し、DeletePending は false のまま (IGNORE_READONLY_ATTRIBUTE を使わない)
    [Fact]
    public void SetDispositionEx_FailsOnReadOnly()
    {
        var dir = CreateDirectory();
        var file = WriteFile(dir, "a.txt", "hello");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var guard = new DeletionGuard(dir);

        using (var handle = new WindowsFileSystemProbe().OpenForDeletion(file).Value!)
        {
            guard.Check(handle);
            var result = handle.SetDispositionEx(DeletionPhase.DispositionFlags);
            Assert.False(result.Succeeded);
            Assert.Equal(5, result.Error);
            Assert.False(handle.GetStandardInformation().Value.DeletePending);
        }

        Assert.True(File.Exists(file));
    }

    // ガード自体: fixture の外、fixture 内の junction を経由するパスは違反
    [Fact]
    public void Guard_RejectsOutsideAndReparseParents()
    {
        var dir = CreateDirectory();
        var real = Directory.CreateDirectory(Path.Combine(dir, "real")).FullName;
        var file = WriteFile(real, "a.txt", "hello");
        CreateJunction(Path.Combine(dir, "link"), real);
        var guard = new DeletionGuard(dir);

        Assert.Throws<GuardViolationException>(() => guard.Check(guard.RootFinalPath + "-other\\a.txt"));
        Assert.Throws<GuardViolationException>(() => guard.Check(guard.RootFinalPath));
        Assert.Throws<GuardViolationException>(() => guard.Check(guard.RootFinalPath + "\\link\\a.txt"));
        guard.Check(guard.RootFinalPath + "\\real\\a.txt");
        Assert.Equal(3, guard.Violations.Count);
        Assert.True(File.Exists(file));
    }
}
