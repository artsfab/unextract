using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Unextract.Core.CommandLine;
using Unextract.Core.Commands;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Target;
using ProtectedLocations = Unextract.Windows.ProtectedLocations;
using ProtectedLocationsResult = Unextract.Windows.ProtectedLocationsResult;
using WindowsFileSystemProbe = Unextract.Windows.WindowsFileSystemProbe;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.Cli.Tests;

// CLI の引数解析、サブコマンドの振り分け、終了状態、出力先 (stdout / stderr)、最上位の例外捕捉、進捗の表示 (K 系・O16 の CLI の部分)。
// target は実 NTFS 上の fixture (テストの出力先の fixtures/ の下に <テスト名>-<GUID> で作る。TestFixtures)。テストの終了後に共通の削除処理が削除する。
// delete の削除の指示は SimulatedDeletionProbe で模擬する (削除用ハンドルは実際に開くが、SetDispositionEx を OS に渡さない)。
// そのため、どのケースでも target のファイルは変わらない (実削除は Unextract.Windows.Tests でガード付きで確認する)。
public class CliApplicationTests
{
    private sealed class RecordingPrompt(string? answer = "y", bool interactive = true) : IConfirmationPrompt
    {
        public List<string> Asked { get; } = [];

        public bool IsInteractive => interactive;

        public string? Ask(string prompt)
        {
            Asked.Add(prompt);
            return answer;
        }
    }

    // 削除用ハンドルは実際に開くが、削除の指示は記録するだけで OS に渡さない。指示の後は同じハンドルの DeletePending を true と報告する
    // (削除が成立したように見せる)。OpenError で削除用オープンの失敗を、DispositionError で削除の指示の失敗を注入する。
    private sealed class SimulatedDeletionProbe(IDeletionProbe inner) : IDeletionProbe
    {
        public List<string> Opened { get; } = [];

        public List<uint> Dispositions { get; } = [];

        public Func<string, int>? OpenError { get; init; }

        public int DispositionError { get; init; }

        public ProbeResult<IDeletionHandle> OpenForDeletion(string path)
        {
            Opened.Add(path);
            if (OpenError?.Invoke(path) is int error and not 0)
            {
                return ProbeResult<IDeletionHandle>.Fail(error, "OpenDeletion");
            }

            var opened = inner.OpenForDeletion(path);
            return opened.Succeeded ? ProbeResult<IDeletionHandle>.Ok(new Handle(opened.Value, this)) : opened;
        }

        public ProbeResult<IdentityCheckInfo> CheckIdentity(string path) => inner.CheckIdentity(path);

        private sealed class Handle(IDeletionHandle inner, SimulatedDeletionProbe owner) : IDeletionHandle
        {
            private bool _pending;

            public ProbeResult<VolumeFileId> GetVolumeFileId() => inner.GetVolumeFileId();

            public ProbeResult<StandardInformation> GetStandardInformation()
            {
                var result = inner.GetStandardInformation();
                return result.Succeeded && _pending
                    ? ProbeResult<StandardInformation>.Ok(result.Value with { DeletePending = true })
                    : result;
            }

            public ProbeResult<BasicInformation> GetBasicInformation() => inner.GetBasicInformation();

            public ProbeResult<AttributeTagInformation> GetAttributeTagInformation() => inner.GetAttributeTagInformation();

            public ProbeResult<IReadOnlyList<StreamEntry>> GetStreams() => inner.GetStreams();

            public ProbeResult<FileId> GetParentFileId() => inner.GetParentFileId();

            public ProbeResult<string> GetFinalPath() => inner.GetFinalPath();

            public ProbeResult<int> Read(Span<byte> buffer) => inner.Read(buffer);

            public ProbeResult<bool> SetDispositionEx(uint flags)
            {
                owner.Dispositions.Add(flags);
                if (owner.DispositionError != 0)
                {
                    return ProbeResult<bool>.Fail(owner.DispositionError, "Disposition");
                }

                _pending = (flags & 0x1) != 0;
                return ProbeResult<bool>.Ok(true);
            }

