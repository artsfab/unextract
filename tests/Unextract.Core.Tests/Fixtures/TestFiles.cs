namespace Unextract.Core.Tests.Fixtures;

// ディスク上の fixture は、実行中のテストが所有する fixture ディレクトリ (TestFixtures。テストの出力ディレクトリの
// fixtures/<テスト名>-<GUID>) にだけ書く。テストの終了後に共通の削除処理が削除する。
internal static class TestFiles
{
    // 実行中のテストの新しい fixture ディレクトリ (呼ぶたびに別のディレクトリ)。
    public static string NewDirectory() => TestFixtures.Create();

    public static string Write(string name, byte[] content)
    {
        var path = Path.Combine(NewDirectory(), name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
