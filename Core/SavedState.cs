using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebCrawler;

/// <summary>What one creature remembers.</summary>
sealed class CreatureMemory
{
    // Which kind of creature this is, so the right one is recreated (see CreatureFactory).
    public string Kind { get; set; } = CreatureFactory.DefaultKind;

    // 0 is wary (red), 1 is at ease (green).
    public float Comfort { get; set; }
    // The last time you did something it liked; comfort starts to fade a day after this.
    public DateTime LastBond { get; set; } = DateTime.UtcNow;
    // How far the fading has already been applied, so it isn't counted twice.
    public DateTime LastDecay { get; set; } = DateTime.UtcNow;

    public DateTime Born { get; set; } = DateTime.UtcNow;
    // Hunger is worked out from how long ago it last ate.
    public DateTime LastFed { get; set; } = DateTime.UtcNow;
    public int FliesEaten { get; set; }
    public int TimesThrown { get; set; }
    public int Naps { get; set; }

    // Where it was and what it was doing when the app closed.
    public float? X { get; set; }
    public float? Y { get; set; }
    public float Heading { get; set; }
    public bool Asleep { get; set; }
    public int SleepingInWeb { get; set; } = -1; // index into SavedState.Webs
    // Tucked up in the nest rather than out on the desktop.
    public bool InNest { get; set; }

    // Places it slept, hid or rested in peace, and places something bad happened.
    public List<RememberedSpot> Favourites { get; set; } = new();
    public List<RememberedSpot> Scary { get; set; } = new();
}

sealed class RememberedSpot
{
    public float X { get; set; }
    public float Y { get; set; }
    public DateTime When { get; set; }
}

/// <summary>A finished web, rebuilt identically from its seed.</summary>
sealed class WebMemory
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Radius { get; set; }
    public int Seed { get; set; }
    public float Age { get; set; }
    public List<SavedPoint> Bundles { get; set; } = new();
}

sealed class SavedPoint
{
    public float X { get; set; }
    public float Y { get; set; }
}

/// <summary>
/// Everything remembered between runs, kept in %LOCALAPPDATA%\WebCrawler\state.json.
/// </summary>
sealed class SavedState
{
    // One entry per spider, in the order they were added.
    // Kept as "Spiders" so older memory files still load.
    public List<CreatureMemory> Spiders { get; set; } = new();
    public List<WebMemory> Webs { get; set; } = new();

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
                    foreach (var c in state.Comfort) state.Spiders.Add(new CreatureMemory { Comfort = c });
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
            // Write to a temporary file first so a shutdown mid-save can't leave a half-written memory.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
