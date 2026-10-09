using Unextract.Gui.Models;
using Unextract.Gui.Services;
using Unextract.Gui.ViewModels;

namespace Unextract.Gui.Tests;

// The search session: the targets of a bulk add, per-item registration errors and skipped duplicates, the tri-state parent, filter
// and viewing independent of selection, select all / select shown, editing settings and removing models, the discard confirmation
// of a new search, the operation lock, re-evaluating existence, and the notice of a save failure.
public sealed class SearchSessionTests
{
    [Fact]
    public async Task FilterDoesNotChangeSelectionAndBulkAdditionUsesAllSearchResults()
    {
        var model = await Session();
        Assert.True(model.Recursive);
        Assert.Null(model.Viewed);
        Assert.All(model.Archives, a => Assert.False(a.IsParentEnabled));
        Assert.Equal(new[] { @"D:\archives\A.zip", @"D:\archives\B.zip" }, model.Archives.Select(a => a.Path));
        model.ArchiveFilter = "a.ZIP";
        Assert.Single(model.VisibleArchives);
        var added = await model.AddTargetsAsync(TargetTemplate.ArchiveDirectory);
        Assert.Equal(2, added.Added);
        Assert.All(model.Archives, a => Assert.True(Assert.Single(a.Targets).IsSelected));
        model.SelectTargets(false, visibleOnly: true);
        Assert.False(model.Archives[0].Targets[0].IsSelected);
        Assert.True(model.Archives[1].Targets[0].IsSelected);
        model.ArchiveFilter = "";
        Assert.False(model.Archives[0].Targets[0].IsSelected);
        model.SelectTargets(false, visibleOnly: false);
        Assert.All(model.Archives, a => Assert.False(a.Targets[0].IsSelected));
        model.ArchiveFilter = "B.zip";
        model.SelectTargets(true, visibleOnly: false);
        Assert.All(model.Archives, a => Assert.True(a.Targets[0].IsSelected));
        model.SelectTargets(false, visibleOnly: false);
        model.SelectTargets(true, visibleOnly: true);
        Assert.False(model.Archives[0].Targets[0].IsSelected);
        Assert.True(model.Archives[1].Targets[0].IsSelected);
    }

    [Fact]
    public async Task ParentHasThreeStatesAndViewingNeverRestoresOldSelection()
    {
        var model = await Session();
        var archive = model.Archives[0];
        Assert.Equal(false, archive.IsSelected);
        archive.IsSelected = true;
        Assert.Equal(false, archive.IsSelected);
        await model.AddTargetsAsync(TargetTemplate.SameDirectory, archive);
        await model.AddTargetsAsync(TargetTemplate.ArchiveDirectory, archive);
        Assert.Equal(true, archive.IsSelected);
        archive.Targets[0].IsSelected = false;
        Assert.Null(archive.IsSelected);
        model.View(archive);
        model.View(archive.Targets[0]);
        model.View(null);
        Assert.Null(archive.IsSelected);
        archive.IsSelected = false;
        Assert.All(archive.Targets, t => Assert.False(t.IsSelected));
        archive.IsSelected = true;
        Assert.All(archive.Targets, t => Assert.True(t.IsSelected));
        model.RemoveTarget(archive, archive.Targets[0]);
        model.RemoveTarget(archive, archive.Targets[0]);
        Assert.False(archive.IsParentEnabled);
        Assert.Equal(0, archive.TargetCount);
    }

    [Fact]
    public async Task DuplicateOnlyIsSkippedAndOtherArchivesAndParentChildTargetsAreAllowed()
    {
        var model = await Session();
        await model.AddTargetsAsync(@"D:\Work");
        var duplicates = await model.AddTargetsAsync("d:/work///");
        Assert.Equal(0, duplicates.Added);
        Assert.Equal(2, duplicates.Duplicates);
        Assert.Empty(duplicates.Errors);
        var child = await model.AddTargetsAsync(@"D:\Work\child");
        Assert.Equal(2, child.Added);
        Assert.All(model.Archives, a => Assert.Equal(2, a.TargetCount));
        Assert.Equal(@"D:\Work", model.Archives[0].Targets[0].Path);
    }

