using System.IO.Compression;

namespace Unextract.Core.Zip;

// ZIP エントリの内容へのアクセス (docs/spec/zip.md#verification)。実体は ZipArchiveEntry のアダプタ。
public interface IZipEntryContent
{
    bool IsEncrypted { get; }

    long Length { get; }

    // Central Directory の CRC-32 (ZipArchiveEntry.Crc32)。
    uint Crc32 { get; }

    // 展開ストリームを新たに開く。例外は呼び出し側が「異常」として扱う。
    Stream Open();
}

// エントリ番号 (ZIP 内の順序、0 始まり) から内容を得る。
public interface IZipContentProvider
{
    IZipEntryContent GetContent(int index);
}

internal sealed class ZipArchiveEntryContent(ZipArchiveEntry entry) : IZipEntryContent
{
    public bool IsEncrypted => entry.IsEncrypted;

    public long Length => entry.Length;

    public uint Crc32 => entry.Crc32;

    public Stream Open() => entry.Open();
}
