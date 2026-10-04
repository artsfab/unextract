using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Unextract.Gui.Models;

namespace Unextract.Gui.ViewModels;

internal sealed class ArchiveViewModel : ObservableModel
{
    private readonly ObservableCollection<TargetViewModel> _targets = [];
    private readonly Func<bool> _settingsEditable;
    private bool _changingSelection;

    public ArchiveViewModel(ArchiveFile file, Func<bool> settingsEditable)
    {
        File = file;
        _settingsEditable = settingsEditable;
        Targets = new(_targets);
    }

    // Targets were added or removed (the work list is rebuilt).
    public event EventHandler? TargetsChanged;
    // A Target's selection or state changed (summaries are refreshed). Display only.
    public event EventHandler? TargetStateChanged;

    public ArchiveFile File { get; }
    public string Path => File.Path;
    public string DisplayName => DisplayText.Escape(System.IO.Path.GetFileName(Path));
    public string DisplayPath => DisplayText.Escape(Path);
    // Accessible name of the work list row.
    public string ItemName => DisplayName;
    public string DisplayDirectory => DisplayText.Escape(System.IO.Path.GetDirectoryName(Path) ?? "");
    public string SizeText => $"{File.Length:N0} bytes";
    public string ModifiedText => File.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public ReadOnlyObservableCollection<TargetViewModel> Targets { get; }
    public int TargetCount => _targets.Count;
    public string TargetCountText => $"Targets: {TargetCount}";
    public bool IsSettingsEditable => _settingsEditable();
    public bool IsParentEnabled => TargetCount != 0 && IsSettingsEditable;
    public bool? IsSelected
    {
        get => _targets.Count == 0 || _targets.All(t => !t.IsSelected) ? false :
            _targets.All(t => t.IsSelected) ? true : null;
        set
        {
            if (!IsParentEnabled) return;
            _changingSelection = true;
            try { foreach (var target in _targets) target.IsSelected = value != false; }
            finally { _changingSelection = false; }
            Notify(nameof(IsSelected));
            TargetStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal bool Contains(string key, TargetViewModel? except = null) =>
        _targets.Any(t => t != except && StringComparer.OrdinalIgnoreCase.Equals(t.DuplicateKey, key));
    internal void Add(TargetViewModel target)
    {
        target.Archive = this;
        _targets.Add(target);
        target.PropertyChanged += TargetChanged;
        NotifyTargets();
    }
    internal void Remove(TargetViewModel target)
    {
        if (!_targets.Remove(target)) return;
        target.PropertyChanged -= TargetChanged;
        NotifyTargets();
    }
    private void TargetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TargetViewModel.IsSelected))
        {
            if (_changingSelection) return;
            Notify(nameof(IsSelected));
            TargetStateChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.PropertyName == nameof(TargetViewModel.StateText)) TargetStateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void NotifyTargets()
    {
        Notify(nameof(TargetCount));
        Notify(nameof(TargetCountText));
        Notify(nameof(IsParentEnabled));
        Notify(nameof(IsSelected));
        TargetsChanged?.Invoke(this, EventArgs.Empty);
    }
    internal void NotifySettingsLock()
    {
        Notify(nameof(IsSettingsEditable));
        Notify(nameof(IsParentEnabled));
        foreach (var target in _targets) target.NotifySettingsLock();
    }
}
