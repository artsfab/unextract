using System.IO;
using System.IO.Compression;
using Unextract.Gui.Models;
using Unextract.Gui.Services;
using Unextract.Gui.ViewModels;
using static Unextract.Gui.Tests.SearchSessionTests;

namespace Unextract.Gui.Tests;

// GUI consumer integration with the bundled real CLI. Every ZIP, target, temp and log directory is a
// self-made GUID fixture under the test output; nothing outside it is a target. Fixtures are kept.
public sealed class IntegrationTests
{
    private static readonly byte[] Bytes = [1, 2, 3];
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private sealed record Real(MainViewModel Model, string Root, string Logs, string Temp, string Archive);

    private static void Zip(string archive, params (string Name, byte[] Content)[] entries)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(content);
        }
    }

    private static async Task<Real> Open(string kind, params (string Name, byte[] Content)[] entries)
    {
        string root = ArchiveSearchTests.Fixture(kind);
        string archive = Path.Combine(root, "a.zip");
        Zip(archive, entries);
        string logs = Path.Combine(root, "logs");
        string temp = Directory.CreateDirectory(Path.Combine(root, "temp")).FullName;
        var runner = new CliProcessRunner(new CliLocation(DeploymentTests.GuiOutputDirectory));
        var search = new FakeSearch { Result = new([new(archive, new FileInfo(archive).Length, DateTime.UtcNow)], []) };
        var model = new MainViewModel(new("cli", true, "ok"), search, new FakeSettings(), runner, new EntriesStore(temp), new LogLocation(logs))
            { SearchDirectory = root };
        Assert.True(await model.SearchAsync());
        return new(model, root, logs, temp, archive);
    }

    // RAR through the same GUI path. guiRoot is the folder whose cli\unextract.exe runs (the GUI never loads UnRAR.dll).
    private static async Task<Real> OpenRar(string kind, string guiRoot, byte[] rar)
    {
        string root = ArchiveSearchTests.Fixture(kind);
        string archive = Path.Combine(root, "a.rar");
        File.WriteAllBytes(archive, rar);
        string logs = Path.Combine(root, "logs");
        string temp = Directory.CreateDirectory(Path.Combine(root, "temp")).FullName;
        var runner = new CliProcessRunner(new CliLocation(guiRoot));
        var search = new FakeSearch { Result = new([new(archive, new FileInfo(archive).Length, DateTime.UtcNow)], []) };
        var model = new MainViewModel(new("cli", true, "ok"), search, new FakeSettings(), runner, new EntriesStore(temp), new LogLocation(logs))
            { SearchDirectory = root };
        Assert.True(await model.SearchAsync());
        return new(model, root, logs, temp, archive);
    }

    // A copy of the bundled cli\ with the adopted UnRAR64.dll placed beside it, as a user would (the DLL is not shipped).
    private static string GuiRootWithUnrar()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "gui-unrar-app-" + Guid.NewGuid().ToString("N"));
        string cli = Directory.CreateDirectory(Path.Combine(root, "cli")).FullName;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(DeploymentTests.GuiOutputDirectory, "cli")))
            File.Copy(file, Path.Combine(cli, Path.GetFileName(file)));
        Unextract.Core.Tests.Fixtures.UnrarTestDll.CopyTo(cli);
        return root;
    }

    [Fact]
    public async Task RarIsAnalyzedAndDeletedThroughTheCliWhenUnrarIsPlacedBesideIt()
    {
        byte[] rar = Unextract.Core.Tests.Fixtures.Rar5Writer.Build(
        [
            new() { Name = "same.txt", Data = Bytes },
            new() { Name = "d", IsDirectory = true, Attributes = 0x10 },
            new() { Name = "d/keep.txt", Data = Bytes },
        ]);
        var r = await OpenRar("int-rar", GuiRootWithUnrar(), rar);
        string target = Dir(r, "t");
        File.WriteAllBytes(Path.Combine(target, "same.txt"), Bytes);
        Directory.CreateDirectory(Path.Combine(target, "d"));
        File.WriteAllBytes(Path.Combine(target, "d", "keep.txt"), [3, 2, 1]);
        await r.Model.AddTargetsAsync(target);
        Assert.Equal(1, (await r.Model.AnalyzeSelectedAsync().WaitAsync(Limit)).Succeeded);
        var plan = r.Model.PlanDeletion()!;
        Assert.Equal(["same.txt"], plan.Items.Single().Candidates.Select(c => c.Name));
        Assert.Equal(new DeletionBatchResult(DeletionStop.None, 1, 1, 1), await r.Model.DeleteAsync(plan, approved: true).WaitAsync(Limit));
        Assert.False(File.Exists(Path.Combine(target, "same.txt")));
        Assert.Equal(new byte[] { 3, 2, 1 }, File.ReadAllBytes(Path.Combine(target, "d", "keep.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(r.Temp));
    }

    [Fact]
    public async Task WithoutUnrarTheRarAnalysisFailsWithTheCliExplanationAndNothingChanges()
    {
        byte[] rar = Unextract.Core.Tests.Fixtures.Rar5Writer.Build([new() { Name = "same.txt", Data = Bytes }]);
        var r = await OpenRar("int-rar-nodll", DeploymentTests.GuiOutputDirectory, rar);
        string target = Dir(r, "t");
        File.WriteAllBytes(Path.Combine(target, "same.txt"), Bytes);
        await r.Model.AddTargetsAsync(target);
        Assert.Equal(0, (await r.Model.AnalyzeSelectedAsync().WaitAsync(Limit)).Succeeded);
        var state = r.Model.Archives[0].Targets[0];
        string library = Path.Combine(DeploymentTests.GuiOutputDirectory, "cli", "UnRAR64.dll");
        Assert.Contains($"{library} に UnRAR.dll 7.23 (x64) の UnRAR64.dll を置いてください。ZIP の処理には影響しません。", state.FailureDetail, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(target, "same.txt")));
        Assert.Empty(r.Model.PlanDeletion()!.Items);
    }

    private static string Dir(Real r, params string[] parts)
    {
        string path = Directory.CreateDirectory(Path.Combine([r.Root, .. parts])).FullName;
        return path;
    }

    [Fact]
    public async Task FastDeletesSameSizeFilesWithoutComparingContentAndLeavesOtherSizesAlone()
    {
        var r = await Open("int-fast", ("same.txt", Bytes), ("other.txt", Bytes));
        string target = Dir(r, "t");
        File.WriteAllBytes(Path.Combine(target, "same.txt"), [9, 9, 9]);
        File.WriteAllBytes(Path.Combine(target, "other.txt"), [9, 9]);
        await r.Model.AddTargetsAsync(target);
        Assert.True(r.Model.SetMode(CliMode.Fast, discardApproved: true));
        Assert.Equal(1, (await r.Model.AnalyzeSelectedAsync().WaitAsync(Limit)).Succeeded);
        var plan = r.Model.PlanDeletion()!;
        Assert.Equal(["same.txt"], plan.Items.Single().Candidates.Select(c => c.Name));
        Assert.Equal(new DeletionBatchResult(DeletionStop.None, 1, 1, 1), await r.Model.DeleteAsync(plan, approved: true).WaitAsync(Limit));
        Assert.False(File.Exists(Path.Combine(target, "same.txt")));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(Path.Combine(target, "other.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(r.Temp));
        Assert.Contains("\"mode\":\"fast\"", File.ReadAllText(Assert.Single(Directory.EnumerateFiles(r.Logs))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrictRevalidatesAfterAnalysisSoAFileChangedAfterwardsIsKeptAndTheRestIsDeleted()
    {
        var r = await Open("int-changed", ("keep.txt", Bytes), ("go.txt", Bytes));
        string target = Dir(r, "t");
        File.WriteAllBytes(Path.Combine(target, "keep.txt"), Bytes);
        File.WriteAllBytes(Path.Combine(target, "go.txt"), Bytes);
        await r.Model.AddTargetsAsync(target);
        Assert.Equal(1, (await r.Model.AnalyzeSelectedAsync().WaitAsync(Limit)).Succeeded);
        var plan = r.Model.PlanDeletion()!;
        Assert.Equal(2, plan.Items.Single().Candidates.Count);
        File.WriteAllBytes(Path.Combine(target, "keep.txt"), [3, 2, 1]);
        var result = await r.Model.DeleteAsync(plan, approved: true).WaitAsync(Limit);
        Assert.Equal(DeletionStop.None, result.Stop);
        Assert.Equal(1, result.Launched);
        Assert.Equal(new byte[] { 3, 2, 1 }, File.ReadAllBytes(Path.Combine(target, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(target, "go.txt")));
        var state = r.Model.Archives[0].Targets[0];
        Assert.True(state.HasDeleteStarted);
        Assert.Empty(r.Model.PlanDeletion()!.Items);
        Assert.Empty(Directory.EnumerateFileSystemEntries(r.Temp));
    }

    [Fact]
    public async Task TwoTargetsOfOneArchiveRunInRegistrationOrderWithConsecutiveLogNumbers()
    {
        var r = await Open("int-two", ("x.txt", Bytes));
        string first = Dir(r, "first");
        string second = Dir(r, "second");
        File.WriteAllBytes(Path.Combine(first, "x.txt"), Bytes);
        File.WriteAllBytes(Path.Combine(second, "x.txt"), Bytes);
        await r.Model.AddTargetsAsync(first);
        await r.Model.AddTargetsAsync(second);
        Assert.Equal(2, (await r.Model.AnalyzeSelectedAsync().WaitAsync(Limit)).Succeeded);
        var plan = r.Model.PlanDeletion()!;
        Assert.Equal(new DeletionBatchResult(DeletionStop.None, 2, 2, 2), await r.Model.DeleteAsync(plan, approved: true).WaitAsync(Limit));
        Assert.False(File.Exists(Path.Combine(first, "x.txt")));
        Assert.False(File.Exists(Path.Combine(second, "x.txt")));
        string[] logs = Directory.EnumerateFiles(r.Logs).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(2, logs.Length);
        Assert.Matches(@"^delete-\d{8}-\d{6}-001\.jsonl$", logs[0]);
        Assert.Matches(@"^delete-\d{8}-\d{6}-002\.jsonl$", logs[1]);
        Assert.Equal(logs[0][..^"-001.jsonl".Length], logs[1][..^"-002.jsonl".Length]);
        Assert.Equal(Path.Combine(r.Logs, logs[0]), r.Model.Archives[0].Targets[0].LogPath);
        Assert.Equal(Path.Combine(r.Logs, logs[1]), r.Model.Archives[0].Targets[1].LogPath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(r.Temp));
    }

    [Fact]
    public async Task ParentAndChildTargetsOverlapWithoutErrorAndTheChildFindsItsFileAlreadyGone()
    {
        var r = await Open("int-nested", ("x.txt", Bytes), ("sub/x.txt", Bytes));
        string parent = Dir(r, "p");
        string child = Dir(r, "p", "sub");
        File.WriteAllBytes(Path.Combine(parent, "x.txt"), Bytes);
        File.WriteAllBytes(Path.Combine(child, "x.txt"), Bytes);
        await r.Model.AddTargetsAsync(parent);
        await r.Model.AddTargetsAsync(child);
        Assert.Equal(2, (await r.Model.AnalyzeSelectedAsync().WaitAsync(Limit)).Succeeded);
        var plan = r.Model.PlanDeletion()!;
        Assert.Equal(2, plan.Items.Count);
        var result = await r.Model.DeleteAsync(plan, approved: true).WaitAsync(Limit);
        Assert.Equal(new DeletionBatchResult(DeletionStop.None, 2, 2, 2), result);
        Assert.False(File.Exists(Path.Combine(parent, "x.txt")));
        Assert.False(File.Exists(Path.Combine(child, "x.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(r.Temp));
        Assert.Equal(result.Launched, Directory.EnumerateFiles(r.Logs).Count());
    }
}
