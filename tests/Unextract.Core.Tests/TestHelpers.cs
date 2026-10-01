using Unextract.Core.Results;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;

namespace Unextract.Core.Tests;

internal static class TestHelpers
{
    public static ZipPrevalidationResult Validate(IEnumerable<ZipEntryInfo> entries, Limits? limits = null) =>
        ZipPrevalidator.Validate(entries, limits ?? Limits.Default);

    public static ZipPrevalidationResult ValidateNames(params string[] names) => Validate(FakeEntries.Names(names));

    public static void AssertFatal(ZipPrevalidationResult result, FatalKind kind, int entryIndex)
    {
        Assert.False(result.Passed);
        var fatal = Assert.IsType<FatalError>(result.Fatal);
        Assert.Equal(kind, fatal.Kind);
        var entry = Assert.IsType<ZipEntryRef>(fatal.Entry);
        Assert.Equal(entryIndex, entry.Index);
        Assert.Empty(result.Entries);
    }

    public static void AssertPassed(ZipPrevalidationResult result, int entryCount)
    {
        Assert.Null(result.Fatal);
        Assert.True(result.Passed);
        Assert.Equal(entryCount, result.Entries.Count);
    }
}
