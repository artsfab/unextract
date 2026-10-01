using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Unextract.Windows;
using Unextract.Windows.Tests;

namespace Unextract.E2E.Tests;

// E2E の作業ディレクトリ。テストの出力先の fixtures/<テスト名>-<GUID>/ に毎回ユニークな名前で作り、その中に archive.zip と
// target/ を置く。テストからは削除しない (掃除は scripts/clean-test-fixtures.ps1)。junction・ACL・symlink は使わない。
public sealed class E2EFixture
{
    private E2EFixture(string directory)
    {
        Directory = directory;
        Target = System.IO.Directory.CreateDirectory(Path.Combine(directory, "target")).FullName;
        ArchivePath = Path.Combine(directory, "archive.zip");
    }

    public string Directory { get; }

    public string Target { get; }

    public string ArchivePath { get; }

    public static E2EFixture Create([CallerMemberName] string testName = "")
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", $"{testName}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(path);
        return new E2EFixture(path);
    }

    // FileMode.CreateNew: 既存のファイルを上書きしない。
    public E2EFixture WriteZip(byte[] zip)
    {
        using var stream = new FileStream(ArchivePath, FileMode.CreateNew);
        stream.Write(zip);
        return this;
    }

    public E2EFixture WriteTarget(string relativePath, string content) => WriteTarget(relativePath, Bytes(content));

    public E2EFixture WriteTarget(string relativePath, byte[] content)
    {
        var path = Path.Combine(Target, relativePath.Replace('/', '\\'));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew);
        stream.Write(content);
        return this;
    }

    public E2EFixture CreateTargetDirectory(string relativePath)
    {
        System.IO.Directory.CreateDirectory(Path.Combine(Target, relativePath.Replace('/', '\\')));
        return this;
    }

    public ProcessResult Run(string? stdin, params string[] options) =>
        UnextractProcess.Run(Directory, stdin, [ArchivePath, "--target", Target, .. options]);

    // 実削除を伴う実行 (--yes) の前の領域外ガード (テスト側の安全装置。製品の安全装置の代わりにしない)。
    // fixture を fixtures/<一意名>/ の直下に限り (DeletionGuard のコンストラクタ)、target と target 内の全ディレクトリについて、
    // 確認用ハンドル (reparse をたどらない) から得た最終パスが fixture の内側であることと、fixture からその項目までの各成分が
    // reparse point でないことを確かめる。違反なら GuardViolationException で中止し、exe を起動しない。
    public ProcessResult RunDeleting(string? stdin, params string[] options)
    {
        var guard = new DeletionGuard(Directory);
        guard.Check(FinalPath(Target));
        foreach (var directory in System.IO.Directory.EnumerateDirectories(Target, "*", SearchOption.AllDirectories))
        {
            guard.Check(FinalPath(directory));
        }

        Assert.Empty(guard.Violations);
        return Run(stdin, options);
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // ディレクトリ配下の全項目の (相対パス → 種類・サイズ・SHA-256・更新日時)。
    public static SortedDictionary<string, string> Snapshot(string directory)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            result[Path.GetRelativePath(directory, path)] = Describe(path);
        }

        return result;
    }

    // ファイル1つの種類・サイズ・SHA-256・更新日時 (ZIP の不変の確認用)。
    public static string Describe(string path)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.Directory) != 0)
        {
            return $"dir {info.LastWriteTimeUtc.Ticks}";
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return $"file {info.Length} {hash} {info.LastWriteTimeUtc.Ticks}";
    }

    private static string FinalPath(string path)
    {
        var opened = HandleOpener.OpenForConfirmation(path);
        if (!opened.Succeeded)
        {
            throw new GuardViolationException($"確認のために開けない: {path} ({opened})");
        }

        using var handle = opened.Value!;
        var finalPath = FileInformation.GetFinalPath(handle);
        return finalPath.Succeeded ? finalPath.Value! : throw new GuardViolationException($"最終パスを取得できない: {path}");
    }
}
