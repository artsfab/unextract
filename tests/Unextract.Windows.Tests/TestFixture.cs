using System.Diagnostics;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.Windows.Tests;

// 実 NTFS 上の作業ディレクトリ。テストの出力先 (bin/.../fixtures/) の下に <テスト名>-<GUID> で作る (TestFixtures)。
// 並列実行でも衝突しない。テストの終了後に、成功・失敗を問わず共通の削除処理が削除する。
internal static class TestFixture
{
    public static string CreateDirectory() => TestFixtures.Create();

    public static string WriteFile(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    // cmd.exe の内部コマンドを実行する (mklink など)。終了コードと標準出力を返す。
    public static (int ExitCode, string Output) Cmd(string arguments)
    {
        // cmd は \" のエスケープを解釈しないため、/s /c "..." で外側の引用符だけを外させる。
        var info = new ProcessStartInfo("cmd.exe", $"/s /c \"{arguments}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    // mklink /J で、同じ fixture ディレクトリ内の別ディレクトリを指す junction を作る。
    public static void CreateJunction(string link, string target)
    {
        var (exitCode, output) = Cmd($"mklink /J \"{link}\" \"{target}\"");
        Assert.True(exitCode == 0, $"mklink /J failed: {output}");
    }

    // mklink /H で hardlink を作る (特権は不要)。
    public static void CreateHardLink(string link, string target)
    {
        var (exitCode, output) = Cmd($"mklink /H \"{link}\" \"{target}\"");
        Assert.True(exitCode == 0, $"mklink /H failed: {output}");
    }

    // 8.3 の短いパス。短い名前が無ければ元のパスがそのまま返る。
    public static string ShortPath(string path)
    {
        var (exitCode, output) = Cmd($"for %I in (\"{path}\") do @echo %~sI");
        Assert.Equal(0, exitCode);
        return output.Trim();
    }

    public static int Win32Code(IOException exception) => exception.HResult & 0xFFFF;
}
