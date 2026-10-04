using System.IO.Compression;
using System.Text;

namespace Unextract.Gui.UiTests;

// Self-made ZIP fixtures; entry names ending in '/' are directory entries.
internal static class Files
{
    public static string Zip(string archive, params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            if (name.EndsWith('/')) continue;
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        return archive;
    }
}
