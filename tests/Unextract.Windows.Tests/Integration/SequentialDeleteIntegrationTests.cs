using Microsoft.Win32.SafeHandles;
using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Windows.Tests.Helper;
using Xunit.Abstractions;
using static Unextract.Windows.Tests.Integration.RealRun;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests.Integration;

// delete の逐次処理の実 NTFS テスト (PLAN_TESTS.md の S 系の Win、A07・A08・L16 と実測項目 S26・S34・S37・S38)。実際に削除する。
// - 削除してよいのは、各テストが自分で作った一意な fixture (bin/.../fixtures/<一意名>/) の中のファイルだけ。fixture を最初に確定し、
//   削除の指示の直前に毎回 DeletionGuard が削除用ハンドルの最終パスと親を確かめる (RealRun.Delete)。
// - junction は同じ fixture 内の別ディレクトリを指すものだけを作る。symlink は作らない。
// - fixture のディレクトリ自体は削除しない。ACL を変えるテストは AclChanges で必ず戻す。
// - テスト自身はパスベースの削除をしない (差し替えは改名で行う)。上書きの改名 (S21 の置換) は、上書きされる側をガードで確かめてから行う。
// 競合は DeleteHooks で注入する: H1 = 削除用オープンの直前 (列挙の後)、H2 = オープン直後、H3 = 全バイト比較中 (Strict のみ)、
// H4 = 最終確認の直前、H5 = 削除の指示の直前。「別プロセス」は HelperProcess (このテストアセンブリのビルド済み実行ファイル) で行う。
// 属性・ADS・hardlink の変更は同一プロセスで行う (削除用ハンドルを開いている間でも別プロセスから同じ変更ができ、同じく検出されることは
// SPEC §13 の PoC 6 で確認済み)。
public class SequentialDeleteIntegrationTests(ITestOutputHelper output)
{
    private static readonly byte[] A = Bytes("alpha content");
    private static readonly byte[] B = Bytes("bravo content");
    private static readonly byte[] C = Bytes("charlie content");

    private sealed record Fixture(string Dir, string Target, string Zip, DeletionGuard Guard)
    {
        public string Path(string relative) => System.IO.Path.Combine(Target, relative);

        public bool Exists(string relative) => File.Exists(Path(relative));
    }

    // fixture: dir/archive.zip、dir/target/。target に ZIP の内容どおりのファイル (ディレクトリエントリはディレクトリ) を置く。
    private static Fixture Create(string name, params (string Name, byte[]? Content)[] entries)
    {
        var dir = CreateDirectory(name);
        var target = Directory.CreateDirectory(System.IO.Path.Combine(dir, "target")).FullName;
        foreach (var (entry, content) in entries)
        {
            var path = System.IO.Path.Combine(target, entry.Replace('/', '\\'));
            if (content is null)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }
        }

