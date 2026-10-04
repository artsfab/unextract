using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Unextract.Core.Analysis;
using Unextract.Core.CommandLine;
using Unextract.Core.Commands;
using Unextract.Core.Deletion;
using Unextract.Core.Display;
using Unextract.Core.Entries;
using Unextract.Core.Results;

namespace Unextract.Cli;

// protocol v1 の出力値。Core の型からの対応は CLI が担当する。
// Name は生の FullName、Message は表示用に整えた文字列を受け取る。
internal sealed record MachineRunRecord(string Operation, string Mode, string Archive, string Target,
    int EntriesTotal, int Selected, bool EntriesOption);

internal sealed record MachineEntryRecord(int Index, string Name, bool Directory, long Length, string Status,
    string? SkipReason = null, MachineReason? Reason = null, bool? PossiblyDeleted = null);

internal sealed record MachineResultRecord(string Outcome, int ExitCode,
    MachineCounts? Counts = null, MachineError? Error = null);

internal sealed record MachineReason(string Code, string Message, string? Step = null, int? Win32Error = null);

internal sealed record MachineError(string Stage, string Code, string? Message = null, string? Step = null,
    int? EntryIndex = null, string? EntryName = null, int? Line = null, int? Win32Error = null,
    bool? PossiblyDeleted = null, bool? DeletionStarted = null);

internal sealed record MachineCounts(int? Matched = null, int? SameSize = null, int? Modified = null,
    int? Missing = null, int? SkippedSpecialFile = null, int? Directory = null, int? Undetermined = null,
    int? Deleted = null, int? DeleteFailed = null, int? NotSelected = null, int? Unprocessed = null);

// Core の型から v1 へ明示的に対応付け、reflection を使わず各フィールド・省略・数値/bool 型を書く。
// 1 レコードを一度だけ ASCII の UTF-8 byte 列にし、LF を付けて同期配送へ渡す。
internal static class MachineOutput
{
    public static MachineRunRecord Run(CommandKind command, PreparedCommandInfo info) => new(
        Operation(command), Mode(info.Mode), info.ArchivePath, ReportText.WithoutDevicePrefix(info.TargetFinalPath),
        info.TotalEntries, info.SelectedEntries, info.EntriesOption);

    public static MachineEntryRecord Entry(EntryResult result) => new(
        result.Entry.Number, result.Entry.Name, result.Classification == Classification.Directory,
        result.Length, Status(result.Classification),
        result.Classification == Classification.SkippedSpecialFile ? SkipCode(result.SkipReason!.Value) : null);

    public static MachineEntryRecord Entry(DeleteEntryResult result) => new(
        result.Entry.Number, result.Entry.Name, false, result.Length, Status(result.Status),
        result.Status == DeleteStatus.SkippedSpecialFile ? SkipCode(result.SkipReason!.Value) : null,
        result.Status is DeleteStatus.DeleteFailed or DeleteStatus.Stopped ? Reason(result) : null,
        result.Status == DeleteStatus.Stopped ? result.PossiblyDeleted : null);

    public static MachineResultRecord Result(AnalyzeCommandOutcome outcome, RunMode mode)
    {
        if (outcome.PreparationFailure is { } failure)
        {
            // Prepare 後の人間向け表示は、判定済み0と未判定だけ。カテゴリーは出さない。
            var counts = outcome.Analysis is { } before
                ? new MachineCounts(Undetermined: before.UnclassifiedCount) : null;
            return PreparationResult(failure, counts);
        }

        var analysis = outcome.Analysis ?? throw new InvalidOperationException("解析結果がありません。");
        return new(analysis.Completed ? "completed" : "fatal", analysis.Completed ? 0 : 1,
            AnalysisCounts(analysis, mode), analysis.Fatal is { } fatal ? Error(fatal, "entry") : null);
    }

    public static MachineResultRecord Result(DeleteCommandOutcome outcome, PreparedCommandInfo? info)
    {
        if (outcome.PreparationFailure is { } failure) return PreparationResult(failure);
        var report = outcome.Report ?? throw new InvalidOperationException("削除結果がありません。");
        var prepared = info ?? throw new InvalidOperationException("Prepare 成功情報がありません。");
        var stop = report.Stop;
        return new(stop is null ? "completed" : "stopped",
            stop is null && report.Count(DeleteStatus.DeleteFailed) == 0 ? 0 : 1,
            DeleteCounts(report, prepared.TotalEntries - prepared.SelectedEntries),
            stop is null ? null : StopError(stop));
    }

