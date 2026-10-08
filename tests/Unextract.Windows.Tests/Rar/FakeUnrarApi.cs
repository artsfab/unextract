using Unextract.Windows.Rar;

namespace Unextract.Windows.Tests.Rar;

// 偽の DLL のヘッダー。既定は Windows で作られた CRC-32 付きのファイル。
internal sealed record FakeHeader(string Name)
{
    public uint Flags { get; init; }

    public ulong UnpackedSize { get; init; }

    public uint HostOs { get; init; } = UnrarNative.HostWindows;

    public uint Attributes { get; init; } = 0x20;

    public uint Crc { get; init; }

    public uint HashType { get; init; } = UnrarNative.HashCrc32;

    public byte[] Hash { get; init; } = new byte[32];

    public uint DictionarySizeKiB { get; init; } = 128;

    public uint RedirectionType { get; init; }

    // RAR_TEST で渡すデータのチャンク。
    public byte[][] Chunks { get; init; } = [];

    public static FakeHeader File(string name, byte[] data, int chunk = 4 * 1024 * 1024) => new(name)
    {
        UnpackedSize = (ulong)data.LongLength,
        Crc = System.IO.Hashing.Crc32.HashToUInt32(data),
        Chunks = data.Chunk(Math.Max(1, chunk)).ToArray(),
    };

    public static FakeHeader Directory(string name) => new(name) { Flags = UnrarNative.HeaderDirectory, Attributes = 0x10 };
}

// 台本で動く偽の UnRAR.dll (ネイティブ呼び出しの差し替え口。docs/ARCHITECTURE.md#dependencies の IUnrarApi)。DLL 不要で、製品のソース・セッション・コールバックの処理を通す。
// ハンドルは open ごとに番号を振り、それぞれが自分の読み取り位置を持つ。Calls に呼び出しを記録する。
internal sealed unsafe class FakeUnrarApi : IUnrarApi
{
    private readonly Dictionary<nint, Cursor> _handles = [];
    private nint _next = 100;

    public FakeUnrarApi(params FakeHeader[] headers)
    {
        Headers = [.. headers];
    }

    public List<FakeHeader> Headers { get; }

    public List<string> Calls { get; } = [];

    public uint ArchiveFlags { get; set; }

    // open の失敗 (mode → OpenResult)。
    public Dictionary<uint, int> OpenFailures { get; } = [];

    // open の中でパスワードを要求する。
    public bool RequestPasswordOnOpen { get; set; }

    // 内容読み取り用の open で使うヘッダー (null なら Headers)。アーカイブの変化の再現に使う。
    public List<FakeHeader>? ExtractHeaders { get; set; }

    // ヘッダーの読み取りの失敗: (mode, index) → コード。
    public Dictionary<(uint Mode, int Index), int> ReadFailures { get; } = [];

    // RAR_SKIP の失敗: (mode, index) → コード。
    public Dictionary<(uint Mode, int Index), int> SkipFailures { get; } = [];

    // RAR_TEST の失敗 (データを全て渡した後): index → コード。
    public Dictionary<int, int> TestFailures { get; } = [];

    // RAR_TEST の前に呼ぶコールバック (P1, P2 を直接指定する。範囲外の長さやパスワードの要求の再現)。
    public Dictionary<int, (uint Message, nint P1, nint P2)> TestPreludes { get; } = [];

    // 中止 (-1) の後もデータのコールバックを1回呼ぶ (中止後の呼び出しの再現)。
    public bool CallAgainAfterAbort { get; set; }

    // close の戻り値 (mode → コード)。
    public Dictionary<uint, int> CloseResults { get; } = [];

    public int VersionValue { get; set; } = UnrarNative.AdoptedVersion;

    public List<string> OpenedPaths { get; } = [];

    public int OpenHandles => _handles.Count;

    public nint Open(UnrarNative.RAROpenArchiveDataEx* data)
    {
        var path = new string(data->ArcNameW);
        OpenedPaths.Add(path);
        Calls.Add($"Open {data->OpenMode}");
        if (RequestPasswordOnOpen)
        {
            UnrarCallbackState.Dispatch(UnrarNative.CallbackNeedPasswordW, data->UserData, 0, 0);
        }

        if (OpenFailures.TryGetValue(data->OpenMode, out var failure))
        {
            data->OpenResult = (uint)failure;
            return 0;
        }

        data->OpenResult = 0;
        data->Flags = ArchiveFlags;
        var handle = _next++;
        _handles[handle] = new Cursor(data->OpenMode, data->UserData, data->OpenMode == UnrarNative.OpenModeExtract ? ExtractHeaders ?? Headers : Headers);
        return handle;
    }

