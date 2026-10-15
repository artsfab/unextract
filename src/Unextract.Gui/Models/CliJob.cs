using System.IO;

namespace Unextract.Gui.Models;

internal enum CliOperation { Analyze, Delete }
internal enum CliMode { Strict, Fast }
internal enum CliStartState { BeforeStartFailure, NotStarted, Started, Unknown }
internal enum ProtocolIssue { None, IncompatibleVersion, InvalidOutput }

internal sealed record CliJob(CliOperation Operation, CliMode Mode, string Archive, string Target,
    string? Entries = null, string? Log = null, bool PruneDirs = false)
{
    public string OperationValue => Operation == CliOperation.Analyze ? "analyze" : "delete";
    public string ModeValue => Mode == CliMode.Strict ? "strict" : "fast";

    public void Validate()
    {
        if (!Enum.IsDefined(Operation) || !Enum.IsDefined(Mode))
            throw new ArgumentException("操作またはモードが不正です。");
        Absolute(Archive);
        Absolute(Target);
        if (Operation == CliOperation.Delete)
        {
            Absolute(Entries);
            Absolute(Log);
        }
        else if (Entries is not null || Log is not null || PruneDirs)
            throw new ArgumentException("解析にはentries・log・prune-dirsを指定できません。");
    }

    private static void Absolute(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("CLIのファイル引数には絶対パスが必要です。");
    }
}

// GUI owns these received values. No CLI/Core assembly types or display-normalized names.
// PruneDirs is received for delete only; DirsSelected only when PruneDirs is true.
internal sealed record CliRun(string Operation, string Mode, string Archive, string Target,
    int EntriesTotal, int Selected, bool EntriesOption, bool? PruneDirs = null, int? DirsSelected = null);
internal sealed record CliEntry(int Index, string Name, bool Directory, long Length, string Status,
    string? SkipReason, CliReason? Reason, bool? PossiblyDeleted);
// One --prune-dirs directory result. Path is relative to the target and is not an entry name.
internal sealed record CliDir(string Path, string Status, string? KeepReason, CliReason? Reason, bool? PossiblyDeleted);
internal sealed record CliReason(string? Step, string Code, int? Win32Error, string Message);
internal sealed record CliError(string Stage, string? Step, string Code, int? EntryIndex,
    string? EntryName, int? Line, int? Win32Error, bool? PossiblyDeleted, bool? DeletionStarted,
    string? Message, string? DirPath = null);
internal sealed record CliResult(string Outcome, int ExitCode, IReadOnlyDictionary<string, int>? Counts,
    CliError? Error);
internal sealed record CliOutput(CliRun? Run, IReadOnlyList<CliEntry> Entries, CliResult? Result,
    ProtocolIssue Issue, string? IssueMessage, IReadOnlyList<CliDir> Dirs)
{
    public bool IsCompatible => Issue == ProtocolIssue.None;
}

internal sealed record CliProgress(CliStartState StartState, CliRun? Run, int ReceivedEntries);
internal sealed record CliJobResult(CliStartState StartState, bool ExitConfirmed, int? ExitCode,
    bool StdoutEof, bool StderrEof, bool Cancelled, CliOutput Output, string Stderr,
    bool StderrTruncated, string? ProcessError)
{
    public bool Succeeded => StartState == CliStartState.Started && ExitConfirmed &&
        StdoutEof && StderrEof && !Cancelled && ProcessError is null && Output.IsCompatible &&
        ExitCode == 0 && Output.Result is { Outcome: "completed", ExitCode: 0 };
}
