using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Unextract.Gui.Models;
using Unextract.Gui.Services;

namespace Unextract.Gui.Tests;

public sealed class JsonlReceiverTests
{
    internal static CliJob Job(CliOperation operation = CliOperation.Analyze, CliMode mode = CliMode.Strict) =>
        new(operation, mode, @"C:\日本語 空白\archive.zip", @"C:\target",
            operation == CliOperation.Delete ? @"C:\entries.txt" : null,
            operation == CliOperation.Delete ? @"C:\log.jsonl" : null);

    internal static string Run(CliJob job, int total = 1, int? selected = null) => Line(new
    {
        v = 1, type = "run", operation = job.OperationValue, mode = job.ModeValue,
        archive = job.Archive, target = job.Target, entries_total = total,
        selected = selected ?? total, entries_option = job.Entries is not null,
    });
    internal static string Entry(string status = "MATCHED", int index = 1, long length = 123, string name = "日本語/顔-\U0001F642.txt") =>
        Line(new { v = 1, type = "entry", index, name, directory = status == "DIRECTORY", length, status });
    internal static string Completed(CliJob job, int files = 1, int directories = 0, int notSelected = 0) =>
        Line(new
        {
            v = 1, type = "result", outcome = "completed", exit_code = 0,
            counts = job.Operation == CliOperation.Analyze
                ? new Dictionary<string, int> { [job.Mode == CliMode.Strict ? "matched" : "same_size"] = files,
                    ["modified"] = 0, ["missing"] = 0, ["skipped_special_file"] = 0, ["directory"] = directories }
                : new Dictionary<string, int> { ["deleted"] = files, ["modified"] = 0, ["missing"] = 0,
                    ["skipped_special_file"] = 0, ["directory"] = directories, ["delete_failed"] = 0,
                    ["not_selected"] = notSelected, ["unprocessed"] = 0 },
        });
    internal static string Error(string outcome = "fatal", string stage = "prepare") => Line(new
    {
        v = 1, type = "result", outcome, exit_code = 1,
        error = new { stage, step = "future_step", code = "FUTURE_CODE", message = "診断の文言" },
    });
    internal static string WithCounts(string error, CliJob job)
    {
        var counts = JsonNode.Parse(Completed(job))!["counts"]!.DeepClone();
        if (job.Operation == CliOperation.Analyze) counts["undetermined"] = 0;
        return Change(error, "counts", counts);
    }
    internal static string Line(object value) => JsonSerializer.Serialize(value) + "\n";
    internal static string Change(string line, string field, JsonNode? value)
    {
        var root = JsonNode.Parse(line)!.AsObject();
        root[field] = value;
        return root.ToJsonString() + "\n";
    }
    internal static CliOutput Receive(string text, CliJob? job = null)
    {
        var receiver = new JsonlReceiver(job ?? Job());
        receiver.Feed(Encoding.UTF8.GetBytes(text));
        return receiver.Finish();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4096)]
    public void ArbitraryChunksDecodeRawNamesAndInt64(int chunk)
    {
        var job = Job();
        string name = "a\\日本語\u202E-\U0001F642.txt";
        byte[] bytes = Encoding.ASCII.GetBytes(Run(job) + Entry(length: long.MaxValue, name: name) + Completed(job));
        var receiver = new JsonlReceiver(job);
        for (int i = 0; i < bytes.Length; i += chunk) receiver.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)));
        var result = receiver.Finish();
        Assert.True(result.IsCompatible, result.IssueMessage);
        Assert.Equal(name, Assert.Single(result.Entries).Name);
        Assert.Equal(long.MaxValue, result.Entries[0].Length);
        Assert.Equal("completed", result.Result!.Outcome);
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("fast")]
    public void EmptyArchiveAndAllDirectoriesAreCompleted(string mode)
    {
        var job = Job(mode: mode == "fast" ? CliMode.Fast : CliMode.Strict);
        foreach (int directories in new[] { 0, 2 })
        {
            string lines = Run(job, directories);
            for (int i = 1; i <= directories; i++) lines += Entry("DIRECTORY", i, 0, $"dir{i}/");
            var result = Receive(lines + Completed(job, 0, directories), job);
            Assert.True(result.IsCompatible, result.IssueMessage);
            Assert.NotNull(result.Result);
        }
    }

    [Fact]
    public void UnterminatedFragmentsAreNotRecords()
    {
        var job = Job();
        var partial = Receive(Run(job) + Entry().TrimEnd('\n'));
        Assert.True(partial.IsCompatible);
        Assert.Empty(partial.Entries);
        Assert.Null(partial.Result);
        partial = Receive(Run(job) + Entry() + Completed(job).TrimEnd('\n'));
        Assert.Single(partial.Entries);
        Assert.Null(partial.Result);
        var complete = Receive(Run(job) + Entry() + Completed(job) + "broken trailing fragment");
        Assert.True(complete.IsCompatible);
        Assert.NotNull(complete.Result);
    }

    [Fact]
    public void UnknownFieldsAndDiagnosticValuesDoNotControlState()
    {
        var job = Job();
        string run = Change(Run(job), "future", JsonNode.Parse("{\"nested\":[null,3]}"));
        string entry = Change(Entry("SKIPPED_SPECIAL_FILE"), "skip_reason", JsonValue.Create("FUTURE_REASON"));
        var result = Receive(run + entry + WithCounts(Error("fatal", "entry"), job));
        Assert.True(result.IsCompatible, result.IssueMessage);
        Assert.Equal("FUTURE_REASON", result.Entries[0].SkipReason);
        Assert.Equal("future_step", result.Result!.Error!.Step);
        Assert.Equal("FUTURE_CODE", result.Result.Error.Code);
    }

    public static IEnumerable<object[]> CorruptOutputs()
    {
        var job = Job();
        string run = Run(job), entry = Entry(), result = Completed(job);
        yield return ["", "\n", ProtocolIssue.InvalidOutput];
        yield return ["malformed", "not-json\n", ProtocolIssue.InvalidOutput];
        yield return ["array", "[]\n", ProtocolIssue.InvalidOutput];
        yield return ["non-ASCII", "{\"text\":\"顔\"}\n", ProtocolIssue.InvalidOutput];
        yield return ["unknown version", Change(run, "v", JsonValue.Create(2)), ProtocolIssue.IncompatibleVersion];
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("v", JsonValue.Create("1")), ("type", JsonValue.Create("future")), ("operation", JsonValue.Create("delete")),
            ("mode", JsonValue.Create("fast")), ("target", null), ("entries_total", JsonValue.Create(-1)),
            ("selected", JsonValue.Create(2)), ("selected", JsonValue.Create(0)), ("entries_option", JsonValue.Create(true)),
            ("entries_total", JsonValue.Create(1.5)), ("archive", JsonValue.Create(42)),
        }) yield return [field, Change(run, field, value), ProtocolIssue.InvalidOutput];
        yield return ["duplicate run", run + run, ProtocolIssue.InvalidOutput];
        yield return ["entry before run", entry, ProtocolIssue.InvalidOutput];
        yield return ["duplicate entry", run + entry + entry, ProtocolIssue.InvalidOutput];
        yield return ["backwards entry", Run(job, 2) + Entry(index: 2), ProtocolIssue.InvalidOutput];
        yield return ["duplicate result", run + entry + result + result, ProtocolIssue.InvalidOutput];
        yield return ["entry after result", run + entry + result + entry, ProtocolIssue.InvalidOutput];
        yield return ["run after result", Error() + run, ProtocolIssue.InvalidOutput];
        yield return ["success without run", result, ProtocolIssue.InvalidOutput];
        yield return ["missing entries", run + result, ProtocolIssue.InvalidOutput];
        yield return ["count mismatch", run + entry + Completed(job, 2), ProtocolIssue.InvalidOutput];
        yield return ["success exit1", run + entry + Change(result, "exit_code", JsonValue.Create(1)), ProtocolIssue.InvalidOutput];
        yield return ["unknown outcome", Change(Error(), "outcome", JsonValue.Create("future")), ProtocolIssue.InvalidOutput];
        yield return ["failure exit0", Change(Error(), "exit_code", JsonValue.Create(0)), ProtocolIssue.InvalidOutput];
        yield return ["null error", Change(Error(), "error", null), ProtocolIssue.InvalidOutput];
        yield return ["missing error", "{\"v\":1,\"type\":\"result\",\"outcome\":\"fatal\",\"exit_code\":1}\n", ProtocolIssue.InvalidOutput];
        yield return ["input after run", run + Error("input_error"), ProtocolIssue.InvalidOutput];
        yield return ["duplicate property", run.Replace("\"v\":1", "\"v\":1,\"v\":1", StringComparison.Ordinal), ProtocolIssue.InvalidOutput];
        yield return ["unpaired surrogate", run + entry.Replace("\"name\":", "\"name\":\"\\uD800\",\"ignored\":", StringComparison.Ordinal), ProtocolIssue.InvalidOutput];
        yield return ["missing failure counts", run + Error("fatal", "entry"), ProtocolIssue.InvalidOutput];
        yield return ["missing completed counts", run + entry + Change(result, "counts", null), ProtocolIssue.InvalidOutput];
        yield return ["unknown error stage", Change(Error(), "error", JsonNode.Parse("{\"stage\":\"future\",\"code\":\"FUTURE\"}")), ProtocolIssue.InvalidOutput];
        yield return ["error bool type", Change(Error(), "error", JsonNode.Parse("{\"stage\":\"prepare\",\"code\":\"FUTURE\",\"possibly_deleted\":\"true\"}")), ProtocolIssue.InvalidOutput];
        yield return ["error index type", Change(Error(), "error", JsonNode.Parse("{\"stage\":\"prepare\",\"code\":\"FUTURE\",\"entry_index\":0}")), ProtocolIssue.InvalidOutput];
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("index", JsonValue.Create(0)), ("name", null), ("length", JsonValue.Create(-1)),
            ("length", JsonValue.Create("12")), ("directory", JsonValue.Create(true)), ("status", JsonValue.Create("SAME_SIZE")),
            ("status", JsonValue.Create("future")), ("skip_reason", JsonValue.Create("HARDLINK")),
            ("possibly_deleted", JsonValue.Create(false)),
        }) yield return [field, run + Change(entry, field, value), ProtocolIssue.InvalidOutput];
    }

    [Theory]
    [MemberData(nameof(CorruptOutputs))]
    public void InvalidRecordsAreNeverAdopted(string description, string text, object issue)
    {
        var result = Receive(text);
        Assert.False(result.IsCompatible, description);
        Assert.Equal((ProtocolIssue)issue, result.Issue);
        Assert.NotEmpty(result.IssueMessage!);
    }

    [Fact]
    public void DeleteAllowsIndexGapsAndCompletedExit1WithDeleteFailures()
    {
        var job = Job(CliOperation.Delete);
        string failure = Change(Entry("DELETE_FAILED", 3), "reason", JsonNode.Parse("{\"code\":\"FUTURE_CODE\",\"message\":\"表示\"}"));
        string result = Line(new { v = 1, type = "result", outcome = "completed", exit_code = 1,
            counts = new { deleted = 1, modified = 0, missing = 0, skipped_special_file = 0, directory = 0,
                delete_failed = 1, not_selected = 2, unprocessed = 0 } });
        var output = Receive(Run(job, 4, 2) + Entry("DELETED", 1) + failure + result, job);
        Assert.True(output.IsCompatible, output.IssueMessage);
        Assert.Equal(1, output.Result!.ExitCode);
        Assert.Equal(new[] { 1, 3 }, output.Entries.Select(x => x.Index));
    }

    [Fact]
    public void StoppedAndInternalErrorsKeepUncertaintyAndRejectLaterEntries()
    {
        var job = Job(CliOperation.Delete);
        string stopped = Change(Change(Entry("STOPPED", 2), "reason", JsonNode.Parse("{\"step\":\"future\",\"code\":\"FUTURE_CODE\",\"message\":\"表示\"}")),
            "possibly_deleted", JsonValue.Create(true));
        var output = Receive(Run(job, 4, 2) + stopped + WithCounts(Error("stopped", "entry"), job), job);
        Assert.True(output.IsCompatible, output.IssueMessage);
        Assert.True(output.Entries[0].PossiblyDeleted);
        output = Receive(Run(job, 4, 2) + stopped + Entry("DELETED", 4), job);
        Assert.False(output.IsCompatible);
        string internalError = Line(new { v = 1, type = "result", outcome = "internal_error", exit_code = 1,
            error = new { stage = "internal", code = "FUTURE_CODE", deletion_started = true, possibly_deleted = true } });
        output = Receive(Run(job, 4, 2) + WithCounts(internalError, job), job);
        Assert.True(output.IsCompatible);
        Assert.True(output.Result!.Error!.DeletionStarted);
        Assert.True(output.Result.Error.PossiblyDeleted);
    }
}
