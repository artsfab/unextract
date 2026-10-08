using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Unextract.Gui.Models;
using Unextract.Gui.Services;
using Unextract.Gui.ViewModels;

namespace Unextract.Gui.Views;

// Event handlers only connect the controls to the view model's operations; no state lives here except the
// pending close request.
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
    private MainViewModel Model => (MainViewModel)DataContext;
    private readonly IFolderPicker _folders = new WindowsFolderPicker();

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea(this);
        SearchDirectoryBox.Focus();
        await Model.InitializeAsync();
    }

    // Never larger than the work area (e.g. 1920x1080 at 200% leaves about 960x516), so nothing starts off screen.
    internal static void FitToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        bool resized = false;
        if (window.ActualWidth > area.Width) { window.Width = area.Width; resized = true; }
        if (window.ActualHeight > area.Height) { window.Height = area.Height; resized = true; }
        if (!resized) return;
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = area.Top + (area.Height - window.Height) / 2;
    }

    private void ChooseFolderClicked(object sender, RoutedEventArgs e)
    {
        if (!Model.IsSettingsEditable) return;
        string? directory = _folders.SelectFolder(this, Model.SearchDirectory);
        if (directory is not null) Model.SearchDirectory = directory;
    }
    private void SearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SearchClicked(sender, e);
    }
    private async void SearchClicked(object sender, RoutedEventArgs e)
    {
        if (!Model.IsSettingsEditable) return;
        bool approved = !Model.HasSession || MessageBox.Show(this,
            "再検索すると現在のArchive・Target設定・選択・解析結果を破棄します。再検索しますか？",
            "再検索の確認", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
        if (approved) await Model.SearchAsync(discardApproved: true);
    }

    private async void BulkAddClicked(object sender, RoutedEventArgs e)
    {
        if (!Model.IsSettingsEditable) return;
        if (Model.Archives.Count == 0)
        {
            MessageBox.Show(this, Model.HasSession ? "検索結果にArchiveがありません。" : "先にアーカイブを検索してください。", "Targetの一括追加",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var editor = TargetEditorWindow.ForBulk(Model.Archives.Select(a => a.Path).ToArray());
        editor.Owner = this;
        if (editor.ShowDialog() == true) await Model.AddTargetsAsync(editor.TemplateText);
    }
    private async void AddTargetClicked(object sender, RoutedEventArgs e)
    {
        if (!Model.IsSettingsEditable || ((FrameworkElement)sender).DataContext is not ArchiveViewModel archive) return;
        var editor = TargetEditorWindow.ForAdd(archive.Path);
        editor.Owner = this;
        if (editor.ShowDialog() == true) await Model.AddTargetsAsync(editor.TemplateText, archive);
    }
    private async void EditTargetClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TargetViewModel { Archive: { } archive } target || !target.CanEdit) return;
        var editor = TargetEditorWindow.ForEdit(archive.Path, target.Template);
        editor.Owner = this;
        if (editor.ShowDialog() == true) await Model.EditTargetAsync(archive, target, editor.TemplateText);
    }
    private void RemoveTargetClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TargetViewModel { Archive: { } archive } target) Model.RemoveTarget(archive, target);
    }
    private async void RefreshTargetClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TargetViewModel target) await Model.RefreshTargetAsync(target);
    }
    private async void AnalyzeTargetClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TargetViewModel { Archive: { } archive } target) await Model.AnalyzeTargetAsync(archive, target);
    }
    private async void AnalyzeArchiveClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ArchiveViewModel archive) await Model.AnalyzeArchiveAsync(archive);
    }
    private async void AnalyzeSelectedClicked(object sender, RoutedEventArgs e) => await Model.AnalyzeSelectedAsync();
    private void CancelAnalysisClicked(object sender, RoutedEventArgs e) => Model.CancelAnalysis();

    // The viewed row is kept in view also when it changes from outside the list (removal, automation).
    private void WorkListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ArchiveList.SelectedItem is { } item) ArchiveList.ScrollIntoView(item);
    }

    // Space toggles the batch selection of the current row; arrows only change the viewed item.
    private void WorkListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.OriginalSource is not ListBoxItem { DataContext: var item }) return;
        e.Handled = true;
        if (!Model.IsSettingsEditable) return;
        if (item is TargetViewModel target) target.IsSelected = !target.IsSelected;
        else if (item is ArchiveViewModel archive) archive.IsSelected = archive.IsSelected != true;
    }

    private void ModeClicked(object sender, RoutedEventArgs e) =>
        ChangeMode((string)((RadioButton)sender).Tag == "Fast" ? CliMode.Fast : CliMode.Strict);
    private void ChangeMode(CliMode mode)
    {
        // The model republishes its mode after a refusal, restoring the radio buttons.
        if (mode == Model.Mode || !Model.IsSettingsEditable) return;
        bool approved = !Model.HasAnalysisResults || MessageBox.Show(this,
            "モードを変更すると、既存の解析結果をすべて破棄します。再解析するまで削除できません。各Targetの直前の削除結果は残ります。変更しますか？",
            "モード変更の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;
        Model.SetMode(mode, approved);
        (Model.IsFast ? FastRadio : StrictRadio).Focus();
    }
    // Arrow keys move between the two modes like a standard radio group (and ask the same confirmation).
    private void ModeGroupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        e.Handled = true;
        ChangeMode(Model.IsFast ? CliMode.Strict : CliMode.Fast);
    }
    // Tabbing into the group lands on the selected mode.
    private void ModeGroupGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var current = Model.IsFast ? FastRadio : StrictRadio;
        if (e.NewFocus is RadioButton radio && radio != current && e.OldFocus is not RadioButton) current.Focus();
    }

    private async void DeleteSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (Model.PlanDeletion() is not { } plan) return;
        // One confirmation for the whole batch; nothing starts before the explicit approval.
        // With nothing deletable the same window only lists the exclusions and has no delete button.
        bool approved = new DeletionConfirmWindow(plan, Model.ConfirmationListText(plan)) { Owner = this }.ShowDialog() == true;
        if (plan.Items.Count != 0) await Model.DeleteAsync(plan, approved);
    }
    private void OpenLogFolderClicked(object sender, RoutedEventArgs e)
    {
        string directory = Model.LogDirectory;
        if (!System.IO.Directory.Exists(directory))
        {
            MessageBox.Show(this, "実行ログの保存フォルダーはまだありません。最初の削除を実行すると作成されます。\n" + directory, "実行ログ",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Only opens the folder: logs are never read, edited or removed by the GUI.
        var info = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add(directory);
        try { System.Diagnostics.Process.Start(info)?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "実行ログ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private bool _closeAfterJob;
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!Model.IsJobRunning) return;
        e.Cancel = true;
        if (_closeAfterJob) return;
        _closeAfterJob = true;
        Model.PropertyChanged += CloseWhenJobEnds;
        // Analysis may be terminated (it deletes nothing); a delete is never killed: the current target
        // finishes, no later target starts, then the window closes.
        if (Model.IsAnalyzing) Model.CancelAnalysis();
        else Model.RequestExit();
    }
    private void CloseWhenJobEnds(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsJobRunning) || Model.IsJobRunning) return;
        Model.PropertyChanged -= CloseWhenJobEnds;
        Dispatcher.BeginInvoke(Close);
    }
    // The work area (row 4) keeps at least this height; below it the whole window scrolls instead of squeezing the lists.
    private const double MinimumWorkAreaHeight = 200;
    private void RootLayoutUpdated(object? sender, EventArgs e)
    {
        double fixedRows = 0;
        for (int i = 0; i < Root.RowDefinitions.Count; i++)
            if (i != 4) fixedRows += Root.RowDefinitions[i].ActualHeight;
        double minimum = Math.Ceiling(fixedRows + MinimumWorkAreaHeight);
        if (Math.Abs(Root.MinHeight - minimum) >= 1) Root.MinHeight = minimum;
    }

    private void SelectionMenuClicked(object sender, RoutedEventArgs e)
    {
        SelectionMenu.PlacementTarget = SelectionMenuButton;
        SelectionMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        SelectionMenu.IsOpen = true;
    }
    private void SelectAllClicked(object sender, RoutedEventArgs e) => Model.SelectTargets(true, false);
    private void ClearAllClicked(object sender, RoutedEventArgs e) => Model.SelectTargets(false, false);
    private void SelectVisibleClicked(object sender, RoutedEventArgs e) => Model.SelectTargets(true, true);
    private void ClearVisibleClicked(object sender, RoutedEventArgs e) => Model.SelectTargets(false, true);
}
