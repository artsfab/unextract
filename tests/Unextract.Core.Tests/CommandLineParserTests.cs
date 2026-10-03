using Unextract.Core.Analysis;
using Unextract.Core.CommandLine;

namespace Unextract.Core.Tests;

// CLI の引数 (SPEC §2、テスト K01〜K06)。
public class CommandLineParserTests
{
    private static CommandLineOptions Ok(params string[] args)
    {
        var result = CommandLineParser.Parse(args);
        Assert.Null(result.Error);
        return Assert.IsType<CommandLineOptions>(result.Options);
    }

    private static string Error(params string[] args)
    {
        var result = CommandLineParser.Parse(args);
        Assert.Null(result.Options);
        Assert.False(string.IsNullOrEmpty(result.Error));
        return result.Error!;
    }

    // K01: analyze・delete の正常な引数 (オプションの順序を変えたもの、--fast・--entries・--yes/-y の組み合わせ)。--fast なしは Strict。
    [Theory]
    [InlineData(new[] { "analyze", "a.zip", "--target", "dir" }, CommandKind.Analyze, RunMode.Strict, null, false)]
    [InlineData(new[] { "analyze", "--target", "dir", "a.zip" }, CommandKind.Analyze, RunMode.Strict, null, false)]
    [InlineData(new[] { "analyze", "a.zip", "--fast", "--target", "dir" }, CommandKind.Analyze, RunMode.Fast, null, false)]
    [InlineData(new[] { "analyze", "--fast", "--target", "dir", "a.zip" }, CommandKind.Analyze, RunMode.Fast, null, false)]
    [InlineData(new[] { "delete", "a.zip", "--target", "dir" }, CommandKind.Delete, RunMode.Strict, null, false)]
    [InlineData(new[] { "delete", "a.zip", "--target", "dir", "--yes" }, CommandKind.Delete, RunMode.Strict, null, true)]
    [InlineData(new[] { "delete", "-y", "a.zip", "--target", "dir" }, CommandKind.Delete, RunMode.Strict, null, true)]
    [InlineData(new[] { "delete", "a.zip", "--target", "dir", "--fast", "--yes" }, CommandKind.Delete, RunMode.Fast, null, true)]
    [InlineData(new[] { "delete", "--entries", "e.txt", "a.zip", "--target", "dir" }, CommandKind.Delete, RunMode.Strict, "e.txt", false)]
    [InlineData(new[] { "delete", "a.zip", "--fast", "--entries", "e.txt", "-y", "--target", "dir" }, CommandKind.Delete, RunMode.Fast, "e.txt", true)]
    public void K01_ValidArguments(string[] args, CommandKind command, RunMode mode, string? entries, bool yes)
    {
        var options = Ok(args);

        Assert.Equal(new CommandLineOptions(command, "a.zip", "dir", mode, entries, yes), options);
    }

    // K02: サブコマンドなし (旧形式を含む)、不明なサブコマンド、大小文字違い → 入力エラーと旧形式の廃止の案内。
    public static TheoryData<string[]> MissingOrUnknownSubcommand => new()
    {
        new string[0],
        new[] { "a.zip", "--target", "dir" },
        new[] { "a.zip", "--target", "dir", "--yes" },
        new[] { "--target", "dir", "a.zip" },
        new[] { "list", "a.zip", "--target", "dir" },
        new[] { "Analyze", "a.zip", "--target", "dir" },
        new[] { "DELETE", "a.zip", "--target", "dir" },
        new[] { "" },
    };

    [Theory]
    [MemberData(nameof(MissingOrUnknownSubcommand))]
    public void K02_MissingOrUnknownSubcommand_IsInputError(string[] args)
    {
        Assert.Equal(CommandLineParser.SubcommandRequired, Error(args));
    }

