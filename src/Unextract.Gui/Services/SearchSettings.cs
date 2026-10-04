using System.IO;
using System.Security;
using System.Text.Json;

namespace Unextract.Gui.Services;

internal sealed record SearchSettingsLoad(string? Directory);
internal sealed record SearchSettingsSave(string? Error);
internal interface ISearchSettings
{
    Task<SearchSettingsLoad> LoadAsync();
    Task<SearchSettingsSave> SaveAsync(string directory);
}

internal sealed class SearchSettings(string path) : ISearchSettings
{
    public SearchSettings() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "unextract", "settings.json")) { }

    public Task<SearchSettingsLoad> LoadAsync() => Task.Run(() =>
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("search_directory", out var directory) &&
                directory.ValueKind == JsonValueKind.String && directory.GetString() is { } value &&
                Path.IsPathFullyQualified(value)) return new SearchSettingsLoad(value);
        }
        catch (Exception e) when (e is JsonException || IsFileFailure(e)) { }
        return new SearchSettingsLoad(null);
    });

    public Task<SearchSettingsSave> SaveAsync(string directory) => Task.Run(() =>
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Only this one value. Direct write, no delete/rename-based replacement or session persistence.
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string> { ["search_directory"] = directory }));
            return new SearchSettingsSave(null);
        }
        catch (Exception e) when (IsFileFailure(e))
        {
            return new SearchSettingsSave($"検索ディレクトリを保存できませんでした ({path}): {e.Message}");
        }
    });

    private static bool IsFileFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}
