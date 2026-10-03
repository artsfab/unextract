using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;

namespace Unextract.Core.Tests;

// target の確認 (docs/spec/cli.md#arguments、docs/spec/filesystem.md#target-root のtarget確認、docs/spec/filesystem.md#handles) を偽ファイルシステムで確認する。テスト P06〜P08 の Core 部分。
public class TargetRootTests
{
    private static readonly TargetLocationPolicy Policy = new(
        [@"\\?\C:\Users\me"],
        [@"\\?\C:\Windows", @"\\?\C:\Program Files", @"\\?\C:\Program Files (x86)", @"\\?\C:\ProgramData"]);

    private static FakeFileSystem NewFs()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\target");
        return fs;
    }

    private static FatalKind? Check(FakeFileSystem fs, string path, TargetLocationPolicy? policy = null)
    {
        var result = TargetRootValidator.Open(fs, path, policy ?? Policy);
        if (result.Root is { } root)
        {
            Assert.Equal(1, fs.OpenHandleCount);
            root.Dispose();
            Assert.Equal(0, fs.OpenHandleCount);
            return null;
        }

        // 失敗時はハンドルを残さない。
        Assert.Equal(0, fs.OpenHandleCount);
        Assert.Null(result.Root);
        return result.Error!.Kind;
    }

    [Fact]
    public void ValidTarget_IsHeldWithFinalPathAndId()
    {
        var fs = NewFs();

        var result = TargetRootValidator.Open(fs, @"C:\TARGET", Policy);

        using var root = result.Root!;
        Assert.Equal(@"\\?\C:\target", root.FinalPath);
        Assert.Equal(new VolumeFileId(FakeFileSystem.DefaultVolumeSerial, fs.Get(@"C:\target").Id), root.Id);
        Assert.Equal(@"\\?\C:\target\a\b.txt", root.ExpectedPath(["a", "b.txt"]));

        // 確認用ハンドル → 保持用ハンドルの順 (docs/spec/filesystem.md#target-root のtarget確認)。
        Assert.Equal(@"ConfirmTarget C:\TARGET", fs.Calls[0]);
        Assert.Equal(@"OpenTargetRoot C:\TARGET", fs.Calls[1]);
    }

    // P06 (Core): 存在しない、ファイルである、非 NTFS
    [Fact]
    public void P06_MissingFileOrNonNtfs_AreInputErrors()
    {
        var fs = NewFs();
        fs.AddFile(@"C:\target\file.txt", []);
        Assert.Equal(FatalKind.TargetNotFound, Check(fs, @"C:\missing"));
        Assert.Equal(FatalKind.TargetNotFound, Check(fs, @"C:\missing\sub"));
        Assert.Equal(FatalKind.TargetNotDirectory, Check(fs, @"C:\target\file.txt"));

        fs.FileSystemName = "FAT32";
        Assert.Equal(FatalKind.TargetNotNtfs, Check(fs, @"C:\target"));
    }

    // P06・P07: 最終成分が junction → 確認用ハンドル (OPEN_REPARSE_POINT 付き) の FileAttributeTagInfo で判定し入力エラー。
    // 保持用ハンドルは開かない。
    [Theory]
    [InlineData(true, FakeNode.ReparseTagMountPoint)]
    [InlineData(false, FakeNode.ReparseTagSymlink)]
    public void P07_FinalComponentReparse_IsInputError(bool isDirectory, uint tag)
    {
        var fs = NewFs();
        fs.AddReparse(@"C:\link", isDirectory, tag, fs.Get(@"C:\target"));

        Assert.Equal(FatalKind.TargetIsReparsePoint, Check(fs, @"C:\link"));
        Assert.DoesNotContain(fs.Calls, c => c.StartsWith("OpenTargetRoot", StringComparison.Ordinal));
    }

    // 祖先の reparse は許し、target は解決後の最終パスで固定する (docs/spec/cli.md#arguments)
    [Fact]
    public void AncestorReparse_IsAllowedAndFinalPathIsResolved()
    {
        var fs = NewFs();
        fs.AddDirectory(@"C:\target\real");
        fs.AddJunction(@"C:\alias", fs.Get(@"C:\target"));

        var result = TargetRootValidator.Open(fs, @"C:\alias\real", Policy);

        using var root = result.Root!;
        Assert.Equal(@"\\?\C:\target\real", root.FinalPath);
    }

    // P07: 確認用ハンドルでの確認の後、保持用ハンドルで開くまでに差し替えられ File ID が変わる → 入力エラー
    [Fact]
    public void P07_ReplacedAfterConfirmation_IsInputError()
    {
        var fs = NewFs();
        fs.AfterConfirmTarget = () =>
        {
            fs.Remove(fs.Get(@"C:\target"));
            fs.AddDirectory(@"C:\target");
        };

        Assert.Equal(FatalKind.TargetChangedDuringCheck, Check(fs, @"C:\target"));
    }

    // 確認・保持・情報取得の API の失敗は入力エラー
    [Theory]
    [InlineData(FakeOp.ConfirmTarget, 5)]
    [InlineData(FakeOp.OpenTargetRoot, 32)]
    [InlineData(FakeOp.DirectoryInfo, 5)]
    [InlineData(FakeOp.FileSystemName, 5)]
    public void ApiFailure_IsInputError(FakeOp op, int error)
    {
        var fs = NewFs();
        fs.Get(@"C:\target").Errors[op] = error;

        Assert.Equal(FatalKind.TargetCheckFailed, Check(fs, @"C:\target"));
    }

    // P08: 最終パスが \\?\UNC\ で始まる target → UNC 拒否として入力エラー
    [Fact]
    public void P08_UncFinalPath_IsInputError()
    {
        var fs = NewFs();
        fs.Get(@"C:\target").FinalPathOverride = @"\\?\UNC\server\share\dir";

        Assert.Equal(FatalKind.TargetIsUncPath, Check(fs, @"C:\target"));
    }

    // 拒否対象: ドライブルート、プロファイルそのもの、Windows / Program Files / Program Files (x86) / ProgramData とその配下
    [Theory]
    [InlineData(@"\\?\C:\", FatalKind.TargetIsDriveRoot)]
    [InlineData(@"\\?\D:\", FatalKind.TargetIsDriveRoot)]
    [InlineData(@"\\?\UNC\server\share", FatalKind.TargetIsUncPath)]
    [InlineData(@"\\?\unc\server\share\x", FatalKind.TargetIsUncPath)]
    [InlineData(@"\\?\C:\Users\me", FatalKind.TargetIsProtectedLocation)]
    [InlineData(@"\\?\C:\USERS\ME", FatalKind.TargetIsProtectedLocation)]
    [InlineData(@"\\?\C:\Windows", FatalKind.TargetIsProtectedLocation)]
    [InlineData(@"\\?\C:\Windows\System32", FatalKind.TargetIsProtectedLocation)]
    [InlineData(@"\\?\C:\Program Files", FatalKind.TargetIsProtectedLocation)]
    [InlineData(@"\\?\C:\Program Files\App\data", FatalKind.TargetIsProtectedLocation)]
    [InlineData(@"\\?\C:\Program Files (x86)\App", FatalKind.TargetIsProtectedLocation)]
    [InlineData(@"\\?\C:\ProgramData", FatalKind.TargetIsProtectedLocation)]
    [InlineData(@"\\?\c:\programdata\x", FatalKind.TargetIsProtectedLocation)]
    [InlineData(@"\\?\Volume{0123}\x", FatalKind.TargetUnsupportedPathForm)]
    [InlineData(@"C:\target", FatalKind.TargetUnsupportedPathForm)]
    [InlineData(@"\\?\C:\a\\b", FatalKind.TargetUnsupportedPathForm)]
    [InlineData(@"\\?\C:\a\", FatalKind.TargetUnsupportedPathForm)]
    public void ProtectedOrUnsupportedFinalPath_IsRejected(string finalPath, FatalKind expected)
    {
        Assert.Equal(expected, TargetRootValidator.CheckFinalPath(finalPath, Policy));
    }

    // プロファイル配下の通常ディレクトリ、名前が似ているだけのディレクトリは許可する
    [Theory]
    [InlineData(@"\\?\C:\Users\me\Downloads\extracted")]
    [InlineData(@"\\?\C:\Users\me\Desktop")]
    [InlineData(@"\\?\C:\Users\meow")]
    [InlineData(@"\\?\C:\WindowsApps")]
    [InlineData(@"\\?\C:\Program Files Extra")]
    [InlineData(@"\\?\D:\work")]
    public void OrdinaryDirectory_IsAllowed(string finalPath)
    {
        Assert.Null(TargetRootValidator.CheckFinalPath(finalPath, Policy));
    }

    // 拒否対象の判定は保持用ハンドルの最終パスで行う (利用者の入力文字列ではない)
    [Fact]
    public void ProtectedLocation_IsJudgedByFinalPath()
    {
        var fs = NewFs();
        fs.AddDirectory(@"C:\Windows");
        fs.AddDirectory(@"C:\Windows\Temp");
        fs.AddJunction(@"C:\innocent", fs.Get(@"C:\Windows"));

        Assert.Equal(FatalKind.TargetIsProtectedLocation, Check(fs, @"C:\innocent\Temp"));
        Assert.Equal(FatalKind.TargetIsDriveRoot, Check(fs, @"C:\"));
    }
}
