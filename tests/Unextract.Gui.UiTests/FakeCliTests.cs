using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Unextract.Gui.UiTests;

// Non-UI tests of the fake CLI (it stands in for the bundled CLI in the UI tests) and of how closely its normal
// outputs follow the real CLI. The real CLI only touches self-made GUID fixtures under the test output.
[Collection("ui")]
public sealed class FakeCliTests
{
    internal sealed record Output(string[] Lines, int ExitCode, string Stderr);

    internal static Output Run(string exe, IEnumerable<string> args, IDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string a in args) info.ArgumentList.Add(a);
        info.Environment.Remove("UNEXTRACT_FAKE_CLI_SCENARIO");
        foreach (var (k, v) in environment ?? new Dictionary<string, string>()) info.Environment[k] = v;
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "the CLI did not exit");
        string text = stdout.GetAwaiter().GetResult();
        Assert.True(text.Length == 0 || text.EndsWith('\n'), "the output must end with LF");
        return new(text.Length == 0 ? [] : text.TrimEnd('\n').Split('\n'), process.ExitCode, stderr.GetAwaiter().GetResult());
    }

    private static string RealExe => Path.Combine(UiEnvironment.RealPackage, "cli", "unextract.exe");
    private static string FakeExe => Path.Combine(UiEnvironment.FakePackage, "cli", "unextract.exe");

    private static void AssertSameRecords(string[] expected, string[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected[i]), JsonNode.Parse(actual[i])), $"record {i + 1}:\n  fake: {expected[i]}\n  real: {actual[i]}");
    }

    [Fact]
    public void FakeNormalOutputsHaveTheSameRecordsAsTheRealCli()
    {
        string root = UiEnvironment.NewFixture("shape");
        string archive = Path.Combine(root, "日本語 a.zip");
        Files.Zip(archive, ("d/", ""), ("matched.txt", "abc"), ("same.txt", "abc"), ("size.txt", "abc"), ("missing.txt", "abc"));
        string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "matched.txt"), "abc");
        File.WriteAllText(Path.Combine(target, "same.txt"), "xyz");
        File.WriteAllText(Path.Combine(target, "size.txt"), "abcdef");
        var entries = new[]
        {
            new Item(1, "d/", "DIRECTORY", 0, true), new Item(2, "matched.txt", "MATCHED", 3), new Item(3, "same.txt", "MODIFIED", 3),
            new Item(4, "size.txt", "MODIFIED", 3), new Item(5, "missing.txt", "MISSING", 3),
        };

        var strict = Run(RealExe, ["analyze", archive, "--target", target, "--jsonl"]);
        Assert.Equal(0, strict.ExitCode);
        AssertSameRecords(FakeResults.Analysis(archive, target, "strict", entries), strict.Lines);

        var fast = Run(RealExe, ["analyze", archive, "--target", target, "--fast", "--jsonl"]);
        Assert.Equal(0, fast.ExitCode);
        AssertSameRecords(FakeResults.Analysis(archive, target, "fast",
            entries[0], entries[1] with { Status = "SAME_SIZE" }, entries[2] with { Status = "SAME_SIZE" }, entries[3], entries[4]), fast.Lines);

        string list = Path.Combine(root, "entries.txt");
        File.WriteAllText(list, "matched.txt\nsame.txt\nmissing.txt\n");
        string log = Path.Combine(root, "delete.jsonl");
        var delete = Run(RealExe, ["delete", archive, "--target", target, "--entries", list, "--yes", "--jsonl", "--log", log]);
        Assert.Equal(0, delete.ExitCode);
        AssertSameRecords(FakeResults.Delete(archive, target, "strict", 5, 3,
            [entries[1] with { Status = "DELETED" }, entries[2], entries[4]], notSelected: 2), delete.Lines);
        Assert.False(File.Exists(Path.Combine(target, "matched.txt")));
        Assert.True(File.Exists(Path.Combine(target, "same.txt")));
    }

    [Fact]
    public void FakeWritesTheScenarioRecordsEntriesAndLogAndDoesNotTouchTheFiles()
    {
        string root = UiEnvironment.NewFixture("fake");
        string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        string guard = Path.Combine(target, "keep.txt");
        File.WriteAllText(guard, "keep");
        string archive = Path.Combine(root, "a.zip");
        string entries = Path.Combine(root, "entries.txt");
        byte[] entriesBytes = Encoding.UTF8.GetBytes("日本語.txt\nsub/b.txt\n");
        File.WriteAllBytes(entries, entriesBytes);
        string log = Path.Combine(root, "log.jsonl");
        string[] lines = FakeResults.Delete(archive, target, "strict", 2, 2, [new(1, "日本語.txt", "DELETED", 1), new(2, "sub/b.txt", "DELETED", 1)], 0);
        var scenario = new Scenario(Path.Combine(root, "scenario"));
        scenario.Add("delete", lines, maxUses: 1);
        scenario.Save();
        var env = new Dictionary<string, string> { ["UNEXTRACT_FAKE_CLI_SCENARIO"] = scenario.Path };
        string[] args = ["delete", archive, "--target", target, "--entries", entries, "--yes", "--jsonl", "--log", log];

        var first = Run(FakeExe, args, env);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(lines, first.Lines);
        Assert.All(first.Lines, l => Assert.True(l.All(c => c < 0x80), "ASCII output"));
        Assert.Equal(string.Concat(lines.Select(l => l + "\n")), File.ReadAllText(log));
        var recorded = Assert.Single(scenario.Invocations());
        Assert.Equal(args, recorded.Args);
        Assert.Equal(entriesBytes, recorded.Entries);
        Assert.Equal(["日本語.txt", "sub/b.txt"], recorded.EntryLines);
        Assert.Equal("keep", File.ReadAllText(guard));

        // max_uses reached: no matching response, nothing on stdout, exit 3; the invocation is still recorded.
        var second = Run(FakeExe, args, env);
        Assert.Equal(3, second.ExitCode);
        Assert.Empty(second.Lines);
        Assert.Equal(2, scenario.Invocations().Count);
        Assert.Equal(-1, scenario.Invocations()[1].Response);
    }

    [Fact]
    public void FakeFailsWithoutAScenarioAndWaitsForTheReleaseFile()
    {
        var none = Run(FakeExe, ["analyze", "x.zip", "--target", "y", "--jsonl"]);
        Assert.Equal(3, none.ExitCode);
        Assert.Empty(none.Lines);

        string root = UiEnvironment.NewFixture("fake-wait");
        var scenario = new Scenario(Path.Combine(root, "scenario"));
        string[] lines = FakeResults.Analysis("a.zip", "t", "strict", new Item(1, "a.txt", "MATCHED", 1));
        string release = scenario.Add("analyze", lines, wait: true, waitAfterLines: 2);
        scenario.Save();
        var info = new ProcessStartInfo(FakeExe) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.Environment["UNEXTRACT_FAKE_CLI_SCENARIO"] = scenario.Path;
        foreach (string a in new[] { "analyze", "a.zip", "--target", "t", "--jsonl" }) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        // run + the first entry come out, then it waits: the process is still alive and the last line is not out yet.
        var reader = process.StandardOutput;
        Assert.Equal(lines[0], reader.ReadLine());
        Assert.Equal(lines[1], reader.ReadLine());
        Assert.False(process.WaitForExit(500));
        Assert.Single(scenario.Invocations());
        File.WriteAllText(release, "");
        Assert.Equal(lines[2], reader.ReadLine());
        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(0, process.ExitCode);
    }
}
