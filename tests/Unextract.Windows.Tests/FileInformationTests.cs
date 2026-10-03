using Microsoft.Win32.SafeHandles;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests;

// 比較用ハンドルからの情報取得 (docs/spec/filesystem.md#handles、docs/spec/filesystem.md#baselines) を実 NTFS で確認する。
public class FileInformationTests
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    private static SafeFileHandle OpenComparison(string path)
    {
        var result = HandleOpener.OpenForComparison(path);
        Assert.True(result.Succeeded, result.ToString());
        return result.Value;
    }

    private static T Ok<T>(Win32Result<T> result)
    {
        Assert.True(result.Succeeded, result.ToString());
        return result.Value!;
    }

    // 確認 1: File ID・サイズ・属性・リンク数・ストリーム一覧・最終パスが取れる
    [Fact]
    public void ComparisonHandle_ReadsSnapshot()
    {
        var dir = CreateDirectory();
        var path = WriteFile(dir, "MixedCase.txt", "hello, unextract");

        FileSnapshot snapshot;
        VolumeFileId id;
        using (var handle = OpenComparison(path))
        {
            snapshot = Ok(FileInformation.ReadSnapshot(handle));
            id = Ok(FileInformation.GetVolumeFileId(handle));
        }

        Assert.NotEqual(default, snapshot.FileId);
        Assert.Equal(id.FileId, snapshot.FileId);
        Assert.Equal(id.VolumeSerialNumber, snapshot.VolumeSerialNumber);
        Assert.Equal(16, snapshot.EndOfFile);
        Assert.Equal(1u, snapshot.NumberOfLinks);
        Assert.False(snapshot.IsDirectory);
        Assert.False(snapshot.DeletePending);
        Assert.Equal(0u, snapshot.ReparseTag);
        Assert.Equal(0u, snapshot.Attributes & ~0x20u & ~0x80u);
        Assert.NotEqual(0, snapshot.LastWriteTime);
        Assert.NotEqual(0, snapshot.ChangeTime);
        Assert.Equal([new StreamEntry("::$DATA", 16)], snapshot.Streams);
        Assert.Equal(@"\\?\" + path, snapshot.FinalPath);
    }

    // 確認 1: 大小文字違いの名前で開くと、最終パスは実名 (ディスク上の大小文字) になる
    [Fact]
    public void FinalPath_ReturnsOnDiskCase()
    {
        var dir = CreateDirectory();
        var path = WriteFile(dir, "MixedCase.txt", "x");

        FileSnapshot viaOtherCase;
        using (var handle = OpenComparison(Path.Combine(dir, "mIXEDcASE.TXT")))
        {
            viaOtherCase = Ok(FileInformation.ReadSnapshot(handle));
        }

        Assert.Equal(@"\\?\" + path, viaOtherCase.FinalPath);
        Assert.EndsWith(@"\MixedCase.txt", viaOtherCase.FinalPath, StringComparison.Ordinal);
    }

    // 確認 2: ADS 付きファイルでは名前付きストリームが一覧に出る
    [Fact]
    public void Streams_IncludeAlternateDataStream()
    {
        var dir = CreateDirectory();
        var path = WriteFile(dir, "ads.txt", "main");
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]");

        using var handle = OpenComparison(path);
        var streams = Ok(FileInformation.GetStreams(handle));

        Assert.Equal(2, streams.Count);
        Assert.Contains(new StreamEntry("::$DATA", 4), streams);
        Assert.Contains(streams, s => s.Name == ":Zone.Identifier:$DATA");
    }

    // 確認 2: hardlink でリンク数が 2 になる
    [Fact]
    public void HardLink_NumberOfLinksIsTwo()
    {
        var dir = CreateDirectory();
        var path = WriteFile(dir, "original.txt", "data");
        var link = Path.Combine(dir, "link.txt");
        CreateHardLink(link, path);

        using var original = OpenComparison(path);
        using var linked = OpenComparison(link);

        Assert.Equal(2u, Ok(FileInformation.GetStandardInformation(original)).NumberOfLinks);
        Assert.Equal(2u, Ok(FileInformation.GetStandardInformation(linked)).NumberOfLinks);
        Assert.Equal(Ok(FileInformation.GetVolumeFileId(original)), Ok(FileInformation.GetVolumeFileId(linked)));
    }

    // 確認 3: 親 File ID が、親ディレクトリを開いて得た File ID と一致する
    [Fact]
    public void ParentFileId_MatchesParentDirectoryFileId()
    {
        var dir = CreateDirectory();
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName;
        var path = WriteFile(sub, "child.txt", "c");

        FileId128 parentOfFile;
        using (var file = OpenComparison(path))
        {
            parentOfFile = Ok(FileInformation.GetParentFileId(file));
        }

        FileId128 subId;
        FileId128 parentOfSub;
        using (var subHandle = Ok(HandleOpener.OpenDirectoryForEnumeration(sub)))
        {
            subId = Ok(FileInformation.GetVolumeFileId(subHandle)).FileId;
            parentOfSub = Ok(FileInformation.GetParentFileId(subHandle));
        }

        FileId128 dirId;
        using (var dirHandle = Ok(HandleOpener.OpenDirectoryForEnumeration(dir)))
        {
            dirId = Ok(FileInformation.GetVolumeFileId(dirHandle)).FileId;
        }

        Assert.Equal(subId, parentOfFile);
        Assert.Equal(dirId, parentOfSub);
        Assert.NotEqual(subId, dirId);
    }

    // 確認 4: junction (同じ fixture 内のディレクトリを指す) は属性 0x410、reparse tag 0xA0000003。
    // 比較用ハンドルは FILE_FLAG_OPEN_REPARSE_POINT 付きなので、リンク先ではなく junction 自身の情報になる。
    [Fact]
    public void Junction_HasReparseAttributesAndMountPointTag()
    {
        var dir = CreateDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var link = Path.Combine(dir, "junction");
        CreateJunction(link, target);

        using var handle = OpenComparison(link);
        var basic = Ok(FileInformation.GetBasicInformation(handle));
        var tag = Ok(FileInformation.GetAttributeTagInformation(handle));
        var standard = Ok(FileInformation.GetStandardInformation(handle));
        var linkId = Ok(FileInformation.GetVolumeFileId(handle)).FileId;

        Assert.Equal(0x410u, basic.Attributes);
        Assert.Equal(0x410u, tag.Attributes);
        Assert.Equal(IoReparseTagMountPoint, tag.ReparseTag);
        Assert.True(standard.IsDirectory);

        using var targetHandle = OpenComparison(target);
        Assert.NotEqual(Ok(FileInformation.GetVolumeFileId(targetHandle)).FileId, linkId);
    }

    // 比較用ハンドルはディレクトリも開ける (FILE_FLAG_BACKUP_SEMANTICS)。Directory・属性・最終パスは取れるが、
    // データストリームの無いディレクトリでは FileStreamInfo が ERROR_HANDLE_EOF (38) で失敗する。
    // GetStreams はこれを読み替えずに失敗として返すため、ReadSnapshot (MATCHED の通常ファイル用) もディレクトリでは失敗する。
    [Fact]
    public void ComparisonHandle_OnDirectory_StreamInfoFailsWithHandleEof()
    {
        const int errorHandleEof = 38;
        var dir = CreateDirectory();
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName;

        using var handle = OpenComparison(sub);

        Assert.True(Ok(FileInformation.GetStandardInformation(handle)).IsDirectory);
        Assert.NotEqual(0u, Ok(FileInformation.GetBasicInformation(handle)).Attributes & FileAttributeDirectory);
        Assert.Equal(@"\\?\" + sub, Ok(FileInformation.GetFinalPath(handle)));

        var streams = FileInformation.GetStreams(handle);
        Assert.False(streams.Succeeded);
        Assert.Equal(errorHandleEof, streams.Error);

        var snapshot = FileInformation.ReadSnapshot(handle);
        Assert.False(snapshot.Succeeded);
        Assert.Null(snapshot.Value);
        Assert.Equal(errorHandleEof, snapshot.Error);
    }

    // target のボリュームは NTFS (この fixture の置き場所が NTFS である前提)
    [Fact]
    public void VolumeInformation_ReportsNtfs()
    {
        var dir = CreateDirectory();

        using var handle = Ok(HandleOpener.OpenTargetRoot(dir));
        var volume = Ok(FileInformation.GetVolumeInformation(handle));

        Assert.Equal("NTFS", volume.FileSystemName);
        Assert.True(volume.IsNtfs);
        Assert.Equal(@"\\?\" + dir, Ok(FileInformation.GetFinalPath(handle)));
    }

    // 確認 9: FILE_READ_ATTRIBUTES だけのハンドルでも FSCTL_READ_FILE_USN_DATA が成功する (docs/RATIONALE.md#current-state PoC 4 の再現)
    [Fact]
    public void ParentFileId_WorksWithReadAttributesOnlyHandle()
    {
        var dir = CreateDirectory();
        var path = WriteFile(dir, "a.txt", "a");
        var readAttributesOnly = new HandleSpec(
            HandleSpecs.FileReadAttributes,
            0,
            HandleSpecs.FileFlagBackupSemantics | HandleSpecs.FileFlagOpenReparsePoint | HandleSpecs.FileFlagOpenNoRecall);

        FileId128 parent;
        using (var handle = Ok(HandleOpener.Open(path, readAttributesOnly)))
        {
            parent = Ok(FileInformation.GetParentFileId(handle));
        }

        using var dirHandle = Ok(HandleOpener.OpenDirectoryForEnumeration(dir));
        Assert.Equal(Ok(FileInformation.GetVolumeFileId(dirHandle)).FileId, parent);
    }
}
