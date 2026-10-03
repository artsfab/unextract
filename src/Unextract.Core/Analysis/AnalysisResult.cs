using Unextract.Core.Results;

namespace Unextract.Core.Analysis;

// SKIPPED_SPECIAL_FILE の理由 (SPEC §6.1 の親成分の表、§7)。表示の分類は変えない。
public enum SkipReason
{
    ParentReparsePoint,
    Directory,
    ReparsePoint,
    HardLink,
    AlternateDataStream,
    ArchiveItself,
    Attributes,
}

// 判定済みの1エントリ。Target は target 内の対応する場所 (期待パス、\\?\ 形式)。MISSING では実在しない期待位置。
// 表示 (SPEC §10.1) だけに使い、delete の入力にしない。
public sealed record EntryResult(ZipEntryRef Entry, string Target, Classification Classification, SkipReason? SkipReason = null);

// analyze の結果 (SPEC §3.4、§10.2)。結果は ZIP 内の順序で決定的。削除候補・スナップショットを持たない (削除の許可証にしない)。
// FATAL 時は、最初の FATAL の直前までが判定済み、FATAL の原因エントリ、それ以降が未判定。
public sealed class AnalysisResult
{
    internal AnalysisResult(int totalEntries, IReadOnlyList<EntryResult> results, FatalError? fatal, bool fatalBeforeClassification = false)
    {
        TotalEntries = totalEntries;
        Results = results;
        Fatal = fatal;
        FatalBeforeClassification = fatalBeforeClassification;

        // target に触れる前の FATAL (ZIP 事前検証など) では、原因のエントリも判定していないため全エントリが未判定。
        var judged = fatalBeforeClassification ? 0 : results.Count + (fatal?.Entry is null ? 0 : 1);
        UnclassifiedCount = totalEntries - judged;
    }

    public int TotalEntries { get; }

    // 判定済みのエントリ (ZIP 内の順序)。
    public IReadOnlyList<EntryResult> Results { get; }

    public FatalError? Fatal { get; }

    public bool Completed => Fatal is null;

    // FATAL が target に触れる前 (Prepare の ZIP 事前検証、ZIP 自身の File ID の取得失敗) のものか。
    public bool FatalBeforeClassification { get; }

    // 判定済みでも FATAL の原因でもないエントリの件数 (表示はパスを列挙せず件数だけ)。
    public int UnclassifiedCount { get; }

    public int Count(Classification classification) => Results.Count(r => r.Classification == classification);

    // target に触れる前の FATAL (ZIP 事前検証、ZIP 自身の File ID の取得失敗など)。判定済みは0件。
    public static AnalysisResult BeforeClassification(int totalEntries, FatalError fatal)
    {
        ArgumentNullException.ThrowIfNull(fatal);
        return new AnalysisResult(totalEntries, [], fatal, fatalBeforeClassification: true);
    }
}
