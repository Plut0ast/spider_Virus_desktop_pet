using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebCrawler;

/// <summary>What one spider remembers about you.</summary>
sealed class SpiderMemory
{
    // 0 is wary (red), 1 is at ease (green).
    public float Comfort { get; set; }
    // The last time you did something it liked; comfort starts to fade a day after this.
    public DateTime LastBond { get; set; } = DateTime.UtcNow;
    // How far the fading has already been applied, so it isn't counted twice.
    public DateTime LastDecay { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// What the spiders remember between runs, kept in %LOCALAPPDATA%\WebCrawler\state.json.
/// </summary>
sealed class SavedState
{
    // One entry per spider, in the order they were added.
    public List<SpiderMemory> Spiders { get; set; } = new();

    // Older save files only stored a comfort value per spider.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<float> Comfort { get; set; }

    static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebCrawler", "state.json");

    public static SavedState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(FilePath)) ?? new SavedState();
                if (state.Comfort != null && state.Spiders.Count == 0)
                    foreach (var c in state.Comfort) state.Spiders.Add(new SpiderMemory { Comfort = c });
                state.Comfort = null;
                return state;
            }
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
