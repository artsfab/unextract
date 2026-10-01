using System.IO.Compression;
using Unextract.Core.Analysis;
using Unextract.Core.Results;
using Unextract.Core.Tests.Fakes;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.Core.Tests;

// テスト C01〜C13: ZIP 内容の検証と CRC (SPEC §5)。ZIP は実際の ZipArchive で読み、target だけを偽で表す。
// 同じ壊れたエントリ x.bin (宣言 Length = N) を、target の状態 (不存在 / サイズ ≠ N / サイズ = N) だけを変えて分類する。
public class ContentVerificationTests
{
    private const string Target = @"C:\target\x.bin";

    // V2 と同じ大きさの、圧縮できるが単調ではないデータ。
    internal static readonly byte[] Data = MakeData(7000);

    public enum TargetState
    {
        Missing,
        SizeDiffers,
        SizeMatches,
    }

    public static TheoryData<string, TargetState> Cases()
    {
        var data = new TheoryData<string, TargetState>();
        foreach (var id in new[] { "C01", "C02", "C03", "C04", "C05-Deflate", "C05-Stored", "C06", "C07", "C08-LZMA", "C08-BZip2", "C08-AES" })
        {
            foreach (var state in Enum.GetValues<TargetState>())
            {
                data.Add(id, state);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void C01_C08_BrokenEntry_ByTargetState(string id, TargetState state)
    {
        var (zip, declared, sameSizeTarget, expected) = Fixture(id);
        using var harness = new PipelineHarness(zip);
        switch (state)
        {
            case TargetState.SizeDiffers:
                harness.Fs.AddFile(Target, new byte[declared + 1]);
                break;
            case TargetState.SizeMatches:
                harness.Fs.AddFile(Target, sameSizeTarget);
                break;
        }

        var result = harness.Run();

        if (state == TargetState.SizeMatches)
        {
            var fatal = Assert.IsType<FatalError>(result.Fatal);
            Assert.Equal(expected, fatal.Kind);
            Assert.Equal("x.bin", fatal.Entry!.Name);
            Assert.Empty(result.DeletionCandidates);
            if (expected == FatalKind.ContentEncrypted)
            {
                // C07: IsEncrypted を Open() の前に確認する。
                Assert.False(harness.Contents.Opened(0));
            }

            return;
        }

        Assert.Null(result.Fatal);
        var entry = Assert.Single(result.Results);
        Assert.Equal(state == TargetState.Missing ? Classification.Missing : Classification.Modified, entry.Classification);

        // 不存在・サイズ不一致では ZIP の内容を開かず、CRC も参照しない。
        Assert.False(harness.Contents.Touched(0), string.Join(", ", harness.Contents.Calls));
    }

    // C09: 先頭付近のバイトが target と異なり、後方で C02 の破損 → MODIFIED ではなく FATAL。不一致位置を変えても同じ。
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(6999)]
    public void C09_EarlyMismatchWithLaterCorruption_IsFatal(int mismatchOffset)
    {
        var (zip, _, _, _) = Fixture("C02");
        var target = (byte[])Data.Clone();
        target[mismatchOffset] ^= 0x01;
        using var harness = new PipelineHarness(zip);
        harness.Fs.AddFile(Target, target);

        var result = harness.Run();

        Assert.Equal(FatalKind.ContentReadFailed, result.Fatal?.Kind);
    }

    // C10: 内容が1バイト異なり、ZIP は健全 → MODIFIED (FATAL にならない)
    [Fact]
    public void C10_OneByteDifference_HealthyZip_IsModified()
    {
        var target = (byte[])Data.Clone();
        target[3500] ^= 0x01;
        using var harness = new PipelineHarness(PipelineHarness.MakeZip(("x.bin", Data)));
        harness.Fs.AddFile(Target, target);

        var result = harness.Run();

        Assert.Null(result.Fatal);
        Assert.Equal(Classification.Modified, Assert.Single(result.Results).Classification);
        Assert.True(harness.Contents.Opened(0));
    }

    // C11: 0 バイトエントリ。CRC = 0 と target 0 バイト → MATCHED、CRC を 0 以外に書き換え → FATAL
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void C11_ZeroByteEntry(bool corruptCrc)
    {
        var zip = PipelineHarness.MakeZip(("empty.txt", []));
        var patcher = new ZipPatcher(zip);
        Assert.Equal(0u, patcher.GetCrc32(0));
        if (corruptCrc)
        {
            zip = patcher.SetCrc32(0, 0x12345678).ToArray();
        }

        using var harness = new PipelineHarness(zip);
        harness.Fs.AddFile(@"C:\target\empty.txt", []);

        var result = harness.Run();

        if (corruptCrc)
        {
            Assert.Equal(FatalKind.ContentCrcMismatch, result.Fatal?.Kind);
        }
        else
        {
            Assert.Null(result.Fatal);
            Assert.Equal(Classification.Matched, Assert.Single(result.Results).Classification);
        }
    }

    // C12: Data Descriptor 付きエントリ (非シーク出力で作成) が target と一致 → MATCHED (期待 CRC は Central Directory の値)
    [Fact]
    public void C12_DataDescriptorEntry_Matches()
    {
        var zip = ZipFixture.Create([new FixtureEntry("x.bin", Data)], nonSeekable: true);
        Assert.NotEqual(0, new ZipPatcher(zip).GetFlags(0) & 0x0008);
        using var harness = new PipelineHarness(zip);
        harness.Fs.AddFile(Target, (byte[])Data.Clone());

        var result = harness.Run();

        Assert.Null(result.Fatal);
        Assert.Equal(Classification.Matched, Assert.Single(result.Results).Classification);
    }

    // C13: ランタイム回帰検知。C01・C03・C07 の fixture を ZipArchive 単体で読み、例外の有無を記録する。
    // PLAN_VALIDATION.md V2 の記録: C01 は例外なしで全バイト、C03 は例外なしで短く終わる、C07 は Open() が成功し平文を読める。
    // この期待が崩れたら V2 の記録を更新する (どちらでも unextract の結果は FATAL のまま: C01_C08 で確認)。
    [Theory]
    [InlineData("C01", false, 7000)]
    [InlineData("C03", false, -1)]
    [InlineData("C07", false, 7000)]
    public void C13_RuntimeBehavior_MatchesValidationRecord(string id, bool expectException, int expectedBytes)
    {
        var (zip, _, _, _) = Fixture(id);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entry = archive.Entries[0];

        Exception? thrown = null;
        long total = 0;
        try
        {
            using var stream = entry.Open();
            var buffer = new byte[4096];
            int n;
            while ((n = stream.Read(buffer)) > 0)
            {
                total += n;
            }
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.Equal(expectException, thrown is not null);
        if (expectedBytes >= 0)
        {
            Assert.Equal(expectedBytes, total);
        }
        else
        {
            Assert.True(total < Data.Length, $"read {total}");
        }

        if (id == "C07")
        {
            Assert.True(entry.IsEncrypted);
        }
    }

    // id ごとの fixture: (ZIP、宣言 Length、サイズ一致の列で使う target、サイズ一致の列で期待する FATAL)
    internal static (byte[] Zip, long Declared, byte[] SameSizeTarget, FatalKind Expected) Fixture(string id)
    {
        var deflate = ZipFixture.Create(new FixtureEntry("x.bin", Data));
        var stored = ZipFixture.Create(new FixtureEntry("x.bin", Data, Level: CompressionLevel.NoCompression));
        var n = Data.Length;
        switch (id)
        {
            case "C01":
                var crc = new ZipPatcher(deflate).GetCrc32(0);
                return (new ZipPatcher(deflate).SetCrc32(0, crc ^ 0xFFFFFFFF).ToArray(), n, Data, FatalKind.ContentCrcMismatch);
            case "C02":
                return (CorruptDeflateMidway(deflate), n, Data, FatalKind.ContentReadFailed);
            case "C03":
                var half = (uint)(new ZipPatcher(deflate).GetCompressedSize(0) / 2);
                return (new ZipPatcher(deflate).SetCompressedSize(0, half).ToArray(), n, Data, FatalKind.ContentTooShort);
            case "C04":
                return (new ZipPatcher(deflate).SetDeclaredLength(0, n - 10).ToArray(), n - 10, Data[..(n - 10)], FatalKind.ContentCrcMismatch);
            case "C05-Deflate":
                return (new ZipPatcher(deflate).SetDeclaredLength(0, n + 10).ToArray(), n + 10, [.. Data, .. new byte[10]], FatalKind.ContentTooShort);
            case "C05-Stored":
                return (new ZipPatcher(stored).SetDeclaredLength(0, n + 10).ToArray(), n + 10, [.. Data, .. new byte[10]], FatalKind.ContentTooShort);
            case "C06":
                return (new ZipPatcher(stored).SetDeclaredLength(0, n - 10).ToArray(), n - 10, Data[..(n - 10)], FatalKind.ContentTooLong);
            case "C07":
                return (new ZipPatcher(deflate).SetEncryptedFlag(0).ToArray(), n, Data, FatalKind.ContentEncrypted);
            case "C08-LZMA":
                return (new ZipPatcher(deflate).SetCompressionMethod(0, 14).ToArray(), n, Data, FatalKind.ContentReadFailed);
            case "C08-BZip2":
                return (new ZipPatcher(deflate).SetCompressionMethod(0, 12).ToArray(), n, Data, FatalKind.ContentReadFailed);
            case "C08-AES":
                return (new ZipPatcher(deflate).SetCompressionMethod(0, 99).ToArray(), n, Data, FatalKind.ContentReadFailed);
            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, null);
        }
    }

    // C02: Deflate データの途中を破損する。破損の位置によっては Deflate が例外なく早く終わる (C03 と同じくバイト数不足で検出) ため、
    // 中央から後ろへ1バイトずつ試し、ZipArchive が InvalidDataException を出す最初の位置を使う (PLAN_TESTS C02 の期待)。
    private static byte[] CorruptDeflateMidway(byte[] deflate)
    {
        var compressed = (int)new ZipPatcher(deflate).GetCompressedSize(0);
        for (var offset = compressed / 2; offset < compressed; offset++)
        {
            var candidate = new ZipPatcher(deflate).CorruptData(0, offset).ToArray();
            if (ThrowsInvalidData(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("no corruption offset produced InvalidDataException");
    }

    private static bool ThrowsInvalidData(byte[] zip)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            using var stream = archive.Entries[0].Open();
            stream.CopyTo(Stream.Null);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static byte[] MakeData(int length)
    {
        var random = new Random(20261002);
        string[] words = ["unextract", "zip", "target", "matched", "modified", "missing", "crc", "deflate", "ntfs", "handle"];
        var text = new System.Text.StringBuilder();
        while (text.Length < length)
        {
            text.Append(words[random.Next(words.Length)]).Append(random.Next(1000)).Append(' ');
        }

        return System.Text.Encoding.ASCII.GetBytes(text.ToString(0, length));
    }
}
