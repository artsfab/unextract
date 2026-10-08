using System.Globalization;

namespace Unextract.Gui.Models;

internal enum DeleteReportKind { Completed, NoRun, WithResult, Interrupted, Unknown }

// What the GUI may say about one finished (or lost) delete process. Success is exactly CliJobResult.Succeeded.
// Everything else is read from what was actually received, following docs/spec/machine-output.md#boundary:
// received values are never extrapolated into a total, and the approved candidates (ZIP index order, not the
// entries file order) are only used to name the one possibly-deleted entry and count the untouched rest.
// That naming needs the delete-time ZIP order to equal the analysis-time order; when the received output shows
// otherwise, no single entry is named and every approved candidate without an entry record is reported unknown.
// An entry the CLI itself reports (result.error possibly_deleted + entry_name) takes precedence over the inference.
// LogConfirmed: a received run proves the CLI created the log (it writes each record to the log first).
internal sealed record DeleteReport(DeleteReportKind Kind, string Outcome, string Detail, int DeletedCount,
    UInt128? DeletedLength, CliEntry? Unknown, int Unprocessed, int UnknownRemaining,
    IReadOnlyList<CliEntry> PossiblyDeleted, bool LogConfirmed)
{
    public bool Succeeded => Kind == DeleteReportKind.Completed;

    public const string CompletedOutcome = "削除完了";
    private const int ListLimit = 20;

    public static DeleteReport Create(IReadOnlyList<CliEntry> approved, int analysisEntriesTotal, string analysisRunTarget,
        CliJobResult result)
    {
        var output = result.Output;
        bool logConfirmed = output.Run is not null;
        var approvedByName = approved.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var received = output.Entries;
        string? unknownCause =
            !output.IsCompatible ? "出力が非互換または異常のため" :
            !result.ExitConfirmed ? "CLIの実終了を確認できないため" :
            !result.StdoutEof ? "出力を最後まで受信できていないため" :
            // The CLI processes only the entries it was given; anything else is not a result the GUI can read.
            output.Run is not null && (output.Run.Selected != approved.Count || received.Any(e => !approvedByName.ContainsKey(e.Name)) ||
                output.Result?.Error?.EntryName is { } errorName && !approvedByName.ContainsKey(errorName))
                ? "CLIの出力が承認した削除対象と一致しないため" : null;
        if (unknownCause is not null)
        {
            // Nothing in a partly read, incompatible, unfinished or out-of-scope stream is promoted to a fact.
            return new(DeleteReportKind.Unknown, "結果不明",
                unknownCause + "、結果を確定できません。削除されたファイルがある可能性があります。" +
                "実行ログで確認してください。\n" + CliJobText.Describe(result), 0, null, null, 0, 0, [], logConfirmed);
        }
        var lines = new List<string>();
        var deleted = received.Where(e => e.Status == "DELETED").ToArray();
        var possibly = received.Where(e => e.PossiblyDeleted == true).ToList();
        if (output.Run is null)
        {
            lines.Add("CLIは削除を開始していません。削除は0件です。");
            AddResult(lines, output.Result, result);
            if (output.Result is null) lines.Add("resultを受信できませんでした。終了コード: " + result.ExitCode);
            return new(DeleteReportKind.NoRun, "エラー終了（削除0件）", string.Join("\n", lines), 0, 0, null, 0, 0, [], logConfirmed);
        }
        if (!StringComparer.OrdinalIgnoreCase.Equals(output.Run.Target, analysisRunTarget))
            lines.Add($"CLIが報告したtarget: {DisplayText.Escape(output.Run.Target)}（解析時: {DisplayText.Escape(analysisRunTarget)}）。表示のみで、判定には使っていません。");
        // Visible evidence that the ZIP changed after analysis: its entry count or a received index differs.
        bool zipChanged = output.Run.EntriesTotal != analysisEntriesTotal ||
            received.Any(e => approvedByName[e.Name].Index != e.Index);
        if (zipChanged)
            lines.Add($"アーカイブのエントリ数またはentryの番号が解析時と異なります（解析時 {analysisEntriesTotal:N0}、削除時 {output.Run.EntriesTotal:N0} エントリ）。解析後にアーカイブが変更された可能性があります。");
        if (result.Succeeded)
        {
            UInt128 length = 0;
            foreach (var entry in deleted) length += (ulong)entry.Length;
            lines.Insert(0, string.Create(CultureInfo.CurrentCulture,
                $"削除成功: {deleted.Length:N0} 件 / 削除したファイルの論理サイズ合計: {SizeFormat.Bytes(length)}"));
            var counts = output.Result!.Counts;
            if (counts is not null)
            {
                string others = string.Join(" / ", new[] { ("modified", "MODIFIED"), ("missing", "MISSING"),
                    ("skipped_special_file", "SKIPPED_SPECIAL_FILE"), ("not_selected", "処理対象外") }
                    .Where(p => counts.GetValueOrDefault(p.Item1) != 0).Select(p => $"{p.Item2} {counts[p.Item1]:N0}"));
                if (others.Length != 0) lines.Add("削除されなかった件数: " + others);
            }
            return new(DeleteReportKind.Completed, CompletedOutcome, string.Join("\n", lines), deleted.Length, length, null, 0, 0, [], logConfirmed);
        }

        bool interrupted = output.Result is null ||
            (output.Result.Outcome == "internal_error" && output.Result.Error?.DeletionStarted == true);
        AddResult(lines, output.Result, result);
        if (output.Result is null) lines.Add("resultを受信できませんでした。終了コード: " + result.ExitCode);
        var resultCounts = output.Result?.Counts;
        if (resultCounts is not null)
        {
            var parts = new[] { ("deleted", "削除成功"), ("delete_failed", "DELETE_FAILED"), ("modified", "MODIFIED"), ("missing", "MISSING"),
                ("skipped_special_file", "SKIPPED_SPECIAL_FILE"), ("directory", "DIRECTORY"), ("not_selected", "処理対象外"), ("unprocessed", "未処理") }
                .Where(p => resultCounts.ContainsKey(p.Item1)).Select(p => $"{p.Item2} {resultCounts[p.Item1]:N0}");
            lines.Add("resultの件数: " + string.Join(" / ", parts));
        }
        else
        {
            lines.Add($"受信できたentry: {received.Count:N0} 件（DELETED {deleted.Length:N0} 件）。resultの件数は受信していません。");
        }
        UInt128 receivedLength = 0;
        foreach (var entry in deleted) receivedLength += (ulong)entry.Length;
        lines.Add("受信したDELETEDのlength合計: " + SizeFormat.Bytes(receivedLength) +
            "（受信できた範囲だけです。総削除サイズではありません）");
        var failed = received.Where(e => e.Status == "DELETE_FAILED").ToArray();
        foreach (var entry in failed.Take(ListLimit))
            lines.Add($"DELETE_FAILED: #{entry.Index} {DisplayText.Escape(entry.Name)} [{DisplayText.Escape(entry.Reason?.Code ?? "")}]");
        if (failed.Length > ListLimit) lines.Add($"DELETE_FAILED: ほか {failed.Length - ListLimit:N0} 件");
        // The CLI's own report of the entry it may have deleted is used as given, with or without a name.
        string? reportedName = null;
        if (output.Result?.Error is { PossiblyDeleted: true } error)
        {
            reportedName = error.EntryName;
            if (reportedName is null)
                lines.Add("削除された可能性あり: " + (error.EntryIndex is { } i ? $"#{i}（CLIは対象名を報告していません）" : "（CLIは対象を報告していません）"));
            else if (possibly.All(p => p.Name != reportedName))
                lines.Add($"削除された可能性あり: {DisplayText.Escape(reportedName)}");
        }
        foreach (var entry in possibly)
            lines.Add($"削除された可能性あり: #{entry.Index} {DisplayText.Escape(entry.Name)}");

        CliEntry? unknown = null;
        int unprocessed = 0, unknownRemaining = 0;
        if (interrupted && zipChanged)
        {
            // The delete-time order is not the analysis order, so "the next one" cannot be named safely.
            var seen = received.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var remaining = approved.Where(c => !seen.Contains(c.Name) && c.Name != reportedName).ToArray();
            unknownRemaining = remaining.Length;
            lines.Add("解析後にアーカイブの内容または順序が変わったため、途絶した位置の次の1件を特定できません。");
            if (remaining.Length != 0)
            {
                lines.Add($"結果不明（削除された可能性あり）: entryを受信していない承認候補 {remaining.Length:N0} 件。実行ログで確認してください。");
                foreach (var entry in remaining.Take(ListLimit))
                    lines.Add($"結果不明: #{entry.Index}（解析時） {DisplayText.Escape(entry.Name)}");
                if (remaining.Length > ListLimit) lines.Add($"結果不明: ほか {remaining.Length - ListLimit:N0} 件");
            }
        }
        else if (interrupted && reportedName is not null)
        {
            // The CLI named the entry it may have deleted: that is used instead of the analysis-order inference.
            unknown = approvedByName[reportedName];
            unprocessed = approved.Count(c => c.Index > unknown.Index);
            lines.Add("不明（削除された可能性あり）の1件は、CLIが報告した上記の対象です。");
            lines.Add($"未処理（触れていない）: {unprocessed:N0} 件");
            lines.Add("未処理の件数は解析時のアーカイブの順による推定です。受信内容に表れない形で解析後にアーカイブが置き換えられた場合は、実行ログで確認してください。");
        }
        else if (interrupted)
        {
            // Next approved candidate after the last received entry, by ZIP index. Index gaps are not +1 arithmetic.
            int last = received.Count == 0 ? 0 : received[^1].Index;
            unknown = approved.Where(c => c.Index > last).OrderBy(c => c.Index).FirstOrDefault();
            if (unknown is not null)
            {
                unprocessed = approved.Count(c => c.Index > unknown.Index);
                lines.Add($"不明（削除された可能性あり）: #{unknown.Index} {DisplayText.Escape(unknown.Name)}");
                lines.Add($"未処理（触れていない）: {unprocessed:N0} 件");
                lines.Add("不明の1件は解析時のアーカイブの順による推定です。受信内容に表れない形で解析後にアーカイブが置き換えられた場合は、実行ログで確認してください。");
            }
            else lines.Add("最後に受信したentryの次に承認した候補はありません。");
        }
        return new(interrupted ? DeleteReportKind.Interrupted : DeleteReportKind.WithResult,
            interrupted ? "エラー終了（結果が不完全）" : "エラー終了", string.Join("\n", lines), deleted.Length, null, unknown,
            unprocessed, unknownRemaining, possibly, logConfirmed);
    }

    private static void AddResult(List<string> lines, CliResult? result, CliJobResult job)
    {
        if (result is null) return;
        lines.Add($"result: outcome={DisplayText.Escape(result.Outcome)} exit_code={result.ExitCode}");
        if (job.ExitCode != result.ExitCode)
            lines.Add($"終了コード({job.ExitCode})とresult.exit_code({result.ExitCode})が一致しません。終了コードを優先し、resultは詳細として表示します。");
        if (result.Error is not { } error) return;
        var detail = new List<string> { $"stage={error.Stage}" };
        if (error.Step is not null) detail.Add($"step={error.Step}");
        detail.Add($"code={error.Code}");
        if (error.EntryIndex is not null) detail.Add($"entry_index={error.EntryIndex}");
        if (error.Line is not null) detail.Add($"line={error.Line}");
        if (error.Win32Error is not null) detail.Add($"win32_error={error.Win32Error}");
        if (error.PossiblyDeleted is not null) detail.Add($"possibly_deleted={Lower(error.PossiblyDeleted.Value)}");
        if (error.DeletionStarted is not null) detail.Add($"deletion_started={Lower(error.DeletionStarted.Value)}");
        lines.Add("停止理由: " + DisplayText.Escape(string.Join(" ", detail)));
        if (error.Message is not null) lines.Add(DisplayText.Escape(error.Message));
    }

    private static string Lower(bool value) => value ? "true" : "false";
}
