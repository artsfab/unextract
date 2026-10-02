using Unextract.Core.Analysis;
using Unextract.Core.Results;
using Unextract.Core.Zip;
using Xunit.Abstractions;
using static Unextract.Windows.Tests.Integration.RealRun;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests.Integration;

// 入力エラーと ZIP・target の保持、dry-run と通常実行の一致を実 NTFS で確認する。fixture は削除しない。
public class InputIntegrationTests(ITestOutputHelper output)
{
    private static readonly byte[] Hello = Bytes("hello");

    // P03: 読み取り権限のない ZIP → ZIP を開けない (入力エラー)。読み取り (一覧) 権限のない target → 入力エラー。
    // ACL は対象の読み取り (RD) だけを自分の SID で拒否し、finally で必ずその DENY を除去して元に戻す (ファイルは削除しない)。
    // 戻せなかった場合はパスと理由をテスト出力に残す (失敗にはしない)。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void P03_NoReadPermission_IsInputError(RunMode mode)
    {
        var dir = CreateDirectory();
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("a.txt", Hello)));
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var lockedTarget = Directory.CreateDirectory(Path.Combine(dir, "locked-target")).FullName;
        var lockedZip = WriteZip(Path.Combine(dir, "locked.zip"), Zip(("a.txt", Hello)));
        using var acl = new AclChanges(output);
        acl.Deny(lockedZip, "RD");
        acl.Deny(lockedTarget, "RD");

        var zipResult = ZipArchiveSource.Open(lockedZip);
        Assert.Null(zipResult.Source);
        Assert.Equal(FatalKind.ArchiveOpenFailed, zipResult.Fatal!.Kind);

        var targetResult = Run(zip, lockedTarget, mode: mode);
        Assert.Equal(ExitStatus.Error, targetResult.Outcome.Status);
        Assert.Equal(FatalKind.TargetCheckFailed, targetResult.Outcome.InputError?.Kind);
        Assert.Contains("Win32 エラー 5", targetResult.Error, StringComparison.Ordinal);

        // 対照: 権限のある target では通る。
        Assert.Null(Run(zip, target, mode: mode).Outcome.InputError);
    }

    // P04: ZIP を保持している間、別ハンドルでの書き込みオープンと改名が失敗する。既に書き込み用に開かれている ZIP は開けない。
    [Fact]
    public void P04_ArchiveIsHeldDuringRun()
    {
        var dir = CreateDirectory();
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("a.txt", Hello)));

        using (var source = ZipArchiveSource.Open(zip).Source!)
        {
            var write = Assert.Throws<IOException>(() => new FileStream(zip, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            Assert.Equal(32, Win32Code(write));
            var move = Assert.Throws<IOException>(() => File.Move(zip, Path.Combine(dir, "renamed.zip")));
            Assert.Equal(32, Win32Code(move));
        }

        using (new FileStream(zip, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            var opened = ZipArchiveSource.Open(zip);
            Assert.Null(opened.Source);
            Assert.Equal(FatalKind.ArchiveOpenFailed, opened.Fatal!.Kind);
        }
    }

    // P04 (実行中、両モード): runner の実行中 (解析完了直後) に、別ハンドルでの ZIP の書き込みオープンと改名が共有違反 (32) で失敗し、
    // ZIP は変わらない。--fast でも ZIP の保持は Strict と同じ (SPEC §15、ZIP の保持はモードに依存しない)。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void P04_ArchiveIsHeldDuringRunnerExecution(RunMode mode)
    {
        var dir = CreateDirectory();
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("a.txt", Hello)));
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        File.WriteAllBytes(Path.Combine(target, "a.txt"), Hello);
        var before = (Hash(zip), File.GetLastWriteTimeUtc(zip));
        var codes = new List<int>();

        var result = Run(zip, target, mode: mode, hooks: new RunHooks
        {
            AfterAnalysis = () =>
            {
                codes.Add(Win32Code(Assert.Throws<IOException>(() => new FileStream(zip, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))));
                codes.Add(Win32Code(Assert.Throws<IOException>(() => File.Move(zip, Path.Combine(dir, "renamed.zip")))));
            },
        });

        Assert.Equal([32, 32], codes);
        Assert.Equal(ExitStatus.Success, result.Outcome.Status);
        Assert.Equal(before, (Hash(zip), File.GetLastWriteTimeUtc(zip)));
        Assert.False(File.Exists(Path.Combine(dir, "renamed.zip")));
    }

    // P06: target が存在しない・ファイル・最終成分が junction → 入力エラー。作成・変更なし。
    [Theory]
    [InlineData("missing", FatalKind.TargetNotFound, RunMode.Strict)]
    [InlineData("file", FatalKind.TargetNotDirectory, RunMode.Strict)]
    [InlineData("junction", FatalKind.TargetIsReparsePoint, RunMode.Strict)]
    [InlineData("missing", FatalKind.TargetNotFound, RunMode.Fast)]
    [InlineData("file", FatalKind.TargetNotDirectory, RunMode.Fast)]
    [InlineData("junction", FatalKind.TargetIsReparsePoint, RunMode.Fast)]
    public void P06_InvalidTarget_IsInputError(string kind, FatalKind expected, RunMode mode)
    {
        var dir = CreateDirectory($"{nameof(P06_InvalidTarget_IsInputError)}-{kind}");
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("a.txt", Hello)));
        var real = Directory.CreateDirectory(Path.Combine(dir, "real")).FullName;
        File.WriteAllBytes(Path.Combine(real, "a.txt"), Hello);
        var target = Path.Combine(dir, "target");
        switch (kind)
        {
            case "file":
                File.WriteAllBytes(target, Hello);
                break;
            case "junction":
                CreateJunction(target, real);
                break;
        }

        var before = Snapshot(dir);

        var result = Run(zip, target, mode: mode);

        Assert.Equal(ExitStatus.Error, result.Outcome.Status);
        Assert.Equal(expected, result.Outcome.InputError?.Kind);
        Assert.Null(result.Outcome.Analysis);
        Assert.Equal(before, Snapshot(dir));
    }

    // P06: 拒否対象 (ユーザープロファイルそのもの、Windows ディレクトリとその配下、ProgramData、ドライブルート) → 入力エラー。
    // 確認用ハンドルと保持用ハンドル (読み取り属性・一覧) を開くだけで、列挙・比較・変更はしない。
    [Theory]
    [InlineData(Environment.SpecialFolder.UserProfile, null, FatalKind.TargetIsProtectedLocation, RunMode.Strict)]
    [InlineData(Environment.SpecialFolder.Windows, null, FatalKind.TargetIsProtectedLocation, RunMode.Strict)]
    [InlineData(Environment.SpecialFolder.Windows, "System32", FatalKind.TargetIsProtectedLocation, RunMode.Strict)]
    [InlineData(Environment.SpecialFolder.CommonApplicationData, null, FatalKind.TargetIsProtectedLocation, RunMode.Strict)]
    [InlineData(Environment.SpecialFolder.ProgramFiles, null, FatalKind.TargetIsProtectedLocation, RunMode.Strict)]
    [InlineData(Environment.SpecialFolder.UserProfile, null, FatalKind.TargetIsProtectedLocation, RunMode.Fast)]
    [InlineData(Environment.SpecialFolder.Windows, null, FatalKind.TargetIsProtectedLocation, RunMode.Fast)]
    [InlineData(Environment.SpecialFolder.Windows, "System32", FatalKind.TargetIsProtectedLocation, RunMode.Fast)]
    [InlineData(Environment.SpecialFolder.CommonApplicationData, null, FatalKind.TargetIsProtectedLocation, RunMode.Fast)]
    [InlineData(Environment.SpecialFolder.ProgramFiles, null, FatalKind.TargetIsProtectedLocation, RunMode.Fast)]
    public void P06_ProtectedLocation_IsInputError(Environment.SpecialFolder folder, string? child, FatalKind expected, RunMode mode)
    {
        var dir = CreateDirectory($"{nameof(P06_ProtectedLocation_IsInputError)}-{folder}");
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("a.txt", Hello)));
        var target = Environment.GetFolderPath(folder);
        if (child is not null)
        {
            target = Path.Combine(target, child);
        }

        var result = Run(zip, target, mode: mode);

        Assert.Equal(expected, result.Outcome.InputError?.Kind);
        Assert.Null(result.Outcome.Analysis);
    }

    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void P06_DriveRoot_IsInputError(RunMode mode)
    {
        var dir = CreateDirectory();
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("a.txt", Hello)));

        var result = Run(zip, Path.GetPathRoot(dir)!, mode: mode);

        Assert.Equal(FatalKind.TargetIsDriveRoot, result.Outcome.InputError?.Kind);
    }

    // プロファイル配下の通常ディレクトリ (この fixture 自体がそう) は許可される
    [Fact]
    public void FixtureUnderProfile_IsAllowed()
    {
        var dir = CreateDirectory();
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("a.txt", Hello)));
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        output.WriteLine(target);

        Assert.Null(Run(zip, target).Outcome.InputError);
    }

    // Y01 (実機): 同じ ZIP・同じ target で --dry-run と通常実行を行い、初回分類と表示が一致する。
    // dry-run と通常実行の前後で target 全体 (パス・サイズ・SHA-256・更新日時) と ZIP (SHA-256・更新日時) が変わらない。
    // 一致は同じモード同士の規定 (SPEC §2、§15.1)。Fast 同士でも確かめる。Fast では changed.txt (同じサイズで内容違い) も削除候補 (SAME_SIZE)。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void Y01_DryRunAndNormalRun_AgreeAndChangeNothing(RunMode mode)
    {
        var dir = CreateDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        File.WriteAllBytes(Path.Combine(target, "same.txt"), Hello);
        File.WriteAllBytes(Path.Combine(target, "changed.txt"), Bytes("hellO"));
        Directory.CreateDirectory(Path.Combine(target, "d"));
        File.WriteAllBytes(Path.Combine(target, "d", "deep.txt"), Hello);
        File.WriteAllText(Path.Combine(target, "d", "deep.txt") + ":Zone.Identifier", "[ZoneTransfer]");
        File.WriteAllBytes(Path.Combine(target, "unrelated.txt"), Bytes("u"));
        var zip = WriteZip(
            Path.Combine(dir, "archive.zip"),
            Zip(("same.txt", Hello), ("changed.txt", Hello), ("missing.txt", Hello), ("d/", null), ("d/deep.txt", Hello)));
        var targetBefore = Snapshot(target);
        var zipBefore = (Hash(zip), File.GetLastWriteTimeUtc(zip));

        var dry = Run(zip, target, dryRun: true, mode: mode);
        Assert.Equal(targetBefore, Snapshot(target));
        var normal = Run(zip, target, dryRun: false, mode: mode);

        Assert.Equal(targetBefore, Snapshot(target));
        Assert.Equal(zipBefore, (Hash(zip), File.GetLastWriteTimeUtc(zip)));
        Assert.Equal(dry.Outcome.ReportLines, normal.Outcome.ReportLines);
        Assert.Equal(
            dry.Analysis.Results.Select(r => (r.Entry, r.Classification, r.SkipReason)),
            normal.Analysis.Results.Select(r => (r.Entry, r.Classification, r.SkipReason)));
        Assert.Equal(ExitStatus.Success, dry.Outcome.Status);
        Assert.Equal(ExitStatus.UserCancelled, normal.Outcome.Status);
        Assert.Contains("中止しました。削除0件。", normal.Output, StringComparison.Ordinal);
        Assert.Equal(mode == RunMode.Fast ? 2 : 1, normal.Analysis.DeletionCandidates.Count);
    }
}
