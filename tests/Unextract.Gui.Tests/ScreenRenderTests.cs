using System.IO;
using System.Windows;
using System.Windows.Controls;
using Unextract.Gui.Models;
using Unextract.Gui.ViewModels;
using Unextract.Gui.Views;
using Xunit.Abstractions;
using static Unextract.Gui.Tests.AnalysisQueueTests;
using static Unextract.Gui.Tests.SearchSessionTests;
using static Unextract.Gui.Tests.SearchViewTests;

namespace Unextract.Gui.Tests;

// Renders the main states of the window and the dialogs at the default size, the minimum size and the logical work
// areas of a 1920x1080 screen at 150% and 200%, into the test fixture (for the implementer's screen review; no image
// comparison). Each render also checks that the main controls keep a non-empty area inside the window and that no
// binding fails. All data is simulated: no file system target, no deletion.
// Tests that create WPF windows run serially in one collection. When xUnit ran the classes that create MainWindow in
// parallel, a test (in DeploymentTests) failed intermittently; with those classes in the "Wpf" collection the failure
// no longer reproduced (Gui.Tests then passed 15 consecutive runs). It was seen only under parallel test execution, not
// in the product. The mechanism inside WPF was not identified.
[Collection("Wpf")]
public sealed class ScreenRenderTests(ITestOutputHelper output)
{
    internal static readonly (string Name, double Width, double Height)[] Sizes =
        [("default", 1280, 820), ("min", 720, 460), ("150", 1280, 688), ("200", 960, 516)];

    private static readonly string Deep = @"D:\アーカイブ保管庫\2024年度\プロジェクト資料（最終版）\とても長いディレクトリ名が続く場合の表示確認用フォルダー";

    private static readonly string[] MainParts =
        ["SearchDirectoryBox", "SearchButton", "ChooseFolderButton", "RecursiveCheckBox", "StrictRadio", "FastRadio", "ArchiveFilterBox",
         "BulkAddButton", "ArchiveList", "DetailPanel", "AnalyzeSelectedButton", "DeleteSelectedButton", "NextStepText", "StatusText",
         "OpenLogFolderButton"];

    private sealed record Scene(MainViewModel Model, FakeRunner Runner, string Folder);

    private static async Task<Scene> Prepare()
    {
        var search = new FakeSearch
        {
            Result = new([new(Deep + @"\資料集A-非常に長いアーカイブ名の例_第1版.zip", 123_456_789, DateTime.UtcNow),
                new(Deep + @"\b.zip", 2048, DateTime.UtcNow), new(@"D:\archives\c.zip", 10, DateTime.UtcNow)],
                [new(@"D:\archives\denied", "アクセスが拒否されました。")]),
        };
        var runner = new FakeRunner();
        var model = new MainViewModel(new("cli", true, "同梱CLIを使用します: C:\\Program Files\\unextract\\cli\\unextract.exe"), search,
            new FakeSettings(), runner, new DeletionPreparationTests.FakeEntries(), new DeletionPreparationTests.FakeLogs())
            { SearchDirectory = Deep };
        string folder = ArchiveSearchTests.Fixture("render");
        return new(model, runner, folder);
    }

