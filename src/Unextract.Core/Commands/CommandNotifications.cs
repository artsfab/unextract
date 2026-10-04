using Unextract.Core.Analysis;
using Unextract.Core.Deletion;

namespace Unextract.Core.Commands;

// Prepare 成功時の値だけを渡す。Prepared・ハンドル・削除の基準は公開しない。
// TargetFinalPath は root が取得した最終パス (\\?\ 形式)。表示への変換は呼び出し側が行う。
public sealed record PreparedCommandInfo(
    string ArchivePath,
    string TargetFinalPath,
    RunMode Mode,
    int TotalEntries,
    int SelectedEntries,
    bool EntriesOption);

// 人間向け表示に代わる同期通知。JSON・ログ・protocol は CLI が担当する。
// OnPrepared の成功後に処理を始め、結果はエントリのハンドルを閉じた後に通知する。
// 通知例外は呼び出し元へ伝え、次のエントリへ進まない。
// OnDirectoryCount は delete の完結した DIRECTORY の累計。結果配送に失敗しても途中件数を保持できる。
public sealed record CommandNotifications(
    Action<PreparedCommandInfo>? OnPrepared = null,
    Action<EntryResult>? OnAnalysisResult = null,
    Action<DeleteEntryResult>? OnDeleteResult = null,
    Action<int>? OnDirectoryCount = null);
