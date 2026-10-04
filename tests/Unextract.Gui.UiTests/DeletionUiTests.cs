using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Unextract.Gui.UiTests;

public sealed class DeletionUiTests : UiTestBase
{
    private static readonly Item[] TwoFiles = [new(1, "f1.txt", "MATCHED", 10), new(2, "f2.txt", "MODIFIED", 10)];

    internal static string AllText(GuiSession ui, AutomationElement window) =>
        string.Join("\n", window.FindAllDescendants().Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)));

    internal static void PressKey(Window window, VirtualKeyShort key)
    {
        window.SetForeground();
        Keyboard.Type(key);
    }

    [Fact]
    public void SelectedTargetsHiddenByTheArchiveFilterStayInTheDeletionAndAreMarked()
    {
        var ui = Start(kind: "del-hidden");
        string a = Path.Combine(ui.Fixtures, "a.zip"), b = Path.Combine(ui.Fixtures, "b.zip");
        string ta = Path.Combine(ui.Fixtures, "out-a"), tb = Path.Combine(ui.Fixtures, "out-b");
        Files.Zip(a, ("f1.txt", "x"), ("f2.txt", "y"));
        Files.Zip(b, ("g1.txt", "z"));
        Directory.CreateDirectory(ta);
        Directory.CreateDirectory(tb);
        ui.FakeAnalyze(a, ta, "strict", TwoFiles);
        ui.FakeAnalyze(b, tb, "strict", [new(1, "g1.txt", "MATCHED", 20)]);
        ui.FakeDelete(a, ta, "strict", 2, 1, [new(1, "f1.txt", "DELETED", 10)], notSelected: 1);
        ui.FakeDelete(b, tb, "strict", 1, 1, [new(1, "g1.txt", "DELETED", 20)], notSelected: 0);

        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out-{{archive.name}}");
        ui.AnalyzeSelected();
        ui.SetText("ArchiveFilterBox", "a.zip");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 1 / 検索 2", StringComparison.Ordinal), "the filter to apply");

        var dialog = ui.OpenDeleteConfirmation();
        Assert.Equal("削除の確認", dialog.Title);
        string body = ui.BodyOf(dialog);
        Assert.Contains("表示フィルタで非表示の選択済みTarget 1 件を含みます（対象から外していません）。", ui.TextOf("DeletionConfirmHidden", dialog));
        Assert.Contains("表示フィルタで非表示の選択済みTarget 1 件を含みます（対象から外していません）。", body);
        Assert.Contains("削除対象のTarget（2 件、この順に実行）:", body);
        Assert.Contains($"1. {a} → {ta}: 候補 1 ファイル / 10 bytes", body);
        Assert.Contains($"2. {b} → {tb}: 候補 1 ファイル / 20 bytes  ［表示フィルタで非表示］", body);
        Assert.Contains("ごみ箱は使わず、完全に削除します。", body);
        Assert.Contains("途中で停止した場合も、それまでに削除したファイルは元に戻りません。", body);
        Assert.StartsWith("Strict", ui.TextOf("DeletionConfirmMode", dialog));
        Assert.StartsWith("2 件", ui.TextOf("DeletionConfirmTargetCount", dialog));
        Assert.StartsWith("2 ファイル（解析時点の最大件数）", ui.TextOf("DeletionConfirmFileCount", dialog));
        Assert.StartsWith("30 bytes（論理サイズの合計", ui.TextOf("DeletionConfirmSize", dialog));
        Assert.DoesNotContain("Fastは内容の一致", body);
        Assert.Null(ui.Find("DeletionConfirmFastWarning", dialog));
        Assert.DoesNotContain(ui.Invocations, i => i.Operation == "delete");

        ui.Invoke("DeletionConfirmDeleteButton", dialog);
        ui.WaitStatus("削除終了");
        ui.WaitIdle();

        var deletes = ui.Invocations.Where(i => i.Operation == "delete").ToArray();
        Assert.Equal(2, deletes.Length);
        Assert.Equal([a, b], deletes.Select(d => d.Args[1]).ToArray());
        Assert.Equal(["f1.txt"], deletes[0].EntryLines);
        Assert.Equal(["g1.txt"], deletes[1].EntryLines);
        foreach (var d in deletes)
        {
            Assert.Contains("--yes", d.Args);
            Assert.Contains("--jsonl", d.Args);
            Assert.DoesNotContain("--fast", d.Args);
            Assert.Equal(ui.TempDirectory, Path.GetDirectoryName(d.Option("--entries")));
            Assert.StartsWith("unextract-entries-", Path.GetFileName(d.Option("--entries")), StringComparison.Ordinal);
            Assert.False(File.Exists(d.Option("--entries")), "the temporary entries file is removed after the CLI exits");
        }
        Assert.Equal(ui.LogsDirectory, Path.GetDirectoryName(deletes[0].Option("--log")));
        Assert.Matches(@"^delete-\d{8}-\d{6}-001\.jsonl$", Path.GetFileName(deletes[0].Option("--log"))!);
        Assert.Matches(@"^delete-\d{8}-\d{6}-002\.jsonl$", Path.GetFileName(deletes[1].Option("--log"))!);
        Assert.True(File.Exists(deletes[0].Option("--log")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(ui.TempDirectory));
    }

    [Fact]
    public void ExcludedTargetsAreListedWithReasonsAndNeitherEnterNorEscapeStartsTheCli()
    {
        var ui = Start(kind: "del-excluded");
        string z1 = Path.Combine(ui.Fixtures, "z1.zip"), z2 = Path.Combine(ui.Fixtures, "z2.zip");
        Files.Zip(z1, ("e.txt", "x"));
        Files.Zip(z2, ("e.txt", "x"));
        string Target(string archive, string kind) => Path.Combine(ui.Fixtures, Path.GetFileNameWithoutExtension(archive) + "-" + kind);
        foreach (string archive in new[] { z1, z2 })
            foreach (string kind in new[] { "ok", "no", "bad" }) Directory.CreateDirectory(Target(archive, kind));
        Item[] matched = [new(1, "e.txt", "MATCHED", 5)];
        ui.FakeAnalyze(z1, Target(z1, "ok"), "strict", matched);
        ui.FakeAnalysisFailure(z1, Target(z1, "bad"));
        ui.FakeAnalyze(z2, Target(z2, "ok"), "strict", [new(1, "e.txt", "MODIFIED", 5)]);
        ui.FakeAnalyze(z2, Target(z2, "no"), "strict", matched);
        ui.FakeAnalyze(z2, Target(z2, "bad"), "strict", matched);
        ui.FakeDelete(z2, Target(z2, "bad"), "strict", 1, 1, [new(1, "e.txt", "DELETED", 5)], notSelected: 0);

        ui.Search();
        foreach (string kind in new[] { "ok", "no", "bad" }) ui.BulkAdd(@"{{archive.dir}}\{{archive.name}}-" + kind);

        // 1. Only z2-bad is analyzed and deleted: it becomes "deleted, waiting for re-analysis".
        ui.SelectionMenu("ClearAllButton");
        ui.Toggle(ui.TargetCheck(Target(z2, "bad")));
        ui.AnalyzeSelected();
        var first = ui.OpenDeleteConfirmation();
        ui.Invoke("DeletionConfirmDeleteButton", first);
        ui.WaitStatus("削除終了");
        ui.WaitIdle();
        Assert.StartsWith("削除実行済み: 削除完了", ui.StateOf(Target(z2, "bad")), StringComparison.Ordinal);
        Assert.Single(ui.Invocations, i => i.Operation == "delete");

        // 2. Select everything but leave z1-no unselected: only selected, unanalyzed Targets are analyzed.
        ui.SelectionMenu("SelectAllButton");
        ui.Toggle(ui.TargetCheck(Target(z1, "no")));
        ui.AnalyzeSelected();
        Assert.Equal(4, ui.Invocations.Count(i => i.Operation == "analyze") - 1);

        // 3. z1-no is selected again but was never analyzed.
        ui.Toggle(ui.TargetCheck(Target(z1, "no")));

        // 4. z2-no is analyzed, then its directory disappears and the existence is re-checked.
        Directory.Move(Target(z2, "no"), Target(z2, "no") + "-moved");
        ui.ViewTarget(Target(z2, "no"));
        ui.Invoke("RefreshTargetButton", ui.Main);
        Wait.Until(() => ui.TextOf("TargetPresenceText", ui.Main).Contains("Targetが存在しません", StringComparison.Ordinal), "the missing Target to be noticed");
        Assert.Contains("Targetが存在しません", ui.TextOf("TargetRowSummaryText", ui.TargetRow(Target(z2, "no"))));

        var dialog = ui.OpenDeleteConfirmation();
        Assert.Equal("削除の確認", dialog.Title);
        string body = ui.BodyOf(dialog);
        Assert.Contains("削除対象のTarget（1 件、この順に実行）:", body);
        Assert.Contains($"1. {z1} → {Target(z1, "ok")}: 候補 1 ファイル / 5 bytes", body);
        Assert.Contains("削除対象から除外するTarget（5 件）:", body);
        Assert.Contains($"- {z1} → {Target(z1, "no")}: 未解析、または解析に失敗しています。", body);
        Assert.Contains($"- {z1} → {Target(z1, "bad")}: 未解析、または解析に失敗しています。", body);
        Assert.Contains($"- {z2} → {Target(z2, "ok")}: 削除候補が0件です。", body);
        Assert.Contains($"- {z2} → {Target(z2, "no")}: Targetが存在しません。", body);
        Assert.Contains($"- {z2} → {Target(z2, "bad")}: 削除実行済みです。再解析が正常に完了するまで削除できません。", body);

        // Escape and Enter (the default button is the cancel button) both leave without starting the CLI.
        PressKey(dialog, VirtualKeyShort.ESCAPE);
        Wait.Until(() => ui.Modal() is null, "the confirmation to close on Escape");
        Assert.Single(ui.Invocations, i => i.Operation == "delete");
        dialog = ui.OpenDeleteConfirmation();
        PressKey(dialog, VirtualKeyShort.RETURN);
        Wait.Until(() => ui.Modal() is null, "the confirmation to close on Enter");
        Thread.Sleep(300); // negative check: give a wrongly started CLI the chance to appear
        Assert.Single(ui.Invocations, i => i.Operation == "delete");
        Assert.True(ui.Get("DeleteSelectedButton").IsEnabled);

        // 5. Nothing deletable at all: the same window lists the reasons and offers no delete button.
        ui.Toggle(ui.TargetCheck(Target(z1, "ok")));
        ui.Invoke("DeleteSelectedButton");
        var notice = ui.WaitModal("削除");
        string text = ui.BodyOf(notice);
        Assert.Contains("削除できるTargetがありません。", text);
        Assert.Contains("削除対象から除外するTarget（5 件）:", text);
        Assert.DoesNotContain("→ " + Target(z1, "ok"), text); // z1-ok was unselected, so it is not listed
        Assert.Null(ui.Find("DeletionConfirmDeleteButton", notice));
        Assert.Null(ui.Find("DeletionConfirmFastWarning", notice));
        ui.CloseConfirmation(notice);
        Assert.Single(ui.Invocations, i => i.Operation == "delete");

    }
}
