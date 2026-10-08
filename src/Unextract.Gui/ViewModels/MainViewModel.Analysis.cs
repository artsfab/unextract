using System.Globalization;
using Unextract.Gui.Models;
using Unextract.Gui.Services;

namespace Unextract.Gui.ViewModels;

internal sealed record AnalysisBatchResult(int Requested, int Succeeded, int Failed, int NotRun, bool Cancelled, int Unstarted)
{
    public static readonly AnalysisBatchResult None = new(0, 0, 0, 0, false, 0);
}

// One sequential analysis queue per session. While it runs the whole configuration is locked (SetBusy),
// while browsing, filtering, expanding and cancelling stay available.
internal sealed partial class MainViewModel
{
    public const string FastWarning =
        "Fastは内容の一致を確認しません。同じパス・同じサイズの変更されたファイルも削除候補になり、" +
        "アーカイブから正常に展開できることも確認しません。";

    private CliMode _mode = CliMode.Strict;
    private bool _analyzing;
    private CancellationTokenSource? _cancel;
    private string _overallProgress = "";
    private string _currentArchive = "";
    private string _currentTarget = "";
    private string _entryProgress = "";
    private string _summary = "解析済みのTargetはありません。";

    internal TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public CliMode Mode => _mode;
    public bool IsStrict => _mode == CliMode.Strict;
    public bool IsFast => _mode == CliMode.Fast;
    public string ModeWarning => IsFast ? FastWarning : "";
    public bool HasAnalysisResults => Archives.Any(a => a.Targets.Any(t => t.HasSuccessfulAnalysis));
    public bool IsAnalyzing => _analyzing;
    public string OverallProgressText => _overallProgress;
    public string CurrentArchiveText => _currentArchive;
    public string CurrentTargetText => _currentTarget;
    public string EntryProgressText => _entryProgress;
    public string AnalysisSummaryText => _summary;
    public string AnalysisSummaryNote =>
        "削除候補サイズは、CLIが報告した削除候補のlengthの合計（論理サイズ）です。物理的に解放される容量ではなく、" +
        "同じファイルを指す重複Targetは重複して計上されます。";

    // Returns false when the change was refused. With analysis results it needs an explicit approval,
    // and then every analysis result is discarded: results of the other mode are never reused.
    public bool SetMode(CliMode mode, bool discardApproved = false)
    {
        try
        {
            if (!Enum.IsDefined(mode) || IsBusy) return false;
            if (mode == _mode) return true;
            if (HasAnalysisResults && !discardApproved)
            {
                Status = "モードを変更するには、既存の解析結果をすべて破棄する確認が必要です。";
                return false;
            }
            _mode = mode;
            using (SuspendViewUpdates())
                foreach (var archive in Archives)
                    foreach (var target in archive.Targets) target.DiscardAnalysis();
            RefreshSummary();
            Status = $"モードを {mode} に変更しました。解析結果は破棄しました。";
            return true;
        }
        finally
        {
            // Radio buttons change themselves; re-publish so the view follows the model even after a refusal.
            Notify(nameof(Mode));
            Notify(nameof(IsStrict));
            Notify(nameof(IsFast));
            Notify(nameof(ModeWarning));
        }
    }

    public Task<AnalysisBatchResult> AnalyzeTargetAsync(ArchiveViewModel archive, TargetViewModel target) =>
        RunQueueAsync(Archives.Contains(archive) && archive.Targets.Contains(target) ? [(archive, target)] : []);

    // Unanalyzed = no adopted snapshot. Selection and display filters are not conditions here.
    public Task<AnalysisBatchResult> AnalyzeArchiveAsync(ArchiveViewModel archive) =>
        RunQueueAsync(!Archives.Contains(archive) ? [] :
            archive.Targets.Where(t => !t.HasSuccessfulAnalysis).Select(t => (archive, t)).ToArray());

    public Task<AnalysisBatchResult> AnalyzeSelectedAsync() =>
        RunQueueAsync(Archives.SelectMany(a => a.Targets.Where(t => t.IsSelected && !t.HasSuccessfulAnalysis)
            .Select(t => (a, t))).ToArray());

    public void CancelAnalysis()
    {
        if (_cancel is not { } cancel) return;
        Status = "解析をキャンセルしています。";
        cancel.Cancel();
    }

    // Reads the runner's immutable snapshot. No UI callback sits in the receive path.
    public void RefreshProgress()
    {
        if (!IsJobRunning) return;
        var progress = _runner.Progress;
        SetEntryProgress(progress.Run is null
            ? "準備中（CLI起動・アーカイブ/Targetの検証中）"
            : $"{(_deleting ? "処理中" : "確認中")} {progress.ReceivedEntries:N0} / {progress.Run.Selected:N0} エントリ");
    }

