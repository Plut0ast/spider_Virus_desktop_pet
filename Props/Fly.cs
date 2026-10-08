using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace WebCrawler;

/// <summary>
/// A small buzzing bug for the spiders to hunt. It only flies over open desktop:
/// it wanders in jittery flight, lands now and then, dodges the cursor, and
/// disappears after about a minute or if it ends up over a window.
/// </summary>
sealed class Fly : IDisposable
{
    const int Size = 44;
    const float Lifetime = 55f;

    public Vector2 Pos;
    public bool Caught, Gone;
    public Creature Hunter;

    readonly World world;
    readonly Random rng;
    readonly float s;
    readonly Overlay win = new();
    readonly Bitmap bmp = new(Size, Size, PixelFormat.Format32bppPArgb);
    readonly Graphics g;
    Vector2 vel, target;
    float landed, age, facing, overWindowFor;

    // Caught in a web, struggling, until a spider gets to it or it tears free.
    public Web StuckIn;
    float stuckFor, noStickFor;

    public bool Landed => landed > 0;

    public Fly(World world, Vector2 start)
    {
        this.world = world;
        rng = world.Rng;
        s = world.S;
        g = Graphics.FromImage(bmp);
        Pos = start;
        target = NewTarget();
        win.Show();
    }

    public void Update(float dt)
    {
        if (Caught || Gone) return;

        if (StuckIn != null)
        {
            stuckFor += dt;
            if (!world.Webs.Contains(StuckIn) || !StuckIn.Finished || stuckFor > 25)
            {
                StuckIn = null;
                noStickFor = 6;
            }
            else
            {
                if (rng.NextDouble() < dt * 2) StuckIn.Vibrate();
                return;
            }
        }
        noStickFor -= dt;
        age += dt;

        if (age > Lifetime || world.IsBlocked(Pos)) { Gone = true; return; }

        // Flies stay over the desktop; one that drifts over a window soon gives up.
        overWindowFor = world.Windows.IsFreeDesktop(Pos) ? 0 : overWindowFor + dt;
        if (overWindowFor > 0.6f) { Gone = true; return; }

        var fromCursor = Pos - world.Cursor;
        float cursorDist = fromCursor.Length();
        if (cursorDist < 70 * s && cursorDist > 0.1f)
        {
            landed = 0;
            var dodge = Pos + fromCursor / cursorDist * 220 * s;
            target = world.Windows.IsFreeDesktop(dodge) ? dodge : NewTarget();
        }

        if (landed > 0)
        {
            landed -= dt;
            vel = Vector2.Zero;
            return;
        }

        // Flying through a finished web can get it stuck.
        if (noStickFor <= 0)
            foreach (var web in world.Webs)
                if (web.Finished && Vector2.Distance(Pos, web.Hub) < web.Radius * 0.85f && rng.NextDouble() < dt * 1.5)
                {
                    StuckIn = web;
                    stuckFor = 0;
                    vel = Vector2.Zero;
                    web.Vibrate();
                    return;
                }

        var to = target - Pos;
        float d = to.Length();
        if (d < 15 * s)
        {
            if (rng.NextDouble() < 0.35) landed = 1 + (float)rng.NextDouble() * 3;
            target = NewTarget();
        }

        var desired = (d > 0.1f ? to / d : Vector2.Zero) * 200 * s + RandomInDisk(260 * s);
        vel += (desired - vel) * Math.Min(1, dt * 5);
        Pos += vel * dt;
        if (vel.Length() > 5) facing = MathF.Atan2(vel.Y, vel.X);
    }

    // Somewhere nearby that's still open desktop.
    Vector2 NewTarget()
    {
        for (int tries = 0; tries < 12; tries++)
        {
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            float r = (80 + (float)rng.NextDouble() * 180) * s;
            var p = Pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            if (world.Windows.IsFreeDesktop(p)) return p;
        }
        return Pos;
    }

    Vector2 RandomInDisk(float r)
    {
        float a = (float)(rng.NextDouble() * Math.PI * 2);
        float d = MathF.Sqrt((float)rng.NextDouble()) * r;
        return new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
    }

    public void Render()
    {
        if (Caught || Gone) return;

        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool flying = landed <= 0;

        var st = g.Save();
        g.TranslateTransform(Size / 2f, Size / 2f);
        g.RotateTransform(facing * 180f / MathF.PI);

        // Wings blur while flying and fold back when landed.
        int wingAlpha = flying ? 60 + rng.Next(100) : 150;
        float spread = flying ? (rng.NextDouble() < 0.5 ? 1f : 0.6f) : 0.35f;
        using (var wing = new SolidBrush(Color.FromArgb(wingAlpha, 200, 220, 255)))
        {
            g.FillEllipse(wing, -5 * s, -2 * s - 5 * s * spread, 6 * s, 5 * s * spread + 1 * s);
            g.FillEllipse(wing, -5 * s, 1 * s, 6 * s, 5 * s * spread + 1 * s);
        }

        using var fill = new SolidBrush(Color.FromArgb(235, Palette.NodeFill));
        using var ring = new Pen(Color.FromArgb(240, 255, 255, 255), 1f * s);
        g.FillEllipse(fill, -3.5f * s, -2.2f * s, 7 * s, 4.4f * s);
        g.DrawEllipse(ring, -3.5f * s, -2.2f * s, 7 * s, 4.4f * s);
        using var eye = new SolidBrush(Palette.BodyRed);
        g.FillEllipse(eye, 2.6f * s, -1.4f * s, 2.8f * s, 2.8f * s);
        g.Restore(st);

        // A stuck fly shudders on the spot.
        var shudder = StuckIn != null ? RandomInDisk(1.5f * s) : Vector2.Zero;
        win.Present(bmp, (int)(Pos.X + shudder.X - Size / 2f), (int)(Pos.Y + shudder.Y - Size / 2f));
    }

    public void Dispose()
    {
        win.Close();
        win.Dispose();
        g.Dispose();
        bmp.Dispose();
    }
}
