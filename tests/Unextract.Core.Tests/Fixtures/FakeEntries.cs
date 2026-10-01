using Unextract.Core.Zip;

namespace Unextract.Core.Tests.Fixtures;

// 検証ロジックを ZipArchive なしで試すための偽のエントリ一覧。
internal static class FakeEntries
{
    public static ZipEntryInfo Entry(int index, string name, long length = 0, int externalAttributes = 0) =>
        new(index, name, length, externalAttributes, IsEncrypted: false, Crc32: 0);

    public static IEnumerable<ZipEntryInfo> Names(params string[] names) =>
        names.Select((name, index) => Entry(index, name));

    public static IEnumerable<ZipEntryInfo> Lengths(params long[] lengths) =>
        lengths.Select((length, index) => Entry(index, $"f{index}.bin", length));

    // 上位16ビットの種別と下位16ビットの DOS 属性から ExternalAttributes を作る。
    public static int Attributes(uint type, uint dos = 0) => unchecked((int)((type << 16) | dos));
}
