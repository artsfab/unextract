using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Unextract.Gui.Models;
using Unextract.Gui.ViewModels;
using Unextract.Gui.Views;

namespace Unextract.Gui.Tests;

// Tests that create WPF windows run serially in one collection. When xUnit ran the classes that create MainWindow in
// parallel, a test (in DeploymentTests) failed intermittently; with those classes in the "Wpf" collection the failure
// no longer reproduced (Gui.Tests then passed 15 consecutive runs). It was seen only under parallel test execution, not
// in the product. The mechanism inside WPF was not identified.
[Collection("Wpf")]
public sealed class SearchViewTests
{
    [Fact]
    public Task WorkListShowsArchivesWithTheirTargetsFlatAndKeepsHiddenSelections() => OnSta(async () =>
    {
        var model = await SearchSessionTests.Session();
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        await model.AddTargetsAsync(TargetTemplate.ArchiveDirectory);
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0 };
        var trace = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        try
        {
            window.Show();
            await Layout(window);
            var list = Assert.IsType<ListBox>(window.FindName("ArchiveList"));
            // One flat, virtualized list: each archive row followed by its Target rows; no list nested inside a row.
            Assert.Equal(6, list.Items.Count);
            Assert.Same(model.Archives[0], list.Items[0]);
            Assert.Same(model.Archives[0].Targets[1], list.Items[2]);
            Assert.Same(model.Archives[1], list.Items[3]);
            Assert.True(VirtualizingStackPanel.GetIsVirtualizing(list));
            Assert.DoesNotContain(Descendants<ListBox>(list), l => l != list);
            Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(list));

            var parent = Descendants<CheckBox>(Container(list, 0)).Single(c => Id(c) == "ArchiveCheckBox");
            Assert.True(parent.IsChecked);
            model.Archives[0].Targets[0].IsSelected = false;
            await Layout(window);
            Assert.Null(parent.IsChecked);
            parent.IsChecked = false;
            parent.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
            Assert.All(model.Archives[0].Targets, t => Assert.False(t.IsSelected));
            parent.IsChecked = true;
            parent.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
            Assert.All(model.Archives[0].Targets, t => Assert.True(t.IsSelected));

            var filter = Assert.IsType<TextBox>(window.FindName("ArchiveFilterBox"));
            filter.Text = "A.zip";
            filter.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            await Layout(window);
            Assert.Equal(3, list.Items.Count);
            Click(window, "ClearVisibleButton");
            Assert.All(model.Archives[0].Targets, t => Assert.False(t.IsSelected));
            Assert.All(model.Archives[1].Targets, t => Assert.True(t.IsSelected));
            filter.Text = "";
            await Layout(window);
            Assert.Equal(6, list.Items.Count);
            Assert.All(model.Archives[0].Targets, t => Assert.False(t.IsSelected));
            Assert.Empty(trace.Errors);
            Render(window, Path.Combine(ArchiveSearchTests.Fixture("view"), "work-list.png"));
        }
        finally
        {
            window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
        }
    });

    [Fact]
    public Task SpaceTogglesTheRowSelectionWhileArrowsOnlyChangeTheViewedItem() => OnSta(async () =>
    {
        var model = await SearchSessionTests.Session();
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            await Layout(window);
            var list = Assert.IsType<ListBox>(window.FindName("ArchiveList"));
            list.SelectedIndex = 1;
            await Layout(window);
            Assert.Same(model.Archives[0].Targets[0], model.Viewed);
            Assert.True(model.Archives[0].Targets[0].IsSelected);
            var row = Container(list, 1);
            Assert.True(row.Focus());
            PressKey(row, Key.Space);
            Assert.False(model.Archives[0].Targets[0].IsSelected);
            Assert.Same(model.Archives[0].Targets[0], model.Viewed);
            PressKey(row, Key.Space);
            Assert.True(model.Archives[0].Targets[0].IsSelected);
            // Space on an archive row sets all of its Targets; viewing never changes the selection.
            var archiveRow = Container(list, 0);
            Assert.True(archiveRow.Focus());
            PressKey(archiveRow, Key.Space);
            Assert.Equal(false, model.Archives[0].IsSelected);
            list.SelectedIndex = 3;
            list.SelectedIndex = 2;
            await Layout(window);
            Assert.Same(model.Archives[1], model.Viewed);
            Assert.Equal(false, model.Archives[0].IsSelected);
            Assert.Equal(true, model.Archives[1].IsSelected);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task TabOrderFollowsTheVisualOrderAndTheModeIsOneStop() => OnSta(async () =>
    {
        var model = await SearchSessionTests.Session();
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0 };
        var trace = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        try
        {
            window.Show();
            await Layout(window);
            var input = Assert.IsType<TextBox>(window.FindName("SearchDirectoryBox"));
            var search = Assert.IsType<Button>(window.FindName("SearchButton"));
            var choose = Assert.IsType<Button>(window.FindName("ChooseFolderButton"));
            double Left(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), window).X;
            Assert.True(Left(input) < Left(search) && Left(search) < Left(choose));
            Assert.Equal(search.TranslatePoint(new Point(0, 0), window).Y, choose.TranslatePoint(new Point(0, 0), window).Y, 0.5);
            Assert.All(new Control[] { input, search, choose }, c => Assert.Equal(int.MaxValue, KeyboardNavigation.GetTabIndex(c)));
            Assert.True(input.Focus());
            await Layout(window);
            var order = new List<object?>();
            for (int i = 0; i < 7; i++)
            {
                Assert.True(((UIElement)FocusManager.GetFocusedElement(window)).MoveFocus(new(FocusNavigationDirection.Next)));
                order.Add(FocusManager.GetFocusedElement(window));
            }
            Assert.Same(search, order[0]);
            Assert.Same(choose, order[1]);
            Assert.Same(window.FindName("RecursiveCheckBox"), order[2]);
            // Both mode radio buttons together are a single stop, on the selected mode.
            Assert.Same(window.FindName("StrictRadio"), order[3]);
            Assert.Same(window.FindName("ArchiveFilterBox"), order[4]);
            Assert.Same(window.FindName("BulkAddButton"), order[5]);
            Assert.Same(window.FindName("SelectionMenuButton"), order[6]);
            Assert.Empty(trace.Errors);
        }
        finally
        {
            window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
        }
    });

    [Fact]
    public Task AutomationNamesAndIdsAreSetOnTheElementsTheUiTestsDrive() => OnSta(async () =>
    {
        var model = await SearchSessionTests.Session();
        await model.AddTargetsAsync(TargetTemplate.SameDirectory);
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            await Layout(window);
            string? Name(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetName(d);
            var list = Assert.IsType<ListBox>(window.FindName("ArchiveList"));
            var archiveItem = Container(list, 0);
            Assert.Equal(model.Archives[0].DisplayName, Name(archiveItem));
            var archiveCheck = Descendants<CheckBox>(archiveItem).Single();
            Assert.Equal("ArchiveCheckBox", Id(archiveCheck));
            Assert.Equal(model.Archives[0].DisplayName, Name(archiveCheck));
            Assert.False(archiveCheck.Focusable);
            var targetItem = Container(list, 1);
            Assert.Equal(model.Archives[0].Targets[0].DisplayPath, Name(targetItem));
            var targetCheck = Descendants<CheckBox>(targetItem).Single(c => Id(c) == "TargetCheckBox");
            Assert.Equal(model.Archives[0].Targets[0].DisplayPath, Name(targetCheck));
            foreach (string id in new[] { "ArchivePathText", "ArchiveTargetCountText" })
                Assert.Single(Descendants<FrameworkElement>(archiveItem), e => Id(e) == id);
            foreach (string id in new[] { "TargetRowStateText", "TargetRowPathText" })
                Assert.Single(Descendants<FrameworkElement>(targetItem), e => Id(e) == id);
            foreach (string name in new[] { "SearchDirectoryBox", "SearchButton", "ChooseFolderButton", "RecursiveCheckBox", "StrictRadio",
                "FastRadio", "FastWarning", "FastWarningText", "BulkAddButton", "SelectionMenuButton", "SelectAllButton", "ClearAllButton", "SelectVisibleButton",
                "ClearVisibleButton", "ArchiveFilterBox", "ArchiveCountText", "OpenLogFolderButton", "CancelAnalysisButton", "AnalyzeSelectedButton",
                "DeleteSelectedButton", "ArchiveList", "DetailPanel", "NextStepText", "SelectionSummaryText", "StatusText",
                "CliStatusText", "DiagnosticsToggle", "DiagnosticsList", "ProgressPanel", "OverallProgressText", "CurrentArchiveText", "CurrentTargetText",
                "EntryProgressText" })
                Assert.NotNull(window.FindName(name));

            model.View(model.Archives[0]);
            await Layout(window);
            var detail = Assert.IsType<ContentControl>(window.FindName("DetailPanel"));
            foreach (string id in new[] { "ArchiveDetailPathText", "AddTargetButton", "AnalyzeArchiveButton" })
                Assert.Single(Descendants<FrameworkElement>(detail), e => Id(e) == id);
            model.View(model.Archives[0].Targets[0]);
            await Layout(window);
            foreach (string id in new[] { "TargetPathText", "TargetArchiveText", "TargetTemplateText", "TargetPresenceText", "TargetStateText",
                "AnalyzeTargetButton", "EditTargetButton", "RefreshTargetButton", "RemoveTargetButton" })
                Assert.Single(Descendants<FrameworkElement>(detail), e => Id(e) == id);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task DialogsExposeAutomationIds() => OnSta(async () =>
    {
        var f = await DeletionPreparationTests.Fixture(@"D:\one");
        await f.Model.AnalyzeSelectedAsync();
        var plan = f.Model.PlanDeletion()!;
        var confirm = new DeletionConfirmWindow(plan, f.Model.ConfirmationListText(plan)) { ShowInTaskbar = false, Opacity = 0 };
        var editor = TargetEditorWindow.ForAdd(@"D:\archives\a.zip");
        editor.ShowInTaskbar = false;
        editor.Opacity = 0;
        try
        {
            confirm.Show();
            editor.Show();
            await Layout(confirm);
            await Layout(editor);
            string[] Ids(Window w) => Descendants<FrameworkElement>(w).Select(Id).OfType<string>()
                .Where(s => s.Length > 0 && !s.EndsWith("ScrollBar", StringComparison.Ordinal)).Distinct().Order().ToArray();
            Assert.Equal(new[] { "DeletionConfirmBody", "DeletionConfirmCancelButton", "DeletionConfirmDeleteButton", "DeletionConfirmExcludedCount",
                "DeletionConfirmFastWarning", "DeletionConfirmFileCount", "DeletionConfirmHeading", "DeletionConfirmHidden", "DeletionConfirmIrreversible",
                "DeletionConfirmMode", "DeletionConfirmSize", "DeletionConfirmTargetCount" }, Ids(confirm));
            Assert.Equal(new[] { "TargetEditorCancelButton", "TargetEditorHeading", "TargetEditorOkButton", "TargetEditorPresetCombo", "TargetEditorPreview",
                "TargetEditorScope", "TargetEditorTemplateBox" }, Ids(editor));
        }
        finally { confirm.Close(); editor.Close(); }
    });

    [Fact]
    public Task TargetEditorShowsResolvedPathAndDisablesInvalidTemplate() => OnSta(async () =>
    {
        var editor = TargetEditorWindow.ForEdit(@"D:\archives\a.b.zip", TargetTemplate.ArchiveDirectory);
        editor.ShowInTaskbar = false;
        editor.Opacity = 0;
        try
        {
            editor.Show();
            await Layout(editor);
            var input = Descendants<TextBox>(editor).Single(t => Id(t) == "TargetEditorTemplateBox");
            var presets = Descendants<ComboBox>(editor).Single();
            var register = Descendants<Button>(editor).Single(b => Id(b) == "TargetEditorOkButton");
            var preview = Descendants<TextBlock>(editor).Single(t => Id(t) == "TargetEditorPreview");
            Assert.Equal(1, presets.SelectedIndex);
            Assert.True(register.IsEnabled);
            Assert.Equal(@"解決先: D:\archives\a.b", preview.Text);
            input.Text = @"D:\bad\..\path";
            Assert.False(register.IsEnabled);
            Assert.Equal(2, presets.SelectedIndex);
            input.Text = @"E:\Work\{{archive.name}}";
            Assert.True(register.IsEnabled);
            Assert.Equal(input.Text, editor.TemplateText);
            presets.SelectedIndex = 0;
            Assert.Equal(TargetTemplate.SameDirectory, input.Text);
            Assert.Equal(@"解決先: D:\archives", preview.Text);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public Task BulkEditorResolvesEveryArchiveAndOffersTheRuleWhenAnyArchiveResolves() => OnSta(async () =>
    {
        // ".zip" has an empty name: {{archive.name}} cannot be resolved for it, the other archive still can.
        var editor = TargetEditorWindow.ForBulk([@"D:\archives\a.zip", @"D:\archives\.zip"]);
        editor.ShowInTaskbar = false;
        editor.Opacity = 0;
        try
        {
            editor.Show();
            await Layout(editor);
            var input = Descendants<TextBox>(editor).Single(t => Id(t) == "TargetEditorTemplateBox");
            var register = Descendants<Button>(editor).Single(b => Id(b) == "TargetEditorOkButton");
            var preview = Descendants<TextBlock>(editor).Single(t => Id(t) == "TargetEditorPreview");
            Assert.Contains("形式が正しいArchive: 2 / 2 件", preview.Text, StringComparison.Ordinal);
            input.Text = TargetTemplate.ArchiveDirectory;
            Assert.True(register.IsEnabled);
            Assert.Contains(@"例: a.zip → D:\archives\a", preview.Text, StringComparison.Ordinal);
            Assert.Contains("形式が正しいArchive: 1 / 2 件（登録エラーになるArchive 1 件", preview.Text, StringComparison.Ordinal);
            input.Text = "{{unknown}}";
            Assert.False(register.IsEnabled);
        }
        finally { editor.Close(); }
    });

    internal static string? Id(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetAutomationId(d);

    internal static ListBoxItem Container(ListBox list, int index) =>
        Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(index));

    internal static void Click(Window window, string name)
    {
        if (window.FindName(name) is MenuItem item) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        else Assert.IsType<Button>(window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal static void PressKey(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)!;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    // Own WPF tree, not the user's desktop. The picture is kept with the test fixture.
    internal static void Render(Window window, string path)
    {
        // The root of the window's template covers the whole client area at offset 0, with the window background.
        var root = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetChild(window, 0));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(file);
    }

    // Several idle passes: scroll bars and virtualized rows settle over more than one layout pass.
    internal static async Task Layout(Window window)
    {
        for (int i = 0; i < 3; i++) await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    internal static Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception e) { done.TrySetException(e); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    internal sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (message?.Contains("Error:", StringComparison.Ordinal) == true) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
