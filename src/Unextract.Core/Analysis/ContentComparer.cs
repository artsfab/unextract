using System.IO.Hashing;
using Unextract.Core.Rar;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Analysis;

internal enum ContentVerdict
{
    Match,
    Mismatch,
    Fatal,

    // Fast: 内容を読んでいない (SAME_SIZE)。
    NotRead,
}

internal readonly record struct ContentOutcome(ContentVerdict Verdict, FatalKind? FatalKind = null, string? Detail = null, int? Win32Error = null)
{
    public static ContentOutcome Match { get; } = new(ContentVerdict.Match);

    public static ContentOutcome Mismatch { get; } = new(ContentVerdict.Mismatch);

    public static ContentOutcome NotRead { get; } = new(ContentVerdict.NotRead);

    public static ContentOutcome Fail(FatalKind kind, string? detail = null, int? win32Error = null) => new(ContentVerdict.Fatal, kind, detail, win32Error);
}

// エントリ内容の検証基準 (docs/spec/zip.md#verification) による比較。analyze と delete が同じ実装を共有し (docs/ARCHITECTURE.md#dependencies)、1回の実行で1つのインスタンスを使う。
// 違反時の扱い (analyze は FATAL / MODIFIED、delete は STOP / MODIFIED) は呼び出し側が決める。各エントリの内容は1回の実行で高々1回しか
// 読まない (Strict の delete でも比較は1回)。実測展開量の累計 (docs/spec/zip.md#limits) はその実行の全ての比較で数える。
// 1. IsEncrypted が false (Open() の前に確認)
// 2. Open() と読み取り中に例外が発生しない (種類を問わない)
// 3. 読み出したバイト数が Length を超えた時点で直ちに異常 (それ以上読まない)
// 4. 終端までのバイト数が Length と等しい
// 5. 読み出した全バイトの CRC-32 が Crc32 と等しい
// 6. 固定サイズのバッファで target と全バイト一致
// ZIP ストリームは一致・不一致にかかわらず終端まで読み切る。target は不一致を確定した後は読まない。
public sealed class ContentComparer
{
    public const int BufferSize = 64 * 1024;

    private readonly Limits _limits;
    private readonly byte[] _zipBuffer = new byte[BufferSize];
    private readonly byte[] _targetBuffer = new byte[BufferSize];

    internal ContentComparer(Limits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
    }

    // その実行の全バイト比較で実際に読んだ量の累計 (docs/spec/zip.md#limits)。
    public long TotalRead { get; private set; }

    // 全バイト比較 (Compare) の回数 (内容比較候補ごとに1回であることの確認用)。
    internal int Comparisons { get; private set; }

    // 内容比較候補の内容検証 (docs/spec/filesystem.md#resolution の手順8、docs/spec/filesystem.md#delete-flow の手順6)。analyze と delete が共有し、Strict と Fast の処理差はここの1か所だけ
    // (docs/RATIONALE.md#fast)。Strict はエントリ内容の検証基準 (docs/spec/zip.md#verification) で同じハンドルから読んで比較する。Fast は ZIP エントリも target も読まずに
    // NotRead (SAME_SIZE) を返す。Fast で docs/spec/zip.md#verification の FATAL・STOP と実測展開量の計上が起きないのは、Compare を呼ばないことの帰結。
    // RAR (session あり) は DLL が押し込む内容を同じ基準で検証する (docs/spec/rar.md#verification)。
    internal ContentOutcome Verify(RunMode mode, IZipContentProvider contents, IRarReadSession? session, int index, IComparisonHandle target) =>
        mode == RunMode.Fast
            ? ContentOutcome.NotRead
            : session is null ? Compare(contents.GetContent(index), target) : Compare(session.GetContent(index), target);

