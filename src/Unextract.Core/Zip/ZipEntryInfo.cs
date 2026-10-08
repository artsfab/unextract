namespace Unextract.Core.Zip;

// 事前検証が見る ZIP エントリの抽象。ZipArchive から ZipArchiveSource が作る。
// 検証ロジック (ZipPrevalidator) はこの型だけに依存し、偽のエントリ一覧でテストできる。
// Index は ZIP 内 (Central Directory) の順序 (0 始まり)。
// RAR のエントリ (docs/SPEC.md#archive-terms) も同じ型で表し、Rar に RAR 固有の情報を持つ (ZIP では null)。
public sealed record ZipEntryInfo(
    int Index,
    string FullName,
    long Length,
    int ExternalAttributes,
    bool IsEncrypted,
    uint Crc32,
    RarEntryMetadata? Rar = null);

// DLL が報告する作成元 OS (docs/spec/rar.md#types)。Windows・Unix 以外は Other。
public enum RarHostOs
{
    Windows,
    Unix,
    Other,
}

// ファイルエントリのハッシュの種類 (docs/spec/rar.md#listing、docs/spec/rar.md#verification)。
public enum RarHashType
{
    None,
    Crc32,
    Blake2,
}

// RAR のエントリの受理規則 (docs/spec/rar.md#listing、docs/spec/rar.md#types、docs/spec/rar.md#limits) が見る情報。DLL の型・定数は持たない
// (変換は Unextract.Windows が行う)。Attributes は作成元 OS に応じて DOS 属性または Unix の mode の生の値。DictionarySize はバイト数。
// IsSplit は前後の巻へ続くフラグのどちらか。暗号化は ZipEntryInfo.IsEncrypted、CRC-32 の値は ZipEntryInfo.Crc32 に入れる。
public sealed record RarEntryMetadata(
    RarHostOs HostOs,
    uint Attributes,
    bool IsDirectory,
    bool IsSolid,
    bool IsSplit,
    RarHashType HashType,
    long DictionarySize,
    uint RedirectionType);
