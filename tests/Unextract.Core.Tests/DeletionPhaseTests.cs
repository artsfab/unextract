using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;
using Unextract.Core.Zip;
using static Unextract.Core.Tests.Fakes.PipelineHarness;

namespace Unextract.Core.Tests;

// 削除フェーズ (SPEC §8.3、§8.4、PLAN.md §4) を偽ファイルシステムで確認する。ZIP は実際の ZipArchive で読む。
// 実行は runner 経由 (初回分類 → 確認 → 削除フェーズ) で行い、テスト用フックで競合を注入する。
public class DeletionPhaseTests
{
    private static readonly byte[] Hello = Bytes("hello");

    // 削除フェーズの開始前 (解析完了直後) に変更を注入するための runner フック付きの実行。
    private sealed class Scenario : IDisposable
    {
        private readonly PipelineHarness _harness;

        public Scenario(params (string Name, byte[]? Content)[] entries)
        {
            _harness = new PipelineHarness(MakeZip(entries));
            Contents = _harness.Contents;
        }

        public FakeFileSystem Fs => _harness.Fs;

        public IZipContentProvider Contents { get; set; }

        public ZipArchiveSource Source => _harness.Source;

        public DeletionHooks DeletionHooks { get; set; } = new();

        public Action? AfterAnalysis { get; set; }

        public Action? AwaitingConfirmation { get; set; }

        // 実行全体のモード (SPEC §15)。共通の安全性テストを Fast でも実行するために切り替える。
        public RunMode Mode { get; set; } = RunMode.Strict;

        public DeletionRequest? Request { get; private set; }

        public FakeNode File(string name, byte[]? content = null) => Fs.AddFile($@"C:\target\{name}", content ?? Hello);

        public FakeNode Node(string name) => Fs.Get($@"C:\target\{name}");

        public bool Exists(string name) => Fs.Find($@"C:\target\{name}") is not null;

        public Result Run(bool dryRun = false, bool yes = true, IConfirmationPrompt? prompt = null)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var phase = new CapturingPhase(new DeletionPhase(Fs, DeletionHooks), r => Request = r);
            var progress = new List<(int, int)>();
            var outcome = UnextractRunner.Run(new RunRequest(
                Source, ArchivePath, TargetPath, dryRun, yes, Fs, TargetLocationPolicy.None, Limits.Default,
                prompt ?? new Prompt(true, "y"), phase, output, error,
                DeletionProgress: (c, t) => progress.Add((c, t)),
                Contents: Contents,
                Hooks: new RunHooks { AfterAnalysis = AfterAnalysis, AwaitingConfirmation = AwaitingConfirmation },
                Mode: Mode));

            // どの経路でも、削除用ハンドルは対象ごとに閉じられている。
            Assert.Equal(0, Fs.OpenDeletionHandleCount);
            Assert.Equal(Fs.DeletionOpenCount, Fs.DeletionCloseCount);
            return new Result(outcome, output.ToString(), error.ToString(), progress);
        }

