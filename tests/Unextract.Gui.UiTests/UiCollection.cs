namespace Unextract.Gui.UiTests;

// Every UI test class joins this collection so that the user-profile guard wraps the whole run.
[CollectionDefinition("ui")]
public sealed class UiCollection : ICollectionFixture<UserProfileGuard>;

// The GUI under test must never touch the real %LOCALAPPDATA%\unextract (settings, logs). The data root override
// sends everything to a fixture; this guard only reads the real folder before and after the run to prove it.
public sealed class UserProfileGuard : IDisposable
{
    private readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "unextract");
    private readonly string[] _before;

    public UserProfileGuard() => _before = Snapshot();

    private string[] Snapshot()
    {
        if (!Directory.Exists(_folder)) return ["(absent)"];
        return Directory.EnumerateFileSystemEntries(_folder, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return $"{Path.GetRelativePath(_folder, path)}|{(info.Attributes.HasFlag(FileAttributes.Directory) ? "dir" : info.Length.ToString())}|{info.LastWriteTimeUtc:O}";
            })
            .Order(StringComparer.Ordinal)
            .Prepend("(present)")
            .ToArray();
    }

    public void Dispose()
    {
        // Compares only existence, names, sizes and write times; reads no file contents.
        var after = Snapshot();
        if (!_before.SequenceEqual(after))
            throw new InvalidOperationException($"{_folder} changed during the UI tests.\nBefore:\n{string.Join('\n', _before)}\nAfter:\n{string.Join('\n', after)}");
    }
}
