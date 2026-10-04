namespace Unextract.Gui.Models;

internal sealed record ArchiveFile(string Path, long Length, DateTime LastWriteTimeUtc);
internal sealed record SearchDiagnostic(string Path, string Message)
{
    public string Display => $"{DisplayText.Escape(Path)}: {DisplayText.Escape(Message)}";
}
internal sealed record ArchiveSearchResult(IReadOnlyList<ArchiveFile> Archives,
    IReadOnlyList<SearchDiagnostic> Diagnostics);
internal enum TargetPresence { Present, Missing, Unknown }
internal sealed record TargetObservation(TargetPresence Presence, string? Detail = null);
internal sealed record TargetRegistrationResult(int Added, int Duplicates,
    IReadOnlyList<SearchDiagnostic> Errors);
