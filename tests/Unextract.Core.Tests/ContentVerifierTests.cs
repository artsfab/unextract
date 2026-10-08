using System.IO.Hashing;
using Unextract.Core.Analysis;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Tests;

// テスト C16: 内容検証器のチャンクの契約 (docs/spec/zip.md#verification の基準3〜6、docs/spec/rar.md#verification)。
// 読み方 (ZIP の pull、RAR の押し込み) によらず、任意の長さのチャンクを同じ判定にする。DLL のチャンクは 64 KiB を超える (4 MiB を観測)。
public class ContentVerifierTests
{
    private static readonly byte[] Data = MakeData((4 * 1024 * 1024) + 70_001);

    public static TheoryData<string> ChunkPatterns() => ["whole", "4MiB", "64KiB+1", "64KiB-1", "bytes-head", "mixed"];

    [Theory]
    [MemberData(nameof(ChunkPatterns))]
    public void C16_SameContent_AnyChunking_Matches(string pattern)
    {
        var comparer = new ContentComparer(Limits.Default);
        var target = new MemoryTarget(Data);
        var verifier = comparer.Begin(Data.Length, Crc(Data), target);

        foreach (var chunk in Split(Data, pattern))
        {
            Assert.Null(verifier.Append(chunk.Span));
        }

        Assert.Equal(ContentVerdict.Match, verifier.Complete().Verdict);
        Assert.Equal(Data.Length, comparer.TotalRead);
    }

    [Theory]
    [MemberData(nameof(ChunkPatterns))]
    public void C16_OneByteDifference_AnyChunking_IsMismatch_AndStopsReadingTarget(string pattern)
    {
        var other = (byte[])Data.Clone();
        other[100_000] ^= 1;
        var comparer = new ContentComparer(Limits.Default);
        var target = new MemoryTarget(other);
        var verifier = comparer.Begin(Data.Length, Crc(Data), target);

        foreach (var chunk in Split(Data, pattern))
        {
            Assert.Null(verifier.Append(chunk.Span));
        }

        Assert.Equal(ContentVerdict.Mismatch, verifier.Complete().Verdict);

        // 不一致を確定した比較単位 (64 KiB 以下) より後ろの target は読まない。
        Assert.True(target.Position <= 100_000 + ContentComparer.BufferSize, $"position {target.Position}");
    }

    [Fact]
    public void C16_TargetShortReads_Match()
    {
        var comparer = new ContentComparer(Limits.Default);
        var target = new MemoryTarget(Data) { MaxRead = 1000 };
        var verifier = comparer.Begin(Data.Length, Crc(Data), target);

        Assert.Null(verifier.Append(Data));
        Assert.Equal(ContentVerdict.Match, verifier.Complete().Verdict);
    }

    [Fact]
    public void C16_ChunkCrossingDeclaredLength_AbortsWithoutUsingAnyByte()
    {
        var comparer = new ContentComparer(Limits.Default);
        var target = new MemoryTarget(Data.AsSpan(0, 1000).ToArray());
        var verifier = comparer.Begin(1000, Crc(Data.AsSpan(0, 1000)), target);

        Assert.Null(verifier.Append(Data.AsSpan(0, 600)));
        var aborted = verifier.Append(Data.AsSpan(600, 401));

        Assert.Equal(FatalKind.ContentTooLong, aborted?.FatalKind);
        Assert.Equal(600, verifier.Read);
        Assert.Equal(600, comparer.TotalRead);
        Assert.Equal(600, target.Position);

        // 中止の後のチャンクは何もせず同じ中止を返し、終端の判定も中止のまま。
        Assert.Equal(aborted, verifier.Append(Data.AsSpan(0, 1)));
        Assert.Equal(aborted, verifier.Complete());
        Assert.Equal(600, comparer.TotalRead);
    }

    [Fact]
    public void C16_EmptyEntry_NoChunks_Matches()
    {
        var comparer = new ContentComparer(Limits.Default);
        var verifier = comparer.Begin(0, 0, new MemoryTarget([]));

        Assert.Null(verifier.Append([]));
        Assert.Equal(ContentVerdict.Match, verifier.Complete().Verdict);
    }

    [Fact]
    public void C16_EmptyEntry_TargetHasData_IsMismatch()
    {
        var comparer = new ContentComparer(Limits.Default);
        var verifier = comparer.Begin(0, 0, new MemoryTarget([1]));

        Assert.Equal(ContentVerdict.Mismatch, verifier.Complete().Verdict);
    }

    [Fact]
    public void C16_Short_IsTooShort_BeforeCrc()
    {
        var comparer = new ContentComparer(Limits.Default);
        var verifier = comparer.Begin(1000, 0x12345678, new MemoryTarget(Data.AsSpan(0, 1000).ToArray()));

        Assert.Null(verifier.Append(Data.AsSpan(0, 999)));
        Assert.Equal(FatalKind.ContentTooShort, verifier.Complete().FatalKind);
    }

