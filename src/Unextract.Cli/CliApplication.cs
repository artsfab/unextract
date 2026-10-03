using System.Text;
using Unextract.Core.Analysis;
using Unextract.Core.CommandLine;
using Unextract.Core.Commands;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;
using Unextract.Windows;

namespace Unextract.Cli;

// CLI が使う外部とのつながり。テストでは偽の probe などに差し替える。
internal sealed record CliEnvironment(
    IFileSystemProbe Probe,
    IDeletionProbe DeletionProbe,
    Func<ProtectedLocationsResult> ResolveProtectedLocations,
    IConfirmationPrompt Prompt,
    bool ShowProgress)
{
    public static CliEnvironment Windows()
    {
        var probe = new WindowsFileSystemProbe();
        return new(probe, probe, ProtectedLocations.Resolve, new ConsolePrompt(), !Console.IsErrorRedirected);
    }
}

// unextract analyze <archive.zip> --target <dir> [--fast]
// unextract delete  <archive.zip> --target <dir> [--fast] [--entries <file>] [--yes|-y]   (SPEC §2、§3)
// 出力先 (SPEC §10.4): 結果行・ヘッダー・合計・要約は stdout、入力エラー・FATAL・STOP の原因・内部エラーと進捗は stderr。
internal static class CliApplication
{
    public static ExitStatus Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment? environment = null)
    {
        var deletionStarted = false;
        try
        {
            return RunCore(args, stdout, stderr, environment ?? CliEnvironment.Windows(), () => deletionStarted = true);
        }
        catch (Exception ex)
        {
            // delete 開始後の例外報告や進捗の Dispose 自体も失敗し得るため、開始通知で削除0件の境界を区別する。
            stderr.WriteLine();
            stderr.WriteLine($"内部エラー: 想定外の例外が発生しました ({ex.GetType().Name}: {SafeDisplay.Escape(ex.Message)})");
            stderr.WriteLine(deletionStarted
                ? "逐次処理開始後に中止しました。削除が行われた可能性があります。削除したファイルは元に戻りません。"
                : "中止しました。削除0件。");
            return ExitStatus.Error;
        }
    }

    private static ExitStatus RunCore(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment, Action deletionStarting)
    {
        var parsed = CommandLineParser.Parse(args);
        if (parsed.Options is not { } options)
        {
            stderr.WriteLine($"入力エラー: {parsed.Error}");
            foreach (var line in CommandLineParser.UsageLines)
            {
                stderr.WriteLine(line);
            }

            return ExitStatus.Error;
        }

        var analyze = options.Command == CommandKind.Analyze;
        using var progress = environment.ShowProgress
            ? new ProgressLine(stderr, analyze ? ReportText.CheckingProgress : ReportText.ProcessingProgress)
            : null;

        // 進捗を表示する場合、stdout・stderr への書き込みの前に進捗の行を消す (同じ端末で結果行が進捗の行に続かないようにする)。
        var output = progress is null ? stdout : new ProgressAwareWriter(stdout, progress);
        var error = progress is null ? stderr : new ProgressAwareWriter(stderr, progress);
        var context = new CommandContext(
            environment.Probe,
            () =>
            {
                var locations = environment.ResolveProtectedLocations();
                return new TargetLocationPolicyResult(locations.Policy, locations.Error);
            },
            Limits.Default,
            output,
            error,
            progress is null ? null : progress.Report);

        return analyze
            ? AnalyzeCommand.Run(new AnalyzeCommandRequest(options.ArchivePath, options.TargetPath, options.Mode, context)).Status
            : DeleteCommand.Run(new DeleteCommandRequest(
                options.ArchivePath,
                options.TargetPath,
                options.Mode,
                options.EntriesPath,
                options.AssumeYes,
                environment.DeletionProbe,
                environment.Prompt,
                context,
                DeletionStarting: deletionStarting)).Status;
    }
}

// 進捗 (Checking n / total、Processing n / total) を stderr の1行に CR で上書き表示する (PLAN.md §5.6)。端末への出力が多くなりすぎない
// よう、最初・100 件ごと・最後だけ新しい件数を書く。結果行などを書く前に Clear で行を消し、次の Report で直前の表示を書き直す。
internal sealed class ProgressLine(TextWriter writer, Func<int, int, string> format) : IDisposable
{
    private const int Interval = 100;
    private const char CarriageReturn = '\r';
    private string? _last;
    private int _shownLength;

    public void Report(int current, int total)
    {
        if (current == 1 || current == total || current % Interval == 0)
        {
            _last = format(current, total);
            Show();
        }
        else if (_shownLength == 0 && _last is not null)
        {
            Show();
        }
    }

    // 進捗の行が表示されていれば、空白で上書きして行頭に戻す。
    public void Clear()
    {
        if (_shownLength == 0)
        {
            return;
        }

        writer.Write(CarriageReturn + new string(' ', _shownLength) + CarriageReturn);
        _shownLength = 0;
    }

    public void Dispose() => Clear();

    private void Show()
    {
        var text = _last!;
        var padding = _shownLength > text.Length ? new string(' ', _shownLength - text.Length) : string.Empty;
        writer.Write(CarriageReturn + text + padding);
        _shownLength = Math.Max(text.Length, _shownLength);
    }
}

// 書き込みの前に進捗の行を消す TextWriter。
internal sealed class ProgressAwareWriter(TextWriter inner, ProgressLine progress) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public override IFormatProvider FormatProvider => inner.FormatProvider;

    public override void Write(char value)
    {
        progress.Clear();
        inner.Write(value);
    }

    public override void Write(string? value)
    {
        progress.Clear();
        inner.Write(value);
    }

    public override void WriteLine()
    {
        progress.Clear();
        inner.WriteLine();
    }

    public override void WriteLine(string? value)
    {
        progress.Clear();
        inner.WriteLine(value);
    }

    public override void Flush() => inner.Flush();
}

// 確認プロンプト (SPEC §2、§3.2)。
internal sealed class ConsolePrompt : IConfirmationPrompt
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public string? Ask(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }
}
