using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Unextract.Gui.Models;
using Unextract.Gui.Services;
using Unextract.Gui.ViewModels;
using Unextract.Gui.Views;
using Xunit.Abstractions;
using static Unextract.Gui.Tests.AnalysisQueueTests;
using static Unextract.Gui.Tests.JsonlReceiverTests;
using static Unextract.Gui.Tests.SearchSessionTests;
using static Unextract.Gui.Tests.SearchViewTests;

namespace Unextract.Gui.Tests;

// Acceptance measurements at the ZIP entry-count limit (100,000 entries per archive, docs/spec/zip.md) and
// several Targets held at once. All data is simulated in memory: no file system target, no deletion.
// The bounds are deliberately loose regression guards; the measured numbers go to the test output.
// Tests that create WPF windows run serially in one collection. When xUnit ran the classes that create MainWindow in
// parallel, a test (in DeploymentTests) failed intermittently; with those classes in the "Wpf" collection the failure
// no longer reproduced (Gui.Tests then passed 15 consecutive runs). It was seen only under parallel test execution, not
// in the product. The mechanism inside WPF was not identified.
[Collection("Wpf")]
public sealed class PerformanceTests(ITestOutputHelper output)
{
    private const int EntriesPerTarget = 100_000;
    private const int Archives = 10;

    private static string NameOf(int i) =>
        $"dir{i % 200:D3}/サブ{i % 37:D2}/very-long-directory-name-for-measurement/ファイル-{i:D6}-\u00C9\u9854.bin";

    private static CliJobResult Big(CliJob job, int total)
    {
        string candidate = job.Mode == CliMode.Strict ? "MATCHED" : "SAME_SIZE";
        var entries = new CliEntry[total];
        int[] counts = new int[3];
        for (int i = 1; i <= total; i++)
        {
            int kind = i % 10 == 0 ? 2 : i % 7 == 0 ? 1 : 0;
            counts[kind]++;
            entries[i - 1] = new(i, NameOf(i), false, i * 1024L, kind == 0 ? candidate : kind == 1 ? "MODIFIED" : "MISSING", null, null, null);
        }
        var map = new Dictionary<string, int> { [job.Mode == CliMode.Strict ? "matched" : "same_size"] = counts[0], ["modified"] = counts[1],
            ["missing"] = counts[2], ["skipped_special_file"] = 0, ["directory"] = 0 };
        var run = new CliRun("analyze", job.ModeValue, job.Archive, job.Target, total, total, false);
        var result = new CliOutput(run, entries, new("completed", 0, map, null), ProtocolIssue.None, null);
        return new(CliStartState.Started, true, 0, true, true, false, result, "", false, null);
    }

    private static FakeSearch ManyArchives() =>
        new() { Result = new(Enumerable.Range(1, Archives).Select(i => new ArchiveFile($@"D:\archives\a{i:D2}.zip", 1000, DateTime.UtcNow)).ToArray(), []) };

    [Fact]
    public void ReceivingOneHundredThousandEntriesInChunksStaysFast()
    {
        var job = Job();
        var text = new StringBuilder(Run(job, EntriesPerTarget));
        for (int i = 1; i <= EntriesPerTarget; i++) text.Append(Entry("MATCHED", i, i * 1024L, NameOf(i)));
        text.Append(Completed(job, EntriesPerTarget));
        byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
        var watch = Stopwatch.StartNew();
        var receiver = new JsonlReceiver(job);
        for (int offset = 0; offset < bytes.Length; offset += 4096)
            receiver.Feed(bytes.AsSpan(offset, Math.Min(4096, bytes.Length - offset)));
        var result = receiver.Finish();
        watch.Stop();
        output.WriteLine($"JSONL受信: {EntriesPerTarget:N0} entry / {bytes.Length / 1024.0 / 1024:F1} MiB を4 KiB単位で {watch.ElapsedMilliseconds} ms");
        Assert.True(result.IsCompatible);
        Assert.Equal(EntriesPerTarget, result.Entries.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"受信が遅い: {watch.Elapsed}");
    }

