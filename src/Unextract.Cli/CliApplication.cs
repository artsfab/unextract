using Unextract.Core.Analysis;
using Unextract.Core.CommandLine;
using Unextract.Core.Deletion;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;
using Unextract.Windows;

namespace Unextract.Cli;

// CLI が使う外部とのつながり。テストでは偽の probe などに差し替える。
internal sealed record CliEnvironment(
    IFileSystemProbe Probe,
    Func<ProtectedLocationsResult> ResolveProtectedLocations,
    IConfirmationPrompt Prompt,
    bool ShowProgress,
    IDeletionPhase DeletionPhase)
{
    public static CliEnvironment Windows()
    {
        var probe = new WindowsFileSystemProbe();
        return new(probe, ProtectedLocations.Resolve, new ConsolePrompt(), !Console.IsErrorRedirected, new DeletionPhase(probe));
    }
}

// unextract <archive.zip> --target <dir> [--dry-run] [--fast] [--yes|-y] (SPEC §2、§3)。
// 出力先 (SPEC §10): 解析結果の一覧と削除フェーズの結果は stdout、入力エラー・FATAL・停止の原因・内部エラーと進捗は stderr。
internal static class CliApplication
{
    public static ExitStatus Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment? environment = null)
    {
        try
        {
            return RunCore(args, stdout, stderr, environment ?? CliEnvironment.Windows());
        }
        catch (Exception ex)
        {
            // probe や ZIP から投げられた想定外の例外。削除フェーズは対象ごとに例外を停止として扱い結果を表示するため、
            // ここに来るのは削除フェーズの外 (解析まで) の例外で、削除は起きていない。
            stderr.WriteLine();
            stderr.WriteLine($"内部エラー: 想定外の例外が発生しました ({ex.GetType().Name}: {SafeDisplay.Escape(ex.Message)})");
            stderr.WriteLine("中止しました。削除0件。");
            return ExitStatus.Error;
        }
    }

    private static ExitStatus RunCore(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        var parsed = CommandLineParser.Parse(args);
        if (parsed.Options is not { } options)
        {
            stderr.WriteLine($"入力エラー: {parsed.Error}");
            stderr.WriteLine(CommandLineParser.Usage);
            return ExitStatus.Error;
        }

        // §3 の手順1: ZIP を FileShare.Read で開いて実行終了まで保持する。
        var opened = ZipArchiveSource.Open(options.ArchivePath);
        if (opened.Source is not { } source)
        {
            ReportFatal(stderr, opened.Fatal!);
            return ExitStatus.Error;
        }

        using (source)
        {
            // §3 の手順1: 拒否対象の実パス。解決できなければ安全を確認できないため入力エラー。
            var locations = environment.ResolveProtectedLocations();
            if (locations.Policy is not { } policy)
            {
                stderr.WriteLine($"入力エラー: {locations.Error!.Describe()}");
                stderr.WriteLine("削除開始前に中止しました。削除0件。");
                return ExitStatus.Error;
            }

            using var progress = environment.ShowProgress ? new ProgressLine(stderr, AnalysisReport.CheckingProgress) : null;
            using var deleting = environment.ShowProgress ? new ProgressLine(stderr, AnalysisReport.DeletingProgress) : null;
            var outcome = UnextractRunner.Run(new RunRequest(
                source,
                options.ArchivePath,
                options.TargetPath,
                options.DryRun,
                options.AssumeYes,
                environment.Probe,
                policy,
                Limits.Default,
                environment.Prompt,
                environment.DeletionPhase,
                stdout,
                stderr,
                progress is null ? null : progress.Report,
                deleting is null ? null : deleting.Report,
                Mode: options.Mode));
            return outcome.Status;
        }
    }

    private static void ReportFatal(TextWriter stderr, FatalError fatal)
    {
        stderr.WriteLine($"FATAL: {fatal.Describe()}");
        stderr.WriteLine("削除開始前に中止しました。削除0件。");
    }
}

// 進捗 (Checking n / total、Deleting n / total) を1行で上書き表示する。端末への出力が多くなりすぎないよう、最初・一定件数ごと・最後だけ書く。
internal sealed class ProgressLine(TextWriter writer, Func<int, int, string> format) : IDisposable
{
    private const int Interval = 100;
    private const char CarriageReturn = '\r';
    private bool _written;

    public void Report(int current, int total)
    {
        if (current != 1 && current != total && current % Interval != 0)
        {
            return;
        }

        writer.Write(CarriageReturn + format(current, total));
        _written = true;

        // 最後の件で行を終える (続く結果表示が進捗の行に続かないようにする)。途中で終わった場合は Dispose で終える。
        if (current == total)
        {
            writer.WriteLine();
            _written = false;
        }
    }

    public void Dispose()
    {
        if (_written)
        {
            writer.WriteLine();
        }
    }
}

// 確認プロンプト (SPEC §2)。
internal sealed class ConsolePrompt : IConfirmationPrompt
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public string? Ask(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }
}
