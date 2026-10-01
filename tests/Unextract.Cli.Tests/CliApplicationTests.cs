using System.IO.Compression;
using System.Runtime.CompilerServices;
using Unextract.Core.Analysis;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;
using ProtectedLocations = Unextract.Windows.ProtectedLocations;
using ProtectedLocationsResult = Unextract.Windows.ProtectedLocationsResult;
using WindowsFileSystemProbe = Unextract.Windows.WindowsFileSystemProbe;

namespace Unextract.Cli.Tests;

// CLI の引数解析、終了状態、出力先 (stdout / stderr)、最上位の例外捕捉、dry-run と通常実行の挙動。target は実 NTFS 上の fixture
// (テストの出力先の fixtures/ の下に毎回ユニークな名前で作る)。テストからは削除しない。
// 削除フェーズは記録するだけの偽物に差し替えるため、どのケースでも target のファイルは変わらない
// (実削除は Unextract.Windows.Tests でガード付きで確認する)。
public class CliApplicationTests
{
    private static readonly string FixtureDirectory =
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "fixtures")).FullName;

    private sealed class RecordingPrompt(string? answer = "y") : IConfirmationPrompt
    {
        public int AskCount { get; private set; }

        public bool IsInteractive => true;

        public string? Ask(string prompt)
        {
            AskCount++;
            return answer;
        }
    }

    // 削除フェーズの偽物。呼ばれたことを記録し、指定した結果を返す。ファイルには触れない。
    private sealed class RecordingDeletion(Func<DeletionRequest, DeletionReport>? result = null) : IDeletionPhase
    {
        public int CallCount { get; private set; }

        public DeletionReport Delete(DeletionRequest request)
        {
            CallCount++;
            return result?.Invoke(request) ?? new DeletionReport(request.Candidates.Select(c => c.Entry).ToList(), [], null, 0);
        }
    }

    // 指定した操作で例外を投げる probe (最上位の例外捕捉の確認用)。
    private sealed class ThrowingProbe(IFileSystemProbe inner, string throwOn) : IFileSystemProbe
    {
        public ProbeResult<TargetConfirmation> ConfirmTargetFinalComponent(string path) =>
            throwOn == nameof(ConfirmTargetFinalComponent) ? throw new IOException("injected \u202E failure") : inner.ConfirmTargetFinalComponent(path);

        public ProbeResult<IDirectoryHandle> OpenTargetRoot(string path) => inner.OpenTargetRoot(path);

        public ProbeResult<IDirectoryHandle> OpenDirectoryForEnumeration(string path) => inner.OpenDirectoryForEnumeration(path);

        public ProbeResult<IComparisonHandle> OpenForComparison(string path) =>
            throwOn == nameof(OpenForComparison) ? throw new InvalidOperationException("injected") : inner.OpenForComparison(path);

        public ProbeResult<VolumeFileId> GetFileIdentity(string path) => inner.GetFileIdentity(path);
    }

    private static (ExitStatus Status, string Stdout, string Stderr) Run(string[] args, CliEnvironment? environment = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var status = CliApplication.Run(args, stdout, stderr, environment ?? Environment());
        return (status, stdout.ToString(), stderr.ToString());
    }

    private static CliEnvironment Environment(IFileSystemProbe? probe = null, IConfirmationPrompt? prompt = null, IDeletionPhase? deletion = null) =>
        new(probe ?? new WindowsFileSystemProbe(), ProtectedLocations.Resolve, prompt ?? new RecordingPrompt(), ShowProgress: false, deletion ?? new RecordingDeletion());

    private static string NewDirectory([CallerMemberName] string name = "")
    {
        var path = Path.Combine(FixtureDirectory, $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteZip(string directory, params (string Name, string? Content)[] entries)
    {
        var path = Path.Combine(directory, "archive.zip");
        using (var stream = new FileStream(path, FileMode.CreateNew))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                if (content is not null)
                {
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }
            }
        }

        return path;
    }

    private static Dictionary<string, (long, DateTime)> Snapshot(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (File.Exists(p) ? new FileInfo(p).Length : -1, File.GetLastWriteTimeUtc(p)));

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new string[] { },
        new string[] { "a.zip" },
        new string[] { "a.zip", "--target" },
        new string[] { "a.zip", "--target=dir" },
        new string[] { "a.zip", "--target", "dir", "--unknown" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void InvalidArguments_AreInputError(string[] args)
    {
        var (status, stdout, stderr) = Run(args);

        Assert.Equal(ExitStatus.Error, status);
        Assert.Equal(1, ExitCodes.ToProcessExitCode(status));
        Assert.Empty(stdout);
        Assert.Contains("入力エラー", stderr);
        Assert.Contains("使い方", stderr);
    }

    [Fact]
    public void MissingArchive_IsError()
    {
        var dir = NewDirectory();

        var (status, _, stderr) = Run([Path.Combine(dir, "missing.zip"), "--target", dir]);

        Assert.Equal(ExitStatus.Error, status);
        Assert.Contains("FATAL: ZIP を開けません", stderr);
        Assert.Contains("削除0件", stderr);
    }

    [Fact]
    public void InvalidArchive_IsError()
    {
        var dir = NewDirectory();
        var path = Path.Combine(dir, "not-a-zip.zip");
        File.WriteAllText(path, "not a zip");

        var (status, _, stderr) = Run([path, "--target", dir]);

        Assert.Equal(ExitStatus.Error, status);
        Assert.Contains("FATAL: ZIP として読み取れません", stderr);
    }

    // 事前検証の FATAL: 原因エントリを表示し、削除0件、終了コード 1。target の項目は変わらない。
    [Fact]
    public void DangerousEntry_IsFatalWithEntryNumber()
    {
        var dir = NewDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var zip = WriteZip(dir, ("ok.txt", "x"), ("../evil.txt", "x"));

        var (status, stdout, stderr) = Run([zip, "--target", target, "--dry-run"]);

        Assert.Equal(ExitStatus.Error, status);

        // 解析結果の一覧は stdout、FATAL の原因と削除0件は stderr (SPEC §10)。
        Assert.Contains("判定済み: 0 エントリ", stdout);
        Assert.Contains("未判定: 1 エントリ", stdout);
        Assert.DoesNotContain("FATAL", stdout);
        Assert.Contains("FATAL: エントリ #2 \"../evil.txt\"", stderr);
        Assert.Contains("削除0件", stderr);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    // target が存在しない → 入力エラー、作成しない
    [Fact]
    public void MissingTarget_IsInputErrorAndNotCreated()
    {
        var dir = NewDirectory();
        var zip = WriteZip(dir, ("a.txt", "x"));
        var target = Path.Combine(dir, "no-such-target");

        var (status, stdout, stderr) = Run([zip, "--target", target]);

        Assert.Equal(ExitStatus.Error, status);
        Assert.Contains("入力エラー: target が存在しません", stderr);
        Assert.Empty(stdout);
        Assert.False(Directory.Exists(target));
    }

    // --dry-run: 全件解析、結果表示、FATAL なしなら 0。出力は C-2 の表示関数の結果と一致する。
    [Fact]
    public void DryRun_OutputMatchesReportFunction_AndExitsZero()
    {
        var dir = NewDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var zip = WriteZip(dir, ("same.txt", "hello"), ("changed.txt", "hello"), ("missing.txt", "x"), ("d/", null));
        File.WriteAllText(Path.Combine(target, "same.txt"), "hello");
        File.WriteAllText(Path.Combine(target, "changed.txt"), "hellO");
        Directory.CreateDirectory(Path.Combine(target, "d"));
        var before = Snapshot(target);

        var (status, stdout, stderr) = Run([zip, "--target", target, "--dry-run"]);

        Assert.Equal(ExitStatus.Success, status);
        Assert.Equal(0, ExitCodes.ToProcessExitCode(status));
        Assert.Empty(stderr);

        // 同じ入力を runner に直接渡して作った表示と一致する。
        var expected = RunnerReport(zip, target);
        var lines = stdout.Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal([.. expected, "--dry-run のため削除しません。"], lines);
        Assert.Contains("  same.txt", lines);
        Assert.Equal(before, Snapshot(target));
    }

    // 通常実行: 確認で n → 中止 (2)、削除フェーズに入らない。y・--yes → 削除フェーズに入り、全件削除なら成功 (0)。
    [Theory]
    [InlineData(false, "n", ExitStatus.UserCancelled, 2)]
    [InlineData(false, "", ExitStatus.UserCancelled, 2)]
    [InlineData(false, null, ExitStatus.UserCancelled, 2)]
    [InlineData(false, "y", ExitStatus.Success, 0)]
    [InlineData(true, null, ExitStatus.Success, 0)]
    public void NormalRun_WithCandidates_FollowsConfirmation(bool yes, string? answer, ExitStatus expected, int exitCode)
    {
        var dir = NewDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var zip = WriteZip(dir, ("same.txt", "hello"));
        var file = Path.Combine(target, "same.txt");
        File.WriteAllText(file, "hello");
        var before = Snapshot(target);
        var prompt = new RecordingPrompt(answer);
        var deletion = new RecordingDeletion();
        string[] args = yes ? [zip, "--target", target, "--yes"] : [zip, "--target", target];

        var (status, stdout, stderr) = Run(args, Environment(prompt: prompt, deletion: deletion));

        Assert.Equal(expected, status);
        Assert.Equal(exitCode, ExitCodes.ToProcessExitCode(status));
        Assert.Equal(yes ? 0 : 1, prompt.AskCount);
        Assert.Equal(expected == ExitStatus.Success ? 1 : 0, deletion.CallCount);
        Assert.Contains("MATCHED (1):", stdout);
        Assert.Contains(expected == ExitStatus.Success ? "削除済み 1、DELETE_FAILED 0、未処理 0" : "中止しました。削除0件。", stdout);
        Assert.Empty(stderr);
        Assert.Equal(before, Snapshot(target));
    }

    // DELETE_FAILED だけがあり停止がない → エラー (1)。停止 → エラー (1)。件数は stdout、理由は stderr (SPEC §2、§10、DEC-18)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletionWithFailures_ExitsOne(bool stopped)
    {
        var dir = NewDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var zip = WriteZip(dir, ("same.txt", "hello"));
        File.WriteAllText(Path.Combine(target, "same.txt"), "hello");
        var deletion = new RecordingDeletion(request =>
        {
            var entry = request.Candidates[0].Entry;
            return stopped
                ? new DeletionReport([], [], new DeletionStop(entry, "同一性の再検証で不一致: File ID", PossiblyDeleted: false), 0)
                : new DeletionReport([], [new DeleteFailure(entry, "使用中")], null, 0);
        });

        var (status, stdout, stderr) = Run([zip, "--target", target, "--yes"], Environment(deletion: deletion));

        Assert.Equal(ExitStatus.Error, status);
        Assert.Equal(1, ExitCodes.ToProcessExitCode(status));
        Assert.Contains(stopped ? "削除済み 0、DELETE_FAILED 0、未処理 0" : "削除済み 0、DELETE_FAILED 1、未処理 0", stdout);
        Assert.Contains(stopped ? "停止: same.txt: 同一性の再検証で不一致: File ID" : "DELETE_FAILED が 1 件あるため、エラーとして終了します", stderr);
    }

    // 通常実行: 削除候補0件で FATAL なしなら、プロンプトなしで成功 (0)
    [Fact]
    public void NormalRun_WithoutCandidates_ExitsZeroWithoutPrompt()
    {
        var dir = NewDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var zip = WriteZip(dir, ("missing.txt", "x"));
        var prompt = new RecordingPrompt();

        var (status, stdout, _) = Run([zip, "--target", target], Environment(prompt: prompt));

        Assert.Equal(ExitStatus.Success, status);
        Assert.Equal(0, prompt.AskCount);
        Assert.Contains("削除候補はありません。", stdout);
    }

    // 最上位の例外捕捉: probe から想定外の例外 → 安全なメッセージ (名前はエスケープ) でエラー (1)。削除は起きない。
    [Theory]
    [InlineData("ConfirmTargetFinalComponent")]
    [InlineData("OpenForComparison")]
    public void UnexpectedException_IsCaughtAndExitsOne(string throwOn)
    {
        var dir = NewDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var zip = WriteZip(dir, ("same.txt", "hello"));
        File.WriteAllText(Path.Combine(target, "same.txt"), "hello");
        var probe = new ThrowingProbe(new WindowsFileSystemProbe(), throwOn);

        var (status, _, stderr) = Run([zip, "--target", target], Environment(probe));

        Assert.Equal(ExitStatus.Error, status);
        Assert.Contains("内部エラー: 想定外の例外が発生しました", stderr);
        Assert.Contains("削除0件", stderr);
        Assert.DoesNotContain("\u202E", stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(target, "same.txt")));
    }

    // 拒否対象の実パスを解決できなければ入力エラー
    [Fact]
    public void UnresolvedProtectedLocations_IsInputError()
    {
        var dir = NewDirectory();
        var zip = WriteZip(dir, ("a.txt", "x"));
        var environment = Environment() with
        {
            ResolveProtectedLocations = () => new ProtectedLocationsResult(null, new FatalError(FatalKind.ProtectedLocationUnresolved)),
        };

        var (status, _, stderr) = Run([zip, "--target", dir], environment);

        Assert.Equal(ExitStatus.Error, status);
        Assert.Contains("入力エラー: 拒否対象のフォルダー", stderr);
    }

    private static IReadOnlyList<string> RunnerReport(string zipPath, string target)
    {
        var opened = ZipArchiveSource.Open(zipPath);
        using var source = opened.Source!;
        var outcome = UnextractRunner.Run(new RunRequest(
            source, zipPath, target, DryRun: true, AssumeYes: false, new WindowsFileSystemProbe(),
            ProtectedLocations.Resolve().Policy!, Limits.Default, new RecordingPrompt(), new RecordingDeletion(), TextWriter.Null, TextWriter.Null));
        return outcome.ReportLines;
    }
}
