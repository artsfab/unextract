using System.IO;
using System.IO.Hashing;
using System.Text;

namespace Unextract.Core.Tests.Fixtures;

// テスト専用の Stored (無圧縮) RAR4 / RAR5 生成器。製品のパーサではなく、ヘッダーの値を自由に操作した fixture を作るためだけに使う (ZipPatcher と同じ位置づけ。docs/TESTING.md#rar)。
// 形式は rarlab の technote (RAR 5.0 archive format / RAR 4.x の旧 technote) に従う。圧縮データは作れないため、
// 圧縮・BLAKE2 の実値・NTFS ストリーム・実物の Solid・分割・SFX は WinRAR の Rar.exe で作った実物 fixture で確かめる (docs/TESTING.md#rar)。
internal sealed class Rar5File
{
    public required string Name { get; init; }

    public byte[] Data { get; init; } = [];

    public bool IsDirectory { get; init; }

    // 0 = Windows, 1 = Unix
    public int HostOs { get; init; }

    public uint Attributes { get; init; } = 0x20;

    public long? DeclaredSize { get; init; }

    public uint? Crc { get; init; }

    public bool OmitCrc { get; init; }

    public bool UnknownSize { get; init; }

    public bool SolidFlag { get; init; }

    // compression info を直接指定する (method・辞書サイズの試験用)。
    public uint? CompressionInfo { get; init; }

    public bool SplitBefore { get; init; }

    public bool SplitAfter { get; init; }

    // 生の名前バイト (UTF-8 以外を試すとき)。
    public byte[]? RawName { get; init; }

    // FHEXTRA_REDIR: 1 Unix symlink, 2 Windows symlink, 3 junction, 4 hardlink, 5 file copy
    public int RedirType { get; init; }

    public string RedirTarget { get; init; } = "target.txt";

    // FHEXTRA_CRYPT を付ける (中身は平文のまま)。1 = password check あり (非0)、2 = password check なし (flags 0)。
    public int EncryptRecord { get; init; }

    // 32バイトの BLAKE2sp ハッシュ extra を付ける (値は不正でよい)。
    public byte[]? Blake2 { get; init; }
}

