using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Unextract.Windows;

// 列挙で返る1項目。名前はロング名 (8.3 の短い名前は項目として返らない)。
public readonly record struct DirectoryEntry(string Name, uint Attributes, uint ReparseTag, FileId128 FileId);

public enum DirectoryEnumerationStepKind
{
    Entry,
    End,
    Failed,
}

public readonly record struct DirectoryEnumerationStep(DirectoryEnumerationStepKind Kind, DirectoryEntry Entry, int Error)
{
    internal static DirectoryEnumerationStep Item(DirectoryEntry entry) => new(DirectoryEnumerationStepKind.Entry, entry, 0);

    internal static DirectoryEnumerationStep EndOfDirectory { get; } = new(DirectoryEnumerationStepKind.End, default, 0);

    internal static DirectoryEnumerationStep Fail(int error) => new(DirectoryEnumerationStepKind.Failed, default, error);
}

// GetFileInformationByHandleEx(FileIdExtdDirectoryRestartInfo → FileIdExtdDirectoryInfo) による列挙 (SPEC §6.2 の 1・2・7)。
// 項目を1件ずつ返すだけで、照合・保持の規則 (§6.2 の 3〜6) は持たない。"." と ".." は返さない。
// 終端は ERROR_NO_MORE_FILES (18) だけで、それ以外のエラーは Failed を返す。Failed または End の後は同じ結果を返し続ける。
// ハンドルは呼び出し側が所有し、このクラスは閉じない。
public sealed unsafe class DirectoryEnumerator
{
    // 1回の呼び出しで受け取るバッファの大きさ (バイト)。
    public const int BufferSize = 64 * 1024;

    private const string Operation = "GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)";

    private readonly SafeFileHandle _handle;
    private readonly byte[] _buffer = new byte[BufferSize];
    private bool _started;
    private int _offset = -1;
    private DirectoryEnumerationStep? _terminal;

    public DirectoryEnumerator(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
    }

    public DirectoryEnumerationStep Next()
    {
        while (true)
        {
            if (_terminal is { } terminal)
            {
                return terminal;
            }

            if (_offset < 0 && !Fill())
            {
                continue;
            }

            var step = ReadAt();
            if (step is { } item)
            {
                return item;
            }
        }
    }

    // バッファを読み込む。終端・失敗なら _terminal を設定して false。
    private bool Fill()
    {
        var infoClass = _started
            ? FileInfoByHandleClass.FileIdExtdDirectoryInfo
            : FileInfoByHandleClass.FileIdExtdDirectoryRestartInfo;
        _started = true;

        bool ok;
        fixed (byte* p = _buffer)
        {
            ok = Kernel32.GetFileInformationByHandleEx(_handle, infoClass, p, (uint)_buffer.Length);
        }

        if (!ok)
        {
            var error = Marshal.GetLastPInvokeError();
            _terminal = error == Win32Error.NoMoreFiles
                ? DirectoryEnumerationStep.EndOfDirectory
                : DirectoryEnumerationStep.Fail(error == Win32Error.Success ? Win32Error.InvalidData : error);
            return false;
        }

        _offset = 0;
        return true;
    }

    // _offset の項目を読み、次の位置へ進める。"." と ".." は null を返して読み飛ばす。
    private DirectoryEnumerationStep? ReadAt()
    {
        var headerSize = sizeof(FileIdExtdDirInfoHeader);
        if (_offset > _buffer.Length - headerSize)
        {
            _terminal = DirectoryEnumerationStep.Fail(Win32Error.InvalidData);
            return null;
        }

        var header = MemoryMarshal.Read<FileIdExtdDirInfoHeader>(_buffer.AsSpan(_offset));
        var nameStart = _offset + headerSize;
        if (header.FileNameLength % 2 != 0 || header.FileNameLength > _buffer.Length - nameStart)
        {
            _terminal = DirectoryEnumerationStep.Fail(Win32Error.InvalidData);
            return null;
        }

        var nameSpan = MemoryMarshal.Cast<byte, char>(_buffer.AsSpan(nameStart, (int)header.FileNameLength));

        if (header.NextEntryOffset == 0)
        {
            _offset = -1;
        }
        else if (header.NextEntryOffset > (uint)(_buffer.Length - _offset))
        {
            _terminal = DirectoryEnumerationStep.Fail(Win32Error.InvalidData);
            return null;
        }
        else
        {
            _offset += (int)header.NextEntryOffset;
        }

        if (nameSpan is "." or "..")
        {
            return null;
        }

        return DirectoryEnumerationStep.Item(new DirectoryEntry(
            nameSpan.ToString(), header.FileAttributes, header.ReparsePointTag, header.FileId));
    }
}
