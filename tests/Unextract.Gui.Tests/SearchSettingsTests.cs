using System.IO;
using System.Text.Json;
using Unextract.Gui.Services;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.Gui.Tests;

// Search settings with self-made settings files and sharing denials: only the last entry is saved, defaults after a broken or
// unreadable file, and a save failure is reported with its path. The user's settings file and ACLs are never changed.
public sealed class SearchSettingsTests
{
    [Fact]
    public async Task StoresOnlyLastDirectoryAndNeverSessionData()
    {
        string path = Path.Combine(ArchiveSearchTests.Fixture(), "settings.json");
        var settings = new SearchSettings(path);
        Assert.Null((await settings.LoadAsync()).Directory);
        Assert.Null((await settings.SaveAsync(@"D:\first")).Error);
        Assert.Null((await new SearchSettings(path).SaveAsync(@"E:\日本語 空白")).Error);
        Assert.Equal(@"E:\日本語 空白", (await settings.LoadAsync()).Directory);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("search_directory", Assert.Single(json.RootElement.EnumerateObject()).Name);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"search_directory\":42}")]
    [InlineData("{\"search_directory\":\"relative\"}")]
    public async Task CorruptOrInvalidSettingsStartWithDefault(string content)
    {
        string path = Path.Combine(ArchiveSearchTests.Fixture(), "settings.json");
        File.WriteAllText(path, content);
        Assert.Null((await new SearchSettings(path).LoadAsync()).Directory);
    }

    [Fact]
    public async Task ReadFailureUsesDefaultAndSaveFailureIsReportedWithPath()
    {
        string path = Path.Combine(ArchiveSearchTests.Fixture(), "settings.json");
        File.WriteAllText(path, "{}");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var settings = new SearchSettings(path);
        Assert.Null((await settings.LoadAsync()).Directory);
        Assert.Contains(path, (await settings.SaveAsync(@"D:\new")).Error, StringComparison.Ordinal);
    }
}
