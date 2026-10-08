using System.Numerics;

namespace WebCrawler;

/// <summary>How a stat is shown on a creature's specimen tag.</summary>
enum StatStyle
{
    Text,   // just the value
    Swatch, // a small square of Color before the value
    Gauge,  // a stomach-shaped gauge filled to Amount (0 to 1) before the value
    Tally,  // Amount drawn as tally marks
}

/// <summary>One row on a creature's specimen tag.</summary>
readonly record struct StatLine(string Label, string Value, StatStyle Style = StatStyle.Text, float Amount = 0, Color? Color = null);

/// <summary>
/// What every creature shares, whatever it looks like or however it behaves: its bond with you,
/// hunger, age and life story, places it likes and avoids, and going to bed in the nest.
/// A creature type subclasses this for its behaviour and supplies an <see cref="ICreatureAppearance"/>.
/// </summary>
abstract class Creature : IDisposable
{
    // After a full day with no kind interaction, comfort drains at this rate per real day.
    const double NeglectGraceDays = 1;
    const double NeglectPerDay = 0.1;
    // Hours from a full stomach to starving.
    const double HoursToStarve = 8;
    const int MaxSpots = 8;
    static readonly TimeSpan FavouriteFor = TimeSpan.FromDays(14);
    static readonly TimeSpan ScaryFor = TimeSpan.FromDays(3);

    protected readonly World world;
    protected readonly Random rng;
    protected readonly CrawlerSettings settings;
    protected readonly float s;

    public string Name { get; set; } = "";
    public abstract string Kind { get; }
    public abstract ICreatureAppearance Appearance { get; }

    // How comfortable it is with you: 0 is wary, 1 is at ease.
    public float Comfort { get; protected set; }
    public float ShownComfort => shownComfort;
    protected float shownComfort;  // eases toward Comfort so colour changes are gradual
    protected float comfortPulse;  // flashes when it warms to you

    protected DateTime lastBond, lastDecay, born, lastFed;
    protected int fliesEaten, timesThrown, naps;
    public int FliesEaten => fliesEaten;
    protected readonly List<RememberedSpot> favourites = new();
    protected readonly List<RememberedSpot> scary = new();
    float neglectTimer;

    public bool InNest { get; protected set; }
    public abstract bool IsHeld { get; }

    protected Creature(World world, CreatureMemory m)
    {
        this.world = world;
        rng = world.Rng;
        settings = world.Settings;
        s = world.S;

        Comfort = Math.Clamp(m.Comfort, 0, 1);
        lastBond = m.LastBond;
        lastDecay = m.LastDecay;
        born = m.Born;
        lastFed = m.LastFed;
        fliesEaten = m.FliesEaten;
        timesThrown = m.TimesThrown;
        naps = m.Naps;
        var now = DateTime.UtcNow;
        favourites.AddRange(m.Favourites.Where(p => now - p.When < FavouriteFor));
        scary.AddRange(m.Scary.Where(p => now - p.When < ScaryFor));
        ApplyNeglect(now);
        shownComfort = Comfort;
    }

    public abstract void Update(float dt);
    public abstract void Render();
    public abstract void KeepOnTop();
    // Tucked into the nest: off the desktop and asleep until woken from the nest window.
    public abstract void EnterNest();
    // Woken from the nest and set down at a screen position.
    public abstract void LeaveNest(Vector2 at);
    // Dragged out of the nest: it comes out already held by the cursor at this screen position.
    public abstract void LiftOutOfNest(Vector2 cursor);
    public abstract CreatureMemory GetMemory(List<Web> savedWebs);
    public abstract void Dispose();

    // ---------- the bond ----------

    protected void TickBond(float dt)
    {
        comfortPulse = Math.Max(0, comfortPulse - dt * 1.5f);
        shownComfort += (Comfort - shownComfort) * Math.Min(1, dt * 0.8f);
        neglectTimer -= dt;
        if (neglectTimer <= 0)
        {
            neglectTimer = 60;
            ApplyNeglect(DateTime.UtcNow);
        }
    }

    void ApplyNeglect(DateTime now)
    {
        var from = lastBond.AddDays(NeglectGraceDays);
        if (lastDecay > from) from = lastDecay;
        if (now <= from) return;
        Comfort = Math.Clamp(Comfort - (float)((now - from).TotalDays * NeglectPerDay), 0, 1);
        lastDecay = now;
    }

