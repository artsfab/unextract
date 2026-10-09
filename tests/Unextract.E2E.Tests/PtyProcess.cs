using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Porta.Pty;

namespace Unextract.E2E.Tests;

// 実端末 (ConPTY) で exe・cmd を動かす (X28〜X31)。改行なしの prompt も検出し、UTF-8 の途中で分割された読み取りも復号する。
// fixture の削除より先に dispose する。fixture はテストの終了後に共通の削除処理 (TestFixtures) が削除する。
internal sealed class PtyProcess : IAsyncDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex Escape = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[ -/]*[@-~])",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Progress = new(
        "\r*(?:Checking|Processing) [0-9]+ / [0-9]+[ \r]*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly IPtyConnection _terminal;
    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly Task _reading;
    private int? _exitCode;
    private Task? _disposeTask;
    private volatile bool _disposed;

    private PtyProcess(IPtyConnection terminal)
    {
        _terminal = terminal;
        _process = Process.GetProcessById(terminal.Pid);
        // 終了後も PID 再利用に影響されず同じプロセスの終了を待つため、起動直後にハンドルを保持する。
        _ = _process.SafeHandle;
        // Porta.Pty 2.2.2 の Windows pipe は同期 I/O。呼び出し元をブロックさせない。
        _reading = Task.Run(ReadAsync);
    }

    public string Output
    {
        get
        {
            lock (_output)
            {
                // VT の装飾を除く。端末幅を十分大きくし、画面の折り返しを避ける。
                return Escape.Replace(_output.ToString(), "").Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }
    }

    // 進捗 (stderr) は同じ端末に描かれ、ConPTY の差分の出力では結果行の前に (消去の空白・CR とともに) 残る。結果行を読む前に除く
    // (見え方は手動の M05)。
    public string OutputWithoutProgress => Progress.Replace(Output, "");

    public string Diagnostics
    {
        get
        {
            lock (_output)
            {
                return $"exe: {UnextractProcess.ExePath}\npid: {_terminal.Pid}\n--- terminal ---\n{Output}" +
                    $"\n--- raw terminal (ESC escaped) ---\n{_output.ToString().Replace("\u001b", "\\x1B", StringComparison.Ordinal)}";
            }
        }
    }

    // 1つの PTY で起動から終了までを行う。本体の失敗には context と terminal output を付ける。PTY / process tree を終了させてから
    // テストを終え (fixture はその後に共通の削除処理が削除する)、後始末だけが失敗した場合もテストを失敗させる。
    public static async Task RunAsync(string context, Func<CancellationToken, Task<PtyProcess>> start, Func<PtyProcess, CancellationToken, Task> body)
    {
        using var timeout = new CancellationTokenSource(UnextractProcess.Timeout);
        PtyProcess? terminal = null;
        string? failure = null;
        try
        {
            terminal = await start(timeout.Token);
            await body(terminal, timeout.Token);
        }
        catch (Exception e)
        {
            failure = $"{e}\n{context}\n" + (terminal?.Diagnostics ?? "PTY 未起動。");
        }
        finally
        {
            try
            {
                if (terminal is not null)
                {
                    await terminal.DisposeAsync();
                }
            }
            catch (Exception e)
            {
                // 本体の失敗を保持する。
                failure += $"\nPTY 後始末失敗: {e}\n{context}\n{terminal?.Diagnostics}";
            }
        }

        if (failure is not null)
        {
            Assert.Fail(failure);
        }
    }

    // 対話の delete。実削除を伴い得るので、起動前に領域外ガードを確かめる。
    public static Task<PtyProcess> StartDeleteAsync(E2EFixture fixture, bool fast, CancellationToken token)
    {
        fixture.CheckGuard();
        return SpawnAsync(UnextractProcess.ExePath, fixture.Directory,
            ["delete", fixture.ArchivePath, "--target", fixture.Target, .. fast ? new[] { "--fast" } : []], verbatim: false, token);
    }

    // cmd.exe /d /s /c "<command>" (コードページの切り替えなどを同じコンソールで続けて行う)。command は引用符を含めてそのまま渡す。
    public static Task<PtyProcess> StartCmdAsync(string directory, string command, CancellationToken token) =>
        SpawnAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), directory, ["/d", "/s", "/c", $"\"{command}\""], verbatim: true, token);

    private static async Task<PtyProcess> SpawnAsync(string app, string directory, string[] commandLine, bool verbatim, CancellationToken token)
    {
        var terminal = await PtyProvider.SpawnAsync(new PtyOptions
        {
            App = app,
            Cwd = directory,
            CommandLine = commandLine,
            VerbatimCommandLine = verbatim,
            Cols = 240,
            Rows = 80,
        }, token);
        return new PtyProcess(terminal);
    }

    public async Task WaitForAsync(string text, CancellationToken token)
    {
        while (!Output.Contains(text, StringComparison.Ordinal))
        {
            if (_process.HasExited)
            {
                // 終了通知と reader の競合を避ける。終了直前の出力も回収してから判定する。
                await DisposeAsync();
                if (Output.Contains(text, StringComparison.Ordinal))
                {
                    return;
                }

                throw new InvalidOperationException($"PTY が {text} を出さずに終了した。");
            }

            if (_reading.IsCompleted)
            {
                await _reading;
                if (Output.Contains(text, StringComparison.Ordinal))
                {
                    return;
                }

                throw new InvalidOperationException($"PTY が {text} を出さずに出力を終了した。");
            }

            await Task.Delay(20, token);
        }
    }

    // 1行を入力して Enter を押す (空文字列は Enter だけ)。delete への y は StartDeleteAsync の領域外ガードの後に限る。
    public async Task SendLineAsync(string text, CancellationToken token)
    {
        await _terminal.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(text + "\r"), token);
        await _terminal.WriterStream.FlushAsync(token);
    }

    public async Task<int> WaitForExitAsync(CancellationToken token)
    {
        if (_disposed)
        {
            return _exitCode ?? throw new InvalidOperationException("PTY の後始末で終了コードを取得できなかった。");
        }

        await _process.WaitForExitAsync(token);
        var exitCode = _terminal.ExitCode;
        // プロセス終了後に ConPTY を閉じ、パイプを EOF にして残りの出力を回収する。
        await DisposeAsync();
        return exitCode;
    }

    private async Task ReadAsync()
    {
        using var reader = new StreamReader(_terminal.ReaderStream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[2048];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer)) != 0)
            {
                lock (_output)
                {
                    _output.Append(buffer, 0, count);
                }
            }
        }
        catch (Exception e) when (_disposed && e is IOException or ObjectDisposedException)
        {
            // ConPTY の close により pending read が解除される。
        }
    }

    // 初回の後始末の失敗も保持する。再呼び出しを成功扱いにして fixture 削除へ進ませない。
    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (_process.HasExited)
                {
                    // 終了確認と kill の間に終了した。
                }

                await _process.WaitForExitAsync().WaitAsync(CleanupTimeout);
            }

            _exitCode = _terminal.ExitCode;
        }
        finally
        {
            // Porta.Pty の job object (KILL_ON_JOB_CLOSE) は残る子プロセスも終了させる。
            // close 中も reader を動かし続け、ConPTY の出力パイプを詰まらせない。
            try
            {
                await Task.Run(_terminal.Dispose).WaitAsync(CleanupTimeout);
                await _reading.WaitAsync(CleanupTimeout);
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
