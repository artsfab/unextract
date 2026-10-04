using Unextract.Core.Analysis;
using Unextract.Core.Commands;
using Unextract.Core.Results;
using Unextract.Core.Tests.Fakes;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;

namespace Unextract.Core.Tests;

public class PrepareDiagnosticTests
{
    // J04: 両操作・両モードで、表示文字列からの復元なしに区分・原因・件数を取得できる。
    [Theory]
    [InlineData("archive-open", RunMode.Strict)]
    [InlineData("archive-open", RunMode.Fast)]
    [InlineData("archive-unreadable", RunMode.Strict)]
    [InlineData("archive-unreadable", RunMode.Fast)]
    [InlineData("protected", RunMode.Strict)]
    [InlineData("protected", RunMode.Fast)]
    [InlineData("target", RunMode.Strict)]
    [InlineData("target", RunMode.Fast)]
    [InlineData("zip-validation", RunMode.Strict)]
    [InlineData("zip-validation", RunMode.Fast)]
    [InlineData("identity-empty", RunMode.Strict)]
    [InlineData("identity-empty", RunMode.Fast)]
    [InlineData("identity", RunMode.Strict)]
    [InlineData("identity", RunMode.Fast)]
    public void J04_PrepareFailuresPreserveDiagnostics(string kind, RunMode mode)
    {
        var zip = kind switch
        {
            "identity-empty" => ZipFixture.Create(),
            "zip-validation" => ZipFixture.Create(new FixtureEntry("ok.txt"), new FixtureEntry("CON")),
            _ => ZipFixture.Create(new FixtureEntry("ok.txt")),
        };
        var h = new CommandHarness(zip, createTarget: kind != "target") { Mode = mode };
        switch (kind)
        {
            case "archive-open":
                h.OpenArchive = _ => ZipArchiveSource.Open(Path.Combine(TestFiles.Directory, $"missing-{Guid.NewGuid():N}.zip"));
                break;
            case "archive-unreadable":
                h.OpenArchive = _ => ZipArchiveSource.Open(new MemoryStream([1, 2, 3]));
                break;
            case "protected":
                h.Locations = new(null, new FatalError(FatalKind.ProtectedLocationUnresolved, Detail: "Windows", Win32Error: 5));
                break;
            case "identity-empty":
            case "identity":
                h.Fs.Get(CommandHarness.ArchivePath).Errors[FakeOp.GetFileIdentity] = 5;
                break;
        }

        var analyze = h.Analyze();
        var delete = h.Delete();
        var expectedStage = kind switch
        {
            "archive-open" or "archive-unreadable" => PrepareStage.Archive,
            "protected" => PrepareStage.ProtectedLocations,
            "target" => PrepareStage.TargetRoot,
            "zip-validation" => PrepareStage.ZipValidation,
            _ => PrepareStage.ArchiveIdentity,
        };
        var expectedKind = kind switch
        {
            "archive-open" => FatalKind.ArchiveOpenFailed,
            "archive-unreadable" => FatalKind.ArchiveUnreadable,
            "protected" => FatalKind.ProtectedLocationUnresolved,
            "target" => FatalKind.TargetNotFound,
            "zip-validation" => FatalKind.ReservedName,
            _ => FatalKind.ArchiveIdentityFailed,
        };
        var expectedTotal = kind switch { "identity-empty" => 0, "identity" => 1, "zip-validation" => 2, _ => (int?)null };
        foreach (var failure in new[] { analyze.Outcome.PreparationFailure, delete.Outcome.PreparationFailure })
        {
            Assert.NotNull(failure);
            Assert.Equal(expectedStage, failure.Stage);
            Assert.Equal(kind is not ("protected" or "target"), failure.IsFatal);
            Assert.Equal(expectedKind, failure.Fatal!.Kind);
            Assert.Equal(expectedTotal, failure.TotalEntries);
            Assert.Null(failure.EntriesError);
            Assert.Null(failure.Fatal.Step);
            Assert.Equal(kind switch
            {
                "identity" or "identity-empty" or "protected" => (int?)5,
                "target" => 2,
                _ => null,
            }, failure.Fatal.Win32Error);
            if (kind == "zip-validation")
            {
                Assert.Equal(new ZipEntryRef(1, "CON"), failure.Fatal.Entry);
                Assert.Equal(2, failure.Fatal.Entry!.Number);
            }
        }

        Assert.Same(analyze.Outcome.PrepareError, analyze.Outcome.PreparationFailure!.Fatal);
        Assert.Same(delete.Outcome.PrepareError, delete.Outcome.PreparationFailure!.Fatal);
        Assert.Equal(ExitStatus.Error, analyze.Outcome.Status);
        Assert.Equal(ExitStatus.Error, delete.Outcome.Status);
        Assert.Null(delete.Outcome.Report);
        if (expectedTotal is { } count)
        {
            Assert.Equal(count, analyze.Outcome.Analysis!.UnclassifiedCount);
            Assert.Equal(analyze.Outcome.PreparationFailure.Fatal, analyze.Outcome.Analysis.Fatal);
        }
        else
        {
            Assert.Null(analyze.Outcome.Analysis);
        }

        Assert.False(h.TouchedTargetEntries());
        Assert.Empty(h.Fs.Deleted);
    }

    [Fact]
    public void J04_DiagnosticFieldsDoNotChangeHumanDescription()
    {
        var original = new FatalError(FatalKind.TargetInfoFailed, new ZipEntryRef(1, "a\u200B.txt"), "Win32 エラー 5");
        var diagnostic = original with { Step = EntryStep.Inspect, Win32Error = 5 };
        Assert.Equal(original.Describe(), diagnostic.Describe());
        Assert.Contains("エントリ #2", diagnostic.Describe(), StringComparison.Ordinal);
        Assert.Contains("a\\u{200B}.txt", diagnostic.Describe(), StringComparison.Ordinal);
    }
}
