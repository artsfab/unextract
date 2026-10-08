using Unextract.Core.Analysis;
using Unextract.Core.Commands;
using Unextract.Core.Results;

namespace Unextract.Cli.Tests;

// J11 (RAR): RAR の Prepare の失敗の v1 の result (docs/spec/machine-output.md#result、docs/spec/rar.md#listing)。writer は使わない。
public class RarMachineRecordTests
{
    // 手順2の FATAL (ボリューム、DLL、列挙中の上限) は target ルート確認前なので counts を省略する。上限は原因エントリを持つ。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void J11_RarArchiveStageFatal_OmitsCounts(RunMode mode)
    {
        var cases = new (FatalError Fatal, string Code, int? Index, string? Name)[]
        {
            (new FatalError(FatalKind.ArchiveMultiVolume), "ARCHIVE_MULTI_VOLUME", null, null),
            (FatalError.RarLibraryUnavailable(RarLibraryFailure.NotFound, @"C:\app\UnRAR64.dll"), "RAR_LIBRARY_UNAVAILABLE", null, null),
            (new FatalError(FatalKind.NameTooLong, new ZipEntryRef(2, "c.txt")), "NAME_TOO_LONG", 3, "c.txt"),
        };
        foreach (var (fatal, code, index, name) in cases)
        {
            var failure = new PrepareFailure(PrepareStage.Archive, "not parsed", fatal);
            var analyze = MachineOutput.Result(new AnalyzeCommandOutcome(ExitStatus.Error, null, fatal, failure), mode);
            var delete = MachineOutput.Result(new DeleteCommandOutcome(ExitStatus.Error, null, fatal, failure), null);
            foreach (var record in new[] { analyze, delete })
            {
                Assert.Equal("fatal", record.Outcome);
                Assert.Equal(1, record.ExitCode);
                Assert.Null(record.Counts);
                Assert.Equal("prepare", record.Error!.Stage);
                Assert.Equal(code, record.Error.Code);
                Assert.Equal(index, record.Error.EntryIndex);
                Assert.Equal(name, record.Error.EntryName);
                Assert.Equal(fatal.Describe(), record.Error.Message);
            }
        }
    }

    // DLL の FATAL の message は人間向けと同じ説明 (docs/spec/rar.md#pinning)。
    [Fact]
    public void J11_RarLibraryUnavailable_MessageIsHumanDescription()
    {
        var fatal = FatalError.RarLibraryUnavailable(RarLibraryFailure.VersionMismatch, @"C:\app\UnRAR64.dll", version: 9);
        var failure = new PrepareFailure(PrepareStage.Archive, "not parsed", fatal);

        var record = MachineOutput.Result(new AnalyzeCommandOutcome(ExitStatus.Error, null, fatal, failure), RunMode.Strict);

        Assert.Equal(
            @"UnRAR.dll を使用できないため、RAR を処理できません (版が一致しません (RARGetDllVersion=9))。C:\app\UnRAR64.dll に UnRAR.dll 7.23 (x64) の UnRAR64.dll を置いてください。ZIP の処理には影響しません。",
            record.Error!.Message);
    }

    // 手順10 の失敗は target ルート確認後の FATAL で、analyze は判明済みの未判定だけを数える。delete は counts を省略する。
    [Fact]
    public void J11_ContentSessionFailure_CountsLikeOtherPrepareFatalsAfterTargetRoot()
    {
        var fatal = new FatalError(FatalKind.ArchiveOpenFailed, Detail: "ERAR_EOPEN", Format: Core.Zip.ArchiveFormat.Rar);
        var failure = new PrepareFailure(PrepareStage.ContentSession, "not parsed", fatal, @"\\?\C:\target", 3);
        var outcome = new AnalyzeCommandOutcome(ExitStatus.Error, AnalysisResult.BeforeClassification(3, fatal), fatal, failure);

        var record = MachineOutput.Result(outcome, RunMode.Strict);

        Assert.Equal("fatal", record.Outcome);
        Assert.Equal(new MachineCounts(Undetermined: 3), record.Counts);
        Assert.Equal("ARCHIVE_OPEN_FAILED", record.Error!.Code);
        Assert.Equal("RAR を開けません (ERAR_EOPEN)", record.Error.Message);
        Assert.Null(MachineOutput.Result(new DeleteCommandOutcome(ExitStatus.Error, null, fatal, failure), null).Counts);
    }
}
