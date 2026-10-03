using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Unextract.Windows;

public readonly record struct VolumeFileId(ulong VolumeSerialNumber, FileId128 FileId);

public readonly record struct StandardInformation(long EndOfFile, uint NumberOfLinks, bool DeletePending, bool IsDirectory);

public readonly record struct BasicInformation(long LastWriteTime, long ChangeTime, uint Attributes);

public readonly record struct AttributeTagInformation(uint Attributes, uint ReparseTag);

// 名前は "::$DATA" (既定のデータストリーム)、":name:$DATA" (名前付きストリーム) の形。
public readonly record struct StreamEntry(string Name, long Size);

public readonly record struct VolumeInformation(string FileSystemName)
{
    public bool IsNtfs => FileSystemName == "NTFS";
}

// SPEC §8.2 のスナップショット項目。全て同じハンドルから取得する。
public sealed record FileSnapshot(
    ulong VolumeSerialNumber,
    FileId128 FileId,
    FileId128 ParentFileId,
    long EndOfFile,
    long LastWriteTime,
    long ChangeTime,
    uint Attributes,
    uint NumberOfLinks,
    bool IsDirectory,
    bool DeletePending,
    IReadOnlyList<StreamEntry> Streams,
    uint ReparseTag,
    string FinalPath);

// ハンドルからの情報取得 (SPEC §8.1、§8.2)。どの関数も、API が失敗したら部分的な値を返さず失敗を返す。
public static unsafe class FileInformation
{
    // FILE_STREAM_INFO の取得バッファ。足りなければ倍にし、上限を超えたら失敗とする。
    internal const int StreamBufferInitialSize = 4 * 1024;
    internal const int StreamBufferMaxSize = 1024 * 1024;

    // USN_RECORD_V3 の受け取りバッファ (固定部分 76 バイト + 名前 最大 255 文字)。
    internal const int UsnRecordBufferSize = 1024;

    // 最終パスの上限 (UTF-16 コード単位、終端の NUL を含む)。
    internal const int FinalPathMaxLength = 32 * 1024;

    // GetFinalPathNameByHandleW の flags: FILE_NAME_NORMALIZED (0) | VOLUME_NAME_DOS (0)
    internal const uint FinalPathFlags = 0;

    // SPEC §7 の reparse point 属性 (FILE_ATTRIBUTE_REPARSE_POINT)。
    public const uint FileAttributeReparsePoint = 0x400;

    public static Win32Result<FileSnapshot> ReadSnapshot(SafeFileHandle handle)
    {
        var id = GetVolumeFileId(handle);
        if (!id.Succeeded)
        {
            return id.Cast<FileSnapshot>();
        }

        var parent = GetParentFileId(handle);
        if (!parent.Succeeded)
        {
            return parent.Cast<FileSnapshot>();
        }

        var standard = GetStandardInformation(handle);
        if (!standard.Succeeded)
        {
            return standard.Cast<FileSnapshot>();
        }

        var basic = GetBasicInformation(handle);
        if (!basic.Succeeded)
        {
            return basic.Cast<FileSnapshot>();
        }

        var tag = GetAttributeTagInformation(handle);
        if (!tag.Succeeded)
        {
            return tag.Cast<FileSnapshot>();
        }

        var streams = GetStreams(handle);
        if (!streams.Succeeded)
        {
            return streams.Cast<FileSnapshot>();
        }

        var finalPath = GetFinalPath(handle);
        if (!finalPath.Succeeded)
        {
            return finalPath.Cast<FileSnapshot>();
        }

        return Win32Result<FileSnapshot>.Ok(new FileSnapshot(
            id.Value.VolumeSerialNumber,
            id.Value.FileId,
            parent.Value,
            standard.Value.EndOfFile,
            basic.Value.LastWriteTime,
            basic.Value.ChangeTime,
            basic.Value.Attributes,
            standard.Value.NumberOfLinks,
            standard.Value.IsDirectory,
            standard.Value.DeletePending,
            streams.Value,
            tag.Value.ReparseTag,
            finalPath.Value));
    }

