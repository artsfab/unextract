using System.Globalization;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit.Abstractions;

namespace Unextract.Gui.UiTests;

// Control, bidirectional-control and other invisible characters (and very long paths) in archive, Target and entry
// names are shown in the escaped \u{...} form. Test code writes the characters as \u escapes only.
public sealed class DisplayTextUiTests(ITestOutputHelper output) : UiTestBase(output)
{
    // Written as code points, not as characters or escapes in the source (see the repository rule on invisible characters).
    private static readonly string Rlo = Char(0x202E), Zwsp = Char(0x200B), Lri = Char(0x2066), Rli = Char(0x2067);
    private static string Char(int codePoint) => char.ConvertFromUtf32(codePoint);
    // U+2028 / U+2029 are not written as escapes in the source: they would still count as line breaks to some tools.
    private static readonly string LineSeparator = ((char)0x2028).ToString(), ParagraphSeparator = ((char)0x2029).ToString();

    private static bool IsInvisible(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator && rune.Value is not ('\n' or '\r' or '\t');

    private static void AssertNoRawInvisibleCharacters(string what, string text)
    {
        foreach (var rune in text.EnumerateRunes())
            Assert.False(IsInvisible(rune), $"{what} contains the raw character U+{rune.Value:X4}: {Models.Escape(text)}");
    }

    // Everything the window shows as text: names, and the values of read-only text boxes (full paths and details).
    // Editable inputs hold what the user typed and are not display conversions.
    private static void AssertWindowShowsNoRawInvisibleCharacters(AutomationElement window)
    {
        int readOnlyValues = 0;
        foreach (var element in window.FindAllDescendants())
        {
            // Some elements do not support every property: read them as optional values.
            string id = element.Properties.AutomationId.ValueOrDefault ?? "";
            if (element.Properties.ControlType.ValueOrDefault == ControlType.Edit)
            {
                if (element.Patterns.Value.PatternOrDefault is not { } value || !value.IsReadOnly.ValueOrDefault) continue;
                AssertNoRawInvisibleCharacters($"read-only text '{id}'", value.Value.ValueOrDefault ?? "");
                readOnlyValues++;
                continue;
            }
            AssertNoRawInvisibleCharacters($"{element.Properties.ControlType.ValueOrDefault} '{id}' name", element.Properties.Name.ValueOrDefault ?? "");
        }
        Assert.True(readOnlyValues > 0, "no read-only text was checked");
    }

    [UiFact]
    public void InvisibleCharactersAndVeryLongPathsAreShownEscaped()
    {
        var ui = Start();
        string deep = ui.Fixtures;
        for (int i = 0; i < 6; i++) deep = Path.Combine(deep, new string((char)('a' + i), 45));
        deep = Path.Combine(deep, "dir" + Lri + "x");
        Directory.CreateDirectory(deep);
        string archive = Path.Combine(deep, "a" + Rlo + "b" + Zwsp + ".zip");
        Files.Zip(archive, ("x.txt", "x"));
        Assert.True(archive.Length > 260, "the archive path is longer than MAX_PATH");
        string target = Path.Combine(deep, "t" + Rli + "out");
        Directory.CreateDirectory(target);

        string[] names = ["e" + Rlo + "x.txt", "bell\u0007.txt", "line\nbreak.txt", "bidi" + Rli + "mark.txt", "plain.txt", "ls" + LineSeparator + "para" + ParagraphSeparator + ".txt"];
        Item[] items = names.Select((n, i) => new Item(i + 1, n, "MATCHED", 1)).ToArray();
        ui.FakeAnalyze(archive, target, "strict", items);

        ui.Search();
        string escapedName = Models.Escape(Path.GetFileName(archive));
        Assert.Contains("\\u{202E}", escapedName);
        Assert.Contains("\\u{200B}", escapedName);
        var row = ui.ArchiveRow(escapedName);
        Assert.Equal(Models.Escape(archive), ui.TextOf("ArchivePathText", row));
        Assert.Contains("\\u{2066}", ui.TextOf("ArchivePathText", row));

        // The archive details, and the Target settings dialog, show the archive and the resolved Target escaped, too.
        ui.ViewArchive(escapedName);
        Assert.Equal(Models.Escape(archive), ui.Get("ArchiveDetailPathText").AsTextBox().Text);
        ui.BulkAdd(@"{{archive.dir}}\t" + Rli + "out");
        ui.ViewTarget(target);
        Assert.Equal(Models.Escape(archive), ui.Get("TargetArchiveText").AsTextBox().Text);
        ui.Invoke("EditTargetButton", ui.Main);
        var editor = ui.WaitModal("Target設定");
        Assert.Equal("解決先: " + Models.Escape(target), ui.TextOf("TargetEditorPreview", editor));
        AssertWindowShowsNoRawInvisibleCharacters(editor);
        Assert.Equal("Archive: " + Models.Escape(archive), ui.Get("TargetEditorScope", editor).AsTextBox().Text);
        ui.Invoke("TargetEditorCancelButton", editor);
        Wait.Until(() => ui.Modal() is null, "the editor to close");

        ui.AnalyzeSelected();
        ui.ViewTarget(target);
        Wait.Until(() => ui.Find("ResultList") is not null, "the result list");
        var list = ui.Get("ResultList");
        foreach (string name in names)
            Wait.Until(() => list.FindFirstDescendant(ui.Conditions.ByName(Models.Escape(name))) is not null,
                $"the row for '{Models.Escape(name)}'");
        Assert.Contains("\\u{202E}", Models.Escape(names[0]));
        AssertWindowShowsNoRawInvisibleCharacters(ui.Main);

        // The confirmation lists the same names (archive and Target paths) escaped as well.
        var confirm = ui.OpenDeleteConfirmation();
        string body = ui.BodyOf(confirm);
        AssertNoRawInvisibleCharacters("the deletion confirmation", body);
        AssertWindowShowsNoRawInvisibleCharacters(confirm);
        Assert.Contains(Models.Escape(archive), body);
        Assert.Contains(Models.Escape(target), body);
        ui.CloseConfirmation(confirm);

        Assert.DoesNotContain(ui.Invocations, i => i.Operation == "delete");

        // What the CLI was given is the raw path, not the display form.
        var analysis = Assert.Single(ui.Invocations);
        Assert.Equal(archive, analysis.Args[1]);
        Assert.Equal(target, analysis.Option("--target"));
        AssertWindowShowsNoRawInvisibleCharacters(ui.Main);
    }
}
