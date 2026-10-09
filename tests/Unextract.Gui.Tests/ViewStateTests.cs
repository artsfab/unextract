using Unextract.Gui.Models;
using Unextract.Gui.ViewModels;
using static Unextract.Gui.Tests.AnalysisQueueTests;
using static Unextract.Gui.Tests.SearchSessionTests;

namespace Unextract.Gui.Tests;

// The display state of the redesigned window: the viewed item, the flat work list, the next-step guide and the
// selection summary. All of it is display only; these tests also pin that it never changes selection or plans.
// The viewed item (details pane) is independent of selection, filtering, removal and the lock while running: a filter that hides it
// keeps the view and says so, removing the viewed Target returns to its Archive, and the selection never changes. Also the
// transitions of the next-step guide and the selection summary, the status badges, the row summaries and the reasons an operation
// is unavailable.
public sealed class ViewStateTests
{
    [Fact]
    public async Task WorkListListsEachVisibleArchiveFollowedByItsTargetsAndFollowsAdditionsAndRemovals()
    {
        var model = await Session();
        Assert.Equal(model.Archives, model.WorkItems);
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        await model.AddTargetsAsync(@"{{archive.dir}}\x", model.Archives[1]);
        Assert.Equal(new object[] { model.Archives[0], model.Archives[0].Targets[0], model.Archives[1], model.Archives[1].Targets[0], model.Archives[1].Targets[1] },
            model.WorkItems);
        Assert.All(model.Archives.SelectMany(a => a.Targets.Select(t => (a, t))), p => Assert.Same(p.a, p.t.Archive));
        model.ArchiveFilter = "B.zip";
        Assert.Equal(new object[] { model.Archives[1], model.Archives[1].Targets[0], model.Archives[1].Targets[1] }, model.WorkItems);
        Assert.True(model.RemoveTarget(model.Archives[1], model.Archives[1].Targets[0]));
        Assert.Equal(new object[] { model.Archives[1], model.Archives[1].Targets[0] }, model.WorkItems);
    }

    [Fact]
    public async Task ViewingIsIndependentOfSelectionFiltersAndRemoval()
    {
        var model = await Session();
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        var a = model.Archives[0].Targets[0];
        var b = model.Archives[1].Targets[0];
        b.IsSelected = false;
        model.View(a);
        Assert.Same(a, model.Viewed);
        Assert.Same(a, model.SelectedWorkItem);
        Assert.Equal("", model.ViewedHiddenText);
        model.View(b);
        model.View(model.Archives[0]);
        Assert.True(a.IsSelected);
        Assert.False(b.IsSelected);

        // A filter that hides the viewed item keeps it viewed (and says so); the list row selection clears.
        model.View(a);
        model.ArchiveFilter = "B.zip";
        Assert.Same(a, model.Viewed);
        Assert.Null(model.SelectedWorkItem);
        Assert.Contains("一覧に表示されていません", model.ViewedHiddenText, StringComparison.Ordinal);
        model.SelectedWorkItem = null;
        Assert.Same(a, model.Viewed);
        model.ArchiveFilter = "";
        Assert.Same(a, model.SelectedWorkItem);
        Assert.Equal("", model.ViewedHiddenText);

        // Removing the viewed Target shows its archive; nothing else changes.
        Assert.True(model.RemoveTarget(model.Archives[0], a));
        Assert.Same(model.Archives[0], model.Viewed);
        Assert.False(b.IsSelected);
        // Items that are not in the session are never viewed.
        model.View(a);
        Assert.Same(model.Archives[0], model.Viewed);
        model.View("not an item");
        Assert.Same(model.Archives[0], model.Viewed);
        // A new search starts with nothing viewed.
        Assert.True(await model.SearchAsync(discardApproved: true));
        Assert.Null(model.Viewed);
    }

    [Fact]
    public async Task ViewingDuringALockedRunIsAllowedAndChangesNothingElse()
    {
        var runner = new FakeRunner();
        var model = await Session(new FakeSearch(), runner);
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = (_, _) => gate.Task;
        var running = model.AnalyzeSelectedAsync();
        Assert.True(model.IsBusy);
        model.View(model.Archives[1].Targets[0]);
        Assert.Same(model.Archives[1].Targets[0], model.Viewed);
        Assert.All(model.Archives, x => Assert.True(x.Targets[0].IsSelected));
        Assert.Contains("解析中です", model.NextStepText, StringComparison.Ordinal);
        Assert.Contains("処理の実行中は", model.Archives[1].Targets[0].ActionHint, StringComparison.Ordinal);
        gate.SetResult(Ok(runner.Jobs[0]));
        runner.Handler = null;
        await running;
        Assert.Equal(2, runner.Jobs.Count);
    }

