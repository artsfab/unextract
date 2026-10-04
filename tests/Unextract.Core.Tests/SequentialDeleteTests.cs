using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;
using Unextract.Core.Zip;
using static Unextract.Core.Tests.Fakes.PipelineHarness;

namespace Unextract.Core.Tests;

// delete の逐次処理 (docs/SPEC.md#execution、docs/spec/filesystem.md#special-files の事前判定、docs/spec/filesystem.md#baselines〜docs/spec/filesystem.md#failure-boundary、docs/spec/filesystem.md#open-errors) を偽ファイルシステムで確認する (S 系の Core)。
// ZIP は実際の ZipArchive で読む。競合は DeleteHooks (H1〜H5) で注入する。DeleteHarness.Run が毎回、削除用ハンドルの解放、
// 比較用ハンドルを開かないこと、パスを使う呼び出しが各エントリの OpenForDeletion 1回 (と失敗時の CheckIdentity 1回) だけであることを確かめる。
public class SequentialDeleteTests
{
    private static readonly byte[] Hello = Bytes("hello");

    public static TheoryData<RunMode> BothModes => new() { RunMode.Strict, RunMode.Fast };

    private static IEnumerable<string> Names(DeleteReport report, DeleteStatus status) =>
        report.Results.Where(r => r.Status == status).Select(r => r.Entry.Name);

    private static DeleteEntryResult StopOf(DeleteReport report) => report.Stop ?? throw new InvalidOperationException("STOP なし");

    private static bool Opened(DeleteHarness h, string name) =>
        h.Fs.Calls.Contains($@"OpenDeletion \\?\C:\target\{name.Replace('/', '\\')}");

    // ZIP 側の呼び出しを偽ファイルシステムと同じ記録に残す (呼び出し順の確認)。
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

    // 指定したエントリの内容だけを差し替える (ZIP 側の異常の注入)。
    private sealed class ReplacingContents(IZipContentProvider inner, int index, Func<IZipEntryContent, IZipEntryContent> replace) : IZipContentProvider
    {
        public IZipEntryContent GetContent(int i) => i == index ? replace(inner.GetContent(i)) : inner.GetContent(i);
    }

    private sealed class FakeContent(bool encrypted, long length, uint crc, Func<Stream> open) : IZipEntryContent
    {
        public bool IsEncrypted => encrypted;

        public long Length => length;

        public uint Crc32 => crc;

        public Stream Open() => open();
    }

