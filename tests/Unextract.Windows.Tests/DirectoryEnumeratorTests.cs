using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests;

// SPEC §6.2 の 1・2 の列挙を実 NTFS で確認する (照合・保持の規則は段階 C-2)。
public class DirectoryEnumeratorTests(ITestOutputHelper output)
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    private static List<DirectoryEntry> EnumerateAll(SafeFileHandle handle)
    {
        var enumerator = new DirectoryEnumerator(handle);
        var entries = new List<DirectoryEntry>();
        while (true)
        {
            var step = enumerator.Next();
            switch (step.Kind)
            {
                case DirectoryEnumerationStepKind.Entry:
                    entries.Add(step.Entry);
                    break;
                case DirectoryEnumerationStepKind.End:
                    // 終端の後も End を返し続ける
                    Assert.Equal(DirectoryEnumerationStepKind.End, enumerator.Next().Kind);
                    return entries;
                default:
                    Assert.Fail($"enumeration failed: {step.Error}");
                    return entries;
            }
        }
    }

    private static FileId128 FileIdOf(string path)
    {
        using var handle = HandleOpener.OpenForComparison(path).Value!;
        return FileInformation.GetVolumeFileId(handle).Value.FileId;
    }

    // 確認 5: 通常ファイル・ディレクトリ・junction が属性・reparse tag・File ID つきで返り、"." と ".." は除かれる
    [Fact]
    public void Enumerate_ReturnsFilesDirectoriesAndJunctions()
    {
        var dir = CreateDirectory();
        var file = WriteFile(dir, "File.txt", "f");
        var sub = Directory.CreateDirectory(Path.Combine(dir, "Sub")).FullName;
        var junction = Path.Combine(dir, "Junction");
        CreateJunction(junction, sub);

        List<DirectoryEntry> entries;
        using (var handle = HandleOpener.OpenDirectoryForEnumeration(dir).Value!)
        {
            entries = EnumerateAll(handle);
        }

        Assert.Equal(["File.txt", "Junction", "Sub"], entries.Select(e => e.Name).Order(StringComparer.Ordinal));

        var fileEntry = entries.Single(e => e.Name == "File.txt");
        Assert.Equal(0u, fileEntry.Attributes & FileAttributeDirectory);
        Assert.Equal(0u, fileEntry.ReparseTag);
        Assert.Equal(FileIdOf(file), fileEntry.FileId);

        var subEntry = entries.Single(e => e.Name == "Sub");
        Assert.Equal(FileAttributeDirectory, subEntry.Attributes & 0x410u);
        Assert.Equal(0u, subEntry.ReparseTag);
        Assert.Equal(FileIdOf(sub), subEntry.FileId);

        var junctionEntry = entries.Single(e => e.Name == "Junction");
        Assert.Equal(0x410u, junctionEntry.Attributes);
        Assert.Equal(IoReparseTagMountPoint, junctionEntry.ReparseTag);
        Assert.Equal(FileIdOf(junction), junctionEntry.FileId);
    }

    // target ルート用ハンドルでも列挙できる (SPEC §6.2 の 1: target ルートは保持しているハンドルをそのまま使う)
    [Fact]
    public void Enumerate_WorksWithTargetRootHandle()
    {
        var dir = CreateDirectory();
        WriteFile(dir, "a.txt", "a");

        using var handle = HandleOpener.OpenTargetRoot(dir).Value!;

        Assert.Equal(["a.txt"], EnumerateAll(handle).Select(e => e.Name));
    }

    // 空のディレクトリは項目なしで終端になる
    [Fact]
    public void Enumerate_EmptyDirectory_ReturnsNoEntries()
    {
        var dir = CreateDirectory();

        using var handle = HandleOpener.OpenDirectoryForEnumeration(dir).Value!;

        Assert.Empty(EnumerateAll(handle));
    }

    // バッファ (64 KiB) に収まらない数の項目でも、全件を1回ずつ返す
    [Fact]
    public void Enumerate_ManyEntries_SpansMultipleBuffers()
    {
        var dir = CreateDirectory();
        var expected = new List<string>();
        for (var i = 0; i < 1200; i++)
        {
            var name = $"{i:D4}-{new string('n', 100)}.txt";
            File.WriteAllBytes(Path.Combine(dir, name), []);
            expected.Add(name);
        }

        List<DirectoryEntry> entries;
        using (var handle = HandleOpener.OpenDirectoryForEnumeration(dir).Value!)
        {
            entries = EnumerateAll(handle);
        }

        // 1項目は固定部分 88 バイト + 名前 216 バイト。合計は 64 KiB を大きく超える。
        Assert.True(entries.Count * (88 + 216) > DirectoryEnumerator.BufferSize * 4);
        Assert.Equal(expected, entries.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    // 確認 5: 8.3 の短い名前は項目として返らない。8.3 名の生成が無効な環境では前提不成立として報告する (失敗にしない)。
    [Fact]
    public void Enumerate_DoesNotReturnShortNames()
    {
        var dir = CreateDirectory();
        var longPath = WriteFile(dir, "A Long File Name For Short Names.txt", "x");
        var shortName = Path.GetFileName(ShortPath(longPath));

        List<DirectoryEntry> entries;
        using (var handle = HandleOpener.OpenDirectoryForEnumeration(dir).Value!)
        {
            entries = EnumerateAll(handle);
        }

        Assert.Equal(["A Long File Name For Short Names.txt"], entries.Select(e => e.Name));

        if (string.Equals(shortName, "A Long File Name For Short Names.txt", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("前提不成立: このボリュームでは 8.3 名が生成されていない。8.3 名が項目として返らないことは確認できない。");
            return;
        }

        output.WriteLine($"8.3 名: {shortName}");
        Assert.DoesNotContain(entries, e => string.Equals(e.Name, shortName, StringComparison.OrdinalIgnoreCase));

        // 8.3 名でも開けること (= 別名として実在すること) を確認しておく
        var viaShortName = HandleOpener.OpenForComparison(Path.Combine(dir, shortName));
        using (viaShortName.Value)
        {
            Assert.True(viaShortName.Succeeded, viaShortName.ToString());
        }
    }
}
