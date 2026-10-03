using System.IO.Hashing;
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

internal readonly record struct ContentOutcome(ContentVerdict Verdict, FatalKind? FatalKind = null, string? Detail = null)
{
    public static ContentOutcome Match { get; } = new(ContentVerdict.Match);

    public static ContentOutcome Mismatch { get; } = new(ContentVerdict.Mismatch);

    public static ContentOutcome NotRead { get; } = new(ContentVerdict.NotRead);

    public static ContentOutcome Fail(FatalKind kind, string? detail = null) => new(ContentVerdict.Fatal, kind, detail);
}

// エントリ内容の検証基準 (SPEC §5.2) による比較。analyze と delete が同じ実装を共有し (PLAN.md §1)、1回の実行で1つのインスタンスを使う。
// 違反時の扱い (analyze は FATAL / MODIFIED、delete は STOP / MODIFIED) は呼び出し側が決める。各エントリの内容は1回の実行で高々1回しか
// 読まない (Strict の delete でも比較は1回)。実測展開量の累計 (SPEC §11) はその実行の全ての比較で数える。
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

    // その実行の全バイト比較で実際に読んだ量の累計 (SPEC §11)。
    public long TotalRead { get; private set; }

    // 全バイト比較 (Compare) の回数 (内容比較候補ごとに1回であることの確認用)。
    internal int Comparisons { get; private set; }

    // 内容比較候補の内容検証 (SPEC §6.1 の手順8、§8.3 の手順6)。analyze と delete が共有し、Strict と Fast の処理差はここの1か所だけ
    // (DEC-34)。Strict はエントリ内容の検証基準 (§5.2) で同じハンドルから読んで比較する。Fast は ZIP エントリも target も読まずに
    // NotRead (SAME_SIZE) を返す。Fast で §5.2 の FATAL・STOP と実測展開量の計上が起きないのは、Compare を呼ばないことの帰結。
    internal ContentOutcome Verify(RunMode mode, IZipContentProvider contents, int index, IComparisonHandle target) =>
        mode == RunMode.Fast ? ContentOutcome.NotRead : Compare(contents.GetContent(index), target);

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

        var crc = new Crc32();
        long read = 0;
        var mismatch = false;

        // 例外を「異常」として扱うのは ZIP ストリームの操作だけ。target の読み取りは ProbeResult で失敗を返す
        // (target 側の例外は捕まえずに伝える。比較用ハンドルは呼び出し側の using で閉じられる)。
        try
        {
            while (true)
            {
                // Length を1バイト超えた時点で検出できる量だけを要求する。
                var request = (int)Math.Min(BufferSize, length - read + 1);
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

                read += n;
                if (read > length)
                {
                    return ContentOutcome.Fail(FatalKind.ContentTooLong, $"宣言 {length} バイト");
                }

                TotalRead += n;
                if (TotalRead > _limits.MaxTotalReadLength)
                {
                    return ContentOutcome.Fail(FatalKind.TotalReadLengthTooLarge);
                }

                var chunk = _zipBuffer.AsSpan(0, n);
                crc.Append(chunk);

                if (!mismatch)
                {
                    var targetResult = ReadTarget(target, n);
                    if (targetResult.Fatal is { } fatal)
                    {
                        return fatal;
                    }

                    mismatch = targetResult.Count != n || !chunk.SequenceEqual(_targetBuffer.AsSpan(0, n));
                }
            }
        }
        finally
        {
            DisposeQuietly(stream);
        }

        if (read != length)
        {
            return ContentOutcome.Fail(FatalKind.ContentTooShort, $"宣言 {length} バイト、実際 {read} バイト");
        }

        if (crc.GetCurrentHashAsUInt32() != content.Crc32)
        {
            return ContentOutcome.Fail(FatalKind.ContentCrcMismatch);
        }

        if (!mismatch)
        {
            // target がまだ続くなら不一致 (サイズは事前に一致を確認しているが、読み取り結果で確かめる)。
            var tail = ReadTarget(target, 1);
            if (tail.Fatal is { } fatal)
            {
                return fatal;
            }

            mismatch = tail.Count != 0;
        }

        return mismatch ? ContentOutcome.Mismatch : ContentOutcome.Match;
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

    // target から count バイトを _targetBuffer に読む。終端で短くなる。
    private (int Count, ContentOutcome? Fatal) ReadTarget(IComparisonHandle target, int count)
    {
        var filled = 0;
        while (filled < count)
        {
            var result = target.Read(_targetBuffer.AsSpan(filled, count - filled));
            if (!result.Succeeded)
            {
                return (filled, ContentOutcome.Fail(FatalKind.TargetReadFailed, result.Describe()));
            }

            if (result.Value == 0)
            {
                break;
            }

            filled += result.Value;
        }

        return (filled, null);
    }
}
