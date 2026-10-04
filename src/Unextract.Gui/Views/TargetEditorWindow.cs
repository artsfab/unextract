using System.IO;
using System.Windows;
using System.Windows.Controls;
using Unextract.Gui.Models;

namespace Unextract.Gui.Views;

// Adds a Target to one archive, edits an unanalyzed one, or adds the same rule to every searched archive.
// It only checks the template format (TargetTemplate); duplicates and the per-archive results are the view model's.
public partial class TargetEditorWindow : Window
{
    private readonly string[] _archives;
    private readonly bool _bulk;
    private bool _syncing;

    public string TemplateText => TemplateBox.Text;

    private TargetEditorWindow(string[] archives, string initialTemplate, bool bulk, string heading, string ok)
    {
        InitializeComponent();
        _archives = archives;
        _bulk = bulk;
        HeadingText.Text = heading;
        OkButton.Content = ok;
        ScopeText.Text = bulk
            ? $"検索した全 {archives.Length:N0} 件のArchiveに追加します。既存のTargetは残ります。解決後のパスが同じTargetが既にあるArchiveでは追加しません。登録できないArchiveは通知に表示します。"
            : "Archive: " + DisplayText.Escape(archives[0]);
        TemplateBox.Text = initialTemplate;
        SyncPreset();
        Validate();
        Loaded += (_, _) =>
        {
            TemplateBox.Focus();
            TemplateBox.SelectAll();
        };
    }

    public static TargetEditorWindow ForAdd(string archivePath) =>
        new([archivePath], TargetTemplate.SameDirectory, false, "このArchiveにTargetを追加", "登録");
    public static TargetEditorWindow ForEdit(string archivePath, string template) =>
        new([archivePath], template, false, "Targetの設定を編集", "登録");
    public static TargetEditorWindow ForBulk(string[] archivePaths) =>
        new(archivePaths, TargetTemplate.SameDirectory, true, "全ArchiveにTargetを一括追加", "追加");

    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || TemplateBox is null) return;
        if (PresetCombo.SelectedIndex == 0) TemplateBox.Text = TargetTemplate.SameDirectory;
        if (PresetCombo.SelectedIndex == 1) TemplateBox.Text = TargetTemplate.ArchiveDirectory;
        if (PresetCombo.SelectedIndex == 2 && IsPreset(TemplateBox.Text)) TemplateBox.Text = @"{{archive.dir}}\";
    }

    private void TemplateChanged(object sender, TextChangedEventArgs e)
    {
        if (PreviewText is null) return;
        SyncPreset();
        Validate();
    }

    private static bool IsPreset(string text) => text is TargetTemplate.SameDirectory or TargetTemplate.ArchiveDirectory;

    private void SyncPreset()
    {
        _syncing = true;
        try
        {
            PresetCombo.SelectedIndex = TemplateBox.Text == TargetTemplate.SameDirectory ? 0 :
                TemplateBox.Text == TargetTemplate.ArchiveDirectory ? 1 : 2;
        }
        finally { _syncing = false; }
    }

    private void Validate()
    {
        bool ok;
        if (!_bulk)
        {
            try
            {
                PreviewText.Text = "解決先: " + DisplayText.Escape(TargetTemplate.Resolve(TemplateText, _archives[0]));
                ok = true;
            }
            catch (ArgumentException e)
            {
                PreviewText.Text = DisplayText.Escape(e.Message);
                ok = false;
            }
        }
        else
        {
            // Every archive is resolved: the rule is offered when at least one archive gets a Target.
            string? example = null, exampleArchive = null, firstError = null;
            int resolvable = 0;
            foreach (string archive in _archives)
            {
                try
                {
                    string path = TargetTemplate.Resolve(TemplateText, archive);
                    resolvable++;
                    if (example is null) { example = path; exampleArchive = archive; }
                }
                catch (ArgumentException e) { firstError ??= e.Message; }
            }
            int errors = _archives.Length - resolvable;
            PreviewText.Text = example is null
                ? DisplayText.Escape(firstError ?? "")
                : $"例: {DisplayText.Escape(Path.GetFileName(exampleArchive!))} → {DisplayText.Escape(example)}\n" +
                  $"形式が正しいArchive: {resolvable:N0} / {_archives.Length:N0} 件" +
                  (errors == 0 ? "" : $"（登録エラーになるArchive {errors:N0} 件。例: {DisplayText.Escape(firstError!)}）");
            ok = resolvable != 0;
        }
        OkButton.IsEnabled = ok;
        PreviewPanel.Background = (System.Windows.Media.Brush)FindResource(ok ? "SuccessSoftBrush" : "DangerSoftBrush");
        PreviewPanel.BorderBrush = (System.Windows.Media.Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
        PreviewText.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "TextBrush" : "DangerBrush");
    }

    private void OkClicked(object sender, RoutedEventArgs e)
    {
        if (OkButton.IsEnabled) DialogResult = true;
    }
}
