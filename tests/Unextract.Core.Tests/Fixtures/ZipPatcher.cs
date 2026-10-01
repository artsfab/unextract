using System.Buffers.Binary;

namespace Unextract.Core.Tests.Fixtures;

// テスト専用の fixture 生成器: ZipFixture で作った正常な ZIP の Central Directory と EOCD を
// 書き換える。製品コードの ZIP パーサを兼ねない (PLAN.md §1)。
// 対象は ZIP64 EOCD を持たない ZIP だけ。Local Header とデータを書き換えるのは、暗号化フラグ・圧縮方式
// (Central Directory と同じ値にそろえる) と、データの破損 (CorruptData) だけ。
internal sealed class ZipPatcher
{
    private const uint EocdSignature = 0x06054B50;
    private const uint CentralHeaderSignature = 0x02014B50;
    private const int EocdLength = 22;
    private const int CentralHeaderLength = 46;
    private const ushort Zip64ExtraId = 0x0001;

    private readonly byte[] _body;
    private readonly List<Record> _records = [];
    private byte[] _leading = [];
    private bool _adjustOffsets;
    private byte[] _trailing = [];

    public ZipPatcher(byte[] zip)
    {
        var eocd = FindEocd(zip);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(eocd + 10));
        var cdOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 16));
        if (cdOffset == -1 || count == ushort.MaxValue)
        {
            throw new NotSupportedException("ZIP64 EOCD is not supported by the patcher");
        }

        _body = zip[..cdOffset];
        var position = cdOffset;
        for (var i = 0; i < count; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(position)) != CentralHeaderSignature)
            {
                throw new InvalidDataException("unexpected central directory layout");
            }

            var header = zip[position..(position + CentralHeaderLength)];
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            position += CentralHeaderLength;
            var name = zip[position..(position + nameLength)];
            position += nameLength;
            var extra = zip[position..(position + extraLength)];
            position += extraLength;
            var comment = zip[position..(position + commentLength)];
            position += commentLength;
            _records.Add(new Record(header, name, extra, comment));
        }
    }

    public int Count => _records.Count;

    public ushort GetFlags(int index) => _records[index].Flags;

    // 名前のバイト列と UTF-8 フラグ (general purpose bit 11) を置き換える。
    public ZipPatcher SetName(int index, byte[] name, bool utf8Flag)
    {
        var record = _records[index];
        record.Name = name;
        var flags = record.Flags;
        record.Flags = (ushort)(utf8Flag ? flags | 0x0800 : flags & ~0x0800);
        return this;
    }

    // 宣言展開量 (Length) を置き換える。4 GiB 以上は ZIP64 extra で表す。
    public ZipPatcher SetDeclaredLength(int index, long length)
    {
        var record = _records[index];
        var compressed = record.CompressedSize;
        if (length >= uint.MaxValue)
        {
            SetZip64Sizes(record, length, compressed);
        }
        else
        {
            record.Extra = RemoveZip64Extra(record.Extra);
            record.CompressedSizeField = (uint)compressed;
            record.UncompressedSizeField = (uint)length;
        }

        return this;
    }

    // 実際のサイズのまま、サイズを ZIP64 extra で表すエントリにする (強制 ZIP64)。
    public ZipPatcher ForceZip64(int index)
    {
        var record = _records[index];
        SetZip64Sizes(record, record.UncompressedSizeField, record.CompressedSizeField);
        return this;
    }

    // Central Directory の CRC-32 を置き換える (Local Header の CRC は変えない)。
    public ZipPatcher SetCrc32(int index, uint crc)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_records[index].Header.AsSpan(16), crc);
        return this;
    }

    public uint GetCrc32(int index) => BinaryPrimitives.ReadUInt32LittleEndian(_records[index].Header.AsSpan(16));

    public long GetCompressedSize(int index) => _records[index].CompressedSize;

    // Central Directory の圧縮サイズを置き換える (データは変えない)。
    public ZipPatcher SetCompressedSize(int index, uint size)
    {
        _records[index].CompressedSizeField = size;
        return this;
    }

    // 暗号化フラグ (general purpose bit 0) を Central Directory と Local Header に立てる。データは平文のまま。
    public ZipPatcher SetEncryptedFlag(int index)
    {
        var record = _records[index];
        record.Flags = (ushort)(record.Flags | 0x0001);
        var local = LocalHeaderOffset(index);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(_body.AsSpan(local + 6));
        BinaryPrimitives.WriteUInt16LittleEndian(_body.AsSpan(local + 6), (ushort)(flags | 0x0001));
        return this;
    }

    // 圧縮方式を Central Directory と Local Header の両方で置き換える。
    public ZipPatcher SetCompressionMethod(int index, ushort method)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_records[index].Header.AsSpan(10), method);
        BinaryPrimitives.WriteUInt16LittleEndian(_body.AsSpan(LocalHeaderOffset(index) + 8), method);
        return this;
    }

    // エントリの圧縮データの offset バイト目を xor で書き換える。
    public ZipPatcher CorruptData(int index, int offset, byte xor = 0xFF)
    {
        var local = LocalHeaderOffset(index);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(_body.AsSpan(local + 26));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(_body.AsSpan(local + 28));
        _body[local + 30 + nameLength + extraLength + offset] ^= xor;
        return this;
    }

    // 先頭にデータを付ける。adjustOffsets なら SFX と同じく Central Directory のオフセットを調整する。
    public ZipPatcher Prepend(byte[] data, bool adjustOffsets)
    {
        _leading = data;
        _adjustOffsets = adjustOffsets;
        return this;
    }

    public ZipPatcher Append(byte[] data)
    {
        _trailing = data;
        return this;
    }

    public byte[] ToArray()
    {
        var shift = _adjustOffsets ? (uint)_leading.Length : 0;
        using var output = new MemoryStream();
        output.Write(_leading);
        output.Write(_body);

        var cdStart = output.Length - _leading.Length + shift;
        foreach (var record in _records)
        {
            var header = (byte[])record.Header.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)record.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), (ushort)record.Extra.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), (ushort)record.Comment.Length);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(42));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(42), offset + shift);
            output.Write(header);
            output.Write(record.Name);
            output.Write(record.Extra);
            output.Write(record.Comment);
        }

        var cdSize = output.Length - _leading.Length - _body.Length;
        var eocd = new byte[EocdLength];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, EocdSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(8), (ushort)_records.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(10), (ushort)_records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(12), (uint)cdSize);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(16), (uint)cdStart);
        output.Write(eocd);
        output.Write(_trailing);
        return output.ToArray();
    }

    private int LocalHeaderOffset(int index) =>
        (int)BinaryPrimitives.ReadUInt32LittleEndian(_records[index].Header.AsSpan(42));

    private static void SetZip64Sizes(Record record, long uncompressed, long compressed)
    {
        var block = new byte[4 + 16];
        BinaryPrimitives.WriteUInt16LittleEndian(block, Zip64ExtraId);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), 16);
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(4), uncompressed);
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(12), compressed);
        record.Extra = [.. block, .. RemoveZip64Extra(record.Extra)];
        record.UncompressedSizeField = uint.MaxValue;
        record.CompressedSizeField = uint.MaxValue;
    }

    private static byte[] RemoveZip64Extra(byte[] extra)
    {
        var kept = new List<byte>();
        var position = 0;
        while (position + 4 <= extra.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(position));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(position + 2));
            var end = position + 4 + size;
            if (id != Zip64ExtraId)
            {
                kept.AddRange(extra[position..end]);
            }

            position = end;
        }

        return [.. kept];
    }

    private static int FindEocd(byte[] zip)
    {
        for (var i = zip.Length - EocdLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == EocdSignature)
            {
                return i;
            }
        }

        throw new InvalidDataException("EOCD not found");
    }

    private sealed class Record(byte[] header, byte[] name, byte[] extra, byte[] comment)
    {
        public byte[] Header { get; } = header;

        public byte[] Name { get; set; } = name;

        public byte[] Extra { get; set; } = extra;

        public byte[] Comment { get; } = comment;

        public ushort Flags
        {
            get => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(8));
            set => BinaryPrimitives.WriteUInt16LittleEndian(Header.AsSpan(8), value);
        }

        public uint CompressedSizeField
        {
            get => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(20));
            set => BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(20), value);
        }

        public uint UncompressedSizeField
        {
            get => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(24));
            set => BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(24), value);
        }

        // ZIP64 extra があればその値、なければ 32 ビットのフィールド。
        public long CompressedSize
        {
            get
            {
                if (CompressedSizeField != uint.MaxValue)
                {
                    return CompressedSizeField;
                }

                var position = 0;
                while (position + 4 <= Extra.Length)
                {
                    var id = BinaryPrimitives.ReadUInt16LittleEndian(Extra.AsSpan(position));
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(Extra.AsSpan(position + 2));
                    if (id == Zip64ExtraId)
                    {
                        return BinaryPrimitives.ReadInt64LittleEndian(Extra.AsSpan(position + 4 + 8));
                    }

                    position += 4 + size;
                }

                throw new InvalidDataException("ZIP64 extra not found");
            }
        }
    }
}