    [Fact]
    public async Task NextStepAndSelectionSummaryFollowTheSessionWithoutDecidingAnything()
    {
        var runner = new FakeRunner();
        var model = new MainViewModel(new("cli", true, "available"), new FakeSearch(), new FakeSettings(), runner,
            new DeletionPreparationTests.FakeEntries(), new DeletionPreparationTests.FakeLogs()) { SearchDirectory = @"D:\archives" };
        Assert.StartsWith("1. ", model.NextStepText, StringComparison.Ordinal);
        Assert.Equal("", model.SelectionSummaryText);
        Assert.True(await model.SearchAsync());
        Assert.StartsWith("2. ", model.NextStepText, StringComparison.Ordinal);
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        Assert.StartsWith("3. ", model.NextStepText, StringComparison.Ordinal);
        Assert.Contains("選択 2 / 2 Targets", model.SelectionSummaryText, StringComparison.Ordinal);
        Assert.Contains("未解析 2", model.SelectionSummaryText, StringComparison.Ordinal);

        runner.Handler = (job, _) => Task.FromResult(job.Archive.EndsWith("B.zip", StringComparison.Ordinal)
            ? Ok(job, ("m.txt", "MODIFIED", 1)) : Ok(job, ("a.txt", "MATCHED", 10), ("b.txt", "MATCHED", 20)));
        await model.AnalyzeSelectedAsync();
        Assert.StartsWith("4. ", model.NextStepText, StringComparison.Ordinal);
        // A has two candidates; B has none, so it counts as "other" (it will be excluded with a reason).
        Assert.Contains("削除できる 1（削除候補 2 ファイル・30 bytes）、未解析 0、その他 1", model.SelectionSummaryText, StringComparison.Ordinal);
        model.SelectTargets(false, visibleOnly: false);
        Assert.Contains("一括操作の対象がありません", model.NextStepText, StringComparison.Ordinal);
        Assert.Contains("選択 0 / 2 Targets", model.SelectionSummaryText, StringComparison.Ordinal);
        model.Archives[1].Targets[0].IsSelected = true;
        Assert.Contains("削除できるものがありません", model.NextStepText, StringComparison.Ordinal);
        // The guide changes nothing: the plan still decides by the same exclusions.
        var plan = model.PlanDeletion()!;
        Assert.Empty(plan.Items);
        Assert.Equal("削除候補が0件です。", Assert.Single(plan.Excluded).Reason);
    }

    [Fact]
    public async Task StateBadgesAndHintsDescribeTheTargetState()
    {
        var f = await DeletionPreparationTests.Fixture(@"D:\one");
        var target = f.Model.Archives[0].Targets[0];
        Assert.Equal("未解析", target.StateLabel);
        Assert.Equal(StateKind.Neutral, target.StateKind);
        Assert.Equal("", target.ActionHint);
        await f.Model.AnalyzeSelectedAsync();
        Assert.Equal("解析済み", target.StateLabel);
        Assert.Equal(StateKind.Ready, target.StateKind);
        Assert.Contains("削除候補 2 ファイル", target.RowSummaryText, StringComparison.Ordinal);
        Assert.Contains("解析済みのTargetは編集できません", target.ActionHint, StringComparison.Ordinal);
        f.Model.SelectTargets(false, false);
        target.IsSelected = true;
        await f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.Equal("削除実行済み", target.StateLabel);
        Assert.Equal(StateKind.Done, target.StateKind);
        Assert.Contains("直前の削除: 削除完了", target.RowSummaryText, StringComparison.Ordinal);
        Assert.Contains("再解析が正常に完了するまで削除できません", target.ActionHint, StringComparison.Ordinal);

        f.Runner.Handler = (job, _) => Task.FromResult(Failed(job));
        await f.Model.AnalyzeTargetAsync(f.Model.Archives[0], target);
        Assert.Equal("解析失敗", target.StateLabel);
        Assert.Equal(StateKind.Error, target.StateKind);
        Assert.Contains("解析失敗", target.RowSummaryText, StringComparison.Ordinal);
        Assert.Contains("直前の削除: 削除完了", target.RowSummaryText, StringComparison.Ordinal);
        Assert.Contains("削除するには再解析が必要です", target.ActionHint, StringComparison.Ordinal);
        Assert.Equal("1,023 bytes", SizeFormat.Short(1023));
        Assert.Equal("1 KiB", SizeFormat.Short(1024));
    }
}
