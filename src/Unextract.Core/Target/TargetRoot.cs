using Unextract.Core.Results;

namespace Unextract.Core.Target;

// 拒否する位置 (docs/spec/filesystem.md#target-root)。値は最終パス (\\?\ 形式) で与える。
// ExactPaths はそのものだけを拒否し (ユーザープロファイル)、SubtreePaths はそのものと配下を拒否する
// (Windows ディレクトリ、Program Files、Program Files (x86)、ProgramData)。比較は大小文字を区別しない。
// 実際の値 (既知のフォルダーの最終パス) を求めるのは呼び出し側。
public sealed record TargetLocationPolicy(IReadOnlyList<string> ExactPaths, IReadOnlyList<string> SubtreePaths)
{
    public static TargetLocationPolicy None { get; } = new([], []);
}

// 確認済みで保持している target ルート (docs/spec/filesystem.md#target-root のtarget確認)。Dispose で保持用ハンドルを閉じる。
public sealed class TargetRoot : IDisposable
{
    internal TargetRoot(IDirectoryHandle handle, DirectoryHandleInfo info)
    {
        Handle = handle;
        Id = info.Id;
        FinalPath = info.FinalPath;
    }

    public IDirectoryHandle Handle { get; }

    public VolumeFileId Id { get; }

    // 保持用ハンドルから取得した \\?\ 形式の最終パス。期待パスはこれに "\" と ZIP の成分を連結して作る (docs/spec/filesystem.md#handles)。
    public string FinalPath { get; }

    public string ExpectedPath(IEnumerable<string> components) => FinalPath + "\\" + string.Join('\\', components);

    public void Dispose() => Handle.Dispose();
}

public sealed record TargetRootResult(TargetRoot? Root, FatalError? Error);

// target の確認 (docs/spec/cli.md#arguments、docs/spec/filesystem.md#target-root のtarget確認、docs/spec/filesystem.md#handles)。失敗は全て入力エラー (FatalError) として返す。
public static class TargetRootValidator
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const uint FileAttributeReparsePoint = 0x400;
    private const string DevicePrefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    public static TargetRootResult Open(IFileSystemProbe probe, string path, TargetLocationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(policy);

        // 1. 確認用ハンドル (FILE_READ_ATTRIBUTES のみ、OPEN_REPARSE_POINT 付き) で最終成分の reparse と File ID を確認する。
        var confirmation = probe.ConfirmTargetFinalComponent(path);
        if (!confirmation.Succeeded)
        {
            return Fail(IsNotFound(confirmation.Error) ? FatalKind.TargetNotFound : FatalKind.TargetCheckFailed, confirmation.Describe(), confirmation.Error);
        }

        if (IsReparse(confirmation.Value.Attributes, confirmation.Value.ReparseTag))
        {
            return Fail(FatalKind.TargetIsReparsePoint);
        }

        // 2. 確認用ハンドルを閉じた後に保持用ハンドルを開き、同じ個体のディレクトリであることを確かめる。
        var opened = probe.OpenTargetRoot(path);
        if (!opened.Succeeded)
        {
            return Fail(IsNotFound(opened.Error) ? FatalKind.TargetNotFound : FatalKind.TargetCheckFailed, opened.Describe(), opened.Error);
        }

        var handle = opened.Value;
        var error = Check(handle, confirmation.Value, policy, out var info);
        if (error is not null)
        {
            handle.Dispose();
            return new TargetRootResult(null, error);
        }

        return new TargetRootResult(new TargetRoot(handle, info), null);
    }

    // 最終パスの形式と位置の判定 (docs/spec/cli.md#arguments、docs/spec/filesystem.md#handles)。target の最終パスは \\?\X:\... の形だけを受け付ける。
    internal static FatalKind? CheckFinalPath(string finalPath, TargetLocationPolicy policy)
    {
        if (finalPath.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return FatalKind.TargetIsUncPath;
        }

        if (finalPath.Length < DevicePrefix.Length + 3
            || !finalPath.StartsWith(DevicePrefix, StringComparison.Ordinal)
            || !char.IsAsciiLetter(finalPath[4])
            || finalPath[5] != ':'
            || finalPath[6] != '\\')
        {
            return FatalKind.TargetUnsupportedPathForm;
        }

        var rest = finalPath[7..];
        if (rest.Length == 0)
        {
            return FatalKind.TargetIsDriveRoot;
        }

        // 末尾の区切り、空成分を持つ形はドライブルート以外では返らないため、想定外として拒否する。
        if (rest.EndsWith('\\') || rest.Contains(@"\\", StringComparison.Ordinal))
        {
            return FatalKind.TargetUnsupportedPathForm;
        }

        foreach (var exact in policy.ExactPaths)
        {
            if (string.Equals(finalPath, TrimSeparator(exact), StringComparison.OrdinalIgnoreCase))
            {
                return FatalKind.TargetIsProtectedLocation;
            }
        }

        foreach (var subtree in policy.SubtreePaths)
        {
            var root = TrimSeparator(subtree);
            if (string.Equals(finalPath, root, StringComparison.OrdinalIgnoreCase)
                || finalPath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return FatalKind.TargetIsProtectedLocation;
            }
        }

        return null;
    }

    private static FatalError? Check(IDirectoryHandle handle, TargetConfirmation confirmation, TargetLocationPolicy policy, out DirectoryHandleInfo info)
    {
        var result = handle.GetInfo();
        if (!result.Succeeded)
        {
            info = default;
            return Error(FatalKind.TargetCheckFailed, result.Describe(), result.Error);
        }

        info = result.Value;
        if (info.Id != confirmation.Id)
        {
            return Error(FatalKind.TargetChangedDuringCheck);
        }

        if (!info.IsDirectory)
        {
            return Error(FatalKind.TargetNotDirectory);
        }

        if (CheckFinalPath(info.FinalPath, policy) is { } pathError)
        {
            return Error(pathError, info.FinalPath);
        }

        var fileSystem = handle.GetFileSystemName();
        if (!fileSystem.Succeeded)
        {
            return Error(FatalKind.TargetCheckFailed, fileSystem.Describe(), fileSystem.Error);
        }

        if (fileSystem.Value != "NTFS")
        {
            return Error(FatalKind.TargetNotNtfs, fileSystem.Value);
        }

        return null;
    }

    private static string TrimSeparator(string path) =>
        path.Length > 7 && path.EndsWith('\\') ? path.TrimEnd('\\') : path;

    private static bool IsNotFound(int error) => error is ErrorFileNotFound or ErrorPathNotFound;

    private static bool IsReparse(uint attributes, uint reparseTag) =>
        (attributes & FileAttributeReparsePoint) != 0 || reparseTag != 0;

    private static FatalError Error(FatalKind kind, string? detail = null, int? win32Error = null) => new(kind, null, detail, Win32Error: win32Error);

    private static TargetRootResult Fail(FatalKind kind, string? detail = null, int? win32Error = null) => new(null, Error(kind, detail, win32Error));
}