    internal ContentOutcome Compare(IZipEntryContent content, IComparisonHandle target)
    {
        Comparisons++;

        if (content.IsEncrypted)
        {
            return ContentOutcome.Fail(FatalKind.ContentEncrypted);
        }

        var length = content.Length;
        Stream stream;
        try
        {
            stream = content.Open();
        }
        catch (Exception ex)
        {
            return ContentOutcome.Fail(FatalKind.ContentReadFailed, $"{ex.GetType().Name}: {ex.Message}");
        }

        var verifier = Begin(length, content.Crc32, target);

        // 例外を「異常」として扱うのは ZIP ストリームの操作だけ。target の読み取りは ProbeResult で失敗を返す
        // (target 側の例外は捕まえずに伝える。比較用ハンドルは呼び出し側の using で閉じられる)。
        try
        {
            while (true)
            {
                // Length を1バイト超えた時点で検出できる量だけを要求する。
                var request = (int)Math.Min(BufferSize, length - verifier.Read + 1);
                int n;
                try
                {
                    n = stream.Read(_zipBuffer, 0, request);
                }
                catch (Exception ex)
                {
                    return ContentOutcome.Fail(FatalKind.ContentReadFailed, $"{ex.GetType().Name}: {ex.Message}");
                }

                if (n == 0)
                {
                    break;
                }

                if (verifier.Append(_zipBuffer.AsSpan(0, n)) is { } aborted)
                {
                    return aborted;
                }
            }
        }
        finally
        {
            DisposeQuietly(stream);
        }

        return verifier.Complete();
    }

    // RAR の内容検証 (docs/spec/rar.md#verification)。基準1 (暗号化) は Prepare で拒否済み。判定は RAR_TEST が成功を返した後で確定し、
    // 検証器が記録した中止 → RAR_TEST の失敗 (CONTENT_READ_FAILED) → 不足 → CRC-32 (種類が CRC-32 のとき) → 全バイト一致 の順に判定する。
    // 中止しない限り DLL のデータは終端まで受け取る。DLL の呼び出しやコールバック内の例外 (target 側を含む) は捕まえずに伝える。
    internal ContentOutcome Compare(IRarEntryContent content, IComparisonHandle target)
    {
        Comparisons++;

        var verifier = Begin(content.Length, content.ExpectedCrc32, target);
        var test = content.Test(chunk => verifier.Append(chunk) is null);
        if (verifier.Aborted is { } aborted)
        {
            return aborted;
        }

        if (!test.Succeeded)
        {
            return ContentOutcome.Fail(FatalKind.ContentReadFailed, test.Detail);
        }

        return verifier.Complete();
    }

    // 1エントリの内容検証 (基準3〜6) を始める。expectedCrc が null なら CRC-32 を照合しない。読み方 (ZIP の pull、RAR の押し込み) と
    // 基準1・2 (暗号化、読み取りの失敗) は呼び出し側が扱う。
    internal ContentVerifier Begin(long length, uint? expectedCrc, IComparisonHandle target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new ContentVerifier(this, length, expectedCrc, target);
    }

