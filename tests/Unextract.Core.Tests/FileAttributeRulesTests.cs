using Unextract.Core.Target;

namespace Unextract.Core.Tests;

// テスト T07・T08 のうち、属性値だけによる許可・スキップの判定 (docs/spec/filesystem.md#special-files)。偽の属性値で全ビットを確認する。
public class FileAttributeRulesTests
{
    private static readonly uint[] AllowedBits =
    [
        0x20,   // ARCHIVE
        0x80,   // NORMAL
        0x2,    // HIDDEN
        0x2000, // NOT_CONTENT_INDEXED
        0x800,  // COMPRESSED
        0x200,  // SPARSE_FILE
        0x4000, // ENCRYPTED
    ];

    [Fact]
    public void AllowedMask_IsExactlyTheSevenAllowedBits()
    {
        Assert.Equal(0x6AA2u, FileAttributeRules.AllowedMask);
        Assert.Equal(FileAttributeRules.AllowedMask, AllowedBits.Aggregate(0u, (a, b) => a | b));
    }

    [Fact]
    public void T08_NoAttributes_IsNotSpecial()
    {
        Assert.False(FileAttributeRules.IsSpecial(0));
    }

    // テスト T08: 許可7種のそれぞれ単独
    [Theory]
    [InlineData(0x20u)]
    [InlineData(0x80u)]
    [InlineData(0x2u)]
    [InlineData(0x2000u)]
    [InlineData(0x800u)]
    [InlineData(0x200u)]
    [InlineData(0x4000u)]
    public void T08_EachAllowedBitAlone_IsNotSpecial(uint attributes)
    {
        Assert.False(FileAttributeRules.IsSpecial(attributes));
    }

    // テスト T08: 許可7種の全ての組み合わせ (2^7 通り)
    [Fact]
    public void T08_EveryCombinationOfAllowedBits_IsNotSpecial()
    {
        for (var mask = 0; mask < 1 << AllowedBits.Length; mask++)
        {
            var attributes = 0u;
            for (var i = 0; i < AllowedBits.Length; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    attributes |= AllowedBits[i];
                }
            }

            Assert.False(FileAttributeRules.IsSpecial(attributes), $"0x{attributes:X}");
        }
    }

    // テスト T07: docs/spec/filesystem.md#special-files のスキップ例の各ビット (単独、および許可集合全体との組み合わせ)
    [Theory]
    [InlineData(0x1u)]          // READONLY
    [InlineData(0x4u)]          // SYSTEM
    [InlineData(0x10u)]         // DIRECTORY
    [InlineData(0x40u)]         // DEVICE
    [InlineData(0x100u)]        // TEMPORARY
    [InlineData(0x400u)]        // REPARSE_POINT
    [InlineData(0x1000u)]       // OFFLINE
    [InlineData(0x8000u)]       // INTEGRITY_STREAM
    [InlineData(0x10000u)]      // VIRTUAL
    [InlineData(0x20000u)]      // NO_SCRUB_DATA
    [InlineData(0x40000u)]      // RECALL_ON_OPEN
    [InlineData(0x80000u)]      // PINNED
    [InlineData(0x100000u)]     // UNPINNED
    [InlineData(0x400000u)]     // RECALL_ON_DATA_ACCESS
    [InlineData(0x20000000u)]   // STRICTLY_SEQUENTIAL
    public void T07_SkipExampleBit_IsSpecial(uint bit)
    {
        Assert.True(FileAttributeRules.IsSpecial(bit));
        Assert.True(FileAttributeRules.IsSpecial(bit | FileAttributeRules.AllowedMask));
        Assert.True(FileAttributeRules.IsSpecial(bit | 0x20));
    }

    // テスト T07: 未定義・将来のビット
    [Theory]
    [InlineData(0x8u)]
    [InlineData(0x200000u)]
    [InlineData(0x800000u)]
    [InlineData(0x1000000u)]
    [InlineData(0x2000000u)]
    [InlineData(0x4000000u)]
    [InlineData(0x8000000u)]
    [InlineData(0x10000000u)]
    [InlineData(0x40000000u)]
    [InlineData(0x80000000u)]
    public void T07_UndefinedBit_IsSpecial(uint bit)
    {
        Assert.True(FileAttributeRules.IsSpecial(bit));
        Assert.True(FileAttributeRules.IsSpecial(bit | FileAttributeRules.AllowedMask));
    }

    // 全32ビットの網羅: 許可集合のビットだけが特殊でない
    [Fact]
    public void EveryOneOf32Bits_IsSpecialUnlessAllowed()
    {
        for (var i = 0; i < 32; i++)
        {
            var bit = 1u << i;
            Assert.Equal(!AllowedBits.Contains(bit), FileAttributeRules.IsSpecial(bit));
        }

        Assert.True(FileAttributeRules.IsSpecial(uint.MaxValue));
    }
}
