using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Unextract.Gui.Models;
using Unextract.Gui.ViewModels;
using Unextract.Gui.Views;
using Xunit.Abstractions;
using static Unextract.Gui.Tests.AnalysisQueueTests;
using static Unextract.Gui.Tests.SearchSessionTests;
using static Unextract.Gui.Tests.SearchViewTests;

namespace Unextract.Gui.Tests;

// The layout constraints of every screen state (docs/TESTING.md#gui-review), checked on the STA at the default size, the minimum
// size and the logical work areas of a 1920x1080 screen at 150% and 200%: every main control keeps a non-empty area inside the
// window (horizontally always; the window scrolls vertically only below its minimum work area) and no binding fails. The dialogs
// get the same checks for their own controls. Each state is also rendered (a rendering failure fails the test); the pictures are
// saved only when UNEXTRACT_GUI_SHOTS names a directory, for the implementer's screen review (no image comparison). The WPF
// window is real (an HWND with Opacity 0) and uses the same DIP sizes as the published GUI; the published process itself is
// checked only at its start-up size (SmokeUiTests). All data is simulated: no file system target, no deletion.
// Tests that create WPF windows run serially in one collection. When xUnit ran the classes that create MainWindow in
// parallel, a test (in DeploymentTests) failed intermittently; with those classes in the "Wpf" collection the failure
// no longer reproduced (Gui.Tests then passed 15 consecutive runs). It was seen only under parallel test execution, not
// in the product. The mechanism inside WPF was not identified.
[Collection("Wpf")]
public sealed class ScreenRenderTests(ITestOutputHelper output)
{
    internal static readonly (string Name, double Width, double Height)[] Sizes =
        [("default", 1280, 820), ("min", 720, 460), ("150", 1280, 688), ("200", 960, 516)];

    // A long path with a right-to-left override in a directory name (built at run time, never a literal character in the source).
    private static readonly string Deep = @"D:\アーカイブ保管庫\2024年度\プロジェクト資料（最終版）\とても長いディレクトリ名が続く場合の表示確認用フォルダー\第2階層のさらに長いフォルダー名"
        + (char)0x202E + "x";

    private static readonly string[] MainParts =
        ["SearchDirectoryBox", "SearchButton", "ChooseFolderButton", "RecursiveCheckBox", "StrictRadio", "FastRadio", "ArchiveFilterBox",
         "BulkAddButton", "SelectionMenuButton", "ArchiveList", "DetailPanel", "AnalyzeSelectedButton", "DeleteSelectedButton", "NextStepText",
         "StatusText", "DiagnosticsToggle", "OpenLogFolderButton"];

    private sealed record Scene(MainViewModel Model, FakeRunner Runner);

    // Three archives in the main folders, 30 more in a folder that sorts after them (the first three stay first), and one search
    // notification.
    private static Scene Prepare()
    {
        var archives = new List<ArchiveFile>
        {
            new(Deep + @"\資料集A-非常に長いアーカイブ名の例_第1版.zip", 123_456_789, DateTime.UtcNow),
            new(Deep + @"\b.zip", 2048, DateTime.UtcNow), new(@"D:\archives\c.zip", 10, DateTime.UtcNow),
        };
        archives.AddRange(Enumerable.Range(1, 30).Select(i => new ArchiveFile($@"D:\保管庫\多数\many-{i:D2}.zip", 100 + i, DateTime.UtcNow)));
        var search = new FakeSearch { Result = new([.. archives], [new(@"D:\archives\denied", "アクセスが拒否されました。")]) };
        var runner = new FakeRunner();
        var model = new MainViewModel(new("cli", true, "同梱CLIを使用します: C:\\Program Files\\unextract\\cli\\unextract.exe"), search,
            new FakeSettings(), runner, new DeletionPreparationTests.FakeEntries(), new DeletionPreparationTests.FakeLogs())
            { SearchDirectory = Deep };
        return new(model, runner);
    }

