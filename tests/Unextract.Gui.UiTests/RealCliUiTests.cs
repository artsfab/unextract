namespace Unextract.Gui.UiTests;

// The one path with the bundled real CLI: published package, self-made GUID fixture, own TMP and data root.
public sealed class RealCliUiTests : UiTestBase
{
    [Fact]
    public void SearchStrictAnalyzeAndConfirmedDeleteRemoveOnlyTheMatchedFile()
    {
        var ui = Start(fake: false, kind: "real");
        string archive = Path.Combine(ui.Fixtures, "real.zip"), target = Path.Combine(ui.Fixtures, "out");
        Files.Zip(archive, ("d/", ""), ("match.txt", "abc"), ("modified.txt", "abc"), ("missing.txt", "abc"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "match.txt"), "abc");
        File.WriteAllText(Path.Combine(target, "modified.txt"), "xyz");
        File.WriteAllText(Path.Combine(target, "unrelated.txt"), "keep");

        Assert.Contains("同梱CLI", ui.TextOf("CliStatusText"));
        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out");
        ui.AnalyzeSelected();
        Assert.StartsWith("解析済み (Strict): Matched 1 / Modified 1 / Missing 1 / Skipped 0 / Directory 1", ui.StateOf(target), StringComparison.Ordinal);
        // Analysis changes nothing.
        Assert.Equal(new[] { "match.txt", "modified.txt", "unrelated.txt" }, Directory.EnumerateFiles(target).Select(f => Path.GetFileName(f)!).Order().ToArray());

        var confirm = ui.OpenDeleteConfirmation();
        Assert.StartsWith("Strict", ui.TextOf("DeletionConfirmMode", confirm));
        ui.Invoke("DeletionConfirmDeleteButton", confirm);
        ui.WaitStatus("削除終了");
        ui.WaitIdle();

        Assert.Equal(new[] { "modified.txt", "unrelated.txt" }, Directory.EnumerateFiles(target).Select(f => Path.GetFileName(f)!).Order().ToArray());
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "unrelated.txt")));
        Assert.StartsWith("削除実行済み: 削除完了", ui.StateOf(target), StringComparison.Ordinal);
        string log = Assert.Single(Directory.EnumerateFiles(ui.LogsDirectory));
        Assert.Matches(@"^delete-\d{8}-\d{6}-001\.jsonl$", Path.GetFileName(log));
        string text = File.ReadAllText(log);
        Assert.Contains("\"status\":\"DELETED\"", text);
        Assert.Contains("\"name\":\"match.txt\"", text);
        Assert.Contains("\"outcome\":\"completed\"", text);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ui.TempDirectory));
        Assert.True(File.Exists(archive), "the ZIP itself is never touched");
    }
}
