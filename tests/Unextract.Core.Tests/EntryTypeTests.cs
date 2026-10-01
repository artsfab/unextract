using Unextract.Core.Results;
using Unextract.Core.Tests.Fixtures;
using static Unextract.Core.Tests.TestHelpers;

namespace Unextract.Core.Tests;

// SPEC §4.4 (DEC-7)。ExternalAttributes の上位16ビットの種別と下位の DOS 属性。
public class EntryTypeTests
{
    private static Unextract.Core.Zip.ZipPrevalidationResult ValidateOne(string name, int attributes, long length = 0) =>
        Validate([FakeEntries.Entry(0, name, length, attributes)]);

    // テスト Z04: symlink・FIFO・デバイス・socket の種別 (ファイル名・ディレクトリ名の両方)
    [Theory]
    [InlineData(0xA000u)]   // symlink
    [InlineData(0x1000u)]   // FIFO
    [InlineData(0x2000u)]   // 文字デバイス
    [InlineData(0x6000u)]   // ブロックデバイス
    [InlineData(0xC000u)]   // socket
    [InlineData(0xF000u)]
    public void Z04_SpecialTypes_AreFatal_ForFilesAndDirectories(uint type)
    {
        AssertFatal(ValidateOne("link", FakeEntries.Attributes(type)), FatalKind.UnsupportedEntryType, 0);
        AssertFatal(ValidateOne("link/", FakeEntries.Attributes(type)), FatalKind.UnsupportedEntryType, 0);
    }

    // テスト Z04: symlink のパーミッション付きの典型値 (0xA1FF0000)
    [Fact]
    public void Z04_TypicalSymlinkAttributes_AreFatal()
    {
        AssertFatal(ValidateOne("link", unchecked((int)0xA1FF0000)), FatalKind.UnsupportedEntryType, 0);
    }

    // テスト Z04: DOS 属性の reparse point、区切りなしで DOS ディレクトリ属性、Length > 0 のディレクトリ
    [Fact]
    public void Z04_DosAttributesAndDirectoryData_AreFatal()
    {
        AssertFatal(ValidateOne("a.txt", FakeEntries.Attributes(0, 0x400)), FatalKind.DosReparsePointAttribute, 0);
        AssertFatal(ValidateOne("d/", FakeEntries.Attributes(0, 0x410)), FatalKind.DosReparsePointAttribute, 0);
        AssertFatal(ValidateOne("a", FakeEntries.Attributes(0, 0x10)), FatalKind.DosDirectoryAttributeOnFileEntry, 0);
        AssertFatal(ValidateOne("d/", 0, length: 1), FatalKind.DirectoryEntryWithData, 0);
        AssertFatal(ValidateOne("d/", 0, length: -1), FatalKind.DirectoryEntryWithData, 0);
    }

    // テスト Z04a
    [Fact]
    public void Z04a_FileEntryWithDirectoryType_IsFatal()
    {
        AssertFatal(ValidateOne("a.txt", FakeEntries.Attributes(0x4000)), FatalKind.FileEntryWithDirectoryType, 0);
        AssertFatal(ValidateOne("a.txt", FakeEntries.Attributes(0x41ED)), FatalKind.FileEntryWithDirectoryType, 0);
    }

    // テスト Z04b
    [Fact]
    public void Z04b_DirectoryEntryWithFileType_IsFatal()
    {
        AssertFatal(ValidateOne("d/", FakeEntries.Attributes(0x8000)), FatalKind.DirectoryEntryWithFileType, 0);
        AssertFatal(ValidateOne("d/", FakeEntries.Attributes(0x81A4)), FatalKind.DirectoryEntryWithFileType, 0);
    }

    // テスト Z05: 許可される種別と、無視する DOS 属性 (read-only 0x1、hidden 0x2、system 0x4、archive 0x20)
    [Theory]
    [InlineData("a.txt", 0x0000u, 0x00u, false)]
    [InlineData("a.txt", 0x81A4u, 0x00u, false)]
    [InlineData("a.txt", 0x8000u, 0x27u, false)]
    [InlineData("a.txt", 0x0000u, 0x01u, false)]
    [InlineData("a.txt", 0x0000u, 0x02u, false)]
    [InlineData("a.txt", 0x0000u, 0x04u, false)]
    [InlineData("a.txt", 0x0000u, 0x20u, false)]
    [InlineData("d/", 0x0000u, 0x00u, true)]
    [InlineData("d/", 0x41EDu, 0x10u, true)]
    [InlineData("d/", 0x4000u, 0x00u, true)]
    [InlineData("d/", 0x0000u, 0x10u, true)]
    [InlineData("d/", 0x0000u, 0x27u, true)]
    public void Z05_AllowedTypesAndIgnoredDosAttributes_Pass(string name, uint type, uint dos, bool isDirectory)
    {
        var result = ValidateOne(name, FakeEntries.Attributes(type, dos));

        AssertPassed(result, 1);
        Assert.Equal(isDirectory, result.Entries[0].IsDirectory);
    }
}
