using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace WebCrawler;

/// <summary>
/// A small buzzing bug for the spiders to hunt. It wanders in jittery flight,
/// lands now and then, dodges the cursor, and leaves after about a minute.
/// </summary>
sealed class Fly : IDisposable
{
    const int Size = 44;
    const float Lifetime = 55f;

    public Vector2 Pos;
    public bool Caught, Gone;
    public Spider Hunter;

    readonly World world;
    readonly Random rng;
    readonly float s;
    readonly Overlay win = new();
    readonly Bitmap bmp = new(Size, Size, PixelFormat.Format32bppPArgb);
    readonly Graphics g;
    Vector2 vel, target;
    float landed, age, facing;
    bool leaving;

    public bool Landed => landed > 0;

    public Fly(World world)
    {
        this.world = world;
        rng = world.Rng;
        s = world.S;
        g = Graphics.FromImage(bmp);

        var screens = Screen.AllScreens;
        var b = screens[rng.Next(screens.Length)].WorkingArea;
        bool left = rng.NextDouble() < 0.5;
        Pos = new Vector2(left ? b.Left - 20 : b.Right + 20, b.Top + (float)rng.NextDouble() * b.Height);
        target = new Vector2(b.Left + b.Width * (0.2f + (float)rng.NextDouble() * 0.6f),
                             b.Top + b.Height * (0.2f + (float)rng.NextDouble() * 0.6f));
        win.Show();
    }

    public void Update(float dt)
    {
        if (Caught || Gone) return;
        age += dt;

        var fromCursor = Pos - world.Cursor;
        float cursorDist = fromCursor.Length();
        if (cursorDist < 70 * s && cursorDist > 0.1f)
        {
            landed = 0;
            target = Pos + fromCursor / cursorDist * 220 * s;
        }

        if (!leaving && age > Lifetime)
        {
            leaving = true;
            var sc = Screen.FromPoint(new Point((int)Pos.X, (int)Pos.Y)).Bounds;
            var center = new Vector2(sc.Left + sc.Width / 2f, sc.Top + sc.Height / 2f);
            var away = Pos - center;
            target = Pos + (away.Length() > 1 ? Vector2.Normalize(away) : Vector2.UnitX) * 3000;
            landed = 0;
        }
        if (leaving && !World.OnAnyScreen(Pos)) { Gone = true; return; }

        if (landed > 0)
        {
            landed -= dt;
            vel = Vector2.Zero;
            return;
        }

        var to = target - Pos;
        float d = to.Length();
        if (!leaving && d < 15 * s)
        {
            if (rng.NextDouble() < 0.35) landed = 1 + (float)rng.NextDouble() * 3;
            target = NewTarget();
        }

        var desired = (d > 0.1f ? to / d : Vector2.Zero) * 200 * s + RandomInDisk(260 * s);
        vel += (desired - vel) * Math.Min(1, dt * 5);
        Pos += vel * dt;
        if (vel.Length() > 5) facing = MathF.Atan2(vel.Y, vel.X);
    }

    Vector2 NewTarget()
    {
        var area = Screen.FromPoint(new Point((int)Pos.X, (int)Pos.Y)).WorkingArea;
        float a = (float)(rng.NextDouble() * Math.PI * 2);
        float r = (80 + (float)rng.NextDouble() * 180) * s;
        var p = Pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
        return new Vector2(Math.Clamp(p.X, area.Left + 20, area.Right - 20), Math.Clamp(p.Y, area.Top + 20, area.Bottom - 20));
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

        win.Present(bmp, (int)(Pos.X - Size / 2f), (int)(Pos.Y - Size / 2f));
    }

    public void Dispose()
    {
        win.Close();
        win.Dispose();
        g.Dispose();
        bmp.Dispose();
    }
}
