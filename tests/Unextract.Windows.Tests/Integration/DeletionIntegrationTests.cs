using Microsoft.Win32.SafeHandles;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Unextract.Windows.Tests.Helper;
using Xunit.Abstractions;
using static Unextract.Windows.Tests.Integration.RealRun;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests.Integration;

// 削除フェーズの実 NTFS テスト (PLAN_TESTS.md の D 系)。実際に削除する。
// - 削除してよいのは、各テストが自分で作った一意な fixture (bin/.../fixtures/<一意名>/) の中のファイルだけ。fixture を最初に確定し、
//   削除の指示の直前に毎回 DeletionGuard が削除用ハンドルの最終パスと親を確かめる (RealRun.RunDeleting)。
// - junction は同じ fixture 内の別ディレクトリを指すものだけを作る。symlink は作らない。
// - fixture のディレクトリ自体は削除しない。ACL を変えるテストは AclChanges で必ず戻す。
// - テスト自身はパスベースの削除をしない (差し替えは改名で行う)。上書きの改名 (D04 の置換) は、上書きされる側をガードで確かめてから行う。
// 「別プロセス」は HelperProcess (このテストアセンブリのビルド済み実行ファイル) で行う。属性・ADS・hardlink の変更は同一プロセスで行う
// (削除用ハンドルを開いている間でも別プロセスから同じ変更ができ、同じく検出されることは SPEC §13 の PoC 6 で確認済み)。
public class DeletionIntegrationTests(ITestOutputHelper output)
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

    // a.txt (前に削除済み)、b.txt (対象)、c.txt (後の未処理) の3件。
    private static Fixture CreateAbc(string name) => Create(name, ("a.txt", A), ("b.txt", B), ("c.txt", C));

    private void Log(Result result)
    {
        output.WriteLine("--- stdout ---");
        output.WriteLine(result.Output);
        output.WriteLine("--- stderr ---");
        output.WriteLine(result.Error);
    }

    // 対象 b.txt で停止し、a.txt は削除済み、b.txt と c.txt は残る (c.txt は未処理)。
    private void AssertStoppedAtB(Fixture f, Result r, string reasonStart)
    {
        Log(r);
        Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        Assert.Equal(["a.txt"], r.DeletedNames);
        Assert.Empty(r.Deletion.Failed);
        var stop = r.Deletion.Stop!;
        Assert.Equal("b.txt", stop.Entry.Name);
        Assert.StartsWith(reasonStart, stop.Reason, StringComparison.Ordinal);
        Assert.False(stop.PossiblyDeleted);
        Assert.Equal(1, r.Deletion.NotProcessedCount);
        Assert.False(f.Exists("a.txt"));
        Assert.True(f.Exists("c.txt"));
        Assert.True(File.Exists(f.Zip));
        Assert.Contains("削除済み 1、DELETE_FAILED 0、未処理 1", r.Output, StringComparison.Ordinal);
        Assert.Contains("停止: b.txt: ", r.Error, StringComparison.Ordinal);
    }

    // D02: 検証済みの MATCHED だけを個別に削除。ZIP、ZIP にない target のファイル、MODIFIED・特殊なファイル、ディレクトリは残る
    [Fact]
    public void D02_DeletesOnlyVerifiedMatched()
    {
        var f = Create(
            nameof(D02_DeletesOnlyVerifiedMatched),
            ("same.txt", A), ("changed.txt", B), ("d/", null), ("d/deep.txt", C), ("d/ads.txt", A), ("empty/", null));
        File.WriteAllBytes(f.Path("changed.txt"), Bytes("bravo CONTENT"));
        File.WriteAllText(f.Path(@"d\ads.txt") + ":Zone.Identifier", "[ZoneTransfer]");
        File.WriteAllBytes(f.Path("unrelated.txt"), Bytes("u"));
        File.WriteAllBytes(f.Path(@"d\unrelated.txt"), Bytes("u"));
        var zipHash = Hash(f.Zip);
        var before = Snapshot(f.Target);

        var r = RunDeleting(f.Zip, f.Target, f.Guard);

        Log(r);
        Assert.Equal(ExitStatus.Success, r.Outcome.Status);
        Assert.Equal(["same.txt", "d/deep.txt"], r.DeletedNames);
        Assert.Equal(2, f.Guard.CheckCount);
        var after = Snapshot(f.Target);
        before.Remove("same.txt");
        before.Remove(@"d\deep.txt");
        before.Remove("d");
        after.Remove("d");
        Assert.Equal(before, after);
        Assert.True(Directory.Exists(f.Path("d")));
        Assert.True(Directory.Exists(f.Path("empty")));
        Assert.Equal(zipHash, Hash(f.Zip));
    }

    // D04: 確認待ち中の変更 (内容とサイズ / 内容のみ / 同名で作り直し / 別ファイルで置換) → 削除直前再検証で検出して停止
    // 内容のみの変更では、NTFS の日時は粗いシステム時刻で記録されるため、初回比較と同じ刻みのうちに書き換わると
    // LastWriteTime・ChangeTime が変わらないことがある (実行環境の速度による)。その場合は同一性の再検証を通り、
    // 2回目の全バイト比較で検出される (SPEC §3 の 6、§12。D10 と同じ経路)。どちらになるかは変更の前後に観測した値で決める。
    [Theory]
    [InlineData("content-and-size", "同一性の再検証で不一致: EndOfFile")]
    [InlineData("content-only", null)]
    [InlineData("recreate", "同一性の再検証で不一致: File ID")]
    [InlineData("replace", "同一性の再検証で不一致: File ID")]
    public void D04_ChangeWhileAwaitingConfirmation_Stops(string change, string? expectedReason)
    {
        var f = CreateAbc($"{nameof(D04_ChangeWhileAwaitingConfirmation_Stops)}-{change}");
        var b = f.Path("b.txt");

        var r = RunDeleting(f.Zip, f.Target, f.Guard, awaiting: () =>
        {
            switch (change)
            {
                case "content-and-size":
                    File.WriteAllBytes(b, Bytes("bravo content, longer"));
                    break;
                case "content-only":
                    var before = ReadBasic(b);
                    File.WriteAllBytes(b, Bytes("BRAVO content"));
                    expectedReason = ExpectedReasonForContentOnly(before, ReadBasic(b));
                    break;
                case "recreate":
                    // 削除の代わりに改名で退避し、同名で作り直す (File ID が変わる)。
                    File.Move(b, b + ".old");
                    File.WriteAllBytes(b, B);
                    break;
                case "replace":
                    var other = f.Path("other.tmp");
                    File.WriteAllBytes(other, B);
                    GuardedReplace(f.Guard, other, b);
                    break;
            }
        });

        output.WriteLine($"期待する停止理由: {expectedReason}");
        Assert.NotNull(expectedReason);
        AssertStoppedAtB(f, r, expectedReason);
        Assert.True(f.Exists("b.txt"));
    }

    // 内容のみの変更 (サイズ・File ID は同じ) で、同一性の再検証が最初に検出する項目 (DeletionPhase の確認順)。
    // 日時・属性がどれも変わっていなければ、同一性の再検証は通り、2回目の全バイト比較で検出される。
    private static string ExpectedReasonForContentOnly(FileBasicInfoNative before, FileBasicInfoNative after)
    {
        if (after.LastWriteTime != before.LastWriteTime)
        {
            return "同一性の再検証で不一致: LastWriteTime";
        }

        if (after.ChangeTime != before.ChangeTime)
        {
            return "同一性の再検証で不一致: ChangeTime";
        }

        if (after.FileAttributes != before.FileAttributes)
        {
            return "同一性の再検証で不一致: 属性";
        }

        return "2回目の全バイト比較で内容が一致しません";
    }

    // D05: 確認待ち中に ADS 追加 / hardlink 追加 / read-only・system 付与 / 対象を junction に置換 /
    // 途中のディレクトリを junction または同名の別ディレクトリに置換 → 停止
    [Theory]
    [InlineData("ads", "同一性の再検証で不一致")]
    [InlineData("hardlink", "同一性の再検証で不一致")]
    [InlineData("readonly", "同一性の再検証で不一致")]
    [InlineData("system", "同一性の再検証で不一致")]
    [InlineData("target-junction", "削除用に開けません")]
    [InlineData("parent-junction", "同一性の再検証で不一致: 最終パス")]
    [InlineData("parent-replaced", "同一性の再検証で不一致: File ID")]
    public void D05_SpecialChangeWhileAwaitingConfirmation_Stops(string change, string reasonStart)
    {
        var f = Create($"{nameof(D05_SpecialChangeWhileAwaitingConfirmation_Stops)}-{change}", ("a.txt", A), ("p/", null), ("p/b.txt", B), ("c.txt", C));
        var p = f.Path("p");
        var b = f.Path(@"p\b.txt");

        var r = RunDeleting(f.Zip, f.Target, f.Guard, awaiting: () =>
        {
            switch (change)
            {
                case "ads":
                    File.WriteAllText(b + ":extra", "x");
                    break;
                case "hardlink":
                    CreateHardLink(System.IO.Path.Combine(f.Dir, "b-link.txt"), b);
                    break;
                case "readonly":
                    File.SetAttributes(b, FileAttributes.ReadOnly | FileAttributes.Archive);
                    break;
                case "system":
                    File.SetAttributes(b, FileAttributes.System | FileAttributes.Archive);
                    break;
                case "target-junction":
                    // ファイル symlink は特権なしで作れないため、同じ fixture 内のディレクトリを指す junction に置き換える。
                    File.Move(b, System.IO.Path.Combine(f.Dir, "b.moved"));
                    CreateJunction(b, Directory.CreateDirectory(System.IO.Path.Combine(f.Dir, "elsewhere")).FullName);
                    break;
                case "parent-junction":
                    Directory.Move(p, p + "-real");
                    CreateJunction(p, p + "-real");
                    break;
                case "parent-replaced":
                    Directory.Move(p, p + "-old");
                    Directory.CreateDirectory(p);
                    File.WriteAllBytes(b, B);
                    break;
            }
        });

        Log(r);
        Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        Assert.Equal(["a.txt"], r.DeletedNames);
        Assert.Empty(r.Deletion.Failed);
        Assert.Equal("p/b.txt", r.Deletion.Stop!.Entry.Name);
        Assert.StartsWith(reasonStart, r.Deletion.Stop.Reason, StringComparison.Ordinal);
        Assert.Equal(1, r.Deletion.NotProcessedCount);
        Assert.True(f.Exists("c.txt"));
        Assert.Equal(1, f.Guard.CheckCount);
        switch (change)
        {
            case "target-junction":
                Assert.True(File.Exists(System.IO.Path.Combine(f.Dir, "b.moved")));
                break;
            case "parent-junction":
                Assert.True(File.Exists(System.IO.Path.Combine(p + "-real", "b.txt")));
                break;
            case "parent-replaced":
                Assert.True(File.Exists(System.IO.Path.Combine(p + "-old", "b.txt")));
                Assert.True(File.Exists(b));
                break;
            default:
                Assert.True(File.Exists(b));
                break;
        }
    }

    // D07: 確認待ち中に対象以外だけを変える (ZIP にないファイルの追加・変更、他の MATCHED の読み取り、別ディレクトリの作成) → 停止せず削除
    [Fact]
    public void D07_UnrelatedChanges_DoNotStop()
    {
        var f = CreateAbc(nameof(D07_UnrelatedChanges_DoNotStop));
        File.WriteAllBytes(f.Path("unrelated.txt"), Bytes("u"));

        var r = RunDeleting(f.Zip, f.Target, f.Guard, awaiting: () =>
        {
            File.WriteAllBytes(f.Path("new.txt"), Bytes("n"));
            File.AppendAllText(f.Path("unrelated.txt"), "more");
            Assert.Equal(A, File.ReadAllBytes(f.Path("a.txt")));
            Directory.CreateDirectory(f.Path("newdir"));
        });

        Log(r);
        Assert.Equal(ExitStatus.Success, r.Outcome.Status);
        Assert.Equal(["a.txt", "b.txt", "c.txt"], r.DeletedNames);
        Assert.True(f.Exists("new.txt"));
        Assert.True(f.Exists("unrelated.txt"));
        Assert.True(Directory.Exists(f.Path("newdir")));
    }

    // D09: 再検証直後に別プロセスが対象を書き込み用・改名用に開こうとする → 共有違反 (32)。対象は検証した個体のまま削除される
    // D23: 再比較中に別プロセスが対象を書き込みで開こうとする → 共有違反 (32)
    [Fact]
    public void D09_D23_OtherProcessCannotWriteOrRenameWhileDeletionHandleIsOpen()
    {
        var f = CreateAbc(nameof(D09_D23_OtherProcessCannotWriteOrRenameWhileDeletionHandleIsOpen));
        var b = f.Path("b.txt");
        var codes = new List<(string, int)>();
        var hooks = new DeletionHooks
        {
            AfterRevalidation = (file, _) =>
            {
                if (file.Entry.Name == "b.txt")
                {
                    codes.Add(("D09 write", HelperProcess.Try("try-write", b)));
                    codes.Add(("D09 rename", HelperProcess.Try("try-rename", b)));
                }
            },
            DuringRecompare = (file, _) =>
            {
                if (file.Entry.Name == "b.txt")
                {
                    codes.Add(("D23 write", HelperProcess.Try("try-write", b)));
                }
            },
        };

        var r = RunDeleting(f.Zip, f.Target, f.Guard, hooks: hooks);

        Log(r);
        foreach (var (what, code) in codes)
        {
            output.WriteLine($"{what}: {code}");
        }

        Assert.Equal([("D09 write", 32), ("D09 rename", 32), ("D23 write", 32)], codes);
        Assert.Equal(ExitStatus.Success, r.Outcome.Status);
        Assert.Equal(["a.txt", "b.txt", "c.txt"], r.DeletedNames);
        Assert.False(File.Exists(b + ".renamed"));
    }

    // D10: 確認待ち中に、File ID・サイズ・LastWriteTime・ChangeTime・属性を保ったまま内容だけを書き換える
    // → 同一性の再検証は通り、2回目の全バイト比較で検出して停止
    [Fact]
    public void D10_ContentOnlyRewriteWithTimestampsRestored_DetectedByRecompare()
    {
        var f = CreateAbc(nameof(D10_ContentOnlyRewriteWithTimestampsRestored_DetectedByRecompare));
        var b = f.Path("b.txt");

        var r = RunDeleting(f.Zip, f.Target, f.Guard, awaiting: () => RewriteKeepingMetadata(b, Bytes("BRAVO CONTENT")));

        AssertStoppedAtB(f, r, "2回目の全バイト比較で内容が一致しません");
        Assert.Equal(Bytes("BRAVO CONTENT"), File.ReadAllBytes(b));
    }

    // D11: 確認待ち中に、別プロセスが書き込みで開いたまま / FILE_SHARE_DELETE なしで読み取り中 / ACL で READ_DATA 拒否 /
    // ACL で対象の DELETE と親の DELETE_CHILD を拒否 → 削除用オープンが 32・5、識別確認の時点で一致に見えるため DELETE_FAILED。
    // 後続の安全な MATCHED は削除される。停止はないが DELETE_FAILED があるためエラー (1)。
    // 識別確認は拒否の理由も、拒否されたオープンと同じ個体であることも保証しない (SPEC §8.4 の限界)。一致しても削除しないことを確かめる。
    [Theory]
    [InlineData("hold-write", 32)]
    [InlineData("hold-read-share-read", 32)]
    [InlineData("acl-read-data", 5)]
    [InlineData("acl-delete-and-delete-child", 5)]
    public void D11_OpenDeniedButLooksSame_IsDeleteFailedWithoutGuaranteeingSameObject(string situation, int expectedError)
    {
        var f = CreateAbc($"{nameof(D11_OpenDeniedButLooksSame_IsDeleteFailedWithoutGuaranteeingSameObject)}-{situation}");
        var b = f.Path("b.txt");
        HelperProcess? helper = null;
        Result r;
        using (var acl = new AclChanges(output))
        {
            if (situation == "acl-delete-and-delete-child")
            {
                // 親の DACL を変えると icacls が子の ACL を書き直し、a.txt・c.txt の ChangeTime も変わる (再検証で停止する)。
                // そのため親の DELETE_CHILD の拒否は初回分類の前に設定し、確認待ち中には対象の DELETE だけを拒否する。
                acl.Deny(f.Target, "DC");
            }

            try
            {
                r = RunDeleting(f.Zip, f.Target, f.Guard, awaiting: () =>
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
                            acl.Deny(b, "DE");
                            break;
                    }
                });
            }
            finally
            {
                helper?.Dispose();
            }
        }

        Log(r);
        Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        Assert.Null(r.Deletion.Stop);
        Assert.Equal(["a.txt", "c.txt"], r.DeletedNames);
        var failure = Assert.Single(r.Deletion.Failed);
        Assert.Equal("b.txt", failure.Entry.Name);
        Assert.Contains($"Win32 エラー {expectedError}", failure.Reason, StringComparison.Ordinal);
        Assert.Contains("識別確認の時点ではスナップショットと一致する通常ファイルに見える", failure.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(b));
        Assert.Equal(2, f.Guard.CheckCount);
        Assert.Contains("DELETE_FAILED が 1 件あるため、エラーとして終了します", r.Error, StringComparison.Ordinal);
    }

    // D14: 削除用オープンの段階で同一性に疑義 (対象消失 2、親の消失 3、ディレクトリ化 5 + 識別確認の不一致、削除保留中 5 + 識別確認の失敗) → 停止
    [Theory]
    [InlineData("vanished", "Win32 エラー 2")]
    [InlineData("parent-vanished", "Win32 エラー 3")]
    [InlineData("directory", "識別確認でスナップショットと不一致")]
    [InlineData("delete-pending", "識別確認も失敗")]
    public void D14_IdentityInDoubtAtOpen_Stops(string situation, string reasonPart)
    {
        var f = Create($"{nameof(D14_IdentityInDoubtAtOpen_Stops)}-{situation}", ("a.txt", A), ("p/", null), ("p/b.txt", B), ("c.txt", C));
        var p = f.Path("p");
        var b = f.Path(@"p\b.txt");
        HelperProcess? helper = null;
        Result r;
        try
        {
            r = RunDeleting(f.Zip, f.Target, f.Guard, awaiting: () =>
            {
                switch (situation)
                {
                    case "vanished":
                        File.Move(b, System.IO.Path.Combine(f.Dir, "b.moved"));
                        break;
                    case "parent-vanished":
                        Directory.Move(p, System.IO.Path.Combine(f.Dir, "p.moved"));
                        break;
                    case "directory":
                        File.Move(b, System.IO.Path.Combine(f.Dir, "b.moved"));
                        Directory.CreateDirectory(b);
                        break;
                    case "delete-pending":
                        // 別プロセスが削除を指示してハンドルを保持する (ヘルパーもガードで fixture 内であることを確かめる)。
                        helper = HelperProcess.Hold("hold-delete-pending", f.Dir, b);
                        break;
                }
            });
        }
        finally
        {
            helper?.Dispose();
        }

        Log(r);
        Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        Assert.Equal(["a.txt"], r.DeletedNames);
        Assert.Empty(r.Deletion.Failed);
        Assert.Equal("p/b.txt", r.Deletion.Stop!.Entry.Name);
        Assert.Contains(reasonPart, r.Deletion.Stop.Reason, StringComparison.Ordinal);
        Assert.Equal(1, r.Deletion.NotProcessedCount);
        Assert.True(f.Exists("c.txt"));
        Assert.Equal(1, f.Guard.CheckCount);
    }

    // D16: 最終確認の直前に ADS 追加 / hardlink 追加 / read-only 付与 → 最終確認で検出して停止
    [Theory]
    [InlineData("ads")]
    [InlineData("hardlink")]
    [InlineData("readonly")]
    public void D16_ChangeBeforeFinalCheck_Stops(string change)
    {
        var f = CreateAbc($"{nameof(D16_ChangeBeforeFinalCheck_Stops)}-{change}");
        var b = f.Path("b.txt");
        var hooks = new DeletionHooks
        {
            BeforeFinalCheck = (file, _) =>
            {
                if (file.Entry.Name != "b.txt")
                {
                    return;
                }

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
                    case "hardlink": CreateHardLink(System.IO.Path.Combine(f.Dir, "b-link.txt"), b); break;
                    case "readonly": File.SetAttributes(b, FileAttributes.ReadOnly | FileAttributes.Archive); break;
                }
            },
        };

        var r = RunDeleting(f.Zip, f.Target, f.Guard, hooks: hooks);

        AssertStoppedAtB(f, r, "最終確認で不一致");
        Assert.True(File.Exists(b));
    }

    // D19: 確認待ち中に、親ディレクトリ p を同名の別ディレクトリに差し替え、同じファイルを移して入れる (File ID と最終パスは不変)
    // → 親 File ID の不一致で停止
    [Fact]
    public void D19_ParentReplacedWithSameFileMovedIn_StopsOnParentFileId()
    {
        var f = Create(nameof(D19_ParentReplacedWithSameFileMovedIn_StopsOnParentFileId), ("a.txt", A), ("p/", null), ("p/b.txt", B), ("c.txt", C));
        var p = f.Path("p");
        var b = f.Path(@"p\b.txt");

        var r = RunDeleting(f.Zip, f.Target, f.Guard, awaiting: () =>
        {
            Directory.Move(p, p + "-old");
            Directory.CreateDirectory(p);
            File.Move(System.IO.Path.Combine(p + "-old", "b.txt"), b);
        });

        Log(r);
        Assert.Equal("p/b.txt", r.Deletion.Stop!.Entry.Name);
        Assert.Equal("同一性の再検証で不一致: 親 File ID", r.Deletion.Stop.Reason);
        Assert.Equal(["a.txt"], r.DeletedNames);
        Assert.True(File.Exists(b));
        Assert.True(f.Exists("c.txt"));
    }

    // D20: 確認待ち中に、途中のディレクトリを元のディレクトリを指す junction に差し替える / 大文字小文字だけ改名する
    // (親 File ID は不変) → 最終パスの不一致で停止
    [Theory]
    [InlineData("junction")]
    [InlineData("case")]
    public void D20_OnlyFinalPathDiffers_StopsOnFinalPath(string change)
    {
        var f = Create($"{nameof(D20_OnlyFinalPathDiffers_StopsOnFinalPath)}-{change}", ("a.txt", A), ("p/", null), ("p/b.txt", B), ("c.txt", C));
        var p = f.Path("p");

        var r = RunDeleting(f.Zip, f.Target, f.Guard, awaiting: () =>
        {
            if (change == "junction")
            {
                Directory.Move(p, p + "-real");
                CreateJunction(p, p + "-real");
            }
            else
            {
                Directory.Move(p, f.Path("P"));
            }
        });

        Log(r);
        Assert.Equal("p/b.txt", r.Deletion.Stop!.Entry.Name);
        Assert.Equal("同一性の再検証で不一致: 最終パス", r.Deletion.Stop.Reason);
        Assert.Equal(["a.txt"], r.DeletedNames);
        Assert.True(File.Exists(change == "junction" ? System.IO.Path.Combine(p + "-real", "b.txt") : f.Path(@"P\b.txt")));
        Assert.True(f.Exists("c.txt"));
    }

    // D21: 最終確認の直後・削除指示の直前に対象へ read-only を付与 → 指示が 5 で失敗し、対象は残り、以後を停止。前は削除済み、後は未処理
    [Fact]
    public void D21_ReadOnlyBeforeDisposition_StopsAndKeepsFile()
    {
        var f = CreateAbc(nameof(D21_ReadOnlyBeforeDisposition_StopsAndKeepsFile));
        var b = f.Path("b.txt");
        var hooks = new DeletionHooks
        {
            BeforeDisposition = (file, _) =>
            {
                if (file.Entry.Name == "b.txt")
                {
                    File.SetAttributes(b, FileAttributes.ReadOnly | FileAttributes.Archive);
                }
            },
        };

        var r = RunDeleting(f.Zip, f.Target, f.Guard, hooks: hooks);

        AssertStoppedAtB(f, r, "削除の指示が失敗");
        Assert.Contains("Win32 エラー 5", r.Deletion.Stop!.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(b));
        Assert.Equal(B, File.ReadAllBytes(b));
        Assert.Equal(2, f.Guard.CheckCount);
    }

    // D22: ACL で対象の DELETE だけを拒否する (親の DELETE_CHILD は許可のまま)。段階5の結果を実測して出力する。
    // どちらの結果でも誤削除にならないこと (対象以外が残り、停止なら対象も残る) だけを判定する (PLAN_DECISIONS.md DEC-12)。
    // awaiting: 確認待ち中に拒否する (PLAN_TESTS.md の記述どおり)。ACL の変更で対象の ChangeTime が変わるため、段階2で停止し段階5に届かない。
    // before-analysis: 初回分類の前に拒否する (スナップショットが変更後の ChangeTime を持つ)。段階5の成否はこちらで測る。
    [Theory]
    [InlineData("awaiting")]
    [InlineData("before-analysis")]
    public void D22_DeleteDeniedOnTargetOnly_RecordsStage5Result(string when)
    {
        var f = CreateAbc($"{nameof(D22_DeleteDeniedOnTargetOnly_RecordsStage5Result)}-{when}");
        var b = f.Path("b.txt");
        File.WriteAllBytes(f.Path("unrelated.txt"), Bytes("u"));
        Result r;
        using (var acl = new AclChanges(output))
        {
            if (when == "before-analysis")
            {
                acl.Deny(b, "DE");
            }

            r = RunDeleting(f.Zip, f.Target, f.Guard, awaiting: when == "awaiting" ? () => acl.Deny(b, "DE") : null);

            // 結果の記録 (PLAN_VALIDATION.md に転記する)。
            output.WriteLine($"D22 icacls (実行後): {(File.Exists(b) ? Cmd($"icacls \"{b}\"").Output.Trim() : "(対象なし)")}");
        }

        Log(r);
        var deletedB = r.DeletedNames.Contains("b.txt");
        var stop = r.Outcome.Deletion?.Stop;
        output.WriteLine(
            $"D22 ({when}) 結果: 段階5 = {(deletedB ? "成功 (DeletePending = true、削除された)" : $"停止 ({stop?.Reason})")}、"
            + $"b.txt の存在 = {File.Exists(b)}、削除済み = [{string.Join(", ", r.DeletedNames)}]、"
            + $"DELETE_FAILED = {r.Deletion.Failed.Count}、未処理 = {r.Deletion.NotProcessedCount}、終了状態 = {r.Outcome.Status}");

        Assert.False(f.Exists("a.txt"));
        Assert.True(f.Exists("unrelated.txt"));
        Assert.True(File.Exists(f.Zip));
        Assert.Empty(r.Deletion.Failed);
        if (deletedB)
        {
            Assert.Null(stop);
            Assert.False(File.Exists(b));
            Assert.Equal(["a.txt", "b.txt", "c.txt"], r.DeletedNames);
            Assert.Equal(ExitStatus.Success, r.Outcome.Status);
        }
        else
        {
            Assert.Equal("b.txt", stop!.Entry.Name);
            Assert.True(File.Exists(b));
            Assert.True(f.Exists("c.txt"));
            Assert.Equal(ExitStatus.Error, r.Outcome.Status);
        }
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

    // 同じハンドルで内容を書き換えた後、FILE_BASIC_INFO (作成・アクセス・更新・変更日時と属性) を元の値で書き戻す (テスト D10)。
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

    // 書き込み・削除を妨げない共有モードの読み取りで開いて FILE_BASIC_INFO を取得する (テスト D04)。
    private static FileBasicInfoNative ReadBasic(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadBasic(stream.SafeFileHandle);
    }

    private static unsafe FileBasicInfoNative ReadBasic(SafeFileHandle handle)
    {
        FileBasicInfoNative info;
        Assert.True(Kernel32.GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileBasicInfo, &info, (uint)sizeof(FileBasicInfoNative)));
        return info;
    }
}
