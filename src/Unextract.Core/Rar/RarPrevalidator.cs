using Unextract.Core.Results;
using Unextract.Core.Zip;

namespace Unextract.Core.Rar;

// RAR 固有の受理規則 (docs/spec/rar.md#listing、docs/spec/rar.md#names、docs/spec/rar.md#types、docs/spec/rar.md#limits)。
// Prepare の手順6で、アーカイブのフラグ → 全エントリの RAR 規則の順に検査し、最初の FATAL で打ち切る。続く名前・構造・共通上限は ZipPrevalidator が行う。
// エントリ単位の検査順は Solid → 分割 → 暗号化 → リダイレクト → 作成元 OS → 名前末尾の区切り → ディレクトリのデータ → 属性・mode → ハッシュ → 辞書。
// target に触れず、操作・モード・--entries に関係なく全エントリに行う。
public static class RarPrevalidator
{
    private const uint DosDirectory = 0x10;
    private const uint DosReparsePoint = 0x400;

    private const uint UnixTypeMask = 0xF000;
    private const uint UnixDirectory = 0x4000;
    private const uint UnixRegularFile = 0x8000;

    public static FatalError? Validate(RarArchiveInfo archive, IEnumerable<ZipEntryInfo> entries, Limits limits)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(limits);

        if (archive.IsSolid)
        {
            return new FatalError(FatalKind.ArchiveSolid);
        }

        if (archive.HasEncryptedHeaders)
        {
            return new FatalError(FatalKind.ArchiveEncrypted);
        }

        foreach (var entry in entries)
        {
            var rar = entry.Rar ?? throw new InvalidOperationException("RAR のエントリに RAR の情報がありません。");
            if (Check(entry, rar, limits) is { } kind)
            {
                return new FatalError(kind, new ZipEntryRef(entry.Index, entry.FullName));
            }
        }

        return null;
    }

    private static FatalKind? Check(ZipEntryInfo entry, RarEntryMetadata rar, Limits limits)
    {
        if (rar.IsSolid)
        {
            return FatalKind.EntrySolid;
        }

        if (rar.IsSplit)
        {
            return FatalKind.EntrySplit;
        }

        if (entry.IsEncrypted)
        {
            return FatalKind.EntryEncrypted;
        }

        if (rar.RedirectionType != 0)
        {
            return FatalKind.EntryRedirection;
        }

        if (rar.HostOs == RarHostOs.Other)
        {
            return FatalKind.UnsupportedHostOs;
        }

        // ディレクトリの FullName は DLL の名前に区切りを1個付けたもの。ファイルの DLL の名前 (= FullName) が区切りで終わるものは拒否する。
        if (!rar.IsDirectory && (entry.FullName.EndsWith('\\') || entry.FullName.EndsWith('/')))
        {
            return FatalKind.FileEntryNameEndsWithSeparator;
        }

        if (rar.IsDirectory && entry.Length != 0)
        {
            return FatalKind.DirectoryEntryWithData;
        }

        if ((rar.HostOs == RarHostOs.Windows ? CheckDos(rar) : CheckUnix(rar)) is { } typeError)
        {
            return typeError;
        }

        if (!rar.IsDirectory)
        {
            if (rar.HashType == RarHashType.None)
            {
                return FatalKind.EntryWithoutHash;
            }

            if (rar.DictionarySize > limits.MaxRarDictionarySize)
            {
                return FatalKind.EntryDictionaryTooLarge;
            }
        }

        return null;
    }

    // 作成元 OS が Windows: DOS 属性として ZIP の規則と同じに扱う (その他の属性は復元しないため無視する)。
    private static FatalKind? CheckDos(RarEntryMetadata rar)
    {
        if ((rar.Attributes & DosReparsePoint) != 0)
        {
            return FatalKind.DosReparsePointAttribute;
        }

        if (!rar.IsDirectory && (rar.Attributes & DosDirectory) != 0)
        {
            return FatalKind.DosDirectoryAttributeOnFileEntry;
        }

        return null;
    }

    // 作成元 OS が Unix: mode の種別がファイルなら 0x8000、ディレクトリなら 0x4000 の場合だけを許す (ZIP と異なり 0 を許さない)。
    private static FatalKind? CheckUnix(RarEntryMetadata rar)
    {
        var type = rar.Attributes & UnixTypeMask;
        if (rar.IsDirectory)
        {
            return type switch
            {
                UnixDirectory => null,
                UnixRegularFile => FatalKind.DirectoryEntryWithFileType,
                _ => FatalKind.UnsupportedEntryType,
            };
        }

        return type switch
        {
            UnixRegularFile => null,
            UnixDirectory => FatalKind.FileEntryWithDirectoryType,
            _ => FatalKind.UnsupportedEntryType,
        };
    }
}
