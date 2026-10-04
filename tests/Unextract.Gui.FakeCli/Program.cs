using System.Text;
using System.Text.Json;

// 偽 CLI。UNEXTRACT_FAKE_CLI_SCENARIO (シナリオ JSON のパス) が指す内容どおりに標準出力へ行を書くだけで、
// ZIP・target・entries を読み書き・削除しない (entries は内容を記録のために読むだけ)。
// シナリオ:
//   { "record": "<記録ファイル>", "responses": [ { "operation": "analyze|delete", "archive": "?", "target": "?",
//       "max_uses": n, "lines": ["<JSONL行>", ...], "exit_code": n, "stderr": "...",
//       "wait_for_file": "<解放ファイル>", "wait_after_lines": n, "wait_timeout_seconds": n } ] }
// 未指定の operation / archive / target / max_uses は条件なし。lines は検証せずそのまま書く。
// 一致する応答がなければ何も書かず終了コード 3。
internal static class Program
{
    private const string ScenarioVariable = "UNEXTRACT_FAKE_CLI_SCENARIO";

    private static int Main(string[] args)
    {
        string? scenarioPath = Environment.GetEnvironmentVariable(ScenarioVariable);
        if (string.IsNullOrEmpty(scenarioPath))
        {
            Console.Error.WriteLine($"fake-cli: {ScenarioVariable} is not set.");
            return 3;
        }
        JsonElement scenario;
        try { scenario = JsonDocument.Parse(File.ReadAllText(scenarioPath)).RootElement; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"fake-cli: cannot read the scenario: {e.Message}");
            return 3;
        }

        string? recordPath = Text(scenario, "record");
        string? operation = args.Length > 0 ? args[0] : null;
        string? archive = args.Length > 1 ? args[1] : null;
        string? target = OptionValue(args, "--target");
        string? entriesPath = OptionValue(args, "--entries");
        string? logPath = OptionValue(args, "--log");

        int index = -1;
        JsonElement response = default;
        if (scenario.TryGetProperty("responses", out var responses) && responses.ValueKind == JsonValueKind.Array)
        {
            var used = recordPath is null ? [] : ReadRecordedResponseIndexes(recordPath);
            int i = 0;
            foreach (var candidate in responses.EnumerateArray())
            {
                if (Matches(candidate, "operation", operation) && Matches(candidate, "archive", archive) && Matches(candidate, "target", target) &&
                    (!candidate.TryGetProperty("max_uses", out var max) || used.Count(u => u == i) < max.GetInt32()))
                {
                    index = i;
                    response = candidate;
                    break;
                }
                i++;
            }
        }

        if (recordPath is not null) WriteRecord(recordPath, index, args, entriesPath);
        if (index < 0)
        {
            Console.Error.WriteLine("fake-cli: no response in the scenario matches this invocation.");
            return 3;
        }

        FileStream? log = null;
        try
        {
            if (logPath is not null)
            {
                try { log = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"fake-cli: cannot create the log: {e.Message}");
                    return 4;
                }
            }
            var lines = response.TryGetProperty("lines", out var l) ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
            int waitAfter = response.TryGetProperty("wait_after_lines", out var w) ? Math.Min(w.GetInt32(), lines.Length) : lines.Length;
            string? waitFor = Text(response, "wait_for_file");
            using var stdout = Console.OpenStandardOutput();
            for (int n = 0; n < lines.Length; n++)
            {
                if (n == waitAfter && waitFor is not null && !WaitForFile(waitFor, response)) return 5;
                byte[] bytes = Encoding.UTF8.GetBytes(lines[n] + "\n");
                // Like the real CLI: the log first, then standard output.
                log?.Write(bytes);
                log?.Flush();
                stdout.Write(bytes);
                stdout.Flush();
            }
            if (lines.Length == waitAfter && waitFor is not null && !WaitForFile(waitFor, response)) return 5;
            if (Text(response, "stderr") is { } stderr) Console.Error.Write(stderr);
            return response.TryGetProperty("exit_code", out var exit) ? exit.GetInt32() : 0;
        }
        finally { log?.Dispose(); }
    }

    private static bool WaitForFile(string path, JsonElement response)
    {
        int seconds = response.TryGetProperty("wait_timeout_seconds", out var t) ? t.GetInt32() : 120;
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow > limit)
            {
                Console.Error.WriteLine("fake-cli: timed out waiting for the release file.");
                return false;
            }
            Thread.Sleep(20);
        }
        return true;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Matches(JsonElement response, string name, string? actual) =>
        !response.TryGetProperty(name, out var expected) || string.Equals(expected.GetString(), actual, StringComparison.OrdinalIgnoreCase);

    private static string? OptionValue(string[] args, string option)
    {
        int at = Array.IndexOf(args, option);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static List<int> ReadRecordedResponseIndexes(string recordPath)
    {
        var result = new List<int>();
        if (!File.Exists(recordPath)) return result;
        using var stream = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            using var json = JsonDocument.Parse(line);
            result.Add(json.RootElement.GetProperty("response").GetInt32());
        }
        return result;
    }

    // One JSON line per invocation, written before any output so that tests can see an invocation that is still waiting.
    private static void WriteRecord(string recordPath, int response, string[] args, string? entriesPath)
    {
        byte[]? entries = null;
        if (entriesPath is not null)
        {
            try { entries = File.ReadAllBytes(entriesPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("response", response);
            json.WriteStartArray("args");
            foreach (string arg in args) json.WriteStringValue(arg);
            json.WriteEndArray();
            if (entries is not null)
            {
                json.WriteBase64String("entries_base64", entries);
                json.WriteNumber("entries_bytes", entries.Length);
            }
            json.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        using var file = new FileStream(recordPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        file.Write(buffer.ToArray());
        file.Flush();
    }
}
