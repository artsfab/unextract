using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Unextract.Core.Tests.Fixtures;
using Unextract.Windows.Tests.Rar;
using Xunit.Abstractions;
using static Unextract.Windows.Tests.Integration.RealRun;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests.Integration;

// テスト U60〜U68: 採用版の UnRAR.dll (UnrarTestDll) と実 NTFS の target で、RAR の analyze・delete を製品の経路 (Prepare・前進・内容検証・
// 削除) どおりに実行する (docs/TESTING.md#rar)。RAR はテスト専用の生成器 (RarWriter、Stored) で作る。
// 削除してよいのは各テストが作った一意な fixture の中のファイルだけで、削除の指示の直前に毎回 DeletionGuard で確かめる (RealRun.Delete)。
// WinRAR の Rar.exe で作る実物 (圧縮・BLAKE2 の実値・NTFS ストリーム・実物の Solid・分割・SFX・ヘッダー暗号化) の試験は、実物の fixture が
// 用意できるまで未追加 (docs/OPEN_ISSUES.md#rar)。
[Collection(UnrarDllCollection.Name)]
public class RarIntegrationTests(ITestOutputHelper output)
{
    private static readonly byte[] A = Bytes("alpha content");
    private static readonly byte[] B = Bytes("bravo content");
    private static readonly byte[] C = Bytes("charlie content");

    private sealed record Fixture(string Dir, string Target, string Rar, DeletionGuard Guard)
    {
        public string Path(string relative) => System.IO.Path.Combine(Target, relative);

        public bool Exists(string relative) => File.Exists(Path(relative));
    }

