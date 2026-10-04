namespace Unextract.Core.Results;

// 削除開始前の全体 FATAL の原因の種別 (docs/SPEC.md#failure-stages)。target の入力エラー (docs/spec/cli.md#arguments、docs/spec/filesystem.md#target-root のtarget確認) を含む。
public enum FatalKind
{
    // ZIP を開けない・ZipArchive が読めない (docs/spec/zip.md#runtime、docs/SPEC.md#failure-stages)。Prepare の FATAL として扱う。
    ArchiveOpenFailed,
    ArchiveUnreadable,

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
}
