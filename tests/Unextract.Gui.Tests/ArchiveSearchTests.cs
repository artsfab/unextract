using System.Diagnostics;
using System.IO;
using Unextract.Gui.Models;
using Unextract.Gui.Services;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.Gui.Tests;

// Archive search on self-made fixtures: non-recursive and recursive, ZIP extensions, metadata, hidden/system ZIPs and directories,
// and real junctions that are not entered. The search never reads the contents of a ZIP. Through the internal adapter it injects an
// exception while listing, access denied, entries that vanish or fail metadata while walking, and a reparse swap before recursing;
// the remaining places are still searched. The Target existence check is tested apart for a Target created later and for an
// injected check failure. Avoiding paths by ordinary attribute checks is not a handle-safety guarantee under a hostile concurrent
// change.
public sealed class ArchiveSearchTests
{
    [Fact]
    public async Task RealSearchIncludesHiddenSystemZipAndSkipsJunctionWithoutOpeningZipContents()
    {
        string root = Fixture();
        string child = Directory.CreateDirectory(Path.Combine(root, "子 dir")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        string a = Path.Combine(root, "A.ZIP");
        string b = Path.Combine(child, "b.zip");
        string c = Path.Combine(destination, "c.zip");
        File.WriteAllText(a, "not ZIP contents; search does not inspect them");
        File.WriteAllText(b, "b");
        File.WriteAllText(c, "c");
        File.WriteAllText(Path.Combine(root, "ignore.7z"), "ignore");
        Directory.CreateDirectory(Path.Combine(root, "directory.zip"));
        File.SetAttributes(a, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(child, File.GetAttributes(child) | FileAttributes.Hidden | FileAttributes.System);
        string junction = Path.Combine(root, "junction");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", junction, destination }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await stdout + await stderr);

        var search = new ArchiveSearch();
        var shallow = await search.SearchAsync(root, false);
        Assert.Equal(a, Assert.Single(shallow.Archives).Path);
        var recursive = await search.SearchAsync(root, true);
        Assert.Equal(new[] { a, b, c }.Order(StringComparer.Ordinal), recursive.Archives.Select(f => f.Path));
        Assert.Contains(recursive.Diagnostics, d => d.Path == junction && d.Message.Contains("reparse", StringComparison.Ordinal));
        var metadata = recursive.Archives.Single(f => f.Path == a);
        Assert.Equal(new FileInfo(a).Length, metadata.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(a), metadata.LastWriteTimeUtc);
    }

    [Fact]
    public async Task PartialEnumerationFailureAndFileRacesDoNotStopOtherDirectories()
    {
        var fs = new FakeSearchFileSystem();
        fs.Directories[@"D:\root"] = [@"D:\root\denied", @"D:\root\ok", @"D:\root\vanished.zip", @"D:\root\bad.zip"];
        fs.Directories[@"D:\root\denied"] = [];
        fs.Directories[@"D:\root\ok"] = [@"D:\root\ok\z.ZiP", @"D:\root\ok\a.zip"];
        fs.EnumerationFailures[@"D:\root"] = new IOException("mid enumeration");
        fs.EnumerationFailures[@"D:\root\denied"] = new UnauthorizedAccessException("denied");
        fs.AttributeFailures[@"D:\root\vanished.zip"] = new FileNotFoundException("gone");
        fs.MetadataFailures[@"D:\root\bad.zip"] = new IOException("metadata failed");
        var result = await new ArchiveSearch(fs).SearchAsync(@"D:\root", true);
        Assert.Equal(new[] { @"D:\root\ok\a.zip", @"D:\root\ok\z.ZiP" }, result.Archives.Select(f => f.Path));
        Assert.Equal(4, result.Diagnostics.Count);
        Assert.Contains(result.Diagnostics, d => d.Path == @"D:\root\denied");
        Assert.Contains(result.Diagnostics, d => d.Path == @"D:\root\vanished.zip");
        Assert.Contains(result.Diagnostics, d => d.Path == @"D:\root\bad.zip");
    }

    // ZIP and RAR by extension only (case-insensitive; split volumes and unsupported RARs are listed too). Other extensions are ignored.
    [Fact]
    public async Task SearchFindsZipAndRarByExtensionOnly()
    {
        var fs = new FakeSearchFileSystem();
        fs.Directories[@"D:\root"] = [@"D:\root\a.rar", @"D:\root\B.RAR", @"D:\root\c.part2.rar", @"D:\root\d.zip", @"D:\root\e.cbr",
            @"D:\root\f.rar.txt", @"D:\root\g.7z", @"D:\root\h.Rar"];
        var result = await new ArchiveSearch(fs).SearchAsync(@"D:\root", true);
        Assert.Equal(new[] { @"D:\root\B.RAR", @"D:\root\a.rar", @"D:\root\c.part2.rar", @"D:\root\d.zip", @"D:\root\h.Rar" },
            result.Archives.Select(f => f.Path));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task RealSearchDoesNotOpenRarContents()
    {
        string root = Fixture();
        string rar = Path.Combine(root, "x.RAR");
        File.WriteAllText(rar, "not RAR contents; search does not inspect them");
        using var locked = new FileStream(rar, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await new ArchiveSearch().SearchAsync(root, false);
        Assert.Equal(rar, Assert.Single(result.Archives).Path);
        Assert.Equal(new FileInfo(rar).Length, result.Archives[0].Length);
    }

    [Fact]
    public async Task DirectoryReplacedWithReparseBeforeDescentIsNotEnumerated()
    {
        var fs = new FakeSearchFileSystem();
        fs.Directories[@"D:\root"] = [@"D:\root\changed"];
        fs.Directories[@"D:\root\changed"] = [@"D:\root\changed\a.zip"];
        int observations = 0;
        fs.CustomAttributes = path => path == @"D:\root\changed" && ++observations >= 2
            ? FileAttributes.Directory | FileAttributes.ReparsePoint : null;
        var result = await new ArchiveSearch(fs).SearchAsync(@"D:\root", true);
        Assert.Empty(result.Archives);
        Assert.DoesNotContain(@"D:\root\changed", fs.Enumerated);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task RootFailureIsDiagnosed()
    {
        var fs = new FakeSearchFileSystem();
        fs.AttributeFailures[@"D:\missing"] = new DirectoryNotFoundException("not found");
        var result = await new ArchiveSearch(fs).SearchAsync(@"D:\missing", true);
        Assert.Empty(result.Archives);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task TargetObservationDistinguishesMissingFromDenialAndCanSeeLaterCreation()
    {
        string root = Fixture();
        string later = Path.Combine(root, "later");
        var search = new ArchiveSearch();
        Assert.Equal(TargetPresence.Missing, (await search.ObserveTargetAsync(later)).Presence);
        Directory.CreateDirectory(later);
        Assert.Equal(TargetPresence.Present, (await search.ObserveTargetAsync(later)).Presence);
        string file = Path.Combine(root, "file");
        File.WriteAllText(file, "file");
        Assert.Equal(TargetPresence.Unknown, (await search.ObserveTargetAsync(file)).Presence);
        var fs = new FakeSearchFileSystem();
        fs.AttributeFailures[@"D:\denied"] = new UnauthorizedAccessException("denied");
        var observation = await new ArchiveSearch(fs).ObserveTargetAsync(@"D:\denied");
        Assert.Equal(TargetPresence.Unknown, observation.Presence);
        Assert.Contains("denied", observation.Detail, StringComparison.Ordinal);
    }

    // A new fixture of the running test (TestFixtures, deleted after the test) with a Japanese name and a space in the path.
    internal static string Fixture() => Directory.CreateDirectory(Path.Combine(TestFixtures.Create(), "日本語 空白")).FullName;

    private sealed class FakeSearchFileSystem : IArchiveSearchFileSystem
    {
        public Dictionary<string, string[]> Directories { get; } = [];
        public Dictionary<string, Exception> AttributeFailures { get; } = [];
        public Dictionary<string, Exception> MetadataFailures { get; } = [];
        public Dictionary<string, Exception> EnumerationFailures { get; } = [];
        public List<string> Enumerated { get; } = [];
        public Func<string, FileAttributes?>? CustomAttributes { get; set; }
        public FileAttributes GetAttributes(string path)
        {
            FileAttributes? attributes = CustomAttributes?.Invoke(path);
            if (attributes is { } value) return value;
            if (AttributeFailures.TryGetValue(path, out var failure)) throw failure;
            return Directories.ContainsKey(path) ? FileAttributes.Directory : FileAttributes.Normal;
        }
        public IEnumerable<string> Enumerate(string directory)
        {
            Enumerated.Add(directory);
            foreach (var path in Directories[directory]) yield return path;
            if (EnumerationFailures.TryGetValue(directory, out var failure)) throw failure;
        }
        public ArchiveFile ReadArchive(string path)
        {
            if (MetadataFailures.TryGetValue(path, out var failure)) throw failure;
            return new(path, 5, DateTime.UnixEpoch);
        }
    }
}
