using System.IO.Compression;
using System.Security;
using System.Text;
using Unextract.Core.Results;

namespace Unextract.Core.Zip;

public sealed record ZipOpenResult(ZipArchiveSource? Source, FatalError? Fatal);

// ZipArchive のアダプタ。ZIP を FileShare.Read で開いて Dispose まで保持し (docs/SPEC.md#prepare のZIP保持)、
// エントリ名を CP437 指定で復号する (docs/spec/zip.md#decoding)。ZIP 構造の独自解析はしない。
public sealed class ZipArchiveSource : IZipContentProvider, IDisposable
{
    // フラグなしの名前を UTF-8 として扱わせないため、必ず CP437 を渡す (docs/spec/zip.md#decoding、docs/RATIONALE.md#real-names)。
    private static readonly Encoding EntryNameEncoding =
        CodePagesEncodingProvider.Instance.GetEncoding(437)
        ?? throw new InvalidOperationException("code page 437 is not available");

    private readonly Stream _stream;
    private readonly ZipArchive _archive;

    private ZipArchiveSource(Stream stream, ZipArchive archive)
    {
        _stream = stream;
        _archive = archive;
    }

    public int EntryCount => _archive.Entries.Count;

    // ZIP 内の順序で返す。
    public IEnumerable<ZipEntryInfo> Entries
    {
        get
        {
            var entries = _archive.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                yield return new ZipEntryInfo(
                    i,
                    entry.FullName,
                    entry.Length,
                    entry.ExternalAttributes,
                    entry.IsEncrypted,
                    entry.Crc32);
            }
        }
    }

    // 内容比較候補の内容を読むときだけ使う (docs/spec/zip.md#read-scope)。
    public IZipEntryContent GetContent(int index) => new ZipArchiveEntryContent(_archive.Entries[index]);

    public static ZipOpenResult Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (IsOpenFailure(ex))
        {
            return new ZipOpenResult(null, new FatalError(FatalKind.ArchiveOpenFailed, Detail: ex.Message));
        }

        return Open(stream);
    }

    // stream の所有権を受け取る。失敗時は閉じる。
    internal static ZipOpenResult Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, EntryNameEncoding);

            // Central Directory の読み取り失敗をここで検出する。
            _ = archive.Entries.Count;
            return new ZipOpenResult(new ZipArchiveSource(stream, archive), null);
        }
        catch (Exception ex) when (IsOpenFailure(ex) || ex is InvalidDataException)
        {
            stream.Dispose();
            return new ZipOpenResult(null, new FatalError(FatalKind.ArchiveUnreadable, Detail: ex.Message));
        }
    }

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }

    private static bool IsOpenFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or SecurityException;
}
