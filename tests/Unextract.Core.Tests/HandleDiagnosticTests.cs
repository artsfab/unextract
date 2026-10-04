using Unextract.Core.Analysis;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fakes;

namespace Unextract.Core.Tests;

public class HandleDiagnosticTests
{
    // J05: 最終確認の情報取得失敗と M0 不一致を、表示文字列を解析せず区別できる。
    [Theory]
    [InlineData(FakeOp.VolumeFileId)]
    [InlineData(FakeOp.ParentFileId)]
    [InlineData(FakeOp.FinalPath)]
    [InlineData(FakeOp.Standard)]
    [InlineData(FakeOp.Basic)]
    [InlineData(FakeOp.AttributeTag)]
    [InlineData(FakeOp.Streams)]
    public void J05_FinalCheckInformationFailurePreservesNumberAndOrder(FakeOp op)
    {
        var (fs, node, handle, m0) = Open();
        using (handle)
        {
            node.Errors[op] = 1117;
            var start = fs.Calls.Count;

            var failure = HandleInspector.FinalCheck(handle, m0);

            Assert.NotNull(failure);
            Assert.Equal(FinalCheckFailureKind.InformationFailed, failure.Value.Kind);
            Assert.Equal(1117, failure.Value.Win32Error);
            Assert.Equal($"{op} が失敗 (Win32 エラー 1117)", failure.Value.Detail);
            var order = new[] { FakeOp.VolumeFileId, FakeOp.ParentFileId, FakeOp.FinalPath, FakeOp.Standard, FakeOp.Basic, FakeOp.AttributeTag, FakeOp.Streams };
            Assert.Equal(order.Take(Array.IndexOf(order, op) + 1).Select(item => $"{item} {handle.Path}"), fs.Calls.Skip(start));
        }

        Assert.Equal(0, fs.OpenHandleCount);
        Assert.Empty(fs.Deleted);
    }

    [Theory]
    [InlineData("id", "File ID")]
    [InlineData("volume", "File ID")]
    [InlineData("parent", "親 File ID")]
    [InlineData("path", "最終パス")]
    [InlineData("directory", "ディレクトリ")]
    [InlineData("pending", "削除保留中")]
    [InlineData("length", "EndOfFile")]
    [InlineData("links", "リンク数")]
    [InlineData("write-time", "LastWriteTime")]
    [InlineData("change-time", "ChangeTime")]
    [InlineData("attributes", "属性")]
    [InlineData("tag", "reparse 状態")]
    [InlineData("streams", "ストリーム一覧")]
    public void J05_FinalCheckMismatchHasNoWin32Number(string change, string detail)
    {
        var (fs, node, handle, m0) = Open();
        using (handle)
        {
            switch (change)
            {
                case "id": node.Id = fs.NextId(); break;
                case "volume": node.VolumeSerial++; break;
                case "parent": node.ParentFileIdOverride = fs.NextId(); break;
                case "path": node.FinalPathOverride = @"\\?\C:\TARGET\x.txt"; break;
                case "directory": node.IsDirectory = true; break;
                case "pending": node.DeletePending = true; break;
                case "length": node.Content = [1, 2]; break;
                case "links": node.Links++; break;
                case "write-time": node.LastWriteTime++; break;
                case "change-time": node.ChangeTime++; break;
                case "attributes": node.Attributes |= 0x2; break;
                case "tag": node.ReparseTag = FakeNode.ReparseTagSymlink; break;
                case "streams": node.ExtraStreams.Add(new StreamEntry(":extra:$DATA", 1)); break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }

            var failure = HandleInspector.FinalCheck(handle, m0);

            Assert.NotNull(failure);
            Assert.Equal(FinalCheckFailureKind.Mismatch, failure.Value.Kind);
            Assert.Equal(detail, failure.Value.Detail);
            Assert.Null(failure.Value.Win32Error);
        }

        Assert.Equal(0, fs.OpenHandleCount);
        Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void J05_FinalCheckMatchHasNoFailure()
    {
        var (fs, _, handle, m0) = Open();
        using (handle)
        {
            Assert.Null(HandleInspector.FinalCheck(handle, m0));
        }

        Assert.Equal(0, fs.OpenHandleCount);
    }

    private static (FakeFileSystem Fs, FakeNode Node, FakeComparisonHandle Handle, HandleState M0) Open()
    {
        var fs = new FakeFileSystem();
        var parent = fs.AddDirectory(@"C:\target");
        var node = fs.AddFile(@"C:\target\x.txt", [1]);
        var handle = Assert.IsType<FakeComparisonHandle>(fs.OpenForComparison(@"C:\target\x.txt").Value);
        var id = new VolumeFileId(node.VolumeSerial, node.Id);
        var inspection = HandleInspector.Inspect(handle, id, 1, default, stopOnDeletePending: true);
        Assert.Equal(InspectionKind.Candidate, inspection.Kind);
        return (fs, node, handle, HandleInspector.State(id, parent.Id, fs.FinalPathOf(node), inspection));
    }
}