    protected void AddComfort(float amount)
    {
        float before = Comfort;
        Comfort = Math.Clamp(Comfort + amount, 0, 1);
        if (amount > 0) lastBond = DateTime.UtcNow;
        if (amount >= 0.02f && Comfort > before) comfortPulse = 1;
    }

    // ---------- hunger ----------

    // 0 is well fed, 1 is starving. Rises with real time, even while the app is closed.
    public float Hunger => (float)Math.Clamp((DateTime.UtcNow - lastFed).TotalHours / HoursToStarve, 0, 1);

    protected void Feed(float amount)
    {
        double left = Math.Max(0, Hunger - amount);
        lastFed = DateTime.UtcNow - TimeSpan.FromHours(left * HoursToStarve);
    }

    // ---------- places ----------

    // Places within a short walk of each other count as the same spot.
    protected void Remember(List<RememberedSpot> list, Vector2 p)
    {
        var now = DateTime.UtcNow;
        foreach (var spot in list)
            if (Vector2.Distance(new Vector2(spot.X, spot.Y), p) < 120 * s)
            {
                spot.When = now;
                return;
            }
        list.Add(new RememberedSpot { X = p.X, Y = p.Y, When = now });
        if (list.Count > MaxSpots)
        {
            list.Sort((a, b) => a.When.CompareTo(b.When));
            list.RemoveAt(0);
        }
    }

    protected bool NearScarySpot(Vector2 p)
    {
        foreach (var spot in scary)
            if (Vector2.Distance(new Vector2(spot.X, spot.Y), p) < 150 * s) return true;
        return false;
    }

    // ---------- memory and stats ----------

    // The parts of its memory every creature has; the subclass adds where it is and what it's doing.
    protected CreatureMemory BaseMemory() => new()
    {
        Kind = Kind,
        Comfort = Comfort,
        LastBond = lastBond,
        LastDecay = lastDecay,
        Born = born,
        LastFed = lastFed,
        FliesEaten = fliesEaten,
        TimesThrown = timesThrown,
        Naps = naps,
        InNest = InNest,
        Favourites = new List<RememberedSpot>(favourites),
        Scary = new List<RememberedSpot>(scary),
    };

    public string MoodWord => Comfort < 0.25f ? "Scared of you"
                            : Comfort < 0.5f ? "Wary of you"
                            : Comfort < 0.75f ? "Relaxed around you"
                            : "Fond of you";

    // One word for small spaces like its tag.
    public string MoodShort => Comfort < 0.25f ? "scared"
                             : Comfort < 0.5f ? "wary"
                             : Comfort < 0.75f ? "relaxed"
                             : "fond";

    public string HungerWord => Hunger < 0.25f ? "Full"
                              : Hunger < 0.5f ? "Peckish"
                              : Hunger < 0.8f ? "Hungry"
                              : "Starving";

    public string AgeText
    {
        get
        {
            var age = DateTime.UtcNow - born;
            return age.TotalDays >= 1 ? Plural((int)age.TotalDays, "day")
                 : age.TotalHours >= 1 ? Plural((int)age.TotalHours, "hour")
                 : Plural(Math.Max(1, (int)age.TotalMinutes), "minute");
        }
    }

    static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";

    // What its specimen tag in the nest window shows. A creature type can add its own rows (and traits, later).
    public virtual List<StatLine> Stats() => new()
    {
        new("age", AgeText),
        new("mood", $"{MoodShort} ({Comfort:P0})", StatStyle.Swatch, Comfort, Appearance.ComfortColor(Comfort)),
        new("hunger", $"{HungerWord.ToLowerInvariant()} ({Hunger:P0})", StatStyle.Gauge, 1 - Hunger),
        new("flies", fliesEaten.ToString(), StatStyle.Tally, fliesEaten),
        new("naps", naps.ToString()),
        new("thrown", timesThrown.ToString()),
    };

    public string Summary() => string.Join("\n", Stats().Select(l => $"{l.Label}: {l.Value}"))
                               + $"\nFavourite spots: {favourites.Count}\nPlaces it's avoiding: {scary.Count}";

    // ---------- small helpers ----------

    protected static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));
    protected static float Smooth(float t) => t * t * (3 - 2 * t);

    protected static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.PI * 2;
        while (a < -MathF.PI) a += MathF.PI * 2;
        return a;
    }

    protected Vector2 RandomInDisk(float r)
    {
        float a = (float)(rng.NextDouble() * Math.PI * 2);
        float d = MathF.Sqrt((float)rng.NextDouble()) * r;
        return new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
    }
}
