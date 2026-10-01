using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Unextract.Windows.Tests.Helper;

// 別プロセスのヘルパー (テスト専用)。このテストアセンブリのビルド済み実行ファイル (Unextract.Windows.Tests.exe) として
// Process.Start で起動し、他のプログラムによるファイルの使用を模擬する。テストの実行 (testhost) からは呼ばれない。
// 標準出力に結果を1行書く。hold 系は "READY" を書いた後、標準入力から1行 (または EOF) を受けるまでハンドルを保持する。
internal static class HelperProgram
{
    // FILE_INFO_BY_HANDLE_CLASS の FileDispositionInfo (従来の削除保留。POSIX semantics ではない)。
    private const int FileDispositionInfo = 4;

    public static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["hold-write", var path] => Hold(new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)),
                ["hold-read-share-read", var path] => Hold(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)),
                ["hold-delete-pending", var fixture, var path] => HoldDeletePending(fixture, path),
                ["try-write", var path] => Try(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose()),
                ["try-rename", var path] => Try(() => File.Move(path, path + ".renamed")),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"ERROR {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Hold(IDisposable resource)
    {
        using (resource)
        {
            Console.Out.WriteLine($"READY {Environment.ProcessId}");
            Console.Out.Flush();
            Console.In.ReadLine();
        }

        return 0;
    }

    // 別プロセスが削除を指示してハンドルを保持している状態 (削除保留中、PoC 7 #7)。ヘルパーの終了で対象は消える。
    // 対象は fixture 内のファイルだけ: 指示の前にテスト側のガードで最終パスと親を確かめる。
    private static unsafe int HoldDeletePending(string fixture, string path)
    {
        var guard = new DeletionGuard(fixture);
        var spec = new HandleSpec(
            HandleSpecs.Delete | HandleSpecs.FileReadAttributes | HandleSpecs.Synchronize,
            HandleSpecs.FileShareRead | HandleSpecs.FileShareWrite | 0x4, // 0x4 = FILE_SHARE_DELETE
            HandleSpecs.FileFlagOpenReparsePoint);
        var opened = HandleOpener.Open(path, spec);
        if (!opened.Succeeded)
        {
            Console.Out.WriteLine($"ERROR open {opened.Error}");
            return 1;
        }

        using var handle = opened.Value;
        var finalPath = FileInformation.GetFinalPath(handle);
        guard.Check(finalPath.Value ?? throw new GuardViolationException("最終パスを取得できない"));

        byte deleteFile = 1;
        if (!TestKernel32.SetFileInformationByHandle(handle, FileDispositionInfo, &deleteFile, 1))
        {
            Console.Out.WriteLine($"ERROR disposition {Marshal.GetLastPInvokeError()}");
            return 1;
        }

        return Hold(new NoopDisposable());
    }

    // 操作を試し、成功なら 0、失敗なら Win32 エラーコードを書く。
    private static int Try(Action action)
    {
        try
        {
            action();
            Console.Out.WriteLine("0");
        }
        catch (IOException ex)
        {
            Console.Out.WriteLine((ex.HResult & 0xFFFF).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (UnauthorizedAccessException)
        {
            Console.Out.WriteLine("5");
        }

        return 0;
    }

    private static int Usage()
    {
        Console.Out.WriteLine("ERROR usage");
        return 2;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

// テストからヘルパーを起動する。Hold の戻り値を Dispose すると、ヘルパーにハンドルを閉じさせて終了を待つ。
internal sealed class HelperProcess : IDisposable
{
    private readonly Process _process;

    private HelperProcess(Process process, string firstLine)
    {
        _process = process;
        FirstLine = firstLine;
    }

    public string FirstLine { get; }

    public int ProcessId => _process.Id;

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "Unextract.Windows.Tests.exe");

    // ハンドルを保持させる。READY を受け取るまで待つ。
    public static HelperProcess Hold(params string[] args)
    {
        var process = Start(args);
        var line = process.StandardOutput.ReadLine() ?? string.Empty;
        Assert.True(line.StartsWith("READY ", StringComparison.Ordinal), $"helper did not become ready: {line}");
        Assert.NotEqual(Environment.ProcessId, process.Id);
        return new HelperProcess(process, line);
    }

    // 操作を1回試して、結果の Win32 エラーコード (成功は 0) を返す。
    public static int Try(params string[] args)
    {
        using var process = Start(args);
        var line = process.StandardOutput.ReadLine() ?? string.Empty;
        process.WaitForExit();
        Assert.True(int.TryParse(line, out var code), $"helper failed: {line}");
        return code;
    }

    public void Dispose()
    {
        _process.StandardInput.WriteLine("release");
        _process.StandardInput.Close();
        if (!_process.WaitForExit(30_000))
        {
            _process.Kill();
        }

        _process.Dispose();
    }

    private static Process Start(string[] args)
    {
        Assert.True(File.Exists(ExecutablePath), $"helper executable not found: {ExecutablePath}");
        var info = new ProcessStartInfo(ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("helper did not start");
    }
}
