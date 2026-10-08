using Unextract.Core.Results;

namespace Unextract.Core.Tests;

// テスト U03: UnRAR.dll を利用できない4つの原因の説明 (docs/spec/cli.md#input-errors、docs/spec/rar.md#pinning)。
public class RarLibraryTextTests
{
    private const string Library = @"C:\app\cli\UnRAR64.dll";

    private const string Guide = @"。C:\app\cli\UnRAR64.dll に UnRAR.dll 7.23 (x64) の UnRAR64.dll を置いてください。ZIP の処理には影響しません。";

    [Fact]
    public void U03_NotFound()
    {
        var fatal = FatalError.RarLibraryUnavailable(RarLibraryFailure.NotFound, Library);

        Assert.Equal(FatalKind.RarLibraryUnavailable, fatal.Kind);
        Assert.Null(fatal.Entry);
        Assert.Equal("UnRAR.dll を使用できないため、RAR を処理できません (見つかりません)" + Guide, fatal.Describe());
    }

    [Fact]
    public void U03_HashMismatch()
    {
        var fatal = FatalError.RarLibraryUnavailable(RarLibraryFailure.HashMismatch, Library);

        Assert.Equal("UnRAR.dll を使用できないため、RAR を処理できません (版が一致しません (SHA-256))" + Guide, fatal.Describe());
    }

    [Fact]
    public void U03_LoadFailed_KeepsWin32Error()
    {
        var fatal = FatalError.RarLibraryUnavailable(RarLibraryFailure.LoadFailed, Library, "アクセスが拒否されました。", 5);

        Assert.Equal(5, fatal.Win32Error);
        Assert.Equal("UnRAR.dll を使用できないため、RAR を処理できません (読み込めません (アクセスが拒否されました。))" + Guide, fatal.Describe());
    }

    [Fact]
    public void U03_VersionMismatch()
    {
        var fatal = FatalError.RarLibraryUnavailable(RarLibraryFailure.VersionMismatch, Library, version: 9);

        Assert.Equal("UnRAR.dll を使用できないため、RAR を処理できません (版が一致しません (RARGetDllVersion=9))" + Guide, fatal.Describe());
    }

    [Fact]
    public void U03_LibraryPath_IsEscaped()
    {
        var fatal = FatalError.RarLibraryUnavailable(RarLibraryFailure.NotFound, "C:\\a\u0001\\UnRAR64.dll");

        Assert.DoesNotContain('\u0001', fatal.Describe());
    }
}
