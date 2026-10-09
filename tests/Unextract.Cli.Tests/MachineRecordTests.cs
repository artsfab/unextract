using System.IO.Compression;
using System.Text.Json;
using Unextract.Core.Analysis;
using Unextract.Core.CommandLine;
using Unextract.Core.Commands;
using Unextract.Core.Deletion;
using Unextract.Core.Display;
using Unextract.Core.Entries;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;
using Unextract.Windows;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.Cli.Tests;

// J11: Core の値だけから v1 の状態・原因・件数・省略を生成する。writer は使わない。
// 全原因種別・状態・SkipReason・段階の明示的な対応、Number/raw name/64bit Length、run のパス・件数、全 outcome/終了コード、モード・
// 段階による counts と error の省略、STOP の診断・不確実性、配送失敗対象を含む報告専用の集計。analyze の通常/空/FATAL の件数は自作の
// fixture で実 command の結果とも照合する。削除は行わない (docs/spec/machine-output.md#records、#result、#codes)。
public class MachineRecordTests
{
    public static TheoryData<FatalKind, string> FatalCodes => new()
    {
        { FatalKind.ArchiveOpenFailed, "ARCHIVE_OPEN_FAILED" },
        { FatalKind.ArchiveUnreadable, "ARCHIVE_UNREADABLE" },
        { FatalKind.RarLibraryUnavailable, "RAR_LIBRARY_UNAVAILABLE" },
        { FatalKind.ArchiveNotRar, "ARCHIVE_NOT_RAR" },
        { FatalKind.ArchiveSolid, "ARCHIVE_SOLID" },
        { FatalKind.ArchiveMultiVolume, "ARCHIVE_MULTI_VOLUME" },
        { FatalKind.ArchiveEncrypted, "ARCHIVE_ENCRYPTED" },
        { FatalKind.EntrySolid, "ENTRY_SOLID" },
        { FatalKind.EntrySplit, "ENTRY_SPLIT" },
        { FatalKind.EntryEncrypted, "ENTRY_ENCRYPTED" },
        { FatalKind.EntryRedirection, "ENTRY_REDIRECTION" },
        { FatalKind.EntryWithoutHash, "ENTRY_WITHOUT_HASH" },
        { FatalKind.UnsupportedHostOs, "UNSUPPORTED_HOST_OS" },
        { FatalKind.FileEntryNameEndsWithSeparator, "FILE_ENTRY_NAME_ENDS_WITH_SEPARATOR" },
        { FatalKind.EntryDictionaryTooLarge, "ENTRY_DICTIONARY_TOO_LARGE" },
        { FatalKind.TooManyEntries, "TOO_MANY_ENTRIES" },
        { FatalKind.NameTooLong, "NAME_TOO_LONG" },
        { FatalKind.MetadataTooLarge, "METADATA_TOO_LARGE" },
        { FatalKind.PathTooDeep, "PATH_TOO_DEEP" },
        { FatalKind.EntryTooLarge, "ENTRY_TOO_LARGE" },
        { FatalKind.TotalDeclaredLengthTooLarge, "TOTAL_DECLARED_LENGTH_TOO_LARGE" },
        { FatalKind.InvalidDeclaredLength, "INVALID_DECLARED_LENGTH" },
        { FatalKind.NameContainsReplacementCharacter, "NAME_CONTAINS_REPLACEMENT_CHARACTER" },
        { FatalKind.RootedPath, "ROOTED_PATH" },
        { FatalKind.DriveSpecifier, "DRIVE_SPECIFIER" },
        { FatalKind.Colon, "COLON" },
        { FatalKind.ControlCharacter, "CONTROL_CHARACTER" },
        { FatalKind.InvalidCharacter, "INVALID_CHARACTER" },
        { FatalKind.EmptyComponent, "EMPTY_COMPONENT" },
        { FatalKind.DotComponent, "DOT_COMPONENT" },
        { FatalKind.DotDotComponent, "DOT_DOT_COMPONENT" },
        { FatalKind.TrailingDotOrSpace, "TRAILING_DOT_OR_SPACE" },
        { FatalKind.ReservedName, "RESERVED_NAME" },
        { FatalKind.DuplicateEntry, "DUPLICATE_ENTRY" },
        { FatalKind.CaseInsensitiveCollision, "CASE_INSENSITIVE_COLLISION" },
        { FatalKind.FileDirectoryConflict, "FILE_DIRECTORY_CONFLICT" },
        { FatalKind.FileUsedAsParent, "FILE_USED_AS_PARENT" },
        { FatalKind.FileEntryWithDirectoryType, "FILE_ENTRY_WITH_DIRECTORY_TYPE" },
        { FatalKind.DirectoryEntryWithFileType, "DIRECTORY_ENTRY_WITH_FILE_TYPE" },
        { FatalKind.UnsupportedEntryType, "UNSUPPORTED_ENTRY_TYPE" },
        { FatalKind.DosDirectoryAttributeOnFileEntry, "DOS_DIRECTORY_ATTRIBUTE_ON_FILE_ENTRY" },
        { FatalKind.DosReparsePointAttribute, "DOS_REPARSE_POINT_ATTRIBUTE" },
        { FatalKind.DirectoryEntryWithData, "DIRECTORY_ENTRY_WITH_DATA" },
        { FatalKind.TargetNotFound, "TARGET_NOT_FOUND" },
        { FatalKind.TargetCheckFailed, "TARGET_CHECK_FAILED" },
        { FatalKind.TargetIsReparsePoint, "TARGET_IS_REPARSE_POINT" },
        { FatalKind.TargetChangedDuringCheck, "TARGET_CHANGED_DURING_CHECK" },
        { FatalKind.TargetNotDirectory, "TARGET_NOT_DIRECTORY" },
        { FatalKind.TargetNotNtfs, "TARGET_NOT_NTFS" },
        { FatalKind.TargetIsUncPath, "TARGET_IS_UNC_PATH" },
        { FatalKind.TargetIsDriveRoot, "TARGET_IS_DRIVE_ROOT" },
        { FatalKind.TargetUnsupportedPathForm, "TARGET_UNSUPPORTED_PATH_FORM" },
        { FatalKind.TargetIsProtectedLocation, "TARGET_IS_PROTECTED_LOCATION" },
        { FatalKind.ProtectedLocationUnresolved, "PROTECTED_LOCATION_UNRESOLVED" },
        { FatalKind.ArchiveIdentityFailed, "ARCHIVE_IDENTITY_FAILED" },
        { FatalKind.EnumerationOpenFailed, "ENUMERATION_OPEN_FAILED" },
        { FatalKind.EnumerationHandleMismatch, "ENUMERATION_HANDLE_MISMATCH" },
        { FatalKind.EnumerationFailed, "ENUMERATION_FAILED" },
        { FatalKind.UnexpectedTargetType, "UNEXPECTED_TARGET_TYPE" },
        { FatalKind.ComparisonOpenFailed, "COMPARISON_OPEN_FAILED" },
        { FatalKind.ComparisonFileIdMismatch, "COMPARISON_FILE_ID_MISMATCH" },
        { FatalKind.FinalPathMismatch, "FINAL_PATH_MISMATCH" },
        { FatalKind.ParentFileIdMismatch, "PARENT_FILE_ID_MISMATCH" },
        { FatalKind.TargetDeletePending, "TARGET_DELETE_PENDING" },
        { FatalKind.TargetInfoFailed, "TARGET_INFO_FAILED" },
        { FatalKind.TargetReadFailed, "TARGET_READ_FAILED" },
        { FatalKind.ContentEncrypted, "CONTENT_ENCRYPTED" },
        { FatalKind.ContentReadFailed, "CONTENT_READ_FAILED" },
        { FatalKind.ContentTooLong, "CONTENT_TOO_LONG" },
        { FatalKind.ContentTooShort, "CONTENT_TOO_SHORT" },
        { FatalKind.ContentCrcMismatch, "CONTENT_CRC_MISMATCH" },
        { FatalKind.TotalReadLengthTooLarge, "TOTAL_READ_LENGTH_TOO_LARGE" },
        { FatalKind.ArchiveChanged, "ARCHIVE_CHANGED" },
    };

