using Unextract.Core.Analysis;
using Unextract.Core.Commands;
using Unextract.Core.Deletion;
using Unextract.Core.Display;
using Unextract.Core.Rar;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;
using Unextract.Core.Zip;

namespace Unextract.Core.Tests;

// テスト U10〜U22: RAR の Core の経路 (docs/spec/rar.md)。DLL の代わりに偽のソース・セッション (Fakes/FakeRar.cs) を使い、
// Prepare の形式の分岐・受理規則・手順2/6/10、セッションの前進・押し込み型の内容検証、delete の保留ディレクトリ、形式名を確かめる。
// target は ZIP と同じ偽の FS。
public class RarCommandTests
{
    private const string RarPath = @"C:\in\archive.rar";

    private static readonly byte[] Hello = PipelineHarness.Bytes("hello");

    public static TheoryData<RunMode> BothModes => new() { RunMode.Strict, RunMode.Fast };

    private static (CommandHarness H, FakeRarArchive Rar) Setup(RunMode mode, FakeRarArchive rar, bool createTarget = true)
    {
        var fs = new FakeFileSystem();
        var h = new CommandHarness([], fs, createTarget) { Mode = mode, Archive = RarPath, OpenRarArchive = FakeRarArchive.Opener(rar) };
        fs.AddFile(RarPath, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);
        return (h, rar);
    }

    private static IReadOnlyList<string> Header(RunMode mode) => ReportText.Header(RarPath, @"\\?\C:\target", mode);

    // U10: Strict の analyze。全エントリ (ディレクトリを含む) の処理の最初に前進し、内容比較候補だけを RAR_TEST で読む。
    // 非候補は次の前進で RAR_SKIP され、最後の前進先より後ろは読まない。セッション → target ルート → アーカイブの順に閉じる。
    [Fact]
    public void U10_StrictAnalyze_AdvancesEveryEntry_AndTestsOnlyCandidates()
    {
        var (h, rar) = Setup(RunMode.Strict, new FakeRarArchive(
            FakeRarEntry.Directory("d"),
            new FakeRarEntry(@"d\a.txt", Hello),
            new FakeRarEntry("b.txt", Hello),
            new FakeRarEntry("c.txt", Hello),
            new FakeRarEntry("e.txt", Hello)));
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\a.txt");
        h.File("b.txt", PipelineHarness.Bytes("hellO"));
        h.File("e.txt", PipelineHarness.Bytes("hello!"));
        var handles = new List<int>();
        rar.OnSessionDispose = () => handles.Add(h.Fs.OpenHandleCount);
        rar.OnArchiveDispose = () => handles.Add(h.Fs.OpenHandleCount);

        var run = h.Analyze();

        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
        var results = run.Outcome.Analysis!.Results;
        Assert.Equal(
            [Classification.Directory, Classification.Matched, Classification.Modified, Classification.Missing, Classification.Modified],
            results.Select(r => r.Classification));
        Assert.Equal(
            ["OpenSession", "Advance 0", "Advance 1", "Skip 0", "GetContent 1", "Test 1", "Chunk 1 5", "Advance 2", "GetContent 2", "Test 2", "Chunk 2 5",
             "Advance 3", "Advance 4", "Skip 3", "SessionClose", "ArchiveClose"],
            rar.Calls);

        // セッションを閉じる時点では target ルートが開いており、アーカイブを閉じる時点では閉じている。
        Assert.Equal([1, 0], handles);
        Assert.Equal(1, rar.Session!.DisposeCount);
        Assert.Equal(Header(RunMode.Strict), run.OutputLines.Take(4));
    }

    // U11: Fast は内容読み取り用に開かず、前進も内容の読み取りもしない (docs/spec/rar.md#fast)。
    [Fact]
    public void U11_FastAnalyze_DoesNotOpenSession()
    {
        var (h, rar) = Setup(RunMode.Fast, new FakeRarArchive(new FakeRarEntry("a.txt", Hello), new FakeRarEntry("b.txt", Hello)));
        rar.TestFailures[0] = "ERAR_BAD_DATA";
        h.File("a.txt", PipelineHarness.Bytes("HELLO"));

        var run = h.Analyze();

        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
        Assert.Equal([Classification.SameSize, Classification.Missing], run.Outcome.Analysis!.Results.Select(r => r.Classification));
        Assert.Equal(["ArchiveClose"], rar.Calls);
        Assert.Equal(ReportText.RarFastWarning, run.OutputLines[0]);
    }

