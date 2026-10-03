using Unextract.Core.Results;
using static Unextract.Core.Tests.TestHelpers;

namespace Unextract.Core.Tests;

// docs/spec/zip.md#paths。入力は偽のエントリ一覧だけで、target を持たない (target 外へアクセスし得ない)。
public class EntryPathTests
{
    // テスト Z01: target の外へ出得る名前、絶対/ドライブ/UNC/デバイスパス、ADS のコロン
    [Theory]
    [InlineData("../a.txt", FatalKind.DotDotComponent)]
    [InlineData("..\\a.txt", FatalKind.DotDotComponent)]
    [InlineData("a/../../b.txt", FatalKind.DotDotComponent)]
    [InlineData("a/../b.txt", FatalKind.DotDotComponent)]
    [InlineData("a/..", FatalKind.DotDotComponent)]
    [InlineData("../", FatalKind.DotDotComponent)]
    [InlineData("/etc/passwd", FatalKind.RootedPath)]
    [InlineData("\\Windows\\a.txt", FatalKind.RootedPath)]
    [InlineData("\\\\server\\share\\a.txt", FatalKind.RootedPath)]
    [InlineData("//server/share/a.txt", FatalKind.RootedPath)]
    [InlineData("\\\\?\\C:\\a.txt", FatalKind.RootedPath)]
    [InlineData("\\\\.\\PhysicalDrive0", FatalKind.RootedPath)]
    [InlineData("/", FatalKind.RootedPath)]
    [InlineData("C:/a.txt", FatalKind.DriveSpecifier)]
    [InlineData("C:a.txt", FatalKind.DriveSpecifier)]
    [InlineData("c:\\a.txt", FatalKind.DriveSpecifier)]
    [InlineData("a.txt:Zone.Identifier", FatalKind.Colon)]
    [InlineData("a.txt::$DATA", FatalKind.Colon)]
    [InlineData("dir/a:b", FatalKind.Colon)]
    public void Z01_DangerousPaths_AreFatal(string name, FatalKind expected)
    {
        AssertFatal(ValidateNames(name), expected, 0);
    }

    // テスト Z02: Windows 不正名
    [Theory]
    [InlineData("./a.txt", FatalKind.DotComponent)]
    [InlineData("a/./b.txt", FatalKind.DotComponent)]
    [InlineData(".", FatalKind.DotComponent)]
    [InlineData("a//b.txt", FatalKind.EmptyComponent)]
    [InlineData("a\\/b.txt", FatalKind.EmptyComponent)]
    [InlineData("dir//", FatalKind.EmptyComponent)]                  // 末尾区切りは1個だけ
    [InlineData("", FatalKind.EmptyComponent)]
    [InlineData("a\0b.txt", FatalKind.ControlCharacter)]
    [InlineData("a\u0001.txt", FatalKind.ControlCharacter)]
    [InlineData("a\u001F.txt", FatalKind.ControlCharacter)]
    [InlineData("a\u007F.txt", FatalKind.ControlCharacter)]
    [InlineData("a\tb.txt", FatalKind.ControlCharacter)]
    [InlineData("a\nb.txt", FatalKind.ControlCharacter)]
    [InlineData("CON", FatalKind.ReservedName)]
    [InlineData("con.txt", FatalKind.ReservedName)]
    [InlineData("dir/Prn.tar.gz", FatalKind.ReservedName)]
    [InlineData("AUX", FatalKind.ReservedName)]
    [InlineData("nul.", FatalKind.TrailingDotOrSpace)]
    [InlineData("NUL .txt", FatalKind.ReservedName)]
    [InlineData("COM0", FatalKind.ReservedName)]
    [InlineData("com9.log", FatalKind.ReservedName)]
    [InlineData("LPT0.txt", FatalKind.ReservedName)]
    [InlineData("lpt9", FatalKind.ReservedName)]
    [InlineData("CON/a.txt", FatalKind.ReservedName)]
    [InlineData("aux/", FatalKind.ReservedName)]
    [InlineData("COM\u00B9", FatalKind.ReservedName)]
    [InlineData("com\u00B2.txt", FatalKind.ReservedName)]
    [InlineData("COM\u00B3/a.txt", FatalKind.ReservedName)]
    [InlineData("LPT\u00B9.log", FatalKind.ReservedName)]
    [InlineData("lpt\u00B2", FatalKind.ReservedName)]
    [InlineData("LPT\u00B3 .txt", FatalKind.ReservedName)]
    [InlineData("CONIN$", FatalKind.ReservedName)]
    [InlineData("conout$.txt", FatalKind.ReservedName)]
    [InlineData("dir/CONOUT$/", FatalKind.ReservedName)]
    [InlineData("a.txt.", FatalKind.TrailingDotOrSpace)]
    [InlineData("a.txt ", FatalKind.TrailingDotOrSpace)]
    [InlineData("dir./a.txt", FatalKind.TrailingDotOrSpace)]
    [InlineData("dir /a.txt", FatalKind.TrailingDotOrSpace)]
    [InlineData("...", FatalKind.TrailingDotOrSpace)]
    [InlineData("a<b", FatalKind.InvalidCharacter)]
    [InlineData("a>b", FatalKind.InvalidCharacter)]
    [InlineData("a\"b", FatalKind.InvalidCharacter)]
    [InlineData("a|b", FatalKind.InvalidCharacter)]
    [InlineData("a?b", FatalKind.InvalidCharacter)]
    [InlineData("a*b", FatalKind.InvalidCharacter)]
    public void Z02_InvalidWindowsNames_AreFatal(string name, FatalKind expected)
    {
        AssertFatal(ValidateNames(name), expected, 0);
    }