            public void Dispose() => inner.Dispose();
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

    private static CliEnvironment Environment(
        IFileSystemProbe? probe = null, IConfirmationPrompt? prompt = null, IDeletionProbe? deletion = null, bool showProgress = false) =>
        new(
            probe ?? new WindowsFileSystemProbe(),
            deletion ?? new SimulatedDeletionProbe(new WindowsFileSystemProbe()),
            ProtectedLocations.Resolve,
            prompt ?? new RecordingPrompt(),
            showProgress);

    private static string NewDirectory() => TestFixtures.Create();

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

    // 同一内容 (same.txt)、同サイズの内容違い (changed.txt)、サイズ違い (size.txt)、不存在 (missing.txt)、ディレクトリエントリ (d/)。
    private static (string Zip, string Target) Standard()
    {
        var dir = NewDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var zip = WriteZip(dir, ("same.txt", "hello"), ("changed.txt", "hello"), ("size.txt", "hello"), ("missing.txt", "x"), ("d/", null));
        File.WriteAllText(Path.Combine(target, "same.txt"), "hello");
        File.WriteAllText(Path.Combine(target, "changed.txt"), "hellO");
        File.WriteAllText(Path.Combine(target, "size.txt"), "hello!");
        Directory.CreateDirectory(Path.Combine(target, "d"));
        return (zip, target);
    }

    private static Dictionary<string, (long, DateTime)> Snapshot(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (File.Exists(p) ? new FileInfo(p).Length : -1, File.GetLastWriteTimeUtc(p)));

