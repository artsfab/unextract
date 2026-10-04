using System.Globalization;
using Unextract.Gui.Models;
using Unextract.Gui.Services;

namespace Unextract.Gui.ViewModels;

// Delete batch: plan -> one confirmation -> sequential jobs. The analysis result limits the entries handed
// to the CLI; it is never a deletion permit (the CLI revalidates every entry).
internal sealed partial class MainViewModel
{
    private readonly IEntriesStore _entries;
    private readonly ILogLocation _logs;
    private bool _deleting;
    private bool _exitRequested;

    public bool IsDeleting => _deleting;
    public bool IsJobRunning => _analyzing || _deleting;
    public string LogDirectory => _logs.Directory;

    // Builds the fixed plan from the selected targets (display filters do not matter). Excluded targets
    // and their reasons are part of the plan so the confirmation can show them.
    public DeletionPlan? PlanDeletion()
    {
        if (IsBusy || !HasSession) return null;
        var items = new List<PlannedTarget>();
        var excluded = new List<ExcludedTarget>();
        var visible = VisibleArchives.ToHashSet();
        foreach (var archive in Archives)
            foreach (var target in archive.Targets.Where(t => t.IsSelected))
            {
                string? reason = Exclusion(target);
                if (reason is not null)
                {
                    excluded.Add(new(archive.Path, target.Path, reason));
                    continue;
                }
                var snapshot = target.Snapshot!;
                var candidates = snapshot.Entries.Where(e => e.Status == snapshot.CandidateStatus && !e.Directory).ToArray();
                items.Add(new(items.Count + 1, target, archive.Path, target.Path, _mode, snapshot, candidates, snapshot.CandidateLength,
                    !visible.Contains(archive)));
            }
        return new(_mode, items, excluded);
    }

    private string? Exclusion(TargetViewModel target)
    {
        if (target.Phase != AnalysisPhase.Idle) return "処理中です。";
        if (target.Snapshot is not { } snapshot)
            return target.HasDeleteStarted ? "削除実行済みで、再解析が完了していません。" : "未解析、または解析に失敗しています。";
        if (target.SnapshotConsumed) return "削除実行済みです。再解析が正常に完了するまで削除できません。";
        if (snapshot.Mode != _mode) return "現在のモードで解析されていません。";
        if (target.Observation.Presence == TargetPresence.Missing) return "Targetが存在しません。";
        if (snapshot.CandidateCount == 0) return "削除候補が0件です。";
        return null;
    }

    public string ConfirmationText(DeletionPlan plan)
    {
        var text = new List<string>
        {
            $"モード: {plan.Mode}",
            $"Target数: {plan.Items.Count:N0}",
            string.Create(CultureInfo.CurrentCulture, $"削除候補ファイル数（解析時点の最大件数）: {plan.TotalCandidates:N0}"),
            "実行時にCLIが再検証するため、解析後に変化したファイルなど、削除されないファイルがあり得ます。",
            "削除候補サイズ（論理サイズの合計）: " + SizeFormat.Bytes(plan.TotalLength),
            "ごみ箱は使わず、完全に削除します。",
            "途中で停止した場合も、それまでに削除したファイルは元に戻りません。",
        };
        if (plan.Mode == CliMode.Fast) text.Add(FastWarning);
        text.Add("");
        text.Add(ConfirmationListText(plan));
        return string.Join("\n", text);
    }

