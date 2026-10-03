using System.Text;
using Unextract.Core.Entries;
using Unextract.Core.Zip;
using static Unextract.Core.Tests.Fixtures.FakeEntries;

namespace Unextract.Core.Tests;

// --entries の形式と照合 (SPEC §3.3、§11、テスト L01〜L09・L14・L15 の純粋関数の部分)。
// Runner を通した削除0件・target に触れないことは DeleteRunnerTests の L 系で確かめる。
public class EntriesListTests
{
    private static readonly IReadOnlyList<ValidatedZipEntry> Zip = Validate(
        "bin/a.dll", "bin/b.dll", "docs/", "docs/readme.txt", " lead.txt", "#comment", @"win\style.txt", "日本語.txt");

    private static IReadOnlyList<ValidatedZipEntry> Validate(params string[] names)
    {
        var result = ZipPrevalidator.Validate(names.Select((n, i) => Entry(i, n)), Limits.Default);
        Assert.True(result.Passed, result.Fatal?.Describe());
        return result.Entries;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static EntriesParseResult Parse(byte[] data, Limits? limits = null) => EntriesList.Parse(data, limits ?? Limits.Default);

    private static IReadOnlyList<string> Names(EntriesParseResult result)
    {
        Assert.Null(result.Error);
        return result.Lines!.Select(l => l.Name).ToList();
    }

    private static EntriesError Error(EntriesParseResult result)
    {
        Assert.Null(result.Lines);
        return Assert.IsType<EntriesError>(result.Error);
    }

    // L01: LF、CRLF、混在、末尾改行あり・なし、先頭に UTF-8 BOM 1個 → 全て同じ選択集合。
    [Theory]
    [InlineData("bin/a.dll\nbin/b.dll\n")]
    [InlineData("bin/a.dll\nbin/b.dll")]
    [InlineData("bin/a.dll\r\nbin/b.dll\r\n")]
    [InlineData("bin/a.dll\r\nbin/b.dll")]
    [InlineData("bin/a.dll\nbin/b.dll\r\n")]
    [InlineData("bin/a.dll\r\nbin/b.dll\n")]
    public void L01_LineEndingsAndBom(string text)
    {
        Assert.Equal(["bin/a.dll", "bin/b.dll"], Names(Parse(Utf8(text))));
        Assert.Equal(["bin/a.dll", "bin/b.dll"], Names(Parse([0xEF, 0xBB, 0xBF, .. Utf8(text)])));

        var lines = Parse(Utf8(text)).Lines!;
        Assert.Equal([1, 2], lines.Select(l => l.LineNumber));
        var matched = EntriesList.Match(lines, Zip);
        Assert.Null(matched.Error);
        Assert.Equal(["bin/a.dll", "bin/b.dll"], matched.Selected!.Select(e => e.Entry.FullName));
    }

    // L02: 不正な UTF-8、UTF-16LE/BE・UTF-32 の BOM → 入力エラー。UTF-16 は UTF-8 で保存するよう案内する。
    [Fact]
    public void L02_InvalidUtf8_IsInputErrorWithLineNumber()
    {
        var error = Error(Parse([.. Utf8("bin/a.dll\n"), 0x62, 0xC3, 0x28, 0x0A]));

        Assert.Equal(2, error.LineNumber);
        Assert.Equal("--entries の 2 行目: UTF-8 として読めません", error.Describe());
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }, "UTF-16 で保存されています。UTF-8 で保存してください")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x61 }, "UTF-16 で保存されています。UTF-8 で保存してください")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00 }, "UTF-32 で保存されています。UTF-8 で保存してください")]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x61 }, "UTF-32 で保存されています。UTF-8 で保存してください")]
    public void L02_Utf16And32Bom_AreInputErrors(byte[] data, string reason)
    {
        var error = Error(Parse(data));

        Assert.Null(error.LineNumber);
        Assert.Equal(reason, error.Reason);
    }

    // L02: UTF-16LE で保存した実際のファイル (BOM 付き) も同じ案内になる。
    [Fact]
    public void L02_Utf16LeFile_IsInputError()
    {
        var data = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("bin/a.dll\r\n")).ToArray();

        Assert.Equal("UTF-16 で保存されています。UTF-8 で保存してください", Error(Parse(data)).Reason);
    }

    // L02: UTF-8 BOM が2個 → 2個目は名前の一部 (U+FEFF) になり、照合で未知のエントリになる。
    [Fact]
    public void L02_SecondUtf8Bom_BecomesPartOfName()
    {
        var parsed = Parse([0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, .. Utf8("bin/a.dll\n")]);

        var line = Assert.Single(parsed.Lines!);
        Assert.Equal("\uFEFFbin/a.dll", line.Name);
        var matched = EntriesList.Match(parsed.Lines!, Zip);
        Assert.Equal(1, matched.Error!.LineNumber);
        Assert.StartsWith("ZIP に一致するエントリがありません", matched.Error.Reason, StringComparison.Ordinal);
    }

    // L03: 空行 (途中、先頭、末尾の余分な空行)、空ファイル、BOM だけのファイル → 入力エラー (空行は行番号付き)。
    [Theory]
    [InlineData("bin/a.dll\n\nbin/b.dll\n", 2)]
    [InlineData("\nbin/a.dll\n", 1)]
    [InlineData("bin/a.dll\n\n", 2)]
    [InlineData("bin/a.dll\r\n\r\n", 2)]
    [InlineData("bin/a.dll\n\r\n", 2)]
    public void L03_EmptyLine_IsInputError(string text, int line)
    {
        var error = Error(Parse(Utf8(text)));

        Assert.Equal(line, error.LineNumber);
        Assert.Equal("空行です", error.Reason);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF })]
    public void L03_NoLines_IsInputError(byte[] data)
    {
        var error = Error(Parse(data));

        Assert.Null(error.LineNumber);
        Assert.Equal("--entries: 行がありません (空のファイルです)", error.Describe());
    }

    // L04: 行末以外の CR は入力エラー。前後の空白は trim せず名前の一部として照合する。
    [Theory]
    [InlineData("bin/a.dll\rbin/b.dll\n", 1)]
    [InlineData("bin/a.dll\n\rbin/b.dll\n", 2)]
    [InlineData("bin/a.dll\r\r\n", 1)]
    public void L04_CarriageReturnNotAtLineEnd_IsInputError(string text, int line)
    {
        var error = Error(Parse(Utf8(text)));

        Assert.Equal(line, error.LineNumber);
        Assert.Equal("行末以外に CR があります", error.Reason);
    }

    [Fact]
    public void L04_SpacesAreNotTrimmed()
    {
        var lead = EntriesList.Match(Parse(Utf8(" lead.txt\n")).Lines!, Zip);
        Assert.Equal([" lead.txt"], lead.Selected!.Select(e => e.Entry.FullName));

        var trailing = EntriesList.Match(Parse(Utf8("bin/a.dll \n")).Lines!, Zip);
        Assert.StartsWith("ZIP に一致するエントリがありません (\"bin/a.dll \")", trailing.Error!.Reason, StringComparison.Ordinal);

        var trimmed = EntriesList.Match(Parse(Utf8("lead.txt\n")).Lines!, Zip);
        Assert.NotNull(trimmed.Error);
    }

    // L05: 重複行 → 入力エラー (両方の行番号)。
    [Fact]
    public void L05_DuplicateLine_IsInputError()
    {
        var error = Error(Parse(Utf8("bin/a.dll\nbin/b.dll\r\nbin/a.dll\n")));

        Assert.Equal("--entries の 3 行目: 1 行目と同じです (\"bin/a.dll\")", error.Describe());
    }

    // L06: ZIP に無い名前、大小文字だけ違う名前、区切りだけ違う名前 → 入力エラー。後の2つは正しい FullName をヒントに出す。
    [Theory]
    [InlineData("nothing.txt", "ZIP に一致するエントリがありません (\"nothing.txt\")")]
    [InlineData("Bin/a.dll", "ZIP に一致するエントリがありません (\"Bin/a.dll\")。大小文字だけが違うエントリ \"bin/a.dll\" があります")]
    [InlineData(@"bin\a.dll", @"ZIP に一致するエントリがありません (""bin\a.dll"")。区切りだけが違うエントリ ""bin/a.dll"" があります")]
    [InlineData(@"BIN\A.dll", @"ZIP に一致するエントリがありません (""BIN\A.dll"")。大小文字と区切りだけが違うエントリ ""bin/a.dll"" があります")]
    [InlineData("win/style.txt", @"ZIP に一致するエントリがありません (""win/style.txt"")。区切りだけが違うエントリ ""win\style.txt"" があります")]
    public void L06_UnknownEntry_IsInputErrorWithHint(string line, string reason)
    {
        var matched = EntriesList.Match(Parse(Utf8($"bin/b.dll\n{line}\n")).Lines!, Zip);

        Assert.Null(matched.Selected);
        Assert.Equal(2, matched.Error!.LineNumber);
        Assert.Equal(reason, matched.Error.Reason);
    }

    // L07: ディレクトリエントリ → 入力エラー。明示エントリの無い暗黙ディレクトリの名前 → 未知のエントリ。
    [Fact]
    public void L07_DirectoryEntry_IsInputError()
    {
        var explicitDirectory = EntriesList.Match(Parse(Utf8("docs/\n")).Lines!, Zip);
        Assert.Equal("ディレクトリエントリは指定できません (\"docs/\")", explicitDirectory.Error!.Reason);

        var implicitDirectory = EntriesList.Match(Parse(Utf8("bin\n")).Lines!, Zip);
        Assert.Equal("ZIP に一致するエントリがありません (\"bin\")", implicitDirectory.Error!.Reason);

        var withoutSeparator = EntriesList.Match(Parse(Utf8("docs\n")).Lines!, Zip);
        Assert.StartsWith("ZIP に一致するエントリがありません", withoutSeparator.Error!.Reason, StringComparison.Ordinal);
    }

    // L08: # で始まる行、* を含む行はコメント・glob として扱わず名前として照合する (* は ZIP の名前に使えないため常に未知)。
    [Fact]
    public void L08_NoCommentOrGlob()
    {
        var literal = EntriesList.Match(Parse(Utf8("#comment\n")).Lines!, Zip);
        Assert.Equal(["#comment"], literal.Selected!.Select(e => e.Entry.FullName));

        var glob = EntriesList.Match(Parse(Utf8("bin/*\n")).Lines!, Zip);
        Assert.Equal("ZIP に一致するエントリがありません (\"bin/*\")", glob.Error!.Reason);

        var comment = EntriesList.Match(Parse(Utf8("# bin/a.dll\n")).Lines!, Zip);
        Assert.NotNull(comment.Error);
    }

    // L09: 上限 (既定値)。1行 4,096 / 4,097 バイト、行数 100,000 / 100,001。
    [Theory]
    [InlineData(4_096, true)]
    [InlineData(4_097, false)]
    public void L09_LineLength_DefaultLimit(int bytes, bool passes)
    {
        // 3 バイト文字で UTF-8 のバイト数を数えることを確かめる (末尾は 1 バイト文字で調整)。
        var name = new string('あ', bytes / 3) + new string('x', bytes % 3);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(name));

        var result = Parse(Utf8(name + "\r\n"));

        if (passes)
        {
            Assert.Equal([name], Names(result));
        }
        else
        {
            var error = Error(result);
            Assert.Equal(1, error.LineNumber);
            Assert.Equal("1行の長さが上限 (4,096 バイト) を超えています", error.Reason);
        }
    }

    [Theory]
    [InlineData(100_000, true)]
    [InlineData(100_001, false)]
    public void L09_LineCount_DefaultLimit(int count, bool passes)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append("f").Append(i).Append('\n');
        }

        var result = Parse(Utf8(text.ToString()));

        if (passes)
        {
            Assert.Equal(count, result.Lines!.Count);
        }
        else
        {
            var error = Error(result);
            Assert.Equal(100_001, error.LineNumber);
            Assert.Equal("行数が上限 (100,000 行) を超えています", error.Reason);
        }
    }

    // L09: 上限を小さくした注入。ファイルサイズ ちょうど / +1 バイト、1行、行数。
    [Theory]
    [InlineData(10, true)]
    [InlineData(9, false)]
    public void L09_FileSize_InjectedLimit(long limit, bool passes)
    {
        var limits = Limits.Default with { MaxEntriesFileBytes = limit };
        var data = Utf8("a.txt\nb.tx");

        var result = Parse(data, limits);

        if (passes)
        {
            Assert.Equal(["a.txt", "b.tx"], Names(result));
        }
        else
        {
            Assert.Equal($"ファイルが大きさの上限 ({limit:N0} バイト) を超えています", Error(result).Reason);
        }
    }

    [Fact]
    public void L09_LineAndCount_InjectedLimits()
    {
        var limits = Limits.Default with { MaxEntriesLineBytes = 3, MaxEntriesLines = 2 };

        Assert.Equal(["abc", "de"], Names(Parse(Utf8("abc\r\nde"), limits)));
        Assert.Equal(1, Error(Parse(Utf8("abcd\n"), limits)).LineNumber);
        Assert.Equal(3, Error(Parse(Utf8("a\nb\nc\n"), limits)).LineNumber);
    }

    // L09: 上限を超えるファイルは全体を読まない (長さが分かれば読み始めない。分からなくても上限 + 1 バイトで止める)。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void L09_TooLargeFile_IsNotReadEntirely(bool seekable)
    {
        var limits = Limits.Default with { MaxEntriesFileBytes = 100 };
        using var stream = new CountingStream(new byte[100_000], seekable);

        var result = EntriesList.Read(stream, limits);

        Assert.StartsWith("ファイルが大きさの上限", Error(result).Reason, StringComparison.Ordinal);
        Assert.True(stream.TotalRead <= 101, $"read {stream.TotalRead}");
        if (seekable)
        {
            Assert.Equal(0, stream.TotalRead);
        }
    }

    // L17 (Core): 存在しない・ディレクトリ → 入力エラー (ファイル全体の問題で行番号なし)。
    [Fact]
    public void L17_UnreadableFile_IsInputError()
    {
        var missing = EntriesList.Read(Path.Combine(Fixtures.TestFiles.Directory, $"missing-{Guid.NewGuid():N}.txt"), Limits.Default);
        Assert.StartsWith("--entries: ファイルを読めません", Error(missing).Describe(), StringComparison.Ordinal);

        var directory = EntriesList.Read(Fixtures.TestFiles.Directory, Limits.Default);
        Assert.StartsWith("--entries: ファイルを読めません", Error(directory).Describe(), StringComparison.Ordinal);
    }

    // L14 (Core): analyze の表示の Entry (日本語名、\ 区切り、先頭空白) をそのまま書けば一致する。
    [Fact]
    public void L14_DisplayedEntryMatchesVerbatim()
    {
        var parsed = Parse(Utf8("日本語.txt\n" + @"win\style.txt" + "\n lead.txt\n"));

        var matched = EntriesList.Match(parsed.Lines!, Zip);

        Assert.Equal([" lead.txt", @"win\style.txt", "日本語.txt"], matched.Selected!.Select(e => e.Entry.FullName));
    }

    // L15 (Core): 書式文字を含むエントリ名は、実際の文字を UTF-8 で書いた entries で一致する。
    [Fact]
    public void L15_FormatCharacterMatchesWhenWrittenAsActualCharacter()
    {
        var zip = Validate("a\u200Bb.txt", "c.txt");

        var matched = EntriesList.Match(Parse(Utf8("a\u200Bb.txt\n")).Lines!, zip);
        Assert.Equal(["a\u200Bb.txt"], matched.Selected!.Select(e => e.Entry.FullName));

        var escaped = EntriesList.Match(Parse(Utf8("a\\u{200B}b.txt\n")).Lines!, zip);
        Assert.NotNull(escaped.Error);
    }

    // 選ばれたエントリは ZIP の順 (entries の行の順ではない)。
    [Fact]
    public void Selected_IsInZipOrder()
    {
        var matched = EntriesList.Match(Parse(Utf8("docs/readme.txt\nbin/a.dll\n")).Lines!, Zip);

        Assert.Equal(["bin/a.dll", "docs/readme.txt"], matched.Selected!.Select(e => e.Entry.FullName));
    }

    private sealed class CountingStream(byte[] data, bool seekable) : MemoryStream(data)
    {
        public long TotalRead { get; private set; }

        public override bool CanSeek => seekable;

        public override long Length => seekable ? base.Length : throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            TotalRead += n;
            return n;
        }
    }
}