    public static TheoryData<EntriesErrorKind, string> EntriesCodes => new()
    {
        { EntriesErrorKind.Unreadable, "ENTRIES_UNREADABLE" },
        { EntriesErrorKind.TooLarge, "ENTRIES_TOO_LARGE" },
        { EntriesErrorKind.Utf16, "ENTRIES_UTF16" },
        { EntriesErrorKind.Utf32, "ENTRIES_UTF32" },
        { EntriesErrorKind.TooManyLines, "ENTRIES_TOO_MANY_LINES" },
        { EntriesErrorKind.CrInLine, "ENTRIES_CR_IN_LINE" },
        { EntriesErrorKind.EmptyLine, "ENTRIES_EMPTY_LINE" },
        { EntriesErrorKind.LineTooLong, "ENTRIES_LINE_TOO_LONG" },
        { EntriesErrorKind.InvalidUtf8, "ENTRIES_INVALID_UTF8" },
        { EntriesErrorKind.DuplicateLine, "ENTRIES_DUPLICATE_LINE" },
        { EntriesErrorKind.NoLines, "ENTRIES_NO_LINES" },
        { EntriesErrorKind.NoMatch, "ENTRIES_NO_MATCH" },
        { EntriesErrorKind.Directory, "ENTRIES_DIRECTORY" },
    };

