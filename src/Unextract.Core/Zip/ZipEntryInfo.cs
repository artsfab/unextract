namespace Unextract.Core.Zip;

// 事前検証が見る ZIP エントリの抽象。ZipArchive から ZipArchiveSource が作る。
// 検証ロジック (ZipPrevalidator) はこの型だけに依存し、偽のエントリ一覧でテストできる。
// Index は ZIP 内 (Central Directory) の順序 (0 始まり)。
public sealed record ZipEntryInfo(
    int Index,
    string FullName,
    long Length,
    int ExternalAttributes,
    bool IsEncrypted,
    uint Crc32);
