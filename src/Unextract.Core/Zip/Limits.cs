namespace Unextract.Core.Zip;

// ZIP上限 (docs/spec/zip.md#limits) とentries上限 (docs/spec/cli.md#entries)。実装の値はここで定義する。境界は「上限以下を許可、超過を拒否」。
// 宣言展開量の上限は内容を読まない MISSING 相当を含む全ファイルエントリに適用する。
// 展開しないデータが危険だからではなく、target に触れる前に target の状態と無関係に受理を決め、
// 作成者が自由に書ける 64 ビットの宣言値による桁あふれを早期に拒否し、MVP の受理範囲を明確にするため
// (docs/spec/zip.md#limits、docs/RATIONALE.md#zip-limits)。テストでは with 式で小さい値を注入する。
public sealed record Limits
{
    public static Limits Default { get; } = new();

    public int MaxEntries { get; init; } = 100_000;

    // 復号後のエントリ名 (FullName) の UTF-16 コード単位数。
    public int MaxNameLength { get; init; } = 1_024;

    public int MaxDepth { get; init; } = 128;

    // 全エントリの「復号後の名前の UTF-16 バイト数 + MetadataBytesPerEntry」の合計。
    public long MaxMetadataBytes { get; init; } = 134_217_728;

    public int MetadataBytesPerEntry { get; init; } = 128;

    public long MaxEntryDeclaredLength { get; init; } = 17_179_869_184;

    public long MaxTotalDeclaredLength { get; init; } = 68_719_476_736;

    // RAR のファイルエントリの辞書サイズ (バイト。docs/spec/rar.md#limits の暫定値)。
    public long MaxRarDictionarySize { get; init; } = 1_073_741_824;

    // その実行の全バイト比較 (analyze・delete) で実際に読んだ量の累計。
    public long MaxTotalReadLength { get; init; } = 68_719_476_736;

    // --entries のファイルの大きさ (バイト)。超えるファイルは全体を読まない (docs/spec/cli.md#entries)。
    public long MaxEntriesFileBytes { get; init; } = 134_217_728;

    // --entries の1行のバイト数 (UTF-8、行末の改行を除く)。
    public int MaxEntriesLineBytes { get; init; } = 4_096;

    // --entries の行数。
    public int MaxEntriesLines { get; init; } = 100_000;
}
