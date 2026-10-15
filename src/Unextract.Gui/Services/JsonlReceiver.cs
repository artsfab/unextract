using System.Buffers;
using System.Collections.ObjectModel;
using System.Text.Json;
using Unextract.Gui.Models;

namespace Unextract.Gui.Services;

// Only the stdout reader mutates this receiver. Even after a bad record the caller drains to EOF.
internal sealed class JsonlReceiver(CliJob job)
{
    private readonly ArrayBufferWriter<byte> _line = new();
    private readonly List<CliEntry> _entries = [];
    private readonly List<CliDir> _dirs = [];
    private readonly HashSet<string> _dirPaths = new(StringComparer.Ordinal);
    private CliRun? _run;
    private CliResult? _result;
    private ProtocolIssue _issue;
    private string? _message;

    public CliRun? Run => _run;
    public int ReceivedEntries => _entries.Count;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (_issue != ProtocolIssue.None) return;
        while (!bytes.IsEmpty)
        {
            int lf = bytes.IndexOf((byte)'\n');
            var part = lf < 0 ? bytes : bytes[..lf];
            _line.Write(part);
            if (lf < 0) return;
            ReadLine(_line.WrittenMemory);
            _line.Clear();
            if (_issue != ProtocolIssue.None) return;
            bytes = bytes[(lf + 1)..];
        }
    }

    public CliOutput Finish()
    {
        // An unterminated last fragment is never a record, including after a valid result.
        _line.Clear();
        return new(_run, _entries.AsReadOnly(), _result, _issue, _message, _dirs.AsReadOnly());
    }

    public void ReadFailed(string message) => Fail(ProtocolIssue.InvalidOutput, message);

    private void ReadLine(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            foreach (byte b in bytes.Span) Require(b < 128, "JSONLがASCIIではありません。");
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            Object(root);
            long version = Integer(root, "v");
            if (version != 2)
            {
                Fail(ProtocolIssue.IncompatibleVersion, $"未対応のJSONL版です: {version}");
                return;
            }
            Require(_result is null, "resultの後にレコードがあります。");
            switch (String(root, "type"))
            {
                case "run": ReadRun(root); break;
                case "entry": ReadEntry(root); break;
                case "dir": ReadDir(root); break;
                case "result": ReadResult(root); break;
                default: throw new FormatException("未知のレコード種別です。");
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            Fail(ProtocolIssue.InvalidOutput, "JSONL出力が不正です: " + e.Message);
        }
    }

    private void ReadRun(JsonElement root)
    {
        Require(_run is null && _entries.Count == 0, "runが重複しています。");
        var run = new CliRun(String(root, "operation"), String(root, "mode"), String(root, "archive"),
            String(root, "target"), Count(root, "entries_total"), Count(root, "selected"), Bool(root, "entries_option"));
        Require(run.Operation == job.OperationValue && run.Mode == job.ModeValue, "起動指定とoperation/modeが異なります。");
        Require(run.EntriesOption == (job.Entries is not null), "entries_optionが起動指定と異なります。");
        Require(run.Selected <= run.EntriesTotal, "selectedが全エントリ数を超えています。");
        Require(job.Operation != CliOperation.Analyze || run.Selected == run.EntriesTotal, "解析のselectedが不正です。");
        if (job.Operation == CliOperation.Delete)
        {
            bool prune = Bool(root, "prune_dirs");
            Require(prune == job.PruneDirs, "prune_dirsが起動指定と異なります。");
            Require(prune || !root.TryGetProperty("dirs_selected", out _), "prune_dirsなしでdirs_selectedがあります。");
            run = run with { PruneDirs = prune, DirsSelected = prune ? Count(root, "dirs_selected") : null };
        }
        else Require(!root.TryGetProperty("prune_dirs", out _) && !root.TryGetProperty("dirs_selected", out _), "解析にprune_dirsがあります。");
        _run = run;
    }

    private void ReadEntry(JsonElement root)
    {
        Require(_run is not null, "runより前にentryがあります。");
        Require(_dirs.Count == 0, "dirの後にentryがあります。");
        var run = _run!;
        int index = Positive(root, "index");
        string name = String(root, "name");
        bool directory = Bool(root, "directory");
        long length = Integer(root, "length");
        string status = String(root, "status");
        int last = _entries.Count == 0 ? 0 : _entries[^1].Index;
        Require(index > last && index <= run.EntriesTotal && _entries.Count < run.Selected, "entryの順序・件数が不正です。");
        Require(length >= 0, "lengthが負です。");
        if (job.Operation == CliOperation.Analyze)
        {
            Require(index == last + 1, "解析entryのindexが連続していません。");
            Require(status is "MODIFIED" or "MISSING" or "SKIPPED_SPECIAL_FILE" or "DIRECTORY" ||
                status == (job.Mode == CliMode.Strict ? "MATCHED" : "SAME_SIZE"), "解析のstatusが不正です。");
            Require(directory == (status == "DIRECTORY") && (!directory || length == 0), "directory/lengthが不正です。");
        }
        else
        {
            Require(!directory && status is "DELETED" or "MODIFIED" or "MISSING" or "SKIPPED_SPECIAL_FILE" or "DELETE_FAILED" or "STOPPED",
                "削除のstatus/directoryが不正です。");
            Require(_entries.Count == 0 || _entries[^1].Status != "STOPPED", "STOPPEDの後にentryがあります。");
        }
        string? skip = OptionalString(root, "skip_reason");
        CliReason? reason = root.TryGetProperty("reason", out var r) ? ReadReason(r) : null;
        bool? possible = OptionalBool(root, "possibly_deleted");
        Require((skip is not null) == (status == "SKIPPED_SPECIAL_FILE"), "skip_reasonの有無が不正です。");
        Require((reason is not null) == (status is "DELETE_FAILED" or "STOPPED"), "reasonの有無が不正です。");
        Require(possible.HasValue == (status == "STOPPED"), "possibly_deletedの有無が不正です。");
        _entries.Add(new(index, name, directory, length, status, skip, reason, possible));
    }

    // The directory stage starts only after every selected file has an entry and none STOPPED (docs/spec/filesystem.md#prune-set).
    // The GUI always passes --entries, so selected counts files only.
    private void ReadDir(JsonElement root)
    {
        Require(_run is not null, "runより前にdirがあります。");
        var run = _run!;
        Require(run.PruneDirs == true, "--prune-dirsを付けていない実行にdirがあります。");
        Require(_entries.Count == run.Selected && (_entries.Count == 0 || _entries[^1].Status != "STOPPED"), "ファイルの処理が終わる前にdirがあります。");
        Require(_dirs.Count < run.DirsSelected && (_dirs.Count == 0 || _dirs[^1].Status != "STOPPED"), "dirの順序・件数が不正です。");
        string path = String(root, "path");
        string status = String(root, "status");
        Require(path.Length != 0 && _dirPaths.Add(path), "dirのpathが空か重複しています。");
        Require(status is "DIR_DELETED" or "DIR_KEPT" or "DIR_MISSING" or "STOPPED", "dirのstatusが不正です。");
        string? keep = OptionalString(root, "keep_reason");
        CliReason? reason = root.TryGetProperty("reason", out var r) ? ReadReason(r) : null;
        bool? possible = OptionalBool(root, "possibly_deleted");
        Require((keep is not null) == (status == "DIR_KEPT"), "keep_reasonの有無が不正です。");
        Require((reason is not null) == (status == "STOPPED"), "dirのreasonの有無が不正です。");
        Require(possible.HasValue == (status == "STOPPED"), "dirのpossibly_deletedの有無が不正です。");
        _dirs.Add(new(path, status, keep, reason, possible));
    }

    private void ReadResult(JsonElement root)
    {
        string outcome = String(root, "outcome");
        int exit = Count(root, "exit_code");
        Require(outcome is "completed" or "input_error" or "fatal" or "stopped" or "internal_error", "outcomeが不正です。");
        Require(exit <= 1 && (outcome == "completed" || exit == 1), "result.exit_codeが不正です。");
        Require(outcome != "completed" || _run is not null, "runなしでcompletedになっています。");
        Require(outcome != "stopped" || job.Operation == CliOperation.Delete && _run is not null &&
            (_dirs.Count > 0 ? _dirs[^1].Status == "STOPPED" : _entries.Count > 0 && _entries[^1].Status == "STOPPED"), "stoppedの順序が不正です。");
        Require(outcome != "input_error" || _run is null, "run後にinput_errorがあります。");
        Require(outcome != "fatal" || job.Operation == CliOperation.Analyze || _run is null, "削除run後にfatalがあります。");
        CliError? error = root.TryGetProperty("error", out var e) ? ReadError(e) : null;
        Require((error is null) == (outcome == "completed"), "errorの有無が不正です。");
        var counts = root.TryGetProperty("counts", out var c) ? ReadCounts(c) : null;
        if (_run is not null && (outcome is "fatal" or "stopped" ||
            job.Operation == CliOperation.Delete && outcome == "internal_error"))
        {
            string[] required = job.Operation == CliOperation.Analyze
                ? [job.Mode == CliMode.Strict ? "matched" : "same_size", "modified", "missing", "skipped_special_file", "directory", "undetermined"]
                : DeleteKeys();
            Require(counts is not null && required.All(counts.ContainsKey), "途中結果のcountsに必要な項目がありません。");
        }
        if (job.Operation == CliOperation.Delete && outcome == "internal_error")
            Require(error!.DeletionStarted.HasValue, "内部エラーにdeletion_startedがありません。");
        if (outcome == "completed") ValidateCompleted(counts, exit);
        _result = new(outcome, exit, counts, error);
    }

    private IReadOnlyDictionary<string, int> ReadCounts(JsonElement root)
    {
        Object(root);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string key in new[] { "matched", "same_size", "modified", "missing", "skipped_special_file", "directory",
            "undetermined", "deleted", "delete_failed", "not_selected", "unprocessed", "dir_deleted", "dir_kept", "dir_missing", "dir_unprocessed" })
        {
            if (root.TryGetProperty(key, out _)) counts.Add(key, Count(root, key));
        }
        return new ReadOnlyDictionary<string, int>(counts);
    }

    private void ValidateCompleted(IReadOnlyDictionary<string, int>? counts, int exit)
    {
        Require(counts is not null, "completedにcountsがありません。");
        string[] keys = job.Operation == CliOperation.Analyze
            ? [job.Mode == CliMode.Strict ? "matched" : "same_size", "modified", "missing", "skipped_special_file", "directory"]
            : ["deleted", "modified", "missing", "skipped_special_file", "directory", "delete_failed", "not_selected", "unprocessed"];
        Require((job.Operation == CliOperation.Analyze ? keys : DeleteKeys()).All(counts!.ContainsKey), "completedのcountsに必要な項目がありません。");
        Require(!counts.TryGetValue("undetermined", out int undetermined) || undetermined == 0, "completedに未判定があります。");
        foreach (string key in keys)
        {
            if (key is "undetermined" or "unprocessed") Require(counts[key] == 0, "completedに未処理があります。");
            else if (key == "not_selected") Require(counts[key] == _run!.EntriesTotal - _run.Selected, "not_selectedが不正です。");
            else if (job.Operation != CliOperation.Delete || key != "directory")
                Require(counts[key] == _entries.Count(x => x.Status.Equals(key, StringComparison.OrdinalIgnoreCase)), "countsとentryが異なります。");
        }
        Require(!_entries.Any(x => x.Status == "STOPPED"), "STOPPEDをcompletedとして受信しました。");
        if (job.Operation == CliOperation.Analyze)
            Require(exit == 0 && _entries.Count == _run!.Selected, "解析completedの件数/exit_codeが不正です。");
        else
        {
            Require((long)_entries.Count + counts["directory"] == _run!.Selected, "削除completedの件数が不正です。");
            Require(exit == (counts["delete_failed"] == 0 ? 0 : 1), "削除completedのexit_codeが不正です。");
            if (_run.PruneDirs == true)
            {
                Require(_dirs.Count == _run.DirsSelected && counts["dir_unprocessed"] == 0, "削除completedのdirの件数が不正です。");
                foreach (var (key, status) in new[] { ("dir_deleted", "DIR_DELETED"), ("dir_kept", "DIR_KEPT"), ("dir_missing", "DIR_MISSING") })
                    Require(counts[key] == _dirs.Count(x => x.Status == status), "countsとdirが異なります。");
            }
        }
    }

    private static CliReason ReadReason(JsonElement root)
    {
        Object(root);
        return new(OptionalString(root, "step"), String(root, "code"), OptionalInt(root, "win32_error"), String(root, "message"));
    }

    private static CliError ReadError(JsonElement root)
    {
        Object(root);
        string stage = String(root, "stage");
        Require(stage is "usage" or "prepare" or "entry" or "internal", "error.stageが不正です。");
        return new(stage, OptionalString(root, "step"), String(root, "code"), OptionalPositive(root, "entry_index"),
            OptionalString(root, "entry_name"), OptionalPositive(root, "line"), OptionalInt(root, "win32_error"),
            OptionalBool(root, "possibly_deleted"), OptionalBool(root, "deletion_started"), OptionalString(root, "message"),
            OptionalString(root, "dir_path"));
    }

    // The delete counts; with prune_dirs the directory-stage counts are required as well.
    private string[] DeleteKeys() =>
    [
        "deleted", "modified", "missing", "skipped_special_file", "directory", "delete_failed", "not_selected", "unprocessed",
        .. _run?.PruneDirs == true ? new[] { "dir_deleted", "dir_kept", "dir_missing", "dir_unprocessed" } : [],
    ];

    private void Fail(ProtocolIssue issue, string message)
    {
        if (_issue != ProtocolIssue.None) return;
        _issue = issue;
        _message = message;
        _line.Clear();
    }

    private static void Object(JsonElement root)
    {
        Require(root.ValueKind == JsonValueKind.Object, "JSONオブジェクトではありません。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject()) Require(names.Add(p.Name), "フィールドが重複しています。");
    }
    private static JsonElement Field(JsonElement root, string key)
    {
        Require(root.TryGetProperty(key, out var value), $"{key}がありません。");
        return value;
    }
    private static string String(JsonElement root, string key)
    {
        var value = Field(root, key);
        Require(value.ValueKind == JsonValueKind.String, $"{key}が文字列ではありません。");
        return value.GetString()!;
    }
    private static long Integer(JsonElement root, string key)
    {
        var value = Field(root, key);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), $"{key}が整数ではありません。");
        return value.GetInt64();
    }
    private static int Count(JsonElement root, string key)
    {
        long value = Integer(root, key);
        Require(value is >= 0 and <= int.MaxValue, $"{key}の数値範囲が不正です。");
        return (int)value;
    }
    private static int Positive(JsonElement root, string key)
    {
        int value = Count(root, key);
        Require(value > 0, $"{key}が1未満です。");
        return value;
    }
    private static bool Bool(JsonElement root, string key)
    {
        var value = Field(root, key);
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, $"{key}がboolではありません。");
        return value.GetBoolean();
    }
    private static string? OptionalString(JsonElement r, string k) => r.TryGetProperty(k, out _) ? String(r, k) : null;
    private static bool? OptionalBool(JsonElement r, string k) => r.TryGetProperty(k, out _) ? Bool(r, k) : null;
    private static int? OptionalInt(JsonElement r, string k) => r.TryGetProperty(k, out _) ? checked((int)Integer(r, k)) : null;
    private static int? OptionalPositive(JsonElement r, string k) => r.TryGetProperty(k, out _) ? Positive(r, k) : null;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new FormatException(message);
    }
}