    // fixture: dir/archive.rar、dir/target/。target に置くファイル (null はディレクトリ) を files で与える。
    private static Fixture Create(byte[] rar, params (string Name, byte[]? Content)[] files)
    {
        var dir = CreateDirectory();
        var target = Directory.CreateDirectory(System.IO.Path.Combine(dir, "target")).FullName;
        foreach (var (file, content) in files)
        {
            var path = System.IO.Path.Combine(target, file.Replace('/', '\\'));
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

        var path2 = System.IO.Path.Combine(dir, "archive.rar");
        File.WriteAllBytes(path2, rar);
        return new Fixture(dir, target, path2, new DeletionGuard(dir));
    }

    private static Rar5File File5(string name, byte[] data) => new() { Name = name, Data = data };

    private static Rar5File Dir5(string name) => new() { Name = name, IsDirectory = true, Attributes = 0x10 };

    private void Log(Result r) => output.WriteLine(r.Output + r.Error);

    // U60: Prepare の受理・拒否 (生成器で作れるもの)。拒否は両操作・両モードで同じ FATAL、削除0件、target 不変。
    public static TheoryData<string> PrepareCases => new()
    {
        "accept-windows-attrs", "accept-unix-mode", "accept-rar4",
        "sfx", "archive-solid", "archive-volume", "rar4-archive-solid", "rar4-archive-volume",
        "entry-solid", "rar4-entry-solid", "split-before", "split-after", "rar4-split-after",
        "encrypted-no-check", "encrypted-check", "rar4-encrypted",
        "unix-symlink", "rar4-unix-symlink", "windows-junction", "hardlink", "file-copy",
        "dos-directory-on-file", "dos-reparse", "unix-type-zero", "unix-dir-type-on-file", "unix-file-type-on-dir",
        "directory-with-data", "without-hash", "unknown-size", "dictionary-8g", "unix-backslash-colon-collision",
    };

    private static (byte[] Rar, FatalKind? Expected) PrepareCase(string id) => id switch
    {
        "accept-windows-attrs" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, Attributes = 0x2027 }]), null),
        "accept-unix-mode" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, HostOs = 1, Attributes = 0x81B4 }]), null),
        "accept-rar4" => (Rar4Writer.Build([new Rar4File { Name = "a.txt", Data = A }]), null),
        "sfx" => ([0x4D, 0x5A, .. Rar5Writer.Build([File5("a.txt", A)])], FatalKind.ArchiveNotRar),
        "archive-solid" => (Rar5Writer.Build([File5("a.txt", A)], archiveFlags: 0x4), FatalKind.ArchiveSolid),
        "archive-volume" => (Rar5Writer.Build([File5("a.txt", A)], archiveFlags: 0x1), FatalKind.ArchiveMultiVolume),
        "rar4-archive-solid" => (Rar4Writer.Build([new Rar4File { Name = "a.txt", Data = A }], archiveFlags: 0x8), FatalKind.ArchiveSolid),
        "rar4-archive-volume" => (Rar4Writer.Build([new Rar4File { Name = "a.txt", Data = A }], archiveFlags: 0x1), FatalKind.ArchiveMultiVolume),
        "entry-solid" => (Rar5Writer.Build([File5("a.txt", A), new Rar5File { Name = "b.txt", Data = B, SolidFlag = true }]), FatalKind.EntrySolid),
        "rar4-entry-solid" => (Rar4Writer.Build([new Rar4File { Name = "a.txt", Data = A }, new Rar4File { Name = "b.txt", Data = B, Solid = true }]), FatalKind.EntrySolid),
        "split-before" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, SplitBefore = true }]), FatalKind.EntrySplit),
        "split-after" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, SplitAfter = true }]), FatalKind.EntrySplit),
        "rar4-split-after" => (Rar4Writer.Build([new Rar4File { Name = "a.txt", Data = A, SplitAfter = true }]), FatalKind.EntrySplit),
        "encrypted-no-check" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, EncryptRecord = 2 }]), FatalKind.EntryEncrypted),
        "encrypted-check" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, EncryptRecord = 1 }]), FatalKind.EntryEncrypted),
        "rar4-encrypted" => (Rar4Writer.Build([new Rar4File { Name = "a.txt", Data = A, Password = true }]), FatalKind.EntryEncrypted),
        "unix-symlink" => (Rar5Writer.Build([new Rar5File { Name = "link", HostOs = 1, Attributes = 0xA1FF, RedirType = 1 }]), FatalKind.EntryRedirection),
        "rar4-unix-symlink" => (Rar4Writer.Build([new Rar4File { Name = "link", HostOs = 3, Attributes = 0xA1FF, Data = "a.txt"u8.ToArray() }]), FatalKind.EntryRedirection),
        "windows-junction" => (Rar5Writer.Build([new Rar5File { Name = "j", RedirType = 3 }]), FatalKind.EntryRedirection),
        "hardlink" => (Rar5Writer.Build([File5("a.txt", A), new Rar5File { Name = "h.txt", RedirType = 4, RedirTarget = "a.txt" }]), FatalKind.EntryRedirection),
        "file-copy" => (Rar5Writer.Build([File5("a.txt", A), new Rar5File { Name = "c.txt", RedirType = 5, RedirTarget = "a.txt" }]), FatalKind.EntryRedirection),
        "dos-directory-on-file" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, Attributes = 0x30 }]), FatalKind.DosDirectoryAttributeOnFileEntry),
        "dos-reparse" => (Rar5Writer.Build([new Rar5File { Name = "d", IsDirectory = true, Attributes = 0x410 }]), FatalKind.DosReparsePointAttribute),
        "unix-type-zero" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, HostOs = 1, Attributes = 0x1A4 }]), FatalKind.UnsupportedEntryType),
        "unix-dir-type-on-file" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, HostOs = 1, Attributes = 0x41ED }]), FatalKind.FileEntryWithDirectoryType),
        "unix-file-type-on-dir" => (Rar5Writer.Build([new Rar5File { Name = "d", IsDirectory = true, HostOs = 1, Attributes = 0x81ED }]), FatalKind.DirectoryEntryWithFileType),
        "directory-with-data" => (Rar5Writer.Build([new Rar5File { Name = "d", IsDirectory = true, Attributes = 0x10, DeclaredSize = 5 }]), FatalKind.DirectoryEntryWithData),
        "without-hash" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, OmitCrc = true }]), FatalKind.EntryWithoutHash),
        "unknown-size" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, UnknownSize = true }]), FatalKind.EntryTooLarge),
        "dictionary-8g" => (Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = A, CompressionInfo = 1u | (16u << 10) }]), FatalKind.EntryDictionaryTooLarge),
        "unix-backslash-colon-collision" => (Rar5Writer.Build(
            [
                new Rar5File { Name = "a\\b.txt", Data = A, HostOs = 1, Attributes = 0x81A4 },
                new Rar5File { Name = "a:b.txt", Data = A, HostOs = 1, Attributes = 0x81A4 },
            ]), FatalKind.DuplicateEntry),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    [Theory]
    [MemberData(nameof(PrepareCases))]
    public void U60_Prepare_AcceptOrReject(string id)
    {
        var (rar, expected) = PrepareCase(id);
        foreach (var mode in new[] { RunMode.Strict, RunMode.Fast })
        {
            var f = Create(rar, ("a.txt", A), ("b.txt", B));
            var before = Snapshot(f.Target);

            var analyzed = Analyze(f.Rar, f.Target, mode: mode);
            var deleted = Delete(f.Rar, f.Target, f.Guard, mode: mode);

            Log(analyzed);
            Log(deleted);
            if (expected is { } kind)
            {
                Assert.Equal(kind, analyzed.PrepareError?.Kind);
                Assert.Equal(kind, deleted.PrepareError?.Kind);
                Assert.Equal(ExitStatus.Error, analyzed.Status);
                Assert.Equal(ExitStatus.Error, deleted.Status);
                Assert.Empty(deleted.Probe!.Opened);
                Assert.Equal(before, Snapshot(f.Target));
            }
            else
            {
                Assert.Null(analyzed.PrepareError);
                Assert.Equal(Candidate(mode), analyzed.Of("a.txt"));
                Assert.Equal(["a.txt"], deleted.DeletedNames);
                Assert.False(f.Exists("a.txt"));
                Assert.True(f.Exists("b.txt"));
            }
        }
    }

    // U61: 両モードの analyze・delete。analyze は非破壊 (target と RAR の不変)。Strict は全バイト一致だけを、Fast は同じサイズを削除する。
    // MODIFIED・MISSING・ADS を持つ target (SKIPPED_SPECIAL_FILE)・ディレクトリは残る。ディレクトリを通過する前進を含む。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void U61_AnalyzeThenDelete(RunMode mode)
    {
        var rar = Rar5Writer.Build(
        [
            File5("same.txt", A), File5("changed.txt", B), File5("short.txt", C), Dir5("d"), File5("d/deep.txt", C),
            File5("d/ads.txt", A), File5("missing.txt", A), Dir5("empty"),
        ]);
        var f = Create(rar,
            ("same.txt", A), ("changed.txt", Bytes("bravo CONTENT")), ("short.txt", Bytes("x")), ("d/deep.txt", C), ("d/ads.txt", A), ("empty", null), ("unrelated.txt", A));
        File.WriteAllText(f.Path(@"d\ads.txt") + ":Zone.Identifier", "[ZoneTransfer]");
        var rarHash = Hash(f.Rar);
        var before = Snapshot(f.Target);

        var analyzed = Analyze(f.Rar, f.Target, mode: mode);

        Log(analyzed);
        Assert.Equal(ExitStatus.Success, analyzed.Status);
        Assert.Equal(before, Snapshot(f.Target));
        Assert.Equal(rarHash, Hash(f.Rar));
        var fast = mode == RunMode.Fast;
        Assert.Equal(Candidate(mode), analyzed.Of("same.txt"));
        Assert.Equal(fast ? Classification.SameSize : Classification.Modified, analyzed.Of("changed.txt"));
        Assert.Equal(Classification.Modified, analyzed.Of("short.txt"));
        Assert.Equal(Candidate(mode), analyzed.Of(@"d\deep.txt"));
        Assert.Equal(Classification.SkippedSpecialFile, analyzed.Of(@"d\ads.txt"));
        Assert.Equal(Classification.Missing, analyzed.Of("missing.txt"));
        Assert.Equal(Classification.Directory, analyzed.Of(@"d\"));

        var r = Delete(f.Rar, f.Target, f.Guard, mode: mode);

        Log(r);
        string[] deleted = fast ? ["same.txt", "changed.txt", @"d\deep.txt"] : ["same.txt", @"d\deep.txt"];
        Assert.Equal(ExitStatus.Success, r.Status);
        Assert.Equal(deleted, r.DeletedNames);
        Assert.Equal(deleted.Length, f.Guard.CheckCount);
        Assert.Equal(2, r.Report.DirectoryCount);
        Assert.Equal(DeleteStatus.SkippedSpecialFile, r.ResultOf(@"d\ads.txt").Status);
        Assert.True(f.Exists("unrelated.txt"));
        Assert.True(f.Exists("short.txt"));
        Assert.Equal(!fast, f.Exists("changed.txt"));
        Assert.True(Directory.Exists(f.Path("empty")));
        Assert.Equal(rarHash, Hash(f.Rar));
    }

    // U62: target 内の RAR 自身 (S09 相当)。analyze は SKIPPED (アーカイブ自身)、delete は保持中の RAR の削除用オープンが共有違反で DELETE_FAILED。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void U62_ArchiveItselfIsNotDeleted(RunMode mode)
    {
        var dir = CreateDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        var rar = Path.Combine(target, "archive.rar");
        File.WriteAllBytes(rar, Rar5Writer.Build([File5("archive.rar", A), File5("later.txt", B)]));
        File.WriteAllBytes(Path.Combine(target, "later.txt"), B);
        var hash = Hash(rar);

        var analyzed = Analyze(rar, target, mode: mode);
        var r = Delete(rar, target, new DeletionGuard(dir), mode: mode);

        Log(r);
        Assert.Equal(SkipReason.ArchiveItself, analyzed.SkipOf("archive.rar"));
        Assert.Equal(DeleteStatus.DeleteFailed, r.ResultOf("archive.rar").Status);
        Assert.StartsWith("削除用に開けません (Win32 エラー 32", r.ResultOf("archive.rar").Reason, StringComparison.Ordinal);
        Assert.Equal(["later.txt"], r.DeletedNames);
        Assert.Equal(ExitStatus.Error, r.Status);
        Assert.True(File.Exists(rar));
        Assert.Equal(hash, Hash(rar));
    }

    // U63: --entries で選択外のエントリとディレクトリを前進で通過して削除する。選択外は触れず、選択外のディレクトリは処理対象外。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void U63_EntriesSelection_PassesUnselected(RunMode mode)
    {
        var rar = Rar5Writer.Build([File5("a.txt", A), Dir5("d"), File5("d/b.txt", B), File5("c.txt", C)]);
        var f = Create(rar, ("a.txt", A), ("d/b.txt", B), ("c.txt", C));
        var entries = Path.Combine(f.Dir, "entries.txt");
        File.WriteAllBytes(entries, Bytes("c.txt\n"));

        var r = Delete(f.Rar, f.Target, f.Guard, mode: mode, entriesPath: entries);

        Log(r);
        Assert.Equal(ExitStatus.Success, r.Status);
        Assert.Equal(["c.txt"], r.DeletedNames);
        Assert.Equal(["c.txt"], r.Report.Results.Select(x => x.Entry.Name));
        Assert.Equal(0, r.Report.DirectoryCount);
        Assert.True(f.Exists("a.txt"));
        Assert.True(f.Exists(@"d\b.txt"));
        Assert.False(r.Opened("a.txt"));
        Assert.False(r.Opened(@"d\b.txt"));
    }

    // U64: Strict の内容検証の失敗 (DLL がデータの CRC 不一致を報告) は、そのエントリの STOP (analyze は FATAL)。それまでの削除は残り、
    // 後ろは未処理。Fast は内容を読まないので同じサイズとして削除する。
    [Theory]
    [InlineData(RunMode.Strict)]
    [InlineData(RunMode.Fast)]
    public void U64_ContentFailure_StopsInStrict(RunMode mode)
    {
        var rar = Rar5Writer.Build([File5("a.txt", A), new Rar5File { Name = "b.txt", Data = B, Crc = 0xDEADBEEF }, File5("c.txt", C)]);
        var f = Create(rar, ("a.txt", A), ("b.txt", B), ("c.txt", C));

        var analyzed = Analyze(f.Rar, f.Target, mode: mode);
        var r = Delete(f.Rar, f.Target, f.Guard, mode: mode);

        Log(analyzed);
        Log(r);
        if (mode == RunMode.Fast)
        {
            Assert.Equal(ExitStatus.Success, r.Status);
            Assert.Equal(["a.txt", "b.txt", "c.txt"], r.DeletedNames);
            return;
        }

        Assert.Equal(FatalKind.ContentReadFailed, analyzed.Analysis.Fatal!.Kind);
        Assert.Equal("b.txt", analyzed.Analysis.Fatal.Entry!.Name);
        Assert.Equal(ExitStatus.Error, r.Status);
        Assert.Equal(["a.txt"], r.DeletedNames);
        var stop = r.Report.Stop!;
        Assert.Equal("b.txt", stop.Entry.Name);
        Assert.Equal(FatalKind.ContentReadFailed, stop.Failure!.FatalKind);
        Assert.Equal(1, r.Report.NotProcessedCount);
        Assert.True(f.Exists("b.txt"));
        Assert.True(f.Exists("c.txt"));
        Assert.False(r.Opened("c.txt"));
    }

    // U65: 内容比較候補でないエントリのデータの破損は読まない (docs/RATIONALE.md#rar-content)。1件目 (target に無い) が壊れていても2件目は MATCHED。
    [Fact]
    public void U65_CorruptNonCandidate_IsNotRead()
    {
        var rar = Rar5Writer.Build([new Rar5File { Name = "bad.txt", Data = A, Crc = 1 }, File5("a.txt", A)]);
        var f = Create(rar, ("a.txt", A));

        var analyzed = Analyze(f.Rar, f.Target);
        var r = Delete(f.Rar, f.Target, f.Guard);

        Assert.Equal(Classification.Missing, analyzed.Of("bad.txt"));
        Assert.Equal(Classification.Matched, analyzed.Of("a.txt"));
        Assert.Equal(ExitStatus.Success, r.Status);
        Assert.Equal(["a.txt"], r.DeletedNames);
    }

    // U66: 切り詰め (全位置)。例外にならず、Prepare の FATAL か、列挙されたエントリ (元の並びの先頭部分) の判定になる。
    // データが切り詰め位置より後ろにあるエントリは MATCHED にならない。各位置の結果を出力に残す (観測の記録)。
    [Fact]
    public void U66_Truncation_AllPositions()
    {
        string[] names = ["f0.txt", "f1.txt", "f2.txt"];
        var files = names.Select(n => File5(n, Bytes("0123456789"))).ToArray();
        var full = Rar5Writer.Build(files);
        var dataEnds = Enumerable.Range(1, files.Length).Select(i => Rar5Writer.Build(files.Take(i), writeEnd: false).Length).ToArray();
        var dir = CreateDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        foreach (var name in names)
        {
            File.WriteAllBytes(Path.Combine(target, name), Bytes("0123456789"));
        }

        var before = Snapshot(target);
        for (var length = 0; length < full.Length; length++)
        {
            var rar = Path.Combine(dir, $"t{length:D3}.rar");
            File.WriteAllBytes(rar, full[..length]);

            var r = Analyze(rar, target);

            var summary = r.PrepareError is { } fatal
                ? $"FATAL {fatal.Kind}"
                : string.Join(",", r.Analysis.Results.Select(x => $"{x.Entry.Name}={x.Classification}")) + (r.Analysis.Fatal is { } f ? $" FATAL {f.Kind}" : string.Empty);
            output.WriteLine($"{length}: {summary}");
            if (r.PrepareError is null)
            {
                var listed = r.Analysis.Results.Select(x => x.Entry.Name).Concat(r.Analysis.Fatal?.Entry is { } e ? [e.Name] : []).ToArray();
                Assert.Equal(names.Take(listed.Length), listed);
                for (var i = 0; i < r.Analysis.Results.Count; i++)
                {
                    if (r.Analysis.Results[i].Classification == Classification.Matched)
                    {
                        Assert.True(dataEnds[i] <= length, $"{length}: {names[i]} は切り詰め位置より後ろのデータで MATCHED");
                    }
                }
            }
        }

        Assert.Equal(before, Snapshot(target));
    }

    // U67: 1ビットの破壊 (全バイト)。例外にならない。データ部分の破壊では MATCHED にならない (DLL の CRC 照合または全バイト比較で検出)。
    [Fact]
    public void U67_BitFlips_DoNotThrow_AndDataCorruptionIsNotMatched()
    {
        var data = Bytes("0123456789abcdef");
        var full = Rar5Writer.Build([File5("a.txt", data), File5("b.txt", data)]);
        var dataStarts = new[] { Rar5Writer.Build([File5("a.txt", data)], writeEnd: false).Length - data.Length, Rar5Writer.Build([File5("a.txt", data), File5("b.txt", data)], writeEnd: false).Length - data.Length };
        var dir = CreateDirectory();
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        File.WriteAllBytes(Path.Combine(target, "a.txt"), data);
        File.WriteAllBytes(Path.Combine(target, "b.txt"), data);

        for (var offset = 0; offset < full.Length; offset++)
        {
            var corrupt = full.ToArray();
            corrupt[offset] ^= (byte)(1 << (offset % 8));
            var rar = Path.Combine(dir, $"b{offset:D3}.rar");
            File.WriteAllBytes(rar, corrupt);

            var r = Analyze(rar, target);

            if (r.PrepareError is null)
            {
                for (var i = 0; i < 2; i++)
                {
                    if (offset >= dataStarts[i] && offset < dataStarts[i] + data.Length)
                    {
                        Assert.DoesNotContain(r.Analysis.Results, x => x.Entry.Index == i && x.Classification == Classification.Matched);
                    }
                }
            }
        }
    }

    // U68: DLL が作業ディレクトリ・target・アーカイブのフォルダーに何も書かない (RAR_EXTRACT と展開先を使わない)。
    // エントリ名は一意にし、どの場所にも同名のファイル・フォルダーが現れないことを確かめる。両モードの analyze と delete の成功と分類も確かめる
    // (先頭がハッシュの無いディレクトリなので、ヘッダー照合の FileCRC の扱いの回帰を検出する。docs/RATIONALE.md#rar-dll-usage)。
    [Fact]
    public void U68_LibraryWritesNothing()
    {
        var unique = $"u68-{Guid.NewGuid():N}";
        var rar = Rar5Writer.Build([Dir5(unique), File5($"{unique}/{unique}.txt", A), File5($"{unique}.bin", B)]);
        var f = Create(rar, ($"{unique}.bin", B));
        var archiveFolder = Snapshot(f.Dir);

        var strict = Analyze(f.Rar, f.Target);
        var fast = Analyze(f.Rar, f.Target, mode: RunMode.Fast);
        var r = Delete(f.Rar, f.Target, f.Guard);

        Log(strict);
        Log(fast);
        Log(r);
        foreach (var (analyzed, mode) in new[] { (strict, RunMode.Strict), (fast, RunMode.Fast) })
        {
            Assert.Equal(ExitStatus.Success, analyzed.Status);
            Assert.Null(analyzed.PrepareError);
            Assert.Null(analyzed.Analysis.Fatal);
            Assert.Equal(Classification.Directory, analyzed.Of($@"{unique}\"));
            Assert.Equal(Classification.Missing, analyzed.Of($@"{unique}\{unique}.txt"));
            Assert.Equal(Candidate(mode), analyzed.Of($"{unique}.bin"));
        }

        Assert.Equal(ExitStatus.Success, r.Status);
        Assert.Equal([$"{unique}.bin"], r.DeletedNames);
        archiveFolder.Remove(Path.Combine("target", $"{unique}.bin"));
        archiveFolder.Remove("target");
        var after = Snapshot(f.Dir);
        after.Remove("target");
        Assert.Equal(archiveFolder, after);
        foreach (var place in new[] { Environment.CurrentDirectory, Path.GetTempPath(), AppContext.BaseDirectory })
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(place, unique + "*"));
        }
    }
}
