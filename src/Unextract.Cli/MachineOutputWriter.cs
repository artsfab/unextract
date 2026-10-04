namespace Unextract.Cli;

internal enum MachineOutputDestination { Log, Stdout }
internal enum MachineOutputOperation { Write, Flush, Close }

// 出力例外だけを command/CLI 境界で識別できるようにする。STOP へ変換しない。
internal sealed class MachineOutputException(
    MachineOutputDestination destination, MachineOutputOperation operation, Exception inner)
    : Exception($"{destination} {operation}: {inner.Message}", inner)
{
    public MachineOutputDestination Destination { get; } = destination;
    public MachineOutputOperation Operation { get; } = operation;
}

// stdout は借用、ログは CLI が別途所有する。writer はどちらも close しない。
internal sealed class MachineOutputWriter(Stream stdout, ExecutionLog? log = null)
{
    private bool _stdoutFailed;
    private bool _logFailed;
    private bool _resultAttempted;

    public MachineOutputException? LastFailure { get; private set; }
    public bool HasWritableDestination => !_stdoutFailed || (log is not null && !_logFailed);

    public void WriteRun(MachineRunRecord record)
    {
        CheckContinuing();
        Deliver(MachineOutput.Serialize(record));
    }

    public void WriteEntry(MachineEntryRecord record)
    {
        CheckContinuing();
        Deliver(MachineOutput.Serialize(record));
    }

    // 失敗後の OUTPUT_FAILED もこの唯一の終端口から、失敗していない先だけへ送る。
    // false は終了コード1が必要。終端配送失敗で2個目の result を書かない。
    public bool WriteResult(MachineResultRecord record)
    {
        if (_resultAttempted) throw new InvalidOperationException("result は既に配送を試みています。");
        _resultAttempted = true;
        if (!HasWritableDestination) return false;
        var bytes = MachineOutput.Serialize(record);
        try
        {
            Deliver(bytes);
            return true;
        }
        catch (MachineOutputException)
        {
            return false;
        }
    }

    private void CheckContinuing()
    {
        if (_resultAttempted) throw new InvalidOperationException("result の後には配送できません。");
        if (LastFailure is { } failure) throw failure;
    }

    private void Deliver(byte[] bytes)
    {
        if (log is not null && !_logFailed) Send(log.Stream, MachineOutputDestination.Log, bytes);
        if (!_stdoutFailed) Send(stdout, MachineOutputDestination.Stdout, bytes);
    }

    private void Send(Stream destination, MachineOutputDestination kind, byte[] bytes)
    {
        var operation = MachineOutputOperation.Write;
        try
        {
            destination.Write(bytes);
            operation = MachineOutputOperation.Flush;
            destination.Flush();
        }
        catch (Exception ex)
        {
            if (kind == MachineOutputDestination.Log) _logFailed = true;
            else _stdoutFailed = true;
            LastFailure = new MachineOutputException(kind, operation, ex);
            throw LastFailure;
        }
    }
}