    // テスト Z02: FATAL の原因の名前は安全な表記で表示する (テスト O04 と同じエスケープ)
    [Fact]
    public void Z02_FatalName_IsDisplayedEscaped()
    {
        var fatal = ValidateNames("ok.txt", "a\u0001b\u202E.txt").Fatal!;

        Assert.Equal("a\\u{0001}b\\u{202E}.txt", fatal.Entry!.DisplayName);
        Assert.Equal("エントリ #2 \"a\\u{0001}b\\u{202E}.txt\": 制御文字を含みます", fatal.Describe());
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("dir/")]
    [InlineData("dir\\")]
    [InlineData("dir/sub/a.txt")]
    [InlineData("dir\\sub\\a.txt")]
    [InlineData(".hidden")]
    [InlineData("a..b.txt")]
    [InlineData(" leading space.txt")]
    [InlineData("CONSOLE.txt")]
    [InlineData("COM10")]
    [InlineData("LPT")]
    [InlineData("xCON")]
    [InlineData("COM\u2074")]                                     // 上付き 4 は予約名ではない
    [InlineData("CONIN")]
    [InlineData("CONOUT$x.txt")]
    [InlineData("日本語/ファイル.txt")]
    [InlineData("café░.txt")]
    public void ValidNames_Pass(string name)
    {
        AssertPassed(ValidateNames(name), 1);
    }

    [Fact]
    public void Components_UseBothSeparators_AndDropDirectoryTrailingSeparator()
    {
        var result = ValidateNames("a\\b/c.txt", "d/e\\");

        AssertPassed(result, 2);
        Assert.Equal(new[] { "a", "b", "c.txt" }, result.Entries[0].Components);
        Assert.False(result.Entries[0].IsDirectory);
        Assert.Equal(new[] { "d", "e" }, result.Entries[1].Components);
        Assert.True(result.Entries[1].IsDirectory);
    }

    // docs/spec/zip.md#decoding: 復号後の名前に U+FFFD を含めば全体 FATAL (実 ZIP での確認はテスト Z07)
    [Fact]
    public void ReplacementCharacter_IsFatal()
    {
        AssertFatal(ValidateNames("ok.txt", "a\uFFFD.txt"), FatalKind.NameContainsReplacementCharacter, 1);
    }
}