    private async Task Shoot(Window window, Scene scene, string state, bool parts = true)
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
                var content = (FrameworkElement)window.Content;
                var problems = new List<string>();
                if (parts)
                {
                    // Inside the window's scrollable area: horizontally within the window, and with a non-empty size.
                    var area = (FrameworkElement)window.FindName("Root");
                    foreach (string id in MainParts)
                    {
                        var element = (FrameworkElement)window.FindName(id);
                        var origin = element.TranslatePoint(new Point(0, 0), area);
                        if (element.ActualWidth < 1 || element.ActualHeight < 1) problems.Add($"{state}/{name}: {id} is empty");
                        else if (origin.X < -0.5 || origin.Y < -0.5 || origin.X + element.ActualWidth > content.ActualWidth + 0.5 ||
                            origin.Y + element.ActualHeight > area.ActualHeight + 0.5) problems.Add($"{state}/{name}: {id} is outside the window {origin} {element.ActualWidth}x{element.ActualHeight}");
                    }
                }
                var root = (Grid)window.FindName("Root");
                output.WriteLine($"{state}/{name}: root {root.ActualHeight:F0} min {root.MinHeight:F0} rows " +
                    string.Join(",", root.RowDefinitions.Select(r => r.ActualHeight.ToString("F0"))));
                Render(window, Path.Combine(scene.Folder, $"{state}-{name}.png"));
                Assert.Empty(problems);
            }
            Assert.Empty(trace.Errors);
        }
        finally { System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); }
    }

    [Fact]
    public Task MainStatesRenderAtEverySize() => OnSta(async () =>
    {
        var scene = await Prepare();
        var model = scene.Model;
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            await Shoot(window, scene, "01-initial");
            Assert.True(await model.SearchAsync());
            await Shoot(window, scene, "02-searched");
            await model.AddTargetsAsync(TargetTemplate.SameDirectory);
            await model.AddTargetsAsync(@"{{archive.dir}}\展開先\{{archive.name}}");
            model.View(model.Archives[0]);
            await Shoot(window, scene, "03-targets-archive-view");

            var failing = model.Archives[1].Targets[1];
            scene.Runner.Handler = (job, _) => Task.FromResult(job.Target == failing.Path ? Failed(job) : Ok(job,
                Enumerable.Range(1, 400).Select(i => ($"サブフォルダー{i % 7}/とても長いファイル名の例-{i:D4}_説明付き.txt",
                    i % 9 == 0 ? "MODIFIED" : i % 13 == 0 ? "MISSING" : "MATCHED", (long)i * 1000))
                .Append(("サブフォルダー1/", "DIRECTORY", 0L)).ToArray()));
            await model.AnalyzeSelectedAsync();
            model.View(model.Archives[0].Targets[1]);
            await Shoot(window, scene, "04-analyzed-target-view");
            model.View(failing);
            await Shoot(window, scene, "05-failed-target-view");

            model.Archives[0].Targets[1].ResultCategory = "MODIFIED";
            model.View(model.Archives[0].Targets[1]);
            model.ArchiveFilter = "b.zip";
            await Shoot(window, scene, "06-filtered-hidden-view");

            model.ArchiveFilter = "";

            var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            scene.Runner.Handler = (job, _) => gate.Task;
            scene.Runner.Progress = new(CliStartState.Started, new("analyze", "strict", model.Archives[2].Path, model.Archives[2].Targets[0].Path, 842, 842, false), 421);
            model.View(model.Archives[2].Targets[0]);
            var running = model.AnalyzeTargetAsync(model.Archives[2], model.Archives[2].Targets[0]);

            model.RefreshProgress();
            await Shoot(window, scene, "07-analyzing");
            gate.SetResult(Cancelled(scene.Runner.Jobs[^1]));
            await running;
            await Shoot(window, scene, "08-cancelled");

            Assert.True(model.SetMode(CliMode.Fast, discardApproved: true));
            scene.Runner.Handler = (job, _) => Task.FromResult(Ok(job, ("a.txt", "MATCHED", 10), ("b.txt", "MODIFIED", 5)));
            await model.AnalyzeSelectedAsync();
            model.View(model.Archives[0].Targets[0]);
            await Shoot(window, scene, "09-fast-analyzed");

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
            await Shoot(window, scene, "10-after-delete-unknown");
            model.View(targets[0]);
            await Shoot(window, scene, "11-after-delete-done");
            model.View(targets[2]);
            await Shoot(window, scene, "12-after-delete-unrun");
            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("DiagnosticsToggle")).IsChecked = true;
            await Shoot(window, scene, "13-notifications");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "render-path.txt"), scene.Folder);
            output.WriteLine("renders: " + scene.Folder);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task DialogsRenderAtTheirSizes() => OnSta(async () =>
    {
        var scene = await Prepare();
        var model = scene.Model;
        Assert.True(await model.SearchAsync());
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        await model.AddTargetsAsync(TargetTemplate.ArchiveDirectory);
        Assert.True(model.SetMode(CliMode.Fast));
        scene.Runner.Handler = (job, _) => Task.FromResult(job.Target == model.Archives[1].Targets[0].Path ? Failed(job) : Ok(job, ("a.txt", "MATCHED", 10)));
        await model.AnalyzeSelectedAsync();
        model.ArchiveFilter = "c.zip";
        var plan = model.PlanDeletion()!;
        Assert.NotEmpty(plan.Excluded);
        Assert.NotEqual(0, plan.HiddenCount);
        async Task ShootDialog(Window dialog, string state, (double W, double H)[] sizes)
        {
            dialog.ShowInTaskbar = false;
            dialog.Opacity = 0;
            dialog.Show();
            try
            {
                foreach (var (w, h) in sizes)
                {
                    dialog.Width = w;
                    if (!double.IsNaN(h)) dialog.Height = h;
                    await Layout(dialog);
                    Render(dialog, Path.Combine(scene.Folder, $"{state}-{w}x{(double.IsNaN(h) ? "auto" : h)}.png"));
                }
            }
            finally { dialog.Close(); }
        }
        await ShootDialog(new DeletionConfirmWindow(plan, model.ConfirmationListText(plan)), "20-confirm-fast-hidden-excluded", [(780, 660), (720, 460), (960, 516)]);
        model.ArchiveFilter = "";
        model.SelectTargets(false, false);
        model.Archives[1].Targets[0].IsSelected = true;
        var none = model.PlanDeletion()!;
        await ShootDialog(new DeletionConfirmWindow(none, model.ConfirmationListText(none)), "21-confirm-nothing", [(780, 660), (720, 460)]);
        var add = TargetEditorWindow.ForAdd(model.Archives[0].Path);
        await ShootDialog(add, "22-editor-add", [(660, double.NaN)]);
        var invalid = TargetEditorWindow.ForAdd(model.Archives[0].Path);
        invalid.Loaded += (_, _) => ((TextBox)invalid.FindName("TemplateBox")).Text = @"D:\bad\..\path";
        await ShootDialog(invalid, "23-editor-invalid", [(660, double.NaN)]);
        await ShootDialog(TargetEditorWindow.ForBulk(model.Archives.Select(a => a.Path).ToArray()), "24-editor-bulk", [(660, double.NaN)]);
        output.WriteLine("renders: " + scene.Folder);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "render-dialogs-path.txt"), scene.Folder);
    });

    // A delete whose output stops after the run record and the first entry: no result record (completion boundary).
    private static CliJobResult Interrupted(CliJob job)
    {
        var run = new CliRun("delete", job.ModeValue, job.Archive, job.Target, 2, 1, true);
        var output = new CliOutput(run, [], null, ProtocolIssue.None, null);
        return new(CliStartState.Started, true, unchecked((int)0xC0000005), true, true, false, output, "", false, null);
    }
}
