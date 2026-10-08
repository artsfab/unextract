using System.IO;
using Unextract.Gui.Models;
using Unextract.Gui.Services;

namespace Unextract.Gui.ViewModels;

internal sealed partial class MainViewModel : ObservableModel
{
    private readonly CliAvailability cli;
    private readonly IArchiveSearch _search;
    private readonly ISearchSettings _settings;
    private readonly ICliProcessRunner _runner;
    private string _searchDirectory = "";
    private bool _recursive = true;
    private bool _busy;
    private bool _hasSession;
    private string _filter = "";
    private string _status = "ディレクトリを選択してアーカイブを検索してください。";
    private IReadOnlyList<ArchiveViewModel> _archives = [];
    private IReadOnlyList<ArchiveViewModel> _visibleArchives = [];
    private IReadOnlyList<SearchDiagnostic> _diagnostics = [];

    public MainViewModel(CliAvailability cli, IArchiveSearch? search = null, ISearchSettings? settings = null,
        ICliProcessRunner? runner = null, IEntriesStore? entries = null, ILogLocation? logs = null)
    {
        this.cli = cli;
        _search = search ?? new ArchiveSearch();
        _settings = settings ?? new SearchSettings();
        _runner = runner ?? new CliProcessRunner(new CliLocation(AppContext.BaseDirectory));
        _entries = entries ?? new EntriesStore();
        _logs = logs ?? new LogLocation();
    }

