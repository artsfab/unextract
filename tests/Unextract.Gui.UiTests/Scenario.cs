using System.Text;
using System.Text.Json;

namespace Unextract.Gui.UiTests;

// Builds JSONL v1 lines (machine-output.md) for the fake CLI. Independent of the GUI's own receiver types.
// Output is ASCII (System.Text.Json escapes non-ASCII), like the real CLI. FakeCliShapeTests compares these
// lines with what the bundled real CLI writes for the same fixtures.
internal static class Jsonl
{
    public static string Run(string operation, string mode, string archive, string target, int entriesTotal, int selected, bool entriesOption) =>
        Line(w =>
        {
            w.WriteString("type", "run");
            w.WriteString("operation", operation);
            w.WriteString("mode", mode);
            w.WriteString("archive", archive);
            w.WriteString("target", target);
            w.WriteNumber("entries_total", entriesTotal);
            w.WriteNumber("selected", selected);
            w.WriteBoolean("entries_option", entriesOption);
        });

    public static string Entry(int index, string name, string status, long length = 0, bool directory = false,
        string? skipReason = null, string? reasonCode = null, string? reasonStep = null, bool? possiblyDeleted = null) =>
        Line(w =>
        {
            w.WriteString("type", "entry");
            w.WriteNumber("index", index);
            w.WriteString("name", name);
            w.WriteBoolean("directory", directory);
            w.WriteNumber("length", length);
            w.WriteString("status", status);
            if (skipReason is not null) w.WriteString("skip_reason", skipReason);
            if (reasonCode is not null)
            {
                w.WriteStartObject("reason");
                if (reasonStep is not null) w.WriteString("step", reasonStep);
                w.WriteString("code", reasonCode);
                w.WriteString("message", "fake reason");
                w.WriteEndObject();
            }
            if (possiblyDeleted is not null) w.WriteBoolean("possibly_deleted", possiblyDeleted.Value);
        });

    public static string Completed(int exitCode, params (string Key, int Value)[] counts) =>
        Line(w =>
        {
            w.WriteString("type", "result");
            w.WriteString("outcome", "completed");
            w.WriteNumber("exit_code", exitCode);
            w.WriteStartObject("counts");
            foreach (var (key, value) in counts) w.WriteNumber(key, value);
            w.WriteEndObject();
        });

    // A run that ends before the first entry (no run record): input_error, exit code 1.
    public static string InputError(string code, string message) =>
        Line(w =>
        {
            w.WriteString("type", "result");
            w.WriteString("outcome", "input_error");
            w.WriteNumber("exit_code", 1);
            w.WriteStartObject("error");
            w.WriteString("stage", "prepare");
            w.WriteString("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
        });

    private static string Line(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

// One scenario file for the fake CLI: responses are matched by operation (and optionally archive / target).
internal sealed class Scenario
{
    private readonly List<Dictionary<string, object?>> _responses = [];
    private readonly List<string> _releases = [];

    public Scenario(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "scenario.json");
        RecordPath = System.IO.Path.Combine(directory, "record.jsonl");
        Folder = directory;
    }

    public string Folder { get; }
    public string Path { get; }
    public string RecordPath { get; }

    // wait: the fake emits `waitAfterLines` lines, then blocks until the test creates the returned release file.
    public string Add(string operation, string[] lines, int exitCode = 0, string? archive = null, string? target = null,
        int? maxUses = null, bool wait = false, int waitAfterLines = 0)
    {
        string? release = wait ? System.IO.Path.Combine(Folder, $"release-{_responses.Count}-{Guid.NewGuid():N}") : null;
        var response = new Dictionary<string, object?> { ["operation"] = operation, ["lines"] = lines, ["exit_code"] = exitCode };
        if (archive is not null) response["archive"] = archive;
        if (target is not null) response["target"] = target;
        if (maxUses is not null) response["max_uses"] = maxUses;
        if (release is not null)
        {
            _releases.Add(release);
            response["wait_for_file"] = release;
            response["wait_after_lines"] = waitAfterLines;
            response["wait_timeout_seconds"] = 90;
        }
        _responses.Add(response);
        return release ?? "";
    }

    // Lets a waiting fake CLI continue.
    public void Release(string releaseFile) => File.WriteAllText(releaseFile, "");

    public void ReleaseAll()
    {
        foreach (string release in _releases) if (!File.Exists(release)) Release(release);
    }

    public void Save() => File.WriteAllText(Path, JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["record"] = RecordPath,
        ["responses"] = _responses,
    }), new UTF8Encoding(false));

    public IReadOnlyList<FakeInvocation> Invocations() => FakeInvocation.ReadAll(RecordPath);
}

// One line of the fake CLI's record file.
internal sealed record FakeInvocation(int Response, string[] Args, byte[]? Entries)
{
    public string Operation => Args[0];
    public string? Option(string name) { int at = Array.IndexOf(Args, name); return at >= 0 && at + 1 < Args.Length ? Args[at + 1] : null; }
    public string[] EntryLines => Entries is null ? [] : Encoding.UTF8.GetString(Entries).Split('\n').SkipLast(1).ToArray();

    public static IReadOnlyList<FakeInvocation> ReadAll(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new List<FakeInvocation>();
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            result.Add(new(root.GetProperty("response").GetInt32(),
                root.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray(),
                root.TryGetProperty("entries_base64", out var e) ? e.GetBytesFromBase64() : null));
        }
        return result;
    }
}