    // U12: Strict の delete。ファイルエントリの処理の最初に前進し、ディレクトリと処理対象外は通過する (Pass)。
    // 末尾の処理対象ディレクトリは追加の前進なしに DIRECTORY と数える (docs/spec/rar.md#session)。
    [Fact]
    public void U12_StrictDelete_AllEntries_PassesDirectories_AndCountsTrailingDirectory()
    {
        var (h, rar) = Setup(RunMode.Strict, new FakeRarArchive(
            FakeRarEntry.Directory("d"),
            new FakeRarEntry(@"d\a.txt", Hello),
            new FakeRarEntry("x.txt", Hello),
            new FakeRarEntry("b.txt", Hello),
            FakeRarEntry.Directory("z")));
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\a.txt");
        h.File("b.txt");
        var counts = new List<int>();
        h.Notifications = null;

        var run = h.Delete();

        var report = run.Outcome.Report!;
        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
        Assert.Equal([DeleteStatus.Deleted, DeleteStatus.Missing, DeleteStatus.Deleted], report.Results.Select(r => r.Status));
        Assert.Equal(2, report.DirectoryCount);
        Assert.Equal(0, report.NotProcessedCount);
        Assert.False(h.Exists(@"d\a.txt"));
        Assert.False(h.Exists("b.txt"));
        Assert.Equal(
            ["OpenSession", "Advance 1", "Pass 0", "GetContent 1", "Test 1", "Chunk 1 5", "Advance 2", "Advance 3", "Skip 2", "GetContent 3", "Test 3", "Chunk 3 5",
             "SessionClose", "ArchiveClose"],
            rar.Calls);
    }

    // U12: --entries で選んだファイルだけを処理し、選択外は前進で通過する。選択外は処理対象外の件数。
    [Fact]
    public void U12_StrictDelete_Selected_PassesUnselected()
    {
        var (h, rar) = Setup(RunMode.Strict, new FakeRarArchive(
            FakeRarEntry.Directory("d"),
            new FakeRarEntry(@"d\a.txt", Hello),
            new FakeRarEntry("x.txt", Hello),
            new FakeRarEntry("b.txt", Hello)));
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\a.txt");
        h.File("b.txt");

        var run = h.Delete(entriesPath: CommandHarness.WriteEntries("b.txt\n"));

        var report = run.Outcome.Report!;
        Assert.Equal([DeleteStatus.Deleted], report.Results.Select(r => r.Status));
        Assert.Equal(0, report.DirectoryCount);
        Assert.True(h.Exists(@"d\a.txt"));
        Assert.Equal(["OpenSession", "Advance 3", "Pass 0", "Pass 1", "Pass 2", "GetContent 3", "Test 3", "Chunk 3 5", "SessionClose", "ArchiveClose"], rar.Calls);
        Assert.Contains("処理対象外 3、未処理 0", run.Output, StringComparison.Ordinal);
    }

    // U12: ディレクトリだけを処理する delete は前進しない (docs/spec/rar.md#session)。
    [Fact]
    public void U12_StrictDelete_DirectoriesOnly_NoAdvance()
    {
        var (h, rar) = Setup(RunMode.Strict, new FakeRarArchive(FakeRarEntry.Directory("d"), FakeRarEntry.Directory(@"d\e")));

        var run = h.Delete();

        Assert.Equal(2, run.Outcome.Report!.DirectoryCount);
        Assert.Equal(["OpenSession", "SessionClose", "ArchiveClose"], rar.Calls);
    }