    public static MachineResultRecord UsageError(string message) => new("input_error", 1,
        Error: new MachineError("usage", "USAGE", SafeDisplay.Escape(message)));

    public static MachineResultRecord LogError(LogCreationFailure failure) => new("input_error", 1,
        Error: new MachineError("prepare", failure.Kind switch
        {
            LogCreationFailureKind.AlreadyExists => "LOG_ALREADY_EXISTS",
            LogCreationFailureKind.CreateFailed => "LOG_CREATE_FAILED",
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        }, SafeDisplay.Escape(failure.Message)));

    public static MachineResultRecord InternalError(CommandKind? command, Exception exception,
        bool deletionStarted, MachineDeleteProgress? progress = null)
    {
        var delete = command == CommandKind.Delete;
        var stop = delete ? progress?.Stop : null;
        return new("internal_error", 1, delete ? progress?.Counts : null,
            new MachineError("internal", exception is MachineOutputException ? "OUTPUT_FAILED" : "UNEXPECTED_EXCEPTION",
                $"{exception.GetType().Name}: {SafeDisplay.Escape(exception.Message)}",
                EntryIndex: stop?.Entry.Number, EntryName: stop?.Entry.Name,
                PossiblyDeleted: stop?.PossiblyDeleted, DeletionStarted: delete ? deletionStarted : null));
    }

    private static MachineResultRecord PreparationResult(PrepareFailure failure, MachineCounts? counts = null)
    {
        var error = failure.EntriesError is { } entries
            ? new MachineError("prepare", Code(entries.Kind), entries.Describe(), Line: entries.LineNumber)
            : Error(failure.Fatal ?? throw new InvalidOperationException("Prepare の原因がありません。"), "prepare");
        return new(failure.IsFatal ? "fatal" : "input_error", 1, counts, error);
    }

    private static MachineError Error(FatalError fatal, string stage) => new(stage, Code(fatal.Kind), fatal.Describe(),
        stage == "entry" ? Step(fatal.Step) : null, fatal.Entry?.Number, fatal.Entry?.Name, Win32Error: fatal.Win32Error);

    private static MachineReason Reason(DeleteEntryResult result)
    {
        var failure = result.Failure ?? throw new InvalidOperationException("削除の失敗原因がありません。");
        return new(failure.FatalKind is { } common ? Code(common) : Code(failure.Kind!.Value),
            SafeDisplay.Escape(result.Reason ?? string.Empty), Step(failure.Step), failure.Win32Error);
    }

    private static MachineError StopError(DeleteEntryResult stop)
    {
        var reason = Reason(stop);
        return new("entry", reason.Code, reason.Message, reason.Step, stop.Entry.Number, stop.Entry.Name,
            Win32Error: reason.Win32Error, PossiblyDeleted: stop.PossiblyDeleted);
    }

    private static MachineCounts AnalysisCounts(AnalysisResult result, RunMode mode) => new(
        Matched: mode == RunMode.Strict ? result.Count(Classification.Matched) : null,
        SameSize: mode == RunMode.Fast ? result.Count(Classification.SameSize) : null,
        Modified: result.Count(Classification.Modified), Missing: result.Count(Classification.Missing),
        SkippedSpecialFile: result.Count(Classification.SkippedSpecialFile), Directory: result.Count(Classification.Directory),
        Undetermined: result.Completed ? null : result.UnclassifiedCount);

    private static MachineCounts DeleteCounts(DeleteReport report, int excluded) => new(
        Deleted: report.Count(DeleteStatus.Deleted), Modified: report.Count(DeleteStatus.Modified),
        Missing: report.Count(DeleteStatus.Missing), SkippedSpecialFile: report.Count(DeleteStatus.SkippedSpecialFile),
        Directory: report.DirectoryCount, DeleteFailed: report.Count(DeleteStatus.DeleteFailed),
        NotSelected: excluded, Unprocessed: report.NotProcessedCount);

