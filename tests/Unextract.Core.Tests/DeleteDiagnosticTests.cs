using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Tests.Fakes;
using static Unextract.Core.Tests.Fakes.PipelineHarness;

namespace Unextract.Core.Tests;

// J06: delete 固有の診断 (docs/spec/machine-output.md#result、#codes)。識別確認自体の番号、resolve・hardlink 検査の失敗、指示失敗後の
// 状態と不確実性、正常結果の診断の省略、通知と Stop の原因参照。SequentialDeleteTests の S13/S15〜S18/S22/S23/S25/S27〜S33 と例外の
// テストは原因種別・段階・番号を、S05/S06 と CommandTests の O15 は同じハンドル・順序・表示を回帰する。
public class DeleteDiagnosticTests
{
    // J06: STOP の結果と終了原因は同じ診断を保持する。診断の追加で表示と後続の非接触を変えない。
    [Theory]
    [InlineData(32, RunMode.Strict)]
    [InlineData(5, RunMode.Strict)]
    [InlineData(32, RunMode.Fast)]
    [InlineData(5, RunMode.Fast)]
    public void J06_IdentityFailurePreservesItsOwnError(int openError, RunMode mode)
    {
        using var h = Create(mode);
        var node = h.Node("a.txt");
        node.Errors[FakeOp.OpenDeletion] = openError;
        node.Errors[FakeOp.CheckIdentity] = 1117;

        var stop = AssertStop(h, new DeleteFailure(DeleteFailureKind.IdentityCheckFailed, EntryStep.Open, 1117), out var report);

        var openText = openError == 32 ? "他のプログラムが使用中 (共有違反)" : "アクセス拒否";
        Assert.Equal($"削除用に開けません (Win32 エラー {openError}: {openText})。識別確認も失敗 (CheckIdentity が失敗 (Win32 エラー 1117))", stop.Reason);
        Assert.Equal($"停止: エントリ #1 \"a.txt\": {stop.Reason}", DeleteOutput.Errors(report)[0]);
    }

    [Theory]
    [InlineData(FakeOp.OpenEnumeration, FatalKind.EnumerationOpenFailed)]
    [InlineData(FakeOp.DirectoryInfo, FatalKind.EnumerationHandleMismatch)]
    [InlineData(FakeOp.Enumerate, FatalKind.EnumerationFailed)]
    public void J06_ResolutionFailurePreservesStageAndNumber(FakeOp op, FatalKind kind)
    {
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            using var h = new DeleteHarness(MakeZip(("d/a.txt", Bytes("hello")), ("b.txt", Bytes("hello")))) { Mode = mode };
            // target ルートの確認には影響しない子ディレクトリで失敗を注入する。
            h.Fs.AddDirectory(@"C:\target\d").Errors[op] = 1117;
            h.File(@"d\a.txt");
            h.File("b.txt");
            AssertStop(h, new DeleteFailure(kind, EntryStep.Resolve, 1117));
            Assert.Equal(0, h.Fs.DeletionOpenCount);
        }
    }

    [Theory]
    [InlineData(RunMode.Strict, false)]
    [InlineData(RunMode.Fast, false)]
    [InlineData(RunMode.Strict, true)]
    [InlineData(RunMode.Fast, true)]
    public void J06_HardLinkInspectionFailureKeepsDiagnostic(RunMode mode, bool pending)
    {
        using var h = Create(mode);
        var node = h.Node("a.txt");
        h.Hooks = new DeleteHooks
        {
            AfterOpen = (_, _) =>
            {
                node.Links = 2;
                if (pending)
                {
                    node.DeletePending = true;
                }
                else
                {
                    node.Errors[FakeOp.Basic] = 1117;
                }
            },
        };

        AssertStop(h, pending
            ? new DeleteFailure(FatalKind.TargetDeletePending, EntryStep.Inspect)
            : new DeleteFailure(FatalKind.TargetInfoFailed, EntryStep.Inspect, 1117));
        Assert.Empty(h.Contents.Calls);
    }

    [Theory]
    [InlineData("false", RunMode.Strict, false)]
    [InlineData("true", RunMode.Strict, true)]
    [InlineData("unreadable", RunMode.Strict, true)]
    [InlineData("false", RunMode.Fast, false)]
    [InlineData("true", RunMode.Fast, true)]
    [InlineData("unreadable", RunMode.Fast, true)]
    public void J06_DispositionFailurePreservesOriginalErrorAndUncertainty(string after, RunMode mode, bool possiblyDeleted)
    {
        using var h = Create(mode);
        var node = h.Node("a.txt");
        h.Hooks = new DeleteHooks
        {
            BeforeDisposition = (_, _) =>
            {
                node.Errors[FakeOp.Disposition] = 5;
                if (after == "unreadable")
                {
                    node.Errors[FakeOp.Standard] = 1117;
                }
                else
                {
                    node.DeletePending = after == "true";
                }
            },
        };

        var stop = AssertStop(h, new DeleteFailure(DeleteFailureKind.DispositionFailed, EntryStep.Dispose, 5));

        Assert.Equal(possiblyDeleted, stop.PossiblyDeleted);
        Assert.Equal("削除の指示が失敗: Disposition が失敗 (Win32 エラー 5)", stop.Reason);
        Assert.True(h.Exists("a.txt"));
    }

    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void J06_NormalResultsHaveNoFailure(RunMode mode)
    {
        using var h = new DeleteHarness(MakeZip(("deleted.txt", Bytes("hello")), ("modified.txt", Bytes("hello")),
            ("missing.txt", Bytes("hello")), ("special.txt", Bytes("hello")))) { Mode = mode };
        h.File("deleted.txt");
        h.File("modified.txt", Bytes("different size"));
        h.File("special.txt").Attributes |= 0x1;

        var report = h.Run();

        Assert.Equal(new[] { DeleteStatus.Deleted, DeleteStatus.Modified, DeleteStatus.Missing, DeleteStatus.SkippedSpecialFile },
            report.Results.Select(result => result.Status));
        Assert.All(report.Results, result => Assert.Null(result.Failure));
    }

    private static DeleteHarness Create(RunMode mode)
    {
        var h = new DeleteHarness(MakeZip(("a.txt", Bytes("hello")), ("b.txt", Bytes("hello")))) { Mode = mode };
        h.File("a.txt");
        h.File("b.txt");
        return h;
    }

    private static DeleteEntryResult AssertStop(DeleteHarness h, DeleteFailure expected)
        => AssertStop(h, expected, out _);

    private static DeleteEntryResult AssertStop(DeleteHarness h, DeleteFailure expected, out DeleteReport report)
    {
        report = h.Run();
        var stop = Assert.Single(report.Results);
        Assert.Same(stop, report.Stop);
        Assert.Same(stop, Assert.Single(h.Notified));
        Assert.Equal(expected, stop.Failure);
        Assert.Equal(1, report.NotProcessedCount);
        Assert.Empty(h.Fs.Deleted);
        Assert.True(h.Exists("b.txt"));
        Assert.DoesNotContain(h.Fs.Calls, call => call.EndsWith("b.txt", StringComparison.Ordinal));
        return stop;
    }
}
