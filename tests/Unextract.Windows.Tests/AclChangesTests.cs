using Xunit.Abstractions;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests;

// ACL を変えるテストの安全装置 (AclChanges) 自体の確認。DENY を戻せたことをテストの中で確かめ、残っていればテストを失敗にする。
public sealed class AclChangesTests(ITestOutputHelper output)
{
    [Fact]
    public void RestoredDenyLeavesNoDenyEntry()
    {
        var dir = CreateDirectory();
        var file = WriteFile(dir, "a.txt", "a");

        AclChanges.Run(output, acl =>
        {
            acl.Deny(file, "RD");
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(file));
        });

        Assert.Equal("a", File.ReadAllText(file));
    }

    // 登録していない継承の DENY を親に付けて、復元後も子に DENY が残る状態を作る。Run は失敗し、本体の例外も保持する。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemainingDenyFailsTheTestAndKeepsTheBodyFailure(bool bodyFails)
    {
        var dir = CreateDirectory();
        var parent = Directory.CreateDirectory(Path.Combine(dir, "parent")).FullName;
        var file = WriteFile(parent, "a.txt", "a");
        try
        {
            var error = Record.Exception(() => AclChanges.Run(output, acl =>
            {
                acl.Deny(file, "RD");
                var (exitCode, text) = Cmd($"icacls \"{parent}\" /deny *{AclChanges.CurrentUserSid}:(OI)(RD)");
                Assert.True(exitCode == 0, $"icacls /deny failed: {text}");
                if (bodyFails)
                {
                    throw new InvalidDataException("本体の失敗");
                }
            }));

            if (bodyFails)
            {
                var both = Assert.IsType<AggregateException>(error);
                Assert.IsType<InvalidDataException>(both.InnerExceptions[0]);
                Assert.Contains("DENY が残っています", both.InnerExceptions[1].Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("DENY が残っています", Assert.IsType<InvalidOperationException>(error).Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            var (exitCode, text) = Cmd($"icacls \"{parent}\" /remove:d *{AclChanges.CurrentUserSid}");
            Assert.True(exitCode == 0, $"icacls /remove:d failed: {text}");
        }

        Assert.Equal("a", File.ReadAllText(file));
    }
}