    [Fact]
    public async Task BulkReportsIndividualResolutionErrorsAndKeepsOtherAdditions()
    {
        var search = new FakeSearch { Result = new([File(@"D:\archives\.zip"), File(@"D:\archives\ok.zip")], []) };
        var model = await Session(search);
        model.ArchiveFilter = ".zip";
        var result = await model.AddTargetsAsync(TargetTemplate.ArchiveDirectory);
        Assert.Equal(1, result.Added);
        Assert.Equal(@"D:\archives\.zip", Assert.Single(result.Errors).Path);
        Assert.Equal(0, model.Archives[0].TargetCount);
        Assert.Equal(1, model.Archives[1].TargetCount);
        Assert.Single(model.Diagnostics);
    }

    [Fact]
    public async Task EditPreservesSelectionAndChecksDuplicatesAndRemovalIsOnlyModelRemoval()
    {
        var model = await Session();
        var archive = model.Archives[0];
        await model.AddTargetsAsync(TargetTemplate.SameDirectory, archive);
        await model.AddTargetsAsync(TargetTemplate.ArchiveDirectory, archive);
        var target = archive.Targets[0];
        target.IsSelected = false;
        Assert.False(await model.EditTargetAsync(archive, target, archive.Targets[1].Path));
        Assert.Equal(@"D:\archives", target.Path);
        Assert.False(await model.EditTargetAsync(archive, target, @"D:\wrong\..\work"));
        Assert.True(await model.EditTargetAsync(archive, target, @"E:\elsewhere"));
        Assert.Equal(@"E:\elsewhere", target.Path);
        Assert.False(target.IsSelected);
        Assert.Equal(TargetPresence.Present, target.Observation.Presence);
        Assert.True(model.RemoveTarget(archive, target));
        Assert.Single(archive.Targets);
        Assert.False(model.RemoveTarget(archive, target));
    }

