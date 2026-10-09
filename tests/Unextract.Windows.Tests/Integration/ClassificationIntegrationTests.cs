using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Xunit.Abstractions;
using static Unextract.Windows.Tests.Integration.RealRun;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests.Integration;

// 実 ZIP と実 NTFS の target での analyze の分類 (docs/spec/filesystem.md#classification、docs/spec/filesystem.md#special-files)。fixture はテストの出力先の fixtures/ の下に毎回ユニークな名前で作り、
// 削除しない。junction は同じ fixture 内の別ディレクトリを指すものだけ。symlink は作らない (特権が必要)。
// 前提が成り立たない項目は「前提不成立」と出力して、その項目の確認を行わずに終える (成功・失敗のどちらにも数えないよう報告する)。
public class ClassificationIntegrationTests(ITestOutputHelper output)
{
    private static readonly byte[] Hello = Bytes("hello");

    private static (string Dir, string Target) NewTarget()
    {
        var dir = CreateDirectory();
        return (dir, Directory.CreateDirectory(Path.Combine(dir, "target")).FullName);
    }

    private void NotSatisfied(string what) => output.WriteLine($"前提不成立: {what}");

    // T05 (実走査): ZIP にない target ファイルが多数あっても、表示・集計・分類されない
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void T05_UnrelatedFiles_AreIgnored(RunMode mode)
    {
        var (dir, target) = NewTarget();
        File.WriteAllBytes(Path.Combine(target, "keep.txt"), Hello);
        var unrelatedDir = Directory.CreateDirectory(Path.Combine(target, "unrelated-dir")).FullName;
        for (var i = 0; i < 300; i++)
        {
            File.WriteAllText(Path.Combine(target, $"unrelated-{i}.txt"), "x");
            File.WriteAllText(Path.Combine(unrelatedDir, $"unrelated-{i}.txt"), "x");
        }

        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("keep.txt", Hello), ("gone.txt", Hello)));

        var result = Analyze(zip, target, mode: mode);

        Assert.Null(result.Analysis.Fatal);
        Assert.Equal(2, result.Analysis.Results.Count);
        Assert.Equal(Candidate(mode), result.Of("keep.txt"));
        Assert.Equal(Classification.Missing, result.Of("gone.txt"));
        Assert.DoesNotContain("unrelated", result.Output, StringComparison.Ordinal);
    }

    // T06: target 側の大小文字違い (ファイル名、途中のディレクトリ名) → MISSING
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void T06_CaseDifference_IsMissing(RunMode mode)
    {
        var (dir, target) = NewTarget();
        File.WriteAllBytes(Path.Combine(target, "File.txt"), Hello);
        Directory.CreateDirectory(Path.Combine(target, "Dir"));
        File.WriteAllBytes(Path.Combine(target, "Dir", "a.txt"), Hello);
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("file.txt", Hello), ("dir/a.txt", Hello)));

        var result = Analyze(zip, target, mode: mode);

        Assert.Null(result.Analysis.Fatal);
        Assert.Equal(Classification.Missing, result.Of("file.txt"));
        Assert.Equal(Classification.Missing, result.Of("dir/a.txt"));
    }

    // T06: 8.3 名でだけ一致 (ファイル名、途中のディレクトリ名) → MISSING。8.3 名が生成されていなければ前提不成立として報告する。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void T06_ShortNameOnly_IsMissing(RunMode mode)
    {
        var (dir, target) = NewTarget();
        var longFile = Path.Combine(target, "Long File Name Sample.txt");
        File.WriteAllBytes(longFile, Hello);
        var longDir = Directory.CreateDirectory(Path.Combine(target, "Long Directory Sample")).FullName;
        File.WriteAllBytes(Path.Combine(longDir, "inner.txt"), Hello);
        var shortFile = Path.GetFileName(ShortPath(longFile));
        var shortDir = Path.GetFileName(ShortPath(longDir));
        if (string.Equals(shortFile, "Long File Name Sample.txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shortDir, "Long Directory Sample", StringComparison.OrdinalIgnoreCase))
        {
            NotSatisfied("T06 (8.3 名): このボリュームでは 8.3 名が生成されていない");
            return;
        }

        output.WriteLine($"8.3 名: {shortFile}, {shortDir}");
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip((shortFile, Hello), ($"{shortDir}/inner.txt", Hello)));

        var result = Analyze(zip, target, mode: mode);

        Assert.Null(result.Analysis.Fatal);
        Assert.Equal(Classification.Missing, result.Of(shortFile));
        Assert.Equal(Classification.Missing, result.Of($"{shortDir}/inner.txt"));
    }

    // T07: ADS (Zone.Identifier)、hardlink、read-only、system、temporary、許可集合外の属性 (offline) → SKIPPED_SPECIAL_FILE
    // T08: archive、hidden、not-content-indexed だけ → MATCHED
    // T09: ZIP はファイル、target はディレクトリ → SKIPPED (FATAL にならない)
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void T07_T08_T09_SpecialAndAllowedAttributes(RunMode mode)
    {
        var (dir, target) = NewTarget();
        string File(string name, FileAttributes? attributes = null)
        {
            var path = Path.Combine(target, name);
            System.IO.File.WriteAllBytes(path, Hello);
            if (attributes is { } value)
            {
                System.IO.File.SetAttributes(path, value);
            }

            return path;
        }

        System.IO.File.WriteAllText(File("ads.txt") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        CreateHardLink(Path.Combine(target, "hardlink.txt"), File("hardlink-source.bin"));
        File("readonly.txt", FileAttributes.ReadOnly | FileAttributes.Archive);
        File("system.txt", FileAttributes.System | FileAttributes.Archive);
        File("temporary.txt", FileAttributes.Temporary | FileAttributes.Archive);
        File("offline.txt", FileAttributes.Offline | FileAttributes.Archive);
        File("archive.txt", FileAttributes.Archive);
        File("normal.txt", FileAttributes.Normal);
        File("hidden.txt", FileAttributes.Hidden | FileAttributes.Archive);
        File("notindexed.txt", FileAttributes.NotContentIndexed | FileAttributes.Archive);
        Directory.CreateDirectory(Path.Combine(target, "folder.txt"));

        string[] names = ["ads.txt", "hardlink.txt", "readonly.txt", "system.txt", "temporary.txt", "offline.txt",
            "archive.txt", "normal.txt", "hidden.txt", "notindexed.txt", "folder.txt"];
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(names.Select(n => (n, (byte[]?)Hello)).ToArray()));

        var result = Analyze(zip, target, mode: mode);

        Assert.Null(result.Analysis.Fatal);
        Assert.Equal(SkipReason.AlternateDataStream, result.SkipOf("ads.txt"));
        Assert.Equal(SkipReason.HardLink, result.SkipOf("hardlink.txt"));
        Assert.Equal(SkipReason.Attributes, result.SkipOf("readonly.txt"));
        Assert.Equal(SkipReason.Attributes, result.SkipOf("system.txt"));
        Assert.Equal(SkipReason.Attributes, result.SkipOf("temporary.txt"));
        Assert.Equal(SkipReason.Attributes, result.SkipOf("offline.txt"));
        Assert.Equal(SkipReason.Directory, result.SkipOf("folder.txt"));
        foreach (var name in new[] { "ads.txt", "hardlink.txt", "readonly.txt", "system.txt", "temporary.txt", "offline.txt", "folder.txt" })
        {
            Assert.Equal(Classification.SkippedSpecialFile, result.Of(name));
        }

        foreach (var name in new[] { "archive.txt", "normal.txt", "hidden.txt", "notindexed.txt" })
        {
            Assert.Equal(Candidate(mode), result.Of(name));
        }
    }

    // T08: NTFS 圧縮・sparse の属性だけ → MATCHED (Fast は SAME_SIZE)。属性を設定できない環境では前提不成立として報告する。
    [Theory]
    [InlineData("compressed", RunMode.Strict)]
    [InlineData("sparse", RunMode.Strict)]
    [InlineData("compressed", RunMode.Fast)]
    [InlineData("sparse", RunMode.Fast)]
    public void T08_CompressedAndSparse_AreMatched(string kind, RunMode mode)
    {
        var (dir, target) = NewTarget();
        var path = Path.Combine(target, "x.txt");
        File.WriteAllBytes(path, Hello);
        var (exitCode, text) = kind == "compressed" ? Cmd($"compact /c \"{path}\"") : Cmd($"fsutil sparse setflag \"{path}\"");
        var expected = kind == "compressed" ? FileAttributes.Compressed : FileAttributes.SparseFile;
        if (exitCode != 0 || (File.GetAttributes(path) & expected) == 0)
        {
            NotSatisfied($"T08 ({kind}): 属性を設定できない (exit {exitCode}: {text.Trim()})");
            return;
        }

        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("x.txt", Hello)));

        var result = Analyze(zip, target, mode: mode);

        Assert.Null(result.Analysis.Fatal);
        Assert.Equal(Candidate(mode), result.Of("x.txt"));
    }

    // T09: ZIP 自身に対応する対象 (ボリュームシリアルと File ID が一致) → SKIPPED_SPECIAL_FILE。ZIP は変わらない。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void T09_ArchiveItself_IsSkipped(RunMode mode)
    {
        var (_, target) = NewTarget();
        var zip = WriteZip(Path.Combine(target, "archive.zip"), Zip(("archive.zip", Hello)));
        var hash = Hash(zip);

        var result = Analyze(zip, target, mode: mode);

        Assert.Null(result.Analysis.Fatal);
        Assert.Equal(SkipReason.ArchiveItself, result.SkipOf("archive.zip"));
        Assert.Equal(hash, Hash(zip));
    }

    // T02: 親成分が存在しない・大小文字違い・通常ファイル → MISSING。T03: 親成分が junction → SKIPPED (リンク先を読まない)。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void T02_T03_ParentComponents(RunMode mode)
    {
        var (dir, target) = NewTarget();
        Directory.CreateDirectory(Path.Combine(target, "m2", "b"));
        File.WriteAllBytes(Path.Combine(target, "m2", "b", "c.txt"), Hello);
        File.WriteAllBytes(Path.Combine(target, "m3"), Hello);
        var elsewhere = Directory.CreateDirectory(Path.Combine(dir, "elsewhere", "b")).Parent!.FullName;
        File.WriteAllBytes(Path.Combine(elsewhere, "b", "c.txt"), Hello);
        CreateJunction(Path.Combine(target, "j"), elsewhere);
        var elsewhereBefore = Snapshot(elsewhere);
        var zip = WriteZip(
            Path.Combine(dir, "archive.zip"),
            Zip(("m1/b/c.txt", Hello), ("M2/b/c.txt", Hello), ("m3/b/c.txt", Hello), ("j/b/c.txt", Hello)));

        var result = Analyze(zip, target, mode: mode);

        Assert.Null(result.Analysis.Fatal);
        Assert.Equal(Classification.Missing, result.Of("m1/b/c.txt"));
        Assert.Equal(Classification.Missing, result.Of("M2/b/c.txt"));
        Assert.Equal(Classification.Missing, result.Of("m3/b/c.txt"));
        Assert.Equal(Classification.SkippedSpecialFile, result.Of("j/b/c.txt"));
        Assert.Equal(SkipReason.ParentReparsePoint, result.SkipOf("j/b/c.txt"));
        Assert.Equal(elsewhereBefore, Snapshot(elsewhere));
    }

    // T11: 比較対象を別のハンドルが書き込みで開いたままにする → 比較用オープンが共有違反 (32) で analyze は FATAL。
    // ここでは同一プロセス内の別ハンドルで模擬する。別プロセスでも同じ結果 (32) になることは docs/RATIONALE.md#sharing-limits の PoC 5 で確認済み。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void T11_TargetOpenForWriting_IsFatal(RunMode mode)
    {
        var (dir, target) = NewTarget();
        File.WriteAllBytes(Path.Combine(target, "first.txt"), Hello);
        var editing = Path.Combine(target, "editing.txt");
        File.WriteAllBytes(editing, Hello);
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("first.txt", Hello), ("editing.txt", Hello)));

        Result result;
        using (new FileStream(editing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            result = Analyze(zip, target, mode: mode);
        }

        var fatal = Assert.IsType<FatalError>(result.Analysis.Fatal);
        Assert.Equal(FatalKind.ComparisonOpenFailed, fatal.Kind);
        Assert.Contains("Win32 エラー 32", fatal.Describe(), StringComparison.Ordinal);
        Assert.Equal("editing.txt", fatal.Entry!.Name);
        Assert.Contains("\"editing.txt\"", result.Error, StringComparison.Ordinal);
        Assert.Equal(ExitStatus.Error, result.Status);
    }

    // T12 (実機): 判定終了後に target のハンドルが開いていない (別ハンドルで書き込み・改名ができる)
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void T12_NoTargetHandleRemainsAfterRun(RunMode mode)
    {
        var (dir, target) = NewTarget();
        var path = Path.Combine(target, "same.txt");
        File.WriteAllBytes(path, Hello);
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        File.WriteAllBytes(Path.Combine(target, "sub", "x.txt"), Hello);
        var zip = WriteZip(Path.Combine(dir, "archive.zip"), Zip(("same.txt", Hello), ("sub/x.txt", Hello)));

        var result = Analyze(zip, target, mode: mode);
        Assert.Equal([Candidate(mode), Candidate(mode)], result.Analysis.Results.Select(r => r.Classification));

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
        }

        File.Move(path, Path.Combine(target, "renamed.txt"));
        Directory.Move(Path.Combine(target, "sub"), Path.Combine(target, "sub-renamed"));
        Directory.Move(target, Path.Combine(dir, "target-renamed"));
    }

    // T15: ディレクトリ単位で大文字小文字を区別する NTFS ディレクトリに Foo と foo (内容が異なる) が共存する。
    // ZIP は大小文字だけ違う名前を同時に持てない (事前検証で FATAL) ため、Foo / foo / FOO を別々の ZIP で指す。
    // 設定 (fsutil file setCaseSensitiveInfo) ができない環境では前提不成立として報告する。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void T15_CaseSensitiveDirectory(RunMode mode)
    {
        var (dir, target) = NewTarget();
        var cs = Directory.CreateDirectory(Path.Combine(target, "cs")).FullName;
        var (exitCode, text) = Cmd($"fsutil file setCaseSensitiveInfo \"{cs}\" enable");
        if (exitCode != 0)
        {
            NotSatisfied($"T15: ディレクトリ単位の大文字小文字の区別を有効にできない (exit {exitCode}: {text.Trim()})");
            return;
        }

        File.WriteAllBytes(Path.Combine(cs, "Foo"), Bytes("upper"));
        File.WriteAllBytes(Path.Combine(cs, "foo"), Bytes("lower"));
        if (File.ReadAllText(Path.Combine(cs, "Foo")) != "upper")
        {
            NotSatisfied("T15: 大文字小文字だけ違う2つのファイルを共存させられない");
            return;
        }

        Result AnalyzeOne(string name, string content, string zipName) =>
            Analyze(WriteZip(Path.Combine(dir, zipName), Zip(($"cs/{name}", Bytes(content)))), target, mode: mode);

        var upper = AnalyzeOne("Foo", "upper", "upper.zip");
        var lower = AnalyzeOne("foo", "lower", "lower.zip");
        var other = AnalyzeOne("FOO", "upper", "other.zip");

        Assert.Equal(Candidate(mode), upper.Of("cs/Foo"));
        Assert.Equal(Candidate(mode), lower.Of("cs/foo"));
        Assert.Equal(Classification.Missing, other.Of("cs/FOO"));
        Assert.EndsWith(@"\cs\Foo", upper.Analysis.Results[0].Target, StringComparison.Ordinal);
        Assert.EndsWith(@"\cs\foo", lower.Analysis.Results[0].Target, StringComparison.Ordinal);

        // delete でも、それぞれ正しい個体を削除・残す。foo だけを削除し、Foo (内容 upper) は残る。
        var deleting = Delete(Path.Combine(dir, "lower.zip"), target, new DeletionGuard(dir), mode: mode);
        Assert.Equal(["cs/foo"], deleting.DeletedNames);
        Assert.False(File.Exists(Path.Combine(cs, "foo")));
        Assert.Equal("upper", File.ReadAllText(Path.Combine(cs, "Foo")));
    }

    // C01・C02・C03・C07 (代表): 実 NTFS の target で、サイズ一致 → FATAL、不存在・サイズ不一致 → FATAL にならない
    [Theory]
    [InlineData("C01")]
    [InlineData("C02")]
    [InlineData("C03")]
    [InlineData("C07")]
    public void C_BrokenEntry_OnRealTarget(string id)
    {
        var (zipBytes, expected) = Broken(id);
        foreach (var state in new[] { "missing", "size-differs", "size-matches" })
        {
            var (dir, target) = NewTarget();
            if (state == "size-differs")
            {
                File.WriteAllBytes(Path.Combine(target, "x.bin"), new byte[Data.Length + 1]);
            }
            else if (state == "size-matches")
            {
                File.WriteAllBytes(Path.Combine(target, "x.bin"), Data);
            }

            var result = Analyze(WriteZip(Path.Combine(dir, "archive.zip"), zipBytes), target);

            if (state == "size-matches")
            {
                Assert.Equal(expected, result.Analysis.Fatal?.Kind);
            }
            else
            {
                Assert.Null(result.Analysis.Fatal);
                Assert.Equal(state == "missing" ? Classification.Missing : Classification.Modified, result.Of("x.bin"));
            }
        }
    }

    // C15 (実機): C01・C02・C03・C07 (代表) を --fast で、target の3状態 (不存在 / サイズ ≠ N / サイズ = N) で実行する。
    // analyze と delete (確認に y) の両方。不存在は MISSING、サイズ ≠ N は MODIFIED、サイズ = N は FATAL ではなく
    // SAME_SIZE で、delete では削除される (docs/SPEC.md#modes)。ZIP の Open() が呼ばれないことは Core の C15 で確かめる。
    // 同じ fixture の Strict の delete は、サイズ = N で STOP (S13) になり削除しない。
    [Theory]
    [InlineData("C01")]
    [InlineData("C02")]
    [InlineData("C03")]
    [InlineData("C07")]
    public void C15_Fast_BrokenEntry_OnRealTarget(string id)
    {
        var (zipBytes, _) = Broken(id);
        foreach (var state in new[] { "missing", "size-differs", "size-matches" })
        {
            var (dir, target) = NewTarget();
            var x = Path.Combine(target, "x.bin");
            if (state == "size-differs")
            {
                File.WriteAllBytes(x, new byte[Data.Length + 1]);
            }
            else if (state == "size-matches")
            {
                File.WriteAllBytes(x, Data);
            }

            var zip = WriteZip(Path.Combine(dir, "archive.zip"), zipBytes);
            var expected = state switch
            {
                "missing" => Classification.Missing,
                "size-differs" => Classification.Modified,
                _ => Classification.SameSize,
            };

            var analyze = Analyze(zip, target, mode: RunMode.Fast);
            Assert.Null(analyze.Analysis.Fatal);
            Assert.Equal(expected, analyze.Of("x.bin"));
            Assert.Equal(ExitStatus.Success, analyze.Status);
            Assert.Equal(state != "missing", File.Exists(x));

            if (state == "size-matches")
            {
                var strict = Delete(zip, target, new DeletionGuard(dir), mode: RunMode.Strict);
                Assert.Equal(DeleteStatus.Stopped, strict.ResultOf("x.bin").Status);
                Assert.True(File.Exists(x));
            }

            var deleting = Delete(zip, target, new DeletionGuard(dir), mode: RunMode.Fast);
            Assert.Equal(ExitStatus.Success, deleting.Status);
            var status = state switch
            {
                "missing" => DeleteStatus.Missing,
                "size-differs" => DeleteStatus.Modified,
                _ => DeleteStatus.Deleted,
            };
            Assert.Equal(status, deleting.ResultOf("x.bin").Status);
            Assert.Equal(state == "size-differs", File.Exists(x));
            Assert.True(File.Exists(zip));
        }
    }

    // T17 (実機): T01 と同じ4つ (同一内容、1 byte 変更、サイズ違い、0 byte) を --fast で → SAME_SIZE、SAME_SIZE、MODIFIED、SAME_SIZE。
    // 同一内容と1 byte 変更を区別しない。0 byte に特例はない (docs/SPEC.md#modes)。内容を読まないことは Core の T17 で確かめる。
    [Fact]
    public void T17_Fast_SizeOnly_OnRealTarget()
    {
        var (dir, target) = NewTarget();
        File.WriteAllBytes(Path.Combine(target, "same.txt"), Hello);
        File.WriteAllBytes(Path.Combine(target, "changed.txt"), Bytes("hellO"));
        File.WriteAllBytes(Path.Combine(target, "size.txt"), Bytes("hello, world"));
        File.WriteAllBytes(Path.Combine(target, "zero.txt"), []);
        var zip = WriteZip(
            Path.Combine(dir, "archive.zip"),
            Zip(("same.txt", Hello), ("changed.txt", Hello), ("size.txt", Hello), ("zero.txt", [])));

        var result = Analyze(zip, target, mode: RunMode.Fast);

        Assert.Null(result.Analysis.Fatal);
        Assert.Equal(
            [Classification.SameSize, Classification.SameSize, Classification.Modified, Classification.SameSize],
            result.Analysis.Results.Select(r => r.Classification));
    }

    // R06 (実機): 宣言 Length 合計が 64 GiB 超 (ヘッダー値の書き換え、実データは小さい)。全エントリ MISSING でも FATAL。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void R06_DeclaredTotalOver64GiB_IsFatalEvenIfAllMissing(RunMode mode)
    {
        var (dir, target) = NewTarget();
        var patcher = new Core.Tests.Fixtures.ZipPatcher(Zip(Enumerable.Range(0, 5).Select(i => ($"f{i}.bin", (byte[]?)Hello)).ToArray()));
        for (var i = 0; i < 5; i++)
        {
            patcher.SetDeclaredLength(i, 13L * 1024 * 1024 * 1024);
        }

        var result = Analyze(WriteZip(Path.Combine(dir, "archive.zip"), patcher.ToArray()), target, mode: mode);

        Assert.Equal(FatalKind.TotalDeclaredLengthTooLarge, result.Analysis.Fatal?.Kind);
        Assert.Empty(result.Analysis.Results);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }
}
