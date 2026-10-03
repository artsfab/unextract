using Unextract.Core.Analysis;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;
using Unextract.Core.Zip;
using static Unextract.Core.Tests.Fakes.PipelineHarness;

namespace Unextract.Core.Tests;

// target 側の分類 (docs/spec/filesystem.md#classification、docs/spec/filesystem.md#special-files) を偽ファイルシステム上で確認する。ZIP は実際の ZipArchive で読む。
public class ClassificationTests
{
    private static readonly byte[] Hello = Bytes("hello");
    private static readonly byte[] World = Bytes("world!");

    // 共通の安全性テストを両モードで実行する (docs/TESTING.md#principles のモード適用)。削除候補の期待は Candidate(mode) で読み替える。
    public static TheoryData<RunMode> BothModes => new() { RunMode.Strict, RunMode.Fast };

    private static Classification Single(AnalysisResult result)
    {
        Assert.Null(result.Fatal);
        return Assert.Single(result.Results).Classification;
    }

    // T01: 同一内容 → MATCHED、1 byte 変更 → MODIFIED、サイズ違い → MODIFIED (ZIP 内容を読まない)、0 byte → MATCHED。
    // mtime の違いは内容一致を覆さない。
    [Fact]
    public void T01_ContentComparison()
    {
        var zip = MakeZip(("same.txt", Hello), ("changed.txt", Hello), ("size.txt", Hello), ("zero.txt", []));
        using var harness = new PipelineHarness(zip);
        harness.Fs.AddFile(@"C:\target\same.txt", Bytes("hello")).LastWriteTime = 1;
        harness.Fs.AddFile(@"C:\target\changed.txt", Bytes("hellO"));
        harness.Fs.AddFile(@"C:\target\size.txt", Bytes("hello, world"));
        harness.Fs.AddFile(@"C:\target\zero.txt", []);

        var result = harness.Run();

        Assert.Null(result.Fatal);
        Assert.Equal(
            [Classification.Matched, Classification.Modified, Classification.Modified, Classification.Matched],
            result.Results.Select(r => r.Classification));
        Assert.False(harness.Contents.Touched(2));
        Assert.Equal(@"\\?\C:\target\same.txt", result.Results[0].Target);
    }

    // T17: T01 と同じ4つを --fast で → SAME_SIZE、SAME_SIZE、MODIFIED (ZIP 内容を読まない)、SAME_SIZE。
    // 同一内容と1 byte 変更を区別しない。0 byte に特例はない (docs/SPEC.md#modes)。
    // ZIP エントリの GetContent・Open()・Crc32 は呼び出し記録に残らない。target の内容の読み取りは、読めば例外になるよう注入して確かめる。
    [Fact]
    public void T17_Fast_SizeOnlyWithoutReadingContent()
    {
        var zip = MakeZip(("same.txt", Hello), ("changed.txt", Hello), ("size.txt", Hello), ("zero.txt", []));
        using var harness = new PipelineHarness(zip) { Mode = RunMode.Fast };
        FakeNode[] nodes =
        [
            harness.Fs.AddFile(@"C:\target\same.txt", Bytes("hello")),
            harness.Fs.AddFile(@"C:\target\changed.txt", Bytes("hellO")),
            harness.Fs.AddFile(@"C:\target\size.txt", Bytes("hello, world")),
            harness.Fs.AddFile(@"C:\target\zero.txt", []),
        ];
        foreach (var node in nodes)
        {
            node.ThrowOnRead = true;
        }

        var result = harness.Run();

        Assert.Null(result.Fatal);
        Assert.Equal(
            [Classification.SameSize, Classification.SameSize, Classification.Modified, Classification.SameSize],
            result.Results.Select(r => r.Classification));
        Assert.Empty(harness.Contents.Calls);
    }

