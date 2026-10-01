using Unextract.Core.Results;

namespace Unextract.Core.Tests;

public class ResultTypeTests
{
    [Theory]
    [InlineData(Classification.Matched, "MATCHED")]
    [InlineData(Classification.Modified, "MODIFIED")]
    [InlineData(Classification.Missing, "MISSING")]
    [InlineData(Classification.SkippedSpecialFile, "SKIPPED_SPECIAL_FILE")]
    [InlineData(Classification.Directory, "DIRECTORY")]
    public void Classification_DisplayString(Classification classification, string expected)
    {
        Assert.Equal(expected, classification.ToDisplayString());
    }

    [Fact]
    public void ExitStatus_MapsToThreeDistinctCodes()
    {
        Assert.Equal(0, ExitCodes.ToProcessExitCode(ExitStatus.Success));
        Assert.Equal(1, ExitCodes.ToProcessExitCode(ExitStatus.Error));
        Assert.Equal(2, ExitCodes.ToProcessExitCode(ExitStatus.UserCancelled));
    }

    [Fact]
    public void FatalKind_EveryKindHasDescription()
    {
        foreach (var kind in Enum.GetValues<FatalKind>())
        {
            Assert.False(string.IsNullOrEmpty(FatalKindText.Describe(kind)));
        }
    }
}
