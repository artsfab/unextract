namespace Unextract.Core.Target;

// 削除用ハンドル (docs/spec/filesystem.md#handles の表の「削除用」の行) の不透明な表現。比較用ハンドルと同じ情報取得と先頭からの逐次読み取り
// (同一性の照合とStrictの1回の全バイト比較に使う) に加えて、同じハンドルへの削除の指示ができる。
// Dispose でハンドルを閉じる。削除の指示が成立していれば、閉じた時点で名前が消える (docs/spec/filesystem.md#delete-flow の削除指示)。
public interface IDeletionHandle : IComparisonHandle
{
    // SetFileInformationByHandle(FileDispositionInfoEx) に flags を渡す (docs/spec/filesystem.md#delete-flow の削除指示)。
    // 成功は「API が成功を返した」ことだけを意味し、削除の成立ではない。成立は DeletePending で確かめる (docs/spec/filesystem.md#delete-flow の成立確認)。
    ProbeResult<bool> SetDispositionEx(uint flags);
}

// ディレクトリ削除用ハンドル (docs/spec/filesystem.md#handles の表の「ディレクトリ削除用」の行、docs/spec/filesystem.md#prune-flow) の不透明な表現。
// 照合・検査・空の確認 (同じハンドルでの列挙)・最終確認・削除の指示・成立確認を全てこのハンドルで行う。内容は読まない。
// Dispose でハンドルを閉じる。削除の指示が成立していれば、閉じた時点で名前が消える。
public interface IDirectoryDeletionHandle : IHandleInformation
{
    // 同じハンドルで項目を列挙する (FileIdExtdDirectoryRestartInfo から始める)。
    IDirectoryEnumeration Enumerate();

    // ファイルの削除用ハンドルと同じ SetFileInformationByHandle(FileDispositionInfoEx) の呼び出し (docs/spec/filesystem.md#prune-flow の手順8)。
    // 成功は削除の成立ではない。成立は DeletePending で確かめる。
    ProbeResult<bool> SetDispositionEx(uint flags);
}

// 識別確認 (docs/spec/filesystem.md#failure-boundary) で得る情報。FinalPath は \\?\ 形式のまま。
public readonly record struct IdentityCheckInfo(
    VolumeFileId Id,
    FileId ParentFileId,
    string FinalPath,
    bool IsDirectory,
    bool DeletePending,
    uint Attributes,
    uint ReparseTag);

// 削除フェーズが使う target 操作 (docs/spec/filesystem.md#delete-flow、docs/spec/filesystem.md#failure-boundary、docs/spec/filesystem.md#prune-flow)。パスを使うのはこの3つのオープンだけである。
// オープンの失敗は OS のエラーコード (Win32 エラー) で返す。
public interface IDeletionProbe
{
    // docs/spec/filesystem.md#handles の削除用ハンドル (GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE、FILE_SHARE_READ、
    // FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL) で期待パスを開く。
    ProbeResult<IDeletionHandle> OpenForDeletion(string path);

    // docs/spec/filesystem.md#handles のディレクトリ削除用ハンドル (DELETE | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE、
    // FILE_SHARE_READ | FILE_SHARE_WRITE、FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL) で期待パスを開く。
    ProbeResult<IDirectoryDeletionHandle> OpenDirectoryForDeletion(string path);

    // docs/spec/filesystem.md#failure-boundary の識別確認。FILE_READ_ATTRIBUTES のみ (共有モードの判定に参加しない)、
    // FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL で開き、情報を取得して閉じる。
    // このハンドルでは削除しない。拒否された削除用オープンと同じ個体を見たことも、拒否の理由も保証しない。
    ProbeResult<IdentityCheckInfo> CheckIdentity(string path);
}
