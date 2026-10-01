using Microsoft.Win32.SafeHandles;

namespace Unextract.Windows;

// SPEC §8.1 の表の用途別オープン。アクセス・共有モード・フラグは表の行と完全に一致させ、HandleSpecs の1か所で定義する。
// FILE_FLAG_BACKUP_SEMANTICS を使っても特権の有効化はしない。
public static class HandleOpener
{
    // 表の「削除用」の行。1件の再検証・再比較・削除の間だけ開く。
    public static Win32Result<SafeFileHandle> OpenForDeletion(string path) => Open(path, HandleSpecs.Deletion);

    // SPEC §8.4 の識別確認。削除用オープンが共有違反・アクセス拒否で失敗したときだけ開く。このハンドルでは削除しない。
    public static Win32Result<SafeFileHandle> OpenForIdentityCheck(string path) => Open(path, HandleSpecs.IdentityCheck);

    // 表の「target ルート」の行。実行終了まで保持する。
    public static Win32Result<SafeFileHandle> OpenTargetRoot(string path) => Open(path, HandleSpecs.TargetRoot);

    // 表の「列挙用 (target ルート以外のディレクトリ)」の行。
    public static Win32Result<SafeFileHandle> OpenDirectoryForEnumeration(string path) =>
        Open(path, HandleSpecs.Enumeration);

    // 表の「比較用」の行。
    public static Win32Result<SafeFileHandle> OpenForComparison(string path) => Open(path, HandleSpecs.Comparison);

    // SPEC §3 の手順1の確認用ハンドル (最終成分の reparse をたどらない)。
    public static Win32Result<SafeFileHandle> OpenForConfirmation(string path) => Open(path, HandleSpecs.Confirmation);

    // FILE_READ_ATTRIBUTES のみで開く (共有モードの判定に参加しない)。ZIP 自身の File ID と、拒否対象のフォルダーの最終パスに使う。
    public static Win32Result<SafeFileHandle> OpenForAttributes(string path) => Open(path, HandleSpecs.AttributesOnly);

    internal static Win32Result<SafeFileHandle> Open(string path, HandleSpec spec)
    {
        ArgumentNullException.ThrowIfNull(path);

        var handle = Kernel32.CreateFile(
            path, spec.Access, spec.Share, 0, HandleSpecs.OpenExisting, spec.Flags, 0);
        if (handle.IsInvalid)
        {
            var result = Win32Result<SafeFileHandle>.LastError("CreateFileW");
            handle.Dispose();
            return result;
        }

        return Win32Result<SafeFileHandle>.Ok(handle);
    }
}

internal readonly record struct HandleSpec(uint Access, uint Share, uint Flags);

// アクセス・共有モード・フラグの定数 (SPEC §8.1 の表)。値は winnt.h / fileapi.h のもの。
internal static class HandleSpecs
{
    // アクセス
    public const uint FileListDirectory = 0x0001;
    public const uint FileReadAttributes = 0x0080;
    public const uint Delete = 0x00010000;
    public const uint Synchronize = 0x00100000;
    public const uint GenericRead = 0x80000000;

    // 共有モード
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;

    // 作成方法
    public const uint OpenExisting = 3;

    // フラグ
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint FileFlagOpenNoRecall = 0x00100000;
    public const uint FileFlagSequentialScan = 0x08000000;

    // | target ルート | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | FILE_SHARE_READ | FILE_SHARE_WRITE (DELETE を共有しない)
    // | FILE_FLAG_BACKUP_SEMANTICS | 実行終了まで |
    public static readonly HandleSpec TargetRoot = new(
        FileListDirectory | FileReadAttributes,
        FileShareRead | FileShareWrite,
        FileFlagBackupSemantics);

    // | 列挙用 (target ルート以外のディレクトリ) | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | FILE_SHARE_READ | FILE_SHARE_WRITE
    // | FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT | そのディレクトリの検証と列挙の間だけ |
    public static readonly HandleSpec Enumeration = new(
        FileListDirectory | FileReadAttributes,
        FileShareRead | FileShareWrite,
        FileFlagBackupSemantics | FileFlagOpenReparsePoint);

    // | 比較用 | GENERIC_READ | FILE_SHARE_READ
    // | FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL | FILE_FLAG_SEQUENTIAL_SCAN
    // | そのエントリの初回判定中だけ |
    public static readonly HandleSpec Comparison = new(
        GenericRead,
        FileShareRead,
        FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagOpenNoRecall | FileFlagSequentialScan);

    // | 削除用 | GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE | FILE_SHARE_READ
    // | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL | 1件の再検証・再比較・削除の間だけ |
    // FILE_SHARE_WRITE と FILE_SHARE_DELETE を含めない。FILE_FLAG_BACKUP_SEMANTICS は付けない (ディレクトリは開けず 5 になる)。
    public static readonly HandleSpec Deletion = new(
        GenericRead | Delete | FileReadAttributes | Synchronize,
        FileShareRead,
        FileFlagOpenReparsePoint | FileFlagOpenNoRecall);

    // SPEC §8.4 の識別確認: FILE_READ_ATTRIBUTES のみ、FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL。
    // データアクセスを持たないため共有モードの判定に参加しない (共有モードの値は他者に影響しない)。
    public static readonly HandleSpec IdentityCheck = new(
        FileReadAttributes,
        FileShareRead | FileShareWrite,
        FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagOpenNoRecall);

    // SPEC §3 の手順1の確認用: FILE_READ_ATTRIBUTES のみ、FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT。
    // データアクセスを持たないため共有モードの判定に参加しない (共有モードの値は他者に影響しない)。
    public static readonly HandleSpec Confirmation = new(
        FileReadAttributes,
        FileShareRead | FileShareWrite,
        FileFlagBackupSemantics | FileFlagOpenReparsePoint);

    // FILE_READ_ATTRIBUTES のみ、FILE_FLAG_BACKUP_SEMANTICS (reparse はたどる)。ZIP 自身の File ID (SPEC §7) と、
    // 拒否対象のフォルダーの最終パス (SPEC §3 の手順1) に使う。保持中の ZIP (FileShare.Read) とも共存する。
    public static readonly HandleSpec AttributesOnly = new(
        FileReadAttributes,
        FileShareRead | FileShareWrite,
        FileFlagBackupSemantics);
}