    [Fact]
    public async Task TenTargetsOfOneHundredThousandEntriesAreAdoptedFilteredAndHeldWithinBounds()
    {
        var runner = new FakeRunner { Handler = (job, _) => Task.FromResult(Big(job, EntriesPerTarget)) };
        var model = await Session(ManyArchives(), runner);
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        var watch = Stopwatch.StartNew();
        var result = await model.AnalyzeSelectedAsync().WaitAsync(TimeSpan.FromMinutes(2));
        watch.Stop();
        Assert.Equal(Archives, result.Succeeded);
        long held = GC.GetTotalMemory(true) - before;
        output.WriteLine($"{Archives} Target x {EntriesPerTarget:N0} entry の採用: {watch.ElapsedMilliseconds} ms、保持メモリ {held / 1024.0 / 1024:F0} MiB (1 Targetあたり {held / 1024.0 / 1024 / Archives:F0} MiB)");
        var target = model.Archives[0].Targets[0];
        Assert.Equal(EntriesPerTarget, target.VisibleResults.Count);
        watch.Restart();
        target.ResultPathFilter = "ファイル-05";
        int pathHits = target.VisibleResults.Count;
        output.WriteLine($"パス絞り込み (部分一致、{pathHits:N0} 件): {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        target.ResultPathFilter = "";
        target.ResultCategory = "MODIFIED";
        int category = target.VisibleResults.Count;
        output.WriteLine($"分類絞り込み ({category:N0} 件): {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        var plan = model.PlanDeletion();
        output.WriteLine($"削除計画 (候補 {plan!.Items.Sum(i => i.Candidates.Count):N0} 件): {watch.ElapsedMilliseconds} ms");
        Assert.True(held < 3L * 1024 * 1024 * 1024, $"保持メモリが大きい: {held}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public Task ViewScrollAndFilterOneHundredThousandEntriesKeepTheWindowResponsive() => OnSta(async () =>
    {
        var runner = new FakeRunner { Handler = (job, _) => Task.FromResult(Big(job, EntriesPerTarget)) };
        var model = await Session(ManyArchives(), runner);
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        Assert.Equal(Archives, (await model.AnalyzeSelectedAsync().WaitAsync(TimeSpan.FromMinutes(2))).Succeeded);
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            var watch = Stopwatch.StartNew();
            await Layout(window);
            output.WriteLine($"{Archives} Archive / {Archives} Target の初回レイアウト: {watch.ElapsedMilliseconds} ms");
            var detail = (ContentControl)window.FindName("DetailPanel");
            var slowest = TimeSpan.Zero;
            for (int round = 0; round < 3; round++)
                foreach (var archive in model.Archives)
                {
                    watch.Restart();
                    model.View(archive.Targets[0]);
                    await Layout(window);
                    if (watch.Elapsed > slowest) slowest = watch.Elapsed;
                }
            output.WriteLine($"Target詳細の切替 ({Archives * 3} 回、各{EntriesPerTarget:N0}件の結果一覧): 最大 {slowest.TotalMilliseconds:F0} ms");
            var list = Descendants<ListBox>(detail).Single(l => Id(l) == "ResultList");
            Assert.Equal(EntriesPerTarget, list.Items.Count);
            int realized = Descendants<ListBoxItem>(list).Count();
            output.WriteLine($"結果一覧の実体化された項目: {realized}");
            Assert.True(realized < 500, $"仮想化されていない: {realized}");

            watch.Restart();
            list.ScrollIntoView(list.Items[EntriesPerTarget - 1]);
            await Layout(window);
            output.WriteLine($"末尾へスクロール: {watch.ElapsedMilliseconds} ms");
            watch.Restart();
            list.ScrollIntoView(list.Items[0]);
            await Layout(window);
            output.WriteLine($"先頭へスクロール: {watch.ElapsedMilliseconds} ms");

            var filter = Descendants<TextBox>(detail).Single(t => Id(t) == "ResultPathFilterBox");
            watch.Restart();
            filter.Text = "ファイル-05";
            filter.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            await Layout(window);
            var filterTime = watch.Elapsed;
            output.WriteLine($"パス絞り込みの反映 (表示 {Descendants<ListBox>(detail).Single(l => Id(l) == "ResultList").Items.Count:N0} 件): {filterTime.TotalMilliseconds:F0} ms");
            watch.Restart();
            filter.Text = "";
            filter.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            await Layout(window);
            output.WriteLine($"絞り込み解除の反映: {watch.ElapsedMilliseconds} ms");
            // Target (docs/TESTING.md#gui): ordinary viewing and filtering answer within a second.
            Assert.True(slowest < TimeSpan.FromSeconds(1), $"詳細の切替が遅い: {slowest}");
            Assert.True(filterTime < TimeSpan.FromSeconds(1), $"絞り込みが遅い: {filterTime}");
        }
        finally { window.Close(); }
    });
}
