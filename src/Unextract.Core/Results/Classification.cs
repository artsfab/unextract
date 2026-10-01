namespace Unextract.Core.Results;

// target 側の分類 (SPEC §6)。全体 FATAL は分類ではなく FatalError で表す。
public enum Classification
{
    Matched,
    Modified,
    Missing,
    SkippedSpecialFile,
    Directory,
}

public static class ClassificationExtensions
{
    public static string ToDisplayString(this Classification classification) => classification switch
    {
        Classification.Matched => "MATCHED",
        Classification.Modified => "MODIFIED",
        Classification.Missing => "MISSING",
        Classification.SkippedSpecialFile => "SKIPPED_SPECIAL_FILE",
        Classification.Directory => "DIRECTORY",
        _ => throw new ArgumentOutOfRangeException(nameof(classification), classification, null),
    };
}