    // T02: 親成分の分類表の MISSING の行 (存在しない、大小文字だけ違うディレクトリ、通常ファイル)。ZIP 内容は開かない。
    [Theory]
    [InlineData("none", RunMode.Strict)]
    [InlineData("a-only", RunMode.Strict)]
    [InlineData("case-a", RunMode.Strict)]
    [InlineData("case-b", RunMode.Strict)]
    [InlineData("file-a", RunMode.Strict)]
    [InlineData("file-b", RunMode.Strict)]
    [InlineData("none", RunMode.Fast)]
    [InlineData("a-only", RunMode.Fast)]
    [InlineData("case-a", RunMode.Fast)]
    [InlineData("case-b", RunMode.Fast)]
    [InlineData("file-a", RunMode.Fast)]
    [InlineData("file-b", RunMode.Fast)]
    public void T02_ParentMissingRows_AreMissing(string layout, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("a/b/c.txt", Hello))) { Mode = mode };
        var fs = harness.Fs;
        switch (layout)
        {
            case "a-only":
                fs.AddDirectory(@"C:\target\a");
                break;
            case "case-a":
                fs.AddDirectory(@"C:\target\A");
                fs.AddDirectory(@"C:\target\A\b");
                fs.AddFile(@"C:\target\A\b\c.txt", Bytes("hello"));
                break;
            case "case-b":
                fs.AddDirectory(@"C:\target\a");
                fs.AddDirectory(@"C:\target\a\B");
                fs.AddFile(@"C:\target\a\B\c.txt", Bytes("hello"));
                break;
            case "file-a":
                fs.AddFile(@"C:\target\a", Bytes("file"));
                break;
            case "file-b":
                fs.AddDirectory(@"C:\target\a");
                fs.AddFile(@"C:\target\a\b", Bytes("file"));
                break;
        }

        var result = harness.Run();

        Assert.Equal(Classification.Missing, Single(result));
        Assert.False(harness.Contents.Touched(0));
        Assert.Equal(0, fs.ComparisonOpenCount);
    }

    // T03: 親成分の分類表の reparse の行 (ディレクトリ junction、ディレクトリ symlink、ファイル symlink)。
    // reparse の判定は他の種類より優先し、リンク先を読まない。
    [Theory]
    [InlineData("junction", RunMode.Strict)]
    [InlineData("dir-symlink", RunMode.Strict)]
    [InlineData("file-symlink", RunMode.Strict)]
    [InlineData("junction", RunMode.Fast)]
    [InlineData("dir-symlink", RunMode.Fast)]
    [InlineData("file-symlink", RunMode.Fast)]
    public void T03_ParentReparse_IsSkipped(string kind, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("a/b/c.txt", Hello))) { Mode = mode };
        var fs = harness.Fs;
        var elsewhere = fs.AddDirectory(@"C:\elsewhere");
        fs.AddFile(@"C:\elsewhere\c.txt", Bytes("hello"));
        fs.AddDirectory(@"C:\target\a");
        switch (kind)
        {
            case "junction":
                fs.AddJunction(@"C:\target\a\b", elsewhere);
                break;
            case "dir-symlink":
                fs.AddReparse(@"C:\target\a\b", isDirectory: true, FakeNode.ReparseTagSymlink, elsewhere);
                break;
            case "file-symlink":
                fs.AddReparse(@"C:\target\a\b", isDirectory: false, FakeNode.ReparseTagSymlink);
                break;
        }

        var result = harness.Run();

        Assert.Equal(Classification.SkippedSpecialFile, Single(result));
        Assert.Equal(SkipReason.ParentReparsePoint, result.Results[0].SkipReason);
        // reparse の先 (elsewhere) にも、reparse 自身の下 (a\b) にも触れない。
        Assert.DoesNotContain(fs.Calls, c => c.Contains("elsewhere", StringComparison.Ordinal));
        Assert.DoesNotContain(fs.Calls, c => c.Contains(@"\a\b", StringComparison.Ordinal));
        Assert.Equal(0, fs.ComparisonOpenCount);
        Assert.False(harness.Contents.Touched(0));
    }

    // T04: 親成分の分類表の判定不能の行 (列挙 API の失敗、想定外の種類) → 全体 FATAL
    [Theory]
    [InlineData("enumerate-root", FatalKind.EnumerationFailed, RunMode.Strict)]
    [InlineData("enumerate-a", FatalKind.EnumerationFailed, RunMode.Strict)]
    [InlineData("device", FatalKind.UnexpectedTargetType, RunMode.Strict)]
    [InlineData("enumerate-root", FatalKind.EnumerationFailed, RunMode.Fast)]
    [InlineData("enumerate-a", FatalKind.EnumerationFailed, RunMode.Fast)]
    [InlineData("device", FatalKind.UnexpectedTargetType, RunMode.Fast)]
    public void T04_ParentUndeterminable_IsFatal(string injection, FatalKind expected, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("first.txt", Hello), ("a/b/c.txt", Hello))) { Mode = mode };
        var fs = harness.Fs;
        fs.AddFile(@"C:\target\first.txt", Bytes("hello"));
        var a = fs.AddDirectory(@"C:\target\a");
        switch (injection)
        {
            case "enumerate-root":
                fs.Get(@"C:\target").Errors[FakeOp.Enumerate] = 1117;
                fs.Get(@"C:\target").EnumerationFailAfter = 1;
                break;
            case "enumerate-a":
                a.Errors[FakeOp.Enumerate] = 1117;
                break;
            case "device":
                fs.AddDirectory(@"C:\target\a\b", attributes: 0x10 | 0x40);
                break;
        }

        var result = harness.Run();

        Assert.Equal(expected, result.Fatal?.Kind);
        var fatalEntry = injection == "enumerate-root" ? "first.txt" : "a/b/c.txt";
        Assert.Equal(fatalEntry, result.Fatal!.Entry!.Name);
    }

    // T05 (保持しない部分): ZIP にない target ファイルが同じディレクトリに多数あっても、照合結果に保持されない
    [Theory]
    [MemberData(nameof(BothModes))]
    public void T05_UnrelatedNames_AreNotRetained(RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("dir/keep.txt", Hello), ("dir/gone.txt", Hello), ("top.txt", Hello))) { Mode = mode };
        var fs = harness.Fs;
        fs.AddDirectory(@"C:\target\dir");
        fs.AddFile(@"C:\target\dir\keep.txt", Bytes("hello"));
        for (var i = 0; i < 500; i++)
        {
            fs.AddFile($@"C:\target\dir\unrelated-{i}.txt", Bytes("x"));
            fs.AddFile($@"C:\target\other-{i}.txt", Bytes("x"));
        }

        var result = harness.Run();

        Assert.Equal(
            [Candidate(mode), Classification.Missing, Classification.Missing],
            result.Results.Select(r => r.Classification));
        Assert.Equal(["dir", "keep.txt"], harness.LastRun!.Resolver.RetainedNames.Order(StringComparer.Ordinal));
        var report = string.Join('\n', AnalyzeOutput.Format(result, mode));
        Assert.DoesNotContain("unrelated", report, StringComparison.Ordinal);
        Assert.DoesNotContain("other-", report, StringComparison.Ordinal);
    }

    // docs/spec/filesystem.md#real-names の 6: 同じディレクトリは1回だけ列挙する (ディレクトリごとの成分集合を先に集める)
    [Fact]
    public void RealNameCheck_EnumeratesEachDirectoryOnce()
    {
        using var harness = new PipelineHarness(MakeZip(("d/1.txt", Hello), ("x.txt", Hello), ("d/2.txt", Hello), ("d/e/3.txt", Hello), ("d/3.txt", Hello)));
        var fs = harness.Fs;
        fs.AddDirectory(@"C:\target\d");
        fs.AddDirectory(@"C:\target\d\e");

        harness.Run();

        Assert.Single(fs.Calls, c => c == @"Enumerate \\?\C:\target");
        Assert.Single(fs.Calls, c => c == @"Enumerate \\?\C:\target\d");
        Assert.Single(fs.Calls, c => c == @"Enumerate \\?\C:\target\d\e");
        Assert.Equal(
            ["", "d", @"d\e"],
            harness.LastRun!.Resolver.EnumeratedDirectories.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WantedNames_AreCollectedPerDirectoryFromFileEntries()
    {
        var entries = new[]
        {
            new ValidatedZipEntry(Fixtures.FakeEntries.Entry(0, "a/b.txt"), false, ["a", "b.txt"]),
            new ValidatedZipEntry(Fixtures.FakeEntries.Entry(1, "a/c/"), true, ["a", "c"]),
            new ValidatedZipEntry(Fixtures.FakeEntries.Entry(2, "a/c/d.txt"), false, ["a", "c", "d.txt"]),
            new ValidatedZipEntry(Fixtures.FakeEntries.Entry(3, "e.txt"), false, ["e.txt"]),
        };

        var wanted = RealNameResolver.CollectWantedNames(entries);

        Assert.Equal(["a", "e.txt"], wanted[""].Order(StringComparer.Ordinal));
        Assert.Equal(["b.txt", "c"], wanted["a"].Order(StringComparer.Ordinal));
        Assert.Equal(["d.txt"], wanted[@"a\c"]);
        Assert.Equal(3, wanted.Count);
    }

    // T07 (偽 FS 部分): ADS (Zone.Identifier)、hardlink、ファイル symlink、read-only、system、temporary、offline、許可集合外の属性
    // → SKIPPED_SPECIAL_FILE、内容を読まない
    [Theory]
    [InlineData("ads", SkipReason.AlternateDataStream, RunMode.Strict)]
    [InlineData("hardlink", SkipReason.HardLink, RunMode.Strict)]
    [InlineData("symlink", SkipReason.ReparsePoint, RunMode.Strict)]
    [InlineData("readonly", SkipReason.Attributes, RunMode.Strict)]
    [InlineData("system", SkipReason.Attributes, RunMode.Strict)]
    [InlineData("temporary", SkipReason.Attributes, RunMode.Strict)]
    [InlineData("offline", SkipReason.Attributes, RunMode.Strict)]
    [InlineData("recall", SkipReason.Attributes, RunMode.Strict)]
    [InlineData("undefined", SkipReason.Attributes, RunMode.Strict)]
    [InlineData("ads", SkipReason.AlternateDataStream, RunMode.Fast)]
    [InlineData("hardlink", SkipReason.HardLink, RunMode.Fast)]
    [InlineData("symlink", SkipReason.ReparsePoint, RunMode.Fast)]
    [InlineData("readonly", SkipReason.Attributes, RunMode.Fast)]
    [InlineData("system", SkipReason.Attributes, RunMode.Fast)]
    [InlineData("temporary", SkipReason.Attributes, RunMode.Fast)]
    [InlineData("offline", SkipReason.Attributes, RunMode.Fast)]
    [InlineData("recall", SkipReason.Attributes, RunMode.Fast)]
    [InlineData("undefined", SkipReason.Attributes, RunMode.Fast)]
    public void T07_SpecialFile_IsSkipped(string kind, SkipReason reason, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("x.txt", Hello))) { Mode = mode };
        var fs = harness.Fs;
        if (kind == "symlink")
        {
            fs.AddReparse(@"C:\target\x.txt", isDirectory: false, FakeNode.ReparseTagSymlink).Content = Bytes("hello");
        }
        else
        {
            var node = fs.AddFile(@"C:\target\x.txt", Bytes("hello"));
            switch (kind)
            {
                case "ads":
                    node.ExtraStreams.Add(new StreamEntry(":Zone.Identifier:$DATA", 24));
                    break;
                case "hardlink":
                    node.Links = 2;
                    break;
                case "readonly":
                    node.Attributes |= 0x1;
                    break;
                case "system":
                    node.Attributes |= 0x4;
                    break;
                case "temporary":
                    node.Attributes |= 0x100;
                    break;
                case "offline":
                    node.Attributes |= 0x1000;
                    break;
                case "recall":
                    node.Attributes |= 0x400000;
                    break;
                case "undefined":
                    node.Attributes |= 0x8;
                    break;
            }
        }

        var result = harness.Run();

        Assert.Equal(Classification.SkippedSpecialFile, Single(result));
        Assert.Equal(reason, result.Results[0].SkipReason);
        Assert.False(harness.Contents.Touched(0));
    }

    // T08 (偽 FS 部分): archive、hidden、not-content-indexed、NTFS 圧縮、sparse、EFS 暗号化の各属性だけ → MATCHED (Fast は SAME_SIZE)
    [Theory]
    [InlineData(0x20u, RunMode.Strict)]
    [InlineData(0x80u, RunMode.Strict)]
    [InlineData(0x2u, RunMode.Strict)]
    [InlineData(0x2000u, RunMode.Strict)]
    [InlineData(0x800u, RunMode.Strict)]
    [InlineData(0x200u, RunMode.Strict)]
    [InlineData(0x4000u, RunMode.Strict)]
    [InlineData(0x6AA2u, RunMode.Strict)]
    [InlineData(0x20u, RunMode.Fast)]
    [InlineData(0x80u, RunMode.Fast)]
    [InlineData(0x2u, RunMode.Fast)]
    [InlineData(0x2000u, RunMode.Fast)]
    [InlineData(0x800u, RunMode.Fast)]
    [InlineData(0x200u, RunMode.Fast)]
    [InlineData(0x4000u, RunMode.Fast)]
    [InlineData(0x6AA2u, RunMode.Fast)]
    public void T08_AllowedAttributes_AreMatched(uint attributes, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("x.txt", Hello))) { Mode = mode };
        harness.Fs.AddFile(@"C:\target\x.txt", Bytes("hello"), attributes);

        Assert.Equal(Candidate(mode), Single(harness.Run()));
    }

    // T09: ZIP にファイルがあり target はディレクトリ → SKIPPED_SPECIAL_FILE。
    // T16 (T09 の補強): そのディレクトリの FileStreamInfo が ERROR_HANDLE_EOF で失敗しても FATAL にならない。
    // Directory を最初に判定し、ストリーム一覧などを取得しない (docs/spec/filesystem.md#special-files の判定順序)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void T09_T16_TargetDirectory_IsSkippedWithoutStreamQuery(RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("x.txt", Hello))) { Mode = mode };
        var dir = harness.Fs.AddDirectory(@"C:\target\x.txt");
        dir.Errors[FakeOp.Basic] = 5;
        dir.Errors[FakeOp.AttributeTag] = 5;

        var result = harness.Run();

        Assert.Equal(Classification.SkippedSpecialFile, Single(result));
        Assert.Equal(SkipReason.Directory, result.Results[0].SkipReason);
        Assert.DoesNotContain(harness.Fs.Calls, c => c.StartsWith("Streams", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Fs.Calls, c => c.StartsWith("Basic", StringComparison.Ordinal));
        Assert.False(harness.Contents.Touched(0));
    }

    // T16 の対照: ディレクトリでない対象で FileStreamInfo が ERROR_HANDLE_EOF (38) で失敗したら FATAL
    [Theory]
    [MemberData(nameof(BothModes))]
    public void T16_NonDirectory_StreamInfoHandleEof_IsFatal(RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("x.txt", Hello))) { Mode = mode };
        harness.Fs.AddFile(@"C:\target\x.txt", Bytes("hello")).Errors[FakeOp.Streams] = 38;

        var result = harness.Run();

        Assert.Equal(FatalKind.TargetInfoFailed, result.Fatal?.Kind);
    }

    // T09: ZIP 自身に対応する対象 (ボリュームシリアルと File ID が一致) → SKIPPED_SPECIAL_FILE
    [Theory]
    [MemberData(nameof(BothModes))]
    public void T09_ArchiveItself_IsSkipped(RunMode mode)
    {
        var zip = MakeZip(("archive.zip", Hello));
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\target");
        fs.AddDirectory(@"C:\in");
        fs.AddFile(@"C:\target\archive.zip", zip);
        using var harness = new PipelineHarness(zip, fs) { ArchiveLocation = @"C:\target\archive.zip", Mode = mode };

        var result = harness.Run();

        Assert.Equal(Classification.SkippedSpecialFile, Single(result));
        Assert.Equal(SkipReason.ArchiveItself, result.Results[0].SkipReason);
    }

    // T10: 情報の取得失敗、存在確認後のオープン失敗、比較中の読み取り失敗 → SKIP ではなく全体 FATAL
    // Fast では「比較中の読取失敗」を除く (Fast は target の内容を読まない。docs/TESTING.md#principles のモード適用)。
    [Theory]
    [InlineData(FakeOp.Streams, FatalKind.TargetInfoFailed, RunMode.Strict)]
    [InlineData(FakeOp.Basic, FatalKind.TargetInfoFailed, RunMode.Strict)]
    [InlineData(FakeOp.AttributeTag, FatalKind.TargetInfoFailed, RunMode.Strict)]
    [InlineData(FakeOp.Standard, FatalKind.TargetInfoFailed, RunMode.Strict)]
    [InlineData(FakeOp.VolumeFileId, FatalKind.TargetInfoFailed, RunMode.Strict)]
    [InlineData(FakeOp.FinalPath, FatalKind.TargetInfoFailed, RunMode.Strict)]
    [InlineData(FakeOp.OpenComparison, FatalKind.ComparisonOpenFailed, RunMode.Strict)]
    [InlineData(FakeOp.Read, FatalKind.TargetReadFailed, RunMode.Strict)]
    [InlineData(FakeOp.Streams, FatalKind.TargetInfoFailed, RunMode.Fast)]
    [InlineData(FakeOp.Basic, FatalKind.TargetInfoFailed, RunMode.Fast)]
    [InlineData(FakeOp.AttributeTag, FatalKind.TargetInfoFailed, RunMode.Fast)]
    [InlineData(FakeOp.Standard, FatalKind.TargetInfoFailed, RunMode.Fast)]
    [InlineData(FakeOp.VolumeFileId, FatalKind.TargetInfoFailed, RunMode.Fast)]
    [InlineData(FakeOp.FinalPath, FatalKind.TargetInfoFailed, RunMode.Fast)]
    [InlineData(FakeOp.OpenComparison, FatalKind.ComparisonOpenFailed, RunMode.Fast)]
    public void T10_TargetApiFailure_IsFatal(FakeOp op, FatalKind expected, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("ok.txt", Hello), ("x.txt", World))) { Mode = mode };
        harness.Fs.AddFile(@"C:\target\ok.txt", Bytes("hello"));
        var node = harness.Fs.AddFile(@"C:\target\x.txt", Bytes("world!"));
        node.Errors[op] = 5;
        if (op == FakeOp.Read)
        {
            node.ReadFailAt = 3;
        }

        var result = harness.Run();

        Assert.Equal(expected, result.Fatal?.Kind);
        Assert.Equal("x.txt", result.Fatal!.Entry!.Name);
        Assert.Equal(Candidate(mode), Assert.Single(result.Results).Classification);
    }

    // T10: 存在を確認した後に開けない (見つからない 2、アクセス拒否 5、共有違反 32) → FATAL
    [Theory]
    [InlineData(2, RunMode.Strict)]
    [InlineData(5, RunMode.Strict)]
    [InlineData(32, RunMode.Strict)]
    [InlineData(2, RunMode.Fast)]
    [InlineData(5, RunMode.Fast)]
    [InlineData(32, RunMode.Fast)]
    public void T10_OpenFailureAfterEnumeration_IsFatal(int error, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("x.txt", Hello))) { Mode = mode };
        harness.Fs.AddFile(@"C:\target\x.txt", Bytes("hello")).Errors[FakeOp.OpenComparison] = error;

        var result = harness.Run();

        Assert.Equal(FatalKind.ComparisonOpenFailed, result.Fatal?.Kind);
        Assert.Contains($"Win32 エラー {error}", result.Fatal!.Describe(), StringComparison.Ordinal);
    }

    // T12: 比較用ハンドルは各エントリの判定終了時に閉じられる。FATAL の経路を含む (PipelineHarness.Run が毎回確認する)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void T12_HandlesAreClosedAfterEachEntry(RunMode mode)
    {
        var zip = MakeZip(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello), ("d/e.txt", Hello), ("f.txt", Hello));
        using var harness = new PipelineHarness(zip) { Mode = mode };
        var fs = harness.Fs;
        fs.AddFile(@"C:\target\a.txt", Bytes("hello"));
        fs.AddFile(@"C:\target\b.txt", Bytes("hellO"));
        fs.AddFile(@"C:\target\c.txt", Bytes("hello")).Links = 2;
        fs.AddDirectory(@"C:\target\d");
        fs.AddFile(@"C:\target\d\e.txt", Bytes("hello"));
        var openAtEachStart = new List<int>();
        fs.BeforeOpenComparison = _ => openAtEachStart.Add(fs.OpenComparisonCount);

        var result = harness.Run();

        Assert.Null(result.Fatal);
        Assert.Equal(4, fs.ComparisonOpenCount);
        Assert.All(openAtEachStart, count => Assert.Equal(0, count));
        Assert.Equal(1, fs.MaxConcurrentComparisons);
    }

    // T12: 例外の経路でも比較用ハンドルは閉じられる (例外はそのまま伝わる。削除は起こらない)。
    [Fact]
    public void T12_HandleIsClosedWhenProbeThrows()
    {
        using var harness = new PipelineHarness(MakeZip(("x.txt", Hello)));
        harness.Fs.AddFile(@"C:\target\x.txt", Bytes("hello")).ThrowOnRead = true;
        var opened = TargetRootValidator.Open(harness.Fs, TargetPath, TargetLocationPolicy.None);
        using var root = opened.Root!;
        var entries = ZipPrevalidator.Validate(harness.Source.Entries, Limits.Default).Entries;

        Assert.Throws<InvalidOperationException>(() => Analyzer.Run(new AnalyzeRequest(
            entries, harness.Contents, harness.Fs, root, default, Limits.Default)));

        Assert.Equal(1, harness.Fs.ComparisonOpenCount);
        Assert.Equal(1, harness.Fs.ComparisonCloseCount);
        Assert.Equal(1, harness.Fs.OpenHandleCount);
    }

    // T13: 実名確認の失敗 → 全体 FATAL
    [Theory]
    [InlineData("open-enumeration", FatalKind.EnumerationOpenFailed, RunMode.Strict)]
    [InlineData("enumeration-midway", FatalKind.EnumerationFailed, RunMode.Strict)]
    [InlineData("enumeration-handle-id", FatalKind.EnumerationHandleMismatch, RunMode.Strict)]
    [InlineData("enumeration-handle-path", FatalKind.EnumerationHandleMismatch, RunMode.Strict)]
    [InlineData("enumeration-handle-info", FatalKind.EnumerationHandleMismatch, RunMode.Strict)]
    [InlineData("comparison-id", FatalKind.ComparisonFileIdMismatch, RunMode.Strict)]
    [InlineData("open-enumeration", FatalKind.EnumerationOpenFailed, RunMode.Fast)]
    [InlineData("enumeration-midway", FatalKind.EnumerationFailed, RunMode.Fast)]
    [InlineData("enumeration-handle-id", FatalKind.EnumerationHandleMismatch, RunMode.Fast)]
    [InlineData("enumeration-handle-path", FatalKind.EnumerationHandleMismatch, RunMode.Fast)]
    [InlineData("enumeration-handle-info", FatalKind.EnumerationHandleMismatch, RunMode.Fast)]
    [InlineData("comparison-id", FatalKind.ComparisonFileIdMismatch, RunMode.Fast)]
    public void T13_RealNameCheckFailure_IsFatal(string injection, FatalKind expected, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("d/x.txt", Hello))) { Mode = mode };
        var fs = harness.Fs;
        var dir = fs.AddDirectory(@"C:\target\d");
        var file = fs.AddFile(@"C:\target\d\x.txt", Bytes("hello"));
        fs.AddFile(@"C:\target\d\y.txt", Bytes("y"));
        switch (injection)
        {
            case "open-enumeration":
                dir.Errors[FakeOp.OpenEnumeration] = 5;
                break;
            case "enumeration-midway":
                dir.Errors[FakeOp.Enumerate] = 1117;
                dir.EnumerationFailAfter = 1;
                break;
            case "enumeration-handle-id":
                // 親の列挙で見つけた後、d が同名の別ディレクトリに差し替えられた (File ID が変わる)。
                fs.Get(@"C:\target").EnumerationOverride = [new DirectoryItem("d", 0x10, 0, fs.NextId())];
                break;
            case "enumeration-handle-path":
                dir.FinalPathOverride = @"\\?\C:\elsewhere\d";
                break;
            case "enumeration-handle-info":
                dir.Errors[FakeOp.DirectoryInfo] = 5;
                break;
            case "comparison-id":
                dir.EnumerationOverride = [new DirectoryItem("x.txt", 0x20, 0, fs.NextId())];
                break;
        }

        var result = harness.Run();

        Assert.Equal(expected, result.Fatal?.Kind);
        _ = file;
    }

    // T14 (1): 1回の列挙で同じ名前が2回返る → 最初の1件だけを採用する
    [Theory]
    [InlineData(true, RunMode.Strict)]
    [InlineData(false, RunMode.Strict)]
    [InlineData(true, RunMode.Fast)]
    [InlineData(false, RunMode.Fast)]
    public void T14_DuplicateNameInEnumeration_FirstWins(bool firstIsReal, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("x.txt", Hello))) { Mode = mode };
        var fs = harness.Fs;
        var file = fs.AddFile(@"C:\target\x.txt", Bytes("hello"));
        var real = new DirectoryItem("x.txt", 0x20, 0, file.Id);
        var other = new DirectoryItem("x.txt", 0x20, 0, fs.NextId());
        fs.Get(@"C:\target").EnumerationOverride = firstIsReal ? [real, other] : [other, real];

        var result = harness.Run();

        if (firstIsReal)
        {
            Assert.Equal(Candidate(mode), Single(result));
        }
        else
        {
            // 採用した項目と比較用ハンドルの File ID が異なる → FATAL
            Assert.Equal(FatalKind.ComparisonFileIdMismatch, result.Fatal?.Kind);
        }
    }

    // T14 (2): 照合する名前が列挙で返らない (列挙中の改名による見落とし) → MISSING、ZIP 内容を開かない
    [Theory]
    [MemberData(nameof(BothModes))]
    public void T14_NameMissingFromEnumeration_IsMissing(RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("x.txt", Hello))) { Mode = mode };
        var fs = harness.Fs;
        fs.AddFile(@"C:\target\x.txt", Bytes("hello"));
        fs.Get(@"C:\target").EnumerationOverride = [];

        var result = harness.Run();

        Assert.Equal(Classification.Missing, Single(result));
        Assert.False(harness.Contents.Touched(0));
        Assert.Equal(0, fs.ComparisonOpenCount);
    }

    // P08 (Core): 期待パスは \\?\ 形式の target 最終パスから組み立て、比較用ハンドルの最終パスと序数比較する。
    // 大小文字だけの違いでも FATAL (途中のディレクトリの大小文字だけの改名など)。
    [Theory]
    [InlineData(null, true, RunMode.Strict)]
    [InlineData(@"\\?\C:\target\D\x.txt", false, RunMode.Strict)]
    [InlineData(@"\\?\c:\target\d\x.txt", false, RunMode.Strict)]
    [InlineData(@"C:\target\d\x.txt", false, RunMode.Strict)]
    [InlineData(null, true, RunMode.Fast)]
    [InlineData(@"\\?\C:\target\D\x.txt", false, RunMode.Fast)]
    [InlineData(@"\\?\c:\target\d\x.txt", false, RunMode.Fast)]
    [InlineData(@"C:\target\d\x.txt", false, RunMode.Fast)]
    public void P08_FinalPathComparedOrdinallyWithDevicePrefix(string? finalPath, bool matches, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("d/x.txt", Hello))) { Mode = mode };
        harness.Fs.AddDirectory(@"C:\target\d");
        var file = harness.Fs.AddFile(@"C:\target\d\x.txt", Bytes("hello"));
        string? opened = null;
        harness.Fs.BeforeOpenComparison = path =>
        {
            opened = path;
            file.FinalPathOverride = finalPath;
        };

        var result = harness.Run();

        Assert.Equal(@"\\?\C:\target\d\x.txt", opened);
        if (matches)
        {
            Assert.Equal(Candidate(mode), Single(result));
            Assert.Equal(@"\\?\C:\target\d\x.txt", result.Results[0].Target);
        }
        else
        {
            Assert.Equal(FatalKind.FinalPathMismatch, result.Fatal?.Kind);
        }
    }

    // A06・docs/spec/filesystem.md#baselines: analyze はスナップショット・削除候補・M0 を持たず、親 File ID も取得しない (親 File ID の照合は delete だけ)。
    // 各エントリの結果は、表示用の Target (期待パス、\\?\ 形式) だけを持つ。MISSING・DIRECTORY では実在しない位置でもよい。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void A06_AnalyzeKeepsOnlyResultsWithTarget(RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("d/x.txt", Hello), ("d/missing.txt", Hello), ("e/", null))) { Mode = mode };
        harness.Fs.AddDirectory(@"C:\target\d");
        harness.Fs.AddFile(@"C:\target\d\x.txt", Bytes("hello"), 0x20 | 0x2);

        var result = harness.Run();

        Assert.Equal(
            [(Candidate(mode), @"\\?\C:\target\d\x.txt"), (Classification.Missing, @"\\?\C:\target\d\missing.txt"), (Classification.Directory, @"\\?\C:\target\e")],
            result.Results.Select(r => (r.Classification, r.Target)));
        Assert.DoesNotContain(harness.Fs.Calls, c => c.StartsWith("ParentFileId ", StringComparison.Ordinal));
    }

    // DIRECTORY: ZIP のディレクトリエントリは target を調べずに DIRECTORY
    [Fact]
    public void DirectoryEntry_IsDirectoryWithoutTouchingTarget()
    {
        using var harness = new PipelineHarness(MakeZip(("d/", null)));

        Assert.Equal(Classification.Directory, Single(harness.Run()));
        Assert.DoesNotContain(harness.Fs.Calls, c => c.StartsWith("Enumerate", StringComparison.Ordinal));
    }

    // P01: 先頭に MATCHED が複数、後方の内容比較候補に CRC 不一致 → 全体 FATAL、削除候補なし。
    // 先頭の MATCHED は判定済みとして表示、原因エントリを表示、未判定は件数のみ。
    [Fact]
    public void P01_LaterCrcMismatch_BlocksEarlierMatched()
    {
        var zip = MakeZip(("m1.txt", Hello), ("m2.txt", Hello), ("bad.bin", ContentVerificationTests.Data), ("later.txt", Hello));
        var patcher = new Fixtures.ZipPatcher(zip);
        zip = patcher.SetCrc32(2, patcher.GetCrc32(2) ^ 1).ToArray();
        using var harness = new PipelineHarness(zip);
        harness.Fs.AddFile(@"C:\target\m1.txt", Bytes("hello"));
        harness.Fs.AddFile(@"C:\target\m2.txt", Bytes("hello"));
        harness.Fs.AddFile(@"C:\target\bad.bin", (byte[])ContentVerificationTests.Data.Clone());
        harness.Fs.AddFile(@"C:\target\later.txt", Bytes("hello"));

        var result = harness.Run();

        Assert.Equal(FatalKind.ContentCrcMismatch, result.Fatal?.Kind);
        Assert.Equal(2, result.Fatal!.Entry!.Index);
        Assert.Equal([Classification.Matched, Classification.Matched], result.Results.Select(r => r.Classification));
        Assert.Equal(1, result.UnclassifiedCount);
        Assert.False(harness.Contents.Touched(3));
    }

    // P02 (偽 FS): 先頭に MATCHED、後方に target 安全判定 API の失敗・比較対象の共有違反 → 全体 FATAL、削除候補なし
    [Theory]
    [InlineData(FakeOp.Streams, 1117, FatalKind.TargetInfoFailed, RunMode.Strict)]
    [InlineData(FakeOp.OpenComparison, 32, FatalKind.ComparisonOpenFailed, RunMode.Strict)]
    [InlineData(FakeOp.Streams, 1117, FatalKind.TargetInfoFailed, RunMode.Fast)]
    [InlineData(FakeOp.OpenComparison, 32, FatalKind.ComparisonOpenFailed, RunMode.Fast)]
    public void P02_LaterTargetFailure_BlocksEarlierMatched(FakeOp op, int error, FatalKind expected, RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("m.txt", Hello), ("x.txt", Hello))) { Mode = mode };
        harness.Fs.AddFile(@"C:\target\m.txt", Bytes("hello"));
        harness.Fs.AddFile(@"C:\target\x.txt", Bytes("hello")).Errors[op] = error;

        var result = harness.Run();

        Assert.Equal(expected, result.Fatal?.Kind);
        Assert.Equal(Candidate(mode), Assert.Single(result.Results).Classification);
    }

    // R07: 内容比較候補で実データが宣言 Length を超える → Length を超えた時点で読み取りを中断し FATAL。それ以上読まない。
    [Fact]
    public void R07_ContentBeyondLength_StopsImmediately()
    {
        using var harness = new PipelineHarness(MakeZip(("x.bin", new byte[10])));
        harness.Fs.AddFile(@"C:\target\x.bin", new byte[10]);
        var opened = TargetRootValidator.Open(harness.Fs, TargetPath, TargetLocationPolicy.None);
        using var root = opened.Root!;
        var entries = ZipPrevalidator.Validate(harness.Source.Entries, Limits.Default).Entries;
        var endless = new EndlessContent(10);

        var result = Analyzer.Run(new AnalyzeRequest(
            entries, new SingleContentProvider(endless), harness.Fs, root, default, Limits.Default));

        Assert.Equal(FatalKind.ContentTooLong, result.Fatal?.Kind);
        Assert.Equal(11, endless.Stream.TotalRead);
        Assert.True(endless.Stream.ReadCalls <= 2, $"reads {endless.Stream.ReadCalls}");
    }

    // R08: 実測合計上限を小さな値に差し替えて、累計がちょうど上限 / +1
    [Theory]
    [InlineData(10, true)]
    [InlineData(9, false)]
    public void R08_TotalReadLimit(long limit, bool passes)
    {
        using var harness = new PipelineHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello)));
        harness.Fs.AddFile(@"C:\target\a.txt", Bytes("hello"));
        harness.Fs.AddFile(@"C:\target\b.txt", Bytes("hello"));
        harness.Limits = Limits.Default with { MaxTotalReadLength = limit };

        var result = harness.Run();

        if (passes)
        {
            Assert.Null(result.Fatal);
            Assert.Equal([Classification.Matched, Classification.Matched], result.Results.Select(r => r.Classification));
        }
        else
        {
            Assert.Equal(FatalKind.TotalReadLengthTooLarge, result.Fatal?.Kind);
            Assert.Equal("b.txt", result.Fatal!.Entry!.Name);
        }
    }

    // R09: R08 と同じ差し替えで実測合計の上限を 0 にし、サイズ一致の内容比較候補を含む ZIP を --fast で実行する
    // → FATAL にならず SAME_SIZE。実測展開量を計上しない (docs/SPEC.md#modes)。対照として同じ入力の Strict は FATAL。
    [Theory]
    [InlineData(RunMode.Fast)]
    [InlineData(RunMode.Strict)]
    public void R09_Fast_DoesNotCountActualReadLength(RunMode mode)
    {
        using var harness = new PipelineHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello))) { Mode = mode };
        harness.Fs.AddFile(@"C:\target\a.txt", Bytes("hello"));
        harness.Fs.AddFile(@"C:\target\b.txt", Bytes("hello"));
        harness.Limits = Limits.Default with { MaxTotalReadLength = 0 };

        var result = harness.Run();

        if (mode == RunMode.Fast)
        {
            Assert.Null(result.Fatal);
            Assert.Equal([Classification.SameSize, Classification.SameSize], result.Results.Select(r => r.Classification));
            Assert.Empty(harness.Contents.Calls);
        }
        else
        {
            Assert.Equal(FatalKind.TotalReadLengthTooLarge, result.Fatal?.Kind);
            Assert.Equal("a.txt", result.Fatal!.Entry!.Name);
        }
    }

    // 決定性: 同じ入力に対して結果は常に同じ。FATAL の最初の1件は ZIP 内の順序で決まる。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Determinism_FirstFatalFollowsZipOrder(bool reversed)
    {
        (string, byte[]?)[] entries = [("ok.txt", Hello), ("open.txt", Hello), ("info.txt", Hello)];
        var zip = MakeZip(reversed ? [entries[0], entries[2], entries[1]] : entries);

        string Run()
        {
            using var harness = new PipelineHarness(zip);
            harness.Fs.AddFile(@"C:\target\ok.txt", Bytes("hello"));
            harness.Fs.AddFile(@"C:\target\open.txt", Bytes("hello")).Errors[FakeOp.OpenComparison] = 32;
            harness.Fs.AddFile(@"C:\target\info.txt", Bytes("hello")).Errors[FakeOp.Streams] = 5;
            var result = harness.Run();
            Assert.Equal(reversed ? "info.txt" : "open.txt", result.Fatal!.Entry!.Name);
            return string.Join('\n', AnalyzeOutput.Format(result, RunMode.Strict));
        }

        var first = Run();
        Assert.Equal(first, Run());
        Assert.Equal(first, Run());
    }

    // 進捗 (Checking n / total) は各エントリの判定の前に呼ばれる
    [Fact]
    public void Progress_IsReportedPerEntry()
    {
        using var harness = new PipelineHarness(MakeZip(("a.txt", Hello), ("d/", null), ("b.txt", Hello)));

        harness.Run();

        Assert.Equal([(1, 3), (2, 3), (3, 3)], harness.Progress);
        Assert.Equal("Checking 2 / 3", ReportText.CheckingProgress(2, 3));
    }

    private sealed class SingleContentProvider(IZipEntryContent content) : IZipContentProvider
    {
        public IZipEntryContent GetContent(int index) => content;
    }

    // 宣言 Length は declared だが、際限なくデータを返すエントリ。
    private sealed class EndlessContent(long declared) : IZipEntryContent
    {
        public CountingStream Stream { get; } = new();

        public bool IsEncrypted => false;

        public long Length => declared;

        public uint Crc32 => 0;

        public Stream Open() => Stream;
    }

    private sealed class CountingStream : Stream
    {
        public long TotalRead { get; private set; }

        public int ReadCalls { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            Array.Clear(buffer, offset, count);
            TotalRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