    // Every target that will be deleted is listed, including selected ones the display filter hides, then every
    // excluded target with its reason. Nothing is shortened.
    public string ConfirmationListText(DeletionPlan plan)
    {
        var text = new List<string>();
        if (plan.HiddenCount != 0)
            text.Add($"表示フィルタで非表示の選択済みTarget {plan.HiddenCount:N0} 件を含みます（対象から外していません）。");
        text.Add($"削除対象のTarget（{plan.Items.Count:N0} 件、この順に実行）:");
        foreach (var item in plan.Items)
            text.Add(string.Create(CultureInfo.CurrentCulture,
                $"{item.Number:N0}. {DisplayText.Escape(item.ArchivePath)} → {DisplayText.Escape(item.TargetPath)}: 候補 {item.Candidates.Count:N0} ファイル / {SizeFormat.Bytes(item.CandidateLength)}") +
                (item.Hidden ? "  ［表示フィルタで非表示］" : ""));
        if (plan.Excluded.Count != 0)
        {
            text.Add("");
            text.Add($"削除対象から除外するTarget（{plan.Excluded.Count:N0} 件）:");
            foreach (var item in plan.Excluded)
                text.Add($"- {DisplayText.Escape(item.ArchivePath)} → {DisplayText.Escape(item.TargetPath)}: {item.Reason}");
        }
        return string.Join("\n", text);
    }

    // Requests that the current delete step is the last one: no new target starts and the CLI is never killed.
    public void RequestExit()
    {
        if (!_deleting) return;
        _exitRequested = true;
        Status = "現在のTargetの処理が終わり次第終了します。";
        RefreshGuide();
    }

    // The explicit approval is a parameter so no CLI can start without it.
    public async Task<DeletionBatchResult> DeleteAsync(DeletionPlan plan, bool approved)
    {
        if (!approved) return DeletionBatchResult.Refused(DeletionStop.Declined, plan.Items.Count);
        if (IsBusy) return DeletionBatchResult.Refused(DeletionStop.Busy, plan.Items.Count);
        if (plan.Items.Count == 0) return DeletionBatchResult.Refused(DeletionStop.NothingToDelete, 0);
        if (!IsCliAvailable)
        {
            Status = CliMessage;
            return DeletionBatchResult.Refused(DeletionStop.NotStarted, plan.Items.Count);
        }
        // A plan from before a later analysis, mode change or removal is not a permit for the current state.
        if (plan.Mode != _mode || plan.Items.Any(i => Exclusion(i.Target) is not null || i.Target.Snapshot != i.Snapshot ||
            !Archives.Any(a => a.Path == i.ArchivePath && a.Targets.Contains(i.Target))))
        {
            Status = "削除計画が古くなっています。もう一度、削除を開始してください。";
            return DeletionBatchResult.Refused(DeletionStop.StalePlan, plan.Items.Count);
        }
        DateTime batchStart = DateTime.UtcNow;
        SetBusy(true);
        SetDeleting(true);
        _exitRequested = false;
        using (SuspendViewUpdates())
            foreach (var item in plan.Items) item.Target.MarkDeleteQueued();
        int launched = 0, completed = 0, position = 0;
        var stop = DeletionStop.None;
        try
        {
            foreach (var item in plan.Items)
            {
                if (_exitRequested) { stop = DeletionStop.ExitRequested; break; }
                position++;
                SetProgress($"{position:N0} / {plan.Items.Count:N0} Targets", DisplayText.Escape(item.ArchivePath), item.Target.DisplayPath, "準備中");
                var step = await DeleteOneAsync(item, batchStart);
                if (step.Launched) launched++;
                if (step.Completed) completed++;
                else
                {
                    stop = step.Launched ? DeletionStop.Error : DeletionStop.NotStarted;
                    break;
                }
            }
        }
        finally
        {
            using (SuspendViewUpdates())
                foreach (var item in plan.Items)
                    item.Target.ReleaseDeleteQueued("未実行", "この一括削除は前のTargetの後で停止したため、このTargetの削除は開始していません。");
            SetProgress("", "", "", "");
            SetDeleting(false);
            SetBusy(false);
            RefreshSummary();
        }
        Status = $"削除終了: 起動 {launched:N0} / {plan.Items.Count:N0} Targets、正常終了 {completed:N0}" +
            (stop == DeletionStop.None ? "。" : "。後続のTargetは開始していません。");
        return new(stop, plan.Items.Count, launched, completed);
    }

    private sealed record DeleteStep(bool Launched, bool Completed);

