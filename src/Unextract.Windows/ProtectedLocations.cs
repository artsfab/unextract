using Unextract.Core.Results;
using Unextract.Core.Target;

namespace Unextract.Windows;

public sealed record ProtectedLocationsResult(TargetLocationPolicy? Policy, FatalError? Error);

// 拒否対象の実パス (docs/spec/cli.md#arguments、docs/spec/filesystem.md#target-root のtarget確認)。Environment.GetFolderPath で得たパスを FILE_READ_ATTRIBUTES のみのハンドルで開き、
// GetFinalPathNameByHandleW の最終パス (\\?\ 形式) に直す。取得・解決に失敗したら、安全を確認できないため入力エラー。
// 新しい P/Invoke は使わない (CreateFileW と GetFinalPathNameByHandleW だけ)。
public static class ProtectedLocations
{
    // ユーザープロファイルはそのものだけを拒否する。
    private static readonly Environment.SpecialFolder[] Exact = [Environment.SpecialFolder.UserProfile];

    // Windows ディレクトリ、Program Files、Program Files (x86)、ProgramData はそのものと配下を拒否する。
    private static readonly Environment.SpecialFolder[] Subtree =
    [
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonApplicationData,
    ];

    public static ProtectedLocationsResult Resolve()
    {
        var exact = new List<string>();
        var subtree = new List<string>();
        foreach (var (folders, list) in new[] { (Exact, exact), (Subtree, subtree) })
        {
            foreach (var folder in folders)
            {
                var resolved = ResolveFolder(folder);
                if (resolved.Error is { } error)
                {
                    return new ProtectedLocationsResult(null, error);
                }

                list.Add(resolved.FinalPath!);
            }
        }

        return new ProtectedLocationsResult(new TargetLocationPolicy(exact, subtree), null);
    }

    private static (string? FinalPath, FatalError? Error) ResolveFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path))
        {
            return (null, new FatalError(FatalKind.ProtectedLocationUnresolved, Detail: $"{folder}: パスを取得できません"));
        }

        var opened = HandleOpener.OpenForAttributes(path);
        if (!opened.Succeeded)
        {
            return (null, new FatalError(FatalKind.ProtectedLocationUnresolved, Detail: $"{folder}: {opened}", Win32Error: opened.Error));
        }

        using var handle = opened.Value;
        var finalPath = FileInformation.GetFinalPath(handle);
        return finalPath.Succeeded
            ? (finalPath.Value, null)
            : (null, new FatalError(FatalKind.ProtectedLocationUnresolved, Detail: $"{folder}: {finalPath}", Win32Error: finalPath.Error));
    }
}
