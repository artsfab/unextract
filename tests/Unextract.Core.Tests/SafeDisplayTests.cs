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
