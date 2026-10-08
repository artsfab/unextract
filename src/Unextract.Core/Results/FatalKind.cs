namespace Unextract.Core.Results;

// 削除開始前の全体 FATAL の原因の種別 (docs/SPEC.md#failure-stages)。target の入力エラー (docs/spec/cli.md#arguments、docs/spec/filesystem.md#target-root のtarget確認) を含む。
public enum FatalKind
{
    // ZIP を開けない・ZipArchive が読めない (docs/spec/zip.md#runtime、docs/SPEC.md#failure-stages)。Prepare の FATAL として扱う。
    ArchiveOpenFailed,
    ArchiveUnreadable,

    // RAR の DLL と形式 (docs/spec/rar.md#pinning、docs/spec/rar.md#format、docs/spec/rar.md#scope)
    RarLibraryUnavailable,
    ArchiveNotRar,
    ArchiveSolid,
    ArchiveMultiVolume,
    ArchiveEncrypted,

    // RAR のエントリ (docs/spec/rar.md#listing、docs/spec/rar.md#names、docs/spec/rar.md#types、docs/spec/rar.md#limits)
    EntrySolid,
    EntrySplit,
    EntryEncrypted,
    EntryRedirection,
    EntryWithoutHash,
    UnsupportedHostOs,
    FileEntryNameEndsWithSeparator,
    EntryDictionaryTooLarge,

    // resource limits (docs/spec/zip.md#limits)
    TooManyEntries,
    NameTooLong,
    MetadataTooLarge,
    PathTooDeep,
    EntryTooLarge,
    TotalDeclaredLengthTooLarge,
    InvalidDeclaredLength,

    // 名前の復号とパス (docs/spec/zip.md#decoding、docs/spec/zip.md#paths)
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

    // ZIP 内部の構造 (docs/spec/zip.md#structure)
    DuplicateEntry,
    CaseInsensitiveCollision,
    FileDirectoryConflict,
    FileUsedAsParent,

    // ZIP の特殊エントリ (docs/spec/zip.md#types)
    FileEntryWithDirectoryType,
    DirectoryEntryWithFileType,
    UnsupportedEntryType,
    DosDirectoryAttributeOnFileEntry,
    DosReparsePointAttribute,
    DirectoryEntryWithData,

    // target の入力エラー (docs/spec/cli.md#arguments、docs/spec/filesystem.md#target-root のtarget確認)
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

    // target 側の判定不能 (docs/spec/filesystem.md#classification、docs/spec/filesystem.md#special-files、docs/SPEC.md#failure-stages)
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

    // 内容比較候補のエントリ内容の検証基準 1〜5 の違反 (docs/spec/zip.md#verification) と実測展開量の累計上限 (docs/spec/zip.md#limits)
    ContentEncrypted,
    ContentReadFailed,
    ContentTooLong,
    ContentTooShort,
    ContentCrcMismatch,
    TotalReadLengthTooLarge,

    // RAR の内容読み取りのヘッダーが Prepare の列挙と異なる・足りない (docs/spec/rar.md#session)
    ArchiveChanged,
}
