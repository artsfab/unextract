using Microsoft.Win32.SafeHandles;
using CoreTarget = Unextract.Core.Target;

namespace Unextract.Windows;

// target ルートの保持用ハンドル、または列挙用ハンドル (SPEC §8.1)。SafeFileHandle を所有し、Dispose で閉じる。
internal sealed class WindowsDirectoryHandle(SafeFileHandle handle) : CoreTarget.IDirectoryHandle
{
    public CoreTarget.ProbeResult<CoreTarget.DirectoryHandleInfo> GetInfo()
    {
        var id = FileInformation.GetVolumeFileId(handle);
        if (!id.Succeeded)
        {
            return id.Fail<CoreTarget.DirectoryHandleInfo, VolumeFileId>();
        }

        var standard = FileInformation.GetStandardInformation(handle);
        if (!standard.Succeeded)
        {
            return standard.Fail<CoreTarget.DirectoryHandleInfo, StandardInformation>();
        }

        var tag = FileInformation.GetAttributeTagInformation(handle);
        if (!tag.Succeeded)
        {
            return tag.Fail<CoreTarget.DirectoryHandleInfo, AttributeTagInformation>();
        }

        var finalPath = FileInformation.GetFinalPath(handle);
        if (!finalPath.Succeeded)
        {
            return finalPath.Fail<CoreTarget.DirectoryHandleInfo, string>();
        }

        return CoreTarget.ProbeResult<CoreTarget.DirectoryHandleInfo>.Ok(new CoreTarget.DirectoryHandleInfo(
            id.Value.ToCore(), standard.Value.IsDirectory, tag.Value.Attributes, tag.Value.ReparseTag, finalPath.Value));
    }

    public CoreTarget.ProbeResult<string> GetFileSystemName()
    {
        var volume = FileInformation.GetVolumeInformation(handle);
        return volume.Succeeded
            ? CoreTarget.ProbeResult<string>.Ok(volume.Value.FileSystemName)
            : volume.Fail<string, VolumeInformation>();
    }

    public CoreTarget.IDirectoryEnumeration Enumerate() => new Enumeration(new DirectoryEnumerator(handle));

    public void Dispose() => handle.Dispose();

    private sealed class Enumeration(DirectoryEnumerator enumerator) : CoreTarget.IDirectoryEnumeration
    {
        private const string Operation = "GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)";

        public CoreTarget.DirectoryEnumerationStep Next()
        {
            var step = enumerator.Next();
            return step.Kind switch
            {
                DirectoryEnumerationStepKind.Entry => CoreTarget.DirectoryEnumerationStep.OfItem(new CoreTarget.DirectoryItem(
                    step.Entry.Name, step.Entry.Attributes, step.Entry.ReparseTag, step.Entry.FileId.ToCore())),
                DirectoryEnumerationStepKind.End => CoreTarget.DirectoryEnumerationStep.EndOfDirectory,
                _ => CoreTarget.DirectoryEnumerationStep.Fail(step.Error, Operation),
            };
        }
    }
}

// 比較用ハンドル (SPEC §8.1)。
internal sealed class WindowsComparisonHandle(SafeFileHandle handle) : WindowsFileHandle(handle);

// 削除用ハンドル (SPEC §8.1 の表の「削除用」の行)。同一性の再検証・2回目の全バイト比較・最終確認・削除の指示・成立確認を
// 全てこのハンドルで行う (SPEC §8.3)。Dispose で閉じる。削除の指示が成立していれば、閉じた時点で名前が消える。
internal sealed class WindowsDeletionHandle(SafeFileHandle handle) : WindowsFileHandle(handle), CoreTarget.IDeletionHandle
{
    // FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS。これ以外の flags は API を呼ばずに失敗とする
    // (IGNORE_READONLY_ATTRIBUTE (0x10) を含む値を誤って渡さないための二重の確認。SPEC §8.3 の 5)。
    internal const uint DeletePosixSemantics = 0x3;

    private const int ErrorInvalidParameter = 87;

    // SetFileInformationByHandle(FileDispositionInfoEx) の唯一の呼び出し箇所。成功は削除の成立を意味しない
    // (成立は同じハンドルの DeletePending で確かめる。SPEC §8.3 の 6)。
    public unsafe CoreTarget.ProbeResult<bool> SetDispositionEx(uint flags)
    {
        const string operation = "SetFileInformationByHandle(FileDispositionInfoEx)";
        if (flags != DeletePosixSemantics)
        {
            return CoreTarget.ProbeResult<bool>.Fail(ErrorInvalidParameter, operation + " (flags must be 0x3)");
        }

        var info = new FileDispositionInfoExNative { Flags = flags };
        if (!Kernel32.SetFileInformationByHandle(Handle, FileInfoByHandleClass.FileDispositionInfoEx, &info, (uint)sizeof(FileDispositionInfoExNative)))
        {
            return Win32Result<bool>.LastError(operation).Fail<bool, bool>();
        }

        return CoreTarget.ProbeResult<bool>.Ok(true);
    }
}

