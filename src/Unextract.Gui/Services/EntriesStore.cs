using System.IO;
using System.Text;
using Unextract.Gui.Models;

namespace Unextract.Gui.Services;

// Ownership is created only by a successful CreateNew; nothing else is ever deleted through this type.
internal sealed class OwnedEntriesFile
{
    internal OwnedEntriesFile(string path) => Path = path;
    public string Path { get; }
}

internal sealed record EntriesCreation(OwnedEntriesFile? Owned, string? Error);

internal interface IEntriesStore
{
    EntriesCreation Create(IReadOnlyList<string> names);
    // Null on success. A failure is reported with the path and never changes the CLI result.
    string? Delete(OwnedEntriesFile file);
}

internal sealed class EntriesStore(string? directory = null) : IEntriesStore
{
    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);

    public EntriesCreation Create(IReadOnlyList<string> names)
    {
        string path = Path.Combine(directory ?? Path.GetTempPath(), $"unextract-entries-{Guid.NewGuid():N}.txt");
        FileStream stream;
        try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Nothing was created, so nothing is owned.
            return new(null, $"一時entriesファイルを作成できません: {path}: {e.Message}");
        }
        _owned.Add(path);
        var owned = new OwnedEntriesFile(path);
        try
        {
            using (stream)
            {
                using var buffered = new BufferedStream(stream, 64 * 1024);
                foreach (string name in names)
                {
                    buffered.Write(EntriesFormat.Encoding.GetBytes(name));
                    buffered.WriteByte((byte)'\n');
                }
                buffered.Flush();
                stream.Flush(true);
            }
            return new(owned, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            // Partially written, but it is ours: the caller cleans it up and does not start the CLI.
            return new(owned, $"一時entriesファイルを書き込めません: {path}: {e.Message}");
        }
    }

    public string? Delete(OwnedEntriesFile file)
    {
        if (!_owned.Contains(file.Path)) return $"このGUIが作成したファイルではないため削除しません: {file.Path}";
        try
        {
            // The only path-based delete in the GUI: our own newly created, uniquely named temporary entries file.
            File.Delete(file.Path);
            _owned.Remove(file.Path);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"一時entriesファイルを削除できません: {file.Path}: {e.Message}";
        }
    }
}
