using System.Globalization;
using Unextract.Gui.Models;

namespace Unextract.Gui.ViewModels;

// What the window shows, kept apart from what the session does. The viewed item (the details pane) is not the
// selection: viewing, filtering and the work list rows never change IsSelected, the queues or the plans.
// The guide and the selection summary are display only and are never read back to decide anything.
internal sealed partial class MainViewModel
{
    private IReadOnlyList<object> _workItems = [];
    private HashSet<ArchiveViewModel> _visibleSet = [];
    private object? _viewed;
    private int _rebuildSuspended;
    private bool _rebuildPending;
    private int _guideSuspended;
    private bool _guidePending;
    private string _nextStep = "1. 検索するディレクトリを指定して「検索」を押してください。";
    private string _selectionSummary = "";

    // Visible archives, each followed by all of its Targets, in search and registration order.
    public IReadOnlyList<object> WorkItems => _workItems;
    // ArchiveViewModel, TargetViewModel or null.
    public object? Viewed => _viewed;
    // The work list's current row: the viewed item while it is listed. A row leaving the list (filter, rebuild)
    // sets null here, which keeps the viewed item.
    public object? SelectedWorkItem
    {
        get => _viewed is not null && IsListed(_viewed) ? _viewed : null;
        set { if (value is not null) View(value); }
    }
    public string ViewedHiddenText => _viewed is not null && !IsListed(_viewed)
        ? "この項目は現在の絞り込みでは一覧に表示されていません（選択状態は変わりません）。" : "";
    public string NextStepText => _nextStep;
    public string SelectionSummaryText => _selectionSummary;

    // Browsing is always allowed, also while a job runs.
    public void View(object? item)
    {
        if (item is TargetViewModel target && (target.Archive is not { } owner || !Archives.Contains(owner) || !owner.Targets.Contains(target))) return;
        if (item is ArchiveViewModel archive && !Archives.Contains(archive)) return;
        if (item is not (null or ArchiveViewModel or TargetViewModel) || ReferenceEquals(item, _viewed)) return;
        _viewed = item;
        Notify(nameof(Viewed));
        Notify(nameof(SelectedWorkItem));
        Notify(nameof(ViewedHiddenText));
    }

    private bool IsListed(object item) => item switch
    {
        ArchiveViewModel archive => _visibleSet.Contains(archive),
        TargetViewModel { Archive: { } archive } target => _visibleSet.Contains(archive) && archive.Targets.Contains(target),
        _ => false,
    };

    private void RebuildWorkItems()
    {
        if (_rebuildSuspended != 0) { _rebuildPending = true; return; }
        _rebuildPending = false;
        var items = new List<object>(_visibleArchives.Count * 2);
        foreach (var archive in _visibleArchives)
        {
            items.Add(archive);
            items.AddRange(archive.Targets);
        }
        _visibleSet = [.. _visibleArchives];
        _workItems = items.AsReadOnly();
        Notify(nameof(WorkItems));
        Notify(nameof(SelectedWorkItem));
        Notify(nameof(ViewedHiddenText));
    }

    // Bulk operations rebuild the list and refresh the guide once at the end instead of per Target.
    private IDisposable SuspendViewUpdates()
    {
        _rebuildSuspended++;
        _guideSuspended++;
        return new Resume(this);
    }

    private sealed class Resume(MainViewModel model) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--model._rebuildSuspended == 0 && model._rebuildPending) model.RebuildWorkItems();
            if (--model._guideSuspended == 0 && model._guidePending) model.RefreshGuide();
        }
    }

    private void Watch(ArchiveViewModel archive)
    {
        archive.TargetsChanged += (_, _) => { RebuildWorkItems(); RefreshGuide(); };
        archive.TargetStateChanged += (_, _) => RefreshGuide();
    }

    private void RefreshGuide()
    {
        if (_guideSuspended != 0) { _guidePending = true; return; }
        _guidePending = false;
        int total = 0, selected = 0, deletable = 0, unanalyzed = 0;
        long files = 0;
        UInt128 length = 0;
        foreach (var archive in Archives)
            foreach (var target in archive.Targets)
            {
                total++;
                if (!target.IsSelected) continue;
                selected++;
                if (!target.HasSuccessfulAnalysis) unanalyzed++;
                if (Exclusion(target) is null && target.Snapshot is { } snapshot)
                {
                    deletable++;
                    files += snapshot.CandidateCount;
                    length += snapshot.CandidateLength;
                }
            }
        string summary = !HasSession || total == 0 ? "" : string.Create(CultureInfo.CurrentCulture,
            $"選択 {selected:N0} / {total:N0} Targets：削除できる {deletable:N0}（削除候補 {files:N0} ファイル・{SizeFormat.Short(length)}）、未解析 {unanalyzed:N0}、その他 {selected - deletable - unanalyzed:N0}");
        string next =
            !IsCliAvailable ? "同梱CLIが見つからないため、解析と削除はできません。配布物の cli フォルダーを確認してください。" :
            _deleting && _exitRequested ? "現在のTargetの処理が終わり次第終了します。" :
            _deleting ? "削除を実行中です。削除は途中で取り消せません。完了するまで設定と選択は変更できません（閲覧はできます）。" :
            _analyzing ? "解析中です。完了するまで設定と選択は変更できません（閲覧と解析のキャンセルはできます）。" :
            IsBusy ? "処理中です。" :
            !HasSession ? "1. 検索するディレクトリを指定して「検索」を押してください。" :
            Archives.Count == 0 ? "ZIPが見つかりませんでした。別のディレクトリを検索してください。" :
            total == 0 ? "2. Targetを追加してください（「全Archiveに一括追加」、または一覧でArchiveを選んで「このArchiveにTargetを追加」）。" :
            selected == 0 ? "一括操作の対象がありません。一覧のチェックボックスでTargetを選択してください。" :
            unanalyzed != 0 ? $"3. 「選択中の未解析Targetを解析」で解析してください（未解析 {unanalyzed:N0} Targets）。" :
            deletable != 0 ? $"4. 結果を確認し、「選択中のTargetを削除...」で削除の内容を確認してください（削除できる {deletable:N0} Targets）。" :
            "選択中のTargetには削除できるものがありません。理由は各Targetの詳細と「選択中のTargetを削除...」の確認で表示されます。";
        if (summary != _selectionSummary)
        {
            _selectionSummary = summary;
            Notify(nameof(SelectionSummaryText));
        }
        if (next != _nextStep)
        {
            _nextStep = next;
            Notify(nameof(NextStepText));
        }
    }
}