    public static TheoryData<SkipReason, string> SkipCodes => new()
    {
        { SkipReason.ParentReparsePoint, "PARENT_REPARSE" },
        { SkipReason.Directory, "DIRECTORY" },
        { SkipReason.ReparsePoint, "REPARSE" },
        { SkipReason.HardLink, "HARDLINK" },
        { SkipReason.AlternateDataStream, "ADS" },
        { SkipReason.ArchiveItself, "ARCHIVE_ITSELF" },
        { SkipReason.Attributes, "ATTRIBUTES" },
    };

    public static TheoryData<DeleteFailureKind, string> DeleteCodes => new()
    {
        { DeleteFailureKind.DeleteOpenRefused, "DELETE_OPEN_REFUSED" },
        { DeleteFailureKind.OpenFailed, "OPEN_FAILED" },
        { DeleteFailureKind.IdentityCheckFailed, "IDENTITY_CHECK_FAILED" },
        { DeleteFailureKind.IdentityCheckMismatch, "IDENTITY_CHECK_MISMATCH" },
        { DeleteFailureKind.FinalCheckMismatch, "FINAL_CHECK_MISMATCH" },
        { DeleteFailureKind.DispositionFailed, "DISPOSITION_FAILED" },
        { DeleteFailureKind.DeletionUnconfirmed, "DELETION_UNCONFIRMED" },
        { DeleteFailureKind.UnexpectedException, "UNEXPECTED_EXCEPTION" },
    };

    [Fact]
    public void J11_CodeCasesCoverEveryCoreCause()
    {
        Assert.Equal(Enum.GetValues<FatalKind>(), FatalCodes.Select(row => (FatalKind)row[0]));
        Assert.Equal(Enum.GetValues<EntriesErrorKind>(), EntriesCodes.Select(row => (EntriesErrorKind)row[0]));
        Assert.Equal(Enum.GetValues<SkipReason>(), SkipCodes.Select(row => (SkipReason)row[0]));
        Assert.Equal(Enum.GetValues<DeleteFailureKind>(), DeleteCodes.Select(row => (DeleteFailureKind)row[0]));
    }

    [Theory]
    [MemberData(nameof(FatalCodes))]
    public void J11_CommonCauseMapsWithoutParsingMessage(FatalKind kind, string code)
    {
        var fatal = new FatalError(kind, new ZipEntryRef(7, RawName), "diagnostic\u202E", EntryStep.Compare, 1117);
        var failure = new PrepareFailure(PrepareStage.Archive, "unrelated message", fatal);
        var prepared = MachineOutput.Result(new DeleteCommandOutcome(ExitStatus.Error, null, fatal, failure), null);
        Assert.Equal("fatal", prepared.Outcome);
        Assert.Equal(1, prepared.ExitCode);
        Assert.Null(prepared.Counts);
        Assert.Equal(new MachineError("prepare", code, fatal.Describe(), EntryIndex: 8, EntryName: RawName,
            Win32Error: 1117), prepared.Error);

        var stopped = DeleteEntry(DeleteStatus.Stopped) with
        {
            Failure = new DeleteFailure(kind, EntryStep.Compare, 1117),
            Reason = "different message\u202E",
        };
        var entry = MachineOutput.Entry(stopped);
        var result = MachineOutput.Result(new DeleteCommandOutcome(ExitStatus.Error, new DeleteReport([stopped], 0, 1)), Info);
        Assert.Equal(code, entry.Reason!.Code);
        Assert.Equal("compare", entry.Reason.Step);
        Assert.Equal(1117, entry.Reason.Win32Error);
        Assert.Equal(entry.Reason.Code, result.Error!.Code);
        Assert.Equal(entry.Reason.Message, result.Error.Message);
        Assert.Equal("entry", result.Error.Stage);
        Assert.Equal(RawName, result.Error.EntryName);
        Assert.Equal(8, result.Error.EntryIndex);
        Assert.Null(result.Error.DeletionStarted);
    }

