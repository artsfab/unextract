using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;
using Unextract.Core.Zip;
using static Unextract.Core.Tests.Fakes.PipelineHarness;

namespace Unextract.Core.Tests;

// delete --prune-dirs のディレクトリ段階 (docs/spec/filesystem.md#prune-dirs、#prune-set、#prune-flow、#prune-failures) を偽ファイルシステムで確認する (D 系の Core)。
// ZIP は実際の ZipArchive で読む。競合は DeleteHooks (H6〜H8) で注入する。DeleteHarness.Run が毎回、ディレクトリ削除用ハンドルの解放と同時に1つまでであること、
// パスを使う呼び出しが各ディレクトリの OpenDirectoryForDeletion 1回 (と失敗時の CheckIdentity 1回) だけであることを確かめる。
public class PruneDirectoryTests
{
    private const uint Hidden = 0x2;
    private const uint ReadOnly = 0x1;

    private static readonly byte[] Hello = Bytes("hello");

    public static TheoryData<RunMode> BothModes => new() { RunMode.Strict, RunMode.Fast };

    private static IEnumerable<(string Path, DirectoryStatus Status, DirectoryKeepReason? Keep)> Outcomes(DeleteReport report) =>
        report.Prune!.Results.Select(r => (r.Directory.Path, r.Status, r.KeepReason));

    private static DirectoryResult StopOf(DeleteReport report) => report.Prune?.Stop ?? throw new InvalidOperationException("STOP なし");

    private static bool DirectoryOpened(DeleteHarness h, string path) =>
        h.Fs.Calls.Contains($@"OpenDirectoryDeletion \\?\C:\target\{path}");

    private static ValidatedZipEntry Entry(int index, string name)
    {
        var directory = name.EndsWith('/');
        var components = name.TrimEnd('/').Split('/');
        return new ValidatedZipEntry(new ZipEntryInfo(index, name, 0, 0, false, 0), directory, components);
    }

    // D01: 対象と順序 (#prune-set)。--entries なしでは明示ディレクトリと全エントリの祖先、深い順で同じ深さはアーカイブの順、同じパスは1回、
    // target ルートは含めない。--entries ありでは選んだファイルの祖先だけで、明示ディレクトリだけのパスは含めない。
    [Fact]
    public void D01_TargetSetAndOrder()
    {
        var all = new[]
        {
            Entry(0, "top.txt"), Entry(1, "b/x/y.txt"), Entry(2, "a/"), Entry(3, "a/p/q/r.txt"), Entry(4, "c/d/"), Entry(5, "b/z.txt"),
        };

        Assert.Equal(
            [@"a\p\q", @"b\x", @"a\p", @"c\d", "b", "a", "c"],
            PruneSet.Build(all, entriesSelected: false).Select(d => d.Path));

        var selected = new[] { Entry(1, "b/x/y.txt"), Entry(5, "b/z.txt") };
        Assert.Equal([@"b\x", "b"], PruneSet.Build(selected, entriesSelected: true).Select(d => d.Path));
        Assert.Empty(PruneSet.Build([Entry(0, "top.txt")], entriesSelected: true));
    }

    // D02: ファイル段階の後に、空になったディレクトリを深い順に削除し、子を削除して空になった親も同じ段階で削除する。実行前から空だったものも削除する。
    // 中身の残るディレクトリ (ZIP にないファイル、MODIFIED) は空でないとして残し、存在しない・通常ファイル・大小文字違いは DIR_MISSING。
    // HIDDEN は許可集合なので削除する。両モードで同じ (ディレクトリ段階はアーカイブを読まない)。
    [Theory]
    [MemberData(nameof(BothModes))]
    public void D02_DeletesEmptiedDirectoriesDeepestFirst(RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(
            ("a/b/f.txt", Hello), ("a/g.txt", Hello), ("keep/k.txt", Hello), ("mod/m.txt", Hello),
            ("empty/", null), ("hidden/h.txt", Hello), ("absent/", null), ("file/", null), ("Case/", null)))
        { Mode = mode, PruneDirs = true };
        h.Fs.AddDirectory(@"C:\target\a");
        h.Fs.AddDirectory(@"C:\target\a\b");
        h.File(@"a\b\f.txt");
        h.File(@"a\g.txt");
        h.Fs.AddDirectory(@"C:\target\keep");
        h.File(@"keep\k.txt");
        h.File(@"keep\unrelated.txt");
        h.Fs.AddDirectory(@"C:\target\mod");
        h.File(@"mod\m.txt", Bytes("other!"));
        h.Fs.AddDirectory(@"C:\target\empty");
        h.Fs.AddDirectory(@"C:\target\hidden", 0x10 | Hidden);
        h.File(@"hidden\h.txt");
        h.File("file");
        h.Fs.AddDirectory(@"C:\target\case");