    public int ReadHeader(nint archive, UnrarNative.RARHeaderDataEx* header)
    {
        var cursor = _handles[archive];
        var index = cursor.Next;
        Calls.Add($"Read {cursor.Mode} {index}");
        if (ReadFailures.TryGetValue((cursor.Mode, index), out var failure))
        {
            return failure;
        }

        if (index >= cursor.Headers.Count)
        {
            return UnrarNative.ErarEndArchive;
        }

        var h = cursor.Headers[index];
        header->Flags = h.Flags;
        header->UnpSize = (uint)h.UnpackedSize;
        header->UnpSizeHigh = (uint)(h.UnpackedSize >> 32);
        header->HostOS = h.HostOs;
        header->FileAttr = h.Attributes;
        header->FileCRC = h.Crc;
        header->HashType = h.HashType;
        for (var i = 0; i < 32; i++)
        {
            header->Hash[i] = h.Hash[i];
        }

        header->DictSize = h.DictionarySizeKiB;
        header->RedirType = h.RedirectionType;

        // DLL と同じく FileNameExSize に収まるように切り詰めて終端を付ける (wcsncpyz)。
        var capacity = (int)header->FileNameExSize;
        var length = Math.Min(h.Name.Length, capacity - 1);
        h.Name.AsSpan(0, length).CopyTo(new Span<char>(header->FileNameEx, capacity));
        header->FileNameEx[length] = '\0';

        cursor.Next++;
        cursor.Pending = index;
        return UnrarNative.ErarSuccess;
    }

    public int ProcessFile(nint archive, int operation)
    {
        var cursor = _handles[archive];
        var index = cursor.Pending ?? throw new InvalidOperationException("ヘッダーを読まずに ProcessFile を呼んだ");
        cursor.Pending = null;
        if (operation == UnrarNative.RarSkip)
        {
            Calls.Add($"Skip {cursor.Mode} {index}");
            return SkipFailures.TryGetValue((cursor.Mode, index), out var skipFailure) ? skipFailure : UnrarNative.ErarSuccess;
        }

        Assert.Equal(UnrarNative.RarTest, operation);
        Calls.Add($"Test {cursor.Mode} {index}");
        if (TestPreludes.TryGetValue(index, out var prelude))
        {
            if (UnrarCallbackState.Dispatch(prelude.Message, cursor.UserData, prelude.P1, prelude.P2) == -1)
            {
                return 21;
            }
        }

        foreach (var chunk in cursor.Headers[index].Chunks)
        {
            fixed (byte* p = chunk)
            {
                Calls.Add($"Chunk {index} {chunk.Length}");
                if (UnrarCallbackState.Dispatch(UnrarNative.CallbackProcessData, cursor.UserData, (nint)p, chunk.Length) == -1)
                {
                    if (CallAgainAfterAbort)
                    {
                        Calls.Add($"Again {index} {UnrarCallbackState.Dispatch(UnrarNative.CallbackProcessData, cursor.UserData, (nint)p, chunk.Length)}");
                    }

                    // DLL は中止を ERAR_UNKNOWN で返す (docs/RATIONALE.md#rar-dll-usage)。
                    return 21;
                }
            }
        }

        return TestFailures.TryGetValue(index, out var failure) ? failure : UnrarNative.ErarSuccess;
    }

    public int Close(nint archive)
    {
        var cursor = _handles[archive];
        _handles.Remove(archive);
        Calls.Add($"Close {cursor.Mode}");
        return CloseResults.TryGetValue(cursor.Mode, out var code) ? code : UnrarNative.ErarSuccess;
    }

    public int Version() => VersionValue;

    private sealed class Cursor(uint mode, nint userData, List<FakeHeader> headers)
    {
        public uint Mode { get; } = mode;

        public nint UserData { get; } = userData;

        public List<FakeHeader> Headers { get; } = headers;

        public int Next { get; set; }

        public int? Pending { get; set; }
    }
}

internal static class FakeRarFiles
{
    public static readonly byte[] Rar5Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    public static readonly byte[] Rar4Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    // 署名だけを持つ自作のファイル (中身は偽の DLL が台本で返す)。
    public static string Write(string directory, string name = "a.rar", byte[]? content = null)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content ?? [.. Rar5Signature, 0, 0, 0, 0]);
        return path;
    }

    public static UnrarLoadResult Loaded(FakeUnrarApi api) => new(api, null);
}