    // K03: --fast の重複、--entries の重複・値なし・--entries=x・値が - 始まり・空、--yes と -y の併用、--target の各不正、ZIP なし・2つ。
    public static TheoryData<string[]> InvalidArguments => new()
    {
        new[] { "delete", "a.zip" },
        new[] { "analyze", "--target", "dir" },
        new[] { "delete", "a.zip", "--target" },
        new[] { "delete", "a.zip", "--target", "--fast" },
        new[] { "delete", "a.zip", "--target", "" },
        new[] { "delete", "a.zip", "--target", "-dir" },
        new[] { "delete", "a.zip", "--target", "d1", "--target", "d2" },
        new[] { "delete", "a.zip", "b.zip", "--target", "dir" },
        new[] { "delete", "", "--target", "dir" },
        new[] { "delete", "a.zip", "--target", "dir", "--force" },
        new[] { "delete", "a.zip", "--target=dir" },
        new[] { "delete", "a.zip", "--target", "dir", "--yes", "-y" },
        new[] { "delete", "a.zip", "--target", "dir", "-y", "-y" },
        new[] { "delete", "a.zip", "--target", "dir", "-Y" },
        new[] { "delete", "a.zip", "--target", "dir", "-" },
        new[] { "delete", "a.zip", "--target", "dir", "--fast", "--fast" },
        new[] { "analyze", "a.zip", "--target", "dir", "--Fast" },
        new[] { "analyze", "a.zip", "--target", "dir", "--strict" },
        new[] { "delete", "a.zip", "--target", "dir", "--entries" },
        new[] { "delete", "a.zip", "--target", "dir", "--entries", "" },
        new[] { "delete", "a.zip", "--target", "dir", "--entries", "-e.txt" },
        new[] { "delete", "a.zip", "--target", "dir", "--entries", "--yes" },
        new[] { "delete", "a.zip", "--target", "dir", "--entries=e.txt" },
        new[] { "delete", "a.zip", "--target", "dir", "--entries", "e1.txt", "--entries", "e2.txt" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void K03_InvalidArguments_AreInputErrors(string[] args)
    {
        Error(args);
    }

    [Fact]
    public void K03_Messages()
    {
        Assert.Equal("--entries の値がありません", Error("delete", "a.zip", "--target", "dir", "--entries"));
        Assert.Equal("--entries が複数回指定されています", Error("delete", "a.zip", "--target", "dir", "--entries", "a", "--entries", "b"));
        Assert.Equal("--yes が複数回指定されています", Error("delete", "a.zip", "--target", "dir", "-y", "--yes"));
        Assert.Equal("不明なオプションです: --target=dir", Error("analyze", "a.zip", "--target=dir"));
        Assert.Equal("--fast が複数回指定されています", Error("analyze", "a.zip", "--target", "dir", "--fast", "--fast"));
    }

    // K04: --dry-run (analyze・delete・旧形式のそれぞれで、どの位置でも) → 入力エラーで analyze を案内。
    public static TheoryData<string[]> DryRunArguments => new()
    {
        new[] { "analyze", "a.zip", "--target", "dir", "--dry-run" },
        new[] { "delete", "--dry-run", "a.zip", "--target", "dir", "--yes" },
        new[] { "a.zip", "--target", "dir", "--dry-run" },
        new[] { "--dry-run", "a.zip", "--target", "dir" },
        new[] { "--dry-run" },
        new[] { "a.zip", "--dry-run", "--target", "dir", "--fast", "--yes" },
    };

    [Theory]
    [MemberData(nameof(DryRunArguments))]
    public void K04_DryRun_IsRemoved(string[] args)
    {
        Assert.Equal(CommandLineParser.DryRunRemoved, Error(args));
        Assert.Contains("unextract analyze", CommandLineParser.DryRunRemoved, StringComparison.Ordinal);
    }

    // K05: analyze に --yes / -y / --entries → 入力エラー。
    [Theory]
    [InlineData(new[] { "analyze", "a.zip", "--target", "dir", "--yes" }, "analyze では --yes を指定できません")]
    [InlineData(new[] { "analyze", "a.zip", "--target", "dir", "-y" }, "analyze では -y を指定できません")]
    [InlineData(new[] { "analyze", "a.zip", "--target", "dir", "--entries", "e.txt" }, "analyze では --entries を指定できません")]
    public void K05_AnalyzeRejectsDeleteOptions(string[] args, string error)
    {
        Assert.Equal(error, Error(args));
    }

    // K06: 使い方の表示は両サブコマンドの形 (PLAN.md §5.5)。
    [Fact]
    public void K06_Usage()
    {
        Assert.Equal(
            [
                "使い方: unextract analyze <archive.zip> --target <dir> [--fast]",
                "        unextract delete <archive.zip> --target <dir> [--fast] [--entries <file>] [--yes|-y]",
            ],
            CommandLineParser.UsageLines);
    }
}