    // FileIdInfo: ボリュームシリアル番号と 128 ビット File ID。
    public static Win32Result<VolumeFileId> GetVolumeFileId(SafeFileHandle handle)
    {
        if (!TryGetFixed(handle, FileInfoByHandleClass.FileIdInfo, out FileIdInfoNative info, out var failure))
        {
            return failure.Cast<VolumeFileId>();
        }

        return Win32Result<VolumeFileId>.Ok(new VolumeFileId(info.VolumeSerialNumber, info.FileId));
    }

    public static Win32Result<StandardInformation> GetStandardInformation(SafeFileHandle handle)
    {
        if (!TryGetFixed(handle, FileInfoByHandleClass.FileStandardInfo, out FileStandardInfoNative info, out var failure))
        {
            return failure.Cast<StandardInformation>();
        }

        return Win32Result<StandardInformation>.Ok(new StandardInformation(
            info.EndOfFile, info.NumberOfLinks, info.DeletePending != 0, info.Directory != 0));
    }

    public static Win32Result<BasicInformation> GetBasicInformation(SafeFileHandle handle)
    {
        if (!TryGetFixed(handle, FileInfoByHandleClass.FileBasicInfo, out FileBasicInfoNative info, out var failure))
        {
            return failure.Cast<BasicInformation>();
        }

        return Win32Result<BasicInformation>.Ok(new BasicInformation(info.LastWriteTime, info.ChangeTime, info.FileAttributes));
    }

    public static Win32Result<AttributeTagInformation> GetAttributeTagInformation(SafeFileHandle handle)
    {
        if (!TryGetFixed(handle, FileInfoByHandleClass.FileAttributeTagInfo, out FileAttributeTagInfoNative info, out var failure))
        {
            return failure.Cast<AttributeTagInformation>();
        }

        return Win32Result<AttributeTagInformation>.Ok(new AttributeTagInformation(info.FileAttributes, info.ReparseTag));
    }

    // FileStreamInfo: データストリームの一覧。
    public static Win32Result<IReadOnlyList<StreamEntry>> GetStreams(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        const string operation = "GetFileInformationByHandleEx(FileStreamInfo)";
        for (var size = StreamBufferInitialSize; size <= StreamBufferMaxSize; size *= 2)
        {
            var buffer = new byte[size];
            bool ok;
            fixed (byte* p = buffer)
            {
                ok = Kernel32.GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileStreamInfo, p, (uint)size);
            }

            if (ok)
            {
                return ParseStreams(buffer, operation);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error is not (Win32Error.MoreData or Win32Error.InsufficientBuffer))
            {
                return Win32Result<IReadOnlyList<StreamEntry>>.Fail(error, operation);
            }
        }

