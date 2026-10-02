namespace Unextract.Core.Results;

// target 側の分類 (SPEC §6)。全体 FATAL は分類ではなく FatalError で表す。
// 削除候補は Strict では Matched、Fast では SameSize (SPEC §6、§15.3)。SameSize は内容の一致を意味しない。
public enum Classification
{
    Matched,
    SameSize,
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
        Classification.SameSize => "SAME_SIZE",
        Classification.Modified => "MODIFIED",
        Classification.Missing => "MISSING",
        Classification.SkippedSpecialFile => "SKIPPED_SPECIAL_FILE",
        Classification.Directory => "DIRECTORY",
        _ => throw new ArgumentOutOfRangeException(nameof(classification), classification, null),
    };
}
