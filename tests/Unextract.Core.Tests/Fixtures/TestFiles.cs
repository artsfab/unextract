namespace Unextract.Core.Tests.Fixtures;

// ディスク上の fixture はテストの出力ディレクトリ (bin/ 配下) の fixtures/ にだけ書く。
// 実行のたびに同じ名前で上書きし、テストからは削除しない。
internal static class TestFiles
{
    public static string Directory { get; } =
        System.IO.Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "fixtures")).FullName;

    public static string Write(string name, byte[] content)
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
