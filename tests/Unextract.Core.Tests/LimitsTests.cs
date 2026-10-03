using Unextract.Core.Results;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;
using static Unextract.Core.Tests.TestHelpers;

namespace Unextract.Core.Tests;

// docs/spec/zip.md#limits (docs/RATIONALE.md#zip-limits)。境界は「上限以下を許可、超過を拒否」。既定値のままのテストと、小さい値を注入するテストの両方。
// 実 ZIP での R03・R05・R06 は ZipArchiveSourceTests。
public class LimitsTests
{
    private const long GiB = 1L << 30;

    [Fact]
    public void DefaultValues_MatchSpec()
    {
        var limits = Limits.Default;

        Assert.Equal(100_000, limits.MaxEntries);
        Assert.Equal(1_024, limits.MaxNameLength);
        Assert.Equal(128, limits.MaxDepth);
        Assert.Equal(134_217_728, limits.MaxMetadataBytes);
        Assert.Equal(128, limits.MetadataBytesPerEntry);
        Assert.Equal(17_179_869_184, limits.MaxEntryDeclaredLength);
        Assert.Equal(68_719_476_736, limits.MaxTotalDeclaredLength);
        Assert.Equal(68_719_476_736, limits.MaxTotalReadLength);
    }

    // テスト R01: エントリ数 100,000 / 100,001
    [Theory]
    [InlineData(100_000, true)]
    [InlineData(100_001, false)]
    public void R01_EntryCount(int count, bool passes)
    {
        var entries = Enumerable.Range(0, count).Select(i => FakeEntries.Entry(i, $"f{i}"));

        var result = Validate(entries);

        if (passes)
        {
            AssertPassed(result, count);
        }
        else
        {
            AssertFatal(result, FatalKind.TooManyEntries, 100_000);
        }
    }

    // テスト R02: 名前 1,024 / 1,025 UTF-16 コード単位
    [Theory]
    [InlineData(1_024, true)]
    [InlineData(1_025, false)]
    public void R02_NameLength(int length, bool passes)
    {
        var result = ValidateNames(new string('n', length));

        if (passes)
        {
            AssertPassed(result, 1);
        }
        else
        {
            AssertFatal(result, FatalKind.NameTooLong, 0);
        }
    }

    // テスト R02: 名前長はディレクトリの末尾区切りを含む復号後の名前で数える
    [Fact]
    public void R02_NameLength_IncludesDirectoryTrailingSeparator()
    {
        AssertPassed(ValidateNames(new string('d', 1_023) + "/"), 1);
        AssertFatal(ValidateNames(new string('d', 1_024) + "/"), FatalKind.NameTooLong, 0);
    }

    // テスト R02: 深さ 128 / 129 成分
    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void R02_Depth(int depth, bool passes)
    {
        var file = string.Join('/', Enumerable.Repeat("d", depth));
        var directory = file + "/";

        foreach (var name in new[] { file, directory })
        {
            var result = ValidateNames(name);
            if (passes)
            {
                AssertPassed(result, 1);
            }
            else
            {
                AssertFatal(result, FatalKind.PathTooDeep, 0);
            }
        }
    }

