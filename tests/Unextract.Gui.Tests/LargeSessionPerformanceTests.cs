using System.Diagnostics;
using System.Windows.Controls;
using Unextract.Gui.Models;
using Unextract.Gui.Services;
using Unextract.Gui.ViewModels;
using Unextract.Gui.Views;
using Xunit.Abstractions;
using static Unextract.Gui.Tests.AnalysisQueueTests;
using static Unextract.Gui.Tests.SearchSessionTests;
using static Unextract.Gui.Tests.SearchViewTests;

namespace Unextract.Gui.Tests;

// Measurements for many archives and Targets: the work list of 10,000 archives with 3 Targets each, and browsing
// while a job reports progress. Simulated data only; no deletion. The target for ordinary operations (select, view, filter) is an answer within one second;
// the numbers go to the test output (they describe this machine, not a guarantee for real data).
// Measured: bulk add, first display, filtering, select all / none, per-item and per-Archive selection, details switching,
// scrolling and mode change; details switching and filtering while analysis progress is received. Process memory is written to the
// output (it includes tests running in parallel in the same host). Searching many ZIPs on a real disk is not measured continuously
// (the search function is ArchiveSearchTests). Real GPU rendering and the feel of operation are manual (M14).
// Tests that create WPF windows run serially in one collection. When xUnit ran the classes that create MainWindow in
// parallel, a test (in DeploymentTests) failed intermittently; with those classes in the "Wpf" collection the failure
// no longer reproduced (Gui.Tests then passed 15 consecutive runs). It was seen only under parallel test execution, not
// in the product. The mechanism inside WPF was not identified.
[Collection("Wpf")]
public sealed class LargeSessionPerformanceTests(ITestOutputHelper output)
{
    private const int ManyArchives = 10_000;
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(1);

    private static FakeSearch Archives(int count) => new()
    {
        Result = new(Enumerable.Range(1, count).Select(i => new ArchiveFile($@"D:\保管庫\グループ{i % 50:D2}\archive-{i:D5}.zip", 1000 + i, DateTime.UtcNow)).ToArray(), []),
    };

    private void Report(string what, TimeSpan time) => output.WriteLine($"{what}: {time.TotalMilliseconds:F0} ms");

    private static string Memory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return $"プロセス: ワーキングセット {process.WorkingSet64 / 1024 / 1024:N0} MiB / プライベート {process.PrivateMemorySize64 / 1024 / 1024:N0} MiB / マネージドヒープ {GC.GetTotalMemory(false) / 1024 / 1024:N0} MiB";
    }

    [Fact]
    public Task TenThousandArchivesWithThreeTargetsEachStayResponsive() => OnSta(async () =>
    {
        var model = await Session(Archives(ManyArchives));
        output.WriteLine("開始時 " + Memory());
        var watch = Stopwatch.StartNew();
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        await model.AddTargetsAsync(TargetTemplate.ArchiveDirectory);
        await model.AddTargetsAsync(@"{{archive.dir}}\展開先\{{archive.name}}");
        Report($"{ManyArchives:N0} Archive × 3 Target の一括追加 (3回)", watch.Elapsed);
        Assert.Equal(ManyArchives * 4, model.WorkItems.Count);

        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0, Width = 1280, Height = 820 };
        try
        {
            watch.Restart();
            window.Show();
            await Layout(window);
            Report($"初回表示 ({model.WorkItems.Count:N0} 行)", watch.Elapsed);
            var list = (ListBox)window.FindName("ArchiveList");
            int realized = Descendants<ListBoxItem>(list).Count();
            output.WriteLine($"作業一覧の実体化された行: {realized}");
            Assert.True(realized < 200, $"仮想化されていない: {realized}");

            var times = new List<(string What, TimeSpan Time)>();
            async Task Measure(string what, Action action)
            {
                var w = Stopwatch.StartNew();
                action();
                await Layout(window);
                times.Add((what, w.Elapsed));
                Report(what, w.Elapsed);
            }
            var filter = (TextBox)window.FindName("ArchiveFilterBox");
            await Measure("絞り込み (1件に一致)", () => { filter.Text = "archive-09999"; filter.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); });
            Assert.Equal(4, list.Items.Count);
            await Measure("絞り込み (200件に一致)", () => { filter.Text = "グループ07"; filter.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); });
            await Measure("絞り込みの解除", () => { filter.Text = ""; filter.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); });
            Assert.Equal(ManyArchives * 4, list.Items.Count);
            await Measure("すべて解除", () => Click(window, "ClearAllButton"));
            Assert.All(model.Archives, a => Assert.Equal(false, a.IsSelected));
            await Measure("すべて選択", () => Click(window, "SelectAllButton"));
            await Measure("Target 1件のチェック切替", () => model.Archives[5000].Targets[1].IsSelected = false);
            await Measure("Archive 1件のまとめ選択", () => model.Archives[5000].IsSelected = true);
            for (int i = 0; i < 5; i++)
                await Measure($"詳細の切替 {i + 1}", () => list.SelectedItem = model.Archives[i * 1999].Targets[i % 3]);
            await Measure("末尾へスクロール", () => list.ScrollIntoView(list.Items[^1]));
            await Measure("先頭へスクロール", () => list.ScrollIntoView(list.Items[0]));
            await Measure("全件のモード変更 (結果なし)", () => model.SetMode(CliMode.Fast));
            output.WriteLine("操作後 " + Memory());
            foreach (var (what, time) in times) Assert.True(time < Answer, $"{what} が遅い: {time}");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ViewingAndFilteringStayResponsiveWhileAJobReportsProgress() => OnSta(async () =>
    {
        var runner = new FakeRunner();
        var model = await Session(Archives(2_000), runner);
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = (job, _) => gate.Task;
        model.ProgressInterval = TimeSpan.FromMilliseconds(20);
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0, Width = 1280, Height = 820 };
        try
        {
            window.Show();
            await Layout(window);
            var running = model.AnalyzeSelectedAsync();
            var list = (ListBox)window.FindName("ArchiveList");
            var filter = (TextBox)window.FindName("ArchiveFilterBox");
            var slowest = TimeSpan.Zero;
            for (int i = 0; i < 40; i++)
            {
                // The runner's progress snapshot changes underneath while the window is used.
                runner.Progress = new(CliStartState.Started, new("analyze", "strict", model.Archives[0].Path, model.Archives[0].Targets[0].Path, 100_000, 100_000, false), i * 2_500);
                var w = Stopwatch.StartNew();
                if (i % 2 == 0) list.SelectedItem = model.Archives[(i * 37) % 2_000].Targets[0];
                else { filter.Text = i % 4 == 1 ? "グループ1" : ""; filter.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); }
                await Layout(window);
                if (w.Elapsed > slowest) slowest = w.Elapsed;
            }
            Assert.True(model.IsAnalyzing);
            Assert.Contains("エントリ", model.EntryProgressText, StringComparison.Ordinal);
            Report("進捗受信中の詳細切替・絞り込み (40回) の最大", slowest);
            gate.SetResult(Cancelled(runner.Jobs[0]));
            model.CancelAnalysis();
            await running;
            Assert.True(slowest < Answer, $"進捗受信中の操作が遅い: {slowest}");
        }
        finally { window.Close(); }
    });
}
