using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Analysis;

internal enum ResolutionKind
{
    // 最終成分の列挙項目が見つかった。
    Found,

    // 親成分の分類表の MISSING の行、または最終成分が見つからない (SPEC §6.1 の手順1・2)。
    Missing,

    // 親成分が reparse point (SKIPPED_SPECIAL_FILE)。
    ParentReparse,

    // 判定不能 (analyze は FATAL、delete は STOP)。
    Failed,
}

// SPEC §6.1 の手順1・2 の結果。Found のとき Item は最終成分の列挙項目、ParentFileId は手順1でたどった親ディレクトリの File ID
// (target ルート直下なら target ルートの File ID、それ以外は親を見つけた列挙項目の File ID)。
internal readonly record struct Resolution(ResolutionKind Kind, DirectoryItem Item, FileId ParentFileId, FatalKind? FatalKind, string? Detail)
{
    public static Resolution Missing { get; } = new(ResolutionKind.Missing, default, default, null, null);

    public static Resolution ParentReparse { get; } = new(ResolutionKind.ParentReparse, default, default, null, null);

    public static Resolution Found(DirectoryItem item, FileId parentFileId) => new(ResolutionKind.Found, item, parentFileId, null, null);

    public static Resolution Fail(FatalKind kind, string? detail) => new(ResolutionKind.Failed, default, default, kind, detail);
}

// 親成分と最終成分の解決 (SPEC §6.1 の手順1・2、§6.2)。analyze と delete が共有する。
// 実名の確認は RealNameResolver に委ね、同じディレクトリは1回の実行で1回だけ列挙する (delete でも結果を再利用する。DEC-25)。
internal sealed class TargetResolver
{
    private const uint FileAttributeReparsePoint = 0x400;

    // 親成分の「想定外の種類」(SPEC §6.1 の表): FILE_ATTRIBUTE_DEVICE を持つ項目は NTFS のディレクトリ項目として扱えない。
    private const uint FileAttributeDevice = 0x40;
    private const uint FileAttributeDirectory = 0x10;

    private readonly TargetRoot _root;

    // entries は処理対象のエントリ (analyze は全エントリ、delete は全エントリまたは --entries で選んだもの)。
    // 列挙で探す名前はこのファイルエントリからだけ集める (SPEC §3.3、§6.2 の 3)。
    public TargetResolver(IFileSystemProbe probe, TargetRoot root, IEnumerable<ValidatedZipEntry> entries)
    {
        _root = root;
        Names = new RealNameResolver(probe, root, entries);
    }

    internal RealNameResolver Names { get; }

    public Resolution Resolve(ValidatedZipEntry entry)
    {
        var components = entry.Components;

        // 手順1: 途中の親成分。最初に該当した成分で分類を確定し、その先を読まない。
        DirectoryItem? directoryItem = null;
        for (var depth = 0; depth < components.Count - 1; depth++)
        {
            var lookup = Names.Find(Prefix(components, depth), directoryItem, components[depth]);
            switch (lookup.Kind)
            {
                case LookupKind.Failed:
                    return Resolution.Fail(lookup.FatalKind!.Value, lookup.Detail);
                case LookupKind.NotFound:
                    // 存在しない、または大小文字だけ違う (序数比較で一致しない)。
                    return Resolution.Missing;
            }

            var item = lookup.Item;
            if (IsReparse(item.Attributes, item.ReparseTag))
            {
                // reparse の判定は他の種類より優先する。reparse 先をたどらない。
                return Resolution.ParentReparse;
            }

            if ((item.Attributes & FileAttributeDevice) != 0)
            {
                return Resolution.Fail(FatalKind.UnexpectedTargetType, $"属性 0x{item.Attributes:X}");
            }

            if ((item.Attributes & FileAttributeDirectory) == 0)
            {
                // 通常ファイル: その下にファイルは存在し得ない。
                return Resolution.Missing;
            }

            directoryItem = item;
        }

        // 手順2: 最終成分の実名確認。
        var last = components.Count - 1;
        var final = Names.Find(Prefix(components, last), directoryItem, components[last]);
        return final.Kind switch
        {
            LookupKind.Failed => Resolution.Fail(final.FatalKind!.Value, final.Detail),
            LookupKind.NotFound => Resolution.Missing,
            _ => Resolution.Found(final.Item, directoryItem?.FileId ?? _root.Id.FileId),
        };
    }

    internal static bool IsReparse(uint attributes, uint reparseTag) =>
        (attributes & FileAttributeReparsePoint) != 0 || reparseTag != 0;

    private static string[] Prefix(IReadOnlyList<string> components, int count)
    {
        var prefix = new string[count];
        for (var i = 0; i < count; i++)
        {
            prefix[i] = components[i];
        }

        return prefix;
    }
}
