namespace Unextract.Core.Zip;

// 入力のアーカイブの形式 (docs/SPEC.md#archive-terms)。
public enum ArchiveFormat
{
    Zip,
    Rar,
}

public static class ArchiveFormats
{
    // 指定されたパスの最終成分の拡張子が .rar (大小文字を区別しない) なら RAR、それ以外は ZIP (docs/spec/rar.md#format)。中身は見ない。
    public static ArchiveFormat FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return string.Equals(Path.GetExtension(path), ".rar", StringComparison.OrdinalIgnoreCase) ? ArchiveFormat.Rar : ArchiveFormat.Zip;
    }
}

// Prepare の手順2で開いて実行終了まで保持するアーカイブ (ZIP・RAR) の抽象 (docs/SPEC.md#prepare)。
// Entries はアーカイブの順のエントリ (RAR では DLL が列挙したもの)。内容の読み方は形式ごとに異なり、ZIP は GetContent (pull) で読む。
public interface IArchiveSource : IZipContentProvider, IDisposable
{
    int EntryCount { get; }

    IEnumerable<ZipEntryInfo> Entries { get; }
}