    // 読み終えた (または中断した) 展開ストリームを閉じる。判定に必要な読み取りは済んでいるため、閉じるときの例外は判定に使わない。
    private static void DisposeQuietly(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
        }
    }

    // target から count (BufferSize 以下) バイトを _targetBuffer に読む。終端で短くなる。
    private (int Count, ContentOutcome? Fatal) ReadTarget(IComparisonHandle target, int count)
    {
        var filled = 0;
        while (filled < count)
        {
            var result = target.Read(_targetBuffer.AsSpan(filled, count - filled));
            if (!result.Succeeded)
            {
                return (filled, ContentOutcome.Fail(FatalKind.TargetReadFailed, result.Describe(), result.Error));
            }

            if (result.Value == 0)
            {
                break;
            }

            filled += result.Value;
        }

        return (filled, null);
    }

    // 1エントリの内容検証の状態 (基準3〜6)。チャンクは任意の長さ (0 を含む) を受け付け、どの分け方でも判定は同じになる。
    // 中止 (超過、実測累計の超過、target の読み取り失敗) の後は何もせず同じ中止を返す。1回の実行で同時に1つだけ使う (比較用バッファを共有する)。
    internal sealed class ContentVerifier
    {
        private readonly ContentComparer _owner;
        private readonly long _length;
        private readonly uint? _expectedCrc;
        private readonly IComparisonHandle _target;
        private readonly Crc32 _crc = new();
        private bool _mismatch;

        internal ContentVerifier(ContentComparer owner, long length, uint? expectedCrc, IComparisonHandle target)
        {
            _owner = owner;
            _length = length;
            _expectedCrc = expectedCrc;
            _target = target;
        }

        // 受け取ったバイト数 (超過したチャンクは数えない)。
        public long Read { get; private set; }

        // 記録した中止の理由。中止していなければ null。
        public ContentOutcome? Aborted { get; private set; }

        // チャンクを検証に加える。中止したら理由を返す (以後のチャンクは受け取らない)。
        public ContentOutcome? Append(ReadOnlySpan<byte> chunk)
        {
            if (Aborted is { } aborted)
            {
                return aborted;
            }

            if (chunk.IsEmpty)
            {
                return null;
            }

            // 3. 宣言サイズの超過はチャンク全体の長さで判定し、超過したチャンクのどのバイトも CRC・比較に使わない。
            if (chunk.Length > _length - Read)
            {
                return Abort(ContentOutcome.Fail(FatalKind.ContentTooLong, $"宣言 {_length} バイト"));
            }

            Read += chunk.Length;

            // 実測展開量の累計 (docs/spec/zip.md#limits)。
            _owner.TotalRead += chunk.Length;
            if (_owner.TotalRead > _owner._limits.MaxTotalReadLength)
            {
                return Abort(ContentOutcome.Fail(FatalKind.TotalReadLengthTooLarge));
            }

            // 5. CRC-32 は不一致の確定後も全バイトで計算する。
            _crc.Append(chunk);

            // 6. 不一致が未確定なら、BufferSize 以下に分けて target と比較する。target は不一致の確定後は読まない。
            while (!_mismatch && !chunk.IsEmpty)
            {
                var piece = chunk[..Math.Min(chunk.Length, BufferSize)];
                var targetResult = _owner.ReadTarget(_target, piece.Length);
                if (targetResult.Fatal is { } fatal)
                {
                    return Abort(fatal);
                }

                _mismatch = targetResult.Count != piece.Length || !piece.SequenceEqual(_owner._targetBuffer.AsSpan(0, piece.Length));
                chunk = chunk[piece.Length..];
            }

            return null;
        }

        // 終端に達した後の判定 (中止 → 4. 不足 → 5. CRC-32 → 6. target の終端と全バイト一致)。
        public ContentOutcome Complete()
        {
            if (Aborted is { } aborted)
            {
                return aborted;
            }

            if (Read != _length)
            {
                return ContentOutcome.Fail(FatalKind.ContentTooShort, $"宣言 {_length} バイト、実際 {Read} バイト");
            }

            if (_expectedCrc is { } expected && _crc.GetCurrentHashAsUInt32() != expected)
            {
                return ContentOutcome.Fail(FatalKind.ContentCrcMismatch);
            }

            if (!_mismatch)
            {
                // target がまだ続くなら不一致 (サイズは事前に一致を確認しているが、読み取り結果で確かめる)。
                var tail = _owner.ReadTarget(_target, 1);
                if (tail.Fatal is { } fatal)
                {
                    return fatal;
                }

                _mismatch = tail.Count != 0;
            }

            return _mismatch ? ContentOutcome.Mismatch : ContentOutcome.Match;
        }

        private ContentOutcome Abort(ContentOutcome outcome)
        {
            Aborted = outcome;
            return outcome;
        }
    }
}
