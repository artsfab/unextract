using Unextract.Core.Display;
using Unextract.Core.Results;

namespace Unextract.Core.Tests;

// テスト O04: 表示できない名前 (FATAL の原因として) はエスケープ表記とエントリ番号で表示する。
public class SafeDisplayTests
{
    [Theory]
    [InlineData("plain.txt", "plain.txt")]
    [InlineData("日本語/ファイル.txt", "日本語/ファイル.txt")]
    [InlineData("😀.txt", "😀.txt")]
    [InlineData("a\0b", "a\\u{0000}b")]
    [InlineData("a\u001Bb", "a\\u{001B}b")]           // ESC (端末制御)
    [InlineData("a\u007Fb", "a\\u{007F}b")]
    [InlineData("a\u0085b", "a\\u{0085}b")]           // C1 制御文字
    [InlineData("a\r\nb", "a\\u{000D}\\u{000A}b")]
    [InlineData("evil\u202Etxt.exe", "evil\\u{202E}txt.exe")]   // 双方向制御
    [InlineData("a\u200Bb", "a\\u{200B}b")]           // ゼロ幅空白
    [InlineData("a\u2028b", "a\\u{2028}b")]           // 行区切り
    [InlineData("a\uFFFDb", "a\\u{FFFD}b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a\"b", "a\\\"b")]
    public void O04_Escape(string name, string expected)
    {
        Assert.Equal(expected, SafeDisplay.Escape(name));
    }

    // 孤立サロゲートは属性の文字列に置けないため個別に確かめる。
    [Fact]
    public void O04_Escape_LoneSurrogates()
    {
        Assert.Equal("a\\u{D800}b", SafeDisplay.Escape("a" + (char)0xD800 + "b"));
        Assert.Equal("a\\u{DC00}", SafeDisplay.Escape("a" + (char)0xDC00));
        Assert.Equal("\\u{DC00}\\u{D800}", SafeDisplay.Escape(new string([(char)0xDC00, (char)0xD800])));
    }

    // O09・O10: 結果行の Entry・Target 用のエスケープ。\ と " は変換しない。書式文字・C1 制御・行・段落区切りだけを表し、
    // エスケープしたかどうかを返す (転記できない印に使う)。U+FFFD は ZIP 事前検証で拒否されるため対象外。
    [Theory]
    [InlineData("plain.txt", "plain.txt", false)]
    [InlineData(@"bin\a.dll", @"bin\a.dll", false)]
    [InlineData("a\"b", "a\"b", false)]
    [InlineData(" lead/日本語/café░.txt", " lead/日本語/café░.txt", false)]
    [InlineData("😀.txt", "😀.txt", false)]
    [InlineData("a\u0085b", "a\\u{0085}b", true)]
    [InlineData("a\u009Fb", "a\\u{009F}b", true)]
    [InlineData("evil\u202Etxt.exe", "evil\\u{202E}txt.exe", true)]
    [InlineData("a\u2066b\u2069", "a\\u{2066}b\\u{2069}", true)]
    [InlineData("a\u200Bb", "a\\u{200B}b", true)]
    [InlineData("a\u00ADb", "a\\u{00AD}b", true)]
    [InlineData("a\uFEFFb", "a\\u{FEFF}b", true)]
    [InlineData("a\u2028b\u2029", "a\\u{2028}b\\u{2029}", true)]
    [InlineData("a\u001Bb", "a\\u{001B}b", true)]
    public void O10_EscapeForList(string name, string expected, bool escaped)
    {
        Assert.Equal(expected, SafeDisplay.EscapeForList(name, out var actual));
        Assert.Equal(escaped, actual);
    }

    [Fact]
    public void O10_EscapeForList_LoneSurrogate()
    {
        Assert.Equal("a\\u{D800}b", SafeDisplay.EscapeForList("a" + (char)0xD800 + "b", out var escaped));
        Assert.True(escaped);
    }

    [Theory]
    [InlineData("a\U000E0001b", "a\\u{E0001}b", true)]
    [InlineData("a\U000E0061b", "a\\u{E0061}b", true)]
    [InlineData("a\U0001BCA0b", "a\\u{1BCA0}b", true)]
    [InlineData("a\U0001D173b", "a\\u{1D173}b", true)]
    [InlineData("a\U0001F600b", "a\U0001F600b", false)]
    [InlineData("a\U00020000b", "a\U00020000b", false)]
    [InlineData("a\U000E0100b", "a\U000E0100b", false)]
    [InlineData("\u200B\U0001F600\U000E0061\U00020000", "\\u{200B}\U0001F600\\u{E0061}\U00020000", true)]
    public void SupplementaryScalars_EscapeFormatOnly(string name, string expected, bool escaped)
    {
        Assert.Equal(expected, SafeDisplay.Escape(name));
        Assert.Equal(expected, SafeDisplay.EscapeForList(name, out var actual));
        Assert.Equal(escaped, actual);
        Assert.Equal($"MATCHED               {expected} -> C:\\target\\{expected}{(escaped ? ReportText.EscapedMark : string.Empty)}",
            ReportText.Line("MATCHED", name, $@"C:\target\{name}"));
    }

    [Fact]
    public void FatalDescription_EscapesSupplementaryFormat()
    {
        var fatal = new FatalError(FatalKind.ArchiveUnreadable, new ZipEntryRef(0, "a\U000E0061.txt"));
        Assert.Contains("エントリ #1 \"a\\u{E0061}.txt\"", fatal.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("\U000E0061", fatal.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void O04_FatalDescription_ShowsEntryNumberAndEscapedName()
    {
        var fatal = new FatalError(FatalKind.ControlCharacter, new ZipEntryRef(41, "x\u001B[2Jy"));

        Assert.Equal("エントリ #42 \"x\\u{001B}[2Jy\": 制御文字を含みます", fatal.Describe());
    }

    [Fact]
    public void FatalDescription_WithoutEntry_EscapesDetail()
    {
        var fatal = new FatalError(FatalKind.ArchiveUnreadable, Detail: "bad\u0007");

        Assert.Equal("ZIP として読み取れません (bad\\u{0007})", fatal.Describe());
    }
}
