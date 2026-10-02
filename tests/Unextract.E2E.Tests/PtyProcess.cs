using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Porta.Pty;

namespace Unextract.E2E.Tests;

// M08 / O07 専用。改行なしの prompt も検出し、UTF-8 の途中で分割された読み取りも復号する。
// fixture の削除より先に dispose する。fixture の cleanup は呼び出し元の PTY テストが行う。
internal sealed class PtyProcess : IAsyncDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex Escape = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[ -/]*[@-~])",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
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

    public static async Task<PtyProcess> StartAsync(E2EFixture fixture, bool fast, CancellationToken token)
    {
        var terminal = await PtyProvider.SpawnAsync(new PtyOptions
        {
            App = UnextractProcess.ExePath,
            Cwd = fixture.Directory,
            CommandLine = [fixture.ArchivePath, "--target", fixture.Target, .. fast ? new[] { "--fast" } : []],
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

    // このテストでは n のみ送る。実削除を伴う y は既存 E2E の領域外ガードなしで送らない。
    public async Task SendNoAsync(CancellationToken token)
    {
        await _terminal.WriterStream.WriteAsync("n\r"u8.ToArray(), token);
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
