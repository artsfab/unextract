using System.Security;
using Unextract.Core.Rar;
using Unextract.Core.Results;
using Unextract.Core.Zip;

namespace Unextract.Windows.Rar;

// CLI が CommandContext に渡す RAR を開く関数 (docs/spec/rar.md#pinning: 読み込み元は実行中の exe と同じフォルダーの UnRAR64.dll だけ)。
public static class RarArchives
{
    public static ZipOpenResult Open(string path, Limits limits) => RarArchiveSource.Open(path, limits, () => UnrarLibrary.ForProcess.Load());
}

// RAR のアーカイブソース (docs/SPEC.md#prepare の手順2、docs/spec/rar.md#format、docs/spec/rar.md#listing)。
// アーカイブを FileShare.Read で開いて Dispose まで保持し、保持中のハンドルから先頭8バイトの署名を確かめ、DLL を照合・ロードし、
// 保持中のハンドルの最終パスで一覧用に開いてボリュームのフラグを確かめ、全ヘッダーを RAR_SKIP で列挙する。列挙中の上限は各エントリを保持する前に検査する。
// 内容は pull では読まない。Strict の内容読み取り用のセッションは OpenSession で作る (手順10)。
internal sealed class RarArchiveSource : IRarArchiveSource
{
    private static readonly byte[] Rar4Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly byte[] Rar5Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    private readonly FileStream _held;
    private readonly IUnrarApi _api;
    private readonly string _finalPath;
    private readonly List<ZipEntryInfo> _entries;
    private readonly List<RarHeaderSnapshot> _snapshots;

    private RarArchiveSource(FileStream held, IUnrarApi api, string finalPath, RarArchiveInfo archive, List<ZipEntryInfo> entries, List<RarHeaderSnapshot> snapshots)
    {
        _held = held;
        _api = api;
        _finalPath = finalPath;
        Archive = archive;
        _entries = entries;
        _snapshots = snapshots;
    }

    public RarArchiveInfo Archive { get; }

    public int EntryCount => _entries.Count;

    public IEnumerable<ZipEntryInfo> Entries => _entries;

    // RAR を ZIP の経路 (pull) で読まない (docs/ARCHITECTURE.md#shared-path)。
    public IZipEntryContent GetContent(int index) => throw new InvalidOperationException("RAR の内容は読み取りセッションで読む。");

    public static ZipOpenResult Open(string path, Limits limits, Func<UnrarLoadResult> library)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(library);

        FileStream held;
        try
        {
            held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (IsOpenFailure(ex))
        {
            return Fail(new FatalError(FatalKind.ArchiveOpenFailed, Detail: ex.Message));
        }

        var keep = false;
        try
        {
            // 先頭の8バイトだけを読み、0バイト目から RAR4 または RAR5 の署名が始まることを確かめる (SFX はここで拒否する)。
            // 開いた後の読み取りの失敗は ARCHIVE_UNREADABLE (docs/spec/rar.md#format。ZIP の ZipArchive の読み取り失敗と同じ分類・例外の範囲)。
            var head = new byte[8];
            int read;
            try
            {
                read = ReadHead(held, head);
            }
            catch (Exception ex) when (IsOpenFailure(ex))
            {
                return Fail(new FatalError(FatalKind.ArchiveUnreadable, Detail: ex.Message));
            }

            if (!IsRarSignature(head.AsSpan(0, read)))
            {
                return Fail(new FatalError(FatalKind.ArchiveNotRar));
            }

            // DLL の照合とロード (target・entries に触れる前)。
            var loaded = library();
            if (loaded.Api is not { } api)
            {
                return Fail(loaded.Fatal ?? throw new InvalidOperationException("DLL を利用できない原因がありません。"));
            }

            var finalPath = FileInformation.GetFinalPath(held.SafeFileHandle);
            if (!finalPath.Succeeded)
            {
                return Fail(new FatalError(FatalKind.ArchiveOpenFailed, Detail: $"{finalPath.Operation} が失敗 (Win32 エラー {finalPath.Error})", Win32Error: finalPath.Error));
            }

            var entries = new List<ZipEntryInfo>();
            var snapshots = new List<RarHeaderSnapshot>();
            var (archive, fatal) = List(api, finalPath.Value, limits, entries, snapshots);
            if (fatal is not null)
            {
                return Fail(fatal);
            }

            keep = true;
            return new ZipOpenResult(new RarArchiveSource(held, api, finalPath.Value, archive!, entries, snapshots), null);
        }
        finally
        {
            if (!keep)
            {
                held.Dispose();
            }
        }
    }

