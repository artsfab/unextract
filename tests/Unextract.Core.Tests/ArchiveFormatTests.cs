using Unextract.Core.Zip;

namespace Unextract.Core.Tests;

// テスト U01: 形式の判定は指定パスの最終成分の拡張子だけで行う (docs/spec/rar.md#format)。中身は見ない。
public class ArchiveFormatTests
{
    [Theory]
    [InlineData(@"C:\in\a.rar", ArchiveFormat.Rar)]
    [InlineData(@"C:\in\a.RAR", ArchiveFormat.Rar)]
    [InlineData(@"C:\in\a.Rar", ArchiveFormat.Rar)]
    [InlineData(@"a.rar", ArchiveFormat.Rar)]
    [InlineData(@"\\?\C:\in\a.b.rar", ArchiveFormat.Rar)]
    [InlineData(@"C:\in\a.zip", ArchiveFormat.Zip)]
    [InlineData(@"C:\in\a.cbr", ArchiveFormat.Zip)]
    [InlineData(@"C:\in\a.rar.zip", ArchiveFormat.Zip)]
    [InlineData(@"C:\in\a.rar.", ArchiveFormat.Zip)]
    [InlineData(@"C:\in\a.rar ", ArchiveFormat.Zip)]
    [InlineData(@"C:\in\a.rarx", ArchiveFormat.Zip)]
    [InlineData(@"C:\in\rar", ArchiveFormat.Zip)]
    [InlineData(@"C:\in.rar\a", ArchiveFormat.Zip)]
    [InlineData(@"C:\in\.rar", ArchiveFormat.Rar)]
    [InlineData(@"", ArchiveFormat.Zip)]
    public void U01_FormatFromFinalComponentExtension(string path, ArchiveFormat expected)
    {
        Assert.Equal(expected, ArchiveFormats.FromPath(path));
    }
}