    private async Task<AnalysisBatchResult> RunQueueAsync(IReadOnlyList<(ArchiveViewModel Archive, TargetViewModel Target)> queue)
    {
        if (IsBusy) return AnalysisBatchResult.None;
        if (queue.Count == 0)
        {
            Status = "解析する未解析のTargetがありません。";
            return AnalysisBatchResult.None;
        }
        if (!IsCliAvailable)
        {
            Status = CliMessage;
            return AnalysisBatchResult.None;
        }
        var mode = _mode;
        using var cancellation = new CancellationTokenSource();
        _cancel = cancellation;
        SetBusy(true);
        SetAnalyzing(true);
        using (SuspendViewUpdates())
            foreach (var item in queue) item.Target.MarkQueued();
        int succeeded = 0, failed = 0, notRun = 0, position = 0, unstarted = 0;
        bool cancelled = false;
        try
        {
            foreach (var (archive, target) in queue)
            {
                if (cancellation.IsCancellationRequested) { cancelled = true; break; }
                position++;
                SetProgress($"{position:N0} / {queue.Count:N0} Targets", DisplayText.Escape(archive.Path), target.DisplayPath, "準備中");
                if (target.Observation.Presence == TargetPresence.Missing)
                {
                    // A target created since registration is picked up here, inside the same session lock.
                    target.ApplyObservation(await _search.ObserveTargetAsync(target.Path));
                    if (target.Observation.Presence == TargetPresence.Missing)
                    {
                        target.SkipAnalysis(TargetViewModel.OutcomeMissing, null);
                        notRun++;
                        continue;
                    }
                }
                target.BeginAnalysis();
                CliJobResult result;
                try
                {
                    result = await WithProgressAsync(_runner.RunAsync(new CliJob(CliOperation.Analyze, mode, archive.Path, target.Path), cancellation.Token));
                }
                catch (Exception e)
                {
                    // A runner refusal (a previous process is unconfirmed) or an unexpected error: stop the queue.
                    target.FailAnalysis(TargetViewModel.OutcomeFailed, DisplayText.Escape(e.Message));
                    failed++;
                    break;
                }
                if (result.Succeeded)
                {
                    target.CompleteAnalysis(AnalysisSnapshot.Create(mode, result.Output));
                    succeeded++;
                }
                else if (result.Cancelled)
                {
                    target.FailAnalysis(TargetViewModel.OutcomeCancelled, null);
                    cancelled = true;
                    break;
                }
                else
                {
                    target.FailAnalysis(TargetViewModel.OutcomeFailed, CliJobText.Describe(result));
                    failed++;
                }
                RefreshSummary();
            }
            if (cancellation.IsCancellationRequested) cancelled = true;
        }
        finally
        {
            using (SuspendViewUpdates())
                foreach (var item in queue)
                {
                    if (item.Target.Phase != AnalysisPhase.Queued) continue;
                    item.Target.ReleaseQueued();
                    unstarted++;
                }
            _cancel = null;
            SetProgress("", "", "", "");
            SetAnalyzing(false);
            SetBusy(false);
            RefreshSummary();
        }
        Status = $"解析終了: 成功 {succeeded:N0}、失敗 {failed:N0}、Target不存在で未実行 {notRun:N0}" +
            (cancelled ? $"、キャンセル（未開始 {unstarted:N0}）。" : "。");
        return new(queue.Count, succeeded, failed, notRun, cancelled, unstarted);
    }

    private async Task<CliJobResult> WithProgressAsync(Task<CliJobResult> task)
    {
        while (await Task.WhenAny(task, Task.Delay(ProgressInterval)) != task) RefreshProgress();
        RefreshProgress();
        return await task;
    }

    private void SetAnalyzing(bool analyzing)
    {
        _analyzing = analyzing;
        Notify(nameof(IsAnalyzing));
        Notify(nameof(IsJobRunning));
        RefreshGuide();
    }

    private void SetProgress(string overall, string archive, string target, string entry)
    {
        _overallProgress = overall;
        _currentArchive = archive;
        _currentTarget = target;
        Notify(nameof(OverallProgressText));
        Notify(nameof(CurrentArchiveText));
        Notify(nameof(CurrentTargetText));
        SetEntryProgress(entry);
    }

    private void SetEntryProgress(string text)
    {
        if (_entryProgress == text) return;
        _entryProgress = text;
        Notify(nameof(EntryProgressText));
    }

    private void RefreshSummary()
    {
        long targets = 0, consumed = 0, files = 0;
        UInt128 length = 0;
        foreach (var archive in Archives)
            foreach (var target in archive.Targets)
                if (target.Snapshot is { } snapshot)
                {
                    // A snapshot consumed by a delete is not usable for deletion until re-analysis: not a candidate.
                    if (target.SnapshotConsumed) { consumed++; continue; }
                    targets++;
                    files += snapshot.CandidateCount;
                    length += snapshot.CandidateLength;
                }
        _summary = targets + consumed == 0 ? "解析済みのTargetはありません。" :
            string.Create(CultureInfo.CurrentCulture,
                $"解析済み {targets:N0} Targets / 削除候補 {files:N0} ファイル / 削除候補サイズ（論理サイズの合計） {SizeFormat.Bytes(length)}") +
            (consumed == 0 ? "" : string.Create(CultureInfo.CurrentCulture, $"（ほかに削除実行済みで再解析待ちの {consumed:N0} Targets は含みません）"));
        Notify(nameof(AnalysisSummaryText));
        Notify(nameof(HasAnalysisResults));
        RefreshGuide();
    }
}
