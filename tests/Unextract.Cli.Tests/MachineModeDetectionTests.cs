namespace Unextract.Cli.Tests;

public class MachineModeDetectionTests
{
    // J03: Parse の成否・位置・重複に関係なく、raw args 内の Ordinal 完全一致で判定する。
    [Theory]
    [InlineData(new[] { "analyze", "a.zip", "--target", "dir", "--jsonl" }, true)]
    [InlineData(new[] { "--jsonl", "analyze", "a.zip", "--target", "dir" }, true)]
    [InlineData(new[] { "a.zip", "--target", "dir", "--jsonl" }, true)]
    [InlineData(new[] { "delete", "--dry-run", "--jsonl" }, true)]
    [InlineData(new[] { "delete", "--jsonl", "--unknown" }, true)]
    [InlineData(new[] { "delete", "--jsonl", "--jsonl" }, true)]
    [InlineData(new[] { "analyze", "--target", "--jsonl" }, true)]
    [InlineData(new[] { "analyze", "--jsonl=x" }, false)]
    [InlineData(new[] { "analyze", "--JSONL" }, false)]
    [InlineData(new[] { "analyze", "--Jsonl" }, false)]
    [InlineData(new[] { "analyze", " --jsonl" }, false)]
    [InlineData(new[] { "analyze", "--jsonl " }, false)]
    [InlineData(new[] { "delete", "a.zip", "--target", "dir" }, false)]
    [InlineData(new string[0], false)]
    public void J03_DetectBeforeParsing(string[] args, bool expected)
    {
        Assert.Equal(expected, CliApplication.IsMachineMode(args));
    }
}
