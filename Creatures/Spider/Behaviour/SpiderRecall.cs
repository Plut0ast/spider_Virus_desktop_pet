using System.Numerics;

namespace WebCrawler;

/// <summary>
/// The spider-specific side of its memory: where it was and what it was doing when the app
/// closed, and how it uses the places it remembers. The shared parts (comfort, hunger, life
/// story, liked and avoided places) live in <see cref="Creature"/>.
/// </summary>
sealed partial class Spider
{
    void RestorePlace(CreatureMemory m)
    {
        if (m.InNest)
        {
            // Still tucked up in the nest; it comes out when woken from the nest window.
            InNest = true;
            mode = Mode.Sleep;
            var p = Screen.PrimaryScreen!.WorkingArea;
            pos = new Vector2(p.Left + p.Width / 2f, p.Top + p.Height / 2f);
            ResetFeet();
            return;
        }

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
            ResetFeet();
            return;
        }

        var screens = world.UsableScreens();
        EnterFrom(screens.Length > 0 ? screens[rng.Next(screens.Length)] : Screen.AllScreens[0]);
    }

    public override CreatureMemory GetMemory(List<Web> savedWebs)
    {
        var m = BaseMemory();
        bool onDesktop = !away && !InNest;
        m.X = onDesktop ? pos.X : null;
        m.Y = onDesktop ? pos.Y : null;
        m.Heading = heading;
        m.Asleep = onDesktop && mode == Mode.Sleep;
        m.SleepingInWeb = m.Asleep && sleepWeb != null ? savedWebs.IndexOf(sleepWeb) : -1;
        return m;
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
}
