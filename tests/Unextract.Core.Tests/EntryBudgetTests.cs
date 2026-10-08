using Unextract.Core.Results;
using Unextract.Core.Zip;

namespace Unextract.Core.Tests;

// テスト U02: 列挙の予算 (エントリ総数・名前長・メタデータ総量) の境界と検査順 (docs/spec/zip.md#limits、docs/spec/rar.md#limits)。
// ZIP の事前検証での時点・出力は L 系が担う。ここでは RAR の列挙と共有する判定の計算だけを確かめる。
public class EntryBudgetTests
{
    [Fact]
    public void U02_EntryCount_ExactlyLimit_Passes_AndNextIsTooMany()
    {
        var budget = new EntryBudget(Limits.Default with { MaxEntries = 3 });

        Assert.Null(budget.Add(1));
        Assert.Null(budget.Add(1));
        Assert.Null(budget.Add(1));
        Assert.Equal(FatalKind.TooManyEntries, budget.Add(1));
    }

    [Fact]
    public void U02_NameLength_ExactlyLimit_Passes_AndPlusOneIsTooLong()
    {
        var budget = new EntryBudget(Limits.Default with { MaxNameLength = 10 });

        Assert.Null(budget.Add(10));
        Assert.Equal(FatalKind.NameTooLong, budget.Add(11));
    }

    [Fact]
    public void U02_Metadata_ExactlyLimit_Passes_AndPlusOneByteIsTooLarge()
    {
        // 1 エントリ = 名前の UTF-16 バイト数 + 128。
        var limits = Limits.Default with { MaxMetadataBytes = (2 * 10) + 128 + (2 * 5) + 128 };
        var budget = new EntryBudget(limits);
        Assert.Null(budget.Add(10));
        Assert.Null(budget.Add(5));

        var over = new EntryBudget(limits);
        Assert.Null(over.Add(10));
        Assert.Equal(FatalKind.MetadataTooLarge, over.Add(6));
    }

    [Fact]
    public void U02_CheckOrder_CountThenNameThenMetadata()
    {
        // 件数と名前長の両方を超える場合は件数。
        var both = new EntryBudget(Limits.Default with { MaxEntries = 0, MaxNameLength = 1 });
        Assert.Equal(FatalKind.TooManyEntries, both.Add(2));

        // 名前長とメタデータ総量の両方を超える場合は名前長。
        var name = new EntryBudget(Limits.Default with { MaxNameLength = 1, MaxMetadataBytes = 1 });
        Assert.Equal(FatalKind.NameTooLong, name.Add(2));
    }

    [Fact]
    public void U02_NameTooLong_DoesNotAddMetadata()
    {
        // 名前長で拒否したエントリはメタデータ総量に加えない (ZipPrevalidator の現行の状態の進め方)。
        var budget = new EntryBudget(Limits.Default with { MaxNameLength = 10, MaxMetadataBytes = (2 * 10) + 128 });
        Assert.Equal(FatalKind.NameTooLong, budget.Add(11));
        Assert.Null(budget.Add(10));
    }
}