internal static class Rar5Writer
{
    public static readonly byte[] Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    public static byte[] Build(IEnumerable<Rar5File> files, uint archiveFlags = 0, bool writeEnd = true, Action<MemoryStream>? afterFiles = null, bool corruptFirstHeaderCrc = false, IEnumerable<(string Name, byte[] Data)>? serviceHeaders = null)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);

        // Main archive header
        var main = new List<byte>();
        VInt(main, 1);            // type
        VInt(main, 0);            // header flags
        VInt(main, archiveFlags); // archive flags (0x1 volume, 0x4 solid)
        if ((archiveFlags & 0x2) != 0)
        {
            VInt(main, 1);
        }

        WriteBlock(ms, main, corrupt: false);

        var first = true;
        foreach (var f in files)
        {
            WriteFile(ms, f, corruptFirstHeaderCrc && first);
            first = false;
        }

        foreach (var (name, data) in serviceHeaders ?? [])
        {
            WriteFile(ms, new Rar5File { Name = name, Data = data }, false, headerType: 3);
        }

        afterFiles?.Invoke(ms);

        if (writeEnd)
        {
            var end = new List<byte>();
            VInt(end, 5);
            VInt(end, 0);
            VInt(end, 0);
            WriteBlock(ms, end, corrupt: false);
        }

        return ms.ToArray();
    }

    private static void WriteFile(MemoryStream ms, Rar5File f, bool corrupt, int headerType = 2)
    {
        var data = f.IsDirectory ? [] : f.Data;
        var extra = new List<byte>();
        if (f.RedirType != 0)
        {
            var rec = new List<byte>();
            VInt(rec, 5);
            VInt(rec, (ulong)f.RedirType);
            VInt(rec, 0);
            var target = Encoding.UTF8.GetBytes(f.RedirTarget);
            VInt(rec, (ulong)target.Length);
            rec.AddRange(target);
            VInt(extra, (ulong)rec.Count);
            extra.AddRange(rec);
        }

        if (f.EncryptRecord != 0)
        {
            // version 0, flags (0x1 = password check present), lg2count 15, salt 16, IV 16, [check value 8 + checksum 4]
            var rec = new List<byte>();
            VInt(rec, 1);
            VInt(rec, 0);
            VInt(rec, f.EncryptRecord == 1 ? 1u : 0u);
            rec.Add(15);
            rec.AddRange(Enumerable.Repeat((byte)0x11, 16));
            rec.AddRange(Enumerable.Repeat((byte)0x22, 16));
            if (f.EncryptRecord == 1)
            {
                rec.AddRange(Enumerable.Repeat((byte)0x33, 12));
            }
            VInt(extra, (ulong)rec.Count);
            extra.AddRange(rec);
        }

        if (f.Blake2 is { } hash)
        {
            var rec = new List<byte>();
            VInt(rec, 2);
            VInt(rec, 0);
            rec.AddRange(hash);
            VInt(extra, (ulong)rec.Count);
            extra.AddRange(rec);
        }

        var h = new List<byte>();
        VInt(h, (ulong)headerType);
        ulong headerFlags = 0x2; // data area present
        if (extra.Count > 0)
        {
            headerFlags |= 0x1;
        }

        if (f.SplitBefore)
        {
            headerFlags |= 0x8;
        }

        if (f.SplitAfter)
        {
            headerFlags |= 0x10;
        }

        VInt(h, headerFlags);
        if (extra.Count > 0)
        {
            VInt(h, (ulong)extra.Count);
        }

        VInt(h, (ulong)data.Length);

        ulong fileFlags = 0;
        if (f.IsDirectory)
        {
            fileFlags |= 0x1;
        }

        if (!f.OmitCrc && !f.IsDirectory)
        {
            fileFlags |= 0x4;
        }

        if (f.UnknownSize)
        {
            fileFlags |= 0x8;
        }

        VInt(h, fileFlags);
        VInt(h, (ulong)(f.DeclaredSize ?? data.Length));
        VInt(h, f.Attributes);
        if ((fileFlags & 0x4) != 0)
        {
            var crc = f.Crc ?? Crc32.HashToUInt32(data);
            h.AddRange(BitConverter.GetBytes(crc));
        }

        VInt(h, f.CompressionInfo ?? (f.SolidFlag ? 0x40u : 0u)); // compression info: version 0, method 0 (store)
        VInt(h, (ulong)f.HostOs);
        var name = f.RawName ?? Encoding.UTF8.GetBytes(f.Name);
        VInt(h, (ulong)name.Length);
        h.AddRange(name);
        h.AddRange(extra);
        WriteBlock(ms, h, corrupt);
        ms.Write(data);
    }

    private static void WriteBlock(MemoryStream ms, List<byte> header, bool corrupt)
    {
        var size = new List<byte>();
        VInt(size, (ulong)header.Count);
        var crcInput = size.Concat(header).ToArray();
        var crc = Crc32.HashToUInt32(crcInput);
        if (corrupt)
        {
            crc ^= 0xFFFF;
        }

        ms.Write(BitConverter.GetBytes(crc));
        ms.Write(crcInput);
    }

    public static void VInt(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }
}

internal sealed class Rar4File
{
    public required string Name { get; init; }

    public byte[] Data { get; init; } = [];

    public bool IsDirectory { get; init; }

    // 2 = Win32, 3 = Unix
    public byte HostOs { get; init; } = 2;

    public uint Attributes { get; init; } = 0x20;

    public uint? DeclaredSize { get; init; }

    public uint? Crc { get; init; }

    public bool Password { get; init; }

    public bool Solid { get; init; }

    public bool SplitAfter { get; init; }

    // RAR4 の Unicode 名 (flag 0x200)。「ASCII の代替名 + 0 + 独自符号化」で書く。
    public bool UnicodeName { get; init; }

    public byte[]? RawName { get; init; }
}