    [Fact]
    public async Task MissingTargetCanBeRefreshedAfterCreationAndUnknownIsNotBlocked()
    {
        var search = new FakeSearch { Observation = new(TargetPresence.Missing) };
        var model = await Session(search);
        await model.AddTargetsAsync(@"D:\later", model.Archives[0]);
        var target = model.Archives[0].Targets[0];
        Assert.False(target.CanAnalyze);
        Assert.Contains("存在しません", target.PresenceText, StringComparison.Ordinal);
        Assert.False(await model.RefreshTargetAsync(target));
        search.Observation = new(TargetPresence.Present);
        Assert.True(await model.RefreshTargetAsync(target));
        Assert.True(target.CanAnalyze);
        search.Observation = new(TargetPresence.Unknown, "denied");
        Assert.True(await model.RefreshTargetAsync(target));
        Assert.True(target.CanAnalyze);
        Assert.Contains("確認不能", target.PresenceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReSearchCannotDiscardSessionWithoutConfirmation()
    {
        var search = new FakeSearch();
        var model = await Session(search);
        await model.AddTargetsAsync(@"D:\work");
        var original = model.Archives;
        model.Archives[0].Targets[0].IsSelected = false;
        Assert.False(await model.SearchAsync());
        Assert.Same(original, model.Archives);
        Assert.Equal(1, search.Searches);
        Assert.False(model.Archives[0].Targets[0].IsSelected);
        Assert.True(await model.SearchAsync(discardApproved: true));
        Assert.Equal(2, search.Searches);
        Assert.NotSame(original, model.Archives);
        Assert.All(model.Archives, a => Assert.Empty(a.Targets));
    }

    [Fact]
    public async Task EmptySearchIsAlsoASessionThatNeedsConfirmation()
    {
        var search = new FakeSearch { Result = new([], []) };
        var model = await Session(search);
        Assert.True(model.HasSession);
        Assert.False(await model.SearchAsync());
        Assert.Equal(1, search.Searches);
    }

    [Fact]
    public async Task RegistrationLocksSettingsAndSelectionButAllowsFilteringAndViewing()
    {
        var search = new FakeSearch();
        var model = await Session(search);
        await model.AddTargetsAsync(@"D:\work");
        search.ObservationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var adding = model.AddTargetsAsync(@"D:\other");
        Assert.True(model.IsBusy);
        Assert.False(model.Archives[0].IsParentEnabled);
        Assert.False(model.Archives[0].Targets[0].CanEdit);
        model.Archives[0].Targets[0].IsSelected = false;
        model.SelectTargets(false, false);
        Assert.True(model.Archives[0].Targets[0].IsSelected);
        model.SearchDirectory = @"E:\changed";
        model.Recursive = false;
        Assert.Equal(@"D:\archives", model.SearchDirectory);
        Assert.True(model.Recursive);
        Assert.False(await model.SearchAsync(true));
        Assert.Equal(0, (await model.AddTargetsAsync(@"D:\concurrent")).Added);
        Assert.False(model.RemoveTarget(model.Archives[0], model.Archives[0].Targets[0]));
        model.ArchiveFilter = "B.zip";
        model.View(model.Archives[0].Targets[0]);
        Assert.Single(model.VisibleArchives);
        Assert.Same(model.Archives[0].Targets[0], model.Viewed);
        Assert.True(model.Archives[0].Targets[0].IsSelected);
        search.ObservationGate.SetResult(new(TargetPresence.Present));
        await adding;
        Assert.False(model.IsBusy);
        Assert.All(model.Archives, a => Assert.Equal(2, a.TargetCount));
    }

    [Fact]
    public async Task InitializationAndSaveFailureOnlyAffectSettingsAndNotifications()
    {
        var settings = new FakeSettings { Directory = @"E:\last", Error = "save denied" };
        var model = new MainViewModel(new("missing", false, "missing"), new FakeSearch(), settings);
        await model.InitializeAsync();
        Assert.Equal(@"E:\last", model.SearchDirectory);
        Assert.True(await model.SearchAsync());
        Assert.Equal(@"E:\last", settings.Saved);
        Assert.Contains("save denied", Assert.Single(model.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Equal(2, model.Archives.Count);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("")]
    [InlineData("D:/invalid\u0000path")]
    public async Task InvalidSearchPathDoesNotStartSearchOrSave(string path)
    {
        var search = new FakeSearch();
        var settings = new FakeSettings();
        var model = new MainViewModel(new("cli", true, "available"), search, settings) { SearchDirectory = path };
        Assert.False(await model.SearchAsync());
        Assert.Equal(0, search.Searches);
        Assert.Null(settings.Saved);
    }

    internal static async Task<MainViewModel> Session(FakeSearch? search = null)
    {
        var model = new MainViewModel(new("cli", true, "available"), search ?? new FakeSearch(), new FakeSettings())
            { SearchDirectory = @"D:\archives" };
        Assert.True(await model.SearchAsync());
        return model;
    }
    internal static async Task<MainViewModel> Session(FakeSearch search, ICliProcessRunner runner)
    {
        var model = new MainViewModel(new("cli", true, "available"), search, new FakeSettings(), runner)
            { SearchDirectory = @"D:\archives" };
        Assert.True(await model.SearchAsync());
        return model;
    }
    private static ArchiveFile File(string path) => new(path, 3, DateTime.UnixEpoch);

    internal sealed class FakeSearch : IArchiveSearch
    {
        public ArchiveSearchResult Result { get; set; } = new([File(@"D:\archives\B.zip"), File(@"D:\archives\A.zip")], []);
        public TargetObservation Observation { get; set; } = new(TargetPresence.Present);
        public TaskCompletionSource<TargetObservation>? ObservationGate { get; set; }
        public int Searches { get; private set; }
        public Task<ArchiveSearchResult> SearchAsync(string directory, bool recursive) { Searches++; return Task.FromResult(Result); }
        public Task<TargetObservation> ObserveTargetAsync(string path) => ObservationGate?.Task ?? Task.FromResult(Observation);
    }
    internal sealed class FakeSettings : ISearchSettings
    {
        public string? Directory { get; set; }
        public string? Error { get; set; }
        public string? Saved { get; private set; }
        public Task<SearchSettingsLoad> LoadAsync() => Task.FromResult(new SearchSettingsLoad(Directory));
        public Task<SearchSettingsSave> SaveAsync(string directory) { Saved = directory; return Task.FromResult(new SearchSettingsSave(Error)); }
    }
}