    [Fact]
    public void C16_CrcMismatch_IsFatal_EvenIfBytesMatch()
    {
        var comparer = new ContentComparer(Limits.Default);
        var verifier = comparer.Begin(1000, Crc(Data.AsSpan(0, 1000)) ^ 1, new MemoryTarget(Data.AsSpan(0, 1000).ToArray()));

        Assert.Null(verifier.Append(Data.AsSpan(0, 1000)));
        Assert.Equal(FatalKind.ContentCrcMismatch, verifier.Complete().FatalKind);
    }

    [Fact]
    public void C16_NoExpectedCrc_DoesNotCheckCrc()
    {
        var comparer = new ContentComparer(Limits.Default);
        var verifier = comparer.Begin(1000, null, new MemoryTarget(Data.AsSpan(0, 1000).ToArray()));

        Assert.Null(verifier.Append(Data.AsSpan(0, 1000)));
        Assert.Equal(ContentVerdict.Match, verifier.Complete().Verdict);
    }

    [Fact]
    public void C16_TargetReadFailure_InLargeChunk_Aborts()
    {
        var comparer = new ContentComparer(Limits.Default);
        var target = new MemoryTarget(Data) { FailAt = 200_000 };
        var verifier = comparer.Begin(Data.Length, Crc(Data), target);

        var aborted = verifier.Append(Data);

        Assert.Equal(FatalKind.TargetReadFailed, aborted?.FatalKind);
        Assert.Equal(aborted, verifier.Complete());
    }

    [Fact]
    public void C16_TotalReadLimit_CountsAcrossEntries()
    {
        var comparer = new ContentComparer(Limits.Default with { MaxTotalReadLength = 1500 });
        var first = comparer.Begin(1000, null, new MemoryTarget(Data.AsSpan(0, 1000).ToArray()));
        Assert.Null(first.Append(Data.AsSpan(0, 1000)));
        Assert.Equal(ContentVerdict.Match, first.Complete().Verdict);

        var second = comparer.Begin(1000, null, new MemoryTarget(Data.AsSpan(0, 1000).ToArray()));
        Assert.Null(second.Append(Data.AsSpan(0, 500)));
        Assert.Equal(FatalKind.TotalReadLengthTooLarge, second.Append(Data.AsSpan(500, 1))?.FatalKind);
    }

    private static IEnumerable<ReadOnlyMemory<byte>> Split(byte[] data, string pattern)
    {
        var sizes = pattern switch
        {
            "whole" => new[] { data.Length },
            "4MiB" => new[] { 4 * 1024 * 1024 },
            "64KiB+1" => new[] { ContentComparer.BufferSize + 1 },
            "64KiB-1" => new[] { ContentComparer.BufferSize - 1 },
            "bytes-head" => new[] { 1, 1, 1, 1, 1, 0, 1, 1, 1, 200_000 },
            "mixed" => new[] { 3, 70_000, 0, 1, 131_072, 65_536, 5_000_000 },
            _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
        };

        var position = 0;
        var i = 0;
        while (position < data.Length)
        {
            var size = Math.Min(sizes[Math.Min(i, sizes.Length - 1)], data.Length - position);
            yield return data.AsMemory(position, size);
            position += size;
            i++;
        }
    }

    private static uint Crc(ReadOnlySpan<byte> data) => Crc32.HashToUInt32(data);

    private static byte[] MakeData(int length)
    {
        var data = new byte[length];
        var x = 0x2545F491u;
        for (var i = 0; i < data.Length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            data[i] = (byte)x;
        }

        return data;
    }

    // 比較用ハンドルの最小の偽物。MaxRead で1回の読み取りを短くし、FailAt の位置から読み取りに失敗する。
    private sealed class MemoryTarget(byte[] content) : IComparisonHandle
    {
        public int MaxRead { get; init; } = int.MaxValue;

        public long FailAt { get; init; } = -1;

        public long Position { get; private set; }

        public ProbeResult<int> Read(Span<byte> buffer)
        {
            if (FailAt >= 0 && Position >= FailAt)
            {
                return ProbeResult<int>.Fail(23, "Read");
            }

            var available = (int)Math.Min(Math.Min(buffer.Length, MaxRead), content.Length - Position);
            if (FailAt >= 0)
            {
                available = (int)Math.Min(available, FailAt - Position);
            }

            content.AsSpan((int)Position, available).CopyTo(buffer);
            Position += available;
            return ProbeResult<int>.Ok(available);
        }

        public ProbeResult<VolumeFileId> GetVolumeFileId() => throw new NotSupportedException();

        public ProbeResult<StandardInformation> GetStandardInformation() => throw new NotSupportedException();

        public ProbeResult<BasicInformation> GetBasicInformation() => throw new NotSupportedException();

        public ProbeResult<AttributeTagInformation> GetAttributeTagInformation() => throw new NotSupportedException();

        public ProbeResult<IReadOnlyList<StreamEntry>> GetStreams() => throw new NotSupportedException();

        public ProbeResult<FileId> GetParentFileId() => throw new NotSupportedException();

        public ProbeResult<string> GetFinalPath() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
