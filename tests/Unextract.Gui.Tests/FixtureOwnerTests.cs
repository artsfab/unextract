using System.IO;
using Unextract.Core.Tests.Fixtures;
using static Unextract.Gui.Tests.SearchViewTests;

namespace Unextract.Gui.Tests;

// The owner of the test's fixtures (TestFixtures) reaches the STA thread of OnSta, so fixtures made there are deleted too.
public sealed class FixtureOwnerTests
{
    [Fact]
    public Task FixtureOwnerReachesTheStaThread()
    {
        var owner = TestFixtures.Current;
        Assert.NotNull(owner);
        return OnSta(() =>
        {
            Assert.Same(owner, TestFixtures.Current);
            Assert.True(Directory.Exists(ArchiveSearchTests.Fixture()));
            return Task.CompletedTask;
        });
    }
}