        var zip = WriteZip(System.IO.Path.Combine(dir, "archive.zip"), Zip(entries));
        return new Fixture(dir, target, zip, new DeletionGuard(dir));
    }

    // a.txt (前に削除済み)、p/b.txt (対象)、c.txt (後の未処理) の3件。
    private static Fixture CreateApc(string name) => Create(name, ("a.txt", A), ("p/", null), ("p/b.txt", B), ("c.txt", C));

    // a.txt、b.txt、c.txt の3件。
    private static Fixture CreateAbc(string name) => Create(name, ("a.txt", A), ("b.txt", B), ("c.txt", C));

    // 指定したエントリの時だけ action を呼ぶフック。
    private static Action<ZipEntryRef, IDeletionHandle> When(string name, Action action) => (entry, _) =>
    {
        if (entry.Name == name)
        {
            action();
        }
    };

    // 指定したエントリの時だけ action を呼ぶ H1 (削除用オープンの直前) のフック。
    private static Action<ZipEntryRef, string> BeforeOpenOf(string name, Action action) => (entry, _) =>
    {
        if (entry.Name == name)
        {
            action();
        }
    };

    private void Log(Result result)
    {
        output.WriteLine("--- stdout ---");
        output.WriteLine(result.Output);
        output.WriteLine("--- stderr ---");
        output.WriteLine(result.Error);
    }

    // 対象 name で STOP し、a.txt は削除済み、対象と c.txt は残る (c.txt は未処理)。
    private DeleteEntryResult AssertStoppedAt(Fixture f, Result r, string name, string reasonStart)
    {
        Log(r);
        Assert.Equal(ExitStatus.Error, r.Status);
        Assert.Equal(["a.txt"], r.DeletedNames);
        Assert.DoesNotContain(r.Report.Results, x => x.Status == DeleteStatus.DeleteFailed);
        var stop = r.Report.Stop!;
        Assert.Equal(name, stop.Entry.Name);
        Assert.StartsWith(reasonStart, stop.Reason, StringComparison.Ordinal);
        Assert.False(stop.PossiblyDeleted);
        Assert.Equal(1, r.Report.NotProcessedCount);
        Assert.False(f.Exists("a.txt"));
        Assert.True(f.Exists("c.txt"));
        Assert.True(File.Exists(f.Zip));
        Assert.Contains("途中で停止しました。それまでに削除した 1 件は元に戻りません。", r.Output, StringComparison.Ordinal);
        Assert.Contains($"停止: エントリ #", r.Error, StringComparison.Ordinal);
        Assert.False(r.Opened("c.txt"));
        return stop;
    }

    // S01: 検証済みのファイルだけを個別に削除。ZIP、ZIP にない target のファイル、MODIFIED・特殊なファイル、ディレクトリは残る。
    // Fast では同じサイズで内容違いの changed.txt も削除される (SPEC §15.3、§15.5)。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void S01_DeletesOnlyVerifiedFiles(RunMode mode)
    {
        var f = Create(
            $"{nameof(S01_DeletesOnlyVerifiedFiles)}-{mode}",
            ("same.txt", A), ("changed.txt", B), ("d/", null), ("d/deep.txt", C), ("d/ads.txt", A), ("empty/", null));
        File.WriteAllBytes(f.Path("changed.txt"), Bytes("bravo CONTENT"));
        File.WriteAllText(f.Path(@"d\ads.txt") + ":Zone.Identifier", "[ZoneTransfer]");
        File.WriteAllBytes(f.Path("unrelated.txt"), Bytes("u"));
        File.WriteAllBytes(f.Path(@"d\unrelated.txt"), Bytes("u"));
        var zipHash = Hash(f.Zip);
        var before = Snapshot(f.Target);

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode);

        Log(r);
        var fast = mode == RunMode.Fast;
        string[] deleted = fast ? ["same.txt", "changed.txt", "d/deep.txt"] : ["same.txt", "d/deep.txt"];
        Assert.Equal(ExitStatus.Success, r.Status);
        Assert.Equal(deleted, r.DeletedNames);
        Assert.Equal(deleted.Length, f.Guard.CheckCount);
        var after = Snapshot(f.Target);
        before.Remove("same.txt");
        before.Remove(@"d\deep.txt");
        if (fast)
        {
            before.Remove("changed.txt");
        }

        before.Remove("d");
        after.Remove("d");
        Assert.Equal(before, after);
        Assert.True(Directory.Exists(f.Path("d")));
        Assert.True(Directory.Exists(f.Path("empty")));
        Assert.Equal(zipHash, Hash(f.Zip));
    }

    // S09: delete 自身が ZIP を DELETE を共有せず保持するため、削除用オープンは共有違反 (32)。
    // 識別確認が列挙由来の基準と一致 → DELETE_FAILED、終了 1、ZIP を残し後続を削除する (SPEC §8.1・§8.4)。
    // 2026-10-03 の人間判断で正式な回帰要件に確定。analyze は SKIPPED (ZIP 自身)。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void S09_ArchiveItselfIsNotDeleted(RunMode mode)
    {
        var dir = CreateDirectory($"{nameof(S09_ArchiveItselfIsNotDeleted)}-{mode}");
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var zip = WriteZip(Path.Combine(target, "archive.zip"), Zip(("archive.zip", A), ("later.txt", B)));
        var later = Path.Combine(target, "later.txt");
        File.WriteAllBytes(later, B);
        var hash = Hash(zip);

        var analyzed = Analyze(zip, target, mode: mode);
        var r = Delete(zip, target, new DeletionGuard(dir), mode: mode);

        Log(r);
        Assert.Equal(SkipReason.ArchiveItself, analyzed.SkipOf("archive.zip"));
        var result = r.ResultOf("archive.zip");
        Assert.Equal(DeleteStatus.DeleteFailed, result.Status);
        Assert.StartsWith("削除用に開けません (Win32 エラー 32", result.Reason, StringComparison.Ordinal);
        Assert.Equal(ExitStatus.Error, r.Status);
        Assert.Equal(1, ExitCodes.ToProcessExitCode(r.Status));
        Assert.Equal(["archive.zip", "later.txt"], r.Report.Results.Select(x => x.Entry.Name));
        Assert.Equal(["later.txt"], r.DeletedNames);
        Assert.Null(r.Report.Stop);
        Assert.Equal(0, r.Report.NotProcessedCount);
        Assert.Equal(result.Target, Assert.Single(r.Probe!.IdentityChecked));
        Assert.True(r.Opened("later.txt"));
        Assert.False(File.Exists(later));
        Assert.True(File.Exists(zip));
        Assert.Equal(hash, Hash(zip));
    }

    // S13 (Win): Strict の比較で ZIP 側の異常 (CRC 不一致) → そのエントリは STOP で残り、以後は未処理。それ以前の削除は残る。
    [Fact]
    public void S13_CrcMismatchDuringCompare_Stops()
    {
        var f = Create(nameof(S13_CrcMismatchDuringCompare_Stops), ("a.txt", A), ("b.txt", B), ("c.txt", C));
        var patcher = new Core.Tests.Fixtures.ZipPatcher(File.ReadAllBytes(f.Zip));
        var zip = WriteZip(Path.Combine(f.Dir, "crc.zip"), patcher.SetCrc32(1, patcher.GetCrc32(1) ^ 1).ToArray());

        var r = Delete(zip, f.Target, f.Guard);

        AssertStoppedAt(f, r, "b.txt", "全バイト比較で異常: エントリの CRC-32 が一致しません");
        Assert.Equal(B, File.ReadAllBytes(f.Path("b.txt")));
    }

    // S16: 削除用オープンが 32 (他プロセスが書き込み中 / FILE_SHARE_DELETE なしで読み取り中)、5 (READ_DATA の ACL 拒否 / 対象の DELETE と
    // 親の DELETE_CHILD の拒否) で、識別確認が列挙由来の基準と一致 → DELETE_FAILED で続行。理由に「内容は確認していません」(Strict でも)。
    // 後続の安全な対象は削除される。終了 1。識別確認は拒否の理由も同じ個体であることも保証しない (一致しても削除しないことを確かめる)。
    [Theory]
    [InlineData("hold-write", 32, RunMode.Strict)]
    [InlineData("hold-read-share-read", 32, RunMode.Strict)]
    [InlineData("acl-read-data", 5, RunMode.Strict)]
    [InlineData("acl-delete-and-delete-child", 5, RunMode.Strict)]
    [InlineData("hold-write", 32, RunMode.Fast)]
    [InlineData("hold-read-share-read", 32, RunMode.Fast)]
    [InlineData("acl-read-data", 5, RunMode.Fast)]
    [InlineData("acl-delete-and-delete-child", 5, RunMode.Fast)]
    public void S16_OpenDeniedButLooksSame_IsDeleteFailed(string situation, int expectedError, RunMode mode)
    {
        var f = CreateAbc($"{nameof(S16_OpenDeniedButLooksSame_IsDeleteFailed)}-{situation}-{mode}");
        var b = f.Path("b.txt");
        HelperProcess? helper = null;
        Result r;
        using (var acl = new AclChanges(output))
        {
            try
            {
                switch (situation)
                {
                    case "hold-write":
                    case "hold-read-share-read":
                        helper = HelperProcess.Hold(situation, b);
                        break;
                    case "acl-read-data":
                        acl.Deny(b, "RD");
                        break;
                    case "acl-delete-and-delete-child":
                        acl.Deny(f.Target, "DC");
                        acl.Deny(b, "DE");
                        break;
                }

                r = Delete(f.Zip, f.Target, f.Guard, mode: mode);
            }
            finally
            {
                helper?.Dispose();
            }
        }

        Log(r);
        Assert.Equal(ExitStatus.Error, r.Status);
        Assert.Null(r.Report.Stop);
        Assert.Equal(["a.txt", "c.txt"], r.DeletedNames);
        var failure = r.ResultOf("b.txt");
        Assert.Equal(DeleteStatus.DeleteFailed, failure.Status);
        Assert.Contains($"Win32 エラー {expectedError}", failure.Reason, StringComparison.Ordinal);
        Assert.EndsWith("識別確認の時点では同じファイルに見えるため、削除せずに残しました。内容は確認していません", failure.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(b));
        Assert.Equal(2, f.Guard.CheckCount);
        Assert.Contains("DELETE_FAILED が 1 件あるため、エラーとして終了します", r.Error, StringComparison.Ordinal);
    }

    // S17: 削除用オープンで同一性に疑義 (H1 で: 対象消失 2、親の消失 3、ディレクトリ化 5 + 識別確認の不一致、削除保留中 5 + 識別確認の失敗)
    // → STOP。
    [Theory]
    [InlineData("vanished", "削除用に開けません (Win32 エラー 2)", RunMode.Strict)]
    [InlineData("parent-vanished", "削除用に開けません (Win32 エラー 3)", RunMode.Strict)]
    [InlineData("directory", "識別確認で列挙時の項目と不一致", RunMode.Strict)]
    [InlineData("delete-pending", "識別確認も失敗", RunMode.Strict)]
    [InlineData("vanished", "削除用に開けません (Win32 エラー 2)", RunMode.Fast)]
    [InlineData("parent-vanished", "削除用に開けません (Win32 エラー 3)", RunMode.Fast)]
    [InlineData("directory", "識別確認で列挙時の項目と不一致", RunMode.Fast)]
    [InlineData("delete-pending", "識別確認も失敗", RunMode.Fast)]
    public void S17_IdentityInDoubtAtOpen_Stops(string situation, string reasonPart, RunMode mode)
    {
        var f = CreateApc($"{nameof(S17_IdentityInDoubtAtOpen_Stops)}-{situation}-{mode}");
        var p = f.Path("p");
        var b = f.Path(@"p\b.txt");
        HelperProcess? helper = null;
        Result r;
        try
        {
            r = Delete(f.Zip, f.Target, f.Guard, mode: mode, hooks: new DeleteHooks
            {
                BeforeOpen = (entry, _) =>
                {
                    if (entry.Name != "p/b.txt")
                    {
                        return;
                    }

                    switch (situation)
                    {
                        case "vanished":
                            File.Move(b, Path.Combine(f.Dir, "b.moved"));
                            break;
                        case "parent-vanished":
                            Directory.Move(p, Path.Combine(f.Dir, "p.moved"));
                            break;
                        case "directory":
                            File.Move(b, Path.Combine(f.Dir, "b.moved"));
                            Directory.CreateDirectory(b);
                            break;
                        case "delete-pending":
                            // 別プロセスが削除を指示してハンドルを保持する (ヘルパーもガードで fixture 内であることを確かめる)。
                            helper = HelperProcess.Hold("hold-delete-pending", f.Dir, b);
                            break;
                    }
                },
            });
        }
        finally
        {
            helper?.Dispose();
        }

        var stop = AssertStoppedAt(f, r, "p/b.txt", "削除用に開けません");
        Assert.Contains(reasonPart, stop.Reason, StringComparison.Ordinal);
        Assert.Equal(1, f.Guard.CheckCount);
    }

    // S19 (Win): 事前判定 (D1): 最終成分がディレクトリ / ディレクトリ junction / read-only / system → SKIPPED_SPECIAL_FILE (理由付き)。
    // 削除用ハンドルを開かない。同じ状態の analyze も SKIPPED_SPECIAL_FILE。ファイル symlink は特権が必要なため作らない (未確認)。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void S19_PreCheckSkipsWithoutOpening(RunMode mode)
    {
        var f = Create(
            $"{nameof(S19_PreCheckSkipsWithoutOpening)}-{mode}",
            ("folder.txt", A), ("junction.txt", A), ("readonly.txt", A), ("system.txt", A), ("ok.txt", A));
        File.Move(f.Path("folder.txt"), Path.Combine(f.Dir, "folder.moved"));
        Directory.CreateDirectory(f.Path("folder.txt"));
        File.Move(f.Path("junction.txt"), Path.Combine(f.Dir, "junction.moved"));
        CreateJunction(f.Path("junction.txt"), Directory.CreateDirectory(Path.Combine(f.Dir, "elsewhere")).FullName);
        File.SetAttributes(f.Path("readonly.txt"), FileAttributes.ReadOnly | FileAttributes.Archive);
        File.SetAttributes(f.Path("system.txt"), FileAttributes.System | FileAttributes.Archive);

        var analyzed = Analyze(f.Zip, f.Target, mode: mode);
        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode);

        Log(r);
        Assert.Equal(ExitStatus.Success, r.Status);
        foreach (var (name, reason) in new[]
        {
            ("folder.txt", SkipReason.Directory), ("junction.txt", SkipReason.Directory),
            ("readonly.txt", SkipReason.Attributes), ("system.txt", SkipReason.Attributes),
        })
        {
            var result = r.ResultOf(name);
            Assert.Equal((DeleteStatus.SkippedSpecialFile, reason), (result.Status, result.SkipReason));
            Assert.False(r.Opened(name));
            Assert.Equal(Classification.SkippedSpecialFile, analyzed.Of(name));
        }

        Assert.Equal(["ok.txt"], r.DeletedNames);
        Assert.True(Directory.Exists(f.Path("folder.txt")));
        Assert.True(Directory.Exists(f.Path("junction.txt")));
        Assert.True(f.Exists("readonly.txt"));
        Assert.True(f.Exists("system.txt"));
    }

    // S20 (Win): 事前判定を通過した後、開いたハンドルで判定: ADS、hardlink → SKIPPED_SPECIAL_FILE。ハンドル上の判定を省略しない。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void S20_HandleChecksAreNotSkipped(RunMode mode)
    {
        var f = Create($"{nameof(S20_HandleChecksAreNotSkipped)}-{mode}", ("ads.txt", A), ("hardlink.txt", A));
        File.WriteAllText(f.Path("ads.txt") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        CreateHardLink(Path.Combine(f.Dir, "hardlink-other.txt"), f.Path("hardlink.txt"));

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode);

        Log(r);
        Assert.Equal(SkipReason.AlternateDataStream, r.ResultOf("ads.txt").SkipReason);
        Assert.Equal(SkipReason.HardLink, r.ResultOf("hardlink.txt").SkipReason);
        Assert.True(r.Opened("ads.txt"));
        Assert.True(r.Opened("hardlink.txt"));
        Assert.True(f.Exists("ads.txt"));
        Assert.True(f.Exists("hardlink.txt"));
        Assert.Equal(0, f.Guard.CheckCount);
    }

    // S20 回帰: ZIP が元の名前と追加した名前の両方を含む。USN がどちらの親 ID を返しても、
    // 同じハンドルのリンク数で両方を非削除にする。別ディレクトリの2名を検査し、後続の通常ファイルは削除する。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void S20_HardLinksInDifferentParents_AreBothSkipped(RunMode mode)
    {
        var f = Create($"{nameof(S20_HardLinksInDifferentParents_AreBothSkipped)}-{mode}",
            ("original/hardlink.txt", A), ("ok.txt", A));
        Directory.CreateDirectory(f.Path("linked"));
        CreateHardLink(f.Path(@"linked\hardlink-other.txt"), f.Path(@"original\hardlink.txt"));
        var zip = WriteZip(Path.Combine(f.Dir, "both-links.zip"),
            Zip(("original/hardlink.txt", A), ("linked/hardlink-other.txt", A), ("ok.txt", A)));

        var r = Delete(zip, f.Target, f.Guard, mode: mode);

        Log(r);
        Assert.Equal(ExitStatus.Success, r.Status);
        Assert.Null(r.Report.Stop);
        foreach (var name in new[] { "original/hardlink.txt", "linked/hardlink-other.txt" })
        {
            Assert.Equal((DeleteStatus.SkippedSpecialFile, SkipReason.HardLink),
                (r.ResultOf(name).Status, r.ResultOf(name).SkipReason));
            Assert.True(r.Opened(name.Replace('/', '\\')));
            Assert.Equal(A, File.ReadAllBytes(f.Path(name)));
        }

        Assert.Equal(["ok.txt"], r.DeletedNames);
        Assert.Equal(1, f.Guard.CheckCount);
    }

    // S21: H1 で対象を削除して同名で作り直す (改名で退避) / 別ファイルで置換 (MoveFileEx) / 改名して消す → File ID 不一致で STOP、または 2 で STOP。
    // S22: H1 で親ディレクトリを同名の別ディレクトリに差し替え、同じファイルを移して入れる → オープン直後の親 File ID の照合で STOP。
    // S23: H1 で途中のディレクトリを元を指す junction に差し替え / 大小文字だけ改名 → オープン直後の最終パスの不一致で STOP。
    [Theory]
    [InlineData("recreate", "開いたファイルが、列挙で見つけた項目と一致しません", RunMode.Strict)]
    [InlineData("replace", "開いたファイルが、列挙で見つけた項目と一致しません", RunMode.Strict)]
    [InlineData("rename-away", "削除用に開けません (Win32 エラー 2)", RunMode.Strict)]
    [InlineData("parent-replaced", "開いたファイルの親ディレクトリが、列挙でたどった親ディレクトリと一致しません", RunMode.Strict)]
    [InlineData("parent-junction", "開いたファイルの最終パスが期待したパスと一致しません", RunMode.Strict)]
    [InlineData("parent-case", "開いたファイルの最終パスが期待したパスと一致しません", RunMode.Strict)]
    [InlineData("recreate", "開いたファイルが、列挙で見つけた項目と一致しません", RunMode.Fast)]
    [InlineData("replace", "開いたファイルが、列挙で見つけた項目と一致しません", RunMode.Fast)]
    [InlineData("rename-away", "削除用に開けません (Win32 エラー 2)", RunMode.Fast)]
    [InlineData("parent-replaced", "開いたファイルの親ディレクトリが、列挙でたどった親ディレクトリと一致しません", RunMode.Fast)]
    [InlineData("parent-junction", "開いたファイルの最終パスが期待したパスと一致しません", RunMode.Fast)]
    [InlineData("parent-case", "開いたファイルの最終パスが期待したパスと一致しません", RunMode.Fast)]
    public void S21_S22_S23_ChangeAfterEnumeration_Stops(string change, string reason, RunMode mode)
    {
        var f = CreateApc($"{nameof(S21_S22_S23_ChangeAfterEnumeration_Stops)}-{change}-{mode}");
        var p = f.Path("p");
        var b = f.Path(@"p\b.txt");

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode, hooks: new DeleteHooks
        {
            BeforeOpen = (entry, _) =>
            {
                if (entry.Name != "p/b.txt")
                {
                    return;
                }

                switch (change)
                {
                    case "recreate":
                        File.Move(b, b + ".old");
                        File.WriteAllBytes(b, B);
                        break;
                    case "replace":
                        var other = Path.Combine(p, "other.tmp");
                        File.WriteAllBytes(other, B);
                        GuardedReplace(f.Guard, other, b);
                        break;
                    case "rename-away":
                        File.Move(b, b + ".away");
                        break;
                    case "parent-replaced":
                        Directory.Move(p, p + "-old");
                        Directory.CreateDirectory(p);
                        File.Move(Path.Combine(p + "-old", "b.txt"), b);
                        break;
                    case "parent-junction":
                        Directory.Move(p, p + "-real");
                        CreateJunction(p, p + "-real");
                        break;
                    case "parent-case":
                        Directory.Move(p, f.Path("P"));
                        break;
                }
            },
        });

        var stop = AssertStoppedAt(f, r, "p/b.txt", reason);

        // 置換 (replace) では、上書きの前にテスト側でもガードを確かめる。
        Assert.Equal(change == "replace" ? 2 : 1, f.Guard.CheckCount);
        var remaining = change switch
        {
            "rename-away" => b + ".away",
            "parent-junction" => Path.Combine(p + "-real", "b.txt"),
            "parent-case" => f.Path(@"P\b.txt"),
            _ => b,
        };
        Assert.True(File.Exists(remaining));
        _ = stop;
    }

    // S24: H2 (オープン直後)・H3 (比較中)・H4 (最終確認の直前) に別プロセスが対象を書き込み用・改名用・削除用に開こうとする
    // → 共有違反 (32) で失敗し、対象は検証した個体のまま削除される (Strict は一致時)。Fast は H3 が無い。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void S24_OtherProcessCannotWriteRenameOrDeleteWhileDeletionHandleIsOpen(RunMode mode)
    {
        var f = CreateAbc($"{nameof(S24_OtherProcessCannotWriteRenameOrDeleteWhileDeletionHandleIsOpen)}-{mode}");
        var b = f.Path("b.txt");
        var codes = new List<(string, int)>();
        Action Attempts(string stage) => () =>
        {
            codes.Add(($"{stage} write", HelperProcess.Try("try-write", b)));
            codes.Add(($"{stage} rename", HelperProcess.Try("try-rename", b)));
            codes.Add(($"{stage} delete", HelperProcess.Try("try-open-delete", b)));
        };

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode, hooks: new DeleteHooks
        {
            AfterOpen = When("b.txt", Attempts("H2")),
            DuringCompare = When("b.txt", Attempts("H3")),
            BeforeFinalCheck = When("b.txt", Attempts("H4")),
        });

        Log(r);
        foreach (var (what, code) in codes)
        {
            output.WriteLine($"{what}: {code}");
        }

        string[] stages = mode == RunMode.Fast ? ["H2", "H4"] : ["H2", "H3", "H4"];
        Assert.Equal(stages.SelectMany(s => new[] { ($"{s} write", 32), ($"{s} rename", 32), ($"{s} delete", 32) }), codes);
        Assert.Equal(ExitStatus.Success, r.Status);
        Assert.Equal(["a.txt", "b.txt", "c.txt"], r.DeletedNames);
        Assert.False(File.Exists(b + ".renamed"));
    }

    // S25: H3 (比較中) に ADS 追加 / hardlink 追加 / read-only 付与 / 属性変更 (hidden) → 最終確認で検出して STOP。そのファイルは残る。
    // S27: H4 (最終確認の直前) に同じ変更 → 最終確認で STOP (M0 の全項目の照合)。Fast は H4 だけ。
    [Theory]
    [InlineData("ads", "H3", RunMode.Strict)]
    [InlineData("hardlink", "H3", RunMode.Strict)]
    [InlineData("readonly", "H3", RunMode.Strict)]
    [InlineData("hidden", "H3", RunMode.Strict)]
    [InlineData("ads", "H4", RunMode.Strict)]
    [InlineData("hardlink", "H4", RunMode.Strict)]
    [InlineData("readonly", "H4", RunMode.Strict)]
    [InlineData("hidden", "H4", RunMode.Strict)]
    [InlineData("ads", "H4", RunMode.Fast)]
    [InlineData("hardlink", "H4", RunMode.Fast)]
    [InlineData("readonly", "H4", RunMode.Fast)]
    [InlineData("hidden", "H4", RunMode.Fast)]
    public void S25_S27_ChangeWhileHandleIsOpen_StopsAtFinalCheck(string change, string hook, RunMode mode)
    {
        var f = CreateAbc($"{nameof(S25_S27_ChangeWhileHandleIsOpen_StopsAtFinalCheck)}-{change}-{hook}-{mode}");
        var b = f.Path("b.txt");
        var inject = When("b.txt", () =>
        {
            switch (change)
            {
                case "ads":
                    // 削除用ハンドルが DELETE アクセスを持つため、新しいストリームのオープンも FILE_SHARE_DELETE を含めないと 32 になる
                    // (PoC 6 の別プロセスも共有 R|W|D で作成した)。
                    using (var ads = new FileStream(b + ":extra", FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        ads.WriteByte((byte)'x');
                    }

                    break;
                case "hardlink": CreateHardLink(Path.Combine(f.Dir, "b-link.txt"), b); break;
                case "readonly": File.SetAttributes(b, FileAttributes.ReadOnly | FileAttributes.Archive); break;
                case "hidden": File.SetAttributes(b, FileAttributes.Hidden | FileAttributes.Archive); break;
            }
        });

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode, hooks: hook == "H3" ? new DeleteHooks { DuringCompare = inject } : new DeleteHooks { BeforeFinalCheck = inject });

        Log(r);
        Assert.Equal(ExitStatus.Error, r.Status);
        var stop = r.Report.Stop!;
        Assert.Equal("b.txt", stop.Entry.Name);
        Assert.StartsWith("最終確認で不一致: ", stop.Reason, StringComparison.Ordinal);
        Assert.Equal(["a.txt"], r.DeletedNames);
        Assert.True(File.Exists(b));
        Assert.True(f.Exists("c.txt"));
        Assert.Equal(1, f.Guard.CheckCount);
    }

    // S26 (実測): H3 (比較中) に祖先ディレクトリ (対象の親) の改名を試みる。改名が成功したか失敗したかと、そのときの結果を記録する。
    // 改名が成功した場合は最終確認の最終パス不一致で STOP し削除しないこと、失敗した場合はそのファイルの処理が通常どおり進むことを判定する。
    [Fact]
    public void S26_Measure_AncestorRenameWhileDeletionHandleIsOpen()
    {
        var f = CreateApc(nameof(S26_Measure_AncestorRenameWhileDeletionHandleIsOpen));
        var p = f.Path("p");
        string? attempt = null;

        var r = Delete(f.Zip, f.Target, f.Guard, hooks: new DeleteHooks
        {
            DuringCompare = When("p/b.txt", () =>
            {
                try
                {
                    Directory.Move(p, p + "-renamed");
                    attempt = "成功";
                }
                catch (IOException ex)
                {
                    attempt = $"失敗 (Win32 エラー {Win32Code(ex)})";
                }
                catch (UnauthorizedAccessException ex)
                {
                    attempt = $"失敗 ({ex.GetType().Name}、Win32 エラー 5 相当)";
                }
            }),
        });

        Log(r);
        var result = r.ResultOf("p/b.txt");
        output.WriteLine($"S26 結果: 祖先ディレクトリの改名 = {attempt}、p/b.txt = {result.Status} ({result.Reason})、終了状態 = {r.Status}");
        if (attempt == "成功")
        {
            Assert.Equal(DeleteStatus.Stopped, result.Status);
            Assert.Equal("最終確認で不一致: 最終パス", result.Reason);
            Assert.True(File.Exists(Path.Combine(p + "-renamed", "b.txt")));
        }
        else
        {
            Assert.NotNull(attempt);
            Assert.Equal(DeleteStatus.Deleted, result.Status);
            Assert.Equal(["a.txt", "p/b.txt", "c.txt"], r.DeletedNames);
        }
    }

    // S28: H5 (削除指示の直前) で read-only 付与 → 指示が 5 で失敗し、対象は残り、以後を STOP。前は削除済み、後は未処理。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void S28_ReadOnlyBeforeDisposition_StopsAndKeepsFile(RunMode mode)
    {
        var f = CreateAbc($"{nameof(S28_ReadOnlyBeforeDisposition_StopsAndKeepsFile)}-{mode}");
        var b = f.Path("b.txt");

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode, hooks: new DeleteHooks
        {
            BeforeDisposition = When("b.txt", () => File.SetAttributes(b, FileAttributes.ReadOnly | FileAttributes.Archive)),
        });

        var stop = AssertStoppedAt(f, r, "b.txt", "削除の指示が失敗");
        Assert.Contains("Win32 エラー 5", stop.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(b));
        Assert.Equal(B, File.ReadAllBytes(b));
        Assert.Equal(2, f.Guard.CheckCount);
    }

    // S29: 逐次処理中に対象以外だけを変える (ZIP にないファイルの追加・変更、他エントリの読み取り、target 内の別ディレクトリの作成) → STOP しない。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void S29_UnrelatedChanges_DoNotStop(RunMode mode)
    {
        var f = CreateAbc($"{nameof(S29_UnrelatedChanges_DoNotStop)}-{mode}");
        File.WriteAllBytes(f.Path("unrelated.txt"), Bytes("u"));

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode, hooks: new DeleteHooks
        {
            BeforeOpen = BeforeOpenOf("b.txt", () =>
            {
                File.WriteAllBytes(f.Path("new.txt"), Bytes("n"));
                File.AppendAllText(f.Path("unrelated.txt"), "more");
                Assert.Equal(C, File.ReadAllBytes(f.Path("c.txt")));
                Directory.CreateDirectory(f.Path("newdir"));
            }),
        });

        Log(r);
        Assert.Equal(ExitStatus.Success, r.Status);
        Assert.Equal(["a.txt", "b.txt", "c.txt"], r.DeletedNames);
        Assert.True(f.Exists("new.txt"));
        Assert.True(f.Exists("unrelated.txt"));
        Assert.True(Directory.Exists(f.Path("newdir")));
    }

    // S34 (実測): ACL で対象の DELETE だけを拒否する (親の DELETE_CHILD は許可のまま)。拒否する時点を (a) delete の実行前、(b) H3 (比較中)。
    // 結果 (DELETED / STOP、終了状態) を記録する。判定は誤削除がないこと (対象以外が残り、STOP なら対象も残ること)。
    // PLAN.md §4 の推定: (a) 削除される、(b) ChangeTime の変化で最終確認により STOP。
    [Theory]
    [InlineData("before-run", RunMode.Strict)]
    [InlineData("during-compare", RunMode.Strict)]
    [InlineData("before-run", RunMode.Fast)]
    public void S34_Measure_DeleteDeniedOnTargetOnly(string when, RunMode mode)
    {
        var f = CreateAbc($"{nameof(S34_Measure_DeleteDeniedOnTargetOnly)}-{when}-{mode}");
        var b = f.Path("b.txt");
        File.WriteAllBytes(f.Path("unrelated.txt"), Bytes("u"));
        Result r;
        using (var acl = new AclChanges(output))
        {
            if (when == "before-run")
            {
                acl.Deny(b, "DE");
            }

            r = Delete(f.Zip, f.Target, f.Guard, mode: mode, hooks: when == "during-compare"
                ? new DeleteHooks { DuringCompare = When("b.txt", () => acl.Deny(b, "DE")) }
                : null);

            output.WriteLine($"S34 icacls (実行後): {(File.Exists(b) ? Cmd($"icacls \"{b}\"").Output.Trim() : "(対象なし)")}");
        }

        Log(r);
        var result = r.ResultOf("b.txt");
        output.WriteLine(
            $"S34 ({when}, {mode}) 結果: b.txt = {result.Status} ({result.Reason})、b.txt の存在 = {File.Exists(b)}、"
            + $"削除済み = [{string.Join(", ", r.DeletedNames)}]、未処理 = {r.Report.NotProcessedCount}、終了状態 = {r.Status}");

        Assert.False(f.Exists("a.txt"));
        Assert.True(f.Exists("unrelated.txt"));
        Assert.True(File.Exists(f.Zip));
        Assert.NotEqual(DeleteStatus.DeleteFailed, result.Status);
        if (result.Status == DeleteStatus.Deleted)
        {
            Assert.False(File.Exists(b));
            Assert.Equal(["a.txt", "b.txt", "c.txt"], r.DeletedNames);
            Assert.Equal(ExitStatus.Success, r.Status);
        }
        else
        {
            Assert.Equal(DeleteStatus.Stopped, result.Status);
            Assert.True(File.Exists(b));
            Assert.True(f.Exists("c.txt"));
            Assert.Equal(ExitStatus.Error, r.Status);
        }
    }

    // S35 (Win): 列挙のキャッシュと逐次削除: 同じディレクトリのエントリを前から削除しながら処理する。H1 で、後続のエントリと同名の新しいファイル
    // (列挙後に作成) と ZIP にないファイルを追加する → 列挙後に作られた同名ファイルは MISSING (削除しない)。自分の削除で後続の判定が変わらない。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void S35_EnumerationIsReusedDuringSequentialDeletion(RunMode mode)
    {
        var f = Create($"{nameof(S35_EnumerationIsReusedDuringSequentialDeletion)}-{mode}", ("d/a.txt", A), ("d/b.txt", B), ("d/late.txt", C));
        File.Move(f.Path(@"d\late.txt"), Path.Combine(f.Dir, "late.moved"));

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode, hooks: new DeleteHooks
        {
            BeforeOpen = BeforeOpenOf("d/b.txt", () =>
            {
                File.WriteAllBytes(f.Path(@"d\late.txt"), C);
                File.WriteAllBytes(f.Path(@"d\unrelated-new.txt"), Bytes("n"));
            }),
        });

        Log(r);
        Assert.Equal(ExitStatus.Success, r.Status);
        Assert.Equal(["d/a.txt", "d/b.txt"], r.DeletedNames);
        Assert.Equal(DeleteStatus.Missing, r.ResultOf("d/late.txt").Status);
        Assert.True(f.Exists(@"d\late.txt"));
        Assert.True(f.Exists(@"d\unrelated-new.txt"));
    }

    // S37 (実測): 列挙の後 (H1) に read-only を付けたファイルを削除用ハンドルで開く。オープンが成功したか (5 で失敗したか) を記録する。
    // 成功した場合はハンドル上の属性判定で SKIPPED_SPECIAL_FILE、失敗した場合は識別確認により DELETE_FAILED になること、
    // どちらでも削除しないことを判定する。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void S37_Measure_ReadOnlyAfterEnumeration(RunMode mode)
    {
        var f = CreateAbc($"{nameof(S37_Measure_ReadOnlyAfterEnumeration)}-{mode}");
        var b = f.Path("b.txt");

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode, hooks: new DeleteHooks
        {
            BeforeOpen = BeforeOpenOf("b.txt", () => File.SetAttributes(b, FileAttributes.ReadOnly | FileAttributes.Archive)),
        });

        Log(r);
        var result = r.ResultOf("b.txt");
        output.WriteLine($"S37 ({mode}) 結果: 削除用オープン = {(result.Status == DeleteStatus.SkippedSpecialFile ? "成功" : "失敗")}、b.txt = {result.Status} {result.SkipReason} ({result.Reason})");
        Assert.True(File.Exists(b));
        Assert.Equal(["a.txt", "c.txt"], r.DeletedNames);
        if (result.Status == DeleteStatus.SkippedSpecialFile)
        {
            Assert.Equal(SkipReason.Attributes, result.SkipReason);
            Assert.Equal(ExitStatus.Success, r.Status);
        }
        else
        {
            Assert.Equal(DeleteStatus.DeleteFailed, result.Status);
            Assert.Contains("Win32 エラー 5", result.Reason, StringComparison.Ordinal);
        }
    }

    // S38 (実測): 許可集合の各属性・許可外の各属性・reparse を付けた項目で、列挙 (FileIdExtdDirectoryInfo) の属性・reparse tag と
    // ハンドル (FileBasicInfo・FileAttributeTagInfo) の値を比較し、一致・不一致を属性ごとに記録する。安全性はこの一致に依存しない (S20)。
    [Fact]
    public void S38_Measure_EnumerationAndHandleAttributes()
    {
        var dir = CreateDirectory(nameof(S38_Measure_EnumerationAndHandleAttributes));
        var items = Directory.CreateDirectory(Path.Combine(dir, "items")).FullName;
        string Make(string name, FileAttributes? attributes = null)
        {
            var path = Path.Combine(items, name);
            File.WriteAllBytes(path, A);
            if (attributes is { } value)
            {
                File.SetAttributes(path, value);
            }

            return name;
        }

        var names = new List<string>
        {
            Make("archive.txt", FileAttributes.Archive),
            Make("normal.txt", FileAttributes.Normal),
            Make("hidden.txt", FileAttributes.Hidden | FileAttributes.Archive),
            Make("notindexed.txt", FileAttributes.NotContentIndexed | FileAttributes.Archive),
            Make("readonly.txt", FileAttributes.ReadOnly | FileAttributes.Archive),
            Make("system.txt", FileAttributes.System | FileAttributes.Archive),
            Make("temporary.txt", FileAttributes.Temporary | FileAttributes.Archive),
            Make("offline.txt", FileAttributes.Offline | FileAttributes.Archive),
        };
        var compressed = Make("compressed.txt");
        if (Cmd($"compact /c \"{Path.Combine(items, compressed)}\"").ExitCode == 0)
        {
            names.Add(compressed);
        }

        var sparse = Make("sparse.txt");
        if (Cmd($"fsutil sparse setflag \"{Path.Combine(items, sparse)}\"").ExitCode == 0)
        {
            names.Add(sparse);
        }

        Directory.CreateDirectory(Path.Combine(items, "dir"));
        names.Add("dir");
        CreateJunction(Path.Combine(items, "junction"), Directory.CreateDirectory(Path.Combine(dir, "elsewhere")).FullName);
        names.Add("junction");

        var probe = new WindowsFileSystemProbe();
        var opened = probe.OpenDirectoryForEnumeration(items);
        Assert.True(opened.Succeeded, opened.Describe());
        var enumerated = new Dictionary<string, DirectoryItem>(StringComparer.Ordinal);
        using (var handle = opened.Value)
        {
            var enumeration = handle.Enumerate();
            for (var step = enumeration.Next(); step.Kind == Unextract.Core.Target.DirectoryEnumerationStepKind.Item; step = enumeration.Next())
            {
                enumerated[step.Item.Name] = step.Item;
            }
        }

        var mismatches = 0;
        foreach (var name in names)
        {
            var item = enumerated[name];
            var file = probe.OpenForComparison(Path.Combine(items, name));
            Assert.True(file.Succeeded, file.Describe());
            using var h = file.Value;
            var basic = h.GetBasicInformation();
            var tag = h.GetAttributeTagInformation();
            Assert.True(basic.Succeeded && tag.Succeeded);
            var same = item.Attributes == basic.Value.Attributes && item.Attributes == tag.Value.Attributes && item.ReparseTag == tag.Value.ReparseTag;
            mismatches += same ? 0 : 1;
            output.WriteLine(
                $"S38 {name}: 列挙 属性 0x{item.Attributes:X} tag 0x{item.ReparseTag:X} / ハンドル FileBasicInfo 0x{basic.Value.Attributes:X} "
                + $"FileAttributeTagInfo 0x{tag.Value.Attributes:X} tag 0x{tag.Value.ReparseTag:X} → {(same ? "一致" : "不一致")}");
        }

        output.WriteLine($"S38 結果: {names.Count} 項目中 不一致 {mismatches}");
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    // A07 (Win): 競合のない同じ fixture で analyze → delete。analyze の MATCHED (Fast は SAME_SIZE) と delete の DELETED が同じエントリの集合。
    // MODIFIED・MISSING・SKIPPED も同じエントリ (保証ではなく、競合なし・権限差なしの条件での確認)。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void A07_AnalyzeThenDelete_Agree(RunMode mode)
    {
        var f = Create($"{nameof(A07_AnalyzeThenDelete_Agree)}-{mode}", ("same.txt", A), ("changed.txt", B), ("size.txt", C), ("missing.txt", A), ("d/", null), ("d/ads.txt", A));
        File.WriteAllBytes(f.Path("changed.txt"), Bytes("bravo CONTENT"));
        File.WriteAllBytes(f.Path("size.txt"), Bytes("x"));
        File.Move(f.Path("missing.txt"), Path.Combine(f.Dir, "missing.moved"));
        File.WriteAllText(f.Path(@"d\ads.txt") + ":Zone.Identifier", "[ZoneTransfer]");

        var analyzed = Analyze(f.Zip, f.Target, mode: mode);
        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode);

        Log(r);
        IEnumerable<string> Names(Classification c) => analyzed.Analysis.Results.Where(x => x.Classification == c).Select(x => x.Entry.Name);
        IEnumerable<string> Deleted(DeleteStatus s) => r.Report.Results.Where(x => x.Status == s).Select(x => x.Entry.Name);
        Assert.Equal(Names(Candidate(mode)), Deleted(DeleteStatus.Deleted));
        Assert.Equal(Names(Classification.Modified), Deleted(DeleteStatus.Modified));
        Assert.Equal(Names(Classification.Missing), Deleted(DeleteStatus.Missing));
        Assert.Equal(Names(Classification.SkippedSpecialFile), Deleted(DeleteStatus.SkippedSpecialFile));
    }

    // A08: analyze の後、delete の前に、MATCHED のファイルを (a) 内容だけ書き換える (File ID・サイズ・日時・属性を書き戻す)、(b) 削除して同名で
    // 作り直す (同じ内容 / 違う内容)、(c) ADS を追加、(d) read-only を付ける。
    // Strict: (a) MODIFIED、(b) 同じ内容なら DELETED・違う内容なら MODIFIED、(c) SKIPPED (ADS)、(d) SKIPPED (属性、事前判定で開かない)。
    // Fast: (a) DELETED (SPEC §15.5)、(b) サイズで判定 (同じサイズの違う内容も DELETED)、(c)(d) Strict と同じ。analyze の結果を許可証として使わない。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void A08_DeleteVerifiesCurrentStateNotAnalyzeResult(RunMode mode)
    {
        var f = Create(
            $"{nameof(A08_DeleteVerifiesCurrentStateNotAnalyzeResult)}-{mode}",
            ("a.txt", A), ("same-recreated.txt", B), ("other-recreated.txt", C), ("ads.txt", A), ("readonly.txt", B));
        var analyzed = Analyze(f.Zip, f.Target, mode: mode);
        Assert.All(analyzed.Analysis.Results, x => Assert.Equal(Candidate(mode), x.Classification));

        RewriteKeepingMetadata(f.Path("a.txt"), Bytes("ALPHA CONTENT"));
        File.Move(f.Path("same-recreated.txt"), Path.Combine(f.Dir, "same.old"));
        File.WriteAllBytes(f.Path("same-recreated.txt"), B);
        File.Move(f.Path("other-recreated.txt"), Path.Combine(f.Dir, "other.old"));
        File.WriteAllBytes(f.Path("other-recreated.txt"), Bytes("CHARLIE content"));
        File.WriteAllText(f.Path("ads.txt") + ":extra", "x");
        File.SetAttributes(f.Path("readonly.txt"), FileAttributes.ReadOnly | FileAttributes.Archive);

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode);

        Log(r);
        var fast = mode == RunMode.Fast;
        Assert.Equal(fast ? DeleteStatus.Deleted : DeleteStatus.Modified, r.ResultOf("a.txt").Status);
        Assert.Equal(DeleteStatus.Deleted, r.ResultOf("same-recreated.txt").Status);
        Assert.Equal(fast ? DeleteStatus.Deleted : DeleteStatus.Modified, r.ResultOf("other-recreated.txt").Status);
        Assert.Equal((DeleteStatus.SkippedSpecialFile, SkipReason.AlternateDataStream), (r.ResultOf("ads.txt").Status, r.ResultOf("ads.txt").SkipReason));
        Assert.Equal((DeleteStatus.SkippedSpecialFile, SkipReason.Attributes), (r.ResultOf("readonly.txt").Status, r.ResultOf("readonly.txt").SkipReason));
        Assert.False(r.Opened("readonly.txt"));
        Assert.Equal(!fast, f.Exists("a.txt"));
        Assert.True(f.Exists("ads.txt"));
        Assert.True(f.Exists("readonly.txt"));
    }

    // L16: entries ファイルが target 内にあり、ZIP にも同名・同内容のエントリがある → entries は確認の前に読み終えて閉じている。
    // そのファイルが削除されても処理は正常に続く。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void L16_EntriesFileInsideTarget(RunMode mode)
    {
        var entries = Bytes("entries.txt\na.txt\n");
        var f = Create($"{nameof(L16_EntriesFileInsideTarget)}-{mode}", ("entries.txt", entries), ("a.txt", A), ("b.txt", B));

        var r = Delete(f.Zip, f.Target, f.Guard, mode: mode, entriesPath: f.Path("entries.txt"));

        Log(r);
        Assert.Equal(ExitStatus.Success, r.Status);
        Assert.Equal(["entries.txt", "a.txt"], r.DeletedNames);
        Assert.Contains("対象: 3 エントリ中 2 エントリ (--entries)", r.Output, StringComparison.Ordinal);
        Assert.False(f.Exists("entries.txt"));
        Assert.True(f.Exists("b.txt"));
    }

    // 上書きの改名 (MoveFileEx の置換) の前に、上書きされる側がガードの範囲内であることを、そのハンドルの最終パスで確かめる。
    private static void GuardedReplace(DeletionGuard guard, string source, string destination)
    {
        var opened = HandleOpener.OpenForConfirmation(destination);
        Assert.True(opened.Succeeded, opened.ToString());
        using (var handle = opened.Value)
        {
            var finalPath = FileInformation.GetFinalPath(handle);
            Assert.True(finalPath.Succeeded);
            guard.Check(finalPath.Value);
        }

        File.Move(source, destination, overwrite: true);
    }

    // 同じハンドルで内容を書き換えた後、FILE_BASIC_INFO (作成・アクセス・更新・変更日時と属性) を元の値で書き戻す (テスト A08 (a))。
    // 書き戻した後の値が元と一致することも確かめる (前提の確認)。
    private static unsafe void RewriteKeepingMetadata(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var handle = stream.SafeFileHandle;
        var before = ReadBasic(handle);
        Assert.Equal(stream.Length, content.LongLength);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
        var info = before;
        Assert.True(TestKernel32.SetFileInformationByHandle(handle, (int)FileInfoByHandleClass.FileBasicInfo, &info, (uint)sizeof(FileBasicInfoNative)));
        var after = ReadBasic(handle);
        Assert.Equal((before.LastWriteTime, before.ChangeTime, before.FileAttributes), (after.LastWriteTime, after.ChangeTime, after.FileAttributes));
    }

    private static unsafe FileBasicInfoNative ReadBasic(SafeFileHandle handle)
    {
        FileBasicInfoNative info;
        Assert.True(Kernel32.GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileBasicInfo, &info, (uint)sizeof(FileBasicInfoNative)));
        return info;
    }
}
