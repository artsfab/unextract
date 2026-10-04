namespace Unextract.Cli;

internal enum LogCreationFailureKind { AlreadyExists, CreateFailed }
internal sealed record LogCreationFailure(LogCreationFailureKind Kind, string Message);
internal sealed record LogCreationResult(ExecutionLog? Log, LogCreationFailure? Failure);

// CLI 所有のログ。作成判定の根拠は CreateNew の成否で、事前の存在確認はしない。
internal sealed class ExecutionLog(Stream stream) : IDisposable
{
    private bool _disposed;
    public Stream Stream { get; } = stream;

    public static LogCreationResult Create(string path)
    {
        try
        {
            // バッファを無効にし、失敗後の close で未配送の byte 列を再 flush しない。
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1);
            // デバイス (NUL・CON など) やパイプは CreateNew でも開けるが、記録が残らない。
            // Windows の CanSeek はハンドルの種類がディスク (FILE_TYPE_DISK) のときだけ true。
            if (!stream.CanSeek)
            {
                stream.Dispose();
                return new(null, new LogCreationFailure(LogCreationFailureKind.CreateFailed,
                    "ディスク上のファイルではありません (デバイスやパイプには記録が残りません)"));
            }
            return new(new ExecutionLog(stream), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            // Windows の ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS。API の失敗値から分類する。
            var exists = ex is IOException && ex.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7);
            return new(null, new LogCreationFailure(
                exists ? LogCreationFailureKind.AlreadyExists : LogCreationFailureKind.CreateFailed, ex.Message));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Stream.Dispose();
        }
        catch (Exception ex)
        {
            // 終端 result の後でも追加レコードは書かず、CLI は終了コード1にする。
            throw new MachineOutputException(MachineOutputDestination.Log, MachineOutputOperation.Close, ex);
        }
    }
}
