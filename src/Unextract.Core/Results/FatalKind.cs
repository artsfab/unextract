namespace Unextract.Core.Results;

// 削除開始前の全体 FATAL の原因の種別 (SPEC §9)。target の入力エラー (SPEC §2、§3 の手順1) を含む。
public enum FatalKind
{
    // ZIP を開けない・ZipArchive が読めない (SPEC §5.4、§9)。入力エラーとして扱う。
    ArchiveOpenFailed,
    ArchiveUnreadable,

    // resource limits (SPEC §11)
    TooManyEntries,
    NameTooLong,
    MetadataTooLarge,
    PathTooDeep,
    EntryTooLarge,
    TotalDeclaredLengthTooLarge,
    InvalidDeclaredLength,

    // 名前の復号とパス (SPEC §4.1、§4.2)
    NameContainsReplacementCharacter,
    RootedPath,
    DriveSpecifier,
    Colon,
    ControlCharacter,
    InvalidCharacter,
    EmptyComponent,
    DotComponent,
    DotDotComponent,
    TrailingDotOrSpace,
    ReservedName,

    // ZIP 内部の構造 (SPEC §4.3)
    DuplicateEntry,
    CaseInsensitiveCollision,
    FileDirectoryConflict,
    FileUsedAsParent,

    // ZIP の特殊エントリ (SPEC §4.4)
    FileEntryWithDirectoryType,
    DirectoryEntryWithFileType,
    UnsupportedEntryType,
    DosDirectoryAttributeOnFileEntry,
    DosReparsePointAttribute,
    DirectoryEntryWithData,

    // target の入力エラー (SPEC §2、§3 の手順1)
    TargetNotFound,
    TargetCheckFailed,
    TargetIsReparsePoint,
    TargetChangedDuringCheck,
    TargetNotDirectory,
    TargetNotNtfs,
    TargetIsUncPath,
    TargetIsDriveRoot,
    TargetUnsupportedPathForm,
    TargetIsProtectedLocation,
    ProtectedLocationUnresolved,

    // target 側の判定不能 (SPEC §6、§7、§9)
    ArchiveIdentityFailed,
    EnumerationOpenFailed,
    EnumerationHandleMismatch,
    EnumerationFailed,
    UnexpectedTargetType,
    ComparisonOpenFailed,
    ComparisonFileIdMismatch,
    FinalPathMismatch,
    ParentFileIdMismatch,
    TargetDeletePending,
    TargetInfoFailed,
    TargetReadFailed,

    // 内容比較候補のエントリ内容の検証基準 1〜5 の違反 (SPEC §5.2) と実測展開量の累計上限 (SPEC §11)
    ContentEncrypted,
    ContentReadFailed,
    ContentTooLong,
    ContentTooShort,
    ContentCrcMismatch,
    TotalReadLengthTooLarge,
}