        public void Dispose() => _harness.Dispose();
    }

    private sealed record Result(RunOutcome Outcome, string Output, string Error, List<(int, int)> Progress)
    {
        public DeletionReport Deletion => Outcome.Deletion ?? throw new InvalidOperationException(Output + Error);

        public IEnumerable<string> DeletedNames => Deletion.Deleted.Select(e => e.Name);
    }

    private sealed class CapturingPhase(IDeletionPhase inner, Action<DeletionRequest> capture) : IDeletionPhase
    {
        public DeletionReport Delete(DeletionRequest request)
        {
            capture(request);
            return inner.Delete(request);
        }
    }

    private sealed class Prompt(bool interactive, string? answer) : IConfirmationPrompt
    {
        public int Asked { get; private set; }

        public bool IsInteractive => interactive;

        public string? Ask(string prompt)
        {
            Asked++;
            return answer;
        }
    }

    // ZIP 側の呼び出しを偽ファイルシステムと同じ記録に残す (テスト D03 の順序の確認)。
    private sealed class LoggingContents(IZipContentProvider inner, List<string> calls) : IZipContentProvider
    {
        public IZipEntryContent GetContent(int index)
        {
            calls.Add($"ZipContent {index}");
            return new Content(inner.GetContent(index), index, calls);
        }

        private sealed class Content(IZipEntryContent inner, int index, List<string> calls) : IZipEntryContent
        {
            public bool IsEncrypted => inner.IsEncrypted;

            public long Length => inner.Length;

            public uint Crc32 => inner.Crc32;

            public Stream Open()
            {
                calls.Add($"ZipOpen {index}");
                return inner.Open();
            }
        }
    }

    // 2回目の GetContent (削除フェーズの再比較) から、差し替えた内容を返す (テスト D13)。
    private sealed class RecheckContents(IZipContentProvider inner, Func<IZipEntryContent, IZipEntryContent> recheck) : IZipContentProvider
    {
        private readonly Dictionary<int, int> _count = [];

        public IZipEntryContent GetContent(int index)
        {
            _count[index] = _count.GetValueOrDefault(index) + 1;
            var content = inner.GetContent(index);
            return _count[index] >= 2 ? recheck(content) : content;
        }
    }

    private sealed class FakeContent(bool encrypted, long length, uint crc, Func<Stream> open) : IZipEntryContent
    {
        public bool IsEncrypted => encrypted;

        public long Length => length;

        public uint Crc32 => crc;

        public Stream Open() => open();
    }

    // 共通の安全性テストを両モードで実行する (PLAN_TESTS のモード違いの再利用の原則)。
    public static TheoryData<RunMode> BothModes => new() { RunMode.Strict, RunMode.Fast };

    private sealed class ThrowingStream(byte[] prefix) : MemoryStream(prefix)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            return n > 0 ? n : throw new InvalidDataException("injected");
        }
    }

    // D02: --yes で検証済みの MATCHED だけを削除。MODIFIED・特殊・ZIP にないファイル・ディレクトリ・ZIP は残る。
    // Fast では削除候補が SAME_SIZE になり、同じサイズで内容違いの changed.txt も削除される (SPEC §15.3、§15.5)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void D02_DeletesOnlyMatched(RunMode mode)
    {
        using var s = new Scenario(("same.txt", Hello), ("changed.txt", Hello), ("missing.txt", Hello), ("d/", null), ("d/ads.txt", Hello), ("d/deep.txt", Hello)) { Mode = mode };
        s.File("same.txt");
        s.File("changed.txt", Bytes("hellO"));
        s.Fs.AddDirectory(@"C:\target\d");
        s.File(@"d\ads.txt").ExtraStreams.Add(new StreamEntry(":x:$DATA", 1));
        s.File(@"d\deep.txt");
        s.File("unrelated.txt");

        var r = s.Run();

        var fast = mode == RunMode.Fast;
        Assert.Equal(ExitStatus.Success, r.Outcome.Status);
        string[] deleted = fast ? ["same.txt", "changed.txt", "d/deep.txt"] : ["same.txt", "d/deep.txt"];
        Assert.Equal(deleted, r.DeletedNames);
        Assert.False(s.Exists("same.txt"));
        Assert.False(s.Exists(@"d\deep.txt"));
        Assert.Equal(!fast, s.Exists("changed.txt"));
        Assert.True(s.Exists(@"d\ads.txt"));
        Assert.True(s.Exists("unrelated.txt"));
        Assert.True(s.Exists("d"));
        Assert.NotNull(s.Fs.Find(ArchivePath));
        Assert.Contains(fast ? "削除済み 3、DELETE_FAILED 0、未処理 0" : "削除済み 2、DELETE_FAILED 0、未処理 0", r.Output, StringComparison.Ordinal);
        Assert.Empty(r.Error);
        (int, int)[] progress = fast ? [(1, 3), (2, 3), (3, 3)] : [(1, 2), (2, 2)];
        Assert.Equal(progress, r.Progress);
    }

    // D01 (確認動作): y / Y だけで開始。空入力・EOF・その他・非対話で --yes なしは中止 (2) で、削除フェーズに入らない
    [Theory]
    [InlineData(true, "y", true, RunMode.Strict)]
    [InlineData(true, "Y", true, RunMode.Strict)]
    [InlineData(true, "", false, RunMode.Strict)]
    [InlineData(true, null, false, RunMode.Strict)]
    [InlineData(true, "n", false, RunMode.Strict)]
    [InlineData(true, "yes", false, RunMode.Strict)]
    [InlineData(false, "y", false, RunMode.Strict)]
    [InlineData(true, "y", true, RunMode.Fast)]
    [InlineData(true, "Y", true, RunMode.Fast)]
    [InlineData(true, "", false, RunMode.Fast)]
    [InlineData(true, null, false, RunMode.Fast)]
    [InlineData(true, "n", false, RunMode.Fast)]
    [InlineData(true, "yes", false, RunMode.Fast)]
    [InlineData(false, "y", false, RunMode.Fast)]
    public void D01_Confirmation(bool interactive, string? answer, bool deletes, RunMode mode)
    {
        using var s = new Scenario(("same.txt", Hello)) { Mode = mode };
        s.File("same.txt");
        var prompt = new Prompt(interactive, answer);

        var r = s.Run(yes: false, prompt: prompt);

        Assert.Equal(interactive ? 1 : 0, prompt.Asked);
        Assert.Equal(!deletes, s.Exists("same.txt"));
        Assert.Equal(deletes ? ExitStatus.Success : ExitStatus.UserCancelled, r.Outcome.Status);
        Assert.Equal(2, ExitCodes.ToProcessExitCode(ExitStatus.UserCancelled));
        if (!deletes)
        {
            Assert.Null(r.Outcome.Deletion);
            Assert.DoesNotContain(s.Fs.Calls, c => c.StartsWith("OpenDeletion", StringComparison.Ordinal));
            Assert.Contains("削除0件", r.Output, StringComparison.Ordinal);
        }
    }

    // 削除候補0件はプロンプトなしで成功。FATAL があれば削除フェーズに入らない (--yes でも)
    [Fact]
    public void NoCandidatesOrFatal_NeverEntersDeletionPhase()
    {
        using (var s = new Scenario(("missing.txt", Hello)))
        {
            var prompt = new Prompt(true, "y");
            var r = s.Run(yes: false, prompt: prompt);
            Assert.Equal(ExitStatus.Success, r.Outcome.Status);
            Assert.Equal(0, prompt.Asked);
            Assert.Null(s.Request);
        }

        using (var s = new Scenario(("same.txt", Hello), ("busy.txt", Hello)))
        {
            s.File("same.txt");
            s.File("busy.txt").Errors[FakeOp.OpenComparison] = 32;
            var r = s.Run(yes: true);
            Assert.Equal(ExitStatus.Error, r.Outcome.Status);
            Assert.Null(s.Request);
            Assert.True(s.Exists("same.txt"));
            Assert.DoesNotContain(s.Fs.Calls, c => c.StartsWith("OpenDeletion", StringComparison.Ordinal));
        }
    }

    // D03: 各対象について「削除用オープン1回 → 同じハンドルでの再検証 → 同じハンドルの読み取りと ZIP の再展開 → 最終確認 →
    // 削除指示 → 成立確認 → クローズ」の順。オープンの後、クローズまでにパスを使う呼び出しが無い
    [Fact]
    public void D03_CallOrder_UsesOnlyTheOpenedHandle()
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello));
        s.File("a.txt");
        s.File("b.txt");
        s.Contents = new LoggingContents(s.Source, s.Fs.Calls);
        var start = 0;
        s.AfterAnalysis = () => start = s.Fs.Calls.Count;

        var r = s.Run();

        Assert.Equal(["a.txt", "b.txt"], r.DeletedNames);
        var calls = s.Fs.Calls.Skip(start).Where(c => !c.StartsWith("Close root", StringComparison.Ordinal)).ToList();
        string[] Expected(int index, string name)
        {
            var path = $@"\\?\C:\target\{name}";
            return
            [
                $"OpenDeletion {path}",
                $"VolumeFileId {path}",
                $"ParentFileId {path}",
                $"FinalPath {path}",
                $"Standard {path}",
                $"Basic {path}",
                $"AttributeTag {path}",
                $"Streams {path}",
                $"ZipContent {index}",
                $"ZipOpen {index}",
                $"Read {path}",
                $"Read {path}",
                $"Standard {path}",
                $"Streams {path}",
                $"Basic {path}",
                $"Disposition 0x3 {path}",
                $"Standard {path}",
                $"Close deletion {path}",
            ];
        }

        Assert.Equal([.. Expected(0, "a.txt"), .. Expected(1, "b.txt")], calls);

        // パスを使う呼び出しは各対象の OpenDeletion の1回だけ。
        string[] pathCalls = ["OpenDeletion", "CheckIdentity", "OpenComparison", "OpenEnumeration", "GetFileIdentity", "ConfirmTarget", "OpenTargetRoot"];
        Assert.Equal(2, calls.Count(c => pathCalls.Any(p => c.StartsWith(p + " ", StringComparison.Ordinal))));
    }

    // D24: --fast での削除処理の呼び出し順 (D03 の Fast 版)。「削除用オープン1回 → 同じハンドルでの再検証 → 最終確認 → 削除指示 →
    // 成立確認 → クローズ」。D03 との違いは、同じハンドルからの読み取りと ZIP エントリの再展開 (GetContent・Open()) が無いことだけ。
    // 再検証・最終確認で比較する項目は D03 と同じ。オープンの後にパスを使う呼び出しが無く、削除指示は D17 と同じ (0x3 を1回)。
    [Fact]
    public void D24_Fast_CallOrder_SkipsOnlyRecompare()
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello)) { Mode = RunMode.Fast };
        s.File("a.txt");
        s.File("b.txt");
        s.Contents = new LoggingContents(s.Source, s.Fs.Calls);
        var start = 0;
        s.AfterAnalysis = () => start = s.Fs.Calls.Count;
        var recompareHookCalled = false;
        s.DeletionHooks = new DeletionHooks { DuringRecompare = (_, _) => recompareHookCalled = true };

        var r = s.Run();

        Assert.Equal(["a.txt", "b.txt"], r.DeletedNames);
        var calls = s.Fs.Calls.Skip(start).Where(c => !c.StartsWith("Close root", StringComparison.Ordinal)).ToList();
        string[] Expected(string name)
        {
            var path = $@"\\?\C:\target\{name}";
            return
            [
                $"OpenDeletion {path}",
                $"VolumeFileId {path}",
                $"ParentFileId {path}",
                $"FinalPath {path}",
                $"Standard {path}",
                $"Basic {path}",
                $"AttributeTag {path}",
                $"Streams {path}",
                $"Standard {path}",
                $"Streams {path}",
                $"Basic {path}",
                $"Disposition 0x3 {path}",
                $"Standard {path}",
                $"Close deletion {path}",
            ];
        }

        Assert.Equal([.. Expected("a.txt"), .. Expected("b.txt")], calls);

        // 初回分類から削除まで、ZIP の内容には一度も触れない (ZipContent・ZipOpen が記録に無い)。再比較中のフックも呼ばれない。
        Assert.DoesNotContain(s.Fs.Calls, c => c.StartsWith("Zip", StringComparison.Ordinal));
        Assert.False(recompareHookCalled);
        Assert.Equal(0, s.Request!.Comparer.InitialComparisons);
        Assert.Equal(0, s.Request.Comparer.RecheckComparisons);
        Assert.Equal(RunMode.Fast, s.Request.Mode);

        string[] pathCalls = ["OpenDeletion", "CheckIdentity", "OpenComparison", "OpenEnumeration", "GetFileIdentity", "ConfirmTarget", "OpenTargetRoot"];
        Assert.Equal(2, calls.Count(c => pathCalls.Any(p => c.StartsWith(p + " ", StringComparison.Ordinal))));
        var dispositions = calls.Where(c => c.StartsWith("Disposition", StringComparison.Ordinal)).ToList();
        Assert.Equal([@"Disposition 0x3 \\?\C:\target\a.txt", @"Disposition 0x3 \\?\C:\target\b.txt"], dispositions);
    }

    // 初回比較と2回目の比較は同じ ContentComparer を共有する。2回目の比較の読み取り量は初回の累計 (SPEC §11) に加えない
    [Fact]
    public void ContentComparer_IsSharedBetweenInitialAndRecheck()
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Bytes("world!")), ("c.txt", Hello));
        s.File("a.txt");
        s.File("b.txt", Bytes("world?"));
        s.File("c.txt");

        var r = s.Run();

        var comparer = s.Request!.Comparer;
        Assert.Equal(3, comparer.InitialComparisons);
        Assert.Equal(2, comparer.RecheckComparisons);
        Assert.Equal(Hello.Length + 6 + Hello.Length, comparer.TotalRead);
        Assert.Equal(["a.txt", "c.txt"], r.DeletedNames);
    }

    // D06: 再検証の情報取得 API の失敗 → 削除せず停止、以後は未処理
    [Theory]
    [InlineData(FakeOp.VolumeFileId, RunMode.Strict)]
    [InlineData(FakeOp.ParentFileId, RunMode.Strict)]
    [InlineData(FakeOp.FinalPath, RunMode.Strict)]
    [InlineData(FakeOp.Standard, RunMode.Strict)]
    [InlineData(FakeOp.Basic, RunMode.Strict)]
    [InlineData(FakeOp.AttributeTag, RunMode.Strict)]
    [InlineData(FakeOp.Streams, RunMode.Strict)]
    [InlineData(FakeOp.VolumeFileId, RunMode.Fast)]
    [InlineData(FakeOp.ParentFileId, RunMode.Fast)]
    [InlineData(FakeOp.FinalPath, RunMode.Fast)]
    [InlineData(FakeOp.Standard, RunMode.Fast)]
    [InlineData(FakeOp.Basic, RunMode.Fast)]
    [InlineData(FakeOp.AttributeTag, RunMode.Fast)]
    [InlineData(FakeOp.Streams, RunMode.Fast)]
    public void D06_RevalidationInfoFailure_Stops(FakeOp op, RunMode mode)
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello)) { Mode = mode };
        s.File("a.txt");
        var b = s.File("b.txt");
        s.File("c.txt");
        s.AfterAnalysis = () => b.Errors[op] = 1117;

        var r = s.Run();

        Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        Assert.Equal(["a.txt"], r.DeletedNames);
        Assert.Equal("b.txt", r.Deletion.Stop!.Entry.Name);
        Assert.Contains("同一性の再検証で不一致", r.Deletion.Stop.Reason, StringComparison.Ordinal);
        Assert.Contains("Win32 エラー 1117", r.Deletion.Stop.Reason, StringComparison.Ordinal);
        Assert.False(r.Deletion.Stop.PossiblyDeleted);
        Assert.Equal(1, r.Deletion.NotProcessedCount);
        Assert.True(s.Exists("b.txt"));
        Assert.True(s.Exists("c.txt"));
        Assert.DoesNotContain(s.Fs.Calls, c => c.StartsWith("Disposition", StringComparison.Ordinal) && c.EndsWith("b.txt", StringComparison.Ordinal));
    }

    // D04・D05 相当 (偽 FS): 確認待ち中の変更を再検証で検出して停止する。各値を1つずつ変える
    [Theory]
    [InlineData("FileId", RunMode.Strict)]
    [InlineData("VolumeSerial", RunMode.Strict)]
    [InlineData("ParentFileId", RunMode.Strict)]
    [InlineData("FinalPath", RunMode.Strict)]
    [InlineData("EndOfFile", RunMode.Strict)]
    [InlineData("LastWriteTime", RunMode.Strict)]
    [InlineData("ChangeTime", RunMode.Strict)]
    [InlineData("Attributes", RunMode.Strict)]
    [InlineData("Links", RunMode.Strict)]
    [InlineData("Streams", RunMode.Strict)]
    [InlineData("ReparseTag", RunMode.Strict)]
    [InlineData("FileId", RunMode.Fast)]
    [InlineData("VolumeSerial", RunMode.Fast)]
    [InlineData("ParentFileId", RunMode.Fast)]
    [InlineData("FinalPath", RunMode.Fast)]
    [InlineData("EndOfFile", RunMode.Fast)]
    [InlineData("LastWriteTime", RunMode.Fast)]
    [InlineData("ChangeTime", RunMode.Fast)]
    [InlineData("Attributes", RunMode.Fast)]
    [InlineData("Links", RunMode.Fast)]
    [InlineData("Streams", RunMode.Fast)]
    [InlineData("ReparseTag", RunMode.Fast)]
    public void Revalidation_DetectsEachChange(string change, RunMode mode)
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello)) { Mode = mode };
        var a = s.File("a.txt");
        s.File("b.txt");
        s.AwaitingConfirmation = () =>
        {
            switch (change)
            {
                case "FileId": a.Id = s.Fs.NextId(); break;
                case "VolumeSerial": a.VolumeSerial++; break;
                case "ParentFileId": a.Parent!.Id = s.Fs.NextId(); break;
                case "FinalPath": a.FinalPathOverride = @"\\?\C:\TARGET\a.txt"; break;
                case "EndOfFile": a.Content = Bytes("hello!"); break;
                case "LastWriteTime": a.LastWriteTime++; break;
                case "ChangeTime": a.ChangeTime++; break;
                case "Attributes": a.Attributes |= 0x2; break;
                case "Links": a.Links = 2; break;
                case "Streams": a.ExtraStreams.Add(new StreamEntry(":Zone.Identifier:$DATA", 10)); break;
                case "ReparseTag": a.ReparseTag = FakeNode.ReparseTagSymlink; break;
            }
        };

        var r = s.Run(yes: false);

        Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        Assert.Empty(r.DeletedNames);
        Assert.Equal("a.txt", r.Deletion.Stop!.Entry.Name);
        Assert.StartsWith("同一性の再検証で不一致", r.Deletion.Stop.Reason, StringComparison.Ordinal);
        Assert.Equal(1, r.Deletion.NotProcessedCount);
        Assert.True(s.Exists("a.txt"));
        Assert.True(s.Exists("b.txt"));
        Assert.Empty(s.Fs.Deleted);
    }

    // D10 相当 (偽 FS): メタデータを保ったまま内容だけを変える → 再検証は通り、2回目の全バイト比較で検出して停止
    [Fact]
    public void D10_ContentOnlyChange_DetectedByRecompare()
    {
        using var s = new Scenario(("a.txt", Hello));
        var a = s.File("a.txt");
        s.AwaitingConfirmation = () => a.Content = Bytes("jello");

        var r = s.Run(yes: false);

        Assert.Equal("2回目の全バイト比較で内容が一致しません", r.Deletion.Stop!.Reason);
        Assert.True(s.Exists("a.txt"));
        Assert.Equal(1, s.Request!.Comparer.RecheckComparisons);
    }

    // D25 相当 (偽 FS): --fast で (a) 初回分類の前から内容が ZIP と異なる同サイズのファイル、(b) 確認待ち中にメタデータを保ったまま
    // 内容だけを変えたファイル → (a) SAME_SIZE と表示され削除される、(b) 再検証と最終確認を通り削除される (SPEC §15.5 の保証しない事項)。
    // 実 NTFS での確認は Win の D25。
    [Fact]
    public void D25_Fast_ContentOnlyDifferences_AreDeleted()
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello)) { Mode = RunMode.Fast };
        s.File("a.txt", Bytes("jello"));
        var b = s.File("b.txt");
        s.AwaitingConfirmation = () => b.Content = Bytes("jello");

        var r = s.Run(yes: false);

        Assert.Equal(ExitStatus.Success, r.Outcome.Status);
        Assert.Equal(Classification.SameSize, r.Outcome.Analysis!.Results[0].Classification);
        Assert.Contains("SAME_SIZE (2):", r.Output, StringComparison.Ordinal);
        Assert.Equal(["a.txt", "b.txt"], r.DeletedNames);
        Assert.Null(r.Deletion.Stop);
        Assert.False(s.Exists("a.txt"));
        Assert.False(s.Exists("b.txt"));
        Assert.Equal(0, s.Request!.Comparer.RecheckComparisons);
    }

    // D13: 2回目の比較中の ZIP 側の異常 (CRC 不一致、Length 超過、終端欠落、読み取り例外、暗号化) → 削除せず停止。DELETE_FAILED にしない
    [Theory]
    [InlineData("crc", "エントリの CRC-32 が一致しません")]
    [InlineData("too-long", "エントリの内容が宣言展開量を超えています")]
    [InlineData("too-short", "エントリの内容が宣言展開量より短いです")]
    [InlineData("throw", "エントリの内容を読み取れません")]
    [InlineData("open-throw", "エントリの内容を読み取れません")]
    [InlineData("encrypted", "暗号化されたエントリです")]
    public void D13_ZipAnomalyDuringRecompare_Stops(string anomaly, string expected)
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello));
        s.File("a.txt");
        s.File("b.txt");
        s.Contents = new RecheckContents(s.Contents, c => anomaly switch
        {
            "crc" => new FakeContent(false, c.Length, c.Crc32 ^ 1, c.Open),
            "too-long" => new FakeContent(false, c.Length, c.Crc32, () => new MemoryStream(Bytes("hello!"))),
            "too-short" => new FakeContent(false, c.Length, c.Crc32, () => new MemoryStream(Bytes("hell"))),
            "throw" => new FakeContent(false, c.Length, c.Crc32, () => new ThrowingStream(Bytes("he"))),
            "open-throw" => new FakeContent(false, c.Length, c.Crc32, () => throw new InvalidDataException("injected")),
            _ => new FakeContent(true, c.Length, c.Crc32, c.Open),
        });

        var r = s.Run();

        Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        Assert.Empty(r.Deletion.Failed);
        Assert.Equal("a.txt", r.Deletion.Stop!.Entry.Name);
        Assert.StartsWith($"2回目の全バイト比較で異常: {expected}", r.Deletion.Stop.Reason, StringComparison.Ordinal);
        Assert.True(s.Exists("a.txt"));
        Assert.True(s.Exists("b.txt"));
        Assert.DoesNotContain(s.Fs.Calls, c => c.StartsWith("Disposition", StringComparison.Ordinal));
    }

    // D11 (偽 FS): 削除用オープンが 32 / 5 で、識別確認がスナップショットと一致する通常ファイルに見える → DELETE_FAILED (削除しない)、
    // 後続は削除される。停止はないが DELETE_FAILED があるためエラー (DEC-18)。
    // 識別確認は拒否の理由も、拒否されたオープンと同じ個体であることも保証しない (SPEC §8.4 の限界)。一致しても削除しないことだけを確かめる。
    [Theory]
    [InlineData(32, RunMode.Strict)]
    [InlineData(5, RunMode.Strict)]
    [InlineData(32, RunMode.Fast)]
    [InlineData(5, RunMode.Fast)]
    public void D11_OpenDeniedButIdentityLooksSame_IsDeleteFailedWithoutGuaranteeingSameObject(int error, RunMode mode)
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello)) { Mode = mode };
        s.File("a.txt");
        var b = s.File("b.txt");
        s.File("c.txt");
        s.AfterAnalysis = () => b.Errors[FakeOp.OpenDeletion] = error;

        var r = s.Run();

        Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        Assert.Equal(1, ExitCodes.ToProcessExitCode(r.Outcome.Status));
        Assert.Equal(["a.txt", "c.txt"], r.DeletedNames);
        var failure = Assert.Single(r.Deletion.Failed);
        Assert.Equal("b.txt", failure.Entry.Name);
        Assert.Contains($"Win32 エラー {error}", failure.Reason, StringComparison.Ordinal);
        Assert.Null(r.Deletion.Stop);
        Assert.True(s.Exists("b.txt"));
        Assert.Contains(@"CheckIdentity \\?\C:\target\b.txt", s.Fs.Calls);
        Assert.Contains("  b.txt: 削除用に開けません", r.Output, StringComparison.Ordinal);
        Assert.Contains("削除済み 2、DELETE_FAILED 1、未処理 0", r.Output, StringComparison.Ordinal);
        Assert.Contains("DELETE_FAILED が 1 件あるため、エラーとして終了します", r.Error, StringComparison.Ordinal);
    }

    // D14 (偽 FS): 削除用オープンの段階で同一性に疑義 → 停止。DELETE_FAILED で続行しない
    [Theory]
    [InlineData("vanished", RunMode.Strict)]
    [InlineData("parent-vanished", RunMode.Strict)]
    [InlineData("parent-is-file", RunMode.Strict)]
    [InlineData("reparse", RunMode.Strict)]
    [InlineData("parent-replaced", RunMode.Strict)]
    [InlineData("file-id", RunMode.Strict)]
    [InlineData("directory", RunMode.Strict)]
    [InlineData("junction", RunMode.Strict)]
    [InlineData("denied-other-object", RunMode.Strict)]
    [InlineData("delete-pending", RunMode.Strict)]
    [InlineData("vanished", RunMode.Fast)]
    [InlineData("parent-vanished", RunMode.Fast)]
    [InlineData("parent-is-file", RunMode.Fast)]
    [InlineData("reparse", RunMode.Fast)]
    [InlineData("parent-replaced", RunMode.Fast)]
    [InlineData("file-id", RunMode.Fast)]
    [InlineData("directory", RunMode.Fast)]
    [InlineData("junction", RunMode.Fast)]
    [InlineData("denied-other-object", RunMode.Fast)]
    [InlineData("delete-pending", RunMode.Fast)]
    public void D14_IdentityInDoubtAtOpen_Stops(string situation, RunMode mode)
    {
        using var s = new Scenario(("d/", null), ("d/a.txt", Hello), ("z.txt", Hello)) { Mode = mode };
        var d = s.Fs.AddDirectory(@"C:\target\d");
        var a = s.File(@"d\a.txt");
        s.File("z.txt");
        s.AwaitingConfirmation = () =>
        {
            switch (situation)
            {
                case "vanished":
                    s.Fs.Remove(a);
                    break;
                case "parent-vanished":
                    s.Fs.Remove(d);
                    break;
                case "parent-is-file":
                    s.Fs.Remove(d);
                    s.File("d");
                    break;
                case "reparse":
                    a.Attributes |= 0x400;
                    a.ReparseTag = FakeNode.ReparseTagSymlink;
                    break;
                case "parent-replaced":
                    s.Fs.Remove(d);
                    s.Fs.Remove(a);
                    var replacement = s.Fs.AddDirectory(@"C:\target\d");
                    a.Parent = replacement;
                    replacement.Children.Add(a);
                    break;
                case "file-id":
                    s.Fs.Remove(a);
                    s.File(@"d\a.txt");
                    break;
                case "directory":
                    s.Fs.Remove(a);
                    s.Fs.AddDirectory(@"C:\target\d\a.txt");
                    break;
                case "junction":
                    s.Fs.Remove(a);
                    s.Fs.AddJunction(@"C:\target\d\a.txt", d);
                    break;
                case "denied-other-object":
                    s.Fs.Remove(a);
                    s.File(@"d\a.txt").Errors[FakeOp.OpenDeletion] = 5;
                    break;
                case "delete-pending":
                    a.DeletePending = true;
                    break;
            }
        };

        var r = s.Run(yes: false);

        Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        Assert.Empty(r.Deletion.Deleted);
        Assert.Empty(r.Deletion.Failed);
        Assert.Equal("d/a.txt", r.Deletion.Stop!.Entry.Name);
        Assert.Equal(1, r.Deletion.NotProcessedCount);
        Assert.True(s.Exists("z.txt"));
        Assert.Empty(s.Fs.Deleted);
        Assert.DoesNotContain(s.Fs.Calls, c => c.StartsWith("Disposition", StringComparison.Ordinal));

        // 2・3 では識別確認をしない。32・5 だけが識別確認に進む (PLAN.md §4)。
        var identityChecked = s.Fs.Calls.Any(c => c.StartsWith("CheckIdentity", StringComparison.Ordinal));
        Assert.Equal(situation is "directory" or "junction" or "denied-other-object" or "delete-pending", identityChecked);
    }

    // D15: 対応表に無いエラーコード → 識別確認をせずに停止。識別確認自体の失敗 → 停止
    [Theory]
    [InlineData(1920, 0, RunMode.Strict)]
    [InlineData(1921, 0, RunMode.Strict)]
    [InlineData(4390, 0, RunMode.Strict)]
    [InlineData(362, 0, RunMode.Strict)]
    [InlineData(123, 0, RunMode.Strict)]
    [InlineData(1, 0, RunMode.Strict)]
    [InlineData(32, 2, RunMode.Strict)]
    [InlineData(5, 5, RunMode.Strict)]
    [InlineData(32, 1117, RunMode.Strict)]
    [InlineData(1920, 0, RunMode.Fast)]
    [InlineData(1921, 0, RunMode.Fast)]
    [InlineData(4390, 0, RunMode.Fast)]
    [InlineData(362, 0, RunMode.Fast)]
    [InlineData(123, 0, RunMode.Fast)]
    [InlineData(1, 0, RunMode.Fast)]
    [InlineData(32, 2, RunMode.Fast)]
    [InlineData(5, 5, RunMode.Fast)]
    [InlineData(32, 1117, RunMode.Fast)]
    public void D15_UnknownErrorOrIdentityCheckFailure_Stops(int openError, int identityError, RunMode mode)
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello)) { Mode = mode };
        var a = s.File("a.txt");
        s.File("b.txt");
        s.AfterAnalysis = () =>
        {
            a.Errors[FakeOp.OpenDeletion] = openError;
            if (identityError != 0)
            {
                a.Errors[FakeOp.CheckIdentity] = identityError;
            }
        };

        var r = s.Run();

        Assert.Empty(r.Deletion.Failed);
        Assert.Equal("a.txt", r.Deletion.Stop!.Entry.Name);
        Assert.Contains($"Win32 エラー {openError}", r.Deletion.Stop.Reason, StringComparison.Ordinal);
        Assert.True(s.Exists("b.txt"));
        Assert.Equal(identityError != 0, s.Fs.Calls.Any(c => c.StartsWith("CheckIdentity", StringComparison.Ordinal)));
    }

    // D16 (偽 FS): 最終確認の直前 (再比較の後) の ADS 追加・hardlink 追加・read-only 付与・削除保留 → 最終確認で停止
    [Theory]
    [InlineData("ads", RunMode.Strict)]
    [InlineData("hardlink", RunMode.Strict)]
    [InlineData("readonly", RunMode.Strict)]
    [InlineData("pending", RunMode.Strict)]
    [InlineData("ads", RunMode.Fast)]
    [InlineData("hardlink", RunMode.Fast)]
    [InlineData("readonly", RunMode.Fast)]
    [InlineData("pending", RunMode.Fast)]
    public void D16_ChangeBeforeFinalCheck_Stops(string change, RunMode mode)
    {
        using var s = new Scenario(("a.txt", Hello)) { Mode = mode };
        var a = s.File("a.txt");
        s.DeletionHooks = new DeletionHooks
        {
            BeforeFinalCheck = (_, _) =>
            {
                switch (change)
                {
                    case "ads": a.ExtraStreams.Add(new StreamEntry(":x:$DATA", 1)); break;
                    case "hardlink": a.Links = 2; break;
                    case "readonly": a.Attributes |= 0x1; break;
                    case "pending": a.DeletePending = true; break;
                }
            },
        };

        var r = s.Run();

        Assert.StartsWith("最終確認で不一致", r.Deletion.Stop!.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(s.Fs.Calls, c => c.StartsWith("Disposition", StringComparison.Ordinal));
        Assert.Empty(s.Fs.Deleted);
    }

    // D08: 削除指示の失敗 → 停止 (削除されていない)。成立確認の失敗 → 停止し「削除された可能性あり」。削除済み件数は正確
    [Theory]
    [InlineData("disposition-error", RunMode.Strict)]
    [InlineData("readonly-before-disposition", RunMode.Strict)]
    [InlineData("confirm-error", RunMode.Strict)]
    [InlineData("disposition-error", RunMode.Fast)]
    [InlineData("readonly-before-disposition", RunMode.Fast)]
    [InlineData("confirm-error", RunMode.Fast)]
    public void D08_DispositionOrConfirmationFailure_Stops(string failure, RunMode mode)
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello)) { Mode = mode };
        s.File("a.txt");
        var b = s.File("b.txt");
        s.File("c.txt");
        s.DeletionHooks = new DeletionHooks
        {
            BeforeDisposition = (file, _) =>
            {
                if (file.Entry.Name != "b.txt")
                {
                    return;
                }

                switch (failure)
                {
                    case "disposition-error": b.Errors[FakeOp.Disposition] = 1117; break;
                    case "readonly-before-disposition": b.Attributes |= 0x1; break;
                    case "confirm-error": b.Errors[FakeOp.Standard] = 1117; break;
                }
            },
        };

        var r = s.Run();

        Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        Assert.Equal(["a.txt"], r.DeletedNames);
        Assert.Equal(1, r.Deletion.NotProcessedCount);
        Assert.True(s.Exists("c.txt"));
        var stop = r.Deletion.Stop!;
        Assert.Equal("b.txt", stop.Entry.Name);
        if (failure == "confirm-error")
        {
            // 指示は成立していたが確認できなかった。クローズで名前は消えている。報告は「削除された可能性あり」。
            Assert.True(stop.PossiblyDeleted);
            Assert.StartsWith("削除の成立を確認できません", stop.Reason, StringComparison.Ordinal);
            Assert.Contains("(削除された可能性あり)", r.Error, StringComparison.Ordinal);
        }
        else
        {
            Assert.False(stop.PossiblyDeleted);
            Assert.StartsWith("削除の指示が失敗", stop.Reason, StringComparison.Ordinal);
            Assert.True(s.Exists("b.txt"));
        }

        Assert.Contains("削除済み 1、DELETE_FAILED 0、未処理 1", r.Output, StringComparison.Ordinal);
    }

    // D17: 削除指示は削除用ハンドルに対して1回だけ、flags はちょうど 0x3 (IGNORE_READONLY_ATTRIBUTE 0x10 を含まない)
    [Theory]
    [MemberData(nameof(BothModes))]
    public void D17_DispositionFlagsAreExactly0x3(RunMode mode)
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello)) { Mode = mode };
        s.File("a.txt");
        s.File("b.txt");

        s.Run();

        var dispositions = s.Fs.Calls.Where(c => c.StartsWith("Disposition", StringComparison.Ordinal)).ToList();
        Assert.Equal([@"Disposition 0x3 \\?\C:\target\a.txt", @"Disposition 0x3 \\?\C:\target\b.txt"], dispositions);
        Assert.Equal(0x3u, DeletionPhase.DispositionFlags);
        Assert.Equal(0u, DeletionPhase.DispositionFlags & 0x10u);
    }

    // D18: 削除指示の API が成功を返しても DeletePending が false なら DELETED にせず停止し「削除された可能性あり」
    [Theory]
    [MemberData(nameof(BothModes))]
    public void D18_ApiSuccessWithoutDeletePending_IsNotDeleted(RunMode mode)
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello)) { Mode = mode };
        s.File("a.txt").DispositionHasNoEffect = true;
        s.File("b.txt");

        var r = s.Run();

        Assert.Empty(r.Deletion.Deleted);
        Assert.True(r.Deletion.Stop!.PossiblyDeleted);
        Assert.Contains("DeletePending が false", r.Deletion.Stop.Reason, StringComparison.Ordinal);
        Assert.True(s.Exists("a.txt"));
        Assert.True(s.Exists("b.txt"));
        Assert.Contains("停止: a.txt: ", r.Error, StringComparison.Ordinal);
        Assert.Contains("(削除された可能性あり)", r.Error, StringComparison.Ordinal);
    }

    // 例外の経路: フックや target の読み取りが例外を投げても停止として報告し、削除用ハンドルは閉じられる
    [Theory]
    [InlineData("hook")]
    [InlineData("read")]
    public void Exception_StopsAndClosesHandle(string where)
    {
        using var s = new Scenario(("a.txt", Hello), ("b.txt", Hello));
        var a = s.File("a.txt");
        s.File("b.txt");
        if (where == "hook")
        {
            s.DeletionHooks = new DeletionHooks { AfterRevalidation = (_, _) => throw new InvalidOperationException("injected") };
        }
        else
        {
            s.DeletionHooks = new DeletionHooks { AfterRevalidation = (_, _) => a.ThrowOnRead = true };
        }

        var r = s.Run();

        Assert.StartsWith("想定外の例外 (InvalidOperationException", r.Deletion.Stop!.Reason, StringComparison.Ordinal);
        Assert.False(r.Deletion.Stop.PossiblyDeleted);
        Assert.True(s.Exists("a.txt"));
        Assert.True(s.Exists("b.txt"));
    }

    // Y02: 解析完了直後に MATCHED の内容を変える。dry-run は削除用オープン・再検証・2回目の比較・削除を一切呼ばず成功、
    // 通常実行は削除直前再検証で停止する
    [Fact]
    public void Y02_DryRunNeverEntersDeletionPhase()
    {
        foreach (var dryRun in new[] { true, false })
        {
            using var s = new Scenario(("a.txt", Hello));
            var a = s.File("a.txt");
            var start = 0;
            s.AfterAnalysis = () =>
            {
                start = s.Fs.Calls.Count;
                a.Content = Bytes("jello");
            };

            var r = s.Run(dryRun: dryRun);

            if (dryRun)
            {
                Assert.Equal(ExitStatus.Success, r.Outcome.Status);
                Assert.Null(s.Request);
                Assert.Null(r.Outcome.Deletion);
                Assert.DoesNotContain(s.Fs.Calls.Skip(start), c => !c.StartsWith("Close root", StringComparison.Ordinal));
                Assert.Equal(0, s.Fs.DeletionOpenCount);
            }
            else
            {
                Assert.Equal(ExitStatus.Error, r.Outcome.Status);
                Assert.NotNull(r.Deletion.Stop);
                Assert.Equal(1, s.Request!.Comparer.RecheckComparisons);
            }

            Assert.True(s.Exists("a.txt"));
        }
    }

    // Y02 (Fast 同士): 解析完了直後に SAME_SIZE の内容を書き換え、LastWriteTime も変える。dry-run は削除用オープン・再検証・削除を
    // 一切呼ばず成功、通常実行は削除直前再検証 (同一性の再検証) で停止する。メタデータを保った変更は D25 で扱う。
    [Fact]
    public void Y02_Fast_DryRunNeverEntersDeletionPhase()
    {
        foreach (var dryRun in new[] { true, false })
        {
            using var s = new Scenario(("a.txt", Hello)) { Mode = RunMode.Fast };
            var a = s.File("a.txt");
            var start = 0;
            s.AfterAnalysis = () =>
            {
                start = s.Fs.Calls.Count;
                a.Content = Bytes("jello");
                a.LastWriteTime++;
                a.ChangeTime++;
            };

            var r = s.Run(dryRun: dryRun);

            if (dryRun)
            {
                Assert.Equal(ExitStatus.Success, r.Outcome.Status);
                Assert.Null(s.Request);
                Assert.Null(r.Outcome.Deletion);
                Assert.DoesNotContain(s.Fs.Calls.Skip(start), c => !c.StartsWith("Close root", StringComparison.Ordinal));
                Assert.Equal(0, s.Fs.DeletionOpenCount);
            }
            else
            {
                Assert.Equal(ExitStatus.Error, r.Outcome.Status);
                Assert.Equal("同一性の再検証で不一致: LastWriteTime", r.Deletion.Stop!.Reason);
            }

            Assert.True(s.Exists("a.txt"));
        }
    }

    // O03 (実行): DELETE_FAILED のパスと理由、停止原因のパスと理由、削除済み・DELETE_FAILED・未処理の件数。結果は stdout、停止は stderr
    [Fact]
    public void O03_DeletionOutput()
    {
        using var s = new Scenario(("a.txt", Hello), ("busy.txt", Hello), ("bad\u202E.txt", Hello), ("d.txt", Hello), ("e.txt", Hello));
        s.File("a.txt");
        var busy = s.File("busy.txt");
        var bad = s.File("bad\u202E.txt");
        s.File("d.txt");
        s.File("e.txt");
        s.AfterAnalysis = () =>
        {
            busy.Errors[FakeOp.OpenDeletion] = 32;
            bad.Content = Bytes("jello");
        };

        var r = s.Run();

        var output = r.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            [
                "DELETE_FAILED (1):",
                "  busy.txt: 削除用に開けません (OpenDeletion が失敗 (Win32 エラー 32))。識別確認の時点ではスナップショットと一致する通常ファイルに見えるため、削除せずに残しました",
                "削除済み 1、DELETE_FAILED 1、未処理 2",
            ],
            output[^3..]);
        var error = r.Error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            [
                "停止: bad\\u{202E}.txt: 2回目の全バイト比較で内容が一致しません",
                "以後の削除を停止しました。削除済みのファイルは戻りません。(削除済み 1、DELETE_FAILED 1、未処理 2)",
            ],
            error);
        Assert.Equal([(1, 5), (2, 5), (3, 5)], r.Progress);
    }

    // O03 (Fast 同士): 上の O03 の停止は2回目の全バイト比較 (Strict だけ) によるため、Fast では停止の原因を同一性の再検証
    // (LastWriteTime も変える) にする。DELETE_FAILED のパスと理由、停止原因のパスと理由、件数の表示は Strict と同じ形。
    [Fact]
    public void O03_Fast_DeletionOutput()
    {
        using var s = new Scenario(("a.txt", Hello), ("busy.txt", Hello), ("bad\u202E.txt", Hello), ("d.txt", Hello), ("e.txt", Hello)) { Mode = RunMode.Fast };
        s.File("a.txt");
        var busy = s.File("busy.txt");
        var bad = s.File("bad\u202E.txt");
        s.File("d.txt");
        s.File("e.txt");
        s.AfterAnalysis = () =>
        {
            busy.Errors[FakeOp.OpenDeletion] = 32;
            bad.Content = Bytes("jello");
            bad.LastWriteTime++;
        };

        var r = s.Run();

        var output = r.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(AnalysisReport.FastWarning, output[0]);
        Assert.Equal(
            [
                "DELETE_FAILED (1):",
                "  busy.txt: 削除用に開けません (OpenDeletion が失敗 (Win32 エラー 32))。識別確認の時点ではスナップショットと一致する通常ファイルに見えるため、削除せずに残しました",
                "削除済み 1、DELETE_FAILED 1、未処理 2",
            ],
            output[^3..]);
        var error = r.Error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            [
                "停止: bad\\u{202E}.txt: 同一性の再検証で不一致: LastWriteTime",
                "以後の削除を停止しました。削除済みのファイルは戻りません。(削除済み 1、DELETE_FAILED 1、未処理 2)",
            ],
            error);
        Assert.Equal([(1, 5), (2, 5), (3, 5)], r.Progress);
    }

    // 識別確認の比較項目 (SPEC §8.4)。どれか1つでも違えば不一致
    [Fact]
    public void IdentityMismatch_ComparesEachItem()
    {
        var snapshot = new TargetSnapshot(1, new FileId(2, 0), new FileId(3, 0), 5, 6, 7, 0x20, 1, [], 0, @"\\?\C:\t\a.txt");
        var same = new IdentityCheckInfo(new VolumeFileId(1, new FileId(2, 0)), new FileId(3, 0), @"\\?\C:\t\a.txt", false, false, 0x20, 0);

        Assert.Null(DeletionPhase.IdentityMismatch(same, snapshot));
        Assert.Equal("File ID", DeletionPhase.IdentityMismatch(same with { Id = new VolumeFileId(9, new FileId(2, 0)) }, snapshot));
        Assert.Equal("File ID", DeletionPhase.IdentityMismatch(same with { Id = new VolumeFileId(1, new FileId(9, 0)) }, snapshot));
        Assert.Equal("親 File ID", DeletionPhase.IdentityMismatch(same with { ParentFileId = new FileId(9, 0) }, snapshot));
        Assert.Equal("最終パス", DeletionPhase.IdentityMismatch(same with { FinalPath = @"\\?\C:\T\a.txt" }, snapshot));
        Assert.Equal("ディレクトリ", DeletionPhase.IdentityMismatch(same with { IsDirectory = true }, snapshot));
        Assert.Equal("reparse point", DeletionPhase.IdentityMismatch(same with { Attributes = 0x420 }, snapshot));
        Assert.Equal("reparse point", DeletionPhase.IdentityMismatch(same with { ReparseTag = 0xA000000C }, snapshot));
        Assert.Equal("削除保留中", DeletionPhase.IdentityMismatch(same with { DeletePending = true }, snapshot));
    }
}
