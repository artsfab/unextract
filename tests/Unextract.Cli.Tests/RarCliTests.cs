using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Unextract.Core.Commands;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;
using Unextract.Windows;
using Unextract.Windows.Rar;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.Cli.Tests;

// RAR の CLI の組立て (docs/spec/rar.md#pinning、docs/spec/cli.md#input-errors)。DLL は使わない (開く関数を差し替えるか、呼ばれないことを確かめる)。
public class RarCliTests
{
    private const string Library = @"C:\app\UnRAR64.dll";

    private static readonly string Guide = $"。{Library} に UnRAR.dll 7.23 (x64) の UnRAR64.dll を置いてください。ZIP の処理には影響しません。";

    // 製品の環境は常に RAR を開く関数 (読み込み元を固定した RarArchives.Open) を渡す。
    [Fact]
    public void ProductEnvironment_PassesFixedRarOpener()
    {
        Func<string, Limits, ZipOpenResult> expected = RarArchives.Open;
        Assert.Equal(expected, CliEnvironment.Windows().OpenRarArchive);
    }

    // DLL を利用できない RAR は Prepare の FATAL (削除0件)。target・entries に触れない (target が無くても DLL の FATAL)。
    [Theory]
    [InlineData("analyze")]
    [InlineData("delete")]
    public void RarLibraryUnavailable_HumanOutput(string command)
    {
        var dir = NewDirectory();
        var entries = Path.Combine(dir, "entries.txt");
        File.WriteAllBytes(entries, [0xFF, 0xFE, 0, 0]);
        var probe = new UntouchedProbe();
        string[] args = command == "delete"
            ? ["delete", Path.Combine(dir, "a.rar"), "--target", Path.Combine(dir, "missing"), "--entries", entries, "--yes"]
            : ["analyze", Path.Combine(dir, "a.rar"), "--target", Path.Combine(dir, "missing")];

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var status = CliApplication.Run(args, stdout, stderr, Environment(probe, Unavailable));

        Assert.Equal(ExitStatus.Error, status);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(
            [$"FATAL: UnRAR.dll を使用できないため、RAR を処理できません (見つかりません){Guide}", command == "delete" ? DeleteOutput.PrepareAborted : AnalyzeOutput.FatalClosing],
            Lines(stderr.ToString()));
        Assert.Equal(0, probe.Calls);
    }

    // 機械出力: stage=prepare、code=RAR_LIBRARY_UNAVAILABLE、message は人間向けと同じ説明。run を出さず counts を省略する。
    [Theory]
    [InlineData("analyze")]
    [InlineData("delete")]
    public void RarLibraryUnavailable_MachineOutput(string command)
    {
        var dir = NewDirectory();
        string[] args = command == "delete"
            ? ["delete", Path.Combine(dir, "a.rar"), "--target", dir, "--jsonl", "--yes"]
            : ["analyze", Path.Combine(dir, "a.rar"), "--target", dir, "--jsonl"];
        using var stdout = new MemoryStream();

        var status = CliApplication.RunMachine(args, stdout, new StringWriter(), Environment(new UntouchedProbe(), Unavailable));

        Assert.Equal(ExitStatus.Error, status);
        var line = Assert.Single(Encoding.ASCII.GetString(stdout.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal("result", root.GetProperty("type").GetString());
        Assert.Equal("fatal", root.GetProperty("outcome").GetString());
        Assert.False(root.TryGetProperty("counts", out _));
        var error = root.GetProperty("error");
        Assert.Equal("prepare", error.GetProperty("stage").GetString());
        Assert.Equal("RAR_LIBRARY_UNAVAILABLE", error.GetProperty("code").GetString());
        Assert.Equal($"UnRAR.dll を使用できないため、RAR を処理できません (見つかりません){Guide}", error.GetProperty("message").GetString());
    }

    // ZIP の実行は RAR を開く関数を呼ばない (DLL を探さない)。製品の環境 (読み込み元に DLL が無い) でも ZIP は成功する。
    [Fact]
    public void ZipRun_NeverCallsRarOpener()
    {
        var dir = NewDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var zip = Path.Combine(dir, "archive.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("a.txt").Open());
            writer.Write("hello");
        }

        File.WriteAllText(Path.Combine(target, "a.txt"), "hello");
        var probe = new WindowsFileSystemProbe();
        var environment = new CliEnvironment(probe, probe, ProtectedLocations.Resolve, new NoPrompt(), false,
            (_, _) => throw new InvalidOperationException("ZIP の実行で RAR を開く関数が呼ばれた"));

        var status = CliApplication.Run(["analyze", zip, "--target", target], new StringWriter(), new StringWriter(), environment);
        Assert.Equal(ExitStatus.Success, status);

        var product = CliEnvironment.Windows() with { Prompt = new NoPrompt(), ShowProgress = false };
        Assert.Equal(ExitStatus.Success, CliApplication.Run(["analyze", zip, "--target", target], new StringWriter(), new StringWriter(), product));
    }

    private static ZipOpenResult Unavailable(string path, Limits limits) =>
        new(null, FatalError.RarLibraryUnavailable(RarLibraryFailure.NotFound, Library));

    private static CliEnvironment Environment(IFileSystemProbe probe, Func<string, Limits, ZipOpenResult> open) =>
        new(probe, new UntouchedDeletion(), () => throw new InvalidOperationException("拒否対象の場所を解決した"), new NoPrompt(), false, open);

    private static string[] Lines(string text) => text.Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static string NewDirectory() => TestFixtures.Create();

    private sealed class NoPrompt : IConfirmationPrompt
    {
        public bool IsInteractive => true;

        public string? Ask(string prompt) => throw new InvalidOperationException("確認を求めた");
    }

    // target に触れたら数える probe (呼ばれないことの確認用)。
    private sealed class UntouchedProbe : IFileSystemProbe
    {
        public int Calls { get; private set; }

        public ProbeResult<TargetConfirmation> ConfirmTargetFinalComponent(string path) => Touch<TargetConfirmation>();

        public ProbeResult<IDirectoryHandle> OpenTargetRoot(string path) => Touch<IDirectoryHandle>();

        public ProbeResult<IDirectoryHandle> OpenDirectoryForEnumeration(string path) => Touch<IDirectoryHandle>();

        public ProbeResult<IComparisonHandle> OpenForComparison(string path) => Touch<IComparisonHandle>();

        public ProbeResult<Core.Target.VolumeFileId> GetFileIdentity(string path) => Touch<Core.Target.VolumeFileId>();

        private ProbeResult<T> Touch<T>()
        {
            Calls++;
            return ProbeResult<T>.Fail(5, "touched");
        }
    }

    private sealed class UntouchedDeletion : IDeletionProbe
    {
        public ProbeResult<IDeletionHandle> OpenForDeletion(string path) => throw new InvalidOperationException("削除用に開いた");

        public ProbeResult<IdentityCheckInfo> CheckIdentity(string path) => throw new InvalidOperationException("識別確認をした");
    }
}
