using Unextract.Core.Results;

namespace Unextract.Core.Zip;

// 列挙の予算: エントリ総数・名前長・メタデータ総量 (docs/spec/zip.md#limits、docs/spec/rar.md#limits)。検査順は件数 → 名前長 → メタデータ総量。
// 判定の計算だけを共有し、いつ呼ぶかは形式ごとに決める。ZIP は事前検証 (docs/SPEC.md#prepare の手順6) の各エントリで、
// RAR は列挙中 (手順2) に各エントリを保持する前に、1エントリにつき1回呼ぶ。名前長は FullName (ディレクトリの末尾の区切りを含む) の UTF-16 の長さ。
public sealed class EntryBudget(Limits limits)
{
    private long _count;

    // 合計は桁あふれしない型で加算する。
    private Int128 _metadataBytes;

    // エントリを1件加える。超過したら原因を返す (以後は呼ばない)。
    public FatalKind? Add(int nameLength)
    {
        _count++;
        if (_count > limits.MaxEntries)
        {
            return FatalKind.TooManyEntries;
        }

        if (nameLength > limits.MaxNameLength)
        {
            return FatalKind.NameTooLong;
        }

        _metadataBytes += ((Int128)nameLength * sizeof(char)) + limits.MetadataBytesPerEntry;
        if (_metadataBytes > limits.MaxMetadataBytes)
        {
            return FatalKind.MetadataTooLarge;
        }

        return null;
    }
}