    // 手順10: 内容読み取り用に開く。ヘッダーは読まない。
    public RarSessionOpenResult OpenSession()
    {
        var callback = new UnrarCallbackState();
        UnrarHeaderBuffer? buffer = null;
        var keep = false;
        try
        {
            buffer = new UnrarHeaderBuffer();
            var handle = OpenArchive(_api, _finalPath, UnrarNative.OpenModeExtract, callback, out var openResult, out _);
            ThrowIfCallbackFailed(_api, handle, callback);
            if (handle == 0)
            {
                return new RarSessionOpenResult(null, new FatalError(FatalKind.ArchiveOpenFailed, Detail: Describe(openResult, callback)));
            }

            keep = true;
            return new RarSessionOpenResult(new RarReadSession(_api, handle, callback, buffer, _snapshots, _entries), null);
        }
        finally
        {
            if (!keep)
            {
                buffer?.Dispose();
                callback.Dispose();
            }
        }
    }

    public void Dispose() => _held.Dispose();

    internal static unsafe nint OpenArchive(IUnrarApi api, string path, uint mode, UnrarCallbackState callback, out int openResult, out uint flags)
    {
        callback.Reset();
        fixed (char* name = path)
        {
            var data = new UnrarNative.RAROpenArchiveDataEx
            {
                ArcNameW = name,
                OpenMode = mode,
                Callback = UnrarNative.CallbackPointer,
                UserData = callback.UserData,
            };
            var handle = api.Open(&data);
            openResult = (int)data.OpenResult;
            flags = data.Flags;
            return handle;
        }
    }

    // open 中のコールバックの例外は、開けたハンドルを閉じてから再送出する。
    private static void ThrowIfCallbackFailed(IUnrarApi api, nint handle, UnrarCallbackState callback)
    {
        if (callback.HasException)
        {
            if (handle != 0)
            {
                api.Close(handle);
            }

            callback.ThrowIfFailed();
        }
    }

    internal static string Describe(int code, UnrarCallbackState callback) =>
        callback.Describe() is { } reason ? $"{UnrarNative.ErrorName(code)}、{reason}" : UnrarNative.ErrorName(code);

    // 一覧用に開き、ボリュームのフラグを確かめてから全ヘッダーを列挙する。一覧用のハンドルは成否にかかわらず閉じる。
    // close の失敗は、残りの資源を解放した後に例外として伝える (internal_error)。元の失敗を処理している途中なら元の失敗を優先する (docs/ARCHITECTURE.md#lifetimes)。
    private static (RarArchiveInfo? Archive, FatalError? Fatal) List(IUnrarApi api, string finalPath, Limits limits, List<ZipEntryInfo> entries, List<RarHeaderSnapshot> snapshots)
    {
        using var callback = new UnrarCallbackState();
        using var buffer = new UnrarHeaderBuffer();
        var handle = OpenArchive(api, finalPath, UnrarNative.OpenModeListIncludingSplit, callback, out var openResult, out var flags);
        ThrowIfCallbackFailed(api, handle, callback);
        if (handle == 0)
        {
            var encrypted = callback.PasswordRequested || openResult == UnrarNative.ErarMissingPassword;
            return (null, new FatalError(encrypted ? FatalKind.ArchiveEncrypted : FatalKind.ArchiveOpenFailed, Detail: Describe(openResult, callback)));
        }

        FatalError? fatal;
        try
        {
            // ボリューム (分割の巻) は列挙せずに拒否する (docs/spec/rar.md#listing。第1巻・途中の巻は列挙し終えられない)。
            fatal = (flags & UnrarNative.ArchiveVolume) != 0
                ? new FatalError(FatalKind.ArchiveMultiVolume)
                : Enumerate(api, handle, callback, buffer, limits, entries, snapshots);
        }
        catch
        {
            api.Close(handle);
            throw;
        }

        var closed = api.Close(handle);
        if (fatal is null && closed != UnrarNative.ErarSuccess)
        {
            throw new InvalidOperationException($"一覧用の RAR のハンドルを閉じられません ({UnrarNative.ErrorName(closed)})");
        }

        return fatal is null
            ? (new RarArchiveInfo((flags & UnrarNative.ArchiveSolid) != 0, (flags & UnrarNative.ArchiveEncryptedHeaders) != 0), null)
            : (null, fatal);
    }

