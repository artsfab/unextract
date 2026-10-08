using System.Security.Cryptography;
using Unextract.Core.Results;
using Unextract.Windows.Rar;

namespace Unextract.Windows.Tests.Rar;

// テスト U50: UnRAR.dll の照合とロード (docs/spec/rar.md#pinning) のうち、採用版の DLL を使わずに作れる原因。
// 原因ごとに新しいローダーのインスタンスを使い、実行順に依存しない。版の不一致 (RARGetDllVersion) と成功時の版は採用版の DLL が要る (RealUnrarTests の U51)。
public class UnrarLibraryTests
{
    [Fact]
    public void U50_NotFound()
    {
        var path = Path.Combine(TestFixture.CreateDirectory(), UnrarLibrary.FileName);

        var result = new UnrarLibrary(path).Load();

        Assert.Null(result.Api);
        Assert.Equal(
            $"UnRAR.dll を使用できないため、RAR を処理できません (見つかりません)。{path} に UnRAR.dll 7.23 (x64) の UnRAR64.dll を置いてください。ZIP の処理には影響しません。",
            result.Fatal!.Describe());
    }

    [Fact]
    public void U50_FolderMissing_IsNotFound()
    {
        var path = Path.Combine(TestFixture.CreateDirectory(), "missing", UnrarLibrary.FileName);

        Assert.Contains("(見つかりません)", new UnrarLibrary(path).Load().Fatal!.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void U50_HashMismatch_DoesNotLoad()
    {
        var path = Path.Combine(TestFixture.CreateDirectory(), UnrarLibrary.FileName);
        File.WriteAllBytes(path, [0x4D, 0x5A, 1, 2, 3]);

        var result = new UnrarLibrary(path).Load();

        Assert.Equal(FatalKind.RarLibraryUnavailable, result.Fatal!.Kind);
        Assert.Equal("版が一致しません (SHA-256)", result.Fatal.Detail);
        Assert.Null(result.Fatal.Win32Error);
    }

    // 照合を通る自作の非 PE ファイル (期待値をそのハッシュに差し替える) はロードに失敗する。Win32 の説明とコードを保持する。
    [Fact]
    public void U50_LoadFailure_KeepsWin32Error()
    {
        var path = Path.Combine(TestFixture.CreateDirectory(), UnrarLibrary.FileName);
        byte[] content = [.. "not a portable executable"u8];
        File.WriteAllBytes(path, content);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));

        var result = new UnrarLibrary(path, hash, 10).Load();

        var fatal = result.Fatal!;
        Assert.Equal(FatalKind.RarLibraryUnavailable, fatal.Kind);
        Assert.Equal(193, fatal.Win32Error);
        Assert.StartsWith("読み込めません (", fatal.Detail, StringComparison.Ordinal);
        Assert.Equal($"読み込めません ({new System.ComponentModel.Win32Exception(193).Message})", fatal.Detail);
    }

    // 照合のために開いたファイルは、結果にかかわらず閉じている。結果はインスタンスが保持し、ロードは1回だけ。
    [Fact]
    public void U50_ResultIsCachedPerInstance_AndFileIsReleased()
    {
        var path = Path.Combine(TestFixture.CreateDirectory(), UnrarLibrary.FileName);
        File.WriteAllBytes(path, [1, 2, 3]);
        var library = new UnrarLibrary(path);

        var first = library.Load();
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        File.WriteAllBytes(path, [4, 5, 6]);
        Assert.Same(first, library.Load());
    }

    [Fact]
    public void U50_RelativePath_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new UnrarLibrary(UnrarLibrary.FileName));
    }
}
