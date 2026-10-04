using Unextract.Gui.Models;

namespace Unextract.Gui.ViewModels;

// Everything a delete batch may do, fixed when the plan is made: mode, targets, the analysis snapshot each
// candidate list came from. Display filters, later selection changes and later analyses never alter it.
// Hidden records only that the archive filter hid this selected target when planning, so the confirmation says so.
internal sealed record PlannedTarget(int Number, TargetViewModel Target, string ArchivePath, string TargetPath,
    CliMode Mode, AnalysisSnapshot Snapshot, IReadOnlyList<CliEntry> Candidates, UInt128 CandidateLength, bool Hidden = false);

internal sealed record ExcludedTarget(string ArchivePath, string TargetPath, string Reason);

internal sealed class DeletionPlan(CliMode mode, IReadOnlyList<PlannedTarget> items, IReadOnlyList<ExcludedTarget> excluded)
{
    public CliMode Mode { get; } = mode;
    public IReadOnlyList<PlannedTarget> Items { get; } = items;
    public IReadOnlyList<ExcludedTarget> Excluded { get; } = excluded;
    public long TotalCandidates { get; } = items.Sum(i => (long)i.Candidates.Count);
    public int HiddenCount { get; } = items.Count(i => i.Hidden);
    public UInt128 TotalLength { get; } = items.Aggregate(UInt128.Zero, (sum, i) => sum + i.CandidateLength);
}

internal enum DeletionStop { None, Declined, Busy, NothingToDelete, StalePlan, NotStarted, Error, ExitRequested }

// Planned = targets in the plan; Launched = CLI processes that started (or may have); Completed = normal ends.
internal sealed record DeletionBatchResult(DeletionStop Stop, int Planned, int Launched, int Completed)
{
    public static DeletionBatchResult Refused(DeletionStop stop, int planned) => new(stop, planned, 0, 0);
}