    // U13: 前進の失敗は前進先の STOP。step なし、削除された可能性なし、前進先の target に触れない。
    // 照合を終えた保留ディレクトリは DIRECTORY、照合に至らなかったものは未処理 (docs/spec/rar.md#session)。
    [Fact]
    public void U13_StrictDelete_AdvanceFailure_StopsTarget_AndCountsPendingDirectories()
    {
        var (h, rar) = Setup(RunMode.Strict, new FakeRarArchive(
            FakeRarEntry.Directory("d0"),
            new FakeRarEntry("a.txt", Hello),
            FakeRarEntry.Directory("d2"),
            FakeRarEntry.Directory("d3"),
            new FakeRarEntry("b.txt", Hello),
            new FakeRarEntry("c.txt", Hello)));
        rar.AdvanceFailures[4] = new RarAdvanceResult(FatalKind.ArchiveChanged, new ZipEntryRef(3, @"d3\"), null, 2);
        h.File("a.txt");
        h.File("b.txt");
        h.File("c.txt");
        var directoryCounts = new List<int>();
        h.Notifications = new CommandNotifications { OnDirectoryCount = directoryCounts.Add, OnDeleteResult = _ => { } };

        var run = h.Delete();

        var report = run.Outcome.Report!;
        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.Equal([DeleteStatus.Deleted, DeleteStatus.Stopped], report.Results.Select(r => r.Status));
        var stop = report.Stop!;
        Assert.Equal("b.txt", stop.Entry.Name);
        Assert.False(stop.PossiblyDeleted);
        Assert.Null(stop.Failure!.Step);
        Assert.Equal(FatalKind.ArchiveChanged, stop.Failure.FatalKind);
        Assert.Equal("RAR のヘッダーが Prepare で列挙したものと一致しません (検出位置: エントリ #4「d3\\」)", stop.Reason);
        Assert.Equal(2, report.DirectoryCount);
        Assert.Equal([1, 2], directoryCounts);
        Assert.Equal(2, report.NotProcessedCount);
        Assert.True(h.Exists("b.txt"));
        Assert.True(h.Exists("c.txt"));
        Assert.DoesNotContain(h.Fs.Calls, c => c.EndsWith(@"\b.txt", StringComparison.Ordinal) && !c.StartsWith("Close", StringComparison.Ordinal) && c.Contains("Deletion", StringComparison.Ordinal));
        Assert.DoesNotContain("GetContent 4", rar.Calls);
    }

    // U13: 人間向けの STOP の表示 (原因の名前はエスケープ)。
    [Fact]
    public void U13_StrictDelete_AdvanceFailure_HumanOutput()
    {
        var (h, rar) = Setup(RunMode.Strict, new FakeRarArchive(new FakeRarEntry("a.txt", Hello), new FakeRarEntry("b.txt", Hello)));
        rar.AdvanceFailures[1] = new RarAdvanceResult(FatalKind.ArchiveUnreadable, new ZipEntryRef(0, "a.txt"), "ERAR_BAD_DATA", -1);
        h.File("b.txt");

        var run = h.Delete();

        Assert.Equal(
            "停止: エントリ #2 \"b.txt\": RAR として読み取れません (検出位置: エントリ #1「a.txt」、ERAR_BAD_DATA)",
            run.ErrorLines[0]);
        Assert.Equal(1, run.Outcome.Report!.Count(DeleteStatus.Missing));
    }

    // U14: analyze ではディレクトリも前進先になる。直前のエントリの RAR_SKIP の失敗も前進先の FATAL。
    [Fact]
    public void U14_StrictAnalyze_AdvanceFailure_IsFatalOfTarget()
    {
        var (h, rar) = Setup(RunMode.Strict, new FakeRarArchive(new FakeRarEntry("a.txt", Hello), FakeRarEntry.Directory("d"), new FakeRarEntry("z.txt", Hello)));
        rar.AdvanceFailures[1] = new RarAdvanceResult(FatalKind.ArchiveUnreadable, new ZipEntryRef(0, "a.txt"), "ERAR_BAD_DATA", 0);

        var run = h.Analyze();

        var analysis = run.Outcome.Analysis!;
        var fatal = analysis.Fatal!;
        Assert.Equal(FatalKind.ArchiveUnreadable, fatal.Kind);
        Assert.Equal(@"d\", fatal.Entry!.Name);
        Assert.Null(fatal.Step);
        Assert.Equal([Classification.Missing], analysis.Results.Select(r => r.Classification));
        Assert.Equal(1, analysis.UnclassifiedCount);
        Assert.Equal(
            ["FATAL: エントリ #2 \"d\\\\\": RAR として読み取れません (検出位置: エントリ #1「a.txt」、ERAR_BAD_DATA)", AnalyzeOutput.FatalClosing],
            run.ErrorLines);
    }

    public static TheoryData<string> ContentCases() =>
    [
        "match-4MiB", "match-1", "match-65537", "mismatch", "too-long", "too-short", "crc", "blake2", "test-failed", "target-read",
    ];

    // U15: 押し込み型の内容検証 (docs/spec/rar.md#verification)。チャンクの分け方によらず判定は同じ。
    [Theory]
    [MemberData(nameof(ContentCases))]
    public void U15_StrictAnalyze_PushContent(string id)
    {
        var data = new byte[200_000];
        new Random(7).NextBytes(data);
        var entry = new FakeRarEntry("x.bin", data);
        entry = id switch
        {
            "crc" => entry with { Crc32 = System.IO.Hashing.Crc32.HashToUInt32(data) ^ 1 },
            "blake2" => entry with { HashType = RarHashType.Blake2, Crc32 = 0x12345678 },
            _ => entry,
        };
        var rar = new FakeRarArchive(entry, new FakeRarEntry("y.txt", Hello));
        rar.ChunkSizes = id switch
        {
            "match-1" => [1, 1, 1, 7, 65_536],
            "match-65537" => [65_537],
            "too-long" => [1000],
            _ => [4 * 1024 * 1024],
        };
        switch (id)
        {
            case "too-long":
                rar.ActualData[0] = [.. data, 0];
                break;
            case "too-short":
                rar.ActualData[0] = data[..^1];
                break;
            case "test-failed":
                rar.TestFailures[0] = "ERAR_BAD_DATA";
                break;
        }

        var (h, _) = Setup(RunMode.Strict, rar);
        var target = (byte[])data.Clone();
        if (id == "mismatch")
        {
            target[10] ^= 1;
        }

        var node = h.File("x.bin", target);
        if (id == "target-read")
        {
            node.ReadFailAt = 100;
        }

        var run = h.Analyze();

        var analysis = run.Outcome.Analysis!;
        var expected = id switch
        {
            "match-4MiB" or "match-1" or "match-65537" or "blake2" => (Classification?)Classification.Matched,
            "mismatch" => Classification.Modified,
            _ => null,
        };
        if (expected is { } classification)
        {
            Assert.Null(analysis.Fatal);
            Assert.Equal(classification, analysis.Results[0].Classification);
            Assert.DoesNotContain("Aborted 0", rar.Calls);
            return;
        }

        var fatal = analysis.Fatal!;
        Assert.Equal(EntryStep.Compare, fatal.Step);
        Assert.Equal("x.bin", fatal.Entry!.Name);
        switch (id)
        {
            case "too-long":
                Assert.Equal(FatalKind.ContentTooLong, fatal.Kind);

                // 超過したチャンクで直ちに中止し、以後のチャンクを受け取らない。
                Assert.Equal("Aborted 0", rar.Calls[rar.Calls.FindIndex(c => c.StartsWith("Chunk 0", StringComparison.Ordinal)) + 201]);
                Assert.Equal(201, rar.Calls.Count(c => c.StartsWith("Chunk 0", StringComparison.Ordinal)));
                break;
            case "too-short":
                Assert.Equal(FatalKind.ContentTooShort, fatal.Kind);
                break;
            case "crc":
                Assert.Equal(FatalKind.ContentCrcMismatch, fatal.Kind);
                break;
            case "test-failed":
                // 全バイト一致でも RAR_TEST の失敗は FATAL (判定は RAR_TEST の成功後に確定する)。
                Assert.Equal(FatalKind.ContentReadFailed, fatal.Kind);
                Assert.Equal("ERAR_BAD_DATA", fatal.Detail);
                break;
            case "target-read":
                Assert.Equal(FatalKind.TargetReadFailed, fatal.Kind);
                Assert.Contains("Aborted 0", rar.Calls);
                break;
        }
    }

    // U15: delete でも全バイト一致の後の RAR_TEST の失敗は STOP (削除しない)。
    [Fact]
    public void U15_StrictDelete_TestFailureAfterMatch_Stops()
    {
        var rar = new FakeRarArchive(new FakeRarEntry("a.txt", Hello), new FakeRarEntry("b.txt", Hello));
        rar.TestFailures[0] = "ERAR_BAD_DATA";
        var (h, _) = Setup(RunMode.Strict, rar);
        h.File("a.txt");
        h.File("b.txt");

        var run = h.Delete();

        var stop = run.Outcome.Report!.Stop!;
        Assert.Equal("a.txt", stop.Entry.Name);
        Assert.Equal(FatalKind.ContentReadFailed, stop.Failure!.FatalKind);
        Assert.Equal(EntryStep.Compare, stop.Failure.Step);
        Assert.False(stop.PossiblyDeleted);
        Assert.True(h.Exists("a.txt"));
        Assert.True(h.Exists("b.txt"));
        Assert.Equal(1, run.Outcome.Report.NotProcessedCount);
        Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Disposition", StringComparison.Ordinal));
    }

    // U15: コールバック中の例外 (フック H3) はネイティブの境界の外で再送出された後、ZIP と同じく UNEXPECTED_EXCEPTION の STOP になる。
    [Fact]
    public void U15_StrictDelete_HookExceptionDuringPush_Stops()
    {
        var (h, rar) = Setup(RunMode.Strict, new FakeRarArchive(new FakeRarEntry("a.txt", Hello)));
        h.File("a.txt");

        var run = h.Delete(hooks: new DeleteHooks { DuringCompare = (_, _) => throw new InvalidOperationException("hook") });

        var stop = run.Outcome.Report!.Stop!;
        Assert.Equal(DeleteFailureKind.UnexpectedException, stop.Failure!.Kind);
        Assert.False(stop.PossiblyDeleted);
        Assert.True(h.Exists("a.txt"));
        Assert.Contains("Test 0", rar.Calls);
    }

    // U16: Fast の delete は内容を読まずに SAME_SIZE を削除する。セッションを開かない。
    [Fact]
    public void U16_FastDelete_DoesNotReadContent()
    {
        var rar = new FakeRarArchive(new FakeRarEntry("a.txt", Hello));
        rar.TestFailures[0] = "ERAR_BAD_DATA";
        var (h, _) = Setup(RunMode.Fast, rar);
        h.File("a.txt", PipelineHarness.Bytes("HELLO"));

        var run = h.Delete();

        Assert.Equal([DeleteStatus.Deleted], run.Outcome.Report!.Results.Select(r => r.Status));
        Assert.Equal(["ArchiveClose"], rar.Calls);
    }

    public static TheoryData<string, FatalKind, int?> RuleCases() => new()
    {
        { "archive-solid", FatalKind.ArchiveSolid, null },
        { "archive-encrypted", FatalKind.ArchiveEncrypted, null },
        { "entry-solid", FatalKind.EntrySolid, 2 },
        { "entry-split", FatalKind.EntrySplit, 2 },
        { "entry-encrypted", FatalKind.EntryEncrypted, 2 },
        { "redirection", FatalKind.EntryRedirection, 2 },
        { "host-other", FatalKind.UnsupportedHostOs, 2 },
        { "file-trailing-separator", FatalKind.FileEntryNameEndsWithSeparator, 2 },
        { "directory-data", FatalKind.DirectoryEntryWithData, 2 },
        { "dos-directory-on-file", FatalKind.DosDirectoryAttributeOnFileEntry, 2 },
        { "dos-reparse-on-directory", FatalKind.DosReparsePointAttribute, 2 },
        { "unix-file-directory-type", FatalKind.FileEntryWithDirectoryType, 2 },
        { "unix-file-type-zero", FatalKind.UnsupportedEntryType, 2 },
        { "unix-file-symlink-type", FatalKind.UnsupportedEntryType, 2 },
        { "unix-directory-file-type", FatalKind.DirectoryEntryWithFileType, 2 },
        { "unix-directory-type-zero", FatalKind.UnsupportedEntryType, 2 },
        { "no-hash", FatalKind.EntryWithoutHash, 2 },
        { "dictionary-over", FatalKind.EntryDictionaryTooLarge, 2 },
        { "rar-rule-before-name", FatalKind.EntrySolid, 2 },
        { "name-after-rar-rules", FatalKind.ReservedName, 1 },
    };

    // U17: RAR 固有の受理規則 (手順6)。両操作・両モードで同じ FATAL、削除0件、target のエントリに触れない。
    // 1件目は常に正常な a.txt (rar-rule-before-name と name-after-rar-rules では予約名 CON)。RAR 規則は全エントリに名前・構造の検査より先に行う。
    [Theory]
    [MemberData(nameof(RuleCases))]
    public void U17_RarRules_AreFatalInPrepare_ForBothOperationsAndModes(string id, FatalKind kind, int? entryNumber)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            foreach (var delete in new[] { false, true })
            {
                var (h, rar) = Setup(mode, RuleArchive(id));
                h.File("a.txt");

                var failure = delete ? h.Delete().Outcome.PreparationFailure : h.Analyze().Outcome.PreparationFailure;

                Assert.NotNull(failure);
                Assert.Equal(PrepareStage.ZipValidation, failure.Stage);
                Assert.True(failure.IsFatal);
                Assert.Equal(kind, failure.Fatal!.Kind);
                Assert.Equal(entryNumber, failure.Fatal.Entry?.Number);
                Assert.Equal(2, failure.TotalEntries);
                Assert.False(h.TouchedTargetEntries());
                Assert.True(h.Exists("a.txt"));
                Assert.DoesNotContain("OpenSession", rar.Calls);
                Assert.Equal("ArchiveClose", rar.Calls[^1]);
            }
        }
    }

    // U17: 境界 (受理されるもの)。辞書 1 GiB ちょうど、ディレクトリの辞書・ハッシュ無し、Unix の通常ファイル・ディレクトリ、Windows の他の属性。
    [Fact]
    public void U17_RarRules_AcceptedBoundaries()
    {
        var (h, _) = Setup(RunMode.Fast, new FakeRarArchive(
            new FakeRarEntry("a.txt", Hello) { DictionarySize = 1_073_741_824, Attributes = 0x27 },
            new FakeRarEntry("u.txt", Hello) { HostOs = RarHostOs.Unix, Attributes = 0x81A4 },
            new FakeRarEntry("b.txt", Hello) { HashType = RarHashType.Blake2 },
            FakeRarEntry.Directory("d") with { HashType = RarHashType.None, DictionarySize = long.MaxValue },
            FakeRarEntry.Directory("u") with { HostOs = RarHostOs.Unix, Attributes = 0x41ED }));

        var run = h.Analyze();

        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
    }

    // U17: 人間向けの analyze は ZIP の事前検証の FATAL と同じ形 (ヘッダー、判定済み 0、未判定 N)。
    [Fact]
    public void U17_RarRule_AnalyzeOutput()
    {
        var (h, _) = Setup(RunMode.Strict, RuleArchive("entry-split"));

        var run = h.Analyze();

        Assert.Equal([.. Header(RunMode.Strict), "判定済み: 0 エントリ", "未判定: 2 エントリ"], run.OutputLines);
        Assert.Equal(["FATAL: エントリ #2 \"s.txt\": 前後の巻へ続く (分割された) エントリです", AnalyzeOutput.FatalClosing], run.ErrorLines);
    }

    private static FakeRarArchive RuleArchive(string id)
    {
        var good = new FakeRarEntry("a.txt", Hello);
        var bad = new FakeRarEntry("s.txt", Hello);
        return id switch
        {
            "archive-solid" => new FakeRarArchive(good, bad) { Archive = new RarArchiveInfo(true, false) },
            "archive-encrypted" => new FakeRarArchive(good, bad) { Archive = new RarArchiveInfo(false, true) },
            "entry-solid" => new FakeRarArchive(good, bad with { IsSolid = true }),
            "entry-split" => new FakeRarArchive(good, bad with { IsSplit = true }),
            "entry-encrypted" => new FakeRarArchive(good, bad with { IsEncrypted = true }),
            "redirection" => new FakeRarArchive(good, bad with { RedirectionType = 1 }),
            "host-other" => new FakeRarArchive(good, bad with { HostOs = RarHostOs.Other }),
            "file-trailing-separator" => new FakeRarArchive(good, bad with { Name = @"s\" }),
            "directory-data" => new FakeRarArchive(good, FakeRarEntry.Directory("s") with { DeclaredLength = 1 }),
            "dos-directory-on-file" => new FakeRarArchive(good, bad with { Attributes = 0x10 }),
            "dos-reparse-on-directory" => new FakeRarArchive(good, FakeRarEntry.Directory("s") with { Attributes = 0x410 }),
            "unix-file-directory-type" => new FakeRarArchive(good, bad with { HostOs = RarHostOs.Unix, Attributes = 0x41ED }),
            "unix-file-type-zero" => new FakeRarArchive(good, bad with { HostOs = RarHostOs.Unix, Attributes = 0x1A4 }),
            "unix-file-symlink-type" => new FakeRarArchive(good, bad with { HostOs = RarHostOs.Unix, Attributes = 0xA1FF }),
            "unix-directory-file-type" => new FakeRarArchive(good, FakeRarEntry.Directory("s") with { HostOs = RarHostOs.Unix, Attributes = 0x81A4 }),
            "unix-directory-type-zero" => new FakeRarArchive(good, FakeRarEntry.Directory("s") with { HostOs = RarHostOs.Unix, Attributes = 0x1ED }),
            "no-hash" => new FakeRarArchive(good, bad with { HashType = RarHashType.None }),
            "dictionary-over" => new FakeRarArchive(good, bad with { DictionarySize = 1_073_741_825 }),
            "rar-rule-before-name" => new FakeRarArchive(new FakeRarEntry("CON", Hello), bad with { IsSolid = true }),
            "name-after-rar-rules" => new FakeRarArchive(new FakeRarEntry("CON", Hello), bad),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }

    // U18: 手順2の FATAL (ボリューム、列挙中の上限など) は entries・target の入力エラーより先に報告し、analyze はヘッダー・見出し・件数を出さない。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void U18_ArchiveStageFatal_PrecedesTargetAndEntries_AndAnalyzePrintsNoCounts(RunMode mode)
    {
        var fatals = new[]
        {
            (new FatalError(FatalKind.ArchiveMultiVolume), "FATAL: 分割された RAR (ボリューム) には対応していません"),
            (new FatalError(FatalKind.TooManyEntries, new ZipEntryRef(2, "c.txt")), "FATAL: エントリ #3 \"c.txt\": エントリ数が上限を超えています"),
            (new FatalError(FatalKind.ArchiveOpenFailed, Detail: "ERAR_EOPEN"), "FATAL: RAR を開けません (ERAR_EOPEN)"),
            (new FatalError(FatalKind.ArchiveUnreadable, Detail: "ERAR_BAD_DATA"), "FATAL: RAR として読み取れません (ERAR_BAD_DATA)"),
            (FatalError.RarLibraryUnavailable(RarLibraryFailure.NotFound, @"C:\app\UnRAR64.dll"),
                @"FATAL: UnRAR.dll を使用できないため、RAR を処理できません (見つかりません)。C:\app\UnRAR64.dll に UnRAR.dll 7.23 (x64) の UnRAR64.dll を置いてください。ZIP の処理には影響しません。"),
        };
        foreach (var (fatal, message) in fatals)
        {
            var fs = new FakeFileSystem();
            var h = new CommandHarness([], fs, createTarget: false) { Mode = mode, Archive = RarPath, OpenRarArchive = FakeRarArchive.Failing(fatal) };

            var analyze = h.Analyze();
            Assert.Equal(PrepareStage.Archive, analyze.Outcome.PreparationFailure!.Stage);
            Assert.Null(analyze.Outcome.Analysis);
            Assert.Empty(analyze.OutputLines);
            Assert.Equal([message, AnalyzeOutput.FatalClosing], analyze.ErrorLines);

            var delete = h.Delete(entriesPath: CommandHarness.WriteEntries("\n"));
            Assert.Equal(PrepareStage.Archive, delete.Outcome.PreparationFailure!.Stage);
            Assert.Equal([message, DeleteOutput.PrepareAborted], delete.ErrorLines);
            Assert.Empty(delete.OutputLines);
        }
    }

    // U19: 手順10 の open の失敗は target ルート確認後の FATAL。analyze は判定済み 0 / 未判定 N。Fast は開かないので影響しない。
    [Fact]
    public void U19_ContentSessionOpenFailure_IsPrepareFatalAfterTargetRoot()
    {
        FakeRarArchive Archive() => new(new FakeRarEntry("a.txt", Hello), new FakeRarEntry("b.txt", Hello))
        {
            SessionFailure = new FatalError(FatalKind.ArchiveOpenFailed, Detail: "ERAR_EOPEN"),
        };

        var (h, rar) = Setup(RunMode.Strict, Archive());
        h.File("a.txt");
        var analyze = h.Analyze();
        var failure = analyze.Outcome.PreparationFailure!;
        Assert.Equal(PrepareStage.ContentSession, failure.Stage);
        Assert.True(failure.IsFatal);
        Assert.Equal([.. Header(RunMode.Strict), "判定済み: 0 エントリ", "未判定: 2 エントリ"], analyze.OutputLines);
        Assert.Equal(["FATAL: RAR を開けません (ERAR_EOPEN)", AnalyzeOutput.FatalClosing], analyze.ErrorLines);
        Assert.Equal(["OpenSession", "ArchiveClose"], rar.Calls);
        Assert.False(h.TouchedTargetEntries());

        var (d, _) = Setup(RunMode.Strict, Archive());
        d.File("a.txt");
        var prompt = new ScriptedPrompt(true, "y");
        var delete = d.Delete(yes: false, prompt: prompt);
        Assert.Equal(PrepareStage.ContentSession, delete.Outcome.PreparationFailure!.Stage);
        Assert.Empty(prompt.Asked);
        Assert.True(d.Exists("a.txt"));

        var (f, _) = Setup(RunMode.Fast, Archive());
        Assert.Equal(ExitStatus.Success, f.Analyze().Outcome.Status);
    }

    // U20: 誤配線の検出。RAR を開く関数が無い .rar、形式と開いたソースの不一致、要求の組立て時のセッションの組み合わせ。
    [Fact]
    public void U20_Miswiring_Throws()
    {
        var noOpener = new CommandHarness([], new FakeFileSystem()) { Archive = RarPath };
        Assert.Throws<InvalidOperationException>(() => noOpener.Analyze());

        var zipForRar = new CommandHarness([], new FakeFileSystem()) { Archive = RarPath };
        zipForRar.OpenRarArchive = (_, _) => ZipArchiveSource.Open(new MemoryStream(PipelineHarness.MakeZip(("a.txt", Hello))));
        Assert.Throws<InvalidOperationException>(() => zipForRar.Analyze());

        var rar = new FakeRarArchive(new FakeRarEntry("a.txt", Hello));
        var rarForZip = new CommandHarness([], new FakeFileSystem()) { OpenArchive = _ => new ZipOpenResult(rar, null) };
        Assert.Throws<InvalidOperationException>(() => rarForZip.Analyze());
        Assert.Equal(["ArchiveClose"], rar.Calls);
    }

    [Fact]
    public void U20_SessionFor_RejectsWrongCombination()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\target");
        var root = TargetRootValidator.Open(fs, @"C:\target", TargetLocationPolicy.None).Root!;
        var rar = new FakeRarArchive(new FakeRarEntry("a.txt", Hello));
        var session = (IRarReadSession)rar.OpenSession().Session!;
        var zip = ZipArchiveSource.Open(new MemoryStream(PipelineHarness.MakeZip(("a.txt", Hello)))).Source!;
        var id = new VolumeFileId(1, default);

        using (var strictRarWithout = new Prepared(rar, root, [], [], id))
        {
            Assert.Throws<InvalidOperationException>(() => strictRarWithout.SessionFor(RunMode.Strict));
            Assert.Null(strictRarWithout.SessionFor(RunMode.Fast));
        }

        var root2 = TargetRootValidator.Open(fs, @"C:\target", TargetLocationPolicy.None).Root!;
        using (var zipWith = new Prepared(zip, root2, [], [], id, session))
        {
            Assert.Throws<InvalidOperationException>(() => zipWith.SessionFor(RunMode.Strict));
            Assert.Throws<InvalidOperationException>(() => zipWith.SessionFor(RunMode.Fast));
        }
    }

    // U21: 形式名の読み替え (docs/spec/cli.md#archive-wording)。RAR の実行では「ZIP」を「RAR」と表示する。
    [Fact]
    public void U21_RarWording()
    {
        // アーカイブ自身 (target 内の RAR)。
        var inside = @"C:\target\archive.rar";
        var self = new FakeRarArchive(new FakeRarEntry("archive.rar", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]));
        var fs = new FakeFileSystem();
        var h = new CommandHarness([], fs) { Archive = inside, OpenRarArchive = FakeRarArchive.Opener(self), Mode = RunMode.Fast };
        fs.AddFile(inside, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);
        var analyze = h.Analyze();
        Assert.Contains(analyze.OutputLines, l => l.EndsWith("(RAR 自身)", StringComparison.Ordinal));
        Assert.Equal(ReportText.RarFastWarning, analyze.OutputLines[0]);

        // entries の照合。
        var (e, _) = Setup(RunMode.Strict, new FakeRarArchive(new FakeRarEntry("a.txt", Hello)));
        var entries = e.Delete(entriesPath: CommandHarness.WriteEntries("b.txt\n"));
        Assert.Equal("入力エラー: --entries の 1 行目: RAR に一致するエントリがありません (\"b.txt\")", entries.ErrorLines[0]);

        // アーカイブ自身の個体の取得失敗。
        var (i, _) = Setup(RunMode.Strict, new FakeRarArchive(new FakeRarEntry("a.txt", Hello)));
        i.Fs.Get(RarPath).Errors[FakeOp.GetFileIdentity] = 5;
        Assert.StartsWith("FATAL: RAR 自身の File ID を取得できません", i.Analyze().ErrorLines[0], StringComparison.Ordinal);

        // Fast の確認プロンプト。
        var (p, _) = Setup(RunMode.Fast, new FakeRarArchive(new FakeRarEntry("a.txt", Hello)));
        var prompt = new ScriptedPrompt(true, "n");
        p.Delete(yes: false, prompt: prompt);
        Assert.StartsWith(ReportText.RarFastWarning, prompt.Asked[0], StringComparison.Ordinal);
    }

    // U22: セッションの close の失敗は伝え、target ルートとアーカイブは閉じる。
    [Fact]
    public void U22_SessionCloseFailure_Propagates_AndClosesRest()
    {
        var (h, rar) = Setup(RunMode.Strict, new FakeRarArchive(new FakeRarEntry("a.txt", Hello)));
        rar.OnSessionDispose = () => throw new InvalidOperationException("close");

        var fs = h.Fs;
        Assert.Throws<InvalidOperationException>(() => AnalyzeCommand.Run(new AnalyzeCommandRequest(
            RarPath, CommandHarness.TargetPath, RunMode.Strict,
            new CommandContext(fs, () => new TargetLocationPolicyResult(TargetLocationPolicy.None, null), Limits.Default, TextWriter.Null, TextWriter.Null,
                OpenRarArchive: FakeRarArchive.Opener(rar)))));

        Assert.Equal(["SessionClose", "ArchiveClose"], rar.Calls.TakeLast(2));
        Assert.Equal(0, fs.OpenHandleCount);
    }
}
