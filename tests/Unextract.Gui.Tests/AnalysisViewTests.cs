using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Unextract.Gui.Models;
using Unextract.Gui.ViewModels;
using Unextract.Gui.Views;
using static Unextract.Gui.Tests.AnalysisQueueTests;
using static Unextract.Gui.Tests.SearchSessionTests;
using static Unextract.Gui.Tests.SearchViewTests;

namespace Unextract.Gui.Tests;

// The analysis view on a WPF tree on the STA: the result list in the details pane of the viewed Target (with a usable height in the
// window, virtualized, not nested in another list), class and path filters and the filter state kept per Target, the Fast warning,
// and progress and cancel shown only while analyzing, all without binding errors. On a real window it also checks closing: two close
// requests during a delete neither kill nor exit, show the waiting text, and close after the current Target without starting the
// next; a close request during an analysis closes after the cancellation. Response time and memory with many entries belong to the
// performance measurements.
// Tests that create WPF windows run serially in one collection. When xUnit ran the classes that create MainWindow in
// parallel, a test (in DeploymentTests) failed intermittently; with those classes in the "Wpf" collection the failure
// no longer reproduced (Gui.Tests then passed 15 consecutive runs). It was seen only under parallel test execution, not
// in the product. The mechanism inside WPF was not identified.
[Collection("Wpf")]
public sealed class AnalysisViewTests
{
    [Fact]
    public Task ResultListProgressAndFastWarningBindWithoutErrors() => OnSta(async () =>
    {
        var runner = new FakeRunner();
        var model = await Session(new FakeSearch(), runner);
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0, Width = 1100, Height = 760 };
        var trace = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        try
        {
            window.Show();
            await Layout(window);
            var warning = Assert.IsType<Border>(window.FindName("FastWarning"));
            var warningText = Assert.IsType<TextBlock>(window.FindName("FastWarningText"));
            Assert.NotEqual(Visibility.Visible, warning.Visibility);
            Assert.True(model.SetMode(CliMode.Fast));
            await Layout(window);
            Assert.Equal(Visibility.Visible, warning.Visibility);
            Assert.Contains("内容の一致を確認しません", warningText.Text, StringComparison.Ordinal);
            Assert.False(((RadioButton)window.FindName("StrictRadio")).IsChecked);
            Assert.True(((RadioButton)window.FindName("FastRadio")).IsChecked);
            Assert.True(model.SetMode(CliMode.Strict));

            runner.Handler = (job, _) => Task.FromResult(Ok(job, ("a/顔.txt", "MATCHED", 10), ("a/b.txt", "MODIFIED", 3), ("a/", "DIRECTORY", 0)));
            Assert.Equal(2, (await model.AnalyzeSelectedAsync()).Succeeded);
            var list = Assert.IsType<ListBox>(window.FindName("ArchiveList"));
            list.SelectedItem = model.Archives[0].Targets[0];
            await Layout(window);
            Assert.Same(model.Archives[0].Targets[0], model.Viewed);
            var detail = Assert.IsType<ContentControl>(window.FindName("DetailPanel"));
            var results = Descendants<ListBox>(detail).Single(l => Id(l) == "ResultList");
            Assert.Equal(3, results.Items.Count);
            Assert.True(VirtualizingStackPanel.GetIsVirtualizing(results));
            // The entry list is shown with a usable height inside the window, not inside another scrolling list.
            var top = results.TranslatePoint(new Point(0, 0), window);
            Assert.True(results.ActualHeight >= 100, $"result list height {results.ActualHeight}");
            Assert.True(top.Y + results.ActualHeight <= ((FrameworkElement)window.Content).ActualHeight + 0.5, $"result list ends at {top.Y + results.ActualHeight}");
            Assert.DoesNotContain(Descendants<ListBox>(list), l => l != list);
            Assert.Contains(Descendants<TextBlock>(results), t => t.Text == "MATCHED");
            Assert.Contains(Descendants<ComboBox>(detail), c => c.Items.Count == 6);
            // Filtering through the bound controls leaves the Target selection alone.
            var filter = Descendants<TextBox>(detail).Single(t => Id(t) == "ResultPathFilterBox");
            filter.Text = "b.txt";
            filter.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            await Layout(window);
            Assert.Single(results.Items);
            Assert.True(model.Archives[0].Targets[0].IsSelected);
            Assert.StartsWith("解析済み (Strict): Matched 1", Descendants<TextBlock>(detail).Single(t => Id(t) == "TargetStateText").Text, StringComparison.Ordinal);
            // Viewing the other Target and coming back keeps each Target's own filter.
            list.SelectedItem = model.Archives[1].Targets[0];
            await Layout(window);
            Assert.Equal(3, Descendants<ListBox>(detail).Single(l => Id(l) == "ResultList").Items.Count);
            list.SelectedItem = model.Archives[0].Targets[0];
            await Layout(window);
            Assert.Single(Descendants<ListBox>(detail).Single(l => Id(l) == "ResultList").Items);
            Assert.Empty(trace.Errors);
        }
        finally
        {
            window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
        }
    });

    [Fact]
    public Task ProgressPanelAndCancelAreVisibleOnlyWhileAnalyzing() => OnSta(async () =>
    {
        var runner = new FakeRunner();
        var model = await Session(new FakeSearch(), runner);
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = (_, _) => gate.Task;
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            await Layout(window);
            var cancel = Assert.IsType<Button>(window.FindName("CancelAnalysisButton"));
            var panel = Assert.IsType<Border>(window.FindName("ProgressPanel"));
            Assert.NotEqual(Visibility.Visible, panel.Visibility);
            var running = model.AnalyzeSelectedAsync();
            await Layout(window);
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal(Visibility.Visible, cancel.Visibility);
            Assert.Equal("1 / 2 Targets", ((TextBlock)window.FindName("OverallProgressText")).Text);
            Assert.False(((Button)window.FindName("AnalyzeSelectedButton")).IsEnabled);
            Assert.Contains("解析中です", ((TextBlock)window.FindName("NextStepText")).Text, StringComparison.Ordinal);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            gate.SetResult(Cancelled(runner.Jobs[0]));
            await running;
            await Layout(window);
            Assert.NotEqual(Visibility.Visible, panel.Visibility);
            Assert.True(((Button)window.FindName("AnalyzeSelectedButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ClosingDuringDeleteWaitsForTheCurrentTargetThenClosesAndStartsNothingElse() => OnSta(async () =>
    {
        var f = await DeletionPreparationTests.Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runner.Handler = (job, _) => job.Operation == CliOperation.Delete ? gate.Task : Task.FromResult(Ok(job, ("a.txt", "MATCHED", 1)));
        var window = new MainWindow { DataContext = f.Model, ShowInTaskbar = false, Opacity = 0 };
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        await Layout(window);
        var deleting = f.Model.DeleteAsync(f.Model.PlanDeletion()!, approved: true);
        Assert.True(f.Model.IsDeleting);
        window.Close();
        window.Close();
        await Layout(window);
        // Neither request closes it or touches the process; the wait is announced.
        Assert.False(closed);
        Assert.True(window.IsVisible);
        Assert.Contains("現在のTargetの処理が終わり次第終了します", f.Model.Status, StringComparison.Ordinal);
        Assert.False(gate.Task.IsCompleted);
        gate.SetResult(DeletionPreparationTests.DeleteOk(f.Runner.Jobs[^1]));
        var result = await deleting;
        Assert.Equal(DeletionStop.ExitRequested, result.Stop);
        for (int i = 0; i < 200 && !closed; i++) await Task.Delay(10);
        Assert.True(closed);
        Assert.Single(f.Runner.Jobs, j => j.Operation == CliOperation.Delete);
        Assert.True(f.Model.Archives[0].Targets[0].HasDeleteStarted);
        Assert.False(f.Model.Archives[1].Targets[0].HasDeleteStarted);
    });

    [Fact]
    public Task ClosingDuringAnalysisCancelsItAndClosesAfterTheQueueEnds() => OnSta(async () =>
    {
        var runner = new FakeRunner();
        var model = await Session(new FakeSearch(), runner);
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        var gate = new TaskCompletionSource<CliJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = (job, token) =>
        {
            token.Register(() => gate.TrySetResult(Cancelled(job)));
            return gate.Task;
        };
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0 };
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var running = model.AnalyzeSelectedAsync();
        window.Close();
        var result = await running;
        Assert.True(result.Cancelled);
        for (int i = 0; i < 200 && !closed; i++) await Task.Delay(10);
        Assert.True(closed);
    });
}
