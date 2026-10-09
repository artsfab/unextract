using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit.Abstractions;

namespace Unextract.Windows.Tests;

// テストが付けた ACL の DENY (自分の SID) を記録し、終了時に除去して元に戻す (icacls /remove:d)。
// 戻すのは ACL だけで、ファイル・ディレクトリは削除しない。除去の後に、自分の SID の DENY が残っていないことを .NET の ACL 読み取り API で確かめる。
// 除去に失敗した場合や DENY が残っていた場合はテストを失敗にする。Run を使うと、テスト本体の例外も失わずに両方を報告する。
// 対象が既に存在しない (テストで削除された) 場合は戻す必要がないため記録だけする。
public sealed class AclChanges
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _denied = [];
    private bool _restored;

    private AclChanges(ITestOutputHelper output) => _output = output;

    public static string CurrentUserSid => WindowsIdentity.GetCurrent().User!.Value;

    // body を実行し、成功・失敗を問わず ACL を戻して確かめる。本体と復元の両方が失敗したら、両方の例外を AggregateException で報告する。
    public static void Run(ITestOutputHelper output, Action<AclChanges> body) => Run(output, acl =>
    {
        body(acl);
        return 0;
    });

    public static T Run<T>(ITestOutputHelper output, Func<AclChanges, T> body)
    {
        var acl = new AclChanges(output);
        T result;
        try
        {
            result = body(acl);
        }
        catch (Exception failure)
        {
            var restore = acl.Restore();
            if (restore is not null)
            {
                throw new AggregateException("テスト本体と ACL の復元の両方が失敗しました", failure, restore);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }

        if (acl.Restore() is { } error)
        {
            throw error;
        }

        return result;
    }

    // rights は icacls の簡易権利 (RD = READ_DATA、DE = DELETE、DC = DELETE_CHILD など)。
    public void Deny(string path, string rights)
    {
        _denied.Add(path);
        var (exitCode, text) = TestFixture.Cmd($"icacls \"{path}\" /deny *{CurrentUserSid}:({rights})");
        Assert.True(exitCode == 0, $"icacls /deny failed: {text}");
    }

    private Exception? Restore()
    {
        if (_restored)
        {
            return null;
        }

        _restored = true;
        var sid = new SecurityIdentifier(CurrentUserSid);
        var problems = new List<string>();
        foreach (var path in Enumerable.Reverse(_denied).Distinct())
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                _output.WriteLine($"ACL: 対象が存在しないため戻す必要なし: {path}");
                continue;
            }

            var (exitCode, text) = TestFixture.Cmd($"icacls \"{path}\" /remove:d *{CurrentUserSid}");
            if (exitCode != 0)
            {
                problems.Add($"ACL を戻せませんでした: {path}: icacls 終了コード {exitCode}: {text.Trim()}");
                continue;
            }

            try
            {
                FileSystemSecurity security = Directory.Exists(path)
                    ? new DirectoryInfo(path).GetAccessControl()
                    : new FileInfo(path).GetAccessControl();
                var remaining = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                    .OfType<FileSystemAccessRule>()
                    .Where(rule => rule.AccessControlType == AccessControlType.Deny && sid.Equals(rule.IdentityReference))
                    .Select(rule => $"{rule.FileSystemRights}{(rule.IsInherited ? " (継承)" : "")}")
                    .ToList();
                if (remaining.Count > 0)
                {
                    problems.Add($"DENY が残っています: {path}: {string.Join(", ", remaining)}");
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                problems.Add($"ACL を読み取れず、DENY が無いことを確かめられません: {path}: {e.GetType().Name}: {e.Message}");
            }
        }

        foreach (var problem in problems)
        {
            _output.WriteLine(problem);
        }

        return problems.Count == 0 ? null : new InvalidOperationException(string.Join(Environment.NewLine, problems));
    }
}
