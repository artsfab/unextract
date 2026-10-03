using Unextract.Core.Analysis;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;

namespace Unextract.Core.Tests.Fakes;

// ZIP 側の呼び出し記録。GetContent / Open / Crc32 の参照を記録する (内容を読まないことの確認、テスト C01〜C08)。
internal sealed class RecordingContentProvider(IZipContentProvider inner) : IZipContentProvider
{
    public List<string> Calls { get; } = [];

    public IZipEntryContent GetContent(int index)
    {
        Calls.Add($"GetContent {index}");
        return new RecordingContent(inner.GetContent(index), index, Calls);
    }

    public bool Opened(int index) => Calls.Contains($"Open {index}");

    public bool Touched(int index) => Calls.Any(c => c.EndsWith($" {index}", StringComparison.Ordinal));

    private sealed class RecordingContent(IZipEntryContent inner, int index, List<string> calls) : IZipEntryContent
    {
        public bool IsEncrypted => inner.IsEncrypted;

        public long Length => inner.Length;

        public uint Crc32
        {
            get
            {
                calls.Add($"Crc32 {index}");
                return inner.Crc32;
            }
        }

        public Stream Open()
        {
            calls.Add($"Open {index}");
            return inner.Open();
        }
    }
}

// 実 ZIP (ZipArchive で読む) と偽の target で analyze のエントリ処理を実行する。target は C:\target。
internal sealed class PipelineHarness : IDisposable
{
    public const string TargetPath = @"C:\target";
    public const string ArchivePath = @"C:\in\archive.zip";

    private readonly ZipArchiveSource _source;

    public PipelineHarness(byte[] zip, FakeFileSystem? fs = null)
    {
        Fs = fs ?? new FakeFileSystem();
        if (Fs.Find(@"C:\target") is null)
        {
            Fs.AddDirectory(@"C:\target");
        }

        if (Fs.Find(@"C:\in") is null)
        {
            Fs.AddDirectory(@"C:\in");
            Fs.AddFile(ArchivePath, zip);
        }

        var opened = ZipArchiveSource.Open(new MemoryStream(zip));
        _source = opened.Source ?? throw new InvalidOperationException(opened.Fatal!.Describe());
        Contents = new RecordingContentProvider(_source);
    }

    public FakeFileSystem Fs { get; }

    public RecordingContentProvider Contents { get; }

    public Limits Limits { get; set; } = Limits.Default;

    public string ArchiveLocation { get; set; } = ArchivePath;

    // analyze のモード (SPEC §15)。共通の安全性テストを Fast でも実行するために切り替える。
    public RunMode Mode { get; set; } = RunMode.Strict;

    public ZipArchiveSource Source => _source;

    // 実行に使った AnalyzeRun (RealNameResolver の保持内容の確認用)。
    public AnalyzeRun? LastRun { get; private set; }

    public List<(int Current, int Total)> Progress { get; } = [];

    public AnalysisResult Run()
    {
        var prevalidation = ZipPrevalidator.Validate(_source.Entries, Limits);
        Assert.True(prevalidation.Passed, prevalidation.Fatal?.Describe());

        var opened = TargetRootValidator.Open(Fs, TargetPath, TargetLocationPolicy.None);
        using var root = opened.Root ?? throw new InvalidOperationException(opened.Error!.Describe());
        var identity = Fs.GetFileIdentity(ArchiveLocation);
        Assert.True(identity.Succeeded);

        var start = Fs.Calls.Count;
        var run = new AnalyzeRun(new AnalyzeRequest(
            prevalidation.Entries, Contents, Fs, root, identity.Value, Limits, (c, t) => Progress.Add((c, t)), Mode));
        LastRun = run;
        var result = run.Execute();

        // 判定が終わった時点で、保持している target ルート以外のハンドルは全て閉じている (テスト T12)。
        Assert.Equal(1, Fs.OpenHandleCount);
        Assert.Equal(Fs.ComparisonOpenCount, Fs.ComparisonCloseCount);
        Assert.True(Fs.MaxConcurrentComparisons <= 1);

        // analyze は非破壊: 削除用オープン・識別確認・削除の指示を一度も呼ばない (PLAN_TESTS §0)。
        Assert.DoesNotContain(Fs.Calls.Skip(start), IsDeletionCall);
        return result;
    }

    public void Dispose() => _source.Dispose();

    // 削除の能力を使う呼び出し (OpenForDeletion、CheckIdentity、SetDispositionEx) の記録か。
    public static bool IsDeletionCall(string call) =>
        call.StartsWith("OpenDeletion ", StringComparison.Ordinal)
        || call.StartsWith("CheckIdentity ", StringComparison.Ordinal)
        || call.StartsWith("Disposition ", StringComparison.Ordinal);

    public static byte[] MakeZip(params (string Name, byte[]? Content)[] entries) =>
        ZipFixture.Create(entries.Select(e => new FixtureEntry(e.Name, e.Content)));

    public static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    // 削除候補の分類 (PLAN_TESTS のモード違いの再利用の原則: MATCHED を SAME_SIZE に読み替える)。
    public static Classification Candidate(RunMode mode) => mode == RunMode.Fast ? Classification.SameSize : Classification.Matched;
}