    // Problems of the named controls of a window: an empty area, or a position outside the window's content. Horizontally always;
    // vertically only for controls that are not inside a scrolling area of the window (those are reached by scrolling).
    private static IEnumerable<string> PartProblems(Window window, string[] parts, string label)
    {
        var content = (FrameworkElement)window.Content;
        foreach (string id in parts)
        {
            var element = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(id));
            var origin = element.TranslatePoint(new Point(0, 0), content);
            bool scrolls = false;
            for (DependencyObject? up = System.Windows.Media.VisualTreeHelper.GetParent(element); up is not null && up != content;
                 up = System.Windows.Media.VisualTreeHelper.GetParent(up))
                scrolls |= up is ScrollViewer;
            if (element.ActualWidth < 1 || element.ActualHeight < 1) yield return $"{label}: {id} is empty";
            else if (origin.X < -0.5 || origin.X + element.ActualWidth > content.ActualWidth + 0.5 ||
                (!scrolls && (origin.Y < -0.5 || origin.Y + element.ActualHeight > content.ActualHeight + 0.5)))
                yield return $"{label}: {id} is outside the window {origin} {element.ActualWidth}x{element.ActualHeight}";
        }
    }

    private async Task Shoot(Window window, string state, bool parts = true)
    {
        var trace = new BindingErrors();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        try
        {
            foreach (var (name, width, height) in Sizes)
            {
                window.Width = width;
                window.Height = height;
                await Layout(window);
                var problems = new List<string>();
                if (parts)
                {
                    // Only the minimum size permits whole-window scrolling. Other sizes must keep controls in the viewport.
                    var area = (FrameworkElement)window.FindName("Root");
                    var content = (FrameworkElement)window.Content;
                    foreach (string id in MainParts)
                    {
                        var element = (FrameworkElement)window.FindName(id);
                        var origin = element.TranslatePoint(new Point(0, 0), area);
                        if (element.ActualWidth < 1 || element.ActualHeight < 1) problems.Add($"{state}/{name}: {id} is empty");
                        else if (origin.X < -0.5 || origin.Y < -0.5 || origin.X + element.ActualWidth > content.ActualWidth + 0.5 ||
                            origin.Y + element.ActualHeight > (name == "min" ? area.ActualHeight : content.ActualHeight) + 0.5) problems.Add($"{state}/{name}: {id} is outside the window {origin} {element.ActualWidth}x{element.ActualHeight}");
                    }
                }
                var root = (Grid)window.FindName("Root");
                output.WriteLine($"{state}/{name}: root {root.ActualHeight:F0} min {root.MinHeight:F0} rows " +
                    string.Join(",", root.RowDefinitions.Select(r => r.ActualHeight.ToString("F0"))));
                Render(window, $"{state}-{name}");
                Assert.True(problems.Count == 0, string.Join("\n", problems));
            }
            Assert.Empty(trace.Errors);
        }
        finally { System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); }
    }

    // A taller scroll content must not make an off-screen control pass the viewport check.
    [Fact]
    public Task OversizedRootIsRejectedAtDefaultSize() => OnSta(async () =>
    {
        var window = new MainWindow { DataContext = Prepare().Model, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            ((Grid)window.FindName("Root")).Height = 2000;
            var failure = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => Shoot(window, "intentional-overflow"));
            Assert.Contains("outside the window", failure.Message, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MainStatesRenderAtEverySize() => OnSta(async () =>
    {
        var scene = Prepare();
        var model = scene.Model;
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0 };
        var notifications = (ToggleButton)window.FindName("DiagnosticsToggle");
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            await Shoot(window, "01-initial");
            Assert.True(await model.SearchAsync());
            await Shoot(window, "02-searched-many-archives");
            notifications.IsChecked = true;
            await Shoot(window, "02-notifications-expanded");
            notifications.IsChecked = false;
            model.View(model.Archives[0]);
            await Shoot(window, "03-archive-view");
            await model.AddTargetsAsync(TargetTemplate.SameDirectory);
            await model.AddTargetsAsync(@"{{archive.dir}}\展開先\{{archive.name}}");
            model.View(model.Archives[0]);
            await Shoot(window, "03-targets-archive-view");

            var failing = model.Archives[1].Targets[1];
            scene.Runner.Handler = (job, _) => Task.FromResult(job.Target == failing.Path ? Failed(job) : Ok(job,
                Enumerable.Range(1, 400).Select(i => ($"サブフォルダー{i % 7}/とても長いファイル名の例-{i:D4}_説明付き.txt",
                    i % 9 == 0 ? "MODIFIED" : i % 13 == 0 ? "MISSING" : "MATCHED", (long)i * 1000))
                .Append(("サブフォルダー1/", "DIRECTORY", 0L)).ToArray()));
            model.SelectTargets(false, false);
            foreach (var archive in model.Archives.Take(3)) foreach (var target in archive.Targets) target.IsSelected = true;
            await model.AnalyzeSelectedAsync();
            var analyzed = model.Archives[0].Targets[1];
            model.View(analyzed);
            await Shoot(window, "04-analyzed-target-view");
            analyzed.ResultCategory = "MODIFIED";
            analyzed.ResultPathFilter = "0018";
            Assert.Single(analyzed.VisibleResults);
            await Shoot(window, "04-filtered-results");
            analyzed.ResultCategory = "";
            analyzed.ResultPathFilter = "";
            model.View(failing);
            await Shoot(window, "05-failed-target-view");

            model.Archives[0].Targets[1].ResultCategory = "MODIFIED";
            model.View(model.Archives[0].Targets[1]);
            model.ArchiveFilter = "b.zip";
            await Shoot(window, "06-filtered-hidden-view");

            model.ArchiveFilter = "";

            var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            scene.Runner.Handler = (job, _) => gate.Task;
            scene.Runner.Progress = new(CliStartState.Started, new("analyze", "strict", model.Archives[2].Path, model.Archives[2].Targets[0].Path, 842, 842, false), 421);
            model.View(model.Archives[2].Targets[0]);
            var running = model.AnalyzeTargetAsync(model.Archives[2], model.Archives[2].Targets[0]);

            model.RefreshProgress();
            await Shoot(window, "07-analyzing");
            gate.SetResult(Cancelled(scene.Runner.Jobs[^1]));
            await running;
            await Shoot(window, "08-cancelled");

            Assert.True(model.SetMode(CliMode.Fast, discardApproved: true));
            scene.Runner.Handler = (job, _) => Task.FromResult(Ok(job, ("a.txt", "MATCHED", 10), ("b.txt", "MODIFIED", 5)));
            await model.AnalyzeSelectedAsync();
            model.View(model.Archives[0].Targets[0]);
            await Shoot(window, "09-fast-analyzed");

            // Delete results: success, an error end, and an interrupted output (no result record).
            var targets = model.Archives.SelectMany(a => a.Targets).ToArray();
            scene.Runner.Handler = (job, _) => Task.FromResult(job.Operation == CliOperation.Analyze ? Ok(job, ("a.txt", "MATCHED", 10)) :
                job.Target == targets[1].Path ? Interrupted(job) : DeletionPreparationTests.DeleteOk(job, [(1, "a.txt", 10)], 2));
            Assert.True(model.SetMode(CliMode.Strict, discardApproved: true));
            await model.AnalyzeSelectedAsync();
            model.SelectTargets(false, false);
            targets[0].IsSelected = true;
            targets[1].IsSelected = true;
            targets[2].IsSelected = true;
            await model.DeleteAsync(model.PlanDeletion()!, approved: true);
            model.View(targets[1]);
            Assert.StartsWith("削除実行済み: エラー終了（結果が不完全）", targets[1].StateText, StringComparison.Ordinal);
            await Shoot(window, "10-after-delete-unknown");
            model.View(targets[0]);
            await Shoot(window, "11-after-delete-done");
            model.View(targets[2]);
            await Shoot(window, "12-after-delete-unrun");
            notifications.IsChecked = true;
            await Shoot(window, "13-notifications");
            notifications.IsChecked = false;

            // A delete that is still running, then a close request during it (the window waits for the current Target).
            var deleteGate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            scene.Runner.Handler = (job, _) => job.Operation == CliOperation.Analyze ? Task.FromResult(Ok(job, ("a.txt", "MATCHED", 10))) : deleteGate.Task;
            var deleting = targets[3];
            model.SelectTargets(false, false);
            deleting.IsSelected = true;
            await model.AnalyzeSelectedAsync();
            var deletion = model.DeleteAsync(model.PlanDeletion()!, approved: true);
            model.View(deleting);
            Assert.Equal("削除中", deleting.StateText);
            await Shoot(window, "14-deleting");
            window.Close();
            Assert.Equal("現在のTargetの処理が終わり次第終了します。", model.Status);
            await Shoot(window, "15-exit-wait");
            deleteGate.SetResult(DeletionPreparationTests.DeleteOk(scene.Runner.Jobs[^1], [(1, "a.txt", 10)], 0));
            await deletion;
        }
        finally { if (!closed) window.Close(); }
    });

    // No archive found, with a notification (the search directory could not be saved).
    [Fact]
    public Task NoArchiveWithANotificationRendersAtEverySize() => OnSta(async () =>
    {
        var model = new MainViewModel(new("cli", true, "ok"), new FakeSearch { Result = new([], []) },
            new FakeSettings { Error = "設定を保存できませんでした: アクセスが拒否されました。" }, new FakeRunner(),
            new DeletionPreparationTests.FakeEntries(), new DeletionPreparationTests.FakeLogs()) { SearchDirectory = @"D:\empty" };
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            await model.SearchAsync();
            var notifications = (ToggleButton)window.FindName("DiagnosticsToggle");
            Assert.Contains("(1 件)", notifications.Content?.ToString(), StringComparison.Ordinal);
            Assert.Contains("アーカイブが見つかりませんでした", ((TextBlock)window.FindName("NextStepText")).Text, StringComparison.Ordinal);
            notifications.IsChecked = true;
            await Shoot(window, "40-no-archive-with-notification");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task DialogsRenderAtTheirSizes() => OnSta(async () =>
    {
        var scene = Prepare();
        var model = scene.Model;
        Assert.True(await model.SearchAsync());
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        await model.AddTargetsAsync(TargetTemplate.ArchiveDirectory);
        model.SelectTargets(false, false);
        foreach (var archive in model.Archives.Take(3)) foreach (var target in archive.Targets) target.IsSelected = true;
        Assert.True(model.SetMode(CliMode.Fast));
        scene.Runner.Handler = (job, _) => Task.FromResult(job.Target == model.Archives[1].Targets[0].Path ? Failed(job) : Ok(job, ("a.txt", "MATCHED", 10)));
        await model.AnalyzeSelectedAsync();
        model.ArchiveFilter = "c.zip";
        var plan = model.PlanDeletion()!;
        Assert.NotEmpty(plan.Excluded);
        Assert.NotEqual(0, plan.HiddenCount);
        string[] confirmParts = ["HeadingText", "SummaryGrid", "IrreversiblePanel", "Body", "DeleteButton", "CancelButton"];
        string[] editorParts = ["HeadingText", "PresetCombo", "TemplateBox", "PreviewPanel", "OkButton", "CancelButton"];
        async Task ShootDialog(Window dialog, string state, string[] parts, (double W, double H)[] sizes)
        {
            dialog.ShowInTaskbar = false;
            dialog.Opacity = 0;
            var trace = new BindingErrors();
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
            dialog.Show();
            try
            {
                foreach (var (w, h) in sizes)
                {
                    dialog.Width = w;
                    if (!double.IsNaN(h)) dialog.Height = h;
                    await Layout(dialog);
                    string label = $"{state}-{w}x{(double.IsNaN(h) ? "auto" : h)}";
                    // The confirmation scrolls its content vertically at small sizes; its buttons stay outside the scroll area.
                    var problems = PartProblems(dialog, parts, label).ToList();
                    Render(dialog, label);
                    Assert.Empty(problems);
                }
                Assert.Empty(trace.Errors);
            }
            finally
            {
                dialog.Close();
                System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            }
        }
        await ShootDialog(new DeletionConfirmWindow(plan, model.ConfirmationListText(plan)), "20-confirm-fast-hidden-excluded",
            [.. confirmParts, "FastPanel", "HiddenPanel"], [(780, 660), (720, 460), (960, 516)]);
        model.ArchiveFilter = "";
        model.SelectTargets(false, false);
        model.Archives[1].Targets[0].IsSelected = true;
        var none = model.PlanDeletion()!;
        await ShootDialog(new DeletionConfirmWindow(none, model.ConfirmationListText(none)), "21-confirm-nothing", ["HeadingText", "Body", "CancelButton"],
            [(780, 660), (720, 460)]);
        Assert.True(model.SetMode(CliMode.Strict, discardApproved: true));
        await ShootDialog(TargetEditorWindow.ForAdd(model.Archives[0].Path), "22-editor-add", editorParts, [(660, double.NaN)]);
        var named = TargetEditorWindow.ForAdd(model.Archives[0].Path);
        named.Loaded += (_, _) => ((ComboBox)named.FindName("PresetCombo")).SelectedIndex = 1;
        await ShootDialog(named, "22-editor-archive-name", editorParts, [(660, double.NaN)]);
        var invalid = TargetEditorWindow.ForAdd(model.Archives[0].Path);
        invalid.Loaded += (_, _) => ((TextBox)invalid.FindName("TemplateBox")).Text = @"D:\bad\..\path";
        await ShootDialog(invalid, "23-editor-invalid", ["HeadingText", "PresetCombo", "TemplateBox", "OkButton", "CancelButton"], [(660, double.NaN)]);
        await ShootDialog(TargetEditorWindow.ForBulk(model.Archives.Select(a => a.Path).ToArray()), "24-editor-bulk", editorParts, [(660, double.NaN)]);
    });

    // A delete whose output stops after the run record and the first entry: no result record (completion boundary).
    private static CliJobResult Interrupted(CliJob job)
    {
        var run = new CliRun("delete", job.ModeValue, job.Archive, job.Target, 2, 1, true);
        var output = new CliOutput(run, [], null, ProtocolIssue.None, null);
        return new(CliStartState.Started, true, unchecked((int)0xC0000005), true, true, false, output, "", false, null);
    }
}
