using System.IO;
using System.Globalization;

namespace Unextract.Gui.Services;

internal sealed record LogReservation(string? Path, string? Directory, string? Error);

internal interface ILogLocation
{
    string Directory { get; }
    // Chooses a not-yet-existing name. The file itself is created only by the CLI (CreateNew).
    LogReservation Reserve(DateTime batchStartUtc, int number);
}

internal sealed class LogLocation(string? directory = null) : ILogLocation
{
    public string Directory { get; } = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "unextract", "logs");

    public LogReservation Reserve(DateTime batchStartUtc, int number)
    {
        try
        {
            if (!Path.IsPathFullyQualified(Directory)) return new(null, Directory, "ログの保存場所が絶対パスではありません。");
            System.IO.Directory.CreateDirectory(Directory);
            string stem = string.Create(CultureInfo.InvariantCulture,
                $"delete-{batchStartUtc.ToUniversalTime():yyyyMMdd-HHmmss}-{number:D3}");
            string path = Path.Combine(Directory, stem + ".jsonl");
            // Same-named files (restart, second instance) get an unused GUID-suffixed alias; never overwritten here.
            while (Path.Exists(path)) path = Path.Combine(Directory, $"{stem}-{Guid.NewGuid():N}.jsonl");
            return new(path, Directory, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new(null, Directory, e.Message);
        }
    }
}
