using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Unextract.Core.Results;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;
using static Unextract.Core.Tests.TestHelpers;

namespace Unextract.Core.Tests;

// ZipArchive のアダプタと、実 ZIP を通した事前検証。fixture はメモリ上で作る。
public class ZipArchiveSourceTests
{
    private const long GiB = 1L << 30;

    private static readonly byte[] Hello = Encoding.ASCII.GetBytes("hello, unextract");

    private static ZipArchiveSource OpenBytes(byte[] zip)
    {
        var opened = ZipArchiveSource.Open(new MemoryStream(zip, writable: false));
        Assert.Null(opened.Fatal);
        return Assert.IsType<ZipArchiveSource>(opened.Source);
    }

    private static ZipPrevalidationResult ValidateBytes(byte[] zip, Limits? limits = null)
    {
        using var source = OpenBytes(zip);
        return Validate(source.Entries, limits);
    }

    private static List<ZipEntryInfo> ListBytes(byte[] zip)
    {
        using var source = OpenBytes(zip);
        return [.. source.Entries];
    }

    // テスト Z06 (復号の部分): フラグなしの CP437
    [Fact]
    public void Z06_UnflaggedCp437Name_IsDecodedAsCp437()
    {
        var zip = ZipFixture.Create([new FixtureEntry("café░.txt", Hello)], ZipFixture.Cp437);

        var entry = Assert.Single(ListBytes(zip));

        Assert.Equal("café░.txt", entry.FullName);
    }

    // テスト Z06 (復号の部分): UTF-8 フラグ付きの正しい UTF-8
    [Fact]
    public void Z06_FlaggedUtf8Name_IsDecodedAsUtf8()
    {
        var zip = ZipFixture.Create(new FixtureEntry("日本語/ファイル é.txt", Hello));

        var entry = Assert.Single(ListBytes(zip));

        Assert.Equal("日本語/ファイル é.txt", entry.FullName);
    }

    // テスト Z06 (復号の部分): フラグなしの UTF-8 バイト列は CP437 として読んだ別名になる
    [Fact]
    public void Z06_UnflaggedUtf8Bytes_AreDecodedAsCp437()
    {
        var utf8 = Encoding.UTF8.GetBytes("café.txt");
        var zip = new ZipPatcher(ZipFixture.Create(new FixtureEntry("x.txt", Hello)))
            .SetName(0, utf8, utf8Flag: false)
            .ToArray();

        var entry = Assert.Single(ListBytes(zip));

        Assert.Equal(ZipFixture.Cp437.GetString(utf8), entry.FullName);
        Assert.NotEqual("café.txt", entry.FullName);
    }

    // テスト Z07: UTF-8 フラグ付きで不正な UTF-8 → 復号名の U+FFFD で全体 FATAL
    [Fact]
    public void Z07_FlaggedInvalidUtf8_IsFatal()
    {
        var zip = new ZipPatcher(ZipFixture.Create(new FixtureEntry("ok.txt", Hello), new FixtureEntry("x.txt", Hello)))
            .SetName(1, [0x61, 0xFF, 0xFE, 0x2E, 0x74], utf8Flag: true)
            .ToArray();

        var result = ValidateBytes(zip);

        AssertFatal(result, FatalKind.NameContainsReplacementCharacter, 1);
        Assert.Contains("\\u{FFFD}", result.Fatal!.Entry!.DisplayName);
    }

    // テスト Z04 (実 ZIP): ExternalAttributes が symlink のエントリ
    [Fact]
    public void Z04_SymlinkEntryInRealZip_IsFatal()
    {
        var zip = ZipFixture.Create(
            new FixtureEntry("a.txt", Hello),
            new FixtureEntry("link", Encoding.ASCII.GetBytes("a.txt"), unchecked((int)0xA1FF0000)));

        AssertFatal(ValidateBytes(zip), FatalKind.UnsupportedEntryType, 1);
    }

    public static TheoryData<string> Z08Formats => ["stored", "deflate", "zip64-entry", "data-descriptor", "sfx", "trailing-garbage"];

