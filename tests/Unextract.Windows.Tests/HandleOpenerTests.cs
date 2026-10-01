using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests;

// SPEC §8.1 の用途別オープンの共有モードとエラーコードを実 NTFS で確認する。
public class HandleOpenerTests
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;

    // 確認 6: 別のハンドルが GENERIC_WRITE で開いている対象は、比較用ハンドルで開けない (32)
    [Fact]
    public void Comparison_FailsWhileOtherHandleHasWriteAccess()
    {
        var dir = CreateDirectory();
        var path = WriteFile(dir, "editing.txt", "x");

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var result = HandleOpener.OpenForComparison(path);

            Assert.False(result.Succeeded);
            Assert.Equal(ErrorSharingViolation, result.Error);
            Assert.Equal("CreateFileW", result.Operation);
        }
    }

    // 確認 6: 比較用ハンドルを保持している間、別ハンドルでの書き込みオープンと改名 (MoveFileEx) が 32 で失敗する
    [Fact]
    public void Comparison_BlocksWriteOpenAndRename()
    {
        var dir = CreateDirectory();
        var path = WriteFile(dir, "held.txt", "x");
        var renamed = Path.Combine(dir, "renamed.txt");

        using (var handle = HandleOpener.OpenForComparison(path).Value!)
        {
            var write = Assert.Throws<IOException>(
                () => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
            Assert.Equal(ErrorSharingViolation, Win32Code(write));

            var move = Assert.Throws<IOException>(() => File.Move(path, renamed));
            Assert.Equal(ErrorSharingViolation, Win32Code(move));
        }

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(renamed));
    }

    // 確認 7: target ルート用ハンドル (FILE_LIST_DIRECTORY 付き) を保持している間、そのディレクトリを改名できない (32)
    [Fact]
    public void TargetRoot_BlocksRenameOfDirectory()
    {
        var dir = CreateDirectory();
        var root = Directory.CreateDirectory(Path.Combine(dir, "root")).FullName;
        var renamed = Path.Combine(dir, "renamed");

        using (var handle = HandleOpener.OpenTargetRoot(root).Value!)
        {
            var move = Assert.Throws<IOException>(() => Directory.Move(root, renamed));
            Assert.Equal(ErrorSharingViolation, Win32Code(move));
        }

        Assert.True(Directory.Exists(root));
        Assert.False(Directory.Exists(renamed));
    }

    // 確認 7 の対照: FILE_READ_ATTRIBUTES だけのハンドルは共有モードの判定に参加せず、保持していても改名できてしまう
    // (SPEC §8.1 で target ルートに FILE_LIST_DIRECTORY を付ける根拠)。
    [Fact]
    public void ReadAttributesOnlyHandle_DoesNotBlockRename()
    {
        var dir = CreateDirectory();
        var root = Directory.CreateDirectory(Path.Combine(dir, "root")).FullName;
        var renamed = Path.Combine(dir, "renamed");
        var readAttributesOnly = new HandleSpec(
            HandleSpecs.FileReadAttributes,
            HandleSpecs.FileShareRead | HandleSpecs.FileShareWrite,
            HandleSpecs.FileFlagBackupSemantics);

        using (var handle = HandleOpener.Open(root, readAttributesOnly).Value!)
        {
            Directory.Move(root, renamed);
        }

        Assert.False(Directory.Exists(root));
        Assert.True(Directory.Exists(renamed));
    }

    // 確認 8: 存在しないパスは 2、親が無い・親がファイルは 3
    [Fact]
    public void OpenErrors_ReturnWin32Codes()
    {
        var dir = CreateDirectory();
        var file = WriteFile(dir, "file.txt", "x");

        Assert.Equal(ErrorFileNotFound, HandleOpener.OpenForComparison(Path.Combine(dir, "missing.txt")).Error);
        Assert.Equal(ErrorPathNotFound, HandleOpener.OpenForComparison(Path.Combine(dir, "missing", "a.txt")).Error);
        Assert.Equal(ErrorPathNotFound, HandleOpener.OpenForComparison(Path.Combine(file, "a.txt")).Error);

        Assert.Equal(ErrorFileNotFound, HandleOpener.OpenDirectoryForEnumeration(Path.Combine(dir, "missing")).Error);
        Assert.Equal(ErrorPathNotFound, HandleOpener.OpenDirectoryForEnumeration(Path.Combine(dir, "missing", "sub")).Error);
        Assert.Equal(ErrorFileNotFound, HandleOpener.OpenTargetRoot(Path.Combine(dir, "missing")).Error);
        Assert.Equal(ErrorPathNotFound, HandleOpener.OpenTargetRoot(Path.Combine(file, "sub")).Error);
    }

    // 失敗時は部分的な値を持たない
    [Fact]
    public void FailedOpen_HasNoValue()
    {
        var dir = CreateDirectory();

        var result = HandleOpener.OpenForComparison(Path.Combine(dir, "missing.txt"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
    }

    // 定数が SPEC §8.1 の表の値と一致する
    [Fact]
    public void HandleSpecs_MatchSpecTable()
    {
        Assert.Equal(new HandleSpec(0x0001 | 0x0080, 0x1 | 0x2, 0x02000000), HandleSpecs.TargetRoot);
        Assert.Equal(new HandleSpec(0x0001 | 0x0080, 0x1 | 0x2, 0x02000000 | 0x00200000), HandleSpecs.Enumeration);
        Assert.Equal(
            new HandleSpec(0x80000000, 0x1, 0x02000000 | 0x00200000 | 0x00100000 | 0x08000000),
            HandleSpecs.Comparison);
    }
}
