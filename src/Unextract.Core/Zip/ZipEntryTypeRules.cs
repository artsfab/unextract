using Unextract.Core.Results;

namespace Unextract.Core.Zip;

// ZIP の特殊エントリの判定 (docs/spec/zip.md#types、docs/RATIONALE.md#real-names)。
// ZipArchiveEntry は作成元 OS を公開しないため、ExternalAttributes だけで保守的に判定する。
// 種別はエントリの種類 (名前が区切りで終わるか) と組み合わせて判定し、
// ファイルエントリの 0x4000 とディレクトリエントリの 0x8000 は、どちらとして扱うか決められないため FATAL。
// 判定できないエントリを通常ファイルとみなさない。
internal static class ZipEntryTypeRules
{
    private const uint TypeMask = 0xF000;
    private const uint TypeNone = 0;
    private const uint TypeDirectory = 0x4000;
    private const uint TypeRegularFile = 0x8000;

    private const uint DosDirectory = 0x10;
    private const uint DosReparsePoint = 0x400;

    public static FatalKind? Check(ZipEntryInfo entry, bool isDirectory)
    {
        var attributes = unchecked((uint)entry.ExternalAttributes);
        var type = (attributes >> 16) & TypeMask;
        var dos = attributes & 0xFFFF;

        if (isDirectory)
        {
            if (type == TypeRegularFile)
            {
                return FatalKind.DirectoryEntryWithFileType;
            }

            if (type is not (TypeNone or TypeDirectory))
            {
                return FatalKind.UnsupportedEntryType;
            }
        }
        else
        {
            if (type == TypeDirectory)
            {
                return FatalKind.FileEntryWithDirectoryType;
            }

            if (type is not (TypeNone or TypeRegularFile))
            {
                return FatalKind.UnsupportedEntryType;
            }
        }

        // read-only、hidden、system、archive などその他の DOS 属性は復元しないため無視する。
        if ((dos & DosReparsePoint) != 0)
        {
            return FatalKind.DosReparsePointAttribute;
        }

        if (!isDirectory && (dos & DosDirectory) != 0)
        {
            return FatalKind.DosDirectoryAttributeOnFileEntry;
        }

        if (isDirectory && entry.Length != 0)
        {
            return FatalKind.DirectoryEntryWithData;
        }

        return null;
    }
}
