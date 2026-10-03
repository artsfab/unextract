using Unextract.Core.Results;
using static Unextract.Core.Tests.TestHelpers;

namespace Unextract.Core.Tests;

// docs/spec/zip.md#structure。ZIP 内部の構造衝突は target の実在状態と無関係に全体 FATAL
// (target に a/ がない場合の MISSING (テスト T02) とは別概念。この段階の入力は target を持たない)。
public class StructureTests
{
    // テスト Z03
    [Theory]
    [InlineData(new[] { "a.txt", "a.txt" }, FatalKind.DuplicateEntry, 1)]
    [InlineData(new[] { "d/a.txt", "d\\a.txt" }, FatalKind.DuplicateEntry, 1)]
    [InlineData(new[] { "d/", "d/" }, FatalKind.DuplicateEntry, 1)]
    [InlineData(new[] { "d/", "d\\" }, FatalKind.DuplicateEntry, 1)]
    [InlineData(new[] { "a.txt", "A.txt" }, FatalKind.CaseInsensitiveCollision, 1)]
    [InlineData(new[] { "d/", "D/" }, FatalKind.CaseInsensitiveCollision, 1)]
    [InlineData(new[] { "d/x.txt", "D/y.txt" }, FatalKind.CaseInsensitiveCollision, 1)]   // 暗黙ディレクトリ同士
    [InlineData(new[] { "d/", "D/y.txt" }, FatalKind.CaseInsensitiveCollision, 1)]        // 明示と暗黙
    [InlineData(new[] { "a", "A/b.txt" }, FatalKind.CaseInsensitiveCollision, 1)]
    [InlineData(new[] { "a", "a/" }, FatalKind.FileDirectoryConflict, 1)]
    [InlineData(new[] { "a/", "a" }, FatalKind.FileDirectoryConflict, 1)]
    [InlineData(new[] { "a", "a/b.txt" }, FatalKind.FileUsedAsParent, 1)]
    [InlineData(new[] { "a/b.txt", "a" }, FatalKind.FileUsedAsParent, 1)]
    [InlineData(new[] { "x/a", "x/a/b/c.txt" }, FatalKind.FileUsedAsParent, 1)]
    [InlineData(new[] { "ok.txt", "a/b/c.txt", "a/b" }, FatalKind.FileUsedAsParent, 2)]
    public void Z03_StructureConflicts_AreFatal(string[] names, FatalKind expected, int entryIndex)
    {
        AssertFatal(ValidateNames(names), expected, entryIndex);
    }

    public static TheoryData<string[]> ConsistentStructures => new()
    {
        new string[] { "d/", "d/a.txt", "d/sub/b.txt" },
        new string[] { "d/a.txt", "d/" }, // 暗黙ディレクトリの後の明示ディレクトリ
        new string[] { "d/a.txt", "d/b.txt", "d/sub/" },
        new string[] { "a.txt", "a.txt.bak", "A.txt2" },
    };

    [Theory]
    [MemberData(nameof(ConsistentStructures))]
    public void ConsistentStructures_Pass(string[] names)
    {
        AssertPassed(ValidateNames(names), names.Length);
    }

    // 最初の FATAL で打ち切るが、どのエントリが FATAL になるかは ZIP 内の順序で決まる。
    [Fact]
    public void FirstFatal_IsDeterminedByZipOrder()
    {
        string[] names = ["a.txt", "bad\u0001.txt", "A.txt"];

        var first = ValidateNames(names);
        var again = ValidateNames(names);
        var reversed = ValidateNames([.. names.Reverse()]);

        AssertFatal(first, FatalKind.ControlCharacter, 1);
        Assert.Equal(first.Fatal, again.Fatal);
        AssertFatal(reversed, FatalKind.ControlCharacter, 1);

        AssertFatal(ValidateNames("A.txt", "a.txt", "bad\u0001.txt"), FatalKind.CaseInsensitiveCollision, 1);
    }
}
