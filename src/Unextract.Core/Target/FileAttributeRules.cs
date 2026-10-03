namespace Unextract.Core.Target;

// target のファイル属性の許可集合 (docs/spec/filesystem.md#special-files、docs/RATIONALE.md#special-precheck)。Win32 に依存せず、属性値 (uint) だけで判定する。
// 許可リスト方式: 許可集合以外のビットが1つでもあれば特殊 (SKIPPED_SPECIAL_FILE) とする。
// 未定義・将来のビットも許可集合に含まれないため特殊になる。属性値の取得に失敗した場合の扱い (FATAL) は呼び出し側。
public static class FileAttributeRules
{
    public const uint Hidden = 0x2;
    public const uint Archive = 0x20;
    public const uint Normal = 0x80;
    public const uint SparseFile = 0x200;
    public const uint Compressed = 0x800;
    public const uint NotContentIndexed = 0x2000;
    public const uint Encrypted = 0x4000;

    public const uint AllowedMask = Hidden | Archive | Normal | SparseFile | Compressed | NotContentIndexed | Encrypted;

    public static bool IsSpecial(uint attributes) => (attributes & ~AllowedMask) != 0;
}