    private static FatalError? Enumerate(IUnrarApi api, nint handle, UnrarCallbackState callback, UnrarHeaderBuffer buffer, Limits limits,
        List<ZipEntryInfo> entries, List<RarHeaderSnapshot> snapshots)
    {
        var budget = new EntryBudget(limits);
        for (var index = 0; ; index++)
        {
            callback.Reset();
            var read = buffer.Read(api, handle);
            callback.ThrowIfFailed();
            if (read == UnrarNative.ErarEndArchive)
            {
                return null;
            }

            if (read != UnrarNative.ErarSuccess)
            {
                return new FatalError(FatalKind.ArchiveUnreadable, Detail: Describe(read, callback));
            }

            // 列挙中の上限 (docs/spec/rar.md#limits): 名前を文字列にして保持する前に、バッファ上の長さで検査する。
            // バッファを使い切った名前は切り詰めの可能性があるので名前長の上限違反とする。
            var isDirectory = buffer.IsDirectory;
            var nameLength = buffer.NameFillsBuffer ? int.MaxValue : buffer.NameLength + (isDirectory ? 1 : 0);
            if (budget.Add(nameLength) is { } exceeded)
            {
                return new FatalError(exceeded, new ZipEntryRef(index, isDirectory ? buffer.Name + "\\" : buffer.Name));
            }

            var snapshot = buffer.Snapshot();
            snapshots.Add(snapshot);
            entries.Add(ToInfo(index, snapshot));

            callback.Reset();
            var skipped = api.ProcessFile(handle, UnrarNative.RarSkip);
            callback.ThrowIfFailed();
            if (skipped != UnrarNative.ErarSuccess)
            {
                return new FatalError(FatalKind.ArchiveUnreadable, Detail: Describe(skipped, callback));
            }
        }
    }

    // DLL の値を Core のエントリ情報に変換する。展開サイズが long に収まらない値は、宣言展開量の上限で必ず FATAL になる値にする。
    // 未知のハッシュの種類は「ハッシュ無し」とし、ファイルエントリなら拒否される (安全側)。
    private static ZipEntryInfo ToInfo(int index, RarHeaderSnapshot snapshot)
    {
        var flags = snapshot.Flags;
        var isDirectory = snapshot.IsDirectory;
        return new ZipEntryInfo(
            index,
            isDirectory ? snapshot.Name + "\\" : snapshot.Name,
            snapshot.UnpackedSize > long.MaxValue ? long.MaxValue : (long)snapshot.UnpackedSize,
            0,
            (flags & UnrarNative.HeaderEncrypted) != 0,
            snapshot.Crc,
            new RarEntryMetadata(
                snapshot.HostOs switch
                {
                    UnrarNative.HostWindows => RarHostOs.Windows,
                    UnrarNative.HostUnix => RarHostOs.Unix,
                    _ => RarHostOs.Other,
                },
                snapshot.Attributes,
                isDirectory,
                (flags & UnrarNative.HeaderSolid) != 0,
                (flags & (UnrarNative.HeaderSplitBefore | UnrarNative.HeaderSplitAfter)) != 0,
                snapshot.HashType switch
                {
                    UnrarNative.HashCrc32 => RarHashType.Crc32,
                    UnrarNative.HashBlake2 => RarHashType.Blake2,
                    _ => RarHashType.None,
                },
                (long)snapshot.DictionarySize * 1024,
                snapshot.RedirectionType));
    }

    private static int ReadHead(FileStream held, byte[] head)
    {
        var total = 0;
        while (total < head.Length)
        {
            var n = RandomAccess.Read(held.SafeFileHandle, head.AsSpan(total), total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static bool IsRarSignature(ReadOnlySpan<byte> head) => head.StartsWith(Rar4Signature) || head.StartsWith(Rar5Signature);

    private static ZipOpenResult Fail(FatalError fatal) => new(null, fatal);

    private static bool IsOpenFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or SecurityException;
}
