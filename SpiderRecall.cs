using System.Numerics;

namespace WebCrawler;

/// <summary>
/// What the spider carries over between runs: where it was, what it was doing, places it
/// likes and places it avoids, and a little life story.
/// </summary>
sealed partial class Spider
{
    const int MaxSpots = 8;
    static readonly TimeSpan FavouriteFor = TimeSpan.FromDays(14);
    static readonly TimeSpan ScaryFor = TimeSpan.FromDays(3);

    DateTime born;
    int fliesEaten, timesThrown, naps;
    readonly List<RememberedSpot> favourites = new();
    readonly List<RememberedSpot> scary = new();

    void Restore(SpiderMemory m)
    {
        born = m.Born;
        fliesEaten = m.FliesEaten;
        timesThrown = m.TimesThrown;
        naps = m.Naps;
        var now = DateTime.UtcNow;
        favourites.AddRange(m.Favourites.Where(p => now - p.When < FavouriteFor));
        scary.AddRange(m.Scary.Where(p => now - p.When < ScaryFor));

        if (m.X is float x && m.Y is float y && world.IsUsable(new Vector2(x, y)))
        {
            // Back exactly where it was, facing the same way.
            pos = new Vector2(x, y);
            heading = m.Heading;
            mode = Mode.Wander;
            pauseLeft = 1.5f;

            if (m.Asleep)
            {
                if (m.SleepingInWeb >= 0 && m.SleepingInWeb < world.Webs.Count)
                {
                    sleepWeb = world.Webs[m.SleepingInWeb];
                    pos = sleepWeb.Hub;
                }
                mode = Mode.Sleep;
            }

            target = pos;
            vel = Vector2.Zero;
            foreach (var leg in legs)
            {
                leg.Foot = RestAt(leg, pos, heading);
                leg.Lift = 0;
                leg.Stepping = false;
            }
            return;
        }

        var screens = world.UsableScreens();
        EnterFrom(screens.Length > 0 ? screens[rng.Next(screens.Length)] : Screen.AllScreens[0]);
    }

    public SpiderMemory GetMemory(List<Web> savedWebs) => new()
    {
        Comfort = Comfort,
        LastBond = lastBond,
        LastDecay = lastDecay,
        Born = born,
        FliesEaten = fliesEaten,
        TimesThrown = timesThrown,
        Naps = naps,
        X = away ? null : pos.X,
        Y = away ? null : pos.Y,
        Heading = heading,
        Asleep = mode == Mode.Sleep,
        SleepingInWeb = mode == Mode.Sleep && sleepWeb != null ? savedWebs.IndexOf(sleepWeb) : -1,
        Favourites = new List<RememberedSpot>(favourites),
        Scary = new List<RememberedSpot>(scary),
    };

    // Places within a short walk of each other count as the same spot.
    void Remember(List<RememberedSpot> list, Vector2 p)
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

    bool NearScarySpot(Vector2 p)
    {
        foreach (var spot in scary)
            if (Vector2.Distance(new Vector2(spot.X, spot.Y), p) < 150 * s) return true;
        return false;
    }

    bool TryGoToFavourite()
    {
        if (favourites.Count == 0) return false;
        var spot = favourites[rng.Next(favourites.Count)];
        var p = new Vector2(spot.X, spot.Y) + RandomInDisk(30 * s);
        if (!world.IsUsable(p) || NearScarySpot(p)) return false;
        target = p;
        return true;
    }

    // A favourite spot with room for a web on open desktop and no other web already there.
    bool TryFavouriteWebSpot(float radius, out Vector2 hub)
    {
        hub = default;
        if (favourites.Count == 0 || rng.NextDouble() > 0.5) return false;
        foreach (var spot in favourites.OrderBy(_ => rng.Next()))
        {
            var p = new Vector2(spot.X, spot.Y);
            if (world.Windows.CoveredPoints(p, radius) != 0) continue;
            if (world.Webs.Any(w => Vector2.Distance(w.Hub, p) < w.Radius + radius)) continue;
            hub = p;
            return true;
        }
        return false;
    }

    public string Summary()
    {
        var age = DateTime.UtcNow - born;
        string ageText = age.TotalDays >= 1 ? $"{(int)age.TotalDays} days"
                       : age.TotalHours >= 1 ? $"{(int)age.TotalHours} hours"
                       : $"{Math.Max(1, (int)age.TotalMinutes)} minutes";
        string mood = Comfort < 0.25f ? "Scared of you"
                    : Comfort < 0.5f ? "Wary of you"
                    : Comfort < 0.75f ? "Relaxed around you"
                    : "Fond of you";
        return $"Age: {ageText}\n" +
               $"Mood: {mood} ({Comfort:P0} comfort)\n" +
               $"Flies eaten: {fliesEaten}\n" +
               $"Naps: {naps}\n" +
               $"Times thrown: {timesThrown}\n" +
               $"Favourite spots: {favourites.Count}\n" +
               $"Places it's avoiding: {scary.Count}";
    }
}