        return Win32Result<IReadOnlyList<StreamEntry>>.Fail(Win32Error.InsufficientBuffer, operation);
    }

    // FSCTL_READ_FILE_USN_DATA (入力 Min 2 / Max 3) で USN_RECORD_V3 を受け取り、親ディレクトリの File ID を返す。
    // USN ジャーナルが非アクティブなボリュームでも動作する (SPEC §13、PoC 4)。
    // hardlink では開いた名前とは別のリンクの親 ID を返す場合がある。delete はリンク数2以上の
    // 非削除判定を先に行い、その場合はこの値を開いた名前の親として照合しない (SPEC §8.3)。
    public static Win32Result<FileId128> GetParentFileId(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        const string operation = "DeviceIoControl(FSCTL_READ_FILE_USN_DATA)";
        var input = new ReadFileUsnData { MinMajorVersion = 2, MaxMajorVersion = 3 };
        var buffer = new byte[UsnRecordBufferSize];
        uint returned;
        bool ok;
        fixed (byte* p = buffer)
        {
            ok = Kernel32.DeviceIoControl(
                handle,
                IoControlCodes.FsctlReadFileUsnData,
                &input,
                (uint)sizeof(ReadFileUsnData),
                p,
                (uint)buffer.Length,
                &returned,
                0);
        }

        if (!ok)
        {
            return Win32Result<FileId128>.LastError(operation);
        }

        // V3 以外の形式、または固定部分に満たない応答は判定に使わない。
        if (returned < sizeof(UsnRecordV3Header))
        {
            return Win32Result<FileId128>.Fail(Win32Error.InvalidData, operation + " (short record)");
        }

        var record = MemoryMarshal.Read<UsnRecordV3Header>(buffer);
        if (record.MajorVersion != 3 || record.RecordLength > returned)
        {
            return Win32Result<FileId128>.Fail(Win32Error.InvalidData, operation + " (not USN_RECORD_V3)");
        }

        return Win32Result<FileId128>.Ok(record.ParentFileReferenceNumber);
    }

    // GetFinalPathNameByHandleW(FILE_NAME_NORMALIZED | VOLUME_NAME_DOS)。返された文字列を加工しない (\\?\ の接頭辞を含む)。
    public static Win32Result<string> GetFinalPath(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        const string operation = "GetFinalPathNameByHandleW";
        var capacity = 512;
        while (true)
        {
            var buffer = new char[capacity];
            uint length;
            fixed (char* p = buffer)
            {
                length = Kernel32.GetFinalPathNameByHandle(handle, p, (uint)capacity, FinalPathFlags);
            }

            if (length == 0)
            {
                return Win32Result<string>.LastError(operation);
            }

            // 成功時は終端の NUL を含まない長さ、バッファ不足時は NUL を含む必要な長さが返る。
            if (length < capacity)
            {
                return Win32Result<string>.Ok(new string(buffer, 0, (int)length));
            }

            if (length > FinalPathMaxLength || length <= capacity)
            {
                return Win32Result<string>.Fail(Win32Error.InsufficientBuffer, operation);
            }

            capacity = (int)length;
        }
    }

    // GetVolumeInformationByHandleW のファイルシステム名 (NTFS 判定用)。
    public static Win32Result<VolumeInformation> GetVolumeInformation(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        const int nameLength = 261; // MAX_PATH + 1
        var fileSystemName = stackalloc char[nameLength];
        if (!Kernel32.GetVolumeInformationByHandle(handle, null, 0, null, null, null, fileSystemName, nameLength))
        {
            return Win32Result<VolumeInformation>.LastError("GetVolumeInformationByHandleW");
        }

        var name = new string(fileSystemName);
        return Win32Result<VolumeInformation>.Ok(new VolumeInformation(name));
    }

    private static bool TryGetFixed<T>(SafeFileHandle handle, FileInfoByHandleClass infoClass, out T value, out Win32Result<T> failure)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(handle);

        T local;
        if (!Kernel32.GetFileInformationByHandleEx(handle, infoClass, &local, (uint)sizeof(T)))
        {
            value = default;
            failure = Win32Result<T>.LastError($"GetFileInformationByHandleEx({infoClass})");
            return false;
        }

        value = local;
        failure = default;
        return true;
    }

    private static Win32Result<IReadOnlyList<StreamEntry>> ParseStreams(byte[] buffer, string operation)
    {
        var streams = new List<StreamEntry>();
        var headerSize = sizeof(FileStreamInfoHeader);
        var offset = 0;
        while (true)
        {
            if (offset < 0 || offset > buffer.Length - headerSize)
            {
                return Win32Result<IReadOnlyList<StreamEntry>>.Fail(Win32Error.InvalidData, operation);
            }

            var header = MemoryMarshal.Read<FileStreamInfoHeader>(buffer.AsSpan(offset));
            var nameStart = offset + headerSize;
            if (header.StreamNameLength % 2 != 0 || header.StreamNameLength > buffer.Length - nameStart)
            {
                return Win32Result<IReadOnlyList<StreamEntry>>.Fail(Win32Error.InvalidData, operation);
            }

            var name = MemoryMarshal.Cast<byte, char>(buffer.AsSpan(nameStart, (int)header.StreamNameLength)).ToString();
            streams.Add(new StreamEntry(name, header.StreamSize));

            if (header.NextEntryOffset == 0)
            {
                return Win32Result<IReadOnlyList<StreamEntry>>.Ok(streams);
            }

            offset += (int)header.NextEntryOffset;
        }
    }
}
