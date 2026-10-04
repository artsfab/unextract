using System.IO;
using System.Security;
using Unextract.Gui.Models;

namespace Unextract.Gui.Services;

internal interface IArchiveSearch
{
    Task<ArchiveSearchResult> SearchAsync(string directory, bool recursive);
    Task<TargetObservation> ObserveTargetAsync(string path);
}

internal sealed class ArchiveSearch(IArchiveSearchFileSystem fileSystem) : IArchiveSearch
{
    public ArchiveSearch() : this(new ArchiveSearchFileSystem()) { }

    public Task<ArchiveSearchResult> SearchAsync(string directory, bool recursive) =>
        Task.Run(() => Search(directory, recursive));

    private ArchiveSearchResult Search(string root, bool recursive)
    {
        var archives = new List<ArchiveFile>();
        var diagnostics = new List<SearchDiagnostic>();
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out string? directory))
        {
            try
            {
                // Recheck at descent as well as when a child directory is discovered.
                var attributes = fileSystem.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(new(directory, "reparse pointのディレクトリには進入しません。"));
                    continue;
                }
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    diagnostics.Add(new(directory, "検索場所がディレクトリではありません。"));
                    continue;
                }
                // IgnoreInaccessible=false and AttributesToSkip=0: no silent omissions.
                foreach (string path in fileSystem.Enumerate(directory))
                {
                    try
                    {
                        attributes = fileSystem.GetAttributes(path);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (recursive) directories.Push(path);
                        }
                        else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                            archives.Add(fileSystem.ReadArchive(path));
                    }
                    catch (Exception e) when (IsInspectionFailure(e))
                    {
                        diagnostics.Add(new(path, e.Message));
                    }
                }
            }
            catch (Exception e) when (IsInspectionFailure(e))
            {
                // Already discovered siblings/children still get their own turn.
                diagnostics.Add(new(directory, e.Message));
            }
        }
        archives.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        diagnostics.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return new(archives.AsReadOnly(), diagnostics.AsReadOnly());
    }

    public Task<TargetObservation> ObserveTargetAsync(string path) => Task.Run(() =>
    {
        try
        {
            var attributes = fileSystem.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0
                ? new TargetObservation(TargetPresence.Present)
                : new TargetObservation(TargetPresence.Unknown, "ディレクトリではありません。正式な判定はCLIが行います。");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new TargetObservation(TargetPresence.Missing);
        }
        catch (Exception e) when (IsInspectionFailure(e))
        {
            return new TargetObservation(TargetPresence.Unknown, e.Message);
        }
    });

    private static bool IsInspectionFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}

// Internal adapter to inject search races/failures. Not another GUI external boundary.
internal interface IArchiveSearchFileSystem
{
    FileAttributes GetAttributes(string path);
    IEnumerable<string> Enumerate(string directory);
    ArchiveFile ReadArchive(string path);
}

internal sealed class ArchiveSearchFileSystem : IArchiveSearchFileSystem
{
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public IEnumerable<string> Enumerate(string directory) => Directory.EnumerateFileSystemEntries(directory, "*",
        new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 });
    public ArchiveFile ReadArchive(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        // FileInfo timestamps can return a sentinel for a vanished file. Never publish it as metadata.
        if (!file.Exists) throw new FileNotFoundException("検索中にZIPが消失したか、属性を取得できませんでした。", path);
        return new(path, file.Length, file.LastWriteTimeUtc);
    }
}
