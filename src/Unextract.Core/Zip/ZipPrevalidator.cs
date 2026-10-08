using Unextract.Core.Results;

namespace Unextract.Core.Zip;

// 事前検証を通過したエントリ。Components は区切りを除いた成分 (ディレクトリの末尾区切りを含まない)。
public sealed record ValidatedZipEntry(ZipEntryInfo Entry, bool IsDirectory, IReadOnlyList<string> Components);

public sealed class ZipPrevalidationResult
{
    private ZipPrevalidationResult(FatalError? fatal, IReadOnlyList<ValidatedZipEntry> entries, long declaredTotal)
    {
        Fatal = fatal;
        Entries = entries;
        DeclaredTotalLength = declaredTotal;
    }

    public FatalError? Fatal { get; }

    public bool Passed => Fatal is null;

    // 通過時だけ ZIP 内の順序で全エントリを持つ。FATAL 時は空。
    public IReadOnlyList<ValidatedZipEntry> Entries { get; }

    public long DeclaredTotalLength { get; }

    internal static ZipPrevalidationResult Pass(IReadOnlyList<ValidatedZipEntry> entries, long declaredTotal) =>
        new(null, entries, declaredTotal);

    internal static ZipPrevalidationResult Fail(FatalError fatal) => new(fatal, [], 0);
}

// ZIP 事前検証 (docs/spec/zip.md#names の事前検証)。全エントリの名前・種類・重複と衝突・resource limits を検査する。
// target に触れず、入力はエントリの一覧だけなので target の状態に依存しない。
// エントリを ZIP 内の順序で1件ずつ検査し、最初の FATAL で打ち切る。1エントリ内の検査順も固定のため、
// どのエントリのどの原因で FATAL になるかは ZIP 内の順序で決定的に決まる。
public static class ZipPrevalidator
{
    public static ZipPrevalidationResult Validate(IEnumerable<ZipEntryInfo> entries, Limits limits)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(limits);

        var state = new State(limits);
        var validated = new List<ValidatedZipEntry>();
        foreach (var entry in entries)
        {
            var result = state.Check(entry);
            if (result.Error is { } kind)
            {
                return ZipPrevalidationResult.Fail(new FatalError(kind, new ZipEntryRef(entry.Index, entry.FullName)));
            }

            validated.Add(result.Entry!);
        }

        return ZipPrevalidationResult.Pass(validated, (long)state.DeclaredTotal);
    }

    private readonly record struct EntryResult(ValidatedZipEntry? Entry, FatalKind? Error);

    private sealed class State(Limits limits)
    {
        private readonly ZipStructure _structure = new();
        private readonly EntryBudget _budget = new(limits);

        // 合計は桁あふれしない型で加算する。
        public Int128 DeclaredTotal { get; private set; }

        public EntryResult Check(ZipEntryInfo entry)
        {
            var name = entry.FullName;

            // エントリ総数・名前長・メタデータ総量。RAR では列挙中に同じ検査を通過済みで、ここでは必ず通る。
            if (_budget.Add(name.Length) is { } budgetError)
            {
                return Fail(budgetError);
            }

            // 不正な UTF-8 は例外にならず U+FFFD に置換されるため、復号後の名前で検出する (docs/spec/zip.md#decoding)。
            if (name.Contains('\uFFFD'))
            {
                return Fail(FatalKind.NameContainsReplacementCharacter);
            }

            var path = EntryPath.Parse(name);
            if (path.Error is { } pathError)
            {
                return Fail(pathError);
            }

            if (path.Components.Length > limits.MaxDepth)
            {
                return Fail(FatalKind.PathTooDeep);
            }

            // ZIP の特殊エントリ (docs/spec/zip.md#types、docs/RATIONALE.md#real-names)。RAR のエントリの種別・属性は RarPrevalidator が検査済み
            // (docs/spec/rar.md#types)。
            if (entry.Rar is null && ZipEntryTypeRules.Check(entry, path.IsDirectory) is { } typeError)
            {
                return Fail(typeError);
            }

            // 宣言展開量は MISSING 相当を含む全ファイルエントリに適用する (docs/spec/zip.md#limits、docs/RATIONALE.md#zip-limits)。
            // ディレクトリエントリの Length は上 (RAR は RarPrevalidator) で 0 であることを確認済み。
            if (!path.IsDirectory)
            {
                if (entry.Length < 0)
                {
                    return Fail(FatalKind.InvalidDeclaredLength);
                }

                if (entry.Length > limits.MaxEntryDeclaredLength)
                {
                    return Fail(FatalKind.EntryTooLarge);
                }

                DeclaredTotal += entry.Length;
                if (DeclaredTotal > limits.MaxTotalDeclaredLength)
                {
                    return Fail(FatalKind.TotalDeclaredLengthTooLarge);
                }
            }

            if (_structure.Add(path.Components, path.IsDirectory) is { } structureError)
            {
                return Fail(structureError);
            }

            return new EntryResult(new ValidatedZipEntry(entry, path.IsDirectory, path.Components), null);
        }

        private static EntryResult Fail(FatalKind kind) => new(null, kind);
    }
}