// 比較用・削除用ハンドルの共通部分。SafeFileHandle を所有し、Dispose で閉じる。
// 内容は同じハンドルから RandomAccess で先頭から順に読む (パスで開き直さない)。
internal abstract class WindowsFileHandle(SafeFileHandle handle) : CoreTarget.IComparisonHandle
{
    private const int ErrorInvalidData = 13;
    private long _position;

    protected SafeFileHandle Handle => handle;

    public CoreTarget.ProbeResult<CoreTarget.VolumeFileId> GetVolumeFileId()
    {
        var id = FileInformation.GetVolumeFileId(handle);
        return id.Succeeded ? CoreTarget.ProbeResult<CoreTarget.VolumeFileId>.Ok(id.Value.ToCore()) : id.Fail<CoreTarget.VolumeFileId, VolumeFileId>();
    }

    public CoreTarget.ProbeResult<CoreTarget.StandardInformation> GetStandardInformation()
    {
        var info = FileInformation.GetStandardInformation(handle);
        return info.Succeeded
            ? CoreTarget.ProbeResult<CoreTarget.StandardInformation>.Ok(new CoreTarget.StandardInformation(
                info.Value.EndOfFile, info.Value.NumberOfLinks, info.Value.DeletePending, info.Value.IsDirectory))
            : info.Fail<CoreTarget.StandardInformation, StandardInformation>();
    }

    public CoreTarget.ProbeResult<CoreTarget.BasicInformation> GetBasicInformation()
    {
        var info = FileInformation.GetBasicInformation(handle);
        return info.Succeeded
            ? CoreTarget.ProbeResult<CoreTarget.BasicInformation>.Ok(new CoreTarget.BasicInformation(
                info.Value.LastWriteTime, info.Value.ChangeTime, info.Value.Attributes))
            : info.Fail<CoreTarget.BasicInformation, BasicInformation>();
    }

    public CoreTarget.ProbeResult<CoreTarget.AttributeTagInformation> GetAttributeTagInformation()
    {
        var info = FileInformation.GetAttributeTagInformation(handle);
        return info.Succeeded
            ? CoreTarget.ProbeResult<CoreTarget.AttributeTagInformation>.Ok(new CoreTarget.AttributeTagInformation(info.Value.Attributes, info.Value.ReparseTag))
            : info.Fail<CoreTarget.AttributeTagInformation, AttributeTagInformation>();
    }

    // データストリームを持たないディレクトリでは ERROR_HANDLE_EOF (38) の失敗になる。Core は Directory を先に判定し、
    // ディレクトリではこれを呼ばない (SPEC §7)。ファイルで 38 が返った場合はそのまま失敗として返す。
    public CoreTarget.ProbeResult<IReadOnlyList<CoreTarget.StreamEntry>> GetStreams()
    {
        var streams = FileInformation.GetStreams(handle);
        if (!streams.Succeeded)
        {
            return streams.Fail<IReadOnlyList<CoreTarget.StreamEntry>, IReadOnlyList<StreamEntry>>();
        }

        return CoreTarget.ProbeResult<IReadOnlyList<CoreTarget.StreamEntry>>.Ok(
            streams.Value.Select(s => new CoreTarget.StreamEntry(s.Name, s.Size)).ToList());
    }

    public CoreTarget.ProbeResult<CoreTarget.FileId> GetParentFileId()
    {
        var parent = FileInformation.GetParentFileId(handle);
        return parent.Succeeded ? CoreTarget.ProbeResult<CoreTarget.FileId>.Ok(parent.Value.ToCore()) : parent.Fail<CoreTarget.FileId, FileId128>();
    }

    public CoreTarget.ProbeResult<string> GetFinalPath()
    {
        var finalPath = FileInformation.GetFinalPath(handle);
        return finalPath.Succeeded ? CoreTarget.ProbeResult<string>.Ok(finalPath.Value) : finalPath.Fail<string, string>();
    }

    public CoreTarget.ProbeResult<int> Read(Span<byte> buffer)
    {
        int read;
        try
        {
            read = RandomAccess.Read(handle, buffer, _position);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CoreTarget.ProbeResult<int>.Fail(Win32Code(ex), "ReadFile");
        }

        _position += read;
        return CoreTarget.ProbeResult<int>.Ok(read);
    }

    public void Dispose() => handle.Dispose();

    // .NET の例外の HRESULT が Win32 エラー由来 (FACILITY_WIN32) ならそのコード。UnauthorizedAccessException は 5。
    private static int Win32Code(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return 5;
        }

        var hresult = ex.HResult;
        return (hresult & 0xFFFF0000) == 0x80070000 ? hresult & 0xFFFF : ErrorInvalidData;
    }
}
