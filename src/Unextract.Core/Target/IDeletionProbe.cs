namespace Unextract.Core.Target;

// 削除用ハンドル (SPEC §8.1 の表の「削除用」の行) の不透明な表現。比較用ハンドルと同じ情報取得と先頭からの逐次読み取り
// (同一性の再検証と2回目の全バイト比較に使う) に加えて、同じハンドルへの削除の指示ができる。
// Dispose でハンドルを閉じる。削除の指示が成立していれば、閉じた時点で名前が消える (SPEC §8.3 の 5)。
public interface IDeletionHandle : IComparisonHandle
{
    // SetFileInformationByHandle(FileDispositionInfoEx) に flags を渡す (SPEC §8.3 の 5)。
    // 成功は「API が成功を返した」ことだけを意味し、削除の成立ではない。成立は DeletePending で確かめる (§8.3 の 6)。
    ProbeResult<bool> SetDispositionEx(uint flags);
}

// 識別確認 (SPEC §8.4) で得る情報。FinalPath は \\?\ 形式のまま。
public readonly record struct IdentityCheckInfo(
    VolumeFileId Id,
    FileId ParentFileId,
    string FinalPath,
    bool IsDirectory,
    bool DeletePending,
    uint Attributes,
    uint ReparseTag);

// 削除フェーズが使う target 操作 (SPEC §8.3、§8.4)。パスを使うのはこの2つのオープンだけである。
// オープンの失敗は OS のエラーコード (Win32 エラー) で返す。
public interface IDeletionProbe
{
    // §8.1 の削除用ハンドル (GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE、FILE_SHARE_READ、
    // FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL) で期待パスを開く。
    ProbeResult<IDeletionHandle> OpenForDeletion(string path);

    // §8.4 の識別確認。FILE_READ_ATTRIBUTES のみ (共有モードの判定に参加しない)、
    // FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL で開き、情報を取得して閉じる。
    // このハンドルでは削除しない。拒否された削除用オープンと同じ個体を見たことも、拒否の理由も保証しない。
    ProbeResult<IdentityCheckInfo> CheckIdentity(string path);
}
