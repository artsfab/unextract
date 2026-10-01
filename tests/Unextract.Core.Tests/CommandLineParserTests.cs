using Unextract.Core.CommandLine;

namespace Unextract.Core.Tests;

public class CommandLineParserTests
{
    [Theory]
    [InlineData(new[] { "a.zip", "--target", "dir" }, false, false)]
    [InlineData(new[] { "--target", "dir", "a.zip" }, false, false)]
    [InlineData(new[] { "a.zip", "--target", "dir", "--dry-run" }, true, false)]
    [InlineData(new[] { "a.zip", "--dry-run", "--target", "dir", "--yes" }, true, true)]
    [InlineData(new[] { "-y", "a.zip", "--target", "dir" }, false, true)]
    public void Parse_ValidArguments(string[] args, bool dryRun, bool yes)
    {
        var result = CommandLineParser.Parse(args);

        Assert.Null(result.Error);
        var options = Assert.IsType<CommandLineOptions>(result.Options);
        Assert.Equal("a.zip", options.ArchivePath);
        Assert.Equal("dir", options.TargetPath);
        Assert.Equal(dryRun, options.DryRun);
        Assert.Equal(yes, options.AssumeYes);
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new string[] { },
        new string[] { "a.zip" }, // --target は必須
        new string[] { "--target", "dir" }, // ZIP がない
        new string[] { "a.zip", "--target" }, // 値がない
        new string[] { "a.zip", "--target", "--dry-run" }, // 値の代わりにオプション
        new string[] { "a.zip", "--target", "" },
        new string[] { "a.zip", "--target", "d1", "--target", "d2" },
        new string[] { "a.zip", "b.zip", "--target", "dir" },
        new string[] { "", "--target", "dir" },
        new string[] { "a.zip", "--target", "dir", "--force" },
        new string[] { "a.zip", "--target=dir" },
        new string[] { "a.zip", "--target", "dir", "--dry-run", "--dry-run" },
        new string[] { "a.zip", "--target", "dir", "--yes", "-y" },
        new string[] { "a.zip", "--target", "dir", "-Y" },
        new string[] { "a.zip", "--target", "dir", "-" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Parse_InvalidArguments_IsInputError(string[] args)
    {
        var result = CommandLineParser.Parse(args);

        Assert.Null(result.Options);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }
}
