using Unextract.Core.Tests.Fixtures;
using Xunit.Abstractions;
using static Unextract.Windows.Tests.TestFixture;

namespace Unextract.Windows.Tests;

// The shared fixture deletion (TestFixtures / FixtureOwner) on real NTFS. Every case works in a mock output directory inside
// this test's own fixture (never the shared fixtures root): <own fixture>\out\fixtures is the mock root.
public sealed class TestFixturesTests(ITestOutputHelper output)
{
    private static (string Own, string Root, FixtureOwner Owner) MockOwner(string name = "Mock")
    {
        string own = CreateDirectory();
        string root = Path.Combine(own, "out", "fixtures");
        return (own, root, new FixtureOwner(name, root));
    }

    [Fact]
    public void OwnerIsAvailableInTheTestBodyAndOnOtherThreads()
    {
        Assert.NotNull(TestFixtures.Current);
        FixtureOwner? seen = null;
        var thread = new Thread(() => seen = TestFixtures.Current);
        thread.Start();
        thread.Join();
        Assert.Same(TestFixtures.Current, seen);
        Assert.StartsWith(nameof(OwnerIsAvailableInTheTestBodyAndOnOtherThreads) + "-", Path.GetFileName(CreateDirectory()), StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupDeletesTheOwnedDirectoriesWithAttributesLongPathsAndInvisibleNames()
    {
        var (_, root, owner) = MockOwner();
        string a = owner.Create();
        string b = owner.Create();
        string readOnly = WriteFile(a, "read-only.txt", "r");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var readOnlyDirectory = Directory.CreateDirectory(Path.Combine(a, "ro-dir"));
        WriteFile(readOnlyDirectory.FullName, "inner.txt", "i");
        readOnlyDirectory.Attributes |= FileAttributes.ReadOnly;
        string deep = b;
        while (deep.Length < 300) deep = Directory.CreateDirectory(Path.Combine(deep, new string('d', 40))).FullName;
        WriteFile(deep, "long.txt", "l");
        // Invisible characters, built at run time (never written as literal characters in the source).
        string invisible = "name" + (char)0x202E + "txt." + (char)0x200B + "exe";
        WriteFile(a, invisible, "x");

        owner.Cleanup();

        Assert.False(Directory.Exists(a));
        Assert.False(Directory.Exists(b));
        Assert.True(Directory.Exists(root)); // the fixtures root itself stays
        Assert.Empty(owner.Owned);
    }

    [Fact]
    public void JunctionInsideAFixtureIsRemovedWithoutTouchingItsTarget()
    {
        var (own, _, owner) = MockOwner();
        string marker = Directory.CreateDirectory(Path.Combine(own, "outside-marker")).FullName;
        WriteFile(marker, "keep.txt", "keep");
        string fixture = owner.Create();
        CreateJunction(Path.Combine(fixture, "link"), marker);

        owner.Cleanup();

        Assert.False(Directory.Exists(fixture));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(marker, "keep.txt")));
    }

