using System.Runtime.InteropServices;

namespace Unextract.Windows.Rar;

// ヘッダーの照合に使う生の値 (docs/spec/rar.md#session の照合項目: 名前、ディレクトリのフラグ (Flags に含む)、展開サイズ、作成元 OS、属性、
// フラグ、ハッシュの種類と値、辞書サイズ、リダイレクトの種類)。Windows 側のセッションだけが持ち、Core へは出さない。
internal sealed class RarHeaderSnapshot(string name, uint flags, ulong unpackedSize, uint hostOs, uint attributes, uint crc, uint hashType, byte[] hash, uint dictionarySize, uint redirectionType)
{
    public string Name { get; } = name;

    public uint Flags { get; } = flags;

    public ulong UnpackedSize { get; } = unpackedSize;

    public uint HostOs { get; } = hostOs;

    public uint Attributes { get; } = attributes;

    public uint Crc { get; } = crc;

    public uint HashType { get; } = hashType;

    public byte[] Hash { get; } = hash;

    public uint DictionarySize { get; } = dictionarySize;

    public uint RedirectionType { get; } = redirectionType;

    public bool IsDirectory => (Flags & UnrarNative.HeaderDirectory) != 0;

    // Crc はハッシュの種類が CRC-32 のときだけ値として比べる。DLL は種類によらず FileCRC に内部の値を詰めるが、CRC-32 を持たないエントリ
    // (RAR5 のディレクトリなど) ではその値が初期化されていない (unrarsrc 7.2.3 の HashValue::Init(HASH_NONE) は CRC32 を設定しない)。
    // ハンドルの最初のヘッダーでは未初期化のメモリ、以降は直前のヘッダーの値になり、列挙と内容読み取りで食い違う (2026-10-09 に観測。
    // docs/RATIONALE.md#rar-dll-usage)。Hash は BLAKE2 のときだけ DLL が書き、それ以外は Read で消去した 0 のままなので、種類によらず比べる。
    public bool Matches(RarHeaderSnapshot other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Flags == other.Flags
        && UnpackedSize == other.UnpackedSize
        && HostOs == other.HostOs
        && Attributes == other.Attributes
        && (HashType != UnrarNative.HashCrc32 || Crc == other.Crc)
        && HashType == other.HashType
        && Hash.AsSpan().SequenceEqual(other.Hash)
        && DictionarySize == other.DictionarySize
        && RedirectionType == other.RedirectionType;
}

// 1つの DLL のハンドルが使うヘッダー構造体と名前のバッファ (NativeMemory)。ハンドルごとに確保して使い回し、単一スレッドで使う。
internal sealed unsafe class UnrarHeaderBuffer : IDisposable
{
    private UnrarNative.RARHeaderDataEx* _header;
    private char* _name;

    public UnrarHeaderBuffer()
    {
        _header = (UnrarNative.RARHeaderDataEx*)NativeMemory.AllocZeroed((nuint)sizeof(UnrarNative.RARHeaderDataEx));
        _name = (char*)NativeMemory.AllocZeroed(UnrarNative.NameBufferChars * sizeof(char));
    }

    // 名前の長さ (UTF-16)。
    public int NameLength
    {
        get
        {
            var length = new ReadOnlySpan<char>(_name, UnrarNative.NameBufferChars).IndexOf('\0');
            return length < 0 ? UnrarNative.NameBufferChars : length;
        }
    }

    // 名前がバッファを使い切った (切り詰められた可能性がある)。
    public bool NameFillsBuffer => NameLength >= UnrarNative.NameBufferChars - 1;

    public bool IsDirectory => (_header->Flags & UnrarNative.HeaderDirectory) != 0;

    public string Name => new(_name, 0, NameLength);

    public int Read(IUnrarApi api, nint archive)
    {
        ObjectDisposedException.ThrowIf(_header is null, this);
        NativeMemory.Clear(_header, (nuint)sizeof(UnrarNative.RARHeaderDataEx));
        _name[0] = '\0';
        _header->FileNameEx = _name;
        _header->FileNameExSize = UnrarNative.NameBufferChars;
        return api.ReadHeader(archive, _header);
    }

    public RarHeaderSnapshot Snapshot()
    {
        var header = _header;
        return new RarHeaderSnapshot(
            Name,
            header->Flags,
            ((ulong)header->UnpSizeHigh << 32) | header->UnpSize,
            header->HostOS,
            header->FileAttr,
            header->FileCRC,
            header->HashType,
            new ReadOnlySpan<byte>(header->Hash, 32).ToArray(),
            header->DictSize,
            header->RedirType);
    }

    public void Dispose()
    {
        if (_header is not null)
        {
            NativeMemory.Free(_header);
            _header = null;
        }

        if (_name is not null)
        {
            NativeMemory.Free(_name);
            _name = null;
        }
    }
}
