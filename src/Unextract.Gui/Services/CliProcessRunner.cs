using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unextract.Gui.Models;

namespace Unextract.Gui.Services;

internal interface ICliProcessRunner
{
    CliProgress Progress { get; }
    Task<CliJobResult> RunAsync(CliJob job, CancellationToken analysisCancellation = default);
}

internal sealed class CliProcessRunner(CliLocation location) : ICliProcessRunner
{
    internal const int StderrCharacterLimit = 32 * 1024;
    // This adapter is inside the process boundary, solely to exercise OS start/lifetime failures.
    internal Func<ProcessStartInfo, IChildProcess> CreateProcess { get; init; } = info => new ChildProcess(info);
    private CliProgress _progress = new(CliStartState.BeforeStartFailure, null, 0);
    private int _busy;
    public CliProgress Progress => Volatile.Read(ref _progress);

    public Task<CliJobResult> RunAsync(CliJob job, CancellationToken analysisCancellation = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("CLIは実行中、または以前のプロセスの終了を確認できていません。");
        // No process wait, parsing, stream draining or caller code runs on the UI thread.
        return Task.Run(async () =>
        {
            CliJobResult? result = null;
            try
            {
                result = await RunCoreAsync(job, analysisCancellation).ConfigureAwait(false);
                return result;
            }
            finally
            {
                // Unknown start/exit is treated as a potentially live child for this session.
                if (result is not null && (result.ExitConfirmed || result.StartState is CliStartState.BeforeStartFailure or CliStartState.NotStarted))
                    Volatile.Write(ref _busy, 0);
            }
        });
    }

    private async Task<CliJobResult> RunCoreAsync(CliJob job, CancellationToken cancellation)
    {
        var receiver = new JsonlReceiver(job);
        var errors = new ConcurrentQueue<string>();
        var state = CliStartState.BeforeStartFailure;
        Volatile.Write(ref _progress, new(state, null, 0));
        IChildProcess child;
        try
        {
            job.Validate();
            var availability = location.Check();
            if (!availability.IsAvailable) throw new FileNotFoundException(availability.Message, availability.Path);
            if (job.Operation == CliOperation.Analyze && cancellation.IsCancellationRequested)
                return Early(state, true, "解析は起動前にキャンセルされました。");
            child = CreateProcess(StartInfo(location.ExecutablePath, job));
        }
        catch (Exception e) { return Early(state, false, e.Message); }

        using (child)
        {
            try
            {
                if (!child.Start()) return Early(CliStartState.NotStarted, false, "OSはCLIプロセスを開始しませんでした。");
                state = CliStartState.Started;
            }
            catch (Win32Exception e) { return Early(CliStartState.NotStarted, false, e.Message); }
            catch (Exception e)
            {
                // An unexpected exception is not evidence that no process was created.
                state = CliStartState.Unknown;
                errors.Enqueue("CLIの起動成否を確認できません: " + e.Message);
            }
            Volatile.Write(ref _progress, new(state, null, 0));
            if (!child.HasProcess)
                return Early(CliStartState.Unknown, false, string.Join(Environment.NewLine, errors));

            int cancelled = 0;
            // The only termination path is guarded by the immutable Analyze operation.
            using var registration = job.Operation == CliOperation.Analyze
                ? cancellation.Register(() =>
                {
                    Interlocked.Exchange(ref cancelled, 1);
                    try { child.CancelAnalysis(); }
                    catch (Exception e) { errors.Enqueue("解析の終了要求に失敗しました: " + e.Message); }
                })
                : default;
            Task<bool> stdout = DrainOutputAsync();
            Task<(bool Eof, string Text, bool Truncated)> stderr = DrainErrorAsync();
            int? exit = null;
            bool confirmed = false;
            try
            {
                await child.WaitForExitAsync().ConfigureAwait(false);
                exit = child.ExitCode;
                confirmed = true;
            }
            catch (Exception e) { errors.Enqueue("CLIの実終了を確認できません: " + e.Message); }
            bool outputEof = await stdout.ConfigureAwait(false);
            var diagnostic = await stderr.ConfigureAwait(false);
            // A final result is useful only after actual exit and both independent EOFs.
            return new(state, confirmed, exit, outputEof, diagnostic.Eof, Volatile.Read(ref cancelled) != 0,
                receiver.Finish(), diagnostic.Text, diagnostic.Truncated,
                errors.IsEmpty ? null : string.Join(Environment.NewLine, errors));

            async Task<bool> DrainOutputAsync()
            {
                try
                {
                    var buffer = new byte[16 * 1024];
                    Stream stream = child.StandardOutput;
                    while (true)
                    {
                        int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                        if (read == 0) return true;
                        receiver.Feed(buffer.AsSpan(0, read));
                        Volatile.Write(ref _progress, new(state, receiver.Run, receiver.ReceivedEntries));
                    }
                }
                catch (Exception e)
                {
                    receiver.ReadFailed("標準出力を読み切れません: " + e.Message);
                    return false;
                }
            }

            async Task<(bool Eof, string Text, bool Truncated)> DrainErrorAsync()
            {
                var text = new StringBuilder();
                bool truncated = false;
                bool eof = false;
                try
                {
                    using var reader = new StreamReader(child.StandardError, Encoding.UTF8, false, 4096, leaveOpen: true);
                    var buffer = new char[4096];
                    while (true)
                    {
                        int read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                        if (read == 0) { eof = true; break; }
                        int keep = Math.Min(read, StderrCharacterLimit - text.Length);
                        text.Append(buffer, 0, keep);
                        truncated |= keep < read;
                    }
                }
                catch (Exception e) { errors.Enqueue("標準エラー出力を読み切れません: " + e.Message); }
                if (truncated) text.Append("\n[標準エラー出力の続きは省略しました。]");
                return (eof, text.ToString(), truncated);
            }
        }

        CliJobResult Early(CliStartState start, bool cancelled, string message)
        {
            Volatile.Write(ref _progress, new(start, null, 0));
            return new(start, false, null, false, false, cancelled, receiver.Finish(), "", false, message);
        }
    }