    // テスト R03: メタデータ総量 = 名前の UTF-16 バイト数 + 128 × 件数。既定値でちょうど 134,217,728 バイト。
    // 各項は偶数なので総量は常に偶数になり、既定値で +1 バイトの入力は作れない。最小の超過 (+2) をここで、
    // +1 は上限を奇数に注入した R03_MetadataTotal_InjectedLimit で確認する。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R03_MetadataTotal_AtDefaultLimit(bool exceedByTwo)
    {
        // (960 × 2 + 128) × 65,536 = 2,048 × 65,536 = 134,217,728
        const int count = 65_536;
        const int nameLength = 960;
        var entries = Enumerable.Range(0, count).Select(i =>
        {
            var length = exceedByTwo && i == count - 1 ? nameLength + 1 : nameLength;
            return FakeEntries.Entry(i, i.ToString("D6").PadRight(length, 'x'));
        });

        var result = Validate(entries);

        if (exceedByTwo)
        {
            AssertFatal(result, FatalKind.MetadataTooLarge, count - 1);
        }
        else
        {
            AssertPassed(result, count);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public void R03_MetadataTotal_InjectedLimit(int limitDelta, bool passes)
    {
        // (5 + 6 + 7) × 2 + 128 × 3 = 420
        var limits = Limits.Default with { MaxMetadataBytes = 420 + limitDelta };

        var result = Validate(FakeEntries.Names("a.txt", "bb.txt", "ccc.txt"), limits);

        if (passes)
        {
            AssertPassed(result, 3);
        }
        else
        {
            AssertFatal(result, FatalKind.MetadataTooLarge, 2);
        }
    }

    // テスト R04: 長い名前で、100,000 件より前にメタデータ総量の上限を超える (件数上限とは独立)
    [Fact]
    public void R04_MetadataLimit_FiresBeforeEntryLimit()
    {
        // 1件当たり 1,024 × 2 + 128 = 2,176 バイト。61,681 件目 (index 61,680) で 134,217,728 を超える。
        var entries = Enumerable.Range(0, 100_000)
            .Select(i => FakeEntries.Entry(i, i.ToString("D6").PadRight(1_024, 'x')));

        var result = Validate(entries);

        AssertFatal(result, FatalKind.MetadataTooLarge, 61_680);
    }

    // テスト R05: 1エントリの宣言 Length 16 GiB / 16 GiB + 1
    [Theory]
    [InlineData(16 * GiB, true)]
    [InlineData((16 * GiB) + 1, false)]
    public void R05_EntryDeclaredLength(long length, bool passes)
    {
        var result = Validate(FakeEntries.Lengths(length));

        if (passes)
        {
            AssertPassed(result, 1);
        }
        else
        {
            AssertFatal(result, FatalKind.EntryTooLarge, 0);
        }
    }

    // テスト R06: 宣言 Length 合計 64 GiB / 64 GiB + 1。全エントリが MISSING 相当 (入力は target を持たず、
    // target の状態や dry-run の有無を参照しない) でも適用される。
    [Theory]
    [InlineData(new[] { 16 * GiB, 16 * GiB, 16 * GiB, 16 * GiB }, true)]
    [InlineData(new[] { 16 * GiB, 16 * GiB, 16 * GiB, 16 * GiB, 1L }, false)]
    [InlineData(new[] { 16 * GiB, 16 * GiB, 16 * GiB, (16 * GiB) - 1, 1L }, true)]
    [InlineData(new[] { 16 * GiB, 16 * GiB, 16 * GiB, (16 * GiB) - 1, 2L }, false)]
    public void R06_TotalDeclaredLength(long[] lengths, bool passes)
    {
        var result = Validate(FakeEntries.Lengths(lengths));

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

    // テスト R06: ディレクトリエントリは合計に含めない (Length は 0 でなければ別の FATAL)
    [Fact]
    public void R06_DirectoriesDoNotCountTowardTotal()
    {
        var entries = new[]
        {
            FakeEntries.Entry(0, "a.bin", 16 * GiB),
            FakeEntries.Entry(1, "d/"),
            FakeEntries.Entry(2, "b.bin", 16 * GiB),
            FakeEntries.Entry(3, "c.bin", 16 * GiB),
            FakeEntries.Entry(4, "e.bin", 16 * GiB),
        };

        AssertPassed(Validate(entries), 5);
    }

    // 合計の加算は桁あふれしない。long の最大値を2回足しても負に回り込まず上限超過になる。
    [Fact]
    public void TotalDeclaredLength_DoesNotOverflow()
    {
        var limits = Limits.Default with
        {
            MaxEntryDeclaredLength = long.MaxValue,
            MaxTotalDeclaredLength = long.MaxValue,
        };

        var result = Validate(FakeEntries.Lengths(long.MaxValue, long.MaxValue), limits);

        AssertFatal(result, FatalKind.TotalDeclaredLengthTooLarge, 1);
    }

    [Fact]
    public void NegativeDeclaredLength_IsFatal()
    {
        AssertFatal(Validate(FakeEntries.Lengths(1, -1)), FatalKind.InvalidDeclaredLength, 1);
    }

    [Fact]
    public void InjectedLimits_AreUsed()
    {
        var limits = Limits.Default with { MaxEntries = 2, MaxNameLength = 3, MaxDepth = 2 };

        AssertPassed(Validate(FakeEntries.Names("a", "b"), limits), 2);
        AssertFatal(Validate(FakeEntries.Names("a", "b", "c"), limits), FatalKind.TooManyEntries, 2);
        AssertFatal(Validate(FakeEntries.Names("abcd"), limits), FatalKind.NameTooLong, 0);
        AssertFatal(Validate(FakeEntries.Names("a/b/c"), limits), FatalKind.NameTooLong, 0);
        AssertFatal(Validate(FakeEntries.Names("a/b/"), limits with { MaxNameLength = 10, MaxDepth = 1 }), FatalKind.PathTooDeep, 0);
    }
}