    private static string[] Lines(string text) => text.Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    // K02・K03・K04 (CLI): 旧形式、サブコマンドなし・不明、--dry-run、引数の各不正 → 入力エラー (1)。stderr に入力エラーと両サブコマンドの
    // 使い方。stdout は空。削除処理を開始しない (fixture 全体が不変)。
    public static TheoryData<string[], string> InvalidArguments => new()
    {
        { new string[0], CommandLineParser.SubcommandRequired },
        { new[] { "{zip}", "--target", "{target}" }, CommandLineParser.SubcommandRequired },
        { new[] { "{zip}", "--target", "{target}", "--yes" }, CommandLineParser.SubcommandRequired },
        { new[] { "Delete", "{zip}", "--target", "{target}", "--yes" }, CommandLineParser.SubcommandRequired },
        { new[] { "remove", "{zip}", "--target", "{target}" }, CommandLineParser.SubcommandRequired },
        { new[] { "{zip}", "--target", "{target}", "--dry-run" }, CommandLineParser.DryRunRemoved },
        { new[] { "delete", "{zip}", "--target", "{target}", "--dry-run", "--yes" }, CommandLineParser.DryRunRemoved },
        { new[] { "analyze", "{zip}", "--dry-run", "--target", "{target}" }, CommandLineParser.DryRunRemoved },
        { new[] { "analyze", "{zip}", "--target", "{target}", "--yes" }, "analyze では --yes を指定できません" },
        { new[] { "delete", "{zip}", "--target={target}" }, "不明なオプションです: --target={target}" },
        { new[] { "delete", "--target", "{target}" }, "ZIP のパスがありません" },
        { new[] { "delete", "{zip}", "{zip}", "--target", "{target}" }, "ZIP は1つだけ指定できます" },
        { new[] { "delete", "{zip}", "--target", "{target}", "--fast", "--fast" }, "--fast が複数回指定されています" },
        { new[] { "delete", "{zip}", "--target", "{target}", "--entries" }, "--entries の値がありません" },
        { new[] { "delete", "{zip}", "--target" }, "--target の値がありません" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void K02_K03_K04_InvalidArguments_AreInputErrors(string[] template, string error)
    {
        var (zip, target) = Standard();
        var before = Snapshot(target);
        var deletion = new SimulatedDeletionProbe(new WindowsFileSystemProbe());
        var prompt = new RecordingPrompt();
        var args = template.Select(a => a.Replace("{zip}", zip, StringComparison.Ordinal).Replace("{target}", target, StringComparison.Ordinal)).ToArray();

        var (status, stdout, stderr) = Run(args, Environment(prompt: prompt, deletion: deletion));

        Assert.Equal(ExitStatus.Error, status);
        Assert.Equal(1, ExitCodes.ToProcessExitCode(status));
        Assert.Empty(stdout);
        Assert.Equal(
            [$"入力エラー: {error.Replace("{target}", target, StringComparison.Ordinal)}", .. CommandLineParser.UsageLines],
            Lines(stderr));
        Assert.Empty(prompt.Asked);
        Assert.Empty(deletion.Opened);
        Assert.Equal(before, Snapshot(target));
    }

    // ZIP が存在しない・ZIP として読めない (P03・Z09 の CLI): analyze・delete とも FATAL で削除0件。stdout は空。
    [Theory]
    [InlineData("analyze", "missing")]
    [InlineData("analyze", "invalid")]
    [InlineData("analyze", "z09-prefix")]
    [InlineData("delete", "missing")]
    [InlineData("delete", "invalid")]
    [InlineData("delete", "z09-prefix")]
    public void ArchiveCannotBeOpened_IsFatal(string command, string kind)
    {
        foreach (var fast in new[] { false, true })
        {
            var (zip, target) = Standard();
            var path = kind switch
            {
                "missing" => Path.Combine(Path.GetDirectoryName(zip)!, "missing.zip"),
                "invalid" => Path.Combine(Path.GetDirectoryName(zip)!, "not-a-zip.zip"),
                _ => Path.Combine(Path.GetDirectoryName(zip)!, "prefixed.zip"),
            };
            if (kind == "invalid")
            {
                File.WriteAllText(path, "not a zip");
            }
            else if (kind == "z09-prefix")
            {
                // Z09: 先頭にデータを付けただけでオフセットを調整していない ZIP (ZipArchive が開けない)。
                File.WriteAllBytes(path, [.. new byte[4096], .. File.ReadAllBytes(zip)]);
            }

            string[] args = command == "delete" ? ["delete", path, "--target", target, "--yes"] : ["analyze", path, "--target", target];
            var (status, stdout, stderr) = Run(fast ? [.. args, "--fast"] : args);

            Assert.Equal(ExitStatus.Error, status);
            Assert.Empty(stdout);
            var lines = Lines(stderr);
            Assert.StartsWith(kind == "missing" ? "FATAL: ZIP を開けません" : "FATAL: ZIP として読み取れません", lines[0], StringComparison.Ordinal);
            Assert.Equal(command == "delete" ? DeleteOutput.PrepareAborted : AnalyzeOutput.FatalClosing, lines[1]);
            Assert.DoesNotContain(ReportText.FastWarning, stderr, StringComparison.Ordinal);
        }
    }

    // analyze: 全件の結果行と合計行が stdout、終了 0、stderr は空、target 不変。--fast は先頭行が警告で、MATCHED の代わりに SAME_SIZE。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Analyze_PrintsResultsAndDoesNotChangeTarget(bool fast)
    {
        var (zip, target) = Standard();
        File.WriteAllText(Path.Combine(target, "unrelated.txt"), "x");
        var before = Snapshot(target);
        var deletion = new SimulatedDeletionProbe(new WindowsFileSystemProbe());
        string[] args = fast ? ["analyze", zip, "--target", target, "--fast"] : ["analyze", zip, "--target", target];

        var (status, stdout, stderr) = Run(args, Environment(deletion: deletion));

        Assert.Equal(ExitStatus.Success, status);
        Assert.Equal(0, ExitCodes.ToProcessExitCode(status));
        Assert.Empty(stderr);
        Assert.Empty(deletion.Opened);
        Assert.Equal(before, Snapshot(target));
        var lines = Lines(stdout);
        var header = fast ? 5 : 4;
        Assert.Equal(fast, lines[0] == ReportText.FastWarning);
        Assert.Equal($"Archive: {zip}", lines[header - 4]);
        Assert.Equal($"Target:  {target}", lines[header - 3]);
        Assert.Equal(fast ? "Mode:    Fast" : "Mode:    Strict", lines[header - 2]);
        Assert.Equal(ReportText.Legend, lines[header - 1]);
        Assert.Equal(ReportText.Heading, lines[header]);
        string[] results = fast
            ?
            [
                $@"SAME_SIZE             same.txt -> {target}\same.txt",
                $@"SAME_SIZE             changed.txt -> {target}\changed.txt",
                $@"MODIFIED              size.txt -> {target}\size.txt",
                $@"MISSING               missing.txt -> {target}\missing.txt",
                $@"DIRECTORY             d/ -> {target}\d",
                "合計: 5 エントリ (SAME_SIZE 2、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 1)",
            ]
            :
            [
                $@"MATCHED               same.txt -> {target}\same.txt",
                $@"MODIFIED              changed.txt -> {target}\changed.txt",
                $@"MODIFIED              size.txt -> {target}\size.txt",
                $@"MISSING               missing.txt -> {target}\missing.txt",
                $@"DIRECTORY             d/ -> {target}\d",
                "合計: 5 エントリ (MATCHED 1、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 1)",
            ];
        Assert.Equal([.. results, AnalyzeOutput.Closing], lines[(header + 1)..]);
    }

    // 事前検証の FATAL (analyze): 結果表示に至り、判定済み 0・未判定は stdout、原因と削除0件は stderr。target 不変。
    [Fact]
    public void Analyze_DangerousEntry_IsFatalWithEntryNumber()
    {
        var dir = NewDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var zip = WriteZip(dir, ("ok.txt", "x"), ("../evil.txt", "x"));

        var (status, stdout, stderr) = Run(["analyze", zip, "--target", target]);

        Assert.Equal(ExitStatus.Error, status);
        Assert.Equal(["判定済み: 0 エントリ", "未判定: 2 エントリ"], Lines(stdout)[^2..]);
        Assert.DoesNotContain("FATAL", stdout, StringComparison.Ordinal);
        Assert.Equal(["FATAL: エントリ #2 \"../evil.txt\": パス成分 \"..\" を含みます", AnalyzeOutput.FatalClosing], Lines(stderr));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    // target が存在しない → 入力エラー、作成しない (analyze・delete)。
    [Theory]
    [InlineData("analyze")]
    [InlineData("delete")]
    public void MissingTarget_IsInputErrorAndNotCreated(string command)
    {
        var dir = NewDirectory();
        var zip = WriteZip(dir, ("a.txt", "x"));
        var target = Path.Combine(dir, "no-such-target");

        var (status, stdout, stderr) = Run([command, zip, "--target", target, "--fast"]);

        Assert.Equal(ExitStatus.Error, status);
        Assert.StartsWith("入力エラー: target が存在しません", stderr, StringComparison.Ordinal);
        Assert.Empty(stdout);
        Assert.False(Directory.Exists(target));
        Assert.DoesNotContain(ReportText.FastWarning, stderr, StringComparison.Ordinal);
    }

    // delete の確認: n・空入力・EOF → 中止 (2)、削除用オープン0回。y・--yes → 逐次処理 (削除は模擬)、成功 (0)。非対話で --yes なし → 中止 (2)。
    [Theory]
    [InlineData(false, "n", true, ExitStatus.UserCancelled)]
    [InlineData(false, "", true, ExitStatus.UserCancelled)]
    [InlineData(false, null, true, ExitStatus.UserCancelled)]
    [InlineData(false, "y", false, ExitStatus.UserCancelled)]
    [InlineData(false, "y", true, ExitStatus.Success)]
    [InlineData(true, null, true, ExitStatus.Success)]
    [InlineData(true, null, false, ExitStatus.Success)]
    public void Delete_FollowsConfirmation(bool yes, string? answer, bool interactive, ExitStatus expected)
    {
        var (zip, target) = Standard();
        var before = Snapshot(target);
        var prompt = new RecordingPrompt(answer, interactive);
        var deletion = new SimulatedDeletionProbe(new WindowsFileSystemProbe());
        string[] args = yes ? ["delete", zip, "--target", target, "--yes"] : ["delete", zip, "--target", target];

        var (status, stdout, stderr) = Run(args, Environment(prompt: prompt, deletion: deletion));

        Assert.Equal(expected, status);
        Assert.Equal(expected == ExitStatus.Success ? 0 : 2, ExitCodes.ToProcessExitCode(status));
        Assert.Equal(!yes && interactive ? 1 : 0, prompt.Asked.Count);
        Assert.Empty(stderr);
        Assert.Equal(before, Snapshot(target));
        var lines = Lines(stdout);
        Assert.Equal("対象: 全 5 エントリ", lines[4]);
        if (expected == ExitStatus.Success)
        {
            Assert.Equal([0x3u], deletion.Dispositions);
            Assert.Contains($@"DELETED               same.txt -> {target}\same.txt", lines);
            Assert.Equal("要約: 削除済み 1、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 1、DELETE_FAILED 0、処理対象外 0、未処理 0", lines[^1]);
        }
        else
        {
            Assert.Empty(deletion.Opened);
            Assert.Equal(DeleteOutput.Cancelled, lines[^1]);
            Assert.Equal(!interactive, stdout.Contains(DeleteOutput.NotInteractive, StringComparison.Ordinal));
        }
    }

    // DELETE_FAILED だけがあり STOP がない → エラー (1)。STOP → エラー (1)。要約は stdout、理由は stderr (docs/spec/cli.md#arguments、docs/spec/cli.md#delete-output、docs/RATIONALE.md#open-failures)。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteWithFailures_ExitsOne(bool stopped)
    {
        var (zip, target) = Standard();
        var deletion = new SimulatedDeletionProbe(new WindowsFileSystemProbe())
        {
            OpenError = path => !stopped && path.EndsWith(@"\same.txt", StringComparison.Ordinal) ? 32 : 0,
            DispositionError = stopped ? 5 : 0,
        };

        var (status, stdout, stderr) = Run(["delete", zip, "--target", target, "--yes"], Environment(deletion: deletion));

        Assert.Equal(ExitStatus.Error, status);
        Assert.Equal(1, ExitCodes.ToProcessExitCode(status));
        var lines = Lines(stdout);
        if (stopped)
        {
            Assert.Contains($@"STOPPED               same.txt -> {target}\same.txt", lines);
            Assert.Equal("要約: 削除済み 0、MODIFIED 0、MISSING 0、SKIPPED_SPECIAL_FILE 0、DIRECTORY 0、DELETE_FAILED 0、処理対象外 0、未処理 4", lines[^2]);
            Assert.Equal(
                ["停止: エントリ #1 \"same.txt\": 削除の指示が失敗: Disposition が失敗 (Win32 エラー 5)", "以後の処理を停止しました (削除済み 0、DELETE_FAILED 0、未処理 4)。"],
                Lines(stderr));
        }
        else
        {
            Assert.StartsWith($@"DELETE_FAILED         same.txt -> {target}\same.txt : 削除用に開けません (Win32 エラー 32", lines.Single(l => l.StartsWith("DELETE_FAILED ", StringComparison.Ordinal)), StringComparison.Ordinal);
            Assert.Equal("要約: 削除済み 0、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 1、DELETE_FAILED 1、処理対象外 0、未処理 0", lines[^1]);
            Assert.Equal(["DELETE_FAILED が 1 件あるため、エラーとして終了します (削除済み 0)。"], Lines(stderr));
        }

        Assert.True(File.Exists(Path.Combine(target, "same.txt")));
    }

    // 最上位の例外捕捉: probe から想定外の例外 → 安全なメッセージ (名前はエスケープ) でエラー (1)。削除は起きない。
    [Theory]
    [InlineData("analyze", "ConfirmTargetFinalComponent")]
    [InlineData("analyze", "OpenForComparison")]
    [InlineData("delete", "ConfirmTargetFinalComponent")]
    public void UnexpectedException_IsCaughtAndExitsOne(string command, string throwOn)
    {
        var (zip, target) = Standard();
        var probe = new ThrowingProbe(new WindowsFileSystemProbe(), throwOn);
        var deletion = new SimulatedDeletionProbe(new WindowsFileSystemProbe());

        var (status, _, stderr) = Run([command, zip, "--target", target, .. command == "delete" ? new[] { "--yes" } : []], Environment(probe, deletion: deletion));

        Assert.Equal(ExitStatus.Error, status);
        Assert.Contains("内部エラー: 想定外の例外が発生しました", stderr, StringComparison.Ordinal);
        Assert.Contains("中止しました。削除0件。", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", stderr, StringComparison.Ordinal);
        Assert.Empty(deletion.Opened);
        Assert.True(File.Exists(Path.Combine(target, "same.txt")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Delete_SummaryOutputFailure_NeverClaimsZeroDeletions(bool fast, bool failErrorReporting)
    {
        var (zip, target) = Standard();
        var before = Snapshot(target);
        var deletion = new SimulatedDeletionProbe(new WindowsFileSystemProbe());
        var stdout = new FailOnceWriter("要約:");
        var stderr = new FailOnceWriter(failErrorReporting ? "内部エラー:" : "never");

        var status = CliApplication.Run(
            ["delete", zip, "--target", target, "--yes", .. fast ? new[] { "--fast" } : []],
            stdout, stderr, Environment(deletion: deletion));

        Assert.Equal(ExitStatus.Error, status);
        Assert.Equal(fast ? 2 : 1, deletion.Dispositions.Count);
        Assert.All(deletion.Dispositions, flags => Assert.Equal(0x3u, flags));
        Assert.Contains("DELETED ", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains(failErrorReporting ? "削除が行われた可能性があります" : $"削除済み {(fast ? 2 : 1)} 件", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("削除0件", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(target)); // この層の disposition は模擬。実削除の副作用は Core の回帰テストで確認する。
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_OutputFailureBeforeStart_CanClaimZeroDeletions(bool cancelled)
    {
        var (zip, target) = Standard();
        var before = Snapshot(target);
        var deletion = new SimulatedDeletionProbe(new WindowsFileSystemProbe());
        var stdout = new FailOnceWriter(cancelled ? DeleteOutput.Cancelled : "Mode:");
        var stderr = new StringWriter();

        var status = CliApplication.Run(
            ["delete", zip, "--target", target, .. cancelled ? Array.Empty<string>() : new[] { "--yes" }],
            stdout, stderr, Environment(prompt: new RecordingPrompt("n"), deletion: deletion));

        Assert.Equal(ExitStatus.Error, status);
        Assert.Contains("中止しました。削除0件。", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(deletion.Opened);
        Assert.Empty(deletion.Dispositions);
        Assert.Equal(before, Snapshot(target));
    }

    private sealed class FailOnceWriter(string trigger) : StringWriter
    {
        private bool _failed;

        public override void WriteLine(string? value)
        {
            if (!_failed && value?.StartsWith(trigger, StringComparison.Ordinal) == true)
            {
                _failed = true;
                throw new IOException("injected output failure");
            }

            base.WriteLine(value);
        }
    }

    // 拒否対象の実パスを解決できなければ入力エラー (analyze・delete)。
    [Theory]
    [InlineData("analyze")]
    [InlineData("delete")]
    public void UnresolvedProtectedLocations_IsInputError(string command)
    {
        var (zip, target) = Standard();
        var environment = Environment() with
        {
            ResolveProtectedLocations = () => new ProtectedLocationsResult(null, new FatalError(FatalKind.ProtectedLocationUnresolved)),
        };

        string[] args = command == "delete" ? [command, zip, "--target", target, "--yes"] : [command, zip, "--target", target];

        var (status, stdout, stderr) = Run(args, environment);

        Assert.Equal(ExitStatus.Error, status);
        Assert.Empty(stdout);
        Assert.StartsWith("入力エラー: 拒否対象のフォルダー", stderr, StringComparison.Ordinal);
    }

    // --fast は delete のモードとして渡り、同サイズの内容違い (changed.txt) も削除対象になる (削除は模擬)。--fast なしは Strict のまま。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FastOption_IsPassedToDelete(bool fast)
    {
        var (zip, target) = Standard();
        var deletion = new SimulatedDeletionProbe(new WindowsFileSystemProbe());
        string[] args = fast ? ["delete", zip, "--target", target, "--yes", "--fast"] : ["delete", zip, "--target", target, "--yes"];

        var (status, stdout, _) = Run(args, Environment(deletion: deletion));

        Assert.Equal(ExitStatus.Success, status);
        Assert.Equal(fast, Lines(stdout)[0] == ReportText.FastWarning);
        Assert.Equal(fast ? 2 : 1, deletion.Dispositions.Count);
        Assert.Contains(
            fast
                ? "要約: 削除済み 2、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 1、DELETE_FAILED 0、処理対象外 0、未処理 0"
                : "要約: 削除済み 1、MODIFIED 2、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 1、DELETE_FAILED 0、処理対象外 0、未処理 0",
            Lines(stdout));
        Assert.True(File.Exists(Path.Combine(target, "changed.txt")));
    }

    // O16: stdout と stderr を同じ書き込み先にした偽の端末で、進捗の行と結果行が混ざらない。最後に進捗の行は残らない。
    // analyze は Checking n / total、delete は Processing n / total。stderr がリダイレクトされている (ShowProgress = false) ときは出ない。
    [Theory]
    [InlineData("analyze", true)]
    [InlineData("delete", true)]
    [InlineData("analyze", false)]
    [InlineData("delete", false)]
    public void O16_ProgressDoesNotMixWithResultLines(string command, bool showProgress)
    {
        var (zip, target) = Standard();
        var terminal = new StringWriter();
        var environment = Environment(showProgress: showProgress);

        var status = CliApplication.Run(
            command == "delete" ? ["delete", zip, "--target", target, "--yes"] : ["analyze", zip, "--target", target],
            terminal,
            terminal,
            environment);

        Assert.Equal(ExitStatus.Success, status);
        var raw = terminal.ToString();
        var word = command == "delete" ? "Processing" : "Checking";
        Assert.Equal(showProgress, raw.Contains($"{word} 1 / 5", StringComparison.Ordinal));
        Assert.Equal(showProgress, raw.Replace(System.Environment.NewLine, "\n", StringComparison.Ordinal).Contains('\r', StringComparison.Ordinal));
        Assert.DoesNotContain(command == "delete" ? "Checking" : "Processing", raw, StringComparison.Ordinal);

        var screen = Screen(raw);
        Assert.DoesNotContain(screen, l => l.Contains(word, StringComparison.Ordinal));
        var prefix = command == "delete" ? "DELETED " : "MATCHED ";
        Assert.Contains($@"{prefix.PadRight(22)}same.txt -> {target}\same.txt", screen);
        Assert.Contains($@"MODIFIED              size.txt -> {target}\size.txt", screen);
        Assert.Contains($@"MISSING               missing.txt -> {target}\missing.txt", screen);
    }

    // CR で行頭に戻り、LF で次の行へ進む端末を模擬して、最終的に見える行を返す (行末の空白は除く)。
    private static List<string> Screen(string raw)
    {
        var lines = new List<StringBuilder> { new() };
        var column = 0;
        foreach (var c in raw)
        {
            switch (c)
            {
                case '\r':
                    column = 0;
                    break;
                case '\n':
                    lines.Add(new StringBuilder());
                    column = 0;
                    break;
                default:
                    var line = lines[^1];
                    if (column < line.Length)
                    {
                        line[column] = c;
                    }
                    else
                    {
                        line.Append(c);
                    }

                    column++;
                    break;
            }
        }

        return lines.Select(l => l.ToString().TrimEnd()).Where(l => l.Length > 0).ToList();
    }
}
