using Unextract.Core.Display;
using Unextract.Core.Zip;

namespace Unextract.Core.Results;

// 原因となった ZIP エントリ。Index は ZIP 内の順序 (0 始まり)。
public sealed record ZipEntryRef(int Index, string Name)
{
    public int Number => Index + 1;

    public string DisplayName => SafeDisplay.Escape(Name);
}

// エントリの診断用段階。判定・削除順序は変更せず、判明した段階だけ保持する。
public enum EntryStep
{
    Resolve,
    Open,
    Verify,
    Inspect,
    Compare,
    FinalCheck,
    Dispose,
    Confirm,
}

// 全体 FATAL の原因。Entry は ZIP 全体の問題 (開けないなど) では null。
// Step と Win32Error は診断用。日本語の Detail を解析して復元しない。
// LibraryPath は RarLibraryUnavailable の読み込み元 (UnRAR64.dll の絶対パス) で、説明の案内に使う。
// Format は説明の形式名 (docs/spec/cli.md#archive-wording)。RAR の実行の失敗は Prepare・前進の失敗の出口で Rar にする。
public sealed record FatalError(FatalKind Kind, ZipEntryRef? Entry = null, string? Detail = null, EntryStep? Step = null, int? Win32Error = null, string? LibraryPath = null,
    ArchiveFormat Format = ArchiveFormat.Zip)
{
    public string Describe()
    {
        var reason = FatalKindText.Describe(Kind, Format);
        var text = Entry is null
            ? reason
            : $"エントリ #{Entry.Number} \"{Entry.DisplayName}\": {reason}";
        text = Detail is null ? text : $"{text} ({SafeDisplay.Escape(Detail)})";

        // DLL を利用できない場合の案内 (docs/spec/cli.md#input-errors)。
        return LibraryPath is null
            ? text
            : $"{text}。{SafeDisplay.EscapeForList(LibraryPath, out _)} に UnRAR.dll 7.23 (x64) の UnRAR64.dll を置いてください。ZIP の処理には影響しません。";
    }

    // UnRAR.dll を利用できない4つの原因 (docs/spec/rar.md#pinning) の FATAL。win32Description は LoadFailed のとき、version は VersionMismatch のときに使う。
    public static FatalError RarLibraryUnavailable(RarLibraryFailure failure, string libraryPath, string? win32Description = null, int? win32Error = null, int? version = null)
    {
        ArgumentNullException.ThrowIfNull(libraryPath);
        var reason = failure switch
        {
            RarLibraryFailure.NotFound => "見つかりません",
            RarLibraryFailure.HashMismatch => "版が一致しません (SHA-256)",
            RarLibraryFailure.LoadFailed => $"読み込めません ({win32Description ?? throw new ArgumentNullException(nameof(win32Description))})",
            RarLibraryFailure.VersionMismatch => $"版が一致しません (RARGetDllVersion={version ?? throw new ArgumentNullException(nameof(version))})",
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };
        return new FatalError(FatalKind.RarLibraryUnavailable, Detail: reason, Win32Error: win32Error, LibraryPath: libraryPath);
    }
}

// UnRAR.dll を利用できない原因 (docs/spec/rar.md#pinning)。
public enum RarLibraryFailure
{
    NotFound,
    HashMismatch,
    LoadFailed,
    VersionMismatch,
}
