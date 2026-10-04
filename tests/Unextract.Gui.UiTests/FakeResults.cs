namespace Unextract.Gui.UiTests;

internal sealed record Item(int Index, string Name, string Status, long Length = 0, bool Directory = false);

// Complete, well-formed JSONL v1 runs for the fake CLI, in the shape of the real CLI's normal outputs.
internal static class FakeResults
{
    public static string[] Analysis(string archive, string target, string mode, params Item[] all)
    {
        var lines = new List<string> { Jsonl.Run("analyze", mode, archive, target, all.Length, all.Length, entriesOption: false) };
        lines.AddRange(all.Select(Entry));
        int Count(string status) => all.Count(i => i.Status == status);
        var counts = new List<(string, int)>();
        if (mode == "strict") counts.Add(("matched", Count("MATCHED")));
        else counts.Add(("same_size", Count("SAME_SIZE")));
        counts.Add(("modified", Count("MODIFIED")));
        counts.Add(("missing", Count("MISSING")));
        counts.Add(("skipped_special_file", Count("SKIPPED_SPECIAL_FILE")));
        counts.Add(("directory", Count("DIRECTORY")));
        lines.Add(Jsonl.Completed(0, counts.ToArray()));
        return [.. lines];
    }

    // `processed` are the selected file entries in the order the CLI handles them; `selected` is the number of
    // entries lines, `notSelected` the remaining entries of the archive.
    public static string[] Delete(string archive, string target, string mode, int entriesTotal, int selected, Item[] processed, int notSelected)
    {
        var lines = new List<string> { Jsonl.Run("delete", mode, archive, target, entriesTotal, selected, entriesOption: true) };
        lines.AddRange(processed.Select(i => i.Status == "DELETE_FAILED"
            ? Jsonl.Entry(i.Index, i.Name, i.Status, i.Length, i.Directory, reasonCode: "DELETE_OPEN_REFUSED", reasonStep: "open")
            : Entry(i)));
        int Count(string status) => processed.Count(i => i.Status == status);
        int failed = Count("DELETE_FAILED");
        lines.Add(Jsonl.Completed(failed == 0 ? 0 : 1,
            ("modified", Count("MODIFIED")), ("missing", Count("MISSING")), ("skipped_special_file", Count("SKIPPED_SPECIAL_FILE")),
            ("directory", 0), ("deleted", Count("DELETED")), ("delete_failed", failed), ("not_selected", notSelected), ("unprocessed", 0)));
        return [.. lines];
    }

    private static string Entry(Item i) => Jsonl.Entry(i.Index, i.Name, i.Status, i.Length, i.Directory);
}
