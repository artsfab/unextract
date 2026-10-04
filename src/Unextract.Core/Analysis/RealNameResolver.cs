using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Analysis;

// 実名の確認 (docs/spec/filesystem.md#real-names)。確認済みの親ディレクトリをハンドルで開き、項目を列挙して ZIP の成分名と序数比較する。
// - 「そのディレクトリ直下で探す成分名の集合」を全ファイルエントリから先に集め、同じディレクトリは1回だけ列挙する。
// - 序数比較で完全一致した項目だけを記録し、照合しなかった名前は保持しない (RetainedNames で確認できる)。
// - 1回の列挙で同じ名前が複数回返った場合は最初の1件を採用する。返らなかった名前は見つからない (MISSING)。
// - 列挙用ハンドルのオープン・検証・列挙の失敗は FATAL。列挙を終えたら列挙用ハンドルを閉じる。
// ディレクトリはキー (target ルートからの成分を "\" で連結したもの、ルートは "") で識別する。
internal sealed class RealNameResolver
{
    private const uint FileAttributeReparsePoint = 0x400;

    private readonly IFileSystemProbe _probe;
    private readonly TargetRoot _root;
    private readonly Dictionary<string, HashSet<string>> _wanted;
    private readonly Dictionary<string, Dictionary<string, DirectoryItem>> _matched = new(StringComparer.Ordinal);

    public RealNameResolver(IFileSystemProbe probe, TargetRoot root, IEnumerable<ValidatedZipEntry> entries)
    {
        _probe = probe;
        _root = root;
        _wanted = CollectWantedNames(entries);
    }

    // 列挙を終えたディレクトリで記録している名前の全て (照合しなかった名前を含まないことの確認用)。
    public IEnumerable<string> RetainedNames => _matched.Values.SelectMany(d => d.Keys);

    // 列挙を終えたディレクトリのキー。
    public IEnumerable<string> EnumeratedDirectories => _matched.Keys;

    // ディレクトリキーごとに、その直下で探す成分名の集合を作る。ディレクトリエントリは target を調べないため含めない。
    public static Dictionary<string, HashSet<string>> CollectWantedNames(IEnumerable<ValidatedZipEntry> entries)
    {
        var wanted = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var key = string.Empty;
            for (var i = 0; i < entry.Components.Count; i++)
            {
                var name = entry.Components[i];
                if (!wanted.TryGetValue(key, out var names))
                {
                    names = new HashSet<string>(StringComparer.Ordinal);
                    wanted.Add(key, names);
                }

                names.Add(name);
                key = Child(key, name);
            }
        }

        return wanted;
    }

    // directory (確認済みのディレクトリ) の直下で name を探す。directoryItem はそのディレクトリを見つけた列挙項目
    // (ルートでは null)。列挙用ハンドルの検証に使う。
    public LookupResult Find(IReadOnlyList<string> directoryComponents, DirectoryItem? directoryItem, string name)
    {
        var key = string.Join('\\', directoryComponents);
        if (!_matched.TryGetValue(key, out var matches))
        {
            var enumerated = Enumerate(key, directoryComponents, directoryItem);
            if (enumerated.Fatal is { } fatal)
            {
                return LookupResult.Failed(fatal.Kind, fatal.Detail, fatal.Win32Error);
            }

            matches = enumerated.Matches!;
            _matched.Add(key, matches);
        }

        return matches.TryGetValue(name, out var item) ? LookupResult.Found(item) : LookupResult.NotFound;
    }

    private static string Child(string key, string name) => key.Length == 0 ? name : key + "\\" + name;

    private (Dictionary<string, DirectoryItem>? Matches, FatalError? Fatal) Enumerate(
        string key, IReadOnlyList<string> components, DirectoryItem? directoryItem)
    {
        var wanted = _wanted.GetValueOrDefault(key) ?? [];
        if (directoryItem is null)
        {
            // target ルートは保持しているハンドルをそのまま使う (docs/spec/filesystem.md#real-names の 1)。閉じない。
            return Collect(_root.Handle, wanted);
        }

        var path = _root.ExpectedPath(components);
        var opened = _probe.OpenDirectoryForEnumeration(path);
        if (!opened.Succeeded)
        {
            return (null, new FatalError(FatalKind.EnumerationOpenFailed, Detail: opened.Describe(), Win32Error: opened.Error));
        }

        using var handle = opened.Value;
        var info = handle.GetInfo();
        if (!info.Succeeded)
        {
            return (null, new FatalError(FatalKind.EnumerationHandleMismatch, Detail: info.Describe(), Win32Error: info.Error));
        }

        var expectedId = new VolumeFileId(_root.Id.VolumeSerialNumber, directoryItem.Value.FileId);
        var value = info.Value;
        if (value.Id != expectedId
            || !value.IsDirectory
            || (value.Attributes & FileAttributeReparsePoint) != 0
            || value.ReparseTag != 0
            || !string.Equals(value.FinalPath, path, StringComparison.Ordinal))
        {
            return (null, new FatalError(FatalKind.EnumerationHandleMismatch, Detail: SafeDetail(value, path)));
        }

        return Collect(handle, wanted);
    }

    private static (Dictionary<string, DirectoryItem>? Matches, FatalError? Fatal) Collect(IDirectoryHandle handle, HashSet<string> wanted)
    {
        var matches = new Dictionary<string, DirectoryItem>(StringComparer.Ordinal);
        var enumeration = handle.Enumerate();
        while (true)
        {
            var step = enumeration.Next();
            switch (step.Kind)
            {
                case DirectoryEnumerationStepKind.Item:
                    // 序数比較で完全一致した最初の1件だけを記録する。照合しない名前は保持しない。
                    if (wanted.Contains(step.Item.Name))
                    {
                        matches.TryAdd(step.Item.Name, step.Item);
                    }

                    break;

                case DirectoryEnumerationStepKind.End:
                    return (matches, null);

                default:
                    return (null, new FatalError(
                        FatalKind.EnumerationFailed,
                        Detail: $"{step.Operation} が失敗 (Win32 エラー {step.Error})", Win32Error: step.Error));
            }
        }
    }

    private static string SafeDetail(DirectoryHandleInfo info, string expectedPath) =>
        $"期待パス {expectedPath}、最終パス {info.FinalPath}、ディレクトリ {info.IsDirectory}、属性 0x{info.Attributes:X}、reparse tag 0x{info.ReparseTag:X}";
}

internal readonly record struct LookupResult(LookupKind Kind, DirectoryItem Item, FatalKind? FatalKind, string? Detail, int? Win32Error = null)
{
    public static LookupResult NotFound { get; } = new(LookupKind.NotFound, default, null, null);

    public static LookupResult Found(DirectoryItem item) => new(LookupKind.Found, item, null, null);

    public static LookupResult Failed(FatalKind kind, string? detail, int? win32Error = null) => new(LookupKind.Failed, default, kind, detail, win32Error);
}

internal enum LookupKind
{
    Found,
    NotFound,
    Failed,
}