    [Theory]
    [MemberData(nameof(EntriesCodes))]
    public void J11_EntriesErrorPreservesLineAndOmitsInapplicableFields(EntriesErrorKind kind, string code)
    {
        // Reason \u306F Core \u3067\u8868\u793A\u7528\u306B\u6574\u5F62\u6E08\u307F\u306A\u306E\u3067\u3001message \u3067\u518D\u30A8\u30B9\u30B1\u30FC\u30D7\u3057\u306A\u3044 (\ \u3084 " \u3092\u4E8C\u91CD\u306B\u3057\u306A\u3044)\u3002
        var error = new EntriesError(kind, 9, "(\"dir\\a.txt\") raw\u202E");
        var failure = new PrepareFailure(PrepareStage.Entries, "unrelated", EntriesError: error);
        var result = MachineOutput.Result(new DeleteCommandOutcome(ExitStatus.Error, null, PreparationFailure: failure), null);
        Assert.Equal("input_error", result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Null(result.Counts);
        using var json = JsonDocument.Parse(MachineOutput.Serialize(result));
        var value = json.RootElement.GetProperty("error");
        Assert.Equal(["stage", "code", "line", "message"], Names(value));
        Assert.Equal("prepare", value.GetProperty("stage").GetString());
        Assert.Equal(code, value.GetProperty("code").GetString());
        Assert.Equal(9, value.GetProperty("line").GetInt32());
        Assert.Equal(error.Describe(), value.GetProperty("message").GetString());
    }

    [Theory]
    [MemberData(nameof(SkipCodes))]
    public void J11_SkipReasonIsSharedAndDirectoryDescribesZipKind(SkipReason reason, string code)
    {
        var analyze = MachineOutput.Entry(new EntryResult(new ZipEntryRef(7, RawName), "unused",
            Classification.SkippedSpecialFile, reason, long.MaxValue));
        var delete = MachineOutput.Entry(DeleteEntry(DeleteStatus.SkippedSpecialFile) with { SkipReason = reason });
        foreach (var record in new[] { analyze, delete })
        {
            Assert.Equal(code, record.SkipReason);
            Assert.False(record.Directory); // target の種類が dir でも ZIP の通常ファイル。
            Assert.Equal(8, record.Index);
            Assert.Equal(long.MaxValue, record.Length);
            Assert.Null(record.Reason);
            Assert.Null(record.PossiblyDeleted);
            AssertRawName(record);
        }
    }

    [Theory]
    [MemberData(nameof(DeleteCodes))]
    public void J11_DeleteSpecificCauseIsSharedByEntryAndStop(DeleteFailureKind kind, string code)
    {
        var stop = DeleteEntry(DeleteStatus.Stopped) with
        {
            Reason = "日本語\u202E\\",
            Failure = new DeleteFailure(kind, kind == DeleteFailureKind.UnexpectedException ? null : EntryStep.Open),
        };
        var entry = MachineOutput.Entry(stop);
        var result = MachineOutput.Result(new DeleteCommandOutcome(ExitStatus.Error, new DeleteReport([stop], 0, 1)), Info);
        Assert.Equal(code, entry.Reason!.Code);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(SafeDisplay.Escape(stop.Reason), entry.Reason.Message);
        Assert.Equal(entry.Reason.Message, result.Error.Message);
        Assert.Null(entry.Reason.Win32Error);
        Assert.Null(result.Error.Win32Error);
        Assert.Equal("stopped", result.Outcome); // 捕捉済み例外を internal_error に変えない。
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.Counts!.Unprocessed);
    }

