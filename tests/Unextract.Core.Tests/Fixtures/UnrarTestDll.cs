using System.IO;

namespace Unextract.Core.Tests.Fixtures;

// 採用版の UnRAR64.dll (UnRAR.dll 7.23 x64) を使うテストのための DLL の場所 (docs/TESTING.md#rar)。
// DLL はリポジトリにも製品の配布物にも含めない (docs/spec/rar.md#pinning)。開発機・CI では scripts/get-unrar-dll.ps1 で
// リポジトリ外に用意し、環境変数 UNEXTRACT_TEST_UNRAR_DLL (DLL の絶対パス) か、未設定なら既定の場所から読む。
// 製品は環境変数を読まない (読み込み元は実行中の exe と同じフォルダーだけ)。ここはテストが DLL を見つけるためだけに使う。
// 見つからない場合は例外でテストを失敗にする (Skip・前提不成立にしない。CLAUDE.md の開発規則)。版の照合は製品のローダーが行う。
internal static class UnrarTestDll
{
    public const string EnvironmentVariable = "UNEXTRACT_TEST_UNRAR_DLL";

    public const string FileName = "UnRAR64.dll";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "unextract-dev", "unrar-7.23", FileName);

    public static string Location
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
            var path = string.IsNullOrEmpty(configured) ? DefaultPath : configured;
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"採用版の UnRAR64.dll が見つかりません ({path})。scripts\\get-unrar-dll.ps1 で用意するか、{EnvironmentVariable} に絶対パスを設定してください。");
            }

            return path;
        }
    }

    // directory に UnRAR64.dll の複製を置く (exe と同じフォルダーに置く製品の配置を、テストが作った場所で再現する)。既存のファイルは上書きしない。
    public static string CopyTo(string directory)
    {
        var destination = Path.Combine(directory, FileName);
        File.Copy(Location, destination, overwrite: false);
        return destination;
    }
}
