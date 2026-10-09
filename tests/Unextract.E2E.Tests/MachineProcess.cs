using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Unextract.E2E.Tests;

// JSON の byte/LF 契約を確認する入口。既存の人間向け ProcessResult/Report は変えない。
internal sealed record MachineProcessResult(int ExitCode, byte[] Output, string Error)
{
    public JsonElement[] Records => Parse(Output);

    public static JsonElement[] Parse(byte[] bytes)
    {
        Assert.NotEmpty(bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.All(bytes, value => Assert.True(value == '\n' || value is >= 0x20 and <= 0x7E));
        return Encoding.ASCII.GetString(bytes).Split('\n')[..^1].Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement.Clone();
            Assert.Equal(1, record.GetProperty("v").GetInt32());
            return record;
        }).ToArray();
    }

    public override string ToString() => $"exit={ExitCode}\n{Encoding.ASCII.GetString(Output)}\nstderr={Error}";
}

// stdin は開いたままにし、機械モードが EOF/確認入力を待たずに終了することも確認する。
// 失敗時は自分が起動した process tree だけを止める。fixture はテストの終了後に共通の削除処理が削除する。
internal sealed class MachineProcess : IDisposable
{
    private readonly Process _process;
    private readonly Task<string> _error;

    public MachineProcess(E2EFixture fixture, string operation, params string[] options)
    {
        if (operation == "delete")
        {
            fixture.CheckGuard();
        }

        var info = new ProcessStartInfo(UnextractProcess.ExePath)
        {
            WorkingDirectory = fixture.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { operation, fixture.ArchivePath, "--target", fixture.Target, "--jsonl" }.Concat(options))
        {
            info.ArgumentList.Add(argument);
        }

        _process = Process.Start(info) ?? throw new InvalidOperationException("unextract.exe を起動できない");
        _error = _process.StandardError.ReadToEndAsync();
    }

    public Stream Output => _process.StandardOutput.BaseStream;

    public bool HasExited => _process.HasExited;

    public async Task<byte[]> ReadLine()
    {
        using var timeout = new CancellationTokenSource(UnextractProcess.Timeout);
        using var line = new MemoryStream();
        var value = new byte[1];
        while (await Output.ReadAsync(value, timeout.Token) != 0)
        {
            line.WriteByte(value[0]);
            if (value[0] == '\n')
            {
                return line.ToArray();
            }
        }

        Assert.Fail("同期点の LF を受け取る前に stdout が閉じた");
        return [];
    }

    public async Task<MachineProcessResult> Complete(byte[]? prefix = null)
    {
        using var output = new MemoryStream();
        if (prefix is not null)
        {
            output.Write(prefix);
        }

        using var timeout = new CancellationTokenSource(UnextractProcess.Timeout);
        await Output.CopyToAsync(output, timeout.Token);
        var (exit, error) = await Wait();
        return new MachineProcessResult(exit, output.ToArray(), error);
    }

    public async Task<(int Exit, string Error)> Wait()
    {
        using var timeout = new CancellationTokenSource(UnextractProcess.Timeout);
        await _process.WaitForExitAsync(timeout.Token);
        return (_process.ExitCode, await _error);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }

        _process.Dispose();
    }
}
