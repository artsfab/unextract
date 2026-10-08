using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Unextract.Windows;

// kernel32 の LibraryImport 宣言 (docs/spec/filesystem.md#handles)。読み取り・情報取得・列挙と、ハンドルへの削除の指示に必要なものだけを置く。
// パスベースの削除・改名の API (DeleteFileW、MoveFileExW など) は宣言しない。
internal static unsafe partial class Kernel32
{
    private const string Library = "kernel32.dll";

    // 削除の指示 (docs/spec/filesystem.md#delete-flow の削除指示) だけに使う。呼び出しは WindowsDeletionHandle.SetDispositionEx の1か所。
    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass infoClass,
        void* buffer,
        uint bufferSize);

    [LibraryImport(Library, EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass infoClass,
        void* buffer,
        uint bufferSize);

    [LibraryImport(Library, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        char* filePath,
        uint filePathLength,
        uint flags);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        void* inBuffer,
        uint inBufferSize,
        void* outBuffer,
        uint outBufferSize,
        uint* bytesReturned,
        nint overlapped);

    // NTFS 判定用。ファイルシステム名だけを使う。
    [LibraryImport(Library, EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformationByHandle(
        SafeFileHandle file,
        char* volumeNameBuffer,
        uint volumeNameSize,
        uint* volumeSerialNumber,
        uint* maximumComponentLength,
        uint* fileSystemFlags,
        char* fileSystemNameBuffer,
        uint fileSystemNameSize);

    // UnRAR.dll の読み込み (docs/spec/rar.md#pinning) だけに使う。絶対パスと検索範囲を限るフラグで呼び、DLL の検索順序に頼らない。FreeLibrary はしない。
    [LibraryImport(Library, EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    [LibraryImport(Library, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetProcAddress(nint module, string procName);
}

internal static class LoadLibraryFlags
{
    // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32: 依存の探索を DLL のフォルダーと System32 に限る。
    public const uint SearchDllLoadDirAndSystem32 = 0x00000100 | 0x00000800;
}

// FILE_INFO_BY_HANDLE_CLASS (minwinbase.h) のうち使うもの。
internal enum FileInfoByHandleClass
{
    FileBasicInfo = 0,
    FileStandardInfo = 1,
    FileStreamInfo = 7,
    FileAttributeTagInfo = 9,
    FileIdInfo = 18,
    FileIdExtdDirectoryInfo = 19,
    FileIdExtdDirectoryRestartInfo = 20,
    FileDispositionInfoEx = 21,
}

internal static class Win32Error
{
    public const int Success = 0;
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;
    public const int InvalidData = 13;
    public const int NoMoreFiles = 18;
    public const int SharingViolation = 32;
    public const int HandleEof = 38;
    public const int InsufficientBuffer = 122;
    public const int MoreData = 234;
}

internal static class IoControlCodes
{
    // CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 58, METHOD_NEITHER, FILE_ANY_ACCESS)
    public const uint FsctlReadFileUsnData = 0x000900EB;
}
