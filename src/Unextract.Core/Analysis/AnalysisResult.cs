using Unextract.Core.Results;
using Unextract.Core.Target;

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

// 判定済みの1エントリ。
public sealed record EntryResult(ZipEntryRef Entry, Classification Classification, SkipReason? SkipReason = null);

// 再検証用スナップショット (SPEC §8.2)。MATCHED の比較用ハンドルを閉じる前に同じハンドルから記録する。
// Directory と DeletePending が false であることは MATCHED の前提 (記録しない)。
public sealed record TargetSnapshot(
    ulong VolumeSerialNumber,
    FileId FileId,
    FileId ParentFileId,
    long EndOfFile,
    long LastWriteTime,
    long ChangeTime,
    uint Attributes,
    uint NumberOfLinks,
    IReadOnlyList<StreamEntry> Streams,
    uint ReparseTag,
    string FinalPath);

// 削除フェーズに渡す MATCHED。ExpectedPath は削除用ハンドルを開く期待パス (\\?\ 形式)。
public sealed record MatchedFile(ZipEntryRef Entry, string ExpectedPath, TargetSnapshot Snapshot);

// 初回分類の結果 (SPEC §3 の 3〜5、§10)。結果は ZIP 内の順序で決定的。
// FATAL 時は、最初の FATAL の直前までが判定済み、FATAL の原因エントリ、それ以降が未判定。
public sealed class AnalysisResult
{
    private readonly IReadOnlyList<MatchedFile> _matched;

    internal AnalysisResult(int totalEntries, IReadOnlyList<EntryResult> results, IReadOnlyList<MatchedFile> matched, FatalError? fatal)
    {
        TotalEntries = totalEntries;
        Results = results;
        _matched = matched;
        Fatal = fatal;

        var judged = results.Count + (fatal?.Entry is null ? 0 : 1);
        UnclassifiedCount = totalEntries - judged;
    }

    public int TotalEntries { get; }

    // 判定済みのエントリ (ZIP 内の順序)。
    public IReadOnlyList<EntryResult> Results { get; }

    public FatalError? Fatal { get; }

    public bool Completed => Fatal is null;

    // 判定済みでも FATAL の原因でもないエントリの件数 (表示はパスを列挙せず件数だけ)。
    public int UnclassifiedCount { get; }

    // 削除候補。削除開始前の FATAL があれば、判定済みの MATCHED があっても空 (削除0件、SPEC §3)。
    public IReadOnlyList<MatchedFile> DeletionCandidates => Completed ? _matched : [];

    public int Count(Classification classification) => Results.Count(r => r.Classification == classification);

    // target に触れる前の FATAL (ZIP 事前検証、ZIP 自身の File ID の取得失敗など)。判定済みは0件。
    public static AnalysisResult BeforeClassification(int totalEntries, FatalError fatal)
    {
        ArgumentNullException.ThrowIfNull(fatal);
        return new AnalysisResult(totalEntries, [], [], fatal);
    }
}

// 削除フェーズの結果 (SPEC §8.4、§10)。NotProcessedCount は停止の原因の対象より後の、処理しなかった件数。
public sealed record DeleteFailure(ZipEntryRef Entry, string Reason);

// 以後の削除を止めた原因。PossiblyDeleted は成立確認ができなかった場合 (「削除された可能性あり」)。
public sealed record DeletionStop(ZipEntryRef Entry, string Reason, bool PossiblyDeleted);

public sealed record DeletionReport(
    IReadOnlyList<ZipEntryRef> Deleted,
    IReadOnlyList<DeleteFailure> Failed,
    DeletionStop? Stop,
    int NotProcessedCount);
