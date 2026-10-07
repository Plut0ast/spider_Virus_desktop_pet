using System.Text.Json;

namespace WebCrawler;

/// <summary>
/// What the spiders remember between runs, kept in %LOCALAPPDATA%\WebCrawler\state.json.
/// </summary>
sealed class SavedState
{
    // How comfortable each spider is with you, 0 (red) to 1 (green), in the order they were added.
    public List<float> Comfort { get; set; } = new();

    static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebCrawler", "state.json");

    public static SavedState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SavedState>(File.ReadAllText(FilePath)) ?? new SavedState();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new SavedState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
