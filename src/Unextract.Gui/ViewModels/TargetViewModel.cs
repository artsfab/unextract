using Unextract.Gui.Models;

namespace Unextract.Gui.ViewModels;

internal enum AnalysisPhase { Idle, Queued, Running, DeleteQueued, Deleting }

// Display classes of the state badge. Display only: nothing decides what may run or be deleted from it.
internal enum StateKind { Neutral, Busy, Ready, Empty, Error, Warning, Done }

internal sealed class TargetViewModel(string template, string path, Func<bool> settingsEditable) : ObservableModel
{
    private string _template = template;
    private string _path = path;
    private bool _selected = true;
    private TargetObservation _observation = new(TargetPresence.Unknown, "未確認");
    private string _resultCategory = "";
    private string _resultPathFilter = "";
    private EntryRowList? _visibleResults;

    public const string OutcomeFailed = "解析失敗";
    public const string OutcomeCancelled = "キャンセルされました（未解析）";
    public const string OutcomeMissing = "Targetが存在しないため解析しませんでした";

    // The archive this Target was registered to (set once by ArchiveViewModel.Add).
    public ArchiveViewModel? Archive { get; internal set; }
    public string Template => _template;
    public string Path => _path;
    public string DuplicateKey => TargetTemplate.DuplicateKey(Path);
    public string DisplayPath => DisplayText.Escape(Path);
    public string DisplayTemplate => DisplayText.Escape(Template);
    // Accessible name of the work list row.
    public string ItemName => DisplayPath;
    public bool IsSettingsEditable => settingsEditable();
    public bool CanEdit => IsSettingsEditable && !HasSuccessfulAnalysis && !HasDeleteStarted;
    public bool CanRemove => IsSettingsEditable;
    public bool CanAnalyze => IsSettingsEditable && Observation.Presence != TargetPresence.Missing;
    public bool CanRefresh => IsSettingsEditable;
    // The normal-analysis snapshot is the only source of candidates. It is replaced only by a later success.
    public AnalysisSnapshot? Snapshot { get; private set; }
    public bool HasSuccessfulAnalysis => Snapshot is not null;
    // History: once the delete process has started this target stays non-editable; the snapshot it used is consumed.
    public bool HasDeleteStarted { get; private set; }
    public bool SnapshotConsumed { get; private set; }
    public AnalysisPhase Phase { get; private set; }
    // Last non-adopted job outcome (failure/cancel/not run). Display only; it never decides what may be deleted.
    public string? Outcome { get; private set; }
    public string? FailureDetail { get; private set; }
    public string AnalyzeButtonText => HasSuccessfulAnalysis || HasDeleteStarted ? "再解析" : "解析";
    public bool IsSelected
    {
        get => _selected;
        set { if (IsSettingsEditable) Set(ref _selected, value); }
    }
    public TargetObservation Observation => _observation;
    public string PresenceText => Observation.Presence switch
    {
        TargetPresence.Present => "Targetの存在を確認しました（安全性はCLIが判定します）。",
        TargetPresence.Missing => "Targetが存在しません。作成後に存在を再確認してください。",
        _ => "Targetの存在は確認不能です。正式な判定はCLIが行います。 " + DisplayText.Escape(Observation.Detail ?? ""),
    };
    // The latest delete whose CLI process started (or may have), one only: kept apart from the analysis state, so
    // re-analysis and mode changes keep it; replaced only by this target's next started delete. Display only; it
    // never decides what may be deleted.
    public string? DeleteOutcome { get; private set; }
    public string? DeleteDetail { get; private set; }
    public string? LogPath { get; private set; }
    // False while the CLI is not known to have created the log (no run record was received).
    public bool LogConfirmed { get; private set; }
    // This target in the latest delete batch it joined, when its CLI did not start (unrun, preparation failure,
    // certain start failure). Separate from the delete result above, which it never replaces. Cleared when the
    // target joins the next batch.
    public string? BatchOutcome { get; private set; }
    public string? BatchDetail { get; private set; }
    public string StateText => Phase switch
    {
        AnalysisPhase.Queued => "解析待機中",
        AnalysisPhase.Running => "解析中",
        AnalysisPhase.DeleteQueued => "削除待機中",
        AnalysisPhase.Deleting => "削除中",
        _ when Snapshot is not null && SnapshotConsumed => $"削除実行済み: {DeleteOutcome}（再解析するまで削除できません）",
        _ when Snapshot is { } snapshot => $"解析済み ({snapshot.Mode}): {snapshot.SummaryText()}" + LastBatch + LastDelete,
        _ => (Outcome ?? "未解析") + LastBatch + LastDelete,
    };
    private string LastBatch => BatchOutcome is null ? "" : " / 直近の一括削除: " + BatchOutcome;
    private string LastDelete => DeleteOutcome is null ? "" : " / 直前の削除: " + DeleteOutcome;
    public string AnalysisDetailText
    {
        get
        {
            // The latest delete (what may have been deleted, the log) comes first; the analysis lines follow.
            var lines = new List<string>();
            if (BatchOutcome is not null) lines.Add("直近の一括削除: " + BatchOutcome);
            if (BatchDetail is not null) lines.Add(BatchDetail);
            if (DeleteOutcome is not null) lines.Add("直前の削除: " + DeleteOutcome);
            if (DeleteDetail is not null) lines.Add(DeleteDetail);
            if (LogPath is not null)
                lines.Add((LogConfirmed ? "実行ログ: " : "実行ログ（予定した保存先。CLIが作成したことは確認できていません）: ") +
                    DisplayText.Escape(LogPath));
            if (Snapshot is not { } snapshot)
            {
                if (FailureDetail is not null) lines.Add("解析結果は採用していません。\n" + FailureDetail);
            }
            else
            {
                lines.Add((SnapshotConsumed ? "解析時の削除候補（削除実行済みのため、再解析するまで削除には使いません）: " : "削除候補: ") +
                    $"{snapshot.CandidateCount:N0} ファイル / 削除候補サイズ（論理サイズの合計）: {SizeFormat.Bytes(snapshot.CandidateLength)}");
                if (!StringComparer.OrdinalIgnoreCase.Equals(snapshot.RunTarget, Path))
                    lines.Add("CLIが報告したtarget: " + DisplayText.Escape(snapshot.RunTarget));
            }
            return string.Join("\n", lines);
        }
    }

