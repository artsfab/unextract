using System.Text;
using Unextract.Gui.Models;
using Unextract.Gui.Services;
using static Unextract.Gui.Tests.JsonlReceiverTests;

namespace Unextract.Gui.Tests;

public sealed class DeleteReportTests
{
    private static readonly CliJob Delete = Job(CliOperation.Delete);
    // Approved candidates, ZIP order. The index gaps are deliberate (non-candidates are in between).
    private static readonly CliEntry[] Approved = [Candidate(2), Candidate(5), Candidate(7), Candidate(9)];
    private const int AnalysisTotal = 10;

    [Fact]
    public void NormalEndReportsExactDeletedCountAndLogicalSizeFromDeletedEntries()
    {
        string text = Run(Delete, 10, 4) + Del("DELETED", 2, 100) + Del("DELETED", 5, long.MaxValue) +
            Del("MODIFIED", 7, 3) + Del("MISSING", 9, 4) + Line(Result("completed", 0, deleted: 2, modified: 1, missing: 1, notSelected: 6));
        var report = Create(Approved, Finish(text, 0));
        Assert.Equal(DeleteReportKind.Completed, report.Kind);
        Assert.True(report.Succeeded);
        Assert.True(report.LogConfirmed);
        Assert.Equal("削除完了", report.Outcome);
        Assert.Equal(2, report.DeletedCount);
        Assert.Equal((UInt128)100 + long.MaxValue, report.DeletedLength);
        Assert.Contains("削除成功: 2 件", report.Detail, StringComparison.Ordinal);
        Assert.Contains("削除したファイルの論理サイズ合計", report.Detail, StringComparison.Ordinal);
        Assert.Contains("MODIFIED 1", report.Detail, StringComparison.Ordinal);
        Assert.Contains("MISSING 1", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("不明", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("解析時と異なります", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteFailedOnlyEndWithExitOneIsAnErrorEndWithItsCountsAndFailures()
    {
        string failed = Line(new { v = 1, type = "entry", index = 5, name = "日本語/失敗.txt", directory = false, length = 8, status = "DELETE_FAILED",
            reason = new { step = "dispose", code = "FUTURE_CODE", message = "x" } });
        string text = Run(Delete, 10, 4) + Del("DELETED", 2, 1) + failed + Del("DELETED", 7, 1) + Del("DELETED", 9, 1) +
            Line(Result("completed", 1, deleted: 3, deleteFailed: 1, notSelected: 6));
        var report = Create(Renamed(5, "日本語/失敗.txt"), Finish(text, 1));
        Assert.Equal(DeleteReportKind.WithResult, report.Kind);
        Assert.False(report.Succeeded);
        Assert.Equal("エラー終了", report.Outcome);
        Assert.Null(report.DeletedLength);
        Assert.Contains("削除成功 3", report.Detail, StringComparison.Ordinal);
        Assert.Contains("DELETE_FAILED 1", report.Detail, StringComparison.Ordinal);
        Assert.Contains("DELETE_FAILED: #5 日本語/失敗.txt [FUTURE_CODE]", report.Detail, StringComparison.Ordinal);
        Assert.Null(report.Unknown);
        Assert.DoesNotContain("総削除サイズ", report.Detail.Replace("総削除サイズではありません", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void StopShowsReasonCountsAndPossiblyDeletedEntriesWithoutGuessingMore()
    {
        string stopped = Line(new { v = 1, type = "entry", index = 5, name = "stop\u202E.txt", directory = false, length = 8, status = "STOPPED",
            reason = new { step = "confirm", code = "DELETE_NOT_CONFIRMED", message = "確認できません" }, possibly_deleted = true });
        string result = Line(new
        {
            v = 1, type = "result", outcome = "stopped", exit_code = 1,
            counts = Counts(deleted: 1, unprocessed: 2, notSelected: 6),
            error = new { stage = "entry", step = "confirm", code = "DELETE_NOT_CONFIRMED", entry_index = 5, entry_name = "stop\u202E.txt",
                possibly_deleted = true, message = "停止\u2066しました" },
        });
        var report = Create(Renamed(5, "stop\u202E.txt"), Finish(Run(Delete, 10, 4) + Del("DELETED", 2, 9) + stopped + result, 1));
        Assert.Equal(DeleteReportKind.WithResult, report.Kind);
        Assert.Contains("停止理由: stage=entry step=confirm code=DELETE_NOT_CONFIRMED", report.Detail, StringComparison.Ordinal);
        Assert.Contains("possibly_deleted=true", report.Detail, StringComparison.Ordinal);
        Assert.Contains("停止\\u{2066}しました", report.Detail, StringComparison.Ordinal);
        Assert.Contains("未処理 2", report.Detail, StringComparison.Ordinal);
        Assert.Single(report.PossiblyDeleted);
        // The STOPPED entry and result.error name the same entry: it is listed once.
        Assert.Single(report.Detail.Split('\n'), l => l.StartsWith("削除された可能性あり", StringComparison.Ordinal));
        Assert.Contains("削除された可能性あり: #5 stop\\u{202E}.txt", report.Detail, StringComparison.Ordinal);
        Assert.Contains("受信できた範囲だけです", report.Detail, StringComparison.Ordinal);
        Assert.Null(report.Unknown);
        Assert.DoesNotContain('\u202E', report.Detail);
    }

    [Fact]
    public void PossiblyDeletedInResultErrorIsShownEvenWithoutAnEntryName()
    {
        string result = Line(new { v = 1, type = "result", outcome = "internal_error", exit_code = 1, counts = Counts(deleted: 1, unprocessed: 2, notSelected: 6),
            error = new { stage = "internal", code = "FUTURE_CODE", entry_index = 5, possibly_deleted = true, deletion_started = false } });
        var report = Create(Approved, Finish(Run(Delete, 10, 4) + Del("DELETED", 2, 1) + result, 1));
        Assert.Equal(DeleteReportKind.WithResult, report.Kind);
        Assert.Contains("possibly_deleted=true", report.Detail, StringComparison.Ordinal);
        Assert.Contains("削除された可能性あり: #5（CLIは対象名を報告していません）", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatibleOutputWithoutRunMeansNothingWasDeletedAndShowsTheError()
    {
        string text = Line(new { v = 1, type = "result", outcome = "input_error", exit_code = 1,
            error = new { stage = "prepare", code = "ENTRIES_NO_MATCH", line = 3, message = "一致しません" } });
        var report = Create(Approved, Finish(text, 1));
        Assert.Equal(DeleteReportKind.NoRun, report.Kind);
        Assert.Equal("エラー終了（削除0件）", report.Outcome);
        Assert.Equal(0, report.DeletedCount);
        Assert.Contains("削除は0件", report.Detail, StringComparison.Ordinal);
        Assert.Contains("code=ENTRIES_NO_MATCH", report.Detail, StringComparison.Ordinal);
        Assert.Contains("line=3", report.Detail, StringComparison.Ordinal);
        Assert.Null(report.Unknown);
        // Without a run record the log is not known to exist (e.g. LOG_ALREADY_EXISTS names someone else's file).
        Assert.False(report.LogConfirmed);
        // Compatible output with neither run nor result also deleted nothing.
        report = Create(Approved, Finish("", 2));
        Assert.Equal(DeleteReportKind.NoRun, report.Kind);
        Assert.Contains("resultを受信できませんでした", report.Detail, StringComparison.Ordinal);
        Assert.False(report.LogConfirmed);
    }

    [Theory]
    [InlineData(new int[0], 2, 3)]          // run only: the first approved candidate is unknown
    [InlineData(new[] { 2 }, 5, 2)]         // after #2 the next approved is #5 (not index+1)
    [InlineData(new[] { 2, 5 }, 7, 1)]
    [InlineData(new[] { 2, 5, 7 }, 9, 0)]
    public void MissingResultNamesTheNextApprovedCandidateAsUnknownAndCountsTheRest(int[] received, int unknown, int unprocessed)
    {
        var text = new StringBuilder(Run(Delete, 10, 4));
        foreach (int index in received) text.Append(Del("DELETED", index, 1));
        var report = Create(Approved, Finish(text.ToString(), 1));
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.False(report.Succeeded);
        Assert.Equal(unknown, report.Unknown!.Index);
        Assert.Equal(unprocessed, report.Unprocessed);
        Assert.Equal(0, report.UnknownRemaining);
        Assert.True(report.LogConfirmed);
        Assert.Contains($"不明（削除された可能性あり）: #{unknown} ", report.Detail, StringComparison.Ordinal);
        Assert.Contains("解析時のアーカイブの順による推定です", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCandidateAfterTheLastReceivedEntryInventsNoUnknownTarget()
    {
        string text = Run(Delete, 10, 4) + Del("DELETED", 2, 1) + Del("DELETED", 5, 1) + Del("DELETED", 7, 1) + Del("DELETED", 9, 1);
        var report = Create(Approved, Finish(text, 1));
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.Null(report.Unknown);
        Assert.Equal(0, report.Unprocessed);
        Assert.DoesNotContain("不明（削除された可能性あり）", report.Detail, StringComparison.Ordinal);
        Assert.Contains("次に承認した候補はありません", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalErrorAfterDeletionStartedUsesTheSameRuleButBeforeItDoesNot()
    {
        string started = Line(new { v = 1, type = "result", outcome = "internal_error", exit_code = 1, counts = Counts(deleted: 1, unprocessed: 3, notSelected: 6),
            error = new { stage = "internal", code = "OUTPUT_FAILED", deletion_started = true, message = "出力できません" } });
        var report = Create(Approved, Finish(Run(Delete, 10, 4) + Del("DELETED", 5, 1) + started, 1));
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.Equal(7, report.Unknown!.Index);
        Assert.Equal(1, report.Unprocessed);
        Assert.Contains("code=OUTPUT_FAILED", report.Detail, StringComparison.Ordinal);
        Assert.Contains("deletion_started=true", report.Detail, StringComparison.Ordinal);

        string before = started.Replace("\"deletion_started\":true", "\"deletion_started\":false", StringComparison.Ordinal);
        report = Create(Approved, Finish(Run(Delete, 10, 4) + before, 1));
        Assert.Equal(DeleteReportKind.WithResult, report.Kind);
        Assert.Null(report.Unknown);
    }

    [Fact]
    public void ReorderedZipNamesNoSingleEntryAndReportsEveryUnreceivedCandidateAsUnknown()
    {
        // Analysis: file1 #1, file2 #2, file3 #3. The ZIP was rewritten as file2 #1, file3 #2, file1 #3 before the delete.
        CliEntry[] approved = [Candidate(1), Candidate(2), Candidate(3)];
        string text = Run(Delete, 3, 3) + Del("DELETED", 1, 1, "file2") + Del("DELETED", 2, 1, "file3");
        var report = DeleteReport.Create(approved, 3, @"C:\target", Finish(text, 1));
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        // The analysis-order rule would wrongly name file3 (#3) and hide file1, the entry really in progress.
        Assert.Null(report.Unknown);
        Assert.Equal(0, report.Unprocessed);
        Assert.Equal(1, report.UnknownRemaining);
        Assert.Contains("entryの番号が解析時と異なります", report.Detail, StringComparison.Ordinal);
        Assert.Contains("途絶した位置の次の1件を特定できません", report.Detail, StringComparison.Ordinal);
        Assert.Contains("結果不明: #1（解析時） file1", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("不明（削除された可能性あり）: #", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("未処理（触れていない）", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("file3", report.Detail.Split('\n').Where(l => l.StartsWith("結果不明", StringComparison.Ordinal)).Aggregate("", string.Concat), StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedEntryCountAloneMakesTheRemainingCandidatesUnknown()
    {
        var report = Create(Approved, Finish(Run(Delete, 11, 4) + Del("DELETED", 2, 1), 1));
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.Null(report.Unknown);
        Assert.Equal(3, report.UnknownRemaining);
        Assert.Contains("解析時 10、削除時 11 エントリ", report.Detail, StringComparison.Ordinal);
        Assert.Contains("承認候補 3 件", report.Detail, StringComparison.Ordinal);
        // A change seen in a normally completed delete is only displayed.
        string done = Run(Delete, 11, 4) + Del("DELETED", 2, 1) + Del("DELETED", 5, 1) + Del("DELETED", 7, 1) + Del("DELETED", 9, 1) +
            Line(Result("completed", 0, deleted: 4, notSelected: 7));
        report = Create(Approved, Finish(done, 0));
        Assert.True(report.Succeeded);
        Assert.Contains("解析後にアーカイブが変更された可能性があります", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterAZipChangeTheEntryTheCliReportsIsNamedAndOnlyTheOthersAreUnknown()
    {
        string result = Line(new { v = 1, type = "result", outcome = "internal_error", exit_code = 1, counts = Counts(deleted: 1, unprocessed: 2, notSelected: 7),
            error = new { stage = "internal", code = "OUTPUT_FAILED", entry_index = 6, entry_name = "file5", possibly_deleted = true, deletion_started = true } });
        var report = Create(Approved, Finish(Run(Delete, 11, 4) + Del("DELETED", 2, 1) + result, 1));
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.Contains("削除された可能性あり: file5", report.Detail, StringComparison.Ordinal);
        Assert.Equal(2, report.UnknownRemaining);
        Assert.Contains("結果不明: #7（解析時） file7", report.Detail, StringComparison.Ordinal);
        Assert.Contains("結果不明: #9（解析時） file9", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("結果不明: #5", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAVisibleZipChangeTheEntryTheCliReportsWinsOverTheAnalysisOrderInference()
    {
        // Last received #2, so the inference alone would name #5. The CLI says #7 was the one in progress.
        string result = Line(new { v = 1, type = "result", outcome = "internal_error", exit_code = 1, counts = Counts(deleted: 1, unprocessed: 1, notSelected: 6),
            error = new { stage = "internal", code = "OUTPUT_FAILED", entry_index = 7, entry_name = "file7", possibly_deleted = true, deletion_started = true } });
        var report = Create(Approved, Finish(Run(Delete, 10, 4) + Del("DELETED", 2, 1) + result, 1));
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.Equal(7, report.Unknown!.Index);
        Assert.Equal(1, report.Unprocessed);
        Assert.Contains("削除された可能性あり: file7", report.Detail, StringComparison.Ordinal);
        Assert.Contains("CLIが報告した上記の対象です", report.Detail, StringComparison.Ordinal);
        Assert.Contains("未処理の件数は解析時のアーカイブの順による推定です", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("#5", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("不明（削除された可能性あり）: #", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportedEntryNameThatWasNotApprovedIsAnUnknownResult()
    {
        string result = Line(new { v = 1, type = "result", outcome = "internal_error", exit_code = 1, counts = Counts(deleted: 1, unprocessed: 2, notSelected: 6),
            error = new { stage = "internal", code = "OUTPUT_FAILED", entry_index = 3, entry_name = "not-approved", possibly_deleted = true, deletion_started = true } });
        var report = Create(Approved, Finish(Run(Delete, 10, 4) + Del("DELETED", 2, 1) + result, 1));
        Assert.Equal(DeleteReportKind.Unknown, report.Kind);
        Assert.Contains("承認した削除対象と一致しない", report.Detail, StringComparison.Ordinal);
        Assert.Null(report.Unknown);
    }

    [Fact]
    public void OutputOutsideTheApprovedEntriesIsAnUnknownResultEvenWhenCompleted()
    {
        // A name that was not handed to the CLI, or a selected count that is not the approved count.
        string foreign = Run(Delete, 10, 4) + Del("DELETED", 2, 1) + Del("DELETED", 3, 1, "not-approved") + Del("DELETED", 7, 1) + Del("DELETED", 9, 1) +
            Line(Result("completed", 0, deleted: 4, notSelected: 6));
        string count = Run(Delete, 10, 1) + Del("DELETED", 2, 1) + Line(Result("completed", 0, deleted: 1, notSelected: 9));
        foreach (string text in new[] { foreign, count, Run(Delete, 10, 4) + Del("DELETED", 3, 1, "not-approved") })
        {
            var report = Create(Approved, Finish(text, 0));
            Assert.Equal(DeleteReportKind.Unknown, report.Kind);
            Assert.False(report.Succeeded);
            Assert.Equal("結果不明", report.Outcome);
            Assert.Null(report.DeletedLength);
            Assert.Contains("承認した削除対象と一致しない", report.Detail, StringComparison.Ordinal);
            Assert.Contains("実行ログで確認してください", report.Detail, StringComparison.Ordinal);
            Assert.True(report.LogConfirmed);
        }
    }

    [Fact]
    public void IncompatibleOrBrokenOutputIsUnknownWithNoCountsFromPartialContent()
    {
        foreach (string text in new[] { Line(new { v = 2, type = "run" }), Run(Delete, 10, 4) + Del("DELETED", 2, 1) + "not json\n",
            Run(Delete, 10, 4) + Del("DELETED", 2, 1) + Del("DELETED", 2, 1) })
        {
            var report = Create(Approved, Finish(text, 0));
            Assert.Equal(DeleteReportKind.Unknown, report.Kind);
            Assert.Equal("結果不明", report.Outcome);
            Assert.Equal(0, report.DeletedCount);
            Assert.Null(report.DeletedLength);
            Assert.Null(report.Unknown);
            Assert.Contains("出力が非互換または異常のため", report.Detail, StringComparison.Ordinal);
            Assert.Contains("実行ログで確認してください", report.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("削除成功", report.Detail, StringComparison.Ordinal);
        }
        Assert.False(Create(Approved, Finish(Line(new { v = 2, type = "run" }), 0)).LogConfirmed);
        // Unfinished or unreadable stream: the process end or the output is not confirmed.
        var receiver = new JsonlReceiver(Delete);
        receiver.Feed(Encoding.ASCII.GetBytes(Run(Delete, 10, 4)));
        receiver.ReadFailed("標準出力を読み切れません");
        var broken = new CliJobResult(CliStartState.Started, true, 1, false, true, false, receiver.Finish(), "", false, null);
        Assert.Equal(DeleteReportKind.Unknown, Create(Approved, broken).Kind);
        var unfinished = new CliJobResult(CliStartState.Started, false, null, true, true, false,
            new JsonlReceiver(Delete).Finish(), "", false, "CLIの実終了を確認できません。");
        var report2 = Create(Approved, unfinished);
        Assert.Equal(DeleteReportKind.Unknown, report2.Kind);
        // The stated cause matches what is actually unknown.
        Assert.Contains("CLIの実終了を確認できないため", report2.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("非互換", report2.Detail, StringComparison.Ordinal);
        var unknownStart = new CliJobResult(CliStartState.Unknown, false, null, false, false, false,
            new JsonlReceiver(Delete).Finish(), "", false, "CLIの起動成否を確認できません");
        Assert.Contains("CLIの起動成否を確認できません", Create(Approved, unknownStart).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitCodeWinsOverACompletedResultAndTheResultStaysAsDetail()
    {
        string text = Run(Delete, 10, 1) + Del("DELETED", 2, 5) + Line(Result("completed", 0, deleted: 1, notSelected: 9));
        var report = Create([Candidate(2)], Finish(text, 1));
        Assert.Equal(DeleteReportKind.WithResult, report.Kind);
        Assert.False(report.Succeeded);
        Assert.Contains("終了コード(1)とresult.exit_code(0)が一致しません", report.Detail, StringComparison.Ordinal);
        Assert.Contains("result: outcome=completed exit_code=0", report.Detail, StringComparison.Ordinal);
        Assert.Contains("削除成功 1", report.Detail, StringComparison.Ordinal);
        Assert.Null(report.DeletedLength);
        // Exit 0 without a result is also an error end (the run was seen, so the rule for a missing result applies).
        report = Create(Approved, Finish(Run(Delete, 10, 4) + Del("DELETED", 2, 5), 0));
        Assert.Equal(DeleteReportKind.Interrupted, report.Kind);
        Assert.Equal(5, report.Unknown!.Index);
    }

    [Fact]
    public void RunTargetDifferenceIsOnlyDisplayed()
    {
        string text = Run(Delete, 10, 1) + Del("DELETED", 2, 5) + Line(Result("completed", 0, deleted: 1, notSelected: 9));
        var report = DeleteReport.Create([Candidate(2)], AnalysisTotal, @"D:\解析時の target", Finish(text, 0));
        Assert.True(report.Succeeded);
        Assert.Contains(@"CLIが報告したtarget: C:\target（解析時: D:\解析時の target）", report.Detail, StringComparison.Ordinal);
        Assert.Contains("判定には使っていません", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIが報告したtarget", DeleteReport.Create([Candidate(2)], AnalysisTotal, @"c:\TARGET", Finish(text, 0)).Detail,
            StringComparison.Ordinal);
    }

    private static DeleteReport Create(IReadOnlyList<CliEntry> approved, CliJobResult result) =>
        DeleteReport.Create(approved, AnalysisTotal, @"C:\target", result);

    private static CliEntry Candidate(int index, string? name = null) => new(index, name ?? $"file{index}", false, index, "MATCHED", null, null, null);
    private static CliEntry[] Renamed(int index, string name) => Approved.Select(c => c.Index == index ? Candidate(index, name) : c).ToArray();
    // A delete entry for an approved candidate: the CLI reports the FullName it was given.
    private static string Del(string status, int index, long length, string? name = null) => Entry(status, index, length, name ?? $"file{index}");

    private static object Counts(int deleted = 0, int modified = 0, int missing = 0, int deleteFailed = 0, int notSelected = 0, int unprocessed = 0) => new
    {
        deleted, modified, missing, skipped_special_file = 0, directory = 0, delete_failed = deleteFailed,
        not_selected = notSelected, unprocessed,
    };

    private static object Result(string outcome, int exit, int deleted = 0, int modified = 0, int missing = 0, int deleteFailed = 0, int notSelected = 0) =>
        new { v = 1, type = "result", outcome, exit_code = exit, counts = Counts(deleted, modified, missing, deleteFailed, notSelected) };

    private static CliJobResult Finish(string text, int exit)
    {
        var receiver = new JsonlReceiver(Delete);
        receiver.Feed(Encoding.ASCII.GetBytes(text));
        return new(CliStartState.Started, true, exit, true, true, false, receiver.Finish(), "", false, null);
    }
}
