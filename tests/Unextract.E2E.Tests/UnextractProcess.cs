using System.Diagnostics;
using System.Text;

namespace Unextract.E2E.Tests;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string[] OutputLines => SplitLines(StandardOutput);

    public string[] ErrorLines => SplitLines(StandardError);

    // 失敗メッセージ用。
    public override string ToString() =>
        $"exit={ExitCode}\n--- stdout ---\n{StandardOutput}--- stderr ---\n{StandardError}";

    private static string[] SplitLines(string text) =>
        text.Length == 0 ? [] : text.TrimEnd('\r', '\n').Split(["\r\n", "\n"], StringSplitOptions.None);
}

// unextract.exe を別プロセスとして起動する。stdin・stdout・stderr は常にリダイレクトする (stdin がリダイレクトされているため、
// exe からは常に非対話に見える)。stdout・stderr は UTF-8 で読む。タイムアウトを超えたらプロセスツリーを kill して失敗にする
// (確認プロンプトで待ち続けて CI が止まるのを防ぐ)。
public static class UnextractProcess
{
    public const string ExeVariable = "UNEXTRACT_E2E_EXE";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly Lazy<string> Exe = new(ResolveExe);

    public static string ExePath => Exe.Value;

    // stdin: null なら何も書かずに閉じる (空の入力・EOF)。
    public static ProcessResult Run(string workingDirectory, string? stdin, params string[] args) => RunExe(ExePath, workingDirectory, stdin, args);

    // exe を指定して実行する (UnRAR64.dll を隣に置いた複製の exe など。RarE2ETests)。
    public static ProcessResult RunExe(string exe, string workingDirectory, string? stdin, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"起動できない: {exe}");

        // 出力の読み取りを先に始める (パイプの詰まりで子が止まらないように)。
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // 子が入力を読まずに終了した (パイプが閉じられた)。終了コードと出力で判定する。
        }

        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail($"unextract.exe が {Timeout.TotalSeconds} 秒以内に終了しなかったため kill した: {string.Join(' ', args)}");
        }

        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    // 1. 環境変数 UNEXTRACT_E2E_EXE。2. テストアセンブリの位置 (tests/Unextract.E2E.Tests/bin/<構成>/<TFM>/) から相対で、
    // Unextract.Cli のビルド出力 (src/Unextract.Cli/bin/<構成>/<TFM>/unextract.exe)。見つからなければ失敗にする
    // (成功・前提不成立にはしない)。
    private static string ResolveExe()
    {
        var fromVariable = Environment.GetEnvironmentVariable(ExeVariable);
        if (!string.IsNullOrEmpty(fromVariable))
        {
            var full = Path.GetFullPath(fromVariable);
            return File.Exists(full)
                ? full
                : throw new InvalidOperationException($"{ExeVariable} が指す exe が存在しない: {full}");
        }

        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = baseDirectory.Name;
        var configuration = baseDirectory.Parent?.Name
            ?? throw new InvalidOperationException($"テストアセンブリの位置から構成を求められない: {baseDirectory.FullName}");
        var candidate = Path.GetFullPath(Path.Combine(
            baseDirectory.FullName, "..", "..", "..", "..", "..", "src", "Unextract.Cli", "bin", configuration, framework, "unextract.exe"));
        return File.Exists(candidate)
            ? candidate
            : throw new InvalidOperationException(
                $"unextract.exe が見つからない: {candidate} (Unextract.Cli を同じ構成でビルドするか、{ExeVariable} で exe を指定する)");
    }
}
