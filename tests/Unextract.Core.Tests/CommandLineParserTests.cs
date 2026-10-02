using Unextract.Core.Analysis;
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

    // D26: --fast と --dry-run・--yes・-y の併用 (順序を変えたものを含む) は受理し、--fast があるときだけ Fast。--fast なしは Strict
    [Theory]
    [InlineData(new[] { "a.zip", "--target", "dir" }, false, false, RunMode.Strict)]
    [InlineData(new[] { "a.zip", "--target", "dir", "--fast" }, false, false, RunMode.Fast)]
    [InlineData(new[] { "--fast", "a.zip", "--target", "dir" }, false, false, RunMode.Fast)]
    [InlineData(new[] { "a.zip", "--target", "dir", "--fast", "--dry-run" }, true, false, RunMode.Fast)]
    [InlineData(new[] { "a.zip", "--dry-run", "--target", "dir", "--fast" }, true, false, RunMode.Fast)]
    [InlineData(new[] { "a.zip", "--target", "dir", "--fast", "--yes" }, false, true, RunMode.Fast)]
    [InlineData(new[] { "--yes", "--fast", "a.zip", "--target", "dir" }, false, true, RunMode.Fast)]
    [InlineData(new[] { "a.zip", "-y", "--target", "dir", "--fast" }, false, true, RunMode.Fast)]
    [InlineData(new[] { "a.zip", "--fast", "--target", "dir", "--dry-run", "--yes" }, true, true, RunMode.Fast)]
    [InlineData(new[] { "a.zip", "--target", "dir", "--dry-run", "--yes" }, true, true, RunMode.Strict)]
    public void D26_FastOption(string[] args, bool dryRun, bool yes, RunMode mode)
    {
        var result = CommandLineParser.Parse(args);

        Assert.Null(result.Error);
        var options = Assert.IsType<CommandLineOptions>(result.Options);
        Assert.Equal("a.zip", options.ArchivePath);
        Assert.Equal("dir", options.TargetPath);
        Assert.Equal(dryRun, options.DryRun);
        Assert.Equal(yes, options.AssumeYes);
        Assert.Equal(mode, options.Mode);
    }

    public static TheoryData<string[]> InvalidFastArguments => new()
    {
        new string[] { "a.zip", "--target", "dir", "--fast", "--fast" },
        new string[] { "--fast", "a.zip", "--target", "dir", "--dry-run", "--fast" },
        new string[] { "a.zip", "--target", "dir", "--Fast" },
        new string[] { "a.zip", "--target", "dir", "--strict" }, // --strict は設けない (DEC-20)
    };

    // D26: --fast の重複は入力エラー (他のオプションの重複と同じ)。使い方の表示に [--fast] がある
    [Theory]
    [MemberData(nameof(InvalidFastArguments))]
    public void D26_FastOption_InvalidUsage_IsInputError(string[] args)
    {
        var result = CommandLineParser.Parse(args);

        Assert.Null(result.Options);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal("使い方: unextract <archive.zip> --target <dir> [--dry-run] [--fast] [--yes|-y]", CommandLineParser.Usage);
    }
}
