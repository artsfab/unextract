using Unextract.Core.Results;
using Unextract.Core.Zip;

namespace Unextract.Core.Rar;

// 一覧用に開いた時点のアーカイブのフラグのうち、Prepare の手順6で検査するもの (docs/spec/rar.md#listing)。
// ボリュームのフラグは開いた直後に手順2で拒否するので、ここには来ない。
public sealed record RarArchiveInfo(bool IsSolid, bool HasEncryptedHeaders);

// RAR のアーカイブソース (docs/SPEC.md#prepare の手順2)。Entries は DLL が列挙したエントリ (各エントリに Rar を持つ)。
// 内容は pull では読まない (GetContent は常に例外)。Strict では OpenSession で内容読み取り用のセッションを作る (手順10)。
// 実体は Unextract.Windows が UnRAR.dll で作る。Core は DLL に依存しない。
public interface IRarArchiveSource : IArchiveSource
{
    RarArchiveInfo Archive { get; }

    // 手順10: 内容読み取り用に開く。ヘッダーは読まない。失敗は ARCHIVE_OPEN_FAILED の FATAL。
    // セッションは列挙結果 (ヘッダー照合用の値) を持つこのソース自身が作り、別のアーカイブのソースと組み合わせない。
    RarSessionOpenResult OpenSession();
}

public sealed record RarSessionOpenResult(IRarReadSession? Session, FatalError? Fatal);

// 内容読み取りのセッション (docs/spec/rar.md#session)。Prepared が唯一所有し、Analyzer・Deleter は借用する。単一スレッドで使う。
public interface IRarReadSession : IDisposable
{
    // index (アーカイブの順、0 始まり) のエントリのヘッダーまで前進し、通過・前進先のヘッダーを Prepare の列挙と照合する。
    // 前進先はアーカイブの順に単調増加で指定する。
    RarAdvanceResult Advance(int index);

    // 前進先の内容。前進先に立っていてまだテストしていない場合だけ有効で、それ以外は例外 (製品の誤配線)。
    IRarEntryContent GetContent(int index);
}

// 前進の結果。成功なら FailureKind は null で LastVerifiedIndex は前進先。失敗なら FailureKind は ArchiveChanged か ArchiveUnreadable、
// DetectedAt は実際に失敗を検出した位置、Code は DLL のコード名など (診断用)。LastVerifiedIndex はヘッダーの照合を終えた最後のエントリ
// (照合後の RAR_SKIP の失敗ではそのエントリを含む。照合したエントリが無ければ -1)。
public readonly record struct RarAdvanceResult(FatalKind? FailureKind, ZipEntryRef? DetectedAt, string? Code, int LastVerifiedIndex)
{
    public bool Succeeded => FailureKind is null;

    public static RarAdvanceResult Ok(int index) => new(null, null, null, index);

    // 前進先の FATAL・STOP の説明に付ける検出位置 (docs/spec/rar.md#session)。名前は説明全体とともに表示時にエスケープされる
    // (説明の中の " はエスケープされるため、名前は「」で囲む)。
    public string? Describe() => FailureKind is null
        ? null
        : Code is null
            ? $"検出位置: エントリ #{DetectedAt!.Number}「{DetectedAt.Name}」"
            : $"検出位置: エントリ #{DetectedAt!.Number}「{DetectedAt.Name}」、{Code}";
}

// 押し込み型の内容 (docs/spec/rar.md#verification)。Test は DLL の RAR_TEST で内容をチャンクごとに sink へ渡す。
// sink が false を返したら DLL を中止させる。
public interface IRarEntryContent
{
    // 宣言展開サイズ。
    long Length { get; }

    // ハッシュの種類が CRC-32 ならその値。BLAKE2 なら null (照合は DLL に委ねる)。
    uint? ExpectedCrc32 { get; }

    RarTestResult Test(ContentSink sink);
}

public delegate bool ContentSink(ReadOnlySpan<byte> chunk);

// RAR_TEST の結果。失敗なら Detail に DLL のコード名を入れる。
public readonly record struct RarTestResult(bool Succeeded, string? Detail)
{
    public static RarTestResult Success { get; } = new(true, null);

    public static RarTestResult Failed(string detail) => new(false, detail);
}