internal static class Rar4Writer
{
    public static readonly byte[] Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    public static byte[] Build(IEnumerable<Rar4File> files, ushort archiveFlags = 0, bool writeEnd = true, byte[]? extraHeaderBlock = null)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);

        var main = new List<byte> { 0x73 };
        main.AddRange(BitConverter.GetBytes(archiveFlags));
        main.AddRange(BitConverter.GetBytes((ushort)13));
        main.AddRange(new byte[6]);
        WriteBlock(ms, main);

        if (extraHeaderBlock is not null)
        {
            ms.Write(extraHeaderBlock);
        }

        foreach (var f in files)
        {
            var data = f.IsDirectory ? [] : f.Data;
            var name = f.RawName ?? (f.UnicodeName ? EncodeUnicodeName(f.Name) : Encoding.ASCII.GetBytes(f.Name));
            ushort flags = 0x8000;
            if (f.IsDirectory)
            {
                flags |= 0xE0;
            }

            if (f.Password)
            {
                flags |= 0x04;
            }

            if (f.Solid)
            {
                flags |= 0x10;
            }

            if (f.SplitAfter)
            {
                flags |= 0x02;
            }

            if (f.UnicodeName)
            {
                flags |= 0x200;
            }

            var h = new List<byte> { 0x74 };
            h.AddRange(BitConverter.GetBytes(flags));
            h.AddRange(BitConverter.GetBytes((ushort)(32 + name.Length)));
            h.AddRange(BitConverter.GetBytes((uint)data.Length));              // PACK_SIZE
            h.AddRange(BitConverter.GetBytes(f.DeclaredSize ?? (uint)data.Length)); // UNP_SIZE
            h.Add(f.HostOs);
            h.AddRange(BitConverter.GetBytes(f.Crc ?? Crc32.HashToUInt32(data)));
            h.AddRange(BitConverter.GetBytes(0x5A210000u)); // DOS time
            h.Add(29);   // UNP_VER
            h.Add(0x30); // METHOD: store
            h.AddRange(BitConverter.GetBytes((ushort)name.Length));
            h.AddRange(BitConverter.GetBytes(f.Attributes));
            h.AddRange(name);
            WriteBlock(ms, h);
            ms.Write(data);
        }

        if (writeEnd)
        {
            var end = new List<byte> { 0x7B };
            end.AddRange(BitConverter.GetBytes((ushort)0x4000));
            end.AddRange(BitConverter.GetBytes((ushort)7));
            WriteBlock(ms, end);
        }

        return ms.ToArray();
    }

    // 旧形式のコメントヘッダー (0x75) など、SharpCompress が扱わない種類のブロックを作る。
    public static byte[] RawBlock(byte type, ushort flags, byte[] body)
    {
        var h = new List<byte> { type };
        h.AddRange(BitConverter.GetBytes(flags));
        h.AddRange(BitConverter.GetBytes((ushort)(7 + body.Length)));
        h.AddRange(body);
        using var ms = new MemoryStream();
        WriteBlock(ms, h);
        return ms.ToArray();
    }

    private static void WriteBlock(MemoryStream ms, List<byte> headerFromType)
    {
        var crc = (ushort)(Crc32.HashToUInt32(headerFromType.ToArray()) & 0xFFFF);
        ms.Write(BitConverter.GetBytes(crc));
        ms.Write(headerFromType.ToArray());
    }

    // ASCII 代替名 + 0 + highByte + (4文字ごとのフラグ 0xAA = 全文字 2バイト表現) + 各文字 (lo, hi)
    private static byte[] EncodeUnicodeName(string name)
    {
        var result = new List<byte>();
        foreach (var c in name)
        {
            result.Add(c < 0x80 ? (byte)c : (byte)'_');
        }

        result.Add(0);
        result.Add(0); // highByte
        for (var i = 0; i < name.Length; i++)
        {
            if (i % 4 == 0)
            {
                result.Add(0xAA);
            }

            result.Add((byte)(name[i] & 0xFF));
            result.Add((byte)(name[i] >> 8));
        }

        return result.ToArray();
    }
}