    // Short badge for the work list. Display only, derived from the same state as StateText.
    public string StateLabel => Phase switch
    {
        AnalysisPhase.Queued => "解析待機中",
        AnalysisPhase.Running => "解析中",
        AnalysisPhase.DeleteQueued => "削除待機中",
        AnalysisPhase.Deleting => "削除中",
        _ when Snapshot is not null && SnapshotConsumed => "削除実行済み",
        _ when Snapshot is not null => "解析済み",
        _ when Outcome == OutcomeFailed => "解析失敗",
        _ when Outcome == OutcomeCancelled => "キャンセル",
        _ => "未解析",
    };
    public StateKind StateKind => Phase switch
    {
        AnalysisPhase.Idle when Snapshot is not null && SnapshotConsumed =>
            DeleteOutcome == DeleteReport.CompletedOutcome ? StateKind.Done : StateKind.Warning,
        AnalysisPhase.Idle when Snapshot is { } snapshot => snapshot.CandidateCount != 0 ? StateKind.Ready : StateKind.Empty,
        AnalysisPhase.Idle when Outcome == OutcomeFailed => StateKind.Error,
        AnalysisPhase.Idle when DeleteOutcome is not null && DeleteOutcome != DeleteReport.CompletedOutcome => StateKind.Warning,
        AnalysisPhase.Idle when Observation.Presence == TargetPresence.Missing => StateKind.Warning,
        AnalysisPhase.Idle => StateKind.Neutral,
        _ => StateKind.Busy,
    };
    // One line under the path in the work list: candidates, the latest delete and batch state, missing Target.
    public string RowSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (Observation.Presence == TargetPresence.Missing) parts.Add("Targetが存在しません");
            if (Snapshot is { } snapshot && !SnapshotConsumed)
                parts.Add($"削除候補 {snapshot.CandidateCount:N0} ファイル / {SizeFormat.Short(snapshot.CandidateLength)}");
            else if (Snapshot is null && Outcome == OutcomeFailed) parts.Add("解析失敗（詳細を確認してください）");
            if (DeleteOutcome is not null) parts.Add("直前の削除: " + DeleteOutcome);
            if (BatchOutcome is not null) parts.Add("直近の一括削除: " + BatchOutcome);
            return string.Join(" / ", parts);
        }
    }
    // Why editing, analysis or deletion is not available now. Display only.
    public string ActionHint
    {
        get
        {
            if (!IsSettingsEditable) return "処理の実行中は、Targetの設定と選択を変更できません（閲覧はできます）。";
            var hints = new List<string>();
            if (SnapshotConsumed)
                hints.Add("削除を実行したため、再解析が正常に完了するまで削除できません。");
            else if (HasDeleteStarted && Snapshot is null)
                hints.Add("削除を実行したことがあるため、削除するには再解析が必要です。");
            if (HasDeleteStarted) hints.Add("削除を実行したTargetは編集できません。別のTargetにするには、一覧から除去して追加し直してください。");
            else if (Snapshot is not null) hints.Add("解析済みのTargetは編集できません。別のTargetにするには、一覧から除去して追加し直してください。");
            if (Observation.Presence == TargetPresence.Missing)
                hints.Add("Targetが存在しないため解析できません。作成した後で「存在を再確認」を押してください。");
            return string.Join("\n", hints);
        }
    }

    // Filtering the result list never changes selection or the snapshot.
    public IReadOnlyList<CategoryOption> ResultCategories => Snapshot?.Categories() ?? [];
    public string ResultCategory
    {
        get => _resultCategory;
        set { if (Set(ref _resultCategory, value ?? "")) InvalidateResults(); }
    }
    public string ResultPathFilter
    {
        get => _resultPathFilter;
        set { if (Set(ref _resultPathFilter, value ?? "")) InvalidateResults(); }
    }
    public EntryRowList VisibleResults => _visibleResults ??= BuildResults();
    public string ResultCountText => Snapshot is null ? "" : $"表示 {VisibleResults.Count:N0} / {Snapshot.Entries.Count:N0}";

    internal void ApplyObservation(TargetObservation observation)
    {
        _observation = observation;
        Notify(nameof(Observation));
        Notify(nameof(PresenceText));
        Notify(nameof(CanAnalyze));
        NotifyDisplay();
    }

    internal void Edit(string template, string path)
    {
        // MainViewModel checked CanEdit before acquiring the session lock.
        if (HasSuccessfulAnalysis || HasDeleteStarted) return;
        _template = template;
        _path = path;
        Notify(nameof(Template));
        Notify(nameof(Path));
        Notify(nameof(DisplayTemplate));
        Notify(nameof(DisplayPath));
        Notify(nameof(ItemName));
        ApplyObservation(new(TargetPresence.Unknown, "未確認"));
    }

    internal void NotifySettingsLock()
    {
        Notify(nameof(IsSettingsEditable));
        Notify(nameof(CanEdit));
        Notify(nameof(CanRemove));
        Notify(nameof(CanAnalyze));
        Notify(nameof(CanRefresh));
        Notify(nameof(ActionHint));
    }

    internal void MarkQueued()
    {
        Phase = AnalysisPhase.Queued;
        NotifyAnalysis();
    }

    internal void ReleaseQueued()
    {
        if (Phase != AnalysisPhase.Queued) return;
        Phase = AnalysisPhase.Idle;
        NotifyAnalysis();
    }

    // Analysis start retires the previous snapshot; a later failure or cancel never brings it back.
    // The latest delete result is not analysis state and stays.
    internal void BeginAnalysis()
    {
        Snapshot = null;
        SnapshotConsumed = false;
        Outcome = null;
        FailureDetail = null;
        Phase = AnalysisPhase.Running;
        ResetResultView();
        NotifyAnalysis();
    }

    internal void CompleteAnalysis(AnalysisSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotConsumed = false;
        Outcome = null;
        FailureDetail = null;
        Phase = AnalysisPhase.Idle;
        ResetResultView();
        NotifyAnalysis();
    }

    internal void FailAnalysis(string outcome, string? detail)
    {
        Snapshot = null;
        SnapshotConsumed = false;
        Outcome = outcome;
        FailureDetail = detail;
        Phase = AnalysisPhase.Idle;
        ResetResultView();
        NotifyAnalysis();
    }

    // Not started (the target is still missing): nothing was retired or adopted.
    internal void SkipAnalysis(string outcome, string? detail)
    {
        if (Snapshot is null)
        {
            Outcome = outcome;
            FailureDetail = detail;
        }
        Phase = AnalysisPhase.Idle;
        NotifyAnalysis();
    }

    // Mode change: every analysis result and last analysis outcome is discarded. The edit lock from delete history
    // and the latest delete result stay.
    internal void DiscardAnalysis()
    {
        Snapshot = null;
        SnapshotConsumed = false;
        Outcome = null;
        FailureDetail = null;
        Phase = AnalysisPhase.Idle;
        ResetResultView();
        NotifyAnalysis();
    }

    // Joining a new batch replaces the batch state of the previous one; the latest delete result stays.
    internal void MarkDeleteQueued()
    {
        Phase = AnalysisPhase.DeleteQueued;
        BatchOutcome = null;
        BatchDetail = null;
        NotifyAnalysis();
    }

    internal void BeginDelete()
    {
        Phase = AnalysisPhase.Deleting;
        NotifyAnalysis();
    }

    // The CLI process started (or may have): the analysis it was based on is consumed and the target stays
    // non-editable from now on. Only a later successful analysis makes it deletable again.
    internal void MarkDeleteStarted()
    {
        HasDeleteStarted = true;
        SnapshotConsumed = true;
        NotifyAnalysis();
    }

    // End of a delete step whose CLI started (or may have): replaces the previous delete result.
    internal void FinishDelete(string outcome, string? detail, string? logPath, bool logConfirmed = false)
    {
        Phase = AnalysisPhase.Idle;
        BatchOutcome = null;
        BatchDetail = null;
        DeleteOutcome = outcome;
        DeleteDetail = detail;
        LogPath = logPath;
        LogConfirmed = logConfirmed;
        NotifyAnalysis();
    }

    // End of a delete step whose CLI did not start: batch state only. The snapshot stays, so it can be deleted
    // again, and the previous delete result and its log path are kept.
    internal void FinishUnstarted(string outcome, string? detail)
    {
        Phase = AnalysisPhase.Idle;
        SetBatch(outcome, detail);
    }

    // Cleanup notices (e.g. the temporary entries file could not be removed) never change the CLI result.
    // They belong to this step's state: the batch state when the CLI did not start, else the delete result.
    internal void AddDeleteNotice(string notice)
    {
        if (BatchOutcome is not null) BatchDetail = BatchDetail is null ? notice : BatchDetail + "\n" + notice;
        else DeleteDetail = DeleteDetail is null ? notice : DeleteDetail + "\n" + notice;
        NotifyAnalysis();
    }

    // Planned but not started in this batch: batch state only, the previous delete result stays.
    internal void ReleaseDeleteQueued(string outcome, string detail)
    {
        if (Phase != AnalysisPhase.DeleteQueued) return;
        Phase = AnalysisPhase.Idle;
        SetBatch(outcome, detail);
    }

    private void SetBatch(string outcome, string? detail)
    {
        BatchOutcome = outcome;
        BatchDetail = detail;
        NotifyAnalysis();
    }

    private EntryRowList BuildResults()
    {
        if (Snapshot is not { } snapshot) return new([]);
        if (_resultCategory.Length == 0 && _resultPathFilter.Length == 0) return new(snapshot.Entries);
        var indexes = new List<int>();
        for (int i = 0; i < snapshot.Entries.Count; i++)
        {
            var entry = snapshot.Entries[i];
            if ((_resultCategory.Length == 0 || entry.Status == _resultCategory) &&
                (_resultPathFilter.Length == 0 || entry.Name.Contains(_resultPathFilter, StringComparison.OrdinalIgnoreCase)))
                indexes.Add(i);
        }
        return new(snapshot.Entries, indexes.ToArray());
    }

    private void InvalidateResults()
    {
        _visibleResults = null;
        Notify(nameof(VisibleResults));
        Notify(nameof(ResultCountText));
    }

    private void ResetResultView()
    {
        _resultCategory = "";
        _resultPathFilter = "";
        _visibleResults = null;
        Notify(nameof(ResultCategory));
        Notify(nameof(ResultPathFilter));
        Notify(nameof(ResultCategories));
        Notify(nameof(VisibleResults));
        Notify(nameof(ResultCountText));
    }

    private void NotifyAnalysis()
    {
        Notify(nameof(StateText));
        Notify(nameof(AnalysisDetailText));
        Notify(nameof(AnalyzeButtonText));
        Notify(nameof(HasSuccessfulAnalysis));
        Notify(nameof(CanEdit));
        NotifyDisplay();
    }

    private void NotifyDisplay()
    {
        Notify(nameof(StateLabel));
        Notify(nameof(StateKind));
        Notify(nameof(RowSummaryText));
        Notify(nameof(ActionHint));
    }
}
