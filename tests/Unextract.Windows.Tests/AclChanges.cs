using System.Security.Principal;
using Xunit.Abstractions;

namespace Unextract.Windows.Tests;

// テストが付けた ACL の DENY (自分の SID) を記録し、Dispose で除去して元に戻す (icacls /remove:d)。
// 戻すのは ACL だけで、ファイル・ディレクトリは削除しない。戻せなかった場合は、パスと理由をテスト出力に残す (テストの失敗にはしない)。
// 対象が既に存在しない (テストで削除された) 場合は戻す必要がないため記録だけする。
public sealed class AclChanges(ITestOutputHelper output) : IDisposable
{
    private readonly List<string> _denied = [];

    public static string CurrentUserSid => WindowsIdentity.GetCurrent().User!.Value;

    // rights は icacls の簡易権利 (RD = READ_DATA、DE = DELETE、DC = DELETE_CHILD など)。
    public void Deny(string path, string rights)
    {
        _denied.Add(path);
        var (exitCode, text) = TestFixture.Cmd($"icacls \"{path}\" /deny *{CurrentUserSid}:({rights})");
        Assert.True(exitCode == 0, $"icacls /deny failed: {text}");
    }

    public void Dispose()
    {
        foreach (var path in Enumerable.Reverse(_denied).Distinct())
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                output.WriteLine($"ACL: 対象が存在しないため戻す必要なし: {path}");
                continue;
            }

            var (exitCode, text) = TestFixture.Cmd($"icacls \"{path}\" /remove:d *{CurrentUserSid}");
            if (exitCode != 0)
            {
                output.WriteLine($"ACL を戻せませんでした: {path}: icacls 終了コード {exitCode}: {text.Trim()}");
            }
        }
    }
}
