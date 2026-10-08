using Unextract.Core.Rar;
using Unextract.Core.Results;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;
using Unextract.Windows.Rar;

namespace Unextract.Windows.Tests.Rar;

// テスト U51〜U54: 採用版の UnRAR.dll (UnrarTestDll。リポジトリ外に用意する) を実際にロードして、製品のローダー・ソース・セッションを確かめる。
// RAR はテスト専用の生成器 (RarWriter、Stored) で作る。DLL が無い・照合できない環境では失敗する (Skip・前提不成立にしない)。
// WinRAR の Rar.exe で作る実物 (圧縮・BLAKE2・NTFS ストリームなど) による試験は置き換えない (docs/TESTING.md#rar)。
[Collection(UnrarDllCollection.Name)]
public class RealUnrarTests
{
    private static readonly byte[] Hello = "hello rar\n"u8.ToArray();

    private static UnrarLibrary Library() => new(UnrarTestDll.Location);

    private static string WriteRar(byte[] content, string name = "a.rar")
    {
        var path = Path.Combine(TestFixture.CreateDirectory(), name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static RarArchiveSource OpenSource(string path)
    {
        var library = Library();
        var opened = RarArchiveSource.Open(path, Limits.Default, library.Load);
        Assert.Null(opened.Fatal);
        return Assert.IsType<RarArchiveSource>(opened.Source);
    }

    // U51: 採用版は照合とロードに成功し、RARGetDllVersion は採用版の値 (10)。期待版を変えると版の不一致になる。
    [Fact]
    public void U51_AdoptedLibrary_LoadsAndReportsVersion()
    {
        var result = Library().Load();

        Assert.Null(result.Fatal);
        Assert.Equal(UnrarNative.AdoptedVersion, result.Api!.Version());
        Assert.Equal(10, UnrarNative.AdoptedVersion);
    }

    [Fact]
    public void U51_VersionMismatch_IsReportedWithActualVersion()
    {
        var path = UnrarTestDll.Location;

        var result = new UnrarLibrary(path, UnrarLibrary.AdoptedSha256, UnrarNative.AdoptedVersion + 1).Load();

        Assert.Null(result.Api);
        Assert.Equal(FatalKind.RarLibraryUnavailable, result.Fatal!.Kind);
        Assert.Equal("版が一致しません (RARGetDllVersion=10)", result.Fatal.Detail);
        Assert.Equal(
            $"UnRAR.dll を使用できないため、RAR を処理できません (版が一致しません (RARGetDllVersion=10))。{path} に UnRAR.dll 7.23 (x64) の UnRAR64.dll を置いてください。ZIP の処理には影響しません。",
            result.Fatal.Describe());
    }

    // 期待する SHA-256 を変えると、同じファイルでもロードしない (版の固定はファイルのハッシュによる)。
    [Fact]
    public void U51_HashMismatch_WithAdoptedFile()
    {
        var result = new UnrarLibrary(UnrarTestDll.Location, new string('0', 64), UnrarNative.AdoptedVersion).Load();

        Assert.Equal("版が一致しません (SHA-256)", result.Fatal!.Detail);
    }

    // U52: 実物の DLL での一覧 (RAR5・RAR4)。名前・ディレクトリ・サイズ・作成元 OS・属性・ハッシュの種類の変換と、Unix 名の \ と : の _ への変換。
    [Fact]
    public void U52_Enumeration_Rar5()
    {
        var path = WriteRar(Rar5Writer.Build(
        [
            new Rar5File { Name = "d", IsDirectory = true, Attributes = 0x10 },
            new Rar5File { Name = "d/a.txt", Data = Hello },
            new Rar5File { Name = "u.txt", Data = Hello, HostOs = 1, Attributes = 0x81A4 },
            new Rar5File { Name = "a\\b.txt", Data = Hello, HostOs = 1, Attributes = 0x81A4 },
            new Rar5File { Name = "a:c.txt", Data = Hello, HostOs = 1, Attributes = 0x81A4 },
            new Rar5File { Name = "empty.txt" },
        ]));

        using var source = OpenSource(path);

        Assert.Equal(new RarArchiveInfo(false, false), source.Archive);
        Assert.Equal([@"d\", @"d\a.txt", "u.txt", "a_b.txt", "a_c.txt", "empty.txt"], source.Entries.Select(e => e.FullName));
        Assert.Equal([0L, 10, 10, 10, 10, 0], source.Entries.Select(e => e.Length));
        var unix = source.Entries.ElementAt(2).Rar!;
        Assert.Equal(RarHostOs.Unix, unix.HostOs);
        Assert.Equal(0x81A4u, unix.Attributes);
        Assert.Equal(RarHashType.Crc32, unix.HashType);
        Assert.True(source.Entries.First().Rar!.IsDirectory);
    }

    [Fact]
    public void U52_Enumeration_Rar4_UnicodeName()
    {
        var path = WriteRar(Rar4Writer.Build(
        [
            new Rar4File { Name = "a.txt", Data = Hello },
            new Rar4File { Name = "日本語.txt", Data = Hello, UnicodeName = true },
        ]));

        using var source = OpenSource(path);

        Assert.Equal(["a.txt", "日本語.txt"], source.Entries.Select(e => e.FullName));
        Assert.All(source.Entries, e => Assert.Equal(RarHostOs.Windows, e.Rar!.HostOs));
    }

    // U53: 実物の DLL によるセッションの往復。前進・RAR_TEST によるデータの受け取り・RAR_SKIP による通過 (照合つき)、終端の後の前進は無い。
    [Fact]
    public void U53_Session_RoundTrip()
    {
        var big = new byte[5 * 1024 * 1024 + 3];
        new Random(53).NextBytes(big);
        var path = WriteRar(Rar5Writer.Build(
        [
            new Rar5File { Name = "a.txt", Data = Hello },
            new Rar5File { Name = "d", IsDirectory = true, Attributes = 0x10 },
            new Rar5File { Name = "skipped.bin", Data = big },
            new Rar5File { Name = "big.bin", Data = big },
            new Rar5File { Name = "empty.txt" },
        ]));
        using var source = OpenSource(path);
        var opened = source.OpenSession();
        Assert.Null(opened.Fatal);
        using var session = opened.Session!;

        Assert.True(session.Advance(0).Succeeded);
        var first = new List<byte>();
        Assert.Equal(RarTestResult.Success, session.GetContent(0).Test(chunk => { first.AddRange(chunk.ToArray()); return true; }));
        Assert.Equal(Hello, first);

        // d と skipped.bin は RAR_SKIP で通過する。big.bin は複数のチャンクで届く。
        Assert.Equal(RarAdvanceResult.Ok(3), session.Advance(3));
        var content = session.GetContent(3);
        Assert.Equal(big.LongLength, content.Length);
        Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(big), content.ExpectedCrc32);
        using var received = new MemoryStream();
        var chunks = 0;
        Assert.Equal(RarTestResult.Success, content.Test(chunk => { chunks++; received.Write(chunk); return true; }));
        Assert.Equal(big, received.ToArray());
        Assert.True(chunks >= 1);

        Assert.True(session.Advance(4).Succeeded);
        var calls = 0;
        Assert.Equal(RarTestResult.Success, session.GetContent(4).Test(_ => { calls++; return true; }));
        Assert.Equal(0, calls);
    }

    // RAR4 でも往復できる。
    [Fact]
    public void U53_Session_RoundTrip_Rar4()
    {
        var path = WriteRar(Rar4Writer.Build(
        [
            new Rar4File { Name = "a.txt", Data = Hello },
            new Rar4File { Name = "b.txt", Data = "bravo"u8.ToArray() },
        ]));
        using var source = OpenSource(path);
        using var session = source.OpenSession().Session!;

        Assert.True(session.Advance(1).Succeeded);
        var received = new List<byte>();
        Assert.Equal(RarTestResult.Success, session.GetContent(1).Test(chunk => { received.AddRange(chunk.ToArray()); return true; }));
        Assert.Equal("bravo"u8.ToArray(), received);
    }

    // U54: 中止 (sink が false) の後、RAR_TEST は失敗を返し、以後の前進は製品の誤配線として例外。セッションの close は成功する (docs/RATIONALE.md#rar-dll-usage)。
    [Fact]
    public void U54_AbortedTest_ThenCloseSucceeds()
    {
        var path = WriteRar(Rar5Writer.Build(
        [
            new Rar5File { Name = "a.txt", Data = Hello },
            new Rar5File { Name = "b.txt", Data = Hello },
        ]));
        using var source = OpenSource(path);
        var session = source.OpenSession().Session!;

        Assert.True(session.Advance(0).Succeeded);
        var result = session.GetContent(0).Test(_ => false);

        Assert.False(result.Succeeded);
        Assert.Throws<InvalidOperationException>(() => session.Advance(1));
        session.Dispose();
        session.Dispose();
    }

    // コールバックの中の例外はネイティブの境界を越えず (プロセスは終了しない)、RAR_TEST から戻った後に元の例外として再送出される。
    [Fact]
    public void U54_ExceptionInCallback_IsRethrownAfterNativeCall()
    {
        var path = WriteRar(Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = Hello }]));
        using var source = OpenSource(path);
        using var session = source.OpenSession().Session!;
        var thrown = new InvalidOperationException("from sink");

        Assert.True(session.Advance(0).Succeeded);
        var caught = Assert.Throws<InvalidOperationException>(() => session.GetContent(0).Test(_ => throw thrown));

        Assert.Same(thrown, caught);
    }

    // DLL のハッシュ照合: データの CRC32 が不一致なら、全データを受け取った後で RAR_TEST が失敗を返す (判定は RAR_TEST の成功後に確定する)。
    [Fact]
    public void U54_DataCrcMismatch_FailsAfterAllData()
    {
        var path = WriteRar(Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = Hello, Crc = 0xDEADBEEF }]));
        using var source = OpenSource(path);
        using var session = source.OpenSession().Session!;

        Assert.True(session.Advance(0).Succeeded);
        var received = new List<byte>();
        var result = session.GetContent(0).Test(chunk => { received.AddRange(chunk.ToArray()); return true; });

        Assert.Equal(Hello, received);
        Assert.False(result.Succeeded);
    }

    // ソース・セッションの Dispose でアーカイブを手放す (ほかのプロセスが書き込みのために開ける)。
    [Fact]
    public void U54_DisposeReleasesArchive()
    {
        var path = WriteRar(Rar5Writer.Build([new Rar5File { Name = "a.txt", Data = Hello }]));
        var source = OpenSource(path);
        var session = source.OpenSession().Session!;
        Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());

        session.Dispose();
        source.Dispose();

        new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
    }
}
