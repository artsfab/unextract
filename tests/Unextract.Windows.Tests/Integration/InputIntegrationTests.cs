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

        var targetResult = Analyze(zip, lockedTarget, mode: mode);
        Assert.Equal(ExitStatus.Error, targetResult.Status);
        Assert.Equal(FatalKind.TargetCheckFailed, targetResult.PrepareError?.Kind);
        Assert.Contains("Win32 エラー 5", targetResult.Error, StringComparison.Ordinal);

        // 対照: 権限のある target では通る。
        Assert.Null(Analyze(zip, target, mode: mode).PrepareError);
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

    // P04 (実行中、両モード): analyze の実行中 (結果表示の直後) と delete の逐次処理中 (最初のエントリの削除用オープンの直前) に、
    // 別ハンドルでの ZIP の書き込みオープンと改名が共有違反 (32) で失敗し、ZIP は変わらない。--fast でも ZIP の保持は Strict と同じ。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void P04_ArchiveIsHeldDuringExecution(RunMode mode)
    {
        var dir = CreateDirectory();
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("a.txt", Hello)));
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        File.WriteAllBytes(Path.Combine(target, "a.txt"), Hello);
        var before = (Hash(zip), File.GetLastWriteTimeUtc(zip));
        var codes = new List<int>();
        void TryChangeArchive()
        {
            codes.Add(Win32Code(Assert.Throws<IOException>(() => new FileStream(zip, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))));
            codes.Add(Win32Code(Assert.Throws<IOException>(() => File.Move(zip, Path.Combine(dir, "renamed.zip")))));
        }

        var analyzed = Analyze(zip, target, mode: mode, afterResults: TryChangeArchive);
        var deleted = Delete(zip, target, new DeletionGuard(dir), mode: mode, hooks: new Unextract.Core.Deletion.DeleteHooks { BeforeOpen = (_, _) => TryChangeArchive() });

        Assert.Equal([32, 32, 32, 32], codes);
        Assert.Equal(ExitStatus.Success, analyzed.Status);
        Assert.Equal(ExitStatus.Success, deleted.Status);
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

        var result = Analyze(zip, target, mode: mode);

        Assert.Equal(ExitStatus.Error, result.Status);
        Assert.Equal(expected, result.PrepareError?.Kind);
        Assert.Null(result.AnalysisOrNull);
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

        var result = Analyze(zip, target, mode: mode);

        Assert.Equal(expected, result.PrepareError?.Kind);
        Assert.Null(result.AnalysisOrNull);
    }

    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void P06_DriveRoot_IsInputError(RunMode mode)
    {
        var dir = CreateDirectory();
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("a.txt", Hello)));

        var result = Analyze(zip, Path.GetPathRoot(dir)!, mode: mode);

        Assert.Equal(FatalKind.TargetIsDriveRoot, result.PrepareError?.Kind);
    }

    // プロファイル配下の通常ディレクトリ (この fixture 自体がそう) は許可される
    [Fact]
    public void FixtureUnderProfile_IsAllowed()
    {
        var dir = CreateDirectory();
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("a.txt", Hello)));
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        output.WriteLine(target);

        Assert.Null(Analyze(zip, target).PrepareError);
    }

    // A01・P10 (実機): analyze と、確認で n と答えた delete は、target 全体 (パス・サイズ・SHA-256・更新日時) と ZIP (SHA-256・更新日時) を
    // 変えない。delete は確認の前に target のエントリを開かない (削除用オープン0回)。Strict と Fast の両方で確かめる。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void A01_P10_AnalyzeAndCancelledDelete_ChangeNothing(RunMode mode)
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

        var analyzed = Analyze(zip, target, mode: mode);
        Assert.Equal(targetBefore, Snapshot(target));
        var cancelled = Delete(zip, target, new DeletionGuard(dir), mode: mode, answer: "n");

        Assert.Equal(targetBefore, Snapshot(target));
        Assert.Equal(zipBefore, (Hash(zip), File.GetLastWriteTimeUtc(zip)));
        Assert.Equal(ExitStatus.Success, analyzed.Status);
        Assert.Equal(mode == RunMode.Fast ? 2 : 1, analyzed.Analysis.Results.Count(r => r.Classification == Candidate(mode)));
        Assert.Equal(ExitStatus.UserCancelled, cancelled.Status);
        Assert.Null(cancelled.ReportOrNull);
        Assert.Empty(cancelled.Probe!.Opened);
        Assert.Contains("中止しました。削除0件。", cancelled.Output, StringComparison.Ordinal);
    }
}