    public string CliPath => cli.Path;
    public string CliMessage => cli.Message;
    public bool IsCliAvailable => cli.IsAvailable;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsBusy => _busy;
    public bool IsSettingsEditable => !IsBusy;
    public bool HasSession => _hasSession;
    public string SearchDirectory
    {
        get => _searchDirectory;
        set { if (IsSettingsEditable) Set(ref _searchDirectory, value); }
    }
    public bool Recursive
    {
        get => _recursive;
        set { if (IsSettingsEditable) Set(ref _recursive, value); }
    }
    public string ArchiveFilter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) RefreshVisibleArchives(); }
    }
    public IReadOnlyList<ArchiveViewModel> Archives => _archives;
    public IReadOnlyList<ArchiveViewModel> VisibleArchives => _visibleArchives;
    public IReadOnlyList<SearchDiagnostic> Diagnostics => _diagnostics;
    public string DiagnosticCountText => $"検索・登録・保存の通知 ({Diagnostics.Count:N0} 件)";
    public string ArchiveCountText => $"表示 {_visibleArchives.Count:N0} / 検索 {_archives.Count:N0} Archives";

    public async Task InitializeAsync()
    {
        string original = SearchDirectory;
        var settings = await _settings.LoadAsync();
        if (SearchDirectory == original && settings.Directory is { } directory) SearchDirectory = directory;
    }

    public async Task<bool> SearchAsync(bool discardApproved = false)
    {
        if (IsBusy) return false;
        if (HasSession && !discardApproved)
        {
            Status = "再検索には現在のArchive・Target・選択・解析結果を破棄する確認が必要です。";
            return false;
        }
        if (string.IsNullOrEmpty(SearchDirectory) || !Path.IsPathFullyQualified(SearchDirectory))
        {
            Status = "検索ディレクトリを絶対パスで指定してください。";
            return false;
        }
        string directory;
        try { directory = Path.GetFullPath(SearchDirectory); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Status = "検索ディレクトリの形式が不正です: " + DisplayText.Escape(e.Message);
            return false;
        }
        bool recursive = Recursive;
        SetBusy(true);
        Status = "アーカイブを検索しています。";
        try
        {
            var saved = await _settings.SaveAsync(directory);
            var found = await _search.SearchAsync(directory, recursive);
            _archives = Array.AsReadOnly(found.Archives.OrderBy(a => a.Path, StringComparer.Ordinal)
                .Select(a => new ArchiveViewModel(a, () => IsSettingsEditable)).ToArray());
            foreach (var archive in _archives) Watch(archive);
            _viewed = null;
            Notify(nameof(Viewed));
            _diagnostics = saved.Error is null ? found.Diagnostics :
                Array.AsReadOnly(found.Diagnostics.Append(new SearchDiagnostic(directory, saved.Error)).ToArray());
            _hasSession = true;
            Notify(nameof(HasSession));
            Notify(nameof(Archives));
            Notify(nameof(Diagnostics));
            Notify(nameof(DiagnosticCountText));
            RefreshVisibleArchives();
            RefreshSummary();
            Status = $"検索完了: {_archives.Count:N0} Archives。通知 {_diagnostics.Count:N0} 件。";
            return true;
        }
        finally { SetBusy(false); }
    }

    public async Task<TargetRegistrationResult> AddTargetsAsync(string template, ArchiveViewModel? archive = null)
    {
        if (IsBusy || (archive is not null && !Archives.Contains(archive))) return new(0, 0, []);
        // Capture all search results, independently of filter/selection/expansion.
        var scope = archive is null ? Archives.ToArray() : [archive];
        int added = 0, duplicates = 0;
        var errors = new List<SearchDiagnostic>();
        SetBusy(true);
        using var updates = SuspendViewUpdates();
        try
        {
            foreach (var item in scope)
            {
                string path;
                try { path = TargetTemplate.Resolve(template, item.Path); }
                catch (ArgumentException e)
                {
                    errors.Add(new(item.Path, e.Message));
                    continue;
                }
                if (item.Contains(TargetTemplate.DuplicateKey(path))) { duplicates++; continue; }
                var observation = await _search.ObserveTargetAsync(path);
                var target = new TargetViewModel(template, path, () => IsSettingsEditable);
                target.ApplyObservation(observation);
                item.Add(target);
                added++;
            }
            AppendDiagnostics(errors);
            Status = $"Target追加: {added:N0} 件、重複スキップ {duplicates:N0} 件、登録エラー {errors.Count:N0} 件。";
            return new(added, duplicates, errors.AsReadOnly());
        }
        finally { SetBusy(false); }
    }

    public async Task<bool> EditTargetAsync(ArchiveViewModel archive, TargetViewModel target, string template)
    {
        if (IsBusy || !Archives.Contains(archive) || !archive.Targets.Contains(target) || !target.CanEdit) return false;
        string path;
        try { path = TargetTemplate.Resolve(template, archive.Path); }
        catch (ArgumentException e) { RegistrationError(archive, e.Message); return false; }
        if (archive.Contains(TargetTemplate.DuplicateKey(path), target))
        {
            RegistrationError(archive, "同じTargetが既に登録されています。");
            return false;
        }
        SetBusy(true);
        try
        {
            var observation = await _search.ObserveTargetAsync(path);
            target.Edit(template, path);
            target.ApplyObservation(observation);
            Status = "Target設定を更新しました。";
            return true;
        }
        finally { SetBusy(false); }
    }

    public bool RemoveTarget(ArchiveViewModel archive, TargetViewModel target)
    {
        if (IsBusy || !Archives.Contains(archive) || !archive.Targets.Contains(target)) return false;
        archive.Remove(target);
        // The details pane falls back to the archive; the selection of the other Targets is untouched.
        if (ReferenceEquals(Viewed, target)) View(archive);
        RefreshSummary();
        Status ="Targetを一覧から除去しました。";
        return true;
    }

    // The analysis queue also calls this immediately before analysis; missing is a re-evaluable observation.
    public async Task<bool> RefreshTargetAsync(TargetViewModel target)
    {
        if (IsBusy || !Archives.Any(a => a.Targets.Contains(target))) return false;
        SetBusy(true);
        try
        {
            target.ApplyObservation(await _search.ObserveTargetAsync(target.Path));
            Status = target.PresenceText;
            return target.Observation.Presence != TargetPresence.Missing;
        }
        finally { SetBusy(false); }
    }

    public void SelectTargets(bool selected, bool visibleOnly)
    {
        if (IsBusy) return;
        using var updates = SuspendViewUpdates();
        foreach (var archive in visibleOnly ? VisibleArchives : Archives) archive.IsSelected = selected;
    }

    private void RefreshVisibleArchives()
    {
        _visibleArchives = Array.AsReadOnly(_archives.Where(a =>
            a.Path.Contains(ArchiveFilter, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(a.Path).Contains(ArchiveFilter, StringComparison.OrdinalIgnoreCase)).ToArray());
        Notify(nameof(VisibleArchives));
        Notify(nameof(ArchiveCountText));
        RebuildWorkItems();
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        Notify(nameof(IsBusy));
        Notify(nameof(IsSettingsEditable));
        foreach (var archive in Archives) archive.NotifySettingsLock();
        RefreshGuide();
    }
    private void RegistrationError(ArchiveViewModel archive, string message)
    {
        AppendDiagnostics([new(archive.Path, message)]);
        Status = "Targetの登録エラー: " + DisplayText.Escape(message);
    }
    private void AppendDiagnostics(IEnumerable<SearchDiagnostic> diagnostics)
    {
        _diagnostics = Array.AsReadOnly(_diagnostics.Concat(diagnostics).ToArray());
        Notify(nameof(Diagnostics));
        Notify(nameof(DiagnosticCountText));
    }
}