    private static string Operation(CommandKind command) => command switch
    {
        CommandKind.Analyze => "analyze",
        CommandKind.Delete => "delete",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private static string Mode(RunMode mode) => mode switch
    {
        RunMode.Strict => "strict",
        RunMode.Fast => "fast",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string Status(Classification classification) => classification switch
    {
        Classification.Matched => "MATCHED",
        Classification.SameSize => "SAME_SIZE",
        Classification.Modified => "MODIFIED",
        Classification.Missing => "MISSING",
        Classification.SkippedSpecialFile => "SKIPPED_SPECIAL_FILE",
        Classification.Directory => "DIRECTORY",
        _ => throw new ArgumentOutOfRangeException(nameof(classification)),
    };

    private static string Status(DeleteStatus status) => status switch
    {
        DeleteStatus.Deleted => "DELETED",
        DeleteStatus.Modified => "MODIFIED",
        DeleteStatus.Missing => "MISSING",
        DeleteStatus.SkippedSpecialFile => "SKIPPED_SPECIAL_FILE",
        DeleteStatus.DeleteFailed => "DELETE_FAILED",
        DeleteStatus.Stopped => "STOPPED",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string SkipCode(SkipReason reason) => reason switch
    {
        SkipReason.ParentReparsePoint => "PARENT_REPARSE",
        SkipReason.Directory => "DIRECTORY",
        SkipReason.ReparsePoint => "REPARSE",
        SkipReason.HardLink => "HARDLINK",
        SkipReason.AlternateDataStream => "ADS",
        SkipReason.ArchiveItself => "ARCHIVE_ITSELF",
        SkipReason.Attributes => "ATTRIBUTES",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static string? Step(EntryStep? step) => step switch
    {
        null => null,
        EntryStep.Resolve => "resolve",
        EntryStep.Open => "open",
        EntryStep.Verify => "verify",
        EntryStep.Inspect => "inspect",
        EntryStep.Compare => "compare",
        EntryStep.FinalCheck => "final_check",
        EntryStep.Dispose => "dispose",
        EntryStep.Confirm => "confirm",
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    private static string Code(EntriesErrorKind kind) => kind switch
    {
        EntriesErrorKind.Unreadable => "ENTRIES_UNREADABLE",
        EntriesErrorKind.TooLarge => "ENTRIES_TOO_LARGE",
        EntriesErrorKind.Utf16 => "ENTRIES_UTF16",
        EntriesErrorKind.Utf32 => "ENTRIES_UTF32",
        EntriesErrorKind.TooManyLines => "ENTRIES_TOO_MANY_LINES",
        EntriesErrorKind.CrInLine => "ENTRIES_CR_IN_LINE",
        EntriesErrorKind.EmptyLine => "ENTRIES_EMPTY_LINE",
        EntriesErrorKind.LineTooLong => "ENTRIES_LINE_TOO_LONG",
        EntriesErrorKind.InvalidUtf8 => "ENTRIES_INVALID_UTF8",
        EntriesErrorKind.DuplicateLine => "ENTRIES_DUPLICATE_LINE",
        EntriesErrorKind.NoLines => "ENTRIES_NO_LINES",
        EntriesErrorKind.NoMatch => "ENTRIES_NO_MATCH",
        EntriesErrorKind.Directory => "ENTRIES_DIRECTORY",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Code(DeleteFailureKind kind) => kind switch
    {
        DeleteFailureKind.DeleteOpenRefused => "DELETE_OPEN_REFUSED",
        DeleteFailureKind.OpenFailed => "OPEN_FAILED",
        DeleteFailureKind.IdentityCheckFailed => "IDENTITY_CHECK_FAILED",
        DeleteFailureKind.IdentityCheckMismatch => "IDENTITY_CHECK_MISMATCH",
        DeleteFailureKind.FinalCheckMismatch => "FINAL_CHECK_MISMATCH",
        DeleteFailureKind.DispositionFailed => "DISPOSITION_FAILED",
        DeleteFailureKind.DeletionUnconfirmed => "DELETION_UNCONFIRMED",
        DeleteFailureKind.UnexpectedException => "UNEXPECTED_EXCEPTION",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Code(FatalKind kind) => kind switch
    {
        FatalKind.ArchiveOpenFailed => "ARCHIVE_OPEN_FAILED",
        FatalKind.ArchiveUnreadable => "ARCHIVE_UNREADABLE",
        FatalKind.TooManyEntries => "TOO_MANY_ENTRIES",
        FatalKind.NameTooLong => "NAME_TOO_LONG",
        FatalKind.MetadataTooLarge => "METADATA_TOO_LARGE",
        FatalKind.PathTooDeep => "PATH_TOO_DEEP",
        FatalKind.EntryTooLarge => "ENTRY_TOO_LARGE",
        FatalKind.TotalDeclaredLengthTooLarge => "TOTAL_DECLARED_LENGTH_TOO_LARGE",
        FatalKind.InvalidDeclaredLength => "INVALID_DECLARED_LENGTH",
        FatalKind.NameContainsReplacementCharacter => "NAME_CONTAINS_REPLACEMENT_CHARACTER",
        FatalKind.RootedPath => "ROOTED_PATH",
        FatalKind.DriveSpecifier => "DRIVE_SPECIFIER",
        FatalKind.Colon => "COLON",
        FatalKind.ControlCharacter => "CONTROL_CHARACTER",
        FatalKind.InvalidCharacter => "INVALID_CHARACTER",
        FatalKind.EmptyComponent => "EMPTY_COMPONENT",
        FatalKind.DotComponent => "DOT_COMPONENT",
        FatalKind.DotDotComponent => "DOT_DOT_COMPONENT",
        FatalKind.TrailingDotOrSpace => "TRAILING_DOT_OR_SPACE",
        FatalKind.ReservedName => "RESERVED_NAME",
        FatalKind.DuplicateEntry => "DUPLICATE_ENTRY",
        FatalKind.CaseInsensitiveCollision => "CASE_INSENSITIVE_COLLISION",
        FatalKind.FileDirectoryConflict => "FILE_DIRECTORY_CONFLICT",
        FatalKind.FileUsedAsParent => "FILE_USED_AS_PARENT",
        FatalKind.FileEntryWithDirectoryType => "FILE_ENTRY_WITH_DIRECTORY_TYPE",
        FatalKind.DirectoryEntryWithFileType => "DIRECTORY_ENTRY_WITH_FILE_TYPE",
        FatalKind.UnsupportedEntryType => "UNSUPPORTED_ENTRY_TYPE",
        FatalKind.DosDirectoryAttributeOnFileEntry => "DOS_DIRECTORY_ATTRIBUTE_ON_FILE_ENTRY",
        FatalKind.DosReparsePointAttribute => "DOS_REPARSE_POINT_ATTRIBUTE",
        FatalKind.DirectoryEntryWithData => "DIRECTORY_ENTRY_WITH_DATA",
        FatalKind.TargetNotFound => "TARGET_NOT_FOUND",
        FatalKind.TargetCheckFailed => "TARGET_CHECK_FAILED",
        FatalKind.TargetIsReparsePoint => "TARGET_IS_REPARSE_POINT",
        FatalKind.TargetChangedDuringCheck => "TARGET_CHANGED_DURING_CHECK",
        FatalKind.TargetNotDirectory => "TARGET_NOT_DIRECTORY",
        FatalKind.TargetNotNtfs => "TARGET_NOT_NTFS",
        FatalKind.TargetIsUncPath => "TARGET_IS_UNC_PATH",
        FatalKind.TargetIsDriveRoot => "TARGET_IS_DRIVE_ROOT",
        FatalKind.TargetUnsupportedPathForm => "TARGET_UNSUPPORTED_PATH_FORM",
        FatalKind.TargetIsProtectedLocation => "TARGET_IS_PROTECTED_LOCATION",
        FatalKind.ProtectedLocationUnresolved => "PROTECTED_LOCATION_UNRESOLVED",
        FatalKind.ArchiveIdentityFailed => "ARCHIVE_IDENTITY_FAILED",
        FatalKind.EnumerationOpenFailed => "ENUMERATION_OPEN_FAILED",
        FatalKind.EnumerationHandleMismatch => "ENUMERATION_HANDLE_MISMATCH",
        FatalKind.EnumerationFailed => "ENUMERATION_FAILED",
        FatalKind.UnexpectedTargetType => "UNEXPECTED_TARGET_TYPE",
        FatalKind.ComparisonOpenFailed => "COMPARISON_OPEN_FAILED",
        FatalKind.ComparisonFileIdMismatch => "COMPARISON_FILE_ID_MISMATCH",
        FatalKind.FinalPathMismatch => "FINAL_PATH_MISMATCH",
        FatalKind.ParentFileIdMismatch => "PARENT_FILE_ID_MISMATCH",
        FatalKind.TargetDeletePending => "TARGET_DELETE_PENDING",
        FatalKind.TargetInfoFailed => "TARGET_INFO_FAILED",
        FatalKind.TargetReadFailed => "TARGET_READ_FAILED",
        FatalKind.ContentEncrypted => "CONTENT_ENCRYPTED",
        FatalKind.ContentReadFailed => "CONTENT_READ_FAILED",
        FatalKind.ContentTooLong => "CONTENT_TOO_LONG",
        FatalKind.ContentTooShort => "CONTENT_TOO_SHORT",
        FatalKind.ContentCrcMismatch => "CONTENT_CRC_MISMATCH",
        FatalKind.TotalReadLengthTooLarge => "TOTAL_READ_LENGTH_TOO_LARGE",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static byte[] Serialize(MachineRunRecord record) => Encode("run", writer =>
    {
        writer.WriteString("operation", record.Operation);
        writer.WriteString("mode", record.Mode);
        writer.WriteString("archive", record.Archive);
        writer.WriteString("target", record.Target);
        writer.WriteNumber("entries_total", record.EntriesTotal);
        writer.WriteNumber("selected", record.Selected);
        writer.WriteBoolean("entries_option", record.EntriesOption);
    });

    public static byte[] Serialize(MachineEntryRecord record) => Encode("entry", writer =>
    {
        writer.WriteNumber("index", record.Index);
        writer.WriteString("name", record.Name);
        writer.WriteBoolean("directory", record.Directory);
        writer.WriteNumber("length", record.Length);
        writer.WriteString("status", record.Status);
        String(writer, "skip_reason", record.SkipReason);
        if (record.Reason is { } reason)
        {
            writer.WriteStartObject("reason");
            String(writer, "step", reason.Step);
            writer.WriteString("code", reason.Code);
            Number(writer, "win32_error", reason.Win32Error);
            writer.WriteString("message", reason.Message);
            writer.WriteEndObject();
        }
        Boolean(writer, "possibly_deleted", record.PossiblyDeleted);
    });

    public static byte[] Serialize(MachineResultRecord record) => Encode("result", writer =>
    {
        writer.WriteString("outcome", record.Outcome);
        writer.WriteNumber("exit_code", record.ExitCode);
        if (record.Counts is { } counts)
        {
            writer.WriteStartObject("counts");
            Number(writer, "matched", counts.Matched);
            Number(writer, "same_size", counts.SameSize);
            Number(writer, "modified", counts.Modified);
            Number(writer, "missing", counts.Missing);
            Number(writer, "skipped_special_file", counts.SkippedSpecialFile);
            Number(writer, "directory", counts.Directory);
            Number(writer, "undetermined", counts.Undetermined);
            Number(writer, "deleted", counts.Deleted);
            Number(writer, "delete_failed", counts.DeleteFailed);
            Number(writer, "not_selected", counts.NotSelected);
            Number(writer, "unprocessed", counts.Unprocessed);
            writer.WriteEndObject();
        }
        if (record.Error is { } error)
        {
            writer.WriteStartObject("error");
            writer.WriteString("stage", error.Stage);
            String(writer, "step", error.Step);
            writer.WriteString("code", error.Code);
            Number(writer, "entry_index", error.EntryIndex);
            String(writer, "entry_name", error.EntryName);
            Number(writer, "line", error.Line);
            Number(writer, "win32_error", error.Win32Error);
            Boolean(writer, "possibly_deleted", error.PossiblyDeleted);
            Boolean(writer, "deletion_started", error.DeletionStarted);
            String(writer, "message", error.Message);
            writer.WriteEndObject();
        }
    });

    private static byte[] Encode(string type, Action<Utf8JsonWriter> fields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("type", type);
            fields(writer);
            writer.WriteEndObject();
        }
        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
        return buffer.WrittenSpan.ToArray();
    }

    private static void String(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null) writer.WriteString(name, value);
    }

    private static void Number(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
    }

    private static void Boolean(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is { } flag) writer.WriteBoolean(name, flag);
    }
}

// 内部エラー報告に必要な処理済み件数だけを保存する。配送前に Observe する。
// 削除の許可・選択状態・ハンドル・再開状態は保持しない。
internal sealed class MachineDeleteProgress
{
    private PreparedCommandInfo? _prepared;
    private int _processedFiles;
    private int _deleted;
    private int _modified;
    private int _missing;
    private int _skipped;
    private int _failed;
    private int _directories;

    public DeleteEntryResult? Stop { get; private set; }

    public void Prepared(PreparedCommandInfo info) => _prepared = info;

    public void Observe(DeleteEntryResult result)
    {
        _processedFiles++;
        switch (result.Status)
        {
            case DeleteStatus.Deleted: _deleted++; break;
            case DeleteStatus.Modified: _modified++; break;
            case DeleteStatus.Missing: _missing++; break;
            case DeleteStatus.SkippedSpecialFile: _skipped++; break;
            case DeleteStatus.DeleteFailed: _failed++; break;
            case DeleteStatus.Stopped: Stop = result; break;
            default: throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    public void ObserveDirectoryCount(int count) => _directories = count;

    public MachineCounts? Counts => _prepared is { } info ? new(
        Deleted: _deleted, Modified: _modified, Missing: _missing, SkippedSpecialFile: _skipped,
        Directory: _directories, DeleteFailed: _failed, NotSelected: info.TotalEntries - info.SelectedEntries,
        Unprocessed: info.SelectedEntries - _processedFiles - _directories) : null;
}
