using Unextract.Core.Analysis;
using Unextract.Core.Commands;
using Unextract.Core.Deletion;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;

namespace Unextract.Core.Tests.Fakes;

// 確認の入力を順に返すプロンプト。Asked は表示された文字列。
internal sealed class ScriptedPrompt(bool interactive, params string?[] answers) : IConfirmationPrompt
{
    private readonly Queue<string?> _answers = new(answers);

    public List<string> Asked { get; } = [];

    public bool IsInteractive => interactive;

    public string? Ask(string prompt)
    {
        Asked.Add(prompt);
        return _answers.Count > 0 ? _answers.Dequeue() : null;
    }
}

// Output は標準出力、Error は標準エラー出力に相当する (docs/spec/cli.md#streams)。
internal sealed record CommandRun<T>(T Outcome, string Output, string Error)
{
    public IReadOnlyList<string> OutputLines => Output.Split(Environment.NewLine).SkipLast(1).ToList();

    public IReadOnlyList<string> ErrorLines => Error.Split(Environment.NewLine).SkipLast(1).ToList();
}

// AnalyzeCommand・DeleteCommand を、実 ZIP (メモリ上の ZipArchive) と偽の target で実行する (Prepare と確認を含む)。
// target は C:\target、ZIP のパスは C:\in\archive.zip (偽 FS 上にも同じ内容で置き、ZIP 自身の個体の取得に使う)。
// 実行のたびに、target のハンドルが全て閉じていること、削除用ハンドルと比較用ハンドルの使い分けを確かめる。
internal sealed class CommandHarness
{
    public const string TargetPath = PipelineHarness.TargetPath;
    public const string ArchivePath = PipelineHarness.ArchivePath;

    private readonly byte[] _zip;

    public CommandHarness(byte[] zip, FakeFileSystem? fs = null, bool createTarget = true)
    {
        _zip = zip;
        Fs = fs ?? new FakeFileSystem();
        if (createTarget && Fs.Find(TargetPath) is null)
        {
            Fs.AddDirectory(TargetPath);
        }

        if (Fs.Find(@"C:\in") is null)
        {
            Fs.AddDirectory(@"C:\in");
            Fs.AddFile(ArchivePath, zip);
        }
    }

    public FakeFileSystem Fs { get; }

    public Limits Limits { get; set; } = Limits.Default;

    public RunMode Mode { get; set; } = RunMode.Strict;

    public TargetLocationPolicyResult Locations { get; set; } = new(TargetLocationPolicy.None, null);

    // ZIP を開く処理の差し替え (null なら _zip をメモリ上で開く)。
    public Func<string, ZipOpenResult>? OpenArchive { get; set; }

    // 直前の実行の ZIP の内容の呼び出し記録。
    public RecordingContentProvider? Contents { get; private set; }

    public List<(int Current, int Total)> Progress { get; } = [];

    public FakeNode File(string name, byte[]? content = null) => Fs.AddFile($@"C:\target\{name}", content ?? PipelineHarness.Bytes("hello"));

    public bool Exists(string name) => Fs.Find($@"C:\target\{name}") is not null;

    // テスト用の entries ファイルを書く (テストの出力先の fixtures/ に一意な名前で。テストからは削除しない)。
    public static string WriteEntries(byte[] content) => TestFiles.Write($"entries-{Guid.NewGuid():N}.txt", content);

    public static string WriteEntries(string text) => WriteEntries(System.Text.Encoding.UTF8.GetBytes(text));

    public CommandRun<AnalyzeCommandOutcome> Analyze(Action? afterResults = null)
    {
        var (output, error, context) = Context();
        var start = Fs.Calls.Count;
        var outcome = AnalyzeCommand.Run(new AnalyzeCommandRequest(ArchivePath, TargetPath, Mode, context, afterResults));

        // analyze は非破壊: 削除用オープン・識別確認・削除の指示を一度も呼ばない。比較用ハンドルは同時に1つまで。
        Assert.DoesNotContain(Fs.Calls.Skip(start), PipelineHarness.IsDeletionCall);
        Assert.True(Fs.MaxConcurrentComparisons <= 1);
        Assert.Equal(0, Fs.OpenHandleCount);
        return new CommandRun<AnalyzeCommandOutcome>(outcome, output.ToString(), error.ToString());
    }

    public CommandRun<DeleteCommandOutcome> Delete(
        string? entriesPath = null,
        bool yes = true,
        IConfirmationPrompt? prompt = null,
        DeleteHooks? hooks = null,
        Action? awaitingConfirmation = null)
    {
        var (output, error, context) = Context();
        var start = Fs.Calls.Count;
        var comparisons = Fs.ComparisonOpenCount;
        var outcome = DeleteCommand.Run(new DeleteCommandRequest(
            ArchivePath,
            TargetPath,
            Mode,
            entriesPath,
            yes,
            Fs,
            prompt ?? new ScriptedPrompt(true, "y"),
            context,
            awaitingConfirmation,
            hooks));

        // delete は比較用ハンドルを開かない。削除用ハンドルは各エントリの処理の中で閉じ、同時に1つまで。終了時に target のハンドルは無い。
        Assert.Equal(comparisons, Fs.ComparisonOpenCount);
        Assert.DoesNotContain(Fs.Calls.Skip(start), c => c.StartsWith("OpenComparison ", StringComparison.Ordinal));
        Assert.Equal(Fs.DeletionOpenCount, Fs.DeletionCloseCount);
        Assert.True(Fs.MaxConcurrentDeletions <= 1);
        Assert.Equal(0, Fs.OpenHandleCount);
        return new CommandRun<DeleteCommandOutcome>(outcome, output.ToString(), error.ToString());
    }

    // target ルート以外の target のエントリに触れたか (target ルート以外の列挙、比較用・削除用オープン、識別確認)。
    public bool TouchedTargetEntries(int since = 0) => Fs.Calls.Skip(since).Any(c =>
        c.StartsWith("OpenEnumeration ", StringComparison.Ordinal)
        || c.StartsWith("Enumerate ", StringComparison.Ordinal)
        || c.StartsWith("OpenComparison ", StringComparison.Ordinal)
        || c.StartsWith("OpenDeletion ", StringComparison.Ordinal)
        || c.StartsWith("CheckIdentity ", StringComparison.Ordinal));

    private (StringWriter Output, StringWriter Error, CommandContext Context) Context()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Progress.Clear();
        var context = new CommandContext(
            Fs,
            () => Locations,
            Limits,
            output,
            error,
            (c, t) => Progress.Add((c, t)),
            OpenArchive ?? (_ => ZipArchiveSource.Open(new MemoryStream(_zip))),
            source =>
            {
                Contents = new RecordingContentProvider(source);
                return Contents;
            });
        return (output, error, context);
    }
}