    private async Task<DeleteStep> DeleteOneAsync(PlannedTarget item, DateTime batchStart)
    {
        var target = item.Target;
        string[] names = item.Candidates.Select(e => e.Name).ToArray();
        // Transfer-format limit (docs/spec/cli.md#entries): fail before anything is created or started.
        if (!EntriesFormat.TryMeasure(names, out long bytes))
            return NotStarted("削除対象の名前をUTF-8の一覧に書き出せないため、削除処理を開始できません。", null);
        if (bytes > EntriesFormat.MaxBytes)
            return NotStarted("削除対象一覧がCLIの上限を超えるため、削除処理を開始できません。",
                $"一覧は {bytes:N0} バイトで、上限は {EntriesFormat.MaxBytes:N0} バイトです。再解析では解消しません。解析結果は保持します。");
        var log = _logs.Reserve(batchStart, item.Number);
        if (log.Path is null)
            return NotStarted("実行ログの保存場所を用意できないため、削除処理を開始できません。",
                $"保存場所: {DisplayText.Escape(log.Directory ?? "")}\n{DisplayText.Escape(log.Error ?? "")}\nログは作成していません。");
        var entries = _entries.Create(names);
        // True only once no process exists or its exit is confirmed; otherwise the entries file is left alone.
        bool processGone = false;
        try
        {
            if (entries.Error is not null)
            {
                processGone = true;
                return NotStarted("一時entriesファイルを用意できないため、削除処理を開始できません。", DisplayText.Escape(entries.Error));
            }
            target.BeginDelete();
            var job = new CliJob(CliOperation.Delete, item.Mode, item.ArchivePath, item.TargetPath, entries.Owned!.Path, log.Path);
            Task<CliJobResult> running;
            try { running = _runner.RunAsync(job, CancellationToken.None); }
            catch (InvalidOperationException e)
            {
                // The runner refused synchronously, before creating any process.
                processGone = true;
                return NotStarted("CLIを起動できず、削除処理は開始されていません。", DisplayText.Escape(e.Message));
            }
            CliJobResult result;
            try { result = await WithProgressAsync(running); }
            catch (Exception e)
            {
                // Whether a process was created is unknown: treat it as started and keep the entries file (docs/spec/gui.md#delete-run).
                target.MarkDeleteStarted();
                target.FinishDelete("結果不明",
                    "CLIの実行中に想定外のエラーが発生しました。起動成否と結果を確認できないため、削除されたファイルがある可能性があります。" +
                    "実行ログで確認してください。\n" + DisplayText.Escape(e.Message), log.Path);
                return new(true, false);
            }
            processGone = result.ExitConfirmed || result.StartState is CliStartState.BeforeStartFailure or CliStartState.NotStarted;
            if (result.StartState is CliStartState.BeforeStartFailure or CliStartState.NotStarted)
                return NotStarted("CLIを起動できず、削除処理は開始されていません。再解析なしで再度削除できます。", CliJobText.Describe(result));
            // From here the delete may have begun, whatever the outcome.
            target.MarkDeleteStarted();
            var report = DeleteReport.Create(item.Candidates, item.Snapshot.EntriesTotal, item.Snapshot.RunTarget, result);
            target.FinishDelete(report.Outcome, report.Detail, log.Path, report.LogConfirmed);
            return new(true, report.Succeeded);
        }
        finally
        {
            if (entries.Owned is { } owned)
            {
                string? notice = processGone ? _entries.Delete(owned)
                    : $"CLIの終了を確認できないため、一時entriesファイルを残しました: {owned.Path}";
                if (notice is not null)
                {
                    AppendDiagnostics([new("一時entriesファイル", notice)]);
                    target.AddDeleteNotice(DisplayText.Escape(notice));
                }
            }
        }

        DeleteStep NotStarted(string reason, string? detail)
        {
            target.FinishUnstarted(reason, detail);
            return new(false, false);
        }
    }

    private void SetDeleting(bool deleting)
    {
        _deleting = deleting;
        Notify(nameof(IsDeleting));
        Notify(nameof(IsJobRunning));
        RefreshGuide();
    }
}
