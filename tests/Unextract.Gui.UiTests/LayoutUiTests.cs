using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using Xunit.Abstractions;

namespace Unextract.Gui.UiTests;

// The screens of the published GUI in the states of docs/TESTING.md#gui-review, at the default size, the minimum size and
// the logical work areas of a 1920x1080 screen at 150% and 200% (the display scale itself is not changed). The pictures
// are kept in the fixture for the implementer's screen review; there is no image comparison. Each size also checks
// that the main controls keep a non-empty area inside the window (the window may scroll vertically at small sizes).
public sealed class LayoutUiTests(ITestOutputHelper output) : UiTestBase
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    internal static readonly (string Name, int Width, int Height)[] Sizes =
        [("default", 1280, 820), ("min", 720, 460), ("150", 1280, 688), ("200", 960, 516)];

    private static readonly string[] MainParts =
    [
        "SearchDirectoryBox", "SearchButton", "ChooseFolderButton", "RecursiveCheckBox", "StrictRadio", "FastRadio", "ArchiveFilterBox",
        "BulkAddButton", "SelectionMenuButton", "ArchiveList", "NextStepText", "AnalyzeSelectedButton", "DeleteSelectedButton",
        "StatusText", "DiagnosticsToggle", "OpenLogFolderButton",
    ];

    private string Folder(GuiSession ui) => Directory.CreateDirectory(Path.Combine(ui.Root, "shots")).FullName;

    private void Shot(GuiSession ui, AutomationElement element, string name)
    {
        string path = Path.Combine(Folder(ui), name + ".png");
        using var image = Capture.Element(element);
        image.ToFile(path);
        Assert.True(new FileInfo(path).Length > 0);
        output.WriteLine("screenshot: " + path);
    }

    // Waits until the window's layout stops changing after a resize or a state change.
    private static void Settle(GuiSession ui)
    {
        string Probe() => string.Join(";", new[] { "StatusText", "NextStepText", "ArchiveList", "TargetPathText", "ArchiveDetailNameText" }
            .Select(id => ui.Find(id)?.BoundingRectangle.ToString() ?? "-"));
        string last = "";
        int stable = 0;
        Wait.Until(() =>
        {
            string now = Probe();
            stable = now == last ? stable + 1 : 0;
            last = now;
            return stable >= 4;
        }, "the layout to settle");
    }

    private void AssertParts(GuiSession ui, string state, string size)
    {
        Rectangle window = ui.Main.BoundingRectangle;
        foreach (string id in MainParts)
        {
            var part = ui.Get(id);
            Rectangle rect = part.BoundingRectangle;
            Assert.True(rect.Width > 0 && rect.Height > 0, $"{state}/{size}: {id} has an empty rectangle {rect}");
            Assert.True(rect.Left >= window.Left && rect.Right <= window.Right, $"{state}/{size}: {id} {rect} leaves the window {window} horizontally");
            // Vertically the window scrolls as a whole below its minimum work area; at the larger sizes nothing is pushed out.
            if (size != "min") Assert.True(rect.Top >= window.Top && rect.Bottom <= window.Bottom, $"{state}/{size}: {id} {rect} is outside the window {window}");
        }
    }

    private void ShotSizes(GuiSession ui, string state, bool parts = true)
    {
        foreach (var (size, width, height) in Sizes)
        {
            ui.Main.Patterns.Transform.Pattern.Resize(width, height);
            Wait.Until(() => ui.Main.BoundingRectangle.Width == width && ui.Main.BoundingRectangle.Height == height, $"the window to be {width}x{height}");
            Settle(ui);
            if (parts) AssertParts(ui, state, size);
            Shot(ui, ui.Main, $"{state}-{size}");
        }
        ui.Main.Patterns.Transform.Pattern.Resize(Sizes[0].Width, Sizes[0].Height);
        Settle(ui);
    }

    private static string Zip(GuiSession ui, string relative, int files = 3)
    {
        string path = Path.Combine(ui.Fixtures, relative);
        Files.Zip(path, Enumerable.Range(1, files).Select(i => ($"f{i}.txt", "x")).ToArray());
        return path;
    }

    [Fact]
    public void MainStatesAtEverySize()
    {
        var ui = Start(kind: "layout");
        uint dpi = GetDpiForWindow((nint)ui.Main.Properties.NativeWindowHandle.Value);
        output.WriteLine($"window DPI: {dpi} ({dpi * 100 / 96}%), window {ui.Main.BoundingRectangle}");
        Assert.True(dpi >= 96);
        string rlo = char.ConvertFromUtf32(0x202E);
        string deep = Path.Combine("とても長いディレクトリ名が続く場合の表示確認用フォルダー", "第2階層のさらに長いフォルダー名" + rlo + "x");
        string a = Zip(ui, "a.zip"), d1 = Zip(ui, "del-1.zip"), d2 = Zip(ui, "del-2.zip"), d3 = Zip(ui, "del-3.zip");
        string longZip = Zip(ui, Path.Combine(deep, "資料集-非常に長いアーカイブ名の例_第1版.zip"));
        for (int i = 1; i <= 30; i++) Zip(ui, Path.Combine("many", $"many-{i:D2}.zip"), 1);
        string T(string archive) => Path.Combine(Path.GetDirectoryName(archive)!, "out-" + Path.GetFileNameWithoutExtension(archive));
        foreach (string z in new[] { a, d1, d2, d3, longZip }) Directory.CreateDirectory(T(z));
        Item[] items = [new(1, "d/", "DIRECTORY", 0, true), new(2, "f1.txt", "MATCHED", 1), new(3, "f2.txt", "MODIFIED", 1), new(4, "f3.txt", "MISSING", 1)];
        string release = ui.FakeAnalyze(a, T(a), "strict", items, wait: true, waitAfter: 2, maxUses: 1);
        foreach (string z in new[] { d1, d2, d3 }) ui.FakeAnalyze(z, T(z), "strict", items);
        ui.FakeAnalysisFailure(longZip, T(longZip), maxUses: 1);
        string cancelRelease = ui.FakeAnalyze(longZip, T(longZip), "strict", items, wait: true, waitAfter: 1, maxUses: 1);
        ui.FakeAnalyze(a, T(a), "fast", [items[0], items[1] with { Status = "SAME_SIZE" }, items[2] with { Status = "SAME_SIZE" }, items[3]]);
        ui.FakeAnalyze(a, T(a), "strict", items);
        ui.FakeDelete(d1, T(d1), "strict", 4, 1, [new(2, "f1.txt", "DELETED", 1)], notSelected: 3);
        ui.FakeDelete(d2, T(d2), "strict", 4, 1, [new(2, "f1.txt", "DELETE_FAILED", 1)], notSelected: 3);
        ui.FocusMain();

        ShotSizes(ui, "01-initial");
        ui.Search();
        ui.Toggle(ui.Get("DiagnosticsToggle"));
        ShotSizes(ui, "02-searched-many-archives");
        ui.Toggle(ui.Get("DiagnosticsToggle"));
        ui.ViewArchive("a.zip");
        ShotSizes(ui, "03-archive-view");
        ui.BulkAdd(@"{{archive.dir}}\out-{{archive.name}}");
        ShotSizes(ui, "04-targets-added");

        // Analyze a, the long-path archive and d1-d3 (the many-*.zip Targets do not exist and stay unselected).
        ui.SelectionMenu("ClearAllButton");
        foreach (string z in new[] { a, longZip, d1, d2, d3 }) ui.Toggle(ui.TargetCheck(T(z)));
        ui.ViewTarget(T(a));
        ui.Invoke("AnalyzeSelectedButton");
        Wait.Until(() => ui.TextOf("EntryProgressText").Contains("1 / 4", StringComparison.Ordinal), "the entry progress");
        ShotSizes(ui, "05-analyzing");
        ui.Scenario.Release(release);
        ui.WaitStatus("解析終了");
        ui.WaitIdle();
        ui.ViewTarget(T(a));
        ShotSizes(ui, "06-strict-results");
        ui.Get("ResultCategoryCombo").AsComboBox().Select("MODIFIED (1)");
        ui.SetText("ResultPathFilterBox", "f2");
        Wait.Until(() => ui.TextOf("ResultCountText") == "表示 1 / 4", "the entry filter");
        ShotSizes(ui, "07-filtered-results");
        ui.ViewTarget(T(longZip));
        ShotSizes(ui, "08-analysis-failed-long-path");

        // A cancelled re-analysis.
        ui.Invoke("AnalyzeTargetButton", ui.Main);
        Wait.Until(() => ui.Find("CancelAnalysisButton") is not null, "the analysis to run");
        ui.Invoke("CancelAnalysisButton");
        ui.WaitStatus("解析終了");
        ui.WaitIdle();
        ui.Scenario.Release(cancelRelease);
        ShotSizes(ui, "09-analysis-cancelled");

        // Deletion: d1 succeeds, d2 ends with DELETE_FAILED (the batch stops), d3 is not started. a is hidden by the filter.
        ui.SetText("ArchiveFilterBox", "del-");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 3", StringComparison.Ordinal), "the filter");
        var confirm = ui.OpenDeleteConfirmation();
        Assert.Contains("表示フィルタで非表示", ui.BodyOf(confirm));
        Shot(ui, confirm, "10-confirm-strict-hidden-excluded");
        confirm.Patterns.Transform.Pattern.Resize(720, 460);
        Shot(ui, confirm, "10-confirm-strict-hidden-excluded-720x460");
        ui.CloseConfirmation(confirm);
        ui.SetText("ArchiveFilterBox", "");
        Wait.Until(() => ui.TextOf("ArchiveCountText").StartsWith("表示 35", StringComparison.Ordinal), "the filter to clear");
        ui.Toggle(ui.TargetCheck(T(a)));
        confirm = ui.OpenDeleteConfirmation();
        ui.Invoke("DeletionConfirmDeleteButton", confirm);
        ui.WaitStatus("削除終了");
        ui.WaitIdle();
        ui.ViewTarget(T(d1));

        ShotSizes(ui, "11-after-delete-success");
        ui.ViewTarget(T(d2));
        ShotSizes(ui, "12-after-delete-failed");
        ui.ViewTarget(T(d3));
        ShotSizes(ui, "13-after-delete-unrun");

        // Fast: the mode change asks first, then the warning; a Fast analysis and its confirmation.
        ui.ClickUntil("FastRadio", () => ui.Modal() is not null, "the mode change confirmation");
        var box = ui.WaitModal("モード変更の確認");
        Shot(ui, box, "14-mode-change-confirmation");
        ui.PressMessageBox(box, "1");
        Wait.Until(() => ui.Modal() is null, "the message box to close");
        Wait.Until(() => ui.Find("FastWarningText") is not null, "the Fast warning");
        ui.SelectionMenu("ClearAllButton");
        ui.Toggle(ui.TargetCheck(T(a)));
        ui.AnalyzeSelected();
        ui.ViewTarget(T(a));
        ShotSizes(ui, "15-fast-results");
        confirm = ui.OpenDeleteConfirmation();
        Shot(ui, confirm, "16-confirm-fast");
        ui.CloseConfirmation(confirm);
        ui.SelectionMenu("ClearAllButton");
        ui.Toggle(ui.TargetCheck(T(longZip)));
        confirm = ui.OpenDeleteConfirmation();
        Shot(ui, confirm, "17-confirm-nothing-deletable");
        ui.CloseConfirmation(confirm);

        // Target settings: each preset, an invalid template, and the bulk rule.
        ui.ViewArchive("a.zip");
        ui.Invoke("AddTargetButton", ui.Main);
        var editor = ui.WaitModal("Target設定");
        Shot(ui, editor, "18-editor-same-directory");
        ui.Get("TargetEditorPresetCombo", editor).AsComboBox().Select(1);
        Wait.Until(() => ui.TextOf("TargetEditorPreview", editor).EndsWith(Path.Combine(ui.Fixtures, "a"), StringComparison.Ordinal), "the preset preview");
        Shot(ui, editor, "19-editor-archive-name");
        ui.SetText("TargetEditorTemplateBox", @"D:\bad\..\path", editor);
        Wait.Until(() => !ui.Get("TargetEditorOkButton", editor).IsEnabled, "the registration button to be disabled");
        Shot(ui, editor, "20-editor-invalid");
        ui.Invoke("TargetEditorCancelButton", editor);
        Wait.Until(() => ui.Modal() is null, "the editor to close");
        ui.Invoke("BulkAddButton");
        editor = ui.WaitModal("Target設定");
        Shot(ui, editor, "21-editor-bulk");
        ui.Invoke("TargetEditorCancelButton", editor);
        Wait.Until(() => ui.Modal() is null, "the editor to close");

        // Re-search asks first.
        ui.Invoke("SearchButton");
        box = ui.WaitModal("再検索の確認");
        Shot(ui, box, "22-research-confirmation");
        ui.PressMessageBox(box, "2");
        Wait.Until(() => ui.Modal() is null, "the message box to close");
    }

    [Fact]
    public void DeletingUnknownResultAndExitWaitAtEverySize()
    {
        var ui = Start(kind: "layout-exit");
        string a = Zip(ui, "a.zip"), b = Zip(ui, "b.zip");
        string ta = Path.Combine(ui.Fixtures, "out-a"), tb = Path.Combine(ui.Fixtures, "out-b");
        Directory.CreateDirectory(ta);
        Directory.CreateDirectory(tb);
        Item[] items = [new(1, "f1.txt", "MATCHED", 1), new(2, "f2.txt", "MATCHED", 1), new(3, "f3.txt", "MODIFIED", 1)];
        ui.FakeAnalyze(a, ta, "strict", items);
        ui.FakeAnalyze(b, tb, "strict", items);
        // a: the output stops after the first entry (no result record) - the first entry after it is "unknown".
        ui.FakeDeleteRaw(a, ta, [Jsonl.Run("delete", "strict", a, ta, 3, 2, true), Jsonl.Entry(1, "f1.txt", "DELETED", 1)], exitCode: 1);
        // b: an incompatible record version - the result is unknown; it waits so the delete-running and exit-wait screens can be taken.
        string release = ui.Scenario.Add("delete", [Jsonl.Run("delete", "strict", b, tb, 3, 2, true), "{\"v\":2,\"type\":\"entry\"}"],
            exitCode: 1, archive: b, target: tb, wait: true, waitAfterLines: 1);
        ui.SaveScenario();
        ui.FocusMain();
        ui.Search();
        ui.BulkAdd(@"{{archive.dir}}\out-{{archive.name}}");
        ui.AnalyzeSelected();
        ui.SelectionMenu("ClearAllButton");
        ui.Toggle(ui.TargetCheck(ta));
        var confirm = ui.OpenDeleteConfirmation();
        ui.Invoke("DeletionConfirmDeleteButton", confirm);
        ui.WaitStatus("削除終了");
        ui.WaitIdle();
        ui.ViewTarget(ta);
        Assert.StartsWith("削除実行済み: エラー終了（結果が不完全）", ui.TextOf("TargetStateText"));
        ShotSizes(ui, "30-after-delete-interrupted");

        ui.SelectionMenu("ClearAllButton");
        ui.Toggle(ui.TargetCheck(tb));
        ui.ViewTarget(tb);
        confirm = ui.OpenDeleteConfirmation();
        ui.Invoke("DeletionConfirmDeleteButton", confirm);
        Wait.Until(() => ui.Invocations.Count(i => i.Operation == "delete") == 2, "the second delete to start");
        Wait.Until(() => ui.TextOf("TargetStateText") == "削除中", "the Target to be deleting");
        ShotSizes(ui, "31-deleting");
        ui.Main.Patterns.Window.Pattern.Close();
        Wait.Until(() => ui.Status() == "現在のTargetの処理が終わり次第終了します。", "the waiting message");
        ShotSizes(ui, "32-exit-wait");
        Assert.False(ui.HasExited);
        ui.Scenario.Release(release);
        Wait.Until(() => ui.HasExited, "the GUI to exit after the current Target");
    }

    [Fact]
    public void NoArchiveFoundWithANotification()
    {
        // settings.json is a directory: saving the search directory fails, which is reported as a notification.
        var ui = Start(kind: "layout-empty");
        Directory.CreateDirectory(ui.SettingsPath);
        ui.FocusMain();
        ui.Search();
        Assert.Contains("(1 件)", ui.TextOf("DiagnosticsToggle"));
        Assert.Contains("ZIPが見つかりませんでした", ui.TextOf("NextStepText"));
        ui.Toggle(ui.Get("DiagnosticsToggle"));
        Wait.Until(() => ui.Find("DiagnosticsList") is not null, "the notifications");
        ShotSizes(ui, "40-no-archive-with-notification");
    }
}
