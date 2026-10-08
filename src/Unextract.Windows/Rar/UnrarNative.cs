using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Unextract.Windows.Rar;

// UnRAR.dll の定義 (同梱の unrar.h、RAR_DLL_VERSION 10、#pragma pack(1)。sizeof・offset を照合済み: docs/RATIONALE.md#rar-dll-usage)。
// 使う操作は open (一覧用・内容読み取り用)、ヘッダーの読み取り、RAR_SKIP・RAR_TEST、close、版の取得だけ (docs/spec/rar.md#runtime)。
// RAR_EXTRACT と展開先の指定は定義しない。
internal static unsafe class UnrarNative
{
    public const int ErarSuccess = 0;
    public const int ErarEndArchive = 10;
    public const int ErarMissingPassword = 22;

    // 一覧用は前の巻から続くエントリも列挙する RAR_OM_LIST_INCSPLIT (docs/spec/rar.md#listing)。内容読み取り用は RAR_OM_EXTRACT (操作は RAR_TEST だけ)。
    public const uint OpenModeListIncludingSplit = 2;
    public const uint OpenModeExtract = 1;

    public const int RarSkip = 0;
    public const int RarTest = 1;

    public const uint ArchiveVolume = 0x0001;
    public const uint ArchiveSolid = 0x0008;
    public const uint ArchiveEncryptedHeaders = 0x0080;

    public const uint HeaderSplitBefore = 0x01;
    public const uint HeaderSplitAfter = 0x02;
    public const uint HeaderEncrypted = 0x04;
    public const uint HeaderSolid = 0x10;
    public const uint HeaderDirectory = 0x20;

    public const uint HostWindows = 2;
    public const uint HostUnix = 3;

    public const uint HashNone = 0;
    public const uint HashCrc32 = 1;
    public const uint HashBlake2 = 2;

    public const uint CallbackChangeVolume = 0;
    public const uint CallbackProcessData = 1;
    public const uint CallbackNeedPassword = 2;
    public const uint CallbackChangeVolumeW = 3;
    public const uint CallbackNeedPasswordW = 4;
    public const uint CallbackLargeDictionary = 5;

    // 採用版 (docs/spec/rar.md#pinning)。
    public const int AdoptedVersion = 10;

    // FileNameEx のバッファ (UTF-16 の文字数、終端を含む)。これを使い切った名前は切り詰めの可能性があるので名前長の上限違反とする。
    public const int NameBufferChars = 65_536;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RAROpenArchiveDataEx
    {
        public byte* ArcName;
        public char* ArcNameW;
        public uint OpenMode;
        public uint OpenResult;
        public byte* CmtBuf;
        public uint CmtBufSize;
        public uint CmtSize;
        public uint CmtState;
        public uint Flags;
        public nint Callback;
        public nint UserData;
        public uint OpFlags;
        public char* CmtBufW;
        public char* MarkOfTheWeb;
        public fixed uint Reserved[23];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RARHeaderDataEx
    {
        public fixed byte ArcName[1024];
        public fixed char ArcNameW[1024];
        public fixed byte FileName[1024];
        public fixed char FileNameW[1024];
        public uint Flags;
        public uint PackSize;
        public uint PackSizeHigh;
        public uint UnpSize;
        public uint UnpSizeHigh;
        public uint HostOS;
        public uint FileCRC;
        public uint FileTime;
        public uint UnpVer;
        public uint Method;
        public uint FileAttr;
        public byte* CmtBuf;
        public uint CmtBufSize;
        public uint CmtSize;
        public uint CmtState;
        public uint DictSize;
        public uint HashType;
        public fixed byte Hash[32];
        public uint RedirType;
        public char* RedirName;
        public uint RedirNameSize;
        public uint DirTarget;
        public uint MtimeLow;
        public uint MtimeHigh;
        public uint CtimeLow;
        public uint CtimeHigh;
        public uint AtimeLow;
        public uint AtimeHigh;
        public char* ArcNameEx;
        public uint ArcNameExSize;
        public char* FileNameEx;
        public uint FileNameExSize;
        public fixed uint Reserved[982];
    }

    public static string ErrorName(int code) => code switch
    {
        0 => "ERAR_SUCCESS",
        10 => "ERAR_END_ARCHIVE",
        11 => "ERAR_NO_MEMORY",
        12 => "ERAR_BAD_DATA",
        13 => "ERAR_BAD_ARCHIVE",
        14 => "ERAR_UNKNOWN_FORMAT",
        15 => "ERAR_EOPEN",
        16 => "ERAR_ECREATE",
        17 => "ERAR_ECLOSE",
        18 => "ERAR_EREAD",
        19 => "ERAR_EWRITE",
        20 => "ERAR_SMALL_BUF",
        21 => "ERAR_UNKNOWN",
        22 => "ERAR_MISSING_PASSWORD",
        23 => "ERAR_EREFERENCE",
        24 => "ERAR_BAD_PASSWORD",
        25 => "ERAR_LARGE_DICT",
        _ => "ERAR " + code.ToString(CultureInfo.InvariantCulture),
    };

    // 全ハンドル共通のコールバックの入口。処理本体は UnrarCallbackState.Dispatch に置き、例外をネイティブの呼び出し元へ伝えない。
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    public static int Callback(uint message, nint userData, nint p1, nint p2) => UnrarCallbackState.Dispatch(message, userData, p1, p2);

    public static nint CallbackPointer => (nint)(delegate* unmanaged[Stdcall]<uint, nint, nint, nint, int>)&Callback;
}

// ネイティブの呼び出しの差し替え口。製品は UnrarFunctions (DLL の関数ポインター) だけを使い、テストは台本で動く偽物を渡す。
// 呼び出し元は単一スレッドで使う。ProcessFile の操作は RAR_SKIP と RAR_TEST だけで、展開先は渡さない。
internal unsafe interface IUnrarApi
{
    nint Open(UnrarNative.RAROpenArchiveDataEx* data);

    int ReadHeader(nint archive, UnrarNative.RARHeaderDataEx* header);

    int ProcessFile(nint archive, int operation);

    int Close(nint archive);

    int Version();
}

// ロード済みの DLL の関数ポインター。
internal sealed unsafe class UnrarFunctions(
    delegate* unmanaged[Stdcall]<UnrarNative.RAROpenArchiveDataEx*, nint> open,
    delegate* unmanaged[Stdcall]<nint, UnrarNative.RARHeaderDataEx*, int> readHeader,
    delegate* unmanaged[Stdcall]<nint, int, char*, char*, int> processFile,
    delegate* unmanaged[Stdcall]<nint, int> close,
    delegate* unmanaged[Stdcall]<int> version) : IUnrarApi
{
    public nint Open(UnrarNative.RAROpenArchiveDataEx* data) => open(data);

    public int ReadHeader(nint archive, UnrarNative.RARHeaderDataEx* header) => readHeader(archive, header);

    public int ProcessFile(nint archive, int operation)
    {
        if (operation is not (UnrarNative.RarSkip or UnrarNative.RarTest))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "RAR_SKIP と RAR_TEST 以外は使わない");
        }

        // 展開先 (DestPath・DestName) は常に null。
        return processFile(archive, operation, null, null);
    }

    public int Close(nint archive) => close(archive);

    public int Version() => version();
}
