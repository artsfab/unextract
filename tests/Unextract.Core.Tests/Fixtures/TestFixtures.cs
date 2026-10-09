using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Unextract.Core.Tests.Fixtures;
using Xunit.Sdk;

// Every test project that links this file deletes the fixtures of each test after the test (passed or failed).
[assembly: FixtureCleanup]

namespace Unextract.Core.Tests.Fixtures;

// Fixtures on disk: <test output>\fixtures\<test name>-<32 hex digits>, one directory per Create call, registered in the
// owner of the current test. The owner deletes exactly the directories it registered, after the test (FixtureCleanup,
// passed or failed), or, for resources living longer than a test method (UI E2E sessions, class fixtures), when its
// explicit holder ends. Shared with the other test projects through <Compile Include ... Link>.
//
// Deletion (TestFixtures.Delete) is the only place where tests remove fixtures:
// 1. Ownership: only a registered absolute path; directly under the owner's fixtures root; named <prefix>-<32 hex>; the
//    path itself, the fixtures root and every existing component above it are ordinary directories (no reparse point),
//    re-checked right before deleting. The fixtures root itself is never deleted.
// 2. A walk that never enters reparse points clears ReadOnly on ordinary entries; links are removed as links only.
// 3. Directory.Delete(recursive), which does not follow reparse points. ACLs are never changed.
// 4. Transient sharing violations (antivirus, indexer) are retried for up to 2 seconds in total.
// 5. The path must be gone afterwards (access denied is not "gone"). Otherwise the test fails with the path and the cause;
//    the other owned paths are still attempted, and a failure of the test body is kept (xUnit aggregates both).
// A test host that ends abnormally cannot run the cleanup; scripts/clean-test-fixtures.ps1 collects what is left.
public sealed class FixtureOwner
{
    private readonly object _lock = new();
    private readonly List<string> _owned = [];
    private readonly string _prefix;

    // root: the fixtures root (default <test output>\fixtures). Safety tests pass a mock root, never the shared one.
    public FixtureOwner(string name, string? root = null)
    {
        _prefix = TestFixtures.Prefix(name);
        Root = Path.GetFullPath(root ?? Path.Combine(AppContext.BaseDirectory, "fixtures")).TrimEnd('\\');
    }

    public string Root { get; }

    public IReadOnlyList<string> Owned
    {
        get { lock (_lock) return [.. _owned]; }
    }

    // Creates and registers a new fixture directory. Existing components are checked first; missing ones are created
    // below checked parents, and the result is checked again.
    public string Create()
    {
        TestFixtures.CheckAncestors(Root, create: true);
        string path = Path.Combine(Root, $"{_prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        TestFixtures.CheckOrdinaryDirectory(path);
        lock (_lock) _owned.Add(path);
        return path;
    }

    // Deletes every registered path. Throws (after trying all of them) when any is left.
    public void Cleanup()
    {
        string[] owned;
        lock (_lock)
        {
            owned = [.. _owned];
            _owned.Clear();
        }
        var failures = new List<Exception>();
        foreach (string path in owned)
        {
            try { TestFixtures.Delete(Root, path, _prefix); }
            catch (Exception e) { failures.Add(new IOException($"fixture を削除できない (scripts/clean-test-fixtures.ps1 で回収する): {path}: {e.Message}", e)); }
        }
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1) throw new AggregateException("fixture を削除できない", failures);
    }
}

// Before: an owner for the test (AsyncLocal, so it reaches the test body, including STA threads started by it).
// After: the owner deletes what it registered, whether the test passed or failed.
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class FixtureCleanupAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) => TestFixtures.Current = new FixtureOwner(methodUnderTest.Name);

    public override void After(MethodInfo methodUnderTest)
    {
        var owner = TestFixtures.Current;
        TestFixtures.Current = null;
        owner?.Cleanup();
    }
}

public static partial class TestFixtures
{
    private static readonly AsyncLocal<FixtureOwner?> CurrentOwner = new();
    private static readonly TimeSpan RetryLimit = TimeSpan.FromSeconds(2);

    // The owner of the running test (set by FixtureCleanup).
    public static FixtureOwner? Current
    {
        get => CurrentOwner.Value;
        internal set => CurrentOwner.Value = value;
    }

    // A new fixture directory owned by the running test. Without an owner (outside a test method, e.g. in a class fixture)
    // this fails: such resources pass an explicit FixtureOwner instead.
    public static string Create() =>
        (Current ?? throw new InvalidOperationException("fixture の所有者がいない (テストメソッドの外。明示的な FixtureOwner を使う)")).Create();

    internal static string Prefix(string name)
    {
        string prefix = UnsafeName().Replace(name, "_");
        if (prefix.Length == 0) prefix = "fixture";
        return prefix.Length > 60 ? prefix[..60] : prefix;
    }

    [GeneratedRegex("[^A-Za-z0-9_.]")]
    private static partial Regex UnsafeName();

    // Every existing component from the drive root down to `path` must be an ordinary directory (not a reparse point).
    // With create, missing components are created one by one below a checked parent.
    internal static void CheckAncestors(string path, bool create)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\');
        var components = new Stack<string>();
        for (string? current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) components.Push(current);
        while (components.Count > 0)
        {
            string current = components.Pop();
            if (!Exists(current))
            {
                if (!create) throw new IOException($"fixture の経路が無い: {current}");
                Directory.CreateDirectory(current);
            }
            CheckOrdinaryDirectory(current);
        }
    }

    internal static void CheckOrdinaryDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"fixture の経路が reparse point: {path}");
        if ((attributes & FileAttributes.Directory) == 0) throw new IOException($"fixture の経路がディレクトリでない: {path}");
    }

    // True when the name exists. GetAttributes does not follow reparse points, so a dangling link exists. Access denied
    // and other errors are not "absent": they propagate.
    internal static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    // The single deletion routine (see FixtureOwner).
    public static void Delete(string root, string path, string prefix)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd('\\');
        string full = Path.GetFullPath(path).TrimEnd('\\');
        string name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), fullRoot, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix + "-", StringComparison.Ordinal) ||
            !Regex.IsMatch(name[(prefix.Length + 1)..], "^[0-9a-f]{32}$"))
        {
            throw new InvalidOperationException($"所有を確認できない fixture は削除しない: {full}");
        }

        CheckAncestors(fullRoot, create: false);
        if (!Exists(full)) return;
        CheckOrdinaryDirectory(full);
        ClearReadOnly(full);

        var deadline = DateTime.UtcNow + RetryLimit;
        while (true)
        {
            try
            {
                Directory.Delete(full, recursive: true);
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
            catch (IOException e) when (IsSharingViolation(e) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }

        if (Exists(full)) throw new IOException($"削除後も残っている: {full}");
    }

    private static bool IsSharingViolation(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;

    // Clears ReadOnly on ordinary entries without entering reparse points. Links are removed as links (their targets are
    // never touched); Directory.Delete would remove them the same way, but a read-only link would stop it.
    private static void ClearReadOnly(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(child, recursive: false);
                    else File.Delete(child);
                    continue;
                }
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(child, attributes & ~FileAttributes.ReadOnly);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
            }
        }
    }
}