    [Theory]
    [InlineData(EntryStep.Resolve, "resolve")]
    [InlineData(EntryStep.Open, "open")]
    [InlineData(EntryStep.Verify, "verify")]
    [InlineData(EntryStep.Inspect, "inspect")]
    [InlineData(EntryStep.Compare, "compare")]
    [InlineData(EntryStep.FinalCheck, "final_check")]
    [InlineData(EntryStep.Dispose, "dispose")]
    [InlineData(EntryStep.Confirm, "confirm")]
    public void J11_StepsAreExplicit(EntryStep step, string expected)
    {
        var record = MachineOutput.Entry(DeleteEntry(DeleteStatus.Stopped) with
        {
            Failure = new DeleteFailure(FatalKind.TargetInfoFailed, step),
        });
        Assert.Equal(expected, record.Reason!.Step);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J11_StopUncertaintyIsPresentEvenWhenFalse(bool possiblyDeleted)
    {
        var stop = DeleteEntry(DeleteStatus.Stopped) with { PossiblyDeleted = possiblyDeleted };
        var entry = MachineOutput.Entry(stop);
        var result = MachineOutput.Result(new DeleteCommandOutcome(ExitStatus.Error, new DeleteReport([stop], 1, 0)), Info);
        using var entryJson = JsonDocument.Parse(MachineOutput.Serialize(entry));
        using var resultJson = JsonDocument.Parse(MachineOutput.Serialize(result));
        Assert.Equal(possiblyDeleted, entryJson.RootElement.GetProperty("possibly_deleted").GetBoolean());
        Assert.Equal(possiblyDeleted, resultJson.RootElement.GetProperty("error").GetProperty("possibly_deleted").GetBoolean());
        Assert.Equal(["stage", "code", "entry_index", "entry_name", "possibly_deleted", "message"],
            Names(resultJson.RootElement.GetProperty("error")));
    }

    [Theory]
    [InlineData(Classification.Matched, "MATCHED")]
    [InlineData(Classification.SameSize, "SAME_SIZE")]
    [InlineData(Classification.Modified, "MODIFIED")]
    [InlineData(Classification.Missing, "MISSING")]
    [InlineData(Classification.Directory, "DIRECTORY")]
    public void J11_AnalysisEntryHasOnlyAllowedFields(Classification classification, string expected)
    {
        var directory = classification == Classification.Directory;
        var record = MachineOutput.Entry(new EntryResult(new ZipEntryRef(7, RawName), "not exported", classification,
            SkipReason.Attributes, directory ? 0 : long.MaxValue));
        Assert.Equal(expected, record.Status);
        Assert.Equal(directory, record.Directory);
        Assert.Equal(directory ? 0 : long.MaxValue, record.Length);
        AssertRawName(record);
        using var json = JsonDocument.Parse(MachineOutput.Serialize(record));
        Assert.Equal(["v", "type", "index", "name", "directory", "length", "status"], Names(json.RootElement));
    }

    [Theory]
    [InlineData(DeleteStatus.Deleted, "DELETED")]
    [InlineData(DeleteStatus.Modified, "MODIFIED")]
    [InlineData(DeleteStatus.Missing, "MISSING")]
    [InlineData(DeleteStatus.DeleteFailed, "DELETE_FAILED")]
    public void J11_DeleteEntryOmitsStopAndIrrelevantSkipFields(DeleteStatus status, string expected)
    {
        var record = MachineOutput.Entry(DeleteEntry(status) with { SkipReason = SkipReason.Attributes, PossiblyDeleted = true });
        Assert.Equal(expected, record.Status);
        Assert.Null(record.PossiblyDeleted);
        Assert.Null(record.SkipReason);
        Assert.Equal(status == DeleteStatus.DeleteFailed, record.Reason is not null);
        AssertRawName(record);
    }

    [Theory]
    [InlineData(CommandKind.Analyze, RunMode.Strict, "analyze", "strict")]
    [InlineData(CommandKind.Analyze, RunMode.Fast, "analyze", "fast")]
    [InlineData(CommandKind.Delete, RunMode.Strict, "delete", "strict")]
    [InlineData(CommandKind.Delete, RunMode.Fast, "delete", "fast")]
    public void J11_RunRetainsSpecifiedArchiveAndFinalTarget(CommandKind command, RunMode mode, string operation, string modeName)
    {
        var info = Info with { ArchivePath = @"relative\日本語.zip", TargetFinalPath = @"\\?\C:\日本語\u202E", Mode = mode };
        var run = MachineOutput.Run(command, info);
        Assert.Equal(new MachineRunRecord(operation, modeName, info.ArchivePath, @"C:\日本語\u202E", 5, 2, true), run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J11_CompletedDeleteCanExitOneWithoutError(bool failed)
    {
        var results = new[]
        {
            DeleteEntry(DeleteStatus.Deleted), DeleteEntry(DeleteStatus.Modified), DeleteEntry(DeleteStatus.Missing),
            DeleteEntry(DeleteStatus.SkippedSpecialFile) with { SkipReason = SkipReason.Attributes },
            DeleteEntry(failed ? DeleteStatus.DeleteFailed : DeleteStatus.Deleted),
        };
        var report = new DeleteReport(results, 3, 0);
        var info = Info with { TotalEntries = 10, SelectedEntries = 8 };
        var record = MachineOutput.Result(new DeleteCommandOutcome(failed ? ExitStatus.Error : ExitStatus.Success, report), info);
        Assert.Equal("completed", record.Outcome);
        Assert.Equal(failed ? 1 : 0, record.ExitCode);
        Assert.Null(record.Error);
        Assert.Equal(new MachineCounts(Deleted: failed ? 1 : 2, Modified: 1, Missing: 1, SkippedSpecialFile: 1,
            Directory: 3, DeleteFailed: failed ? 1 : 0, NotSelected: 2, Unprocessed: 0), record.Counts);
        AssertDeleteKeys(record);
    }

    [Fact]
    public void J11_EmptyDeleteStillReportsAllZeroCounts()
    {
        var record = MachineOutput.Result(new DeleteCommandOutcome(ExitStatus.Success, new DeleteReport([], 0, 0)),
            Info with { TotalEntries = 0, SelectedEntries = 0 });
        Assert.Equal("completed", record.Outcome);
        Assert.Equal(0, record.ExitCode);
        AssertDeleteKeys(record);
        using var json = JsonDocument.Parse(MachineOutput.Serialize(record));
        Assert.All(json.RootElement.GetProperty("counts").EnumerateObject(), value => Assert.Equal(0, value.Value.GetInt32()));
    }

    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void J11_AnalyzePrepareFatalCountsOnlyKnownUndetermined(RunMode mode)
    {
        var fatal = new FatalError(FatalKind.RootedPath, new ZipEntryRef(1, "/bad"), Step: EntryStep.Resolve);
        var failure = new PrepareFailure(PrepareStage.ZipValidation, "not parsed", fatal, @"\\?\C:\target", 4);
        var outcome = new AnalyzeCommandOutcome(ExitStatus.Error, AnalysisResult.BeforeClassification(4, fatal), fatal, failure);
        var record = MachineOutput.Result(outcome, mode);
        Assert.Equal("fatal", record.Outcome);
        Assert.Equal(1, record.ExitCode);
        Assert.Equal(new MachineCounts(Undetermined: 4), record.Counts);
        Assert.Null(record.Error!.Step);
        Assert.Equal("prepare", record.Error.Stage);
        var delete = MachineOutput.Result(new DeleteCommandOutcome(ExitStatus.Error, null, fatal, failure), null);
        Assert.Null(delete.Counts);
    }

    [Theory]
    [InlineData(PrepareStage.Archive, FatalKind.ArchiveOpenFailed, "fatal")]
    [InlineData(PrepareStage.ProtectedLocations, FatalKind.ProtectedLocationUnresolved, "input_error")]
    [InlineData(PrepareStage.TargetRoot, FatalKind.TargetNotFound, "input_error")]
    public void J11_EarlyPrepareFailureHasNoInventedCounts(PrepareStage stage, FatalKind kind, string expected)
    {
        var failure = new PrepareFailure(stage, "not parsed", new FatalError(kind));
        var record = MachineOutput.Result(new AnalyzeCommandOutcome(ExitStatus.Error, null, PreparationFailure: failure), RunMode.Strict);
        Assert.Equal(expected, record.Outcome);
        Assert.Equal(1, record.ExitCode);
        Assert.Null(record.Counts);
        Assert.Null(record.Error!.EntryIndex);
        Assert.Null(record.Error.EntryName);
        Assert.Null(record.Error.Win32Error);
    }

    [Fact]
    public void J11_UsageAndLogFailuresHaveDistinctStagesAndNoCounts()
    {
        var usage = MachineOutput.UsageError("bad\u202E");
        Assert.Equal(new MachineError("usage", "USAGE", @"bad\u{202E}"), usage.Error);
        foreach (var kind in Enum.GetValues<LogCreationFailureKind>())
        {
            var log = MachineOutput.LogError(new LogCreationFailure(kind, "bad\u202E"));
            Assert.Equal("input_error", log.Outcome);
            Assert.Equal(1, log.ExitCode);
            Assert.Null(log.Counts);
            Assert.Equal("prepare", log.Error!.Stage);
            Assert.Equal(kind == LogCreationFailureKind.AlreadyExists ? "LOG_ALREADY_EXISTS" : "LOG_CREATE_FAILED", log.Error.Code);
            Assert.Equal(@"bad\u{202E}", log.Error.Message);
        }
        Assert.Null(usage.Counts);
    }

    [Fact]
    public void J11_FileWideEntriesErrorOmitsLineAndKnownEmptyPrepareCountIsZero()
    {
        var failure = new PrepareFailure(PrepareStage.Entries, "not parsed",
            EntriesError: new EntriesError(EntriesErrorKind.NoLines, null, "empty"));
        var record = MachineOutput.Result(new DeleteCommandOutcome(ExitStatus.Error, null, PreparationFailure: failure), null);
        using var json = JsonDocument.Parse(MachineOutput.Serialize(record));
        Assert.Equal(["stage", "code", "message"], Names(json.RootElement.GetProperty("error")));
        var fatal = new FatalError(FatalKind.ArchiveIdentityFailed);
        var known = new PrepareFailure(PrepareStage.ArchiveIdentity, "not parsed", fatal, @"\\?\C:\target", 0);
        var result = MachineOutput.Result(new AnalyzeCommandOutcome(ExitStatus.Error,
            AnalysisResult.BeforeClassification(0, fatal), fatal, known), RunMode.Strict);
        Assert.Equal(new MachineCounts(Undetermined: 0), result.Counts);
    }

    [Fact]
    public void J11_DirectoryNotificationsReplaceTheCumulativeCount()
    {
        var progress = new MachineDeleteProgress();
        progress.Prepared(Info with { TotalEntries = 4, SelectedEntries = 4 });
        progress.ObserveDirectoryCount(1);
        progress.Observe(DeleteEntry(DeleteStatus.Deleted));
        progress.ObserveDirectoryCount(2);
        progress.Observe(DeleteEntry(DeleteStatus.Missing));
        Assert.Equal(2, progress.Counts!.Directory);
        Assert.Equal(0, progress.Counts.Unprocessed);
        Assert.Equal(1, progress.Counts.Deleted);
        Assert.Equal(1, progress.Counts.Missing);
    }

    [Theory]
    [InlineData(CommandKind.Analyze, false)]
    [InlineData(CommandKind.Delete, false)]
    [InlineData(CommandKind.Delete, true)]
    public void J11_InternalErrorHasOnlyApplicableDeletionBoundary(CommandKind command, bool started)
    {
        var progress = new MachineDeleteProgress();
        var record = MachineOutput.InternalError(command, new InvalidOperationException("bad\u202E"), started, progress);
        Assert.Equal("internal_error", record.Outcome);
        Assert.Equal(1, record.ExitCode);
        Assert.Equal("internal", record.Error!.Stage);
        Assert.Equal("UNEXPECTED_EXCEPTION", record.Error.Code);
        Assert.Equal(command == CommandKind.Delete ? started : (bool?)null, record.Error.DeletionStarted);
        Assert.Null(record.Counts); // 件数未取得。
        Assert.Null(record.Error.Step);
        Assert.Null(record.Error.PossiblyDeleted);
        Assert.Equal(@"InvalidOperationException: bad\u{202E}", record.Error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J11_ProgressCountsUndeliveredStopAndCompletedDirectories(bool possiblyDeleted)
    {
        var progress = new MachineDeleteProgress();
        progress.Prepared(Info with { TotalEntries = 11, SelectedEntries = 8 });
        progress.Observe(DeleteEntry(DeleteStatus.Deleted));
        progress.Observe(DeleteEntry(DeleteStatus.Modified));
        progress.Observe(DeleteEntry(DeleteStatus.Missing));
        progress.Observe(DeleteEntry(DeleteStatus.SkippedSpecialFile));
        progress.Observe(DeleteEntry(DeleteStatus.DeleteFailed));
        progress.ObserveDirectoryCount(1);
        var stop = DeleteEntry(DeleteStatus.Stopped) with { PossiblyDeleted = possiblyDeleted };
        progress.Observe(stop); // ここで処理済み。entry の配送が失敗しても加算を戻さない。
        var exception = new MachineOutputException(MachineOutputDestination.Stdout, MachineOutputOperation.Write, new IOException("broken"));
        var record = MachineOutput.InternalError(CommandKind.Delete, exception, true, progress);
        Assert.Equal("internal_error", record.Outcome);
        Assert.Equal(1, record.ExitCode);
        Assert.Equal("OUTPUT_FAILED", record.Error!.Code);
        Assert.True(record.Error.DeletionStarted);
        Assert.Equal(possiblyDeleted, record.Error.PossiblyDeleted);
        Assert.Equal(8, record.Error.EntryIndex);
        Assert.Equal(RawName, record.Error.EntryName);
        Assert.Null(record.Error.Step);
        Assert.Null(record.Error.Win32Error);
        Assert.Equal(new MachineCounts(Deleted: 1, Modified: 1, Missing: 1, SkippedSpecialFile: 1,
            Directory: 1, DeleteFailed: 1, NotSelected: 3, Unprocessed: 1), record.Counts);
        AssertDeleteKeys(record);
    }

    [Fact]
    public void J11_ProgressBeforeRunAndAfterLastEntryUsesProcessedFacts()
    {
        var progress = new MachineDeleteProgress();
        progress.Prepared(Info);
        var error = new MachineOutputException(MachineOutputDestination.Log, MachineOutputOperation.Flush, new IOException());
        var before = MachineOutput.InternalError(CommandKind.Delete, error, false, progress);
        Assert.False(before.Error!.DeletionStarted);
        Assert.Equal(2, before.Counts!.Unprocessed);
        progress.ObserveDirectoryCount(1);
        progress.Observe(DeleteEntry(DeleteStatus.Deleted));
        var after = MachineOutput.InternalError(CommandKind.Delete, error, true, progress);
        Assert.Equal(0, after.Counts!.Unprocessed);
        Assert.Equal(1, after.Counts.Deleted);
        Assert.Null(after.Error!.PossiblyDeleted);
        Assert.Null(MachineOutput.InternalError(CommandKind.Analyze, error, false, progress).Counts);
    }

    [Fact]
    public void J11_DiagnosticNumbersComeFromHeldInformation()
    {
        var identity = MachineOutput.Entry(DeleteEntry(DeleteStatus.Stopped) with
        {
            Reason = "open: Win32 32; identity: Win32 1117",
            Failure = new DeleteFailure(DeleteFailureKind.IdentityCheckFailed, EntryStep.Open, 1117),
        });
        Assert.Equal(1117, identity.Reason!.Win32Error);
        Assert.Contains("32", identity.Reason.Message);
        var fetch = MachineOutput.Entry(DeleteEntry(DeleteStatus.Stopped) with
        {
            Failure = new DeleteFailure(FatalKind.TargetInfoFailed, EntryStep.FinalCheck, 5),
        });
        var mismatch = MachineOutput.Entry(DeleteEntry(DeleteStatus.Stopped) with
        {
            Failure = new DeleteFailure(DeleteFailureKind.FinalCheckMismatch, EntryStep.FinalCheck),
        });
        Assert.Equal(new MachineReason("TARGET_INFO_FAILED", "reason", "final_check", 5), fetch.Reason);
        Assert.Equal(new MachineReason("FINAL_CHECK_MISMATCH", "reason", "final_check"), mismatch.Reason);
    }

    [Theory]
    [InlineData(RunMode.Strict, false, false)]
    [InlineData(RunMode.Fast, false, false)]
    [InlineData(RunMode.Strict, true, false)]
    [InlineData(RunMode.Fast, true, false)]
    [InlineData(RunMode.Strict, false, true)]
    [InlineData(RunMode.Fast, false, true)]
    public void J11_ActualAnalysisCountsAndFatalCauseFollowExistingSummary(RunMode mode, bool fail, bool empty)
    {
        var directory = TestFixtures.Create();
        var target = Directory.CreateDirectory(Path.Combine(directory, "target")).FullName;
        var zip = Path.Combine(directory, "archive.zip");
        using (var stream = new FileStream(zip, FileMode.CreateNew))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            if (!empty)
            {
                foreach (var name in new[] { "a", "modified", "missing", "special", "d/", "locked", "last" })
                {
                    var entry = archive.CreateEntry(name);
                    if (!name.EndsWith('/'))
                    {
                        using var writer = new StreamWriter(entry.Open());
                        writer.Write("hello");
                    }
                }
            }
        }
        if (!empty)
        {
            File.WriteAllText(Path.Combine(target, "a"), "hello");
            File.WriteAllText(Path.Combine(target, "modified"), "longer");
            Directory.CreateDirectory(Path.Combine(target, "special"));
            File.WriteAllText(Path.Combine(target, "locked"), "hello");
        }
        using var locked = fail ? new FileStream(Path.Combine(target, "locked"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var context = new CommandContext(new WindowsFileSystemProbe(), () => new(TargetLocationPolicy.None, null),
            Limits.Default, TextWriter.Null, TextWriter.Null, Notifications: new CommandNotifications());
        var outcome = AnalyzeCommand.Run(new AnalyzeCommandRequest(zip, target, mode, context));
        var record = MachineOutput.Result(outcome, mode);
        Assert.Equal(fail ? "fatal" : "completed", record.Outcome);
        Assert.Equal(fail ? 1 : 0, record.ExitCode);
        var counts = record.Counts!;
        Assert.Equal(mode == RunMode.Strict ? (empty ? 0 : fail ? 1 : 2) : (int?)null, counts.Matched);
        Assert.Equal(mode == RunMode.Fast ? (empty ? 0 : fail ? 1 : 2) : (int?)null, counts.SameSize);
        Assert.Equal(empty ? 0 : 1, counts.Modified);
        Assert.Equal(empty ? 0 : fail ? 1 : 2, counts.Missing);
        Assert.Equal(empty ? 0 : 1, counts.SkippedSpecialFile);
        Assert.Equal(empty ? 0 : 1, counts.Directory);
        Assert.Equal(fail ? 1 : (int?)null, counts.Undetermined); // 原因の locked は未判定へ足さない。
        if (fail)
        {
            Assert.Equal("entry", record.Error!.Stage);
            Assert.Equal("open", record.Error.Step);
            Assert.Equal("COMPARISON_OPEN_FAILED", record.Error.Code);
            Assert.Equal(6, record.Error.EntryIndex);
            Assert.Equal("locked", record.Error.EntryName);
            Assert.Equal(32, record.Error.Win32Error);
        }
        else Assert.Null(record.Error);
        using var json = JsonDocument.Parse(MachineOutput.Serialize(record));
        var keys = new List<string> { mode == RunMode.Strict ? "matched" : "same_size", "modified", "missing", "skipped_special_file", "directory" };
        if (fail) keys.Add("undetermined");
        Assert.Equal(keys, Names(json.RootElement.GetProperty("counts")));
        Assert.Null(record.Error?.DeletionStarted);
    }

    private const string RawName = "日本語-é\\\u0085\u202E\u2028\u2029\U0001F642.txt";
    private static PreparedCommandInfo Info => new(@"relative\archive.zip", @"\\?\C:\target", RunMode.Strict, 5, 2, true);
    private static DeleteEntryResult DeleteEntry(DeleteStatus status) => new(new ZipEntryRef(7, RawName), "unused", status,
        Reason: "reason", Failure: new DeleteFailure(status == DeleteStatus.DeleteFailed
            ? DeleteFailureKind.DeleteOpenRefused : DeleteFailureKind.UnexpectedException, null), Length: long.MaxValue);

    private static void AssertRawName(MachineEntryRecord record)
    {
        using var json = JsonDocument.Parse(MachineOutput.Serialize(record));
        Assert.Equal(RawName, json.RootElement.GetProperty("name").GetString());
        Assert.False(json.RootElement.TryGetProperty("target", out _));
        Assert.Equal(8, json.RootElement.GetProperty("index").GetInt32());
    }

    private static string[] Names(JsonElement value) => value.EnumerateObject().Select(p => p.Name).ToArray();

    private static void AssertDeleteKeys(MachineResultRecord record)
    {
        using var json = JsonDocument.Parse(MachineOutput.Serialize(record));
        Assert.Equal(["modified", "missing", "skipped_special_file", "directory", "deleted", "delete_failed", "not_selected", "unprocessed"],
            Names(json.RootElement.GetProperty("counts")));
    }
}
