using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Tests.Fakes;

// 実 ZIP (ZipArchive で読む) と偽の target で delete のエントリ処理 (逐次削除) を実行する。target は C:\target。
// Prepare (確認を含む) は通さない (Runner を通したテストは DeleteRunnerTests)。
// 実行のたびに、どの経路でも成り立つべき約束 (PLAN_TESTS §0 の「同じハンドル」) を確かめる。
internal sealed class DeleteHarness : IDisposable
{
    public const string TargetPath = PipelineHarness.TargetPath;
    public const string ArchivePath = PipelineHarness.ArchivePath;

    private static readonly byte[] Hello = PipelineHarness.Bytes("hello");

    private readonly PipelineHarness _inner;

    public DeleteHarness(byte[] zip, FakeFileSystem? fs = null)
    {
        _inner = new PipelineHarness(zip, fs);
    }

    public FakeFileSystem Fs => _inner.Fs;

    public RecordingContentProvider Contents => _inner.Contents;

    public ZipArchiveSource Source => _inner.Source;

    // 差し替えた ZIP の内容の取得元 (null なら Contents)。
    public IZipContentProvider? ContentsOverride { get; set; }

    public Limits Limits { get; set; } = Limits.Default;

    public RunMode Mode { get; set; } = RunMode.Strict;

    public DeleteHooks? Hooks { get; set; }

    // 処理対象の FullName (--entries で選んだ場合)。null なら全エントリ。
    public IReadOnlyCollection<string>? Selected { get; set; }

    public List<(int Current, int Total)> Progress { get; } = [];

    public List<DeleteEntryResult> Notified { get; } = [];

    public DeleteRun? LastRun { get; private set; }

    public FakeNode File(string name, byte[]? content = null) => Fs.AddFile($@"C:\target\{name}", content ?? Hello);

    public FakeNode Node(string name) => Fs.Get($@"C:\target\{name}");

    public bool Exists(string name) => Fs.Find($@"C:\target\{name}") is not null;

    public DeleteReport Run()
    {
        var prevalidation = ZipPrevalidator.Validate(Source.Entries, Limits);
        Assert.True(prevalidation.Passed, prevalidation.Fatal?.Describe());
        var entries = Selected is null
            ? prevalidation.Entries
            : prevalidation.Entries.Where(e => Selected.Contains(e.Entry.FullName)).ToList();

        var opened = TargetRootValidator.Open(Fs, TargetPath, TargetLocationPolicy.None);
        using var root = opened.Root ?? throw new InvalidOperationException(opened.Error!.Describe());
        var identity = Fs.GetFileIdentity(ArchivePath);
        Assert.True(identity.Succeeded);

        var start = Fs.Calls.Count;
        var comparisons = Fs.ComparisonOpenCount;
        var run = new DeleteRun(new DeleteRequest(
            entries,
            ContentsOverride ?? Contents,
            Fs,
            Fs,
            root,
            identity.Value,
            Limits,
            Mode,
            (c, t) => Progress.Add((c, t)),
            Notified.Add,
            Hooks));
        LastRun = run;
        var report = run.Execute();

        // 削除用ハンドルは各エントリの処理の中で閉じられ、同時に開くのは1つまで。開いているのは target ルートだけ (S04)。
        Assert.Equal(0, Fs.OpenDeletionHandleCount);
        Assert.Equal(Fs.DeletionOpenCount, Fs.DeletionCloseCount);
        Assert.True(Fs.MaxConcurrentDeletions <= 1);
        Assert.Equal(1, Fs.OpenHandleCount);

        // delete は比較用ハンドルを開かない。パスを使う呼び出しは、各エントリの OpenForDeletion 1回 (と、その失敗時の
        // CheckIdentity 1回) だけ (PLAN_TESTS §0 の「同じハンドル」)。
        var calls = Fs.Calls.Skip(start).ToList();
        Assert.DoesNotContain(calls, c => c.StartsWith("OpenComparison ", StringComparison.Ordinal));
        Assert.Equal(comparisons, Fs.ComparisonOpenCount);
        foreach (var group in calls.Where(c => c.StartsWith("OpenDeletion ", StringComparison.Ordinal) || c.StartsWith("CheckIdentity ", StringComparison.Ordinal)).GroupBy(c => c))
        {
            Assert.Single(group);
        }

        // 結果はエントリの処理ごとに通知され、通知の順と結果の順が一致する (逐次表示、O13)。
        Assert.Equal(report.Results, Notified);
        return report;
    }

    public void Dispose() => _inner.Dispose();
}