    // テスト Z08 (読めること。分類は段階 C): 形式だけでは拒否しない
    [Theory]
    [MemberData(nameof(Z08Formats))]
    public void Z08_SupportedFormats_AreReadable(string format)
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("unextract ", 200)));
        var level = format == "stored" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
        var plain = ZipFixture.Create(
            [new FixtureEntry("d/", ExternalAttributes: 0x10), new FixtureEntry("d/a.txt", content, Level: level)],
            nonSeekable: format == "data-descriptor");
        byte[] zip = format switch
        {
            "zip64-entry" => new ZipPatcher(plain).ForceZip64(1).ToArray(),
            "sfx" => new ZipPatcher(plain).Prepend(new byte[4096], adjustOffsets: true).ToArray(),
            "trailing-garbage" => [.. plain, .. Encoding.ASCII.GetBytes("garbage after EOCD")],
            _ => plain,
        };
        if (format == "data-descriptor")
        {
            // ファイルエントリの general purpose bit 3 (Data Descriptor)
            Assert.NotEqual(0, new ZipPatcher(zip).GetFlags(1) & 0x08);
        }

        var result = ValidateBytes(zip);

        AssertPassed(result, 2);
        var file = result.Entries[1].Entry;
        Assert.Equal("d/a.txt", file.FullName);
        Assert.Equal((long)content.Length, file.Length);
        Assert.Equal(Crc32.HashToUInt32(content), file.Crc32);
        Assert.Equal(content, ReadEntry(zip, 1));
    }

    // テスト Z08: 65,536 件超 (ZIP64 EOCD)
    [Fact]
    public void Z08_MoreThan65535Entries_AreReadable()
    {
        const int count = 65_540;
        var zip = ZipFixture.Create(Enumerable.Range(0, count).Select(i => new FixtureEntry($"f{i}.txt")));
        Assert.True(zip.AsSpan().IndexOf([(byte)0x50, (byte)0x4B, (byte)0x06, (byte)0x06]) >= 0, "ZIP64 EOCD がない");

        AssertPassed(ValidateBytes(zip), count);
    }

    // テスト Z09: 先頭にデータを付けただけでオフセットを調整していない ZIP → 入力エラー
    [Fact]
    public void Z09_PrefixWithoutOffsetAdjustment_IsUnreadable()
    {
        var zip = new ZipPatcher(ZipFixture.Create(new FixtureEntry("a.txt", Hello)))
            .Prepend(new byte[4096], adjustOffsets: false)
            .ToArray();

        var opened = ZipArchiveSource.Open(new MemoryStream(zip));

        Assert.Null(opened.Source);
        Assert.Equal(FatalKind.ArchiveUnreadable, opened.Fatal!.Kind);
        Assert.Null(opened.Fatal.Entry);
    }

    // テスト P03 (ZIP が無効): EOCD がない
    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(4096)]
    public void P03_DataWithoutEocd_IsUnreadable(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

        var opened = ZipArchiveSource.Open(new MemoryStream(bytes));

        Assert.Null(opened.Source);
        Assert.Equal(FatalKind.ArchiveUnreadable, opened.Fatal!.Kind);
    }

    // テスト P03 (ZIP が無効): 末尾を切り詰めて EOCD を失った ZIP
    [Fact]
    public void P03_TruncatedZip_IsUnreadable()
    {
        var zip = ZipFixture.Create(new FixtureEntry("a.txt", Hello));

        var opened = ZipArchiveSource.Open(new MemoryStream(zip[..^10]));

        Assert.Equal(FatalKind.ArchiveUnreadable, opened.Fatal!.Kind);
    }

    // テスト P03: パスで開けない ZIP (存在しない、ディレクトリ) は入力エラー
    [Fact]
    public void P03_MissingOrDirectoryPath_IsOpenFailure()
    {
        var missing = Path.Combine(TestFiles.Directory, "does-not-exist.zip");

        Assert.Equal(FatalKind.ArchiveOpenFailed, ZipArchiveSource.Open(missing).Fatal!.Kind);
        Assert.Equal(FatalKind.ArchiveOpenFailed, ZipArchiveSource.Open(TestFiles.Directory).Fatal!.Kind);
        Assert.Equal(FatalKind.ArchiveOpenFailed, ZipArchiveSource.Open(string.Empty).Fatal!.Kind);
    }

    // テスト P05 (空 ZIP): 正常に通過し、エントリ0件
    [Fact]
    public void P05_EmptyZip_Passes()
    {
        var zip = ZipFixture.Create();

        using var source = OpenBytes(zip);
        var result = Validate(source.Entries);

        Assert.Equal(0, source.EntryCount);
        AssertPassed(result, 0);
        Assert.Equal(0, result.DeclaredTotalLength);
    }

    // テスト C14: ZipArchiveEntry.Crc32 を reflection なしで直接参照する (ZipArchiveSource.Entries)。
    // ビルドが通ることが本体で、値が Central Directory の CRC-32 であることも確かめる。
    [Fact]
    public void C14_Crc32_IsReadDirectlyFromPublicProperty()
    {
        var zip = ZipFixture.Create(new FixtureEntry("a.txt", Hello), new FixtureEntry("empty.txt", []));

        var entries = ListBytes(zip);

        Assert.Equal(Crc32.HashToUInt32(Hello), entries[0].Crc32);
        Assert.Equal(0u, entries[1].Crc32);
    }

    // テスト R03 (実 ZIP): 注入した上限でちょうど / +1 バイト
    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public void R03_MetadataTotal_RealZip(int limitDelta, bool passes)
    {
        // (5 + 6 + 2) × 2 + 128 × 3 = 410 (ディレクトリ "d/" も名前どおり数える)
        var zip = ZipFixture.Create(new FixtureEntry("a.txt", Hello), new FixtureEntry("bb.txt", Hello), new FixtureEntry("d/"));
        var limits = Limits.Default with { MaxMetadataBytes = 410 + limitDelta };

        var result = ValidateBytes(zip, limits);

        if (passes)
        {
            AssertPassed(result, 3);
        }
        else
        {
            AssertFatal(result, FatalKind.MetadataTooLarge, 2);
        }
    }

    // テスト R05 (実 ZIP): 宣言 Length を ZIP64 extra で 16 GiB / 16 GiB + 1 に書き換える (実データは小さい)
    [Theory]
    [InlineData(16 * GiB, true)]
    [InlineData((16 * GiB) + 1, false)]
    public void R05_EntryDeclaredLength_RealZip(long length, bool passes)
    {
        var zip = new ZipPatcher(ZipFixture.Create(new FixtureEntry("big.bin", Hello)))
            .SetDeclaredLength(0, length)
            .ToArray();

        var result = ValidateBytes(zip);

        if (passes)
        {
            AssertPassed(result, 1);
            Assert.Equal(length, result.Entries[0].Entry.Length);
        }
        else
        {
            AssertFatal(result, FatalKind.EntryTooLarge, 0);
        }
    }

    // テスト R06 (実 ZIP): 宣言合計 64 GiB / 64 GiB + 1。入力は ZIP だけで target を持たないため、
    // 全エントリが MISSING になる状況でも同じ結果になる。
    [Theory]
    [InlineData(new[] { 16 * GiB, 16 * GiB, 16 * GiB, 16 * GiB }, true)]
    [InlineData(new[] { 16 * GiB, 16 * GiB, 16 * GiB, 16 * GiB, 1L }, false)]
    public void R06_TotalDeclaredLength_RealZip(long[] lengths, bool passes)
    {
        var patcher = new ZipPatcher(ZipFixture.Create(lengths.Select((_, i) => new FixtureEntry($"f{i}.bin", Hello))));
        for (var i = 0; i < lengths.Length; i++)
        {
            patcher.SetDeclaredLength(i, lengths[i]);
        }

        var result = ValidateBytes(patcher.ToArray());

        if (passes)
        {
            AssertPassed(result, lengths.Length);
            Assert.Equal(64 * GiB, result.DeclaredTotalLength);
        }
        else
        {
            AssertFatal(result, FatalKind.TotalDeclaredLengthTooLarge, lengths.Length - 1);
        }
    }

    // ZIP は FileShare.Read で開いて Dispose まで保持し、他者の書き込み・改名を拒否する (docs/SPEC.md#prepare のZIP保持)。
    [Fact]
    public void OpenedArchive_RejectsWritersAndRenameUntilDisposed()
    {
        var path = TestFiles.Write("held.zip", ZipFixture.Create(new FixtureEntry("a.txt", Hello)));
        var renamed = path + ".renamed";

        using (var source = Assert.IsType<ZipArchiveSource>(ZipArchiveSource.Open(path).Source))
        {
            Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite));
            Assert.ThrowsAny<IOException>(() => File.Move(path, renamed));
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.Equal(1, source.EntryCount);
        }

        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(File.Exists(renamed));
    }

    private static byte[] ReadEntry(byte[] zip, int index)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read, leaveOpen: false, ZipFixture.Cp437);
        using var stream = archive.Entries[index].Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
