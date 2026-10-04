using System.Collections;
using System.Globalization;

namespace Unextract.Gui.Models;

// A successful analysis, adopted only after CliJobResult.Succeeded. Entries are the raw received records:
// FullName and 64-bit length stay as the CLI reported them, display conversion is separate.
internal sealed class AnalysisSnapshot
{
    private AnalysisSnapshot(CliMode mode, string runTarget, IReadOnlyList<CliEntry> entries,
        IReadOnlyDictionary<string, int> counts, UInt128 candidateLength)
    {
        Mode = mode;
        RunTarget = runTarget;
        Entries = entries;
        StatusCounts = counts;
        CandidateLength = candidateLength;
    }

    public CliMode Mode { get; }
    public string RunTarget { get; }
    public IReadOnlyList<CliEntry> Entries { get; }
    // A completed analysis reports every ZIP entry (run.selected == run.entries_total == entry count).
    public int EntriesTotal => Entries.Count;
    public IReadOnlyDictionary<string, int> StatusCounts { get; }
    public UInt128 CandidateLength { get; }
    public string CandidateStatus => CandidateStatusOf(Mode);
    public int CandidateCount => Count(CandidateStatus);
    public int Count(string status) => StatusCounts.TryGetValue(status, out int count) ? count : 0;

    public static string CandidateStatusOf(CliMode mode) => mode == CliMode.Strict ? "MATCHED" : "SAME_SIZE";

    public static AnalysisSnapshot Create(CliMode mode, CliOutput output)
    {
        if (output.Run is null || output.Result is not { Outcome: "completed" } || !output.IsCompatible)
            throw new InvalidOperationException("正常完了した解析だけをスナップショットにできます。");
        string candidate = CandidateStatusOf(mode);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        UInt128 length = 0;
        foreach (var entry in output.Entries)
        {
            counts[entry.Status] = counts.GetValueOrDefault(entry.Status) + 1;
            // Per-entry values are non-negative 64-bit; the 128-bit sum cannot overflow for int-count entries.
            if (entry.Status == candidate && !entry.Directory) length += (ulong)entry.Length;
        }
        return new(mode, output.Run.Target, output.Entries, counts, length);
    }

    public string SummaryText()
    {
        var parts = new List<string>();
        parts.Add($"{(Mode == CliMode.Strict ? "Matched" : "Same Size")} {CandidateCount:N0}");
        parts.Add($"Modified {Count("MODIFIED"):N0}");
        parts.Add($"Missing {Count("MISSING"):N0}");
        parts.Add($"Skipped {Count("SKIPPED_SPECIAL_FILE"):N0}");
        if (Count("DIRECTORY") != 0) parts.Add($"Directory {Count("DIRECTORY"):N0}");
        return string.Join(" / ", parts);
    }

    // Raw CLI classification names, with the classes that cannot occur in this mode omitted.
    public IReadOnlyList<CategoryOption> Categories()
    {
        var list = new List<CategoryOption> { new("", $"すべて ({Entries.Count:N0})") };
        foreach (string status in new[] { CandidateStatus, "MODIFIED", "MISSING", "SKIPPED_SPECIAL_FILE", "DIRECTORY" })
            list.Add(new(status, $"{status} ({Count(status):N0})"));
        return list;
    }
}

internal sealed record CategoryOption(string Value, string Label);

internal static class SizeFormat
{
    public static string Bytes(UInt128 value)
    {
        string exact = value.ToString("N0", CultureInfo.CurrentCulture) + " bytes";
        if (value < 1024) return exact;
        string[] units = ["KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double scaled = (double)value;
        int unit = -1;
        while (scaled >= 1024 && unit < units.Length - 1) { scaled /= 1024; unit++; }
        return $"{exact} ({scaled:0.##} {units[unit]})";
    }

    // Compact form for list rows; the exact byte count is shown in the details and the confirmation.
    public static string Short(UInt128 value)
    {
        if (value < 1024) return value.ToString("N0", CultureInfo.CurrentCulture) + " bytes";
        string[] units = ["KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double scaled = (double)value;
        int unit = -1;
        while (scaled >= 1024 && unit < units.Length - 1) { scaled /= 1024; unit++; }
        return string.Create(CultureInfo.CurrentCulture, $"{scaled:0.##} {units[unit]}");
    }
}

internal sealed record EntryRow(CliEntry Entry)
{
    public string Status => Entry.Status;
    public string DisplayName => DisplayText.Escape(Entry.Name);
    public string LengthText => Entry.Directory ? "" : Entry.Length.ToString("N0", CultureInfo.CurrentCulture);
    public string Detail => Entry.SkipReason is null ? "" : "  (" + DisplayText.Escape(Entry.SkipReason) + ")";
}

// Rows are created when the virtualized list asks for them; only the filtered indexes are held.
internal sealed class EntryRowList : IList, IReadOnlyList<EntryRow>
{
    private readonly IReadOnlyList<CliEntry> _entries;
    private readonly int[]? _indexes;

    public EntryRowList(IReadOnlyList<CliEntry> entries, int[]? indexes = null)
    {
        _entries = entries;
        _indexes = indexes;
    }

    public int Count => _indexes?.Length ?? _entries.Count;
    public EntryRow this[int position] => new(_entries[_indexes is null ? position : _indexes[position]]);
    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public int IndexOf(object? value)
    {
        if (value is not EntryRow row) return -1;
        // Entries are in ascending ZIP index order; the filtered positions keep that order.
        int low = 0, high = Count - 1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            int current = _entries[_indexes is null ? mid : _indexes[mid]].Index;
            if (current == row.Entry.Index) return this[mid].Equals(row) ? mid : -1;
            if (current < row.Entry.Index) low = mid + 1; else high = mid - 1;
        }
        return -1;
    }
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }
    public IEnumerator<EntryRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
