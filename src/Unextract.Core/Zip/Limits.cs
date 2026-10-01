namespace Unextract.Core.Zip;

// resource limits (SPEC §11)。値はここだけで定義する。境界は「上限以下を許可、超過を拒否」。
// 宣言展開量の上限は内容を読まない MISSING 相当を含む全ファイルエントリに適用する。
// 展開しないデータが危険だからではなく、target に触れる前に target の状態と無関係に受理を決め、
// 作成者が自由に書ける 64 ビットの宣言値による桁あふれを早期に拒否し、MVP の受理範囲を明確にするため
// (SPEC §11、DEC-6)。テストでは with 式で小さい値を注入する。
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

    // 初回分類の内容比較候補で実際に読んだ量の累計 (段階 C で使う)。
    public long MaxTotalReadLength { get; init; } = 68_719_476_736;
}