    internal static ProcessStartInfo StartInfo(string executable, CliJob job)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add(job.OperationValue);
        info.ArgumentList.Add(job.Archive);
        info.ArgumentList.Add("--target");
        info.ArgumentList.Add(job.Target);
        if (job.Mode == CliMode.Fast) info.ArgumentList.Add("--fast");
        if (job.Operation == CliOperation.Delete)
        {
            info.ArgumentList.Add("--entries");
            info.ArgumentList.Add(job.Entries!);
            info.ArgumentList.Add("--yes");
        }
        info.ArgumentList.Add("--jsonl");
        if (job.Operation == CliOperation.Delete)
        {
            info.ArgumentList.Add("--log");
            info.ArgumentList.Add(job.Log!);
        }
        return info;
    }
}

internal interface IChildProcess : IDisposable
{
    bool Start();
    bool HasProcess { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    Task WaitForExitAsync();
    int ExitCode { get; }
    void CancelAnalysis();
}

internal sealed class ChildProcess(ProcessStartInfo info) : IChildProcess
{
    private readonly Process _process = new() { StartInfo = info };
    public bool Start() => _process.Start();
    public bool HasProcess
    {
        get
        {
            try { _ = _process.Id; return true; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public Stream StandardOutput => _process.StandardOutput.BaseStream;
    public Stream StandardError => _process.StandardError.BaseStream;
    public Task WaitForExitAsync() => _process.WaitForExitAsync();
    public int ExitCode => _process.ExitCode;
    public void CancelAnalysis()
    {
        // No shared Dispose, timeout or application-exit termination behavior.
        if (!_process.HasExited) _process.Kill();
    }
    public void Dispose() => _process.Dispose();
}