    [Fact]
    public void UnregisteredPathsTheRootAndAReparseFixtureAreNotDeleted()
    {
        var (own, root, owner) = MockOwner();
        string fixture = owner.Create();
        string prefix = Path.GetFileName(fixture)[..^33];
        string other = Directory.CreateDirectory(Path.Combine(root, "Other-" + Guid.NewGuid().ToString("N"))).FullName;
        string badName = Directory.CreateDirectory(Path.Combine(root, prefix + "-not-a-guid")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(fixture, $"{prefix}-{Guid.NewGuid():N}")).FullName;

        Assert.Throws<InvalidOperationException>(() => TestFixtures.Delete(root, other, prefix));
        Assert.Throws<InvalidOperationException>(() => TestFixtures.Delete(root, badName, prefix));
        Assert.Throws<InvalidOperationException>(() => TestFixtures.Delete(root, root, prefix));
        Assert.Throws<InvalidOperationException>(() => TestFixtures.Delete(root, nested, prefix));
        Assert.True(Directory.Exists(other) && Directory.Exists(badName) && Directory.Exists(nested));

        // A fixture name that is itself a junction is refused; the target keeps its content.
        string marker = Directory.CreateDirectory(Path.Combine(own, "marker")).FullName;
        WriteFile(marker, "keep.txt", "keep");
        string linked = Path.Combine(root, $"{prefix}-{Guid.NewGuid():N}");
        CreateJunction(linked, marker);
        Assert.Throws<IOException>(() => TestFixtures.Delete(root, linked, prefix));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(marker, "keep.txt")));
        Directory.Delete(linked); // the test's own junction (the link only)

        owner.Cleanup();
        Assert.False(Directory.Exists(fixture));
    }

    // The fixtures root or a component above it is a junction: creating and deleting are refused; the target is unchanged.
    [Theory]
    [InlineData("root")]
    [InlineData("ancestor")]
    public void JunctionOnThePathIsRefused(string where)
    {
        string own = CreateDirectory();
        string real = Directory.CreateDirectory(Path.Combine(own, "real", "fixtures")).FullName;
        string link = Path.Combine(own, "linked");
        string root;
        if (where == "root")
        {
            Directory.CreateDirectory(link);
            CreateJunction(Path.Combine(link, "fixtures"), real);
            root = Path.Combine(link, "fixtures");
        }
        else
        {
            CreateJunction(link, Path.Combine(own, "real"));
            root = Path.Combine(link, "fixtures");
        }
        string victim = Directory.CreateDirectory(Path.Combine(real, $"Mock-{Guid.NewGuid():N}")).FullName;
        WriteFile(victim, "keep.txt", "keep");

        Assert.Throws<IOException>(() => new FixtureOwner("Mock", root).Create());
        Assert.Throws<IOException>(() => TestFixtures.Delete(root, Path.Combine(root, Path.GetFileName(victim)), "Mock"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(victim, "keep.txt")));
        Assert.Equal([victim], Directory.GetDirectories(real));

        Directory.Delete(where == "root" ? Path.Combine(link, "fixtures") : link); // the test's own junction (the link only)
    }

    // A DENY left on a fixture makes the deletion fail with the path; the owner still tries the other paths.
    [Fact]
    public void LeftDenyFailsTheCleanupAndTheOtherPathsAreStillDeleted()
    {
        var (_, _, owner) = MockOwner();
        string denied = owner.Create();
        string inner = Directory.CreateDirectory(Path.Combine(denied, "inner")).FullName;
        WriteFile(inner, "a.txt", "a");
        string other = owner.Create();
        AclChanges.Run(output, acl =>
        {
            // inner can be deleted neither by itself (DELETE) nor through its parent (DELETE_CHILD).
            acl.Deny(denied, "DC");
            acl.Deny(inner, "DE");
            var error = Assert.Throws<IOException>(owner.Cleanup);
            Assert.Contains(denied, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(inner));
            Assert.False(Directory.Exists(other));
        });
        TestFixtures.Delete(Path.GetDirectoryName(denied)!, denied, "Mock");
        Assert.False(Directory.Exists(denied));
    }

    // Another owner's fixture in the same root is neither touched nor reported.
    [Fact]
    public void AnotherOwnersFixtureIsLeftAlone()
    {
        var (_, root, owner) = MockOwner("Mine");
        var otherOwner = new FixtureOwner("Theirs", root);
        string mine = owner.Create();
        string theirs = otherOwner.Create();

        owner.Cleanup();

        Assert.False(Directory.Exists(mine));
        Assert.True(Directory.Exists(theirs));
        otherOwner.Cleanup();
        Assert.False(Directory.Exists(theirs));
    }

    [Fact]
    public void CreateWithoutAnOwnerFails()
    {
        FixtureOwner? saved = TestFixtures.Current;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            // A thread started without the test's execution context has no owner.
            error = Record.Exception(TestFixtures.Create);
        });
        thread.UnsafeStart();
        thread.Join();
        Assert.IsType<InvalidOperationException>(error);
        Assert.Same(saved, TestFixtures.Current);
    }
}

// xUnit order: Before → test → After (the fixtures are deleted) → Dispose of the test class.
public sealed class FixtureCleanupOrderTests : IDisposable
{
    private string? _path;

    [Fact]
    public void FixtureIsDeletedAfterTheTestAndBeforeTheClassIsDisposed()
    {
        _path = CreateDirectory();
        Assert.True(Directory.Exists(_path));
    }

    public void Dispose()
    {
        if (_path is not null && Directory.Exists(_path)) throw new InvalidOperationException("The fixture still exists when the test class is disposed: " + _path);
    }
}
