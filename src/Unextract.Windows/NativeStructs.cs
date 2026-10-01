using System.Runtime.InteropServices;

namespace Unextract.Windows;

// Win32 構造体の blittable な写し。フィールドの順序と型は Windows SDK のヘッダーと一致させる。
// BOOLEAN は byte、LARGE_INTEGER は long、FILE_ID_128 は FileId128 (16 バイト) で表す。
// 可変長の名前を持つ構造体 (FILE_STREAM_INFO、FILE_ID_EXTD_DIR_INFO、USN_RECORD_V3) は固定部分だけを定義し、
// 名前はバッファ上のオフセットから読む。

// FILE_ID_INFO (winbase.h)
[StructLayout(LayoutKind.Sequential)]
internal struct FileIdInfoNative
{
    public ulong VolumeSerialNumber;
    public FileId128 FileId;
}

// FILE_STANDARD_INFO (winbase.h)
[StructLayout(LayoutKind.Sequential)]
internal struct FileStandardInfoNative
{
    public long AllocationSize;
    public long EndOfFile;
    public uint NumberOfLinks;
    public byte DeletePending;
    public byte Directory;
}

// FILE_BASIC_INFO (winbase.h)
[StructLayout(LayoutKind.Sequential)]
internal struct FileBasicInfoNative
{
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;
}

// FILE_ATTRIBUTE_TAG_INFO (winbase.h)
[StructLayout(LayoutKind.Sequential)]
internal struct FileAttributeTagInfoNative
{
    public uint FileAttributes;
    public uint ReparseTag;
}

// FILE_STREAM_INFO (winbase.h) の固定部分。直後に StreamName (WCHAR、StreamNameLength バイト) が続く。
[StructLayout(LayoutKind.Sequential)]
internal struct FileStreamInfoHeader
{
    public uint NextEntryOffset;
    public uint StreamNameLength;
    public long StreamSize;
    public long StreamAllocationSize;
}

// FILE_ID_EXTD_DIR_INFO (winbase.h) の固定部分。直後に FileName (WCHAR、FileNameLength バイト) が続く。
[StructLayout(LayoutKind.Sequential)]
internal struct FileIdExtdDirInfoHeader
{
    public uint NextEntryOffset;
    public uint FileIndex;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public long EndOfFile;
    public long AllocationSize;
    public uint FileAttributes;
    public uint FileNameLength;
    public uint EaSize;
    public uint ReparsePointTag;
    public FileId128 FileId;
}

// READ_FILE_USN_DATA (winioctl.h)
[StructLayout(LayoutKind.Sequential)]
internal struct ReadFileUsnData
{
    public ushort MinMajorVersion;
    public ushort MaxMajorVersion;
}

// USN_RECORD_V3 (winioctl.h) の固定部分。直後に FileName が続く。
[StructLayout(LayoutKind.Sequential)]
internal struct UsnRecordV3Header
{
    public uint RecordLength;
    public ushort MajorVersion;
    public ushort MinorVersion;
    public FileId128 FileReferenceNumber;
    public FileId128 ParentFileReferenceNumber;
    public long Usn;
    public long TimeStamp;
    public uint Reason;
    public uint SourceInfo;
    public uint SecurityId;
    public uint FileAttributes;
    public ushort FileNameLength;
    public ushort FileNameOffset;
}

// FILE_DISPOSITION_INFO_EX (winbase.h)
[StructLayout(LayoutKind.Sequential)]
internal struct FileDispositionInfoExNative
{
    public uint Flags;
}