    private sealed class ThrowingStream(byte[] prefix) : MemoryStream(prefix)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            return n > 0 ? n : throw new InvalidDataException("injected");
        }
    }

    // S01: 対象だけが削除され、MODIFIED・SKIPPED・ZIP にないファイル・ディレクトリ・ZIP は残る。結果は処理順に1件ずつ。
    // Fast では同じサイズで内容違いの changed.txt も削除される (docs/SPEC.md#modes、docs/SPEC.md#modes)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void S01_DeletesOnlyVerifiedFiles(RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(
            ("same.txt", Hello), ("changed.txt", Hello), ("size.txt", Hello), ("missing.txt", Hello), ("dir.txt", Hello),
            ("d/", null), ("d/ads.txt", Hello), ("d/deep.txt", Hello)))
        { Mode = mode };
        h.File("same.txt");
        h.File("changed.txt", Bytes("hellO"));
        h.File("size.txt", Bytes("hello!"));
        h.Fs.AddDirectory(@"C:\target\dir.txt");
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\ads.txt").ExtraStreams.Add(new StreamEntry(":x:$DATA", 1));
        h.File(@"d\deep.txt");
        h.File("unrelated.txt");

        var report = h.Run();

        var fast = mode == RunMode.Fast;
        Assert.Equal(
            [
                ("same.txt", DeleteStatus.Deleted, (SkipReason?)null),
                ("changed.txt", fast ? DeleteStatus.Deleted : DeleteStatus.Modified, null),
                ("size.txt", DeleteStatus.Modified, null),
                ("missing.txt", DeleteStatus.Missing, null),
                ("dir.txt", DeleteStatus.SkippedSpecialFile, SkipReason.Directory),
                ("d/ads.txt", DeleteStatus.SkippedSpecialFile, SkipReason.AlternateDataStream),
                ("d/deep.txt", DeleteStatus.Deleted, null),
            ],
            report.Results.Select(r => (r.Entry.Name, r.Status, r.SkipReason)));
        Assert.Equal(@"\\?\C:\target\d\deep.txt", report.Results[^1].Target);
        Assert.Equal(1, report.DirectoryCount);
        Assert.Equal(0, report.NotProcessedCount);
        Assert.Null(report.Stop);
        Assert.False(h.Exists("same.txt"));
        Assert.False(h.Exists(@"d\deep.txt"));
        Assert.Equal(!fast, h.Exists("changed.txt"));
        Assert.True(h.Exists("size.txt"));
        Assert.True(h.Exists(@"d\ads.txt"));
        Assert.True(h.Exists("dir.txt"));
        Assert.True(h.Exists("unrelated.txt"));
        Assert.True(h.Exists("d"));
        Assert.NotNull(h.Fs.Find(ArchivePath));
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (i, 8)), h.Progress);
    }

    // S05: 呼び出し順 (Strict)。各エントリ: (ディレクトリの初回のみ) 列挙 → OpenForDeletion 1回 → 同じハンドルで File ID・
    // 最終パスの照合 → Standard → 親 File ID の照合 → 残りの情報取得 (M0) → 同じハンドルからの読み取りと ZIP の Open() 1回 → 同じハンドルで最終確認 (M0 の全項目) →
    // SetDispositionEx(0x3) 1回 → DeletePending → クローズ。OpenForComparison は0回 (DeleteHarness が確かめる)。
    // S06: Fast は読み取りと ZIP の Open() を除いたもの。最終確認を含め、その他は同じ。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void S05_S06_CallOrder_UsesOnlyTheDeletionHandle(RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello))) { Mode = mode };
        h.File("a.txt");
        h.File("b.txt");
        h.ContentsOverride = new LoggingContents(h.Source, h.Fs.Calls);
        var duringCompare = 0;
        h.Hooks = new DeleteHooks { DuringCompare = (_, _) => duringCompare++ };
        var start = h.Fs.Calls.Count;

        var report = h.Run();

        Assert.Equal(["a.txt", "b.txt"], Names(report, DeleteStatus.Deleted));
        var calls = h.Fs.Calls.Skip(start)
            .Where(c => !c.StartsWith("ConfirmTarget", StringComparison.Ordinal) && !c.StartsWith("OpenTargetRoot", StringComparison.Ordinal)
                && !c.StartsWith("DirectoryInfo", StringComparison.Ordinal) && !c.StartsWith("FileSystemName", StringComparison.Ordinal)
                && !c.StartsWith("GetFileIdentity", StringComparison.Ordinal) && !c.StartsWith("Close root", StringComparison.Ordinal))
            .ToList();
        string[] Expected(int index, string name)
        {
            var path = $@"\\?\C:\target\{name}";
            string[] identity = [$"VolumeFileId {path}", $"ParentFileId {path}", $"FinalPath {path}"];
            string[] inspect = [$"Standard {path}", $"Basic {path}", $"AttributeTag {path}", $"Streams {path}"];
            string[] compare = mode == RunMode.Strict ? [$"ZipContent {index}", $"ZipOpen {index}", $"Read {path}", $"Read {path}"] : [];
            return
            [
                $"OpenDeletion {path}",
                $"VolumeFileId {path}",
                $"FinalPath {path}",
                $"Standard {path}",
                $"ParentFileId {path}",
                .. inspect.Skip(1),
                .. compare,
                .. identity,
                $"Standard {path}",
                $"Basic {path}",
                $"AttributeTag {path}",
                $"Streams {path}",
                $"Disposition 0x3 {path}",
                $"Standard {path}",
                $"Close deletion {path}",
            ];
        }

        Assert.Equal([@"Enumerate \\?\C:\target", .. Expected(0, "a.txt"), .. Expected(1, "b.txt")], calls);
        Assert.Equal(mode == RunMode.Strict ? 2 : 0, h.LastRun!.Comparer.Comparisons);
        Assert.Equal(mode == RunMode.Strict ? 2 : 0, duringCompare);
        if (mode == RunMode.Fast)
        {
            Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Zip", StringComparison.Ordinal));
        }
    }

    // S07: Fast で、同サイズ・内容違いのファイル、ZIP 側が CRC 不一致・破損・暗号化の同サイズエントリ → どちらも DELETED。
    // ZIP の Open()・CRC 計算なし。Strict の同じ操作は MODIFIED / STOP。
    [Theory]
    [InlineData("content", RunMode.Fast, DeleteStatus.Deleted)]
    [InlineData("crc", RunMode.Fast, DeleteStatus.Deleted)]
    [InlineData("encrypted", RunMode.Fast, DeleteStatus.Deleted)]
    [InlineData("content", RunMode.Strict, DeleteStatus.Modified)]
    [InlineData("crc", RunMode.Strict, DeleteStatus.Stopped)]
    [InlineData("encrypted", RunMode.Strict, DeleteStatus.Stopped)]
    public void S07_FastDeletesSameSizeWithoutReadingContent(string kind, RunMode mode, DeleteStatus expected)
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello))) { Mode = mode };
        h.File("a.txt", kind == "content" ? Bytes("jello") : Hello);
        if (kind != "content")
        {
            h.ContentsOverride = new ReplacingContents(h.Contents, 0, c => kind == "crc"
                ? new FakeContent(false, c.Length, c.Crc32 ^ 1, c.Open)
                : new FakeContent(true, c.Length, c.Crc32, c.Open));
        }

        var report = h.Run();

        Assert.Equal(expected, Assert.Single(report.Results).Status);
        Assert.Equal(expected != DeleteStatus.Deleted, h.Exists("a.txt"));
        if (mode == RunMode.Fast)
        {
            Assert.Empty(h.Contents.Calls);
            Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Read ", StringComparison.Ordinal));
        }
    }

    // S08: ディレクトリエントリと、削除後に空になるディレクトリは削除しない。DIRECTORY は結果行を出さず件数だけ。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void S08_DirectoriesAreNeverDeleted(RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("d/", null), ("d/a.txt", Hello), ("e/", null))) { Mode = mode };
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\a.txt");
        h.Fs.AddDirectory(@"C:\target\e");

        var report = h.Run();

        Assert.Equal(["d/a.txt"], report.Results.Select(r => r.Entry.Name));
        Assert.Equal(2, report.DirectoryCount);
        Assert.False(h.Exists(@"d\a.txt"));
        Assert.True(h.Exists("d"));
        Assert.True(h.Exists("e"));
        Assert.Equal([(1, 3), (2, 3), (3, 3)], h.Progress);
    }

    // S09 (Core): ZIP 保持による共有違反 (32) を注入。識別確認一致 → DELETE_FAILED、ZIP を残して後続を削除する。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void S09_ArchiveItselfIsNotDeleted(RunMode mode)
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\in");
        fs.AddDirectory(@"C:\target");
        var zip = MakeZip(("archive.zip", Hello), ("later.txt", Hello));
        var inTarget = fs.AddFile(@"C:\target\archive.zip", zip);
        inTarget.Errors[FakeOp.OpenDeletion] = 32;
        fs.AddFile(ArchivePath, zip).Id = inTarget.Id;
        using var h = new DeleteHarness(zip, fs) { Mode = mode };
        h.File("later.txt");

        var report = h.Run();

        Assert.Equal(
            [("archive.zip", DeleteStatus.DeleteFailed), ("later.txt", DeleteStatus.Deleted)],
            report.Results.Select(r => (r.Entry.Name, r.Status)));
        Assert.StartsWith("削除用に開けません (Win32 エラー 32", report.Results[0].Reason, StringComparison.Ordinal);
        Assert.Contains(@"CheckIdentity \\?\C:\target\archive.zip", h.Fs.Calls);
        Assert.Null(report.Stop);
        Assert.Equal(0, report.NotProcessedCount);
        Assert.True(h.Exists("archive.zip"));
        Assert.Equal(zip, h.Node("archive.zip").Content);
        Assert.False(h.Exists("later.txt"));
        Assert.Equal(["later.txt"], h.Fs.Deleted.Select(n => n.Name));
    }

    // S10: 1〜3 が対象、4 が target 側の判定不能、5 以降が対象 → 1〜3 は削除されたまま、4 は STOPPED で残る、5 以降は未処理で
    // 残りオープンされない。
    [Theory]
    [InlineData("enumeration", RunMode.Strict)]
    [InlineData("info", RunMode.Strict)]
    [InlineData("file-id", RunMode.Strict)]
    [InlineData("crc", RunMode.Strict)]
    [InlineData("enumeration", RunMode.Fast)]
    [InlineData("info", RunMode.Fast)]
    [InlineData("file-id", RunMode.Fast)]
    public void S10_StopKeepsEarlierDeletionsAndLeavesLaterUntouched(string injection, RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(
            ("m1.txt", Hello), ("m2.txt", Hello), ("m3.txt", Hello), ("s/x.txt", Hello), ("m5.txt", Hello), ("m6.txt", Hello)))
        { Mode = mode };
        foreach (var name in new[] { "m1.txt", "m2.txt", "m3.txt", "m5.txt", "m6.txt" })
        {
            h.File(name);
        }

        var dir = h.Fs.AddDirectory(@"C:\target\s");
        var x = h.File(@"s\x.txt");
        switch (injection)
        {
            case "enumeration":
                dir.Errors[FakeOp.Enumerate] = 1117;
                break;
            case "info":
                x.Errors[FakeOp.Streams] = 1117;
                break;
            case "file-id":
                dir.EnumerationOverride = [new DirectoryItem("x.txt", 0x20, 0, h.Fs.NextId())];
                break;
            case "crc":
                h.ContentsOverride = new ReplacingContents(h.Contents, 3, c => new FakeContent(false, c.Length, c.Crc32 ^ 1, c.Open));
                break;
        }

        var report = h.Run();

        Assert.Equal(["m1.txt", "m2.txt", "m3.txt"], Names(report, DeleteStatus.Deleted));
        var stop = StopOf(report);
        Assert.Equal("s/x.txt", stop.Entry.Name);
        Assert.False(stop.PossiblyDeleted);
        Assert.Equal(2, report.NotProcessedCount);
        Assert.True(h.Exists(@"s\x.txt"));
        Assert.True(h.Exists("m5.txt"));
        Assert.True(h.Exists("m6.txt"));
        Assert.False(Opened(h, "m5.txt"));
        Assert.False(Opened(h, "m6.txt"));
        Assert.Equal(4, h.Progress.Count);
        var expectedReason = injection switch
        {
            "enumeration" => "ディレクトリの列挙に失敗しました",
            "info" => "target のファイルの情報を取得できません",
            "file-id" => "開いたファイルが、列挙で見つけた項目と一致しません",
            _ => "全バイト比較で異常: エントリの CRC-32 が一致しません",
        };
        Assert.StartsWith(expectedReason, stop.Reason, StringComparison.Ordinal);
    }

    // S11・S12: 1件目で STOP → 削除0件、未処理 N−1。最後のエントリで STOP → 未処理 0、それ以前の削除は残る。
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void S11_S12_StopAtFirstOrLast(int stopIndex)
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello)));
        var nodes = new[] { h.File("a.txt"), h.File("b.txt"), h.File("c.txt") };
        nodes[stopIndex].Errors[FakeOp.Basic] = 1117;

        var report = h.Run();

        Assert.Equal(stopIndex, Names(report, DeleteStatus.Deleted).Count());
        Assert.Equal(nodes[stopIndex].Name, StopOf(report).Entry.Name);
        Assert.Equal(2 - stopIndex, report.NotProcessedCount);
        Assert.All(nodes.Skip(stopIndex), n => Assert.NotNull(n.Parent));
    }

    // S13: Strict の比較で ZIP 側の異常 → そのエントリは STOP で残り、以後は未処理。DELETE_FAILED で続行しない。それ以前の削除は残る。
    [Theory]
    [InlineData("crc", "エントリの CRC-32 が一致しません")]
    [InlineData("too-long", "エントリの内容が宣言展開量を超えています")]
    [InlineData("too-short", "エントリの内容が宣言展開量より短いです")]
    [InlineData("throw", "エントリの内容を読み取れません")]
    [InlineData("open-throw", "エントリの内容を読み取れません")]
    [InlineData("encrypted", "暗号化されたエントリです")]
    public void S13_ZipAnomalyDuringCompare_Stops(string anomaly, string expected)
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello)));
        h.File("a.txt");
        h.File("b.txt");
        h.File("c.txt");
        h.ContentsOverride = new ReplacingContents(h.Contents, 1, c => anomaly switch
        {
            "crc" => new FakeContent(false, c.Length, c.Crc32 ^ 1, c.Open),
            "too-long" => new FakeContent(false, c.Length, c.Crc32, () => new MemoryStream(Bytes("hello!"))),
            "too-short" => new FakeContent(false, c.Length, c.Crc32, () => new MemoryStream(Bytes("hell"))),
            "throw" => new FakeContent(false, c.Length, c.Crc32, () => new ThrowingStream(Bytes("he"))),
            "open-throw" => new FakeContent(false, c.Length, c.Crc32, () => throw new InvalidDataException("injected")),
            _ => new FakeContent(true, c.Length, c.Crc32, c.Open),
        });

        var report = h.Run();

        Assert.Equal(["a.txt"], Names(report, DeleteStatus.Deleted));
        Assert.Empty(Names(report, DeleteStatus.DeleteFailed));
        var stop = StopOf(report);
        Assert.Equal("b.txt", stop.Entry.Name);
        Assert.StartsWith($"全バイト比較で異常: {expected}", stop.Reason, StringComparison.Ordinal);
        Assert.Equal(EntryStep.Compare, stop.Failure!.Step);
        Assert.Equal(anomaly switch
        {
            "crc" => FatalKind.ContentCrcMismatch,
            "too-long" => FatalKind.ContentTooLong,
            "too-short" => FatalKind.ContentTooShort,
            "throw" or "open-throw" => FatalKind.ContentReadFailed,
            _ => FatalKind.ContentEncrypted,
        }, stop.Failure.FatalKind);
        Assert.Null(stop.Failure.Kind);
        Assert.Null(stop.Failure.Win32Error);
        Assert.True(h.Exists("b.txt"));
        Assert.True(h.Exists("c.txt"));
        Assert.Equal(1, report.NotProcessedCount);
        Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Disposition", StringComparison.Ordinal) && c.EndsWith("b.txt", StringComparison.Ordinal));
    }

    // S14: Strict で内容が1バイト違う同サイズのファイル → MODIFIED で続行 (STOP ではない)。後続は削除される。
    [Fact]
    public void S14_ContentMismatch_IsModifiedAndContinues()
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello)));
        h.File("a.txt", Bytes("hellO"));
        h.File("b.txt");

        var report = h.Run();

        Assert.Equal([DeleteStatus.Modified, DeleteStatus.Deleted], report.Results.Select(r => r.Status));
        Assert.True(h.Exists("a.txt"));
        Assert.False(h.Exists("b.txt"));
        Assert.Equal(2, h.LastRun!.Comparer.Comparisons);
    }

    // S15: 比較中の target の読み取り失敗 → STOP。
    [Fact]
    public void S15_TargetReadFailureDuringCompare_Stops()
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello)));
        var a = h.File("a.txt");
        a.ReadFailAt = 2;
        a.Errors[FakeOp.Read] = 23;
        h.File("b.txt");

        var report = h.Run();

        Assert.StartsWith("全バイト比較で異常: target のファイルを読み取れません", StopOf(report).Reason, StringComparison.Ordinal);
        Assert.Equal(new DeleteFailure(FatalKind.TargetReadFailed, EntryStep.Compare, 23), StopOf(report).Failure);
        Assert.True(h.Exists("a.txt"));
        Assert.True(h.Exists("b.txt"));
    }

    // S16: 削除用オープンが 32 / 5 で、識別確認が列挙由来の基準と一致 → DELETE_FAILED で続行。理由に「内容は確認していません」
    // (Strict でも)。後続の安全な対象は削除される。識別確認は拒否の理由も同じ個体であることも保証しない (削除しないことだけを確かめる)。
    [Theory]
    [InlineData(32, RunMode.Strict)]
    [InlineData(5, RunMode.Strict)]
    [InlineData(32, RunMode.Fast)]
    [InlineData(5, RunMode.Fast)]
    public void S16_OpenDeniedButIdentityLooksSame_IsDeleteFailed(int error, RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("d/a.txt", Hello), ("d/b.txt", Hello), ("c.txt", Hello))) { Mode = mode };
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\a.txt");
        h.File(@"d\b.txt").Errors[FakeOp.OpenDeletion] = error;
        h.File("c.txt");

        var report = h.Run();

        Assert.Equal(["d/a.txt", "c.txt"], Names(report, DeleteStatus.Deleted));
        var failed = Assert.Single(report.Results, r => r.Status == DeleteStatus.DeleteFailed);
        Assert.Equal("d/b.txt", failed.Entry.Name);
        Assert.Contains($"Win32 エラー {error}", failed.Reason, StringComparison.Ordinal);
        Assert.EndsWith("内容は確認していません", failed.Reason, StringComparison.Ordinal);
        Assert.Equal(new DeleteFailure(DeleteFailureKind.DeleteOpenRefused, EntryStep.Open, error), failed.Failure);
        Assert.Null(report.Stop);
        Assert.True(h.Exists(@"d\b.txt"));
        Assert.Contains(@"CheckIdentity \\?\C:\target\d\b.txt", h.Fs.Calls);
        Assert.Equal(0, h.LastRun!.Comparer.Comparisons - (mode == RunMode.Strict ? 2 : 0));
    }

    // S17: 削除用オープンで同一性に疑義 → STOP。2 (消失)、3 (親の消失・ファイル化)、5 で識別確認が別の個体・ディレクトリ・
    // ディレクトリ junction・削除保留中と判明、表に無いコード、識別確認自体の失敗。変更は列挙の後 (H1) に注入する。
    [Theory]
    [InlineData("vanished", false)]
    [InlineData("parent-vanished", false)]
    [InlineData("parent-is-file", false)]
    [InlineData("directory", true)]
    [InlineData("junction", true)]
    [InlineData("denied-other-object", true)]
    [InlineData("delete-pending", true)]
    [InlineData("unknown-1920", false)]
    [InlineData("unknown-4390", false)]
    [InlineData("unknown-1", false)]
    [InlineData("identity-fails", true)]
    public void S17_IdentityInDoubtAtOpen_Stops(string situation, bool identityChecked)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            using var h = new DeleteHarness(MakeZip(("d/a.txt", Hello), ("z.txt", Hello))) { Mode = mode };
            var d = h.Fs.AddDirectory(@"C:\target\d");
            var a = h.File(@"d\a.txt");
            h.File("z.txt");
            h.Hooks = new DeleteHooks
            {
                BeforeOpen = (entry, _) =>
                {
                    if (entry.Name != "d/a.txt")
                    {
                        return;
                    }

                    switch (situation)
                    {
                        case "vanished": h.Fs.Remove(a); break;
                        case "parent-vanished": h.Fs.Remove(d); break;
                        case "parent-is-file": h.Fs.Remove(d); h.File("d"); break;
                        case "directory": h.Fs.Remove(a); h.Fs.AddDirectory(@"C:\target\d\a.txt"); break;
                        case "junction": h.Fs.Remove(a); h.Fs.AddJunction(@"C:\target\d\a.txt", d); break;
                        case "denied-other-object": h.Fs.Remove(a); h.File(@"d\a.txt").Errors[FakeOp.OpenDeletion] = 5; break;
                        case "delete-pending": a.DeletePending = true; break;
                        case "unknown-1920": a.Errors[FakeOp.OpenDeletion] = 1920; break;
                        case "unknown-4390": a.Errors[FakeOp.OpenDeletion] = 4390; break;
                        case "unknown-1": a.Errors[FakeOp.OpenDeletion] = 1; break;
                        case "identity-fails": a.Errors[FakeOp.OpenDeletion] = 32; a.Errors[FakeOp.CheckIdentity] = 1117; break;
                    }
                },
            };

            var report = h.Run();

            Assert.Empty(Names(report, DeleteStatus.Deleted));
            Assert.Empty(Names(report, DeleteStatus.DeleteFailed));
            var stop = StopOf(report);
            Assert.Equal("d/a.txt", stop.Entry.Name);
            Assert.StartsWith("削除用に開けません (Win32 エラー", stop.Reason, StringComparison.Ordinal);
            var expectedKind = situation switch
            {
                "delete-pending" or "identity-fails" => DeleteFailureKind.IdentityCheckFailed,
                "directory" or "junction" or "denied-other-object" => DeleteFailureKind.IdentityCheckMismatch,
                _ => DeleteFailureKind.OpenFailed,
            };
            int? expectedError = situation switch
            {
                "vanished" => 2,
                "parent-vanished" or "parent-is-file" => 3,
                "delete-pending" => 5,
                "identity-fails" => 1117,
                "unknown-1920" => 1920,
                "unknown-4390" => 4390,
                "unknown-1" => 1,
                _ => null,
            };
            Assert.Equal(new DeleteFailure(expectedKind, EntryStep.Open, expectedError), stop.Failure);
            Assert.Equal(1, report.NotProcessedCount);
            Assert.True(h.Exists("z.txt"));
            Assert.Empty(h.Fs.Deleted);
            Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Disposition", StringComparison.Ordinal));

            // 2・3 と表に無いコードでは識別確認をしない。32・5 だけが識別確認に進む (docs/spec/filesystem.md#open-errors)。
            Assert.Equal(identityChecked, h.Fs.Calls.Any(c => c.StartsWith("CheckIdentity", StringComparison.Ordinal)));
        }
    }

    // S18: 識別確認の基準は列挙由来 (列挙項目の File ID、target ルートのボリューム、たどった親の File ID、期待パス)。
    // どれか1つだけを変えると不一致になる。ディレクトリ・reparse・削除保留中も不一致。
    [Fact]
    public void S18_IdentityCheckComparesWithEnumerationBaseline()
    {
        var baseline = new IdentityBaseline(new VolumeFileId(1, new FileId(2, 0)), new FileId(3, 0), @"\\?\C:\t\a.txt");
        var same = new IdentityCheckInfo(new VolumeFileId(1, new FileId(2, 0)), new FileId(3, 0), @"\\?\C:\t\a.txt", false, false, 0x20, 0);

        Assert.Null(DeleteRun.IdentityMismatch(same, baseline));
        Assert.Equal("File ID", DeleteRun.IdentityMismatch(same with { Id = new VolumeFileId(1, new FileId(9, 0)) }, baseline));
        Assert.Equal("File ID", DeleteRun.IdentityMismatch(same with { Id = new VolumeFileId(9, new FileId(2, 0)) }, baseline));
        Assert.Equal("親 File ID", DeleteRun.IdentityMismatch(same with { ParentFileId = new FileId(9, 0) }, baseline));
        Assert.Equal("最終パス", DeleteRun.IdentityMismatch(same with { FinalPath = @"\\?\C:\T\a.txt" }, baseline));
        Assert.Equal("ディレクトリ", DeleteRun.IdentityMismatch(same with { IsDirectory = true }, baseline));
        Assert.Equal("reparse point", DeleteRun.IdentityMismatch(same with { Attributes = 0x420 }, baseline));
        Assert.Equal("reparse point", DeleteRun.IdentityMismatch(same with { ReparseTag = FakeNode.ReparseTagSymlink }, baseline));
        Assert.Equal("削除保留中", DeleteRun.IdentityMismatch(same with { DeletePending = true }, baseline));
    }

    // S18 (実行): 識別確認で見える親が列挙でたどった親と違う (同名の別ディレクトリに同じファイルを移し、そのファイルが使用中) → STOP。
    [Fact]
    public void S18_IdentityParentDiffersFromEnumeration_Stops()
    {
        using var h = new DeleteHarness(MakeZip(("d/a.txt", Hello)));
        var d = h.Fs.AddDirectory(@"C:\target\d");
        var a = h.File(@"d\a.txt");
        h.Hooks = new DeleteHooks
        {
            BeforeOpen = (_, _) =>
            {
                h.Fs.Remove(d);
                h.Fs.Remove(a);
                var replacement = h.Fs.AddDirectory(@"C:\target\d");
                a.Parent = replacement;
                replacement.Children.Add(a);
                a.Errors[FakeOp.OpenDeletion] = 32;
            },
        };

        var report = h.Run();

        Assert.EndsWith("識別確認で列挙時の項目と不一致: 親 File ID", StopOf(report).Reason, StringComparison.Ordinal);
        Assert.Equal(new DeleteFailure(DeleteFailureKind.IdentityCheckMismatch, EntryStep.Open), StopOf(report).Failure);
        Assert.Empty(h.Fs.Deleted);
    }

    // S19: 事前判定 (D1): 最終成分の列挙項目がディレクトリ / ディレクトリ junction / ファイル symlink / read-only / system /
    // 未定義ビット → SKIPPED_SPECIAL_FILE (理由付き)。OpenForDeletion が呼ばれない。同じ fixture の analyze も SKIPPED_SPECIAL_FILE。
    [Theory]
    [InlineData("directory", SkipReason.Directory)]
    [InlineData("junction", SkipReason.Directory)]
    [InlineData("file-symlink", SkipReason.ReparsePoint)]
    [InlineData("readonly", SkipReason.Attributes)]
    [InlineData("system", SkipReason.Attributes)]
    [InlineData("undefined-bit", SkipReason.Attributes)]
    public void S19_PreCheckSkipsWithoutOpening(string kind, SkipReason reason)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            var fs = new FakeFileSystem();
            fs.AddDirectory(@"C:\target");
            var other = fs.AddDirectory(@"C:\other");
            switch (kind)
            {
                case "directory": fs.AddDirectory(@"C:\target\x.txt"); break;
                case "junction": fs.AddJunction(@"C:\target\x.txt", other); break;
                case "file-symlink": fs.AddReparse(@"C:\target\x.txt", isDirectory: false, FakeNode.ReparseTagSymlink); break;
                case "readonly": fs.AddFile(@"C:\target\x.txt", Hello, 0x21); break;
                case "system": fs.AddFile(@"C:\target\x.txt", Hello, 0x24); break;
                case "undefined-bit": fs.AddFile(@"C:\target\x.txt", Hello, 0x20 | 0x0100_0000); break;
            }

            var zip = MakeZip(("x.txt", Hello), ("y.txt", Hello));
            fs.AddFile(@"C:\target\y.txt", Hello);
            using var h = new DeleteHarness(zip, fs) { Mode = mode };

            var report = h.Run();

            var first = report.Results[0];
            Assert.Equal((DeleteStatus.SkippedSpecialFile, reason), (first.Status, first.SkipReason));
            Assert.Equal(DeleteStatus.Deleted, report.Results[1].Status);
            Assert.False(Opened(h, "x.txt"));
            Assert.NotNull(fs.Find(@"C:\target\x.txt"));

            // 同じ状態の analyze も SKIPPED_SPECIAL_FILE (削除されていない x.txt で確かめる)。
            using var analyze = new PipelineHarness(MakeZip(("x.txt", Hello)), fs) { Mode = mode };
            Assert.Equal(Classification.SkippedSpecialFile, Assert.Single(analyze.Run().Results).Classification);
        }
    }

    // S20: 事前判定を通過した後、開いたハンドルで判定: ADS、hardlink (リンク数2)、ZIP 自身、ハンドル上の属性が許可外 (列挙項目は許可内)。
    // ハンドル上の判定を省略しない。
    [Theory]
    [InlineData("ads", SkipReason.AlternateDataStream)]
    [InlineData("hardlink", SkipReason.HardLink)]
    [InlineData("handle-attributes", SkipReason.Attributes)]
    [InlineData("handle-reparse", SkipReason.ReparsePoint)]
    public void S20_HandleChecksAreNotSkipped(string kind, SkipReason reason)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            using var h = new DeleteHarness(MakeZip(("x.txt", Hello))) { Mode = mode };
            var x = h.File("x.txt");
            switch (kind)
            {
                case "ads": x.ExtraStreams.Add(new StreamEntry(":Zone.Identifier:$DATA", 10)); break;
                case "hardlink": x.Links = 2; break;
                case "handle-attributes":
                    h.Fs.Get(TargetPath).EnumerationOverride = [new DirectoryItem("x.txt", 0x20, 0, x.Id)];
                    x.Attributes = 0x21;
                    break;
                case "handle-reparse":
                    h.Fs.Get(TargetPath).EnumerationOverride = [new DirectoryItem("x.txt", 0x20, 0, x.Id)];
                    x.ReparseTag = FakeNode.ReparseTagSymlink;
                    break;
            }

            var report = h.Run();

            var result = Assert.Single(report.Results);
            Assert.Equal((DeleteStatus.SkippedSpecialFile, reason), (result.Status, result.SkipReason));
            Assert.True(Opened(h, "x.txt"));
            Assert.True(h.Exists("x.txt"));
            Assert.Empty(h.Contents.Calls);
        }
    }

    // S20 回帰: hardlink の USN 親 ID は別名の親を指し得る。削除候補の親照合は維持する。
    [Theory]
    [InlineData(1u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(2u, true)]
    public void S20_ParentCheckIsRequiredExceptForConfirmedHardLinks(uint links, bool parentError)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            using var h = new DeleteHarness(MakeZip(("x.txt", Hello), ("later.txt", Hello))) { Mode = mode };
            var x = h.File("x.txt");
            h.File("later.txt");
            x.Links = links;
            x.ParentFileIdOverride = new FileId(999, 0);
            if (parentError)
            {
                x.Errors[FakeOp.ParentFileId] = 1117;
            }

            var report = h.Run();

            Assert.True(h.Exists("x.txt"));
            Assert.DoesNotContain(h.Fs.Calls, c => c == @"Disposition 0x3 \\?\C:\target\x.txt");
            if (links >= 2)
            {
                Assert.Null(report.Stop);
                Assert.Equal((DeleteStatus.SkippedSpecialFile, SkipReason.HardLink),
                    (report.Results[0].Status, report.Results[0].SkipReason));
                Assert.False(h.Exists("later.txt"));
                Assert.DoesNotContain(@"ParentFileId \\?\C:\target\x.txt", h.Fs.Calls);
            }
            else
            {
                Assert.Equal(DeleteStatus.Stopped, StopOf(report).Status);
                Assert.True(h.Exists("later.txt"));
                Assert.Contains(@"ParentFileId \\?\C:\target\x.txt", h.Fs.Calls);
            }
        }
    }

    [Theory]
    [InlineData("file-id")]
    [InlineData("final-path")]
    [InlineData("standard-error")]
    [InlineData("delete-pending")]
    [InlineData("streams-error")]
    public void S20_HardLinksStillStopOnOtherSafetyFailures(string change)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            using var h = new DeleteHarness(MakeZip(("x.txt", Hello), ("later.txt", Hello))) { Mode = mode };
            var x = h.File("x.txt");
            h.File("later.txt");
            x.Links = 2;
            h.Hooks = new DeleteHooks
            {
                AfterOpen = (_, _) =>
                {
                    switch (change)
                    {
                        case "file-id": x.Id = h.Fs.NextId(); break;
                        case "final-path": x.FinalPathOverride = @"\\?\C:\elsewhere\x.txt"; break;
                        case "standard-error": x.Errors[FakeOp.Standard] = 1117; break;
                        case "delete-pending": x.DeletePending = true; break;
                        case "streams-error": x.Errors[FakeOp.Streams] = 1117; break;
                    }
                },
            };

            var report = h.Run();

            Assert.Equal("x.txt", StopOf(report).Entry.Name);
            Assert.True(h.Exists("x.txt"));
            Assert.True(h.Exists("later.txt"));
            Assert.Empty(h.Contents.Calls);
            Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Disposition", StringComparison.Ordinal));
        }
    }

    // S22 (Core): H1 で親ディレクトリを同名の別ディレクトリに差し替え、同じファイルを移して入れる (File ID と最終パスは不変)
    // → オープン直後の親 File ID の照合で STOP。削除しない。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void S22_ParentReplacedAfterEnumeration_Stops(RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("d/a.txt", Hello))) { Mode = mode };
        var d = h.Fs.AddDirectory(@"C:\target\d");
        var a = h.File(@"d\a.txt");
        h.Hooks = new DeleteHooks
        {
            BeforeOpen = (_, _) =>
            {
                h.Fs.Remove(d);
                h.Fs.Remove(a);
                var replacement = h.Fs.AddDirectory(@"C:\target\d");
                a.Parent = replacement;
                replacement.Children.Add(a);
            },
        };

        var report = h.Run();

        Assert.Equal("開いたファイルの親ディレクトリが、列挙でたどった親ディレクトリと一致しません", StopOf(report).Reason);
        Assert.Equal(new DeleteFailure(FatalKind.ParentFileIdMismatch, EntryStep.Verify), StopOf(report).Failure);
        Assert.True(h.Exists(@"d\a.txt"));
        Assert.Empty(h.Contents.Calls);
    }

    // S23 (Core): H1 で途中のディレクトリの差し替え・大小文字だけの改名 (最終パスだけが変わる) → オープン直後の最終パスの不一致で STOP。
    [Theory]
    [InlineData(@"\\?\C:\target\D\a.txt")]
    [InlineData(@"\\?\C:\elsewhere\d\a.txt")]
    public void S23_FinalPathChangedAfterEnumeration_Stops(string finalPath)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            using var h = new DeleteHarness(MakeZip(("d/a.txt", Hello))) { Mode = mode };
            h.Fs.AddDirectory(@"C:\target\d");
            var a = h.File(@"d\a.txt");
            h.Hooks = new DeleteHooks { BeforeOpen = (_, _) => a.FinalPathOverride = finalPath };

            var report = h.Run();

            Assert.StartsWith("開いたファイルの最終パスが期待したパスと一致しません", StopOf(report).Reason, StringComparison.Ordinal);
            Assert.Equal(new DeleteFailure(FatalKind.FinalPathMismatch, EntryStep.Verify), StopOf(report).Failure);
            Assert.True(h.Exists(@"d\a.txt"));
        }
    }

    // S27 (Core)・S25 (Core): H3 (比較中) と H4 (最終確認の直前) に M0 の各項目を1つずつ変える → 最終確認で STOP。そのファイルは残る。
    // Fast は H3 が無い (内容を読まない) ため H4 だけ。M0 の全項目を照合し、一部の項目だけの最終確認にしない (docs/RATIONALE.md#identity-path)。
    [Theory]
    [InlineData("FileId", "File ID")]
    [InlineData("VolumeSerial", "File ID")]
    [InlineData("ParentFileId", "親 File ID")]
    [InlineData("FinalPath", "最終パス")]
    [InlineData("EndOfFile", "EndOfFile")]
    [InlineData("LastWriteTime", "LastWriteTime")]
    [InlineData("ChangeTime", "ChangeTime")]
    [InlineData("Attributes", "属性")]
    [InlineData("ReadOnly", "属性")]
    [InlineData("Links", "リンク数")]
    [InlineData("Streams", "ストリーム一覧")]
    [InlineData("ReparseTag", "reparse 状態")]
    [InlineData("DeletePending", "削除保留中")]
    [InlineData("Directory", "ディレクトリ")]
    public void S25_S27_FinalCheckComparesEveryM0Item(string change, string item)
    {
        foreach (var (mode, hook) in new[] { (RunMode.Strict, "H3"), (RunMode.Strict, "H4"), (RunMode.Fast, "H4") })
        {
            // 比較中の内容 (EndOfFile) の変更は、共有モード (FILE_SHARE_READ のみ) により実機では起こらない。偽 FS では比較の不一致
            // (MODIFIED) になるため、H3 では確かめない。
            if (hook == "H3" && change == "EndOfFile")
            {
                continue;
            }

            using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello))) { Mode = mode };
            var a = h.File("a.txt");
            h.File("b.txt");
            void Change(ZipEntryRef entry, IDeletionHandle _)
            {
                if (entry.Name != "a.txt")
                {
                    return;
                }

                switch (change)
                {
                    case "FileId": a.Id = h.Fs.NextId(); break;
                    case "VolumeSerial": a.VolumeSerial++; break;
                    case "ParentFileId": a.Parent!.Id = h.Fs.NextId(); break;
                    case "FinalPath": a.FinalPathOverride = @"\\?\C:\TARGET\a.txt"; break;
                    case "EndOfFile": a.Content = Bytes("hello!"); break;
                    case "LastWriteTime": a.LastWriteTime++; break;
                    case "ChangeTime": a.ChangeTime++; break;
                    case "Attributes": a.Attributes |= 0x2; break;
                    case "ReadOnly": a.Attributes |= 0x1; break;
                    case "Links": a.Links = 2; break;
                    case "Streams": a.ExtraStreams.Add(new StreamEntry(":Zone.Identifier:$DATA", 10)); break;
                    case "ReparseTag": a.ReparseTag = FakeNode.ReparseTagSymlink; break;
                    case "DeletePending": a.DeletePending = true; break;
                    case "Directory": a.IsDirectory = true; break;
                }
            }

            h.Hooks = hook == "H3" ? new DeleteHooks { DuringCompare = Change } : new DeleteHooks { BeforeFinalCheck = Change };

            var report = h.Run();

            var stop = StopOf(report);
            Assert.Equal("a.txt", stop.Entry.Name);
            Assert.Equal($"最終確認で不一致: {item}", stop.Reason);
            Assert.Equal(new DeleteFailure(DeleteFailureKind.FinalCheckMismatch, EntryStep.FinalCheck), stop.Failure);
            Assert.Equal(1, report.NotProcessedCount);
            Assert.Empty(h.Fs.Deleted);
            Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Disposition", StringComparison.Ordinal));
        }
    }

    // S28 (Core): H5 (削除指示の直前) で read-only 付与 → 削除指示が失敗して STOP、そのファイルは残る、未処理件数、それ以前は戻らない。
    // S31: 削除指示の失敗 → STOP (削除されていない)。成立確認の失敗 → STOP、「削除された可能性あり」。削除済み件数は正確。
    [Theory]
    [InlineData("readonly-before-disposition", RunMode.Strict)]
    [InlineData("disposition-error", RunMode.Strict)]
    [InlineData("confirm-error", RunMode.Strict)]
    [InlineData("readonly-before-disposition", RunMode.Fast)]
    [InlineData("disposition-error", RunMode.Fast)]
    [InlineData("confirm-error", RunMode.Fast)]
    public void S28_S31_DispositionOrConfirmationFailure_Stops(string failure, RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello), ("c.txt", Hello))) { Mode = mode };
        h.File("a.txt");
        var b = h.File("b.txt");
        h.File("c.txt");
        h.Hooks = new DeleteHooks
        {
            BeforeDisposition = (entry, _) =>
            {
                if (entry.Name != "b.txt")
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

        var report = h.Run();

        Assert.Equal(["a.txt"], Names(report, DeleteStatus.Deleted));
        Assert.Equal(1, report.NotProcessedCount);
        Assert.True(h.Exists("c.txt"));
        var stop = StopOf(report);
        Assert.Equal("b.txt", stop.Entry.Name);
        Assert.Equal(failure == "confirm-error"
            ? new DeleteFailure(DeleteFailureKind.DeletionUnconfirmed, EntryStep.Confirm, 1117)
            : new DeleteFailure(DeleteFailureKind.DispositionFailed, EntryStep.Dispose, failure == "readonly-before-disposition" ? 5 : 1117),
            stop.Failure);
        if (failure == "confirm-error")
        {
            // 指示は成立していたが確認できなかった。クローズで名前は消えている。報告は「削除された可能性あり」。
            Assert.True(stop.PossiblyDeleted);
            Assert.StartsWith("削除の成立を確認できません", stop.Reason, StringComparison.Ordinal);
        }
        else
        {
            Assert.False(stop.PossiblyDeleted);
            Assert.StartsWith("削除の指示が失敗", stop.Reason, StringComparison.Ordinal);
            Assert.True(h.Exists("b.txt"));
        }
    }

    // S30: 情報取得 API の失敗を照合 (H2 の後)・検査・最終確認 (H4) の各段階で注入 → STOP。
    [Theory]
    [InlineData(FakeOp.VolumeFileId, "H2")]
    [InlineData(FakeOp.ParentFileId, "H2")]
    [InlineData(FakeOp.FinalPath, "H2")]
    [InlineData(FakeOp.Standard, "H2")]
    [InlineData(FakeOp.Basic, "H2")]
    [InlineData(FakeOp.AttributeTag, "H2")]
    [InlineData(FakeOp.Streams, "H2")]
    [InlineData(FakeOp.VolumeFileId, "H4")]
    [InlineData(FakeOp.ParentFileId, "H4")]
    [InlineData(FakeOp.FinalPath, "H4")]
    [InlineData(FakeOp.Standard, "H4")]
    [InlineData(FakeOp.Basic, "H4")]
    [InlineData(FakeOp.AttributeTag, "H4")]
    [InlineData(FakeOp.Streams, "H4")]
    public void S30_InfoFailureAtEachStage_Stops(FakeOp op, string hook)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello))) { Mode = mode };
            var a = h.File("a.txt");
            h.File("b.txt");
            void Inject(ZipEntryRef entry, IDeletionHandle _)
            {
                if (entry.Name == "a.txt")
                {
                    a.Errors[op] = 1117;
                }
            }

            h.Hooks = hook == "H2" ? new DeleteHooks { AfterOpen = Inject } : new DeleteHooks { BeforeFinalCheck = Inject };

            var report = h.Run();

            var stop = StopOf(report);
            Assert.Equal("a.txt", stop.Entry.Name);
            Assert.Contains("Win32 エラー 1117", stop.Reason, StringComparison.Ordinal);
            var step = hook == "H4" ? EntryStep.FinalCheck
                : op is FakeOp.VolumeFileId or FakeOp.ParentFileId or FakeOp.FinalPath ? EntryStep.Verify
                : EntryStep.Inspect;
            Assert.Equal(new DeleteFailure(FatalKind.TargetInfoFailed, step, 1117), stop.Failure);
            Assert.False(stop.PossiblyDeleted);
            Assert.True(h.Exists("a.txt"));
            Assert.True(h.Exists("b.txt"));
            if (hook == "H2")
            {
                Assert.Empty(h.Contents.Calls);
            }
            else
            {
                Assert.StartsWith("最終確認で不一致: ", stop.Reason, StringComparison.Ordinal);
            }
        }
    }

    // S32: 削除指示は削除用ハンドルに1回だけ、flags はちょうど 0x3 (IGNORE_READONLY_ATTRIBUTE 0x10 を含まない)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void S32_DispositionFlagsAreExactly0x3(RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello))) { Mode = mode };
        h.File("a.txt");
        h.File("b.txt");

        h.Run();

        var dispositions = h.Fs.Calls.Where(c => c.StartsWith("Disposition", StringComparison.Ordinal)).ToList();
        Assert.Equal([@"Disposition 0x3 \\?\C:\target\a.txt", @"Disposition 0x3 \\?\C:\target\b.txt"], dispositions);
        Assert.Equal(0x3u, SequentialDeleter.DispositionFlags);
        Assert.Equal(0u, SequentialDeleter.DispositionFlags & 0x10u);
    }

    // S33: 削除指示の API が成功を返しても DeletePending が false なら DELETED にせず STOP、「削除された可能性あり」。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void S33_ApiSuccessWithoutDeletePending_IsNotDeleted(RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello))) { Mode = mode };
        h.File("a.txt").DispositionHasNoEffect = true;
        h.File("b.txt");

        var report = h.Run();

        Assert.Empty(Names(report, DeleteStatus.Deleted));
        var stop = StopOf(report);
        Assert.True(stop.PossiblyDeleted);
        Assert.Contains("DeletePending が false", stop.Reason, StringComparison.Ordinal);
        Assert.Equal(new DeleteFailure(DeleteFailureKind.DeletionUnconfirmed, EntryStep.Confirm), stop.Failure);
        Assert.True(h.Exists("a.txt"));
        Assert.True(h.Exists("b.txt"));
    }

    // S35: 列挙のキャッシュと逐次削除: 同じディレクトリに多数のエントリ。前半を削除しながら後半を処理する。H1 で ZIP にない新しいファイルと、
    // 後続のエントリと同名の新しいファイル (列挙後に作成) を追加する → そのディレクトリの列挙は1回だけ。自分の削除で後続の判定が変わらない。
    // 列挙後に作られた同名ファイルは MISSING (削除しない)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void S35_EnumerationIsReusedDuringSequentialDeletion(RunMode mode)
    {
        var names = Enumerable.Range(0, 20).Select(i => $"d/f{i:D2}.txt").ToList();
        using var h = new DeleteHarness(MakeZip([.. names.Select(n => (n, (byte[]?)Hello)), ("d/late.txt", Hello)])) { Mode = mode };
        h.Fs.AddDirectory(@"C:\target\d");
        foreach (var name in names)
        {
            h.File(name.Replace('/', '\\'));
        }

        h.Hooks = new DeleteHooks
        {
            BeforeOpen = (entry, _) =>
            {
                if (entry.Name == "d/f05.txt")
                {
                    h.File(@"d\unrelated-new.txt");
                    h.File(@"d\late.txt");
                }
            },
        };

        var report = h.Run();

        Assert.Equal(names, Names(report, DeleteStatus.Deleted));
        Assert.Equal(("d/late.txt", DeleteStatus.Missing), (report.Results[^1].Entry.Name, report.Results[^1].Status));
        Assert.True(h.Exists(@"d\late.txt"));
        Assert.True(h.Exists(@"d\unrelated-new.txt"));
        Assert.Single(h.Fs.Calls, c => c == @"Enumerate \\?\C:\target\d");
        Assert.Single(h.Fs.Calls, c => c == @"Enumerate \\?\C:\target");
        Assert.Equal(["d", "f00.txt", "f01.txt", "f02.txt", "f03.txt", "f04.txt", "f05.txt", "f06.txt", "f07.txt", "f08.txt", "f09.txt",
            "f10.txt", "f11.txt", "f12.txt", "f13.txt", "f14.txt", "f15.txt", "f16.txt", "f17.txt", "f18.txt", "f19.txt"],
            h.LastRun!.Resolver.RetainedNames.Order(StringComparer.Ordinal));
    }

    // S36: 列挙で同じ名前が2回返る → 最初の1件を採用し、オープンの File ID が異なれば STOP。返らない名前は MISSING。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void S36_DuplicateOrMissingNameInEnumeration(bool firstIsReal)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            using var h = new DeleteHarness(MakeZip(("x.txt", Hello), ("gone.txt", Hello))) { Mode = mode };
            var x = h.File("x.txt");
            h.File("gone.txt");
            var real = new DirectoryItem("x.txt", 0x20, 0, x.Id);
            var other = new DirectoryItem("x.txt", 0x20, 0, h.Fs.NextId());
            h.Fs.Get(TargetPath).EnumerationOverride = firstIsReal ? [real, other] : [other, real];

            var report = h.Run();

            if (firstIsReal)
            {
                Assert.Equal([DeleteStatus.Deleted, DeleteStatus.Missing], report.Results.Select(r => r.Status));
                Assert.True(h.Exists("gone.txt"));
            }
            else
            {
                Assert.Equal("開いたファイルが、列挙で見つけた項目と一致しません (解析中の変化)", StopOf(report).Reason);
                Assert.True(h.Exists("x.txt"));
            }
        }
    }

    // T02・T03 の delete での扱い: 親成分が存在しない・大小文字違い・通常ファイルなら MISSING、reparse なら SKIPPED_SPECIAL_FILE。
    // 判定不能 (列挙失敗・想定外の種類) なら STOP。
    [Theory]
    [InlineData("absent", DeleteStatus.Missing)]
    [InlineData("case", DeleteStatus.Missing)]
    [InlineData("file", DeleteStatus.Missing)]
    [InlineData("junction", DeleteStatus.SkippedSpecialFile)]
    [InlineData("device", DeleteStatus.Stopped)]
    public void ParentClassificationInDelete(string layout, DeleteStatus expected)
    {
        using var h = new DeleteHarness(MakeZip(("a/b/c.txt", Hello)));
        var fs = h.Fs;
        var a = fs.AddDirectory(@"C:\target\a");
        switch (layout)
        {
            case "case": fs.AddDirectory(@"C:\target\a\B"); break;
            case "file": h.File(@"a\b"); break;
            case "junction": fs.AddJunction(@"C:\target\a\b", a); break;
            case "device": fs.AddDirectory(@"C:\target\a\b", attributes: 0x10 | 0x40); break;
        }

        var report = h.Run();

        Assert.Equal(expected, Assert.Single(report.Results).Status);
        Assert.Empty(h.Contents.Calls);
        Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("OpenDeletion", StringComparison.Ordinal));
        if (layout == "junction")
        {
            Assert.Equal(SkipReason.ParentReparsePoint, report.Results[0].SkipReason);
        }
    }

    // R08 (delete): 実測合計上限を小さな値に差し替え、累計がちょうど上限 / +1 → 許可 / 超えたエントリで STOP。
    // R09 (delete): 上限 0 でも Fast は DELETED (実測展開量を計上しない)。
    [Theory]
    [InlineData(10, RunMode.Strict, true)]
    [InlineData(9, RunMode.Strict, false)]
    [InlineData(0, RunMode.Fast, true)]
    public void R08_R09_TotalReadLimitInDelete(long limit, RunMode mode, bool passes)
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello))) { Mode = mode };
        h.File("a.txt");
        h.File("b.txt");
        h.Limits = Limits.Default with { MaxTotalReadLength = limit };

        var report = h.Run();

        if (passes)
        {
            Assert.Equal(["a.txt", "b.txt"], Names(report, DeleteStatus.Deleted));
        }
        else
        {
            Assert.Equal(["a.txt"], Names(report, DeleteStatus.Deleted));
            Assert.Equal("全バイト比較で異常: 読み取った展開量の合計が上限を超えています", StopOf(report).Reason);
            Assert.True(h.Exists("b.txt"));
        }

        Assert.Equal(mode == RunMode.Strict ? Math.Min(10, limit + 1) : 0, h.LastRun!.Comparer.TotalRead);
    }

    // L11 (Core): 処理対象 (--entries で選んだもの) だけを処理する。指定外は対象に相当しても削除しない・解決もしない
    // (指定外のエントリだけを含むディレクトリは列挙しない)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void L11_OnlySelectedEntriesAreLocated(RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("keep/a.txt", Hello), ("sel/b.txt", Hello), ("c.txt", Hello))) { Mode = mode };
        h.Fs.AddDirectory(@"C:\target\keep");
        h.Fs.AddDirectory(@"C:\target\sel");
        h.File(@"keep\a.txt");
        h.File(@"sel\b.txt");
        h.File("c.txt");
        h.Selected = ["sel/b.txt"];

        var report = h.Run();

        Assert.Equal(["sel/b.txt"], report.Results.Select(r => r.Entry.Name));
        Assert.Equal(DeleteStatus.Deleted, report.Results[0].Status);
        Assert.True(h.Exists(@"keep\a.txt"));
        Assert.True(h.Exists("c.txt"));
        Assert.DoesNotContain(h.Fs.Calls, c => c.Contains(@"\keep", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Fs.Calls, c => c.Contains("c.txt", StringComparison.Ordinal));
        Assert.Equal(["b.txt", "sel"], h.LastRun!.Resolver.RetainedNames.Order(StringComparer.Ordinal));
        Assert.Equal([(1, 1)], h.Progress);
    }

    // 例外の経路: フックや target の読み取りが例外を投げても STOP として報告し、削除用ハンドルは閉じられる (DeleteHarness が確かめる)。
    [Theory]
    [InlineData("hook", RunMode.Strict)]
    [InlineData("read", RunMode.Strict)]
    [InlineData("hook", RunMode.Fast)]
    public void Exception_StopsAndClosesHandle(string where, RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello))) { Mode = mode };
        var a = h.File("a.txt");
        h.File("b.txt");
        h.Hooks = where == "hook"
            ? new DeleteHooks { AfterOpen = (_, _) => throw new InvalidOperationException("injected") }
            : new DeleteHooks { AfterOpen = (_, _) => a.ThrowOnRead = true };

        var report = h.Run();

        var stop = StopOf(report);
        Assert.StartsWith("想定外の例外 (InvalidOperationException", stop.Reason, StringComparison.Ordinal);
        Assert.Equal(new DeleteFailure(DeleteFailureKind.UnexpectedException, null), stop.Failure);
        Assert.False(stop.PossiblyDeleted);
        Assert.True(h.Exists("a.txt"));
        Assert.True(h.Exists("b.txt"));
    }

    // 削除の指示の後の想定外の例外は STOP で「削除された可能性あり」(docs/spec/filesystem.md#failure-boundary の「任意」の行)。後続は処理しない。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void Exception_AfterDisposition_IsPossiblyDeleted(RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("a.txt", Hello), ("b.txt", Hello))) { Mode = mode };
        h.File("a.txt").ThrowAfterDisposition = true;
        h.File("b.txt");

        var report = h.Run();

        var stop = StopOf(report);
        Assert.Equal("a.txt", stop.Entry.Name);
        Assert.StartsWith("想定外の例外 (InvalidOperationException", stop.Reason, StringComparison.Ordinal);
        Assert.Equal(new DeleteFailure(DeleteFailureKind.UnexpectedException, null), stop.Failure);
        Assert.True(stop.PossiblyDeleted);
        Assert.True(h.Exists("b.txt"));
        Assert.Equal(1, report.NotProcessedCount);
    }

    // A08 (Core): analyze の後、delete の前に MATCHED のファイルの内容だけを書き換える (メタデータを保つ) → delete は analyze の結果を
    // 使わず現在の内容で判定する。Strict は MODIFIED (削除しない)、Fast は DELETED (docs/SPEC.md#modes)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void A08_DeleteDoesNotTrustAnalyze(RunMode mode)
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\target");
        var zip = MakeZip(("a.txt", Hello));
        using (var analyze = new PipelineHarness(zip, fs) { Mode = mode })
        {
            fs.AddFile(@"C:\target\a.txt", Hello);
            Assert.Equal(Candidate(mode), Assert.Single(analyze.Run().Results).Classification);
        }

        fs.Get(@"C:\target\a.txt").Content = Bytes("jello");
        using var h = new DeleteHarness(zip, fs) { Mode = mode };

        var report = h.Run();

        Assert.Equal(mode == RunMode.Fast ? DeleteStatus.Deleted : DeleteStatus.Modified, Assert.Single(report.Results).Status);
    }
}