        var report = h.Run();

        Assert.Equal(
            [
                (@"a\b", DirectoryStatus.Deleted, null),
                ("a", DirectoryStatus.Deleted, null),
                ("keep", DirectoryStatus.Kept, DirectoryKeepReason.NotEmpty),
                ("mod", DirectoryStatus.Kept, (DirectoryKeepReason?)DirectoryKeepReason.NotEmpty),
                ("empty", DirectoryStatus.Deleted, null),
                ("hidden", DirectoryStatus.Deleted, null),
                ("absent", DirectoryStatus.Missing, null),
                ("file", DirectoryStatus.Missing, null),
                ("Case", DirectoryStatus.Missing, null),
            ],
            Outcomes(report));
        Assert.Equal(@"\\?\C:\target\a\b", report.Prune!.Results[0].Target);
        Assert.Equal(0, report.Prune.NotProcessedCount);
        Assert.False(report.Stopped);
        Assert.False(h.Exists("a"));
        Assert.False(h.Exists("empty"));
        Assert.False(h.Exists("hidden"));
        Assert.True(h.Exists(@"keep\unrelated.txt"));
        Assert.True(h.Exists(@"mod\m.txt"));
        Assert.True(h.Exists("file"));
        Assert.True(h.Exists("case"));

        // 子は親より先に処理を終え (ハンドルを閉じ)、空でないディレクトリには削除の指示をしない。
        var calls = h.Fs.Calls;
        Assert.True(calls.IndexOf(@"Close dir-deletion \\?\C:\target\a\b") < calls.IndexOf(@"OpenDirectoryDeletion \\?\C:\target\a"));
        Assert.DoesNotContain(calls, c => c == @"Disposition 0x3 \\?\C:\target\keep");
    }

    // D03: ファイル段階が STOP したらディレクトリ段階を開始せず、全件が未処理。DELETE_FAILED があっても開始する。
    [Fact]
    public void D03_StageStartsOnlyWithoutFileStop()
    {
        using (var h = new DeleteHarness(MakeZip(("d/a.txt", Hello), ("d/b.txt", Hello))) { PruneDirs = true })
        {
            h.Fs.AddDirectory(@"C:\target\d");
            h.File(@"d\a.txt").Errors[FakeOp.FinalPath] = 31;
            h.File(@"d\b.txt");

            var report = h.Run();

            Assert.NotNull(report.Stop);
            Assert.Empty(report.Prune!.Results);
            Assert.Equal(1, report.Prune.NotProcessedCount);
            Assert.True(report.Stopped);
            Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("OpenDirectoryDeletion ", StringComparison.Ordinal));
        }

        using (var h = new DeleteHarness(MakeZip(("d/a.txt", Hello), ("e/b.txt", Hello))) { PruneDirs = true })
        {
            h.Fs.AddDirectory(@"C:\target\d");
            h.File(@"d\a.txt").Errors[FakeOp.OpenDeletion] = 32;
            h.Fs.AddDirectory(@"C:\target\e");
            h.File(@"e\b.txt");

            var report = h.Run();

            Assert.Equal(DeleteStatus.DeleteFailed, report.Results[0].Status);
            Assert.Equal([("d", DirectoryStatus.Kept, (DirectoryKeepReason?)DirectoryKeepReason.NotEmpty), ("e", DirectoryStatus.Deleted, null)], Outcomes(report));
            Assert.False(report.Stopped);
        }
    }

    // D04: 残す理由。親が reparse・reparse (たどらない)・許可集合外の属性 (READONLY は開かずに残す)・ADS (ハンドル上の検査)。
    [Fact]
    public void D04_KeepReasons()
    {
        using var h = new DeleteHarness(MakeZip(
            ("j/sub/", null), ("ro/", null), ("ads/", null), ("sys/", null)))
        { PruneDirs = true };
        var elsewhere = h.Fs.AddDirectory(@"C:\elsewhere");
        h.Fs.AddDirectory(@"C:\elsewhere\sub");
        h.Fs.AddJunction(@"C:\target\j", elsewhere);
        h.Fs.AddDirectory(@"C:\target\ro", 0x10 | ReadOnly);
        h.Fs.AddDirectory(@"C:\target\ads").ExtraStreams.Add(new StreamEntry(":x:$DATA", 1));
        var sys = h.Fs.AddDirectory(@"C:\target\sys");

        // 列挙の後、オープンの前に SYSTEM が付いた: ハンドル上の検査で属性として残す。
        h.Hooks = new DeleteHooks { BeforeDirectoryOpen = (path, _) => { if (path == "sys") sys.Attributes |= 0x4; } };

        var report = h.Run();

        Assert.Equal(
            [
                (@"j\sub", DirectoryStatus.Kept, (DirectoryKeepReason?)DirectoryKeepReason.ParentReparsePoint),
                ("j", DirectoryStatus.Kept, DirectoryKeepReason.ReparsePoint),
                ("ro", DirectoryStatus.Kept, DirectoryKeepReason.Attributes),
                ("ads", DirectoryStatus.Kept, DirectoryKeepReason.AlternateDataStream),
                ("sys", DirectoryStatus.Kept, DirectoryKeepReason.Attributes),
            ],
            Outcomes(report));
        Assert.False(DirectoryOpened(h, "j"));
        Assert.False(DirectoryOpened(h, "ro"));
        Assert.True(DirectoryOpened(h, "ads"));
        Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Disposition ", StringComparison.Ordinal));
        Assert.True(h.Fs.Find(@"C:\elsewhere\sub") is not null);
    }

    // D05: オープンの失敗。32/5 で識別確認が一致すれば DIR_KEPT (使用中) で続行、識別確認の失敗・不一致と、その他のエラーは STOP。
    [Theory]
    [InlineData(32, null, DirectoryStatus.Kept, null)]
    [InlineData(5, null, DirectoryStatus.Kept, null)]
    [InlineData(32, 5, DirectoryStatus.Stopped, DeleteFailureKind.IdentityCheckFailed)]
    [InlineData(2, null, DirectoryStatus.Stopped, DeleteFailureKind.OpenFailed)]
    [InlineData(1920, null, DirectoryStatus.Stopped, DeleteFailureKind.OpenFailed)]
    public void D05_OpenFailures(int openError, int? identityError, DirectoryStatus expected, DeleteFailureKind? kind)
    {
        using var h = new DeleteHarness(MakeZip(("a/", null), ("b/", null))) { PruneDirs = true };
        var a = h.Fs.AddDirectory(@"C:\target\a");
        h.Fs.AddDirectory(@"C:\target\b");
        a.Errors[FakeOp.OpenDirectoryDeletion] = openError;
        if (identityError is { } error)
        {
            a.Errors[FakeOp.CheckIdentity] = error;
        }

        var report = h.Run();

        var first = report.Prune!.Results[0];
        Assert.Equal(expected, first.Status);
        Assert.True(h.Exists("a"));
        if (expected == DirectoryStatus.Kept)
        {
            Assert.Equal(DirectoryKeepReason.InUse, first.KeepReason);
            Assert.Equal(DirectoryStatus.Deleted, report.Prune.Results[1].Status);
            Assert.False(report.Stopped);
        }
        else
        {
            Assert.Equal(kind, first.Failure!.Kind);
            Assert.Equal(EntryStep.Open, first.Failure.Step);
            Assert.False(first.PossiblyDeleted);
            Assert.Single(report.Prune.Results);
            Assert.Equal(1, report.Prune.NotProcessedCount);
            Assert.True(h.Exists("b"));
        }
    }

    // D06: 識別確認が別の個体・ファイル・削除保留を見たら STOP (一致しない限り残して続行にしない)。
    [Fact]
    public void D06_IdentityCheckMismatchStops()
    {
        using var h = new DeleteHarness(MakeZip(("a/", null))) { PruneDirs = true };
        var a = h.Fs.AddDirectory(@"C:\target\a");
        a.Errors[FakeOp.OpenDirectoryDeletion] = 32;
        h.Hooks = new DeleteHooks
        {
            // 列挙の後に同名の別個体へ差し替える。
            BeforeDirectoryOpen = (_, _) =>
            {
                h.Fs.Remove(a);
                h.Fs.AddDirectory(@"C:\target\a").Errors[FakeOp.OpenDirectoryDeletion] = 32;
            },
        };

        var report = h.Run();

        var stop = StopOf(report);
        Assert.Equal(DeleteFailureKind.IdentityCheckMismatch, stop.Failure!.Kind);
        Assert.Contains("File ID", stop.Reason, StringComparison.Ordinal);
    }

    // D07: 空の確認の後に項目が作られた場合、削除の指示は 145 で拒否され、DeletePending が false なので空でないとして残し、続行する。
    // 親もその子を含むので空でないとして残る。
    [Fact]
    public void D07_ChildCreatedAfterEmptyCheckIsRejected()
    {
        using var h = new DeleteHarness(MakeZip(("p/c/f.txt", Hello))) { PruneDirs = true };
        h.Fs.AddDirectory(@"C:\target\p");
        h.Fs.AddDirectory(@"C:\target\p\c");
        h.File(@"p\c\f.txt");
        h.Hooks = new DeleteHooks
        {
            BeforeDirectoryDisposition = (path, _) => { if (path == @"p\c") h.Fs.AddFile(@"C:\target\p\c\late.txt", Hello); },
        };

        var report = h.Run();

        Assert.Equal(
            [(@"p\c", DirectoryStatus.Kept, (DirectoryKeepReason?)DirectoryKeepReason.NotEmpty), ("p", DirectoryStatus.Kept, DirectoryKeepReason.NotEmpty)],
            Outcomes(report));
        Assert.True(h.Exists(@"p\c\late.txt"));
        Assert.False(h.Node(@"p\c").DeletePending);
        Assert.False(report.Stopped);
    }

    // D08: 最終確認で D0 と違えば (属性・ADS・最終パス) 削除の指示をせず STOP し、以後を処理しない。タイムスタンプは D0 に含めない。
    [Theory]
    [InlineData("attributes", "属性")]
    [InlineData("ads", "ストリームの有無")]
    [InlineData("path", "最終パス")]
    public void D08_FinalCheckMismatchStopsBeforeDisposition(string change, string detail)
    {
        using var h = new DeleteHarness(MakeZip(("a/", null), ("b/", null))) { PruneDirs = true };
        var a = h.Fs.AddDirectory(@"C:\target\a");
        h.Fs.AddDirectory(@"C:\target\b");
        h.Hooks = new DeleteHooks
        {
            BeforeDirectoryFinalCheck = (path, _) =>
            {
                if (path != "a")
                {
                    return;
                }

                a.LastWriteTime++;
                switch (change)
                {
                    case "attributes": a.Attributes |= Hidden; break;
                    case "ads": a.ExtraStreams.Add(new StreamEntry(":x:$DATA", 1)); break;
                    default: a.FinalPathOverride = @"\\?\C:\other\a"; break;
                }
            },
        };

        var report = h.Run();

        var stop = StopOf(report);
        Assert.Equal("a", stop.Directory.Path);
        Assert.Equal(DeleteFailureKind.FinalCheckMismatch, stop.Failure!.Kind);
        Assert.Equal(EntryStep.FinalCheck, stop.Failure.Step);
        Assert.Contains(detail, stop.Reason, StringComparison.Ordinal);
        Assert.False(stop.PossiblyDeleted);
        Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Disposition ", StringComparison.Ordinal));
        Assert.Equal(1, report.Prune!.NotProcessedCount);
        Assert.True(h.Exists("a"));
        Assert.True(h.Exists("b"));
    }

    // D09: タイムスタンプだけの変化 (子の作成と削除) では最終確認は通る。
    [Fact]
    public void D09_TimestampChangeAloneDoesNotStop()
    {
        using var h = new DeleteHarness(MakeZip(("a/", null))) { PruneDirs = true };
        var a = h.Fs.AddDirectory(@"C:\target\a");
        h.Hooks = new DeleteHooks { BeforeDirectoryFinalCheck = (_, _) => { a.LastWriteTime++; a.ChangeTime++; } };

        var report = h.Run();

        Assert.Equal([("a", DirectoryStatus.Deleted, (DirectoryKeepReason?)null)], Outcomes(report));
    }

    // D10: 削除の指示と成立確認の失敗。指示の失敗 (145 以外) は STOP で DeletePending が false なら非削除、API が成功を返しても DeletePending が
    // false なら「削除された可能性あり」、指示の後の例外も「削除された可能性あり」。
    [Theory]
    [InlineData("error", DeleteFailureKind.DispositionFailed, false)]
    [InlineData("no-effect", DeleteFailureKind.DeletionUnconfirmed, true)]
    [InlineData("throw", DeleteFailureKind.UnexpectedException, true)]
    public void D10_DispositionFailures(string failure, DeleteFailureKind kind, bool possiblyDeleted)
    {
        using var h = new DeleteHarness(MakeZip(("a/", null), ("b/", null))) { PruneDirs = true };
        var a = h.Fs.AddDirectory(@"C:\target\a");
        h.Fs.AddDirectory(@"C:\target\b");
        switch (failure)
        {
            case "error": a.Errors[FakeOp.Disposition] = 87; break;
            case "no-effect": a.DispositionHasNoEffect = true; break;
            default: a.ThrowAfterDisposition = true; break;
        }

        var report = h.Run();

        var stop = StopOf(report);
        Assert.Equal(kind, stop.Failure!.Kind);
        Assert.Equal(possiblyDeleted, stop.PossiblyDeleted);
        Assert.Equal(1, report.Prune!.NotProcessedCount);
        Assert.True(h.Exists("b"));
    }

    // D11: 他者が削除保留にしたディレクトリはオープンも識別確認も 5 (docs/RATIONALE.md#prune-dirs の観測) で、STOP。
    [Fact]
    public void D11_DeletePendingStops()
    {
        using var h = new DeleteHarness(MakeZip(("a/", null))) { PruneDirs = true };
        h.Fs.AddDirectory(@"C:\target\a").DeletePending = true;

        var stop = StopOf(h.Run());

        Assert.Equal(DeleteFailureKind.IdentityCheckFailed, stop.Failure!.Kind);
        Assert.Equal(5, stop.Failure.Win32Error);
    }

    // D12: 解決・検査・空の確認・削除の失敗の段階 (STOP の step)。列挙の失敗は空の確認でも inspect。
    [Theory]
    [InlineData(FakeOp.Enumerate, EntryStep.Inspect, FatalKind.EnumerationFailed)]
    [InlineData(FakeOp.Streams, EntryStep.Inspect, FatalKind.TargetInfoFailed)]
    [InlineData(FakeOp.AttributeTag, EntryStep.Inspect, FatalKind.TargetInfoFailed)]
    [InlineData(FakeOp.ParentFileId, EntryStep.Verify, FatalKind.TargetInfoFailed)]
    [InlineData(FakeOp.VolumeFileId, EntryStep.Verify, FatalKind.TargetInfoFailed)]
    public void D12_InformationFailuresStop(FakeOp op, EntryStep step, FatalKind kind)
    {
        using var h = new DeleteHarness(MakeZip(("a/", null))) { PruneDirs = true };
        h.Fs.AddDirectory(@"C:\target\a").Errors[op] = 1117;

        var stop = StopOf(h.Run());

        Assert.Equal(kind, stop.Failure!.FatalKind);
        Assert.Equal(step, stop.Failure.Step);
        Assert.Equal(1117, stop.Failure.Win32Error);
        Assert.True(h.Exists("a"));
    }

    // D37: 最終成分が DEVICE 属性を持つ項目なら種類を判定できないので、開かずに resolve の STOP (親成分と同じ表)。
    [Fact]
    public void D37_DeviceFinalComponentStopsWithoutOpening()
    {
        using var h = new DeleteHarness(MakeZip(("a/", null), ("b/", null))) { PruneDirs = true };
        h.Fs.AddDirectory(@"C:\target\a", 0x10 | 0x40);
        h.Fs.AddDirectory(@"C:\target\b");

        var report = h.Run();

        var stop = StopOf(report);
        Assert.Equal("a", stop.Directory.Path);
        Assert.Equal(FatalKind.UnexpectedTargetType, stop.Failure!.FatalKind);
        Assert.Equal(EntryStep.Resolve, stop.Failure.Step);
        Assert.False(stop.PossiblyDeleted);
        Assert.False(DirectoryOpened(h, "a"));
        Assert.Equal(1, report.Prune!.NotProcessedCount);
        Assert.True(h.Exists("b"));
    }

    // D38: 指示が 145 で失敗した後、同じハンドルの DeletePending が true、または取得に失敗した場合は、空でないとして続行せず
    // STOP し、DeletePending が false と確認できないので「削除された可能性あり」とする。
    [Theory]
    [InlineData("pending")]
    [InlineData("info-failed")]
    public void D38_DirNotEmptyWithoutConfirmedNotPendingStops(string after)
    {
        using var h = new DeleteHarness(MakeZip(("a/", null), ("b/", null))) { PruneDirs = true };
        var a = h.Fs.AddDirectory(@"C:\target\a");
        h.Fs.AddDirectory(@"C:\target\b");
        a.Errors[FakeOp.Disposition] = 145;
        h.Hooks = new DeleteHooks
        {
            BeforeDirectoryDisposition = (path, _) =>
            {
                if (path != "a")
                {
                    return;
                }

                if (after == "pending")
                {
                    a.DeletePending = true;
                }
                else
                {
                    a.Errors[FakeOp.Standard] = 1117;
                }
            },
        };

        var report = h.Run();

        var stop = StopOf(report);
        Assert.Equal("a", stop.Directory.Path);
        Assert.Equal(DeleteFailureKind.DispositionFailed, stop.Failure!.Kind);
        Assert.Equal(EntryStep.Dispose, stop.Failure.Step);
        Assert.Equal(145, stop.Failure.Win32Error);
        Assert.True(stop.PossiblyDeleted);
        Assert.Equal(1, report.Prune!.NotProcessedCount);
        Assert.True(h.Exists("b"));
    }

    // D39: 最終確認の File ID・親 File ID の不一致は FinalCheckMismatch、情報取得の失敗は TargetInfoFailed で、どちらも指示をせずに STOP。
    [Theory]
    [InlineData("file-id", "File ID")]
    [InlineData("parent", "親 File ID")]
    [InlineData("info", null)]
    public void D39_FinalCheckIdentityAndInformationFailuresStop(string change, string? detail)
    {
        using var h = new DeleteHarness(MakeZip(("a/", null), ("b/", null))) { PruneDirs = true };
        var a = h.Fs.AddDirectory(@"C:\target\a");
        h.Fs.AddDirectory(@"C:\target\b");
        h.Hooks = new DeleteHooks
        {
            BeforeDirectoryFinalCheck = (path, _) =>
            {
                if (path != "a")
                {
                    return;
                }

                switch (change)
                {
                    case "file-id": a.Id = new FileId(0xF00D, 0); break;
                    case "parent": a.ParentFileIdOverride = new FileId(0xF00D, 0); break;
                    default: a.Errors[FakeOp.VolumeFileId] = 1117; break;
                }
            },
        };

        var report = h.Run();

        var stop = StopOf(report);
        Assert.Equal("a", stop.Directory.Path);
        Assert.Equal(EntryStep.FinalCheck, stop.Failure!.Step);
        if (detail is null)
        {
            Assert.Equal(FatalKind.TargetInfoFailed, stop.Failure.FatalKind);
            Assert.Equal(1117, stop.Failure.Win32Error);
        }
        else
        {
            Assert.Equal(DeleteFailureKind.FinalCheckMismatch, stop.Failure.Kind);
            Assert.Contains(detail, stop.Reason, StringComparison.Ordinal);
        }

        Assert.False(stop.PossiblyDeleted);
        Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("Disposition ", StringComparison.Ordinal));
        Assert.True(h.Exists("a"));
        Assert.True(h.Exists("b"));
    }

    // D13: 呼び出し順。解決 (段階の中で親を1回だけ列挙) → オープン1回 → 同じハンドルで照合 (File ID・最終パス・親 File ID・Standard) →
    // 検査 (属性・ストリーム) → 空の確認 (同じハンドルの列挙) → 最終確認 → 指示 (0x3) → 成立確認 → クローズ。ファイル段階の列挙結果は使わない。
    [Fact]
    public void D13_CallOrder_UsesOnlyTheDirectoryDeletionHandle()
    {
        using var h = new DeleteHarness(MakeZip(("d/f.txt", Hello))) { PruneDirs = true };
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\f.txt");

        var report = h.Run();

        Assert.Equal([("d", DirectoryStatus.Deleted, (DirectoryKeepReason?)null)], Outcomes(report));
        var calls = h.Fs.Calls;
        var start = calls.LastIndexOf(@"Close deletion \\?\C:\target\d\f.txt") + 1;
        const string D = @"\\?\C:\target\d";
        Assert.Equal(
            [
                @"Enumerate \\?\C:\target",
                $"OpenDirectoryDeletion {D}",
                $"VolumeFileId {D}", $"FinalPath {D}", $"ParentFileId {D}", $"Standard {D}",
                $"AttributeTag {D}", $"Streams {D}",
                $"Enumerate {D}",
                $"VolumeFileId {D}", $"ParentFileId {D}", $"FinalPath {D}", $"Standard {D}", $"AttributeTag {D}", $"Streams {D}",
                $"Disposition 0x3 {D}",
                $"Standard {D}",
                $"Close dir-deletion {D}",
                @"Close root \\?\C:\target",
            ],
            calls.Skip(start));
    }

    // D14: --entries ありでは選んだファイルの祖先だけが対象で、明示ディレクトリエントリだけのパスは対象外 (開かない)。
    [Fact]
    public void D14_EntriesLimitTheTargetsToAncestorsOfSelectedFiles()
    {
        using var h = new DeleteHarness(MakeZip(("x/", null), ("s/a.txt", Hello), ("t/b.txt", Hello)))
        {
            PruneDirs = true,
            Selected = ["s/a.txt"],
        };
        h.Fs.AddDirectory(@"C:\target\x");
        h.Fs.AddDirectory(@"C:\target\s");
        h.File(@"s\a.txt");
        h.Fs.AddDirectory(@"C:\target\t");
        h.File(@"t\b.txt");

        var report = h.Run();

        Assert.Equal([("s", DirectoryStatus.Deleted, (DirectoryKeepReason?)null)], Outcomes(report));
        Assert.True(h.Exists("x"));
        Assert.True(h.Exists(@"t\b.txt"));
        Assert.False(DirectoryOpened(h, "x"));
    }

    // D16: 人間向け表示 (docs/spec/cli.md#delete-output)。ヘッダーに対象のフォルダーの件数、ファイルの結果行の後にフォルダーの結果行
    // (Entry の列なし、DIR_KEPT には理由)、要約の次の行にフォルダーの件数。DIR_KEPT・DIR_MISSING は終了状態を変えない。
    [Fact]
    public void D16_HumanOutput()
    {
        var h = new CommandHarness(MakeZip(("d/f.txt", Hello), ("k/g.txt", Hello), ("m/", null)));
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\f.txt");
        h.Fs.AddDirectory(@"C:\target\k");
        h.File(@"k\g.txt");
        h.File(@"k\other.txt");

        var run = h.Delete(pruneDirs: true);

        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
        var lines = run.OutputLines;
        Assert.Equal("フォルダー: 空のフォルダーも削除します (--prune-dirs、対象 3 件)", lines[lines.ToList().IndexOf("対象: 全 3 エントリ") + 1]);
        Assert.Equal(
            [
                @"DELETED               d/f.txt -> C:\target\d\f.txt",
                @"DELETED               k/g.txt -> C:\target\k\g.txt",
                @"DIR_DELETED           C:\target\d",
                @"DIR_KEPT              C:\target\k (空でない)",
                @"DIR_MISSING           C:\target\m",
                "要約: 削除済み 2、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 1、DELETE_FAILED 0、処理対象外 0、未処理 0",
                "フォルダー: 削除済み 1、DIR_KEPT 1、DIR_MISSING 1、未処理 0",
            ],
            lines.SkipWhile(l => !l.StartsWith("Status", StringComparison.Ordinal)).Skip(1));
        Assert.Empty(run.Error);
    }

    // D17: 確認プロンプトは、ファイルの後に空のフォルダー (最大 D 件) も削除することを示す。中止すれば削除0件 (ディレクトリにも触れない)。
    [Fact]
    public void D17_ConfirmationMentionsDirectories()
    {
        var h = new CommandHarness(MakeZip(("a/b/f.txt", Hello)));
        h.Fs.AddDirectory(@"C:\target\a");
        h.Fs.AddDirectory(@"C:\target\a\b");
        h.File(@"a\b\f.txt");
        var prompt = new ScriptedPrompt(true, "n");

        var run = h.Delete(yes: false, prompt: prompt, pruneDirs: true);

        Assert.Equal(ExitStatus.UserCancelled, run.Outcome.Status);
        Assert.Equal(
            "最大 1 件のファイルエントリを1件ずつ検証し、条件を満たしたものをその場で完全に削除します。" + Environment.NewLine
            + "ファイルの処理を最後まで終えた後、アーカイブ由来のパスにある空のフォルダー (最大 2 件) も1件ずつ検証して削除します。" + Environment.NewLine
            + "途中で停止した場合、それまでに削除したファイルは元に戻りません。" + Environment.NewLine
            + "続行しますか? [y/N] ",
            Assert.Single(prompt.Asked));
        Assert.True(h.Exists(@"a\b\f.txt"));
        Assert.DoesNotContain(h.Fs.Calls, PipelineHarness.IsDeletionCall);
    }

    // D18: ディレクトリ段階の STOP。標準出力にフォルダーの処理中に停止したこと、標準エラー出力に原因と件数を出し、エラーで終了する。
    [Fact]
    public void D18_DirectoryStopOutput()
    {
        var h = new CommandHarness(MakeZip(("a/f.txt", Hello), ("b/", null)));
        h.Fs.AddDirectory(@"C:\target\a");
        h.File(@"a\f.txt");
        h.Fs.Get(@"C:\target\a").Errors[FakeOp.Disposition] = 87;
        h.Fs.AddDirectory(@"C:\target\b");

        var run = h.Delete(pruneDirs: true);

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.Contains(@"STOPPED               C:\target\a", run.OutputLines);
        Assert.Contains("フォルダー: 削除済み 0、DIR_KEPT 0、DIR_MISSING 0、未処理 1", run.OutputLines);
        Assert.Contains(@"フォルダーの処理中に停止しました。それまでに削除したファイル 1 件とフォルダー 0 件は元に戻りません。C:\target\a は削除していません。未処理のフォルダー 1 件には触れていません。",
            run.OutputLines);
        Assert.Equal(
            [
                @"停止: フォルダー ""C:\target\a"": 削除の指示が失敗: Disposition が失敗 (Win32 エラー 87)",
                "以後の処理を停止しました (削除済み 1、DELETE_FAILED 0、フォルダー削除済み 0、フォルダー未処理 1)。",
            ],
            run.ErrorLines);
        Assert.True(h.Exists("b"));
    }

    // D19: ファイル段階の STOP ではディレクトリ段階を開始せず、フォルダーの要約は全件が未処理。
    [Fact]
    public void D19_FileStopLeavesAllDirectoriesUnprocessed()
    {
        var h = new CommandHarness(MakeZip(("a/f.txt", Hello)));
        h.Fs.AddDirectory(@"C:\target\a");
        h.File(@"a\f.txt").Errors[FakeOp.FinalPath] = 31;

        var run = h.Delete(pruneDirs: true);

        Assert.Equal(ExitStatus.Error, run.Outcome.Status);
        Assert.Contains("フォルダー: 削除済み 0、DIR_KEPT 0、DIR_MISSING 0、未処理 1", run.OutputLines);
        Assert.DoesNotContain(run.OutputLines, l => l.StartsWith("DIR_", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("OpenDirectoryDeletion ", StringComparison.Ordinal));
    }

    // D20: 同期通知。OnPrepared は --prune-dirs の有無と対象の件数を渡し、OnDirectoryResult はハンドルを閉じた後に処理の順で呼ばれる。
    [Fact]
    public void D20_Notifications()
    {
        var prepared = new List<Commands.PreparedCommandInfo>();
        var directories = new List<DirectoryResult>();
        var h = new CommandHarness(MakeZip(("a/b/f.txt", Hello)));
        h.Fs.AddDirectory(@"C:\target\a");
        h.Fs.AddDirectory(@"C:\target\a\b");
        h.File(@"a\b\f.txt");
        h.Notifications = new Commands.CommandNotifications(
            OnPrepared: prepared.Add,
            OnDirectoryResult: result =>
            {
                Assert.Equal(0, h.Fs.OpenDirectoryDeletionHandleCount);
                directories.Add(result);
            });

        var run = h.Delete(pruneDirs: true);

        Assert.Equal(ExitStatus.Success, run.Outcome.Status);
        var info = Assert.Single(prepared);
        Assert.True(info.PruneDirs);
        Assert.Equal(2, info.DirectoriesSelected);
        Assert.Equal([@"a\b", "a"], directories.Select(d => d.Directory.Path));
        Assert.Equal(run.Outcome.Report!.Prune!.Results, directories);
        Assert.Empty(run.Output);
    }

    // D30: --prune-dirs は delete だけの値を取らないフラグで、位置は自由、重複と analyze での指定は入力エラー (docs/spec/cli.md#arguments)。
    [Fact]
    public void D30_PruneDirsArgument()
    {
        var parsed = CommandLine.CommandLineParser.Parse(["delete", "--prune-dirs", "a.zip", "--target", "dir"]);
        Assert.True(parsed.Options!.PruneDirs);
        Assert.False(CommandLine.CommandLineParser.Parse(["delete", "a.zip", "--target", "dir"]).Options!.PruneDirs);
        Assert.True(CommandLine.CommandLineParser.Parse(["delete", "a.zip", "--target", "dir", "--jsonl", "-y", "--prune-dirs"]).Options!.PruneDirs);
        Assert.Equal("--prune-dirs が複数回指定されています",
            CommandLine.CommandLineParser.Parse(["delete", "a.zip", "--prune-dirs", "--target", "dir", "--prune-dirs"]).Error);
        Assert.Equal("analyze では --prune-dirs を指定できません",
            CommandLine.CommandLineParser.Parse(["analyze", "a.zip", "--target", "dir", "--prune-dirs"]).Error);
        Assert.Equal("不明なオプションです: --prune-dirs=1",
            CommandLine.CommandLineParser.Parse(["delete", "a.zip", "--target", "dir", "--prune-dirs=1"]).Error);
    }

    // D15: --prune-dirs なしではディレクトリ段階を行わない (既定の動作は変えない)。
    [Fact]
    public void D15_WithoutPruneDirsNoDirectoryIsOpened()
    {
        using var h = new DeleteHarness(MakeZip(("d/f.txt", Hello), ("e/", null)));
        h.Fs.AddDirectory(@"C:\target\d");
        h.File(@"d\f.txt");
        h.Fs.AddDirectory(@"C:\target\e");

        var report = h.Run();

        Assert.Null(report.Prune);
        Assert.True(h.Exists("d"));
        Assert.True(h.Exists("e"));
        Assert.DoesNotContain(h.Fs.Calls, c => c.StartsWith("OpenDirectoryDeletion ", StringComparison.Ordinal));
    }
}
