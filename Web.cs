using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace WebCrawler;

/// <summary>
/// An orb web drawn in the same node style as the spider. It is laid down spokes first,
/// then the capture spiral from the outside in, and slowly fades after ten minutes.
/// Clicking its threads tears them; a few clicks clear it away.
/// </summary>
sealed class Web : IDisposable
{
    const float Life = 600f;
    const float Fade = 30f;
    const float Dissolve = 0.35f;
    const int ClicksToClear = 4;

    public readonly Vector2 Hub;
    public readonly float Radius;
    public float Progress { get; private set; }
    public bool Dead => age >= Life || (clearing && clearT >= Dissolve);
    public bool Disturbed { get; private set; }

    readonly float s;
    readonly float[] spokes;
    readonly float[] spokeLen;     // 0..1 of the radius; clicks cut spokes short
    readonly float[,] jitter;
    readonly bool[,] broken;       // capture-spiral segments torn by clicks
    readonly int rings;
    readonly List<Vector2> bundles = new();
    readonly Random rng;
    readonly Overlay win = new();
    readonly Bitmap bmp;
    readonly Graphics g;
    readonly int size;
    float age, shake, clearT;
    float sinceDamage = float.MaxValue, repairTimer;
    int health = ClicksToClear;
    bool clearing;
    bool hidden;
    bool dirty = true;
    int lastAlpha = -1;

    public readonly int Seed;

    public Web(Vector2 hub, float radius, Random rng, float s, int? seed = null)
    {
        // The web's shape comes from its own seed, so a saved web can be rebuilt exactly.
        Seed = seed ?? rng.Next();
        var shape = new Random(Seed);
        Hub = hub;
        Radius = radius;
        this.s = s;
        this.rng = rng;

        int n = 11 + shape.Next(4);
        spokes = new float[n];
        spokeLen = new float[n];
        float start = (float)(shape.NextDouble() * Math.PI * 2);
        float gap = MathF.PI * 2 / n;
        for (int i = 0; i < n; i++)
        {
            spokes[i] = start + i * gap + ((float)shape.NextDouble() - 0.5f) * 0.25f * gap;
            spokeLen[i] = 1;
        }

        rings = Math.Max(4, (int)(radius * 0.8f / (7 * s)));
        jitter = new float[rings, n];
        broken = new bool[rings, n];
        for (int r = 0; r < rings; r++)
        for (int i = 0; i < n; i++)
            jitter[r, i] = 0.95f + (float)shape.NextDouble() * 0.1f;

        size = (int)(radius * 2 + 24 * s);
        bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        g = Graphics.FromImage(bmp);

        win.Cursor = Cursors.Hand;
        win.MouseDown += OnMouseDown;
        win.Show();
    }

    public void SetProgress(float p)
    {
        p = Math.Clamp(p, 0, 1);
        if (p == Progress) return;
        Progress = p;
        dirty = true;
    }

    public void AddBundle(Vector2 p)
    {
        bundles.Add(p);
        dirty = true;
    }

    // Hidden while its screen has something fullscreen on it.
    public void SetHidden(bool hide)
    {
        if (hide == hidden) return;
        hidden = hide;
        if (hide) win.Hide();
        else
        {
            win.Show();
            dirty = true;
        }
    }

    public bool Finished => Progress >= 1 && !clearing;

    public WebMemory ToMemory() => new()
    {
        X = Hub.X,
        Y = Hub.Y,
        Radius = Radius,
        Seed = Seed,
        Age = age,
        Bundles = bundles.Select(b => new SavedPoint { X = b.X, Y = b.Y }).ToList(),
    };

    public void Restore(WebMemory m)
    {
        Progress = 1;
        age = m.Age;
        foreach (var b in m.Bundles) bundles.Add(new Vector2(b.X, b.Y));
        dirty = true;
    }
    public bool Damaged => Finished && health < ClicksToClear;
    public float SinceDamage => sinceDamage;

    // A fly hitting or struggling in the web makes it tremble.
    public void Vibrate()
    {
        shake = Math.Max(shake, 0.25f);
        dirty = true;
    }

    // Mends one torn thread at a time and lets cut spokes grow back. True once it's whole again.
    public bool Repair(float dt)
    {
        dirty = true;
        bool whole = true;
        for (int i = 0; i < spokeLen.Length; i++)
            if (spokeLen[i] < 1)
            {
                spokeLen[i] = Math.Min(1, spokeLen[i] + dt * 0.5f);
                whole = false;
            }

        repairTimer += dt;
        if (repairTimer >= 0.06f)
        {
            repairTimer = 0;
            int n = spokes.Length, total = rings * n, start = rng.Next(total);
            for (int k = 0; k < total; k++)
            {
                int idx = (start + k) % total, r = idx / n, j = idx % n;
                if (broken[r, j]) { broken[r, j] = false; whole = false; break; }
            }
        }
        else
            foreach (bool b in broken)
                if (b) { whole = false; break; }

        if (whole) health = ClicksToClear;
        return whole;
    }

    public bool Holds(Vector2 p, float margin) => !clearing && Vector2.Distance(p, Hub) < Radius + margin;

    public void Update(float dt, Vector2 cursor)
    {
        age += dt;
        if (Alpha() != lastAlpha) dirty = true;

        // Threads only catch clicks while the cursor is over the web.
        win.SetClickThrough(clearing || Vector2.Distance(cursor, Hub) > Radius + 8 * s);

        sinceDamage += dt;
        if (shake > 0) { shake -= dt; dirty = true; }
        if (clearing) { clearT += dt; dirty = true; }
    }

    void OnMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || clearing) return;
        var origin = new Vector2(Hub.X - size / 2f, Hub.Y - size / 2f);
        Tear(new Vector2(e.X, e.Y) + origin);
    }

    void Tear(Vector2 at)
    {
        Disturbed = true;
        shake = 0.3f;
        sinceDamage = 0;
        dirty = true;

        var local = at - Hub;
        float reach = 24 * s;
        int n = spokes.Length;

        // Snap spiral threads near the click.
        for (int r = 0; r < rings; r++)
        for (int j = 0; j < n; j++)
            if (Vector2.Distance(SegmentMid(r, j), local) < reach) broken[r, j] = true;

        // Cut spokes that pass close to the click, at the point where it touched them.
        for (int i = 0; i < n; i++)
        {
            var dir = Dir(spokes[i]);
            float along = Vector2.Dot(local, dir);
            if (along <= 0 || along > Radius * spokeLen[i]) continue;
            if (Vector2.Distance(dir * along, local) < reach * 0.5f)
                spokeLen[i] = Math.Min(spokeLen[i], Math.Max(0.1f, along / Radius - 0.05f));
        }

        // Wrapped flies near the click fall out.
        bundles.RemoveAll(b => Vector2.Distance(b, at) < reach * 1.2f);

        health--;
        if (health <= 0) clearing = true;
    }

    Vector2 SegmentMid(int r, int j)
    {
        int j2 = (j + 1) % spokes.Length;
        float rad = RingRadius(r);
        return (Dir(spokes[j]) * rad * jitter[r, j] + Dir(spokes[j2]) * rad * jitter[r, j2]) / 2;
    }

    float RingRadius(int r) => Radius * (0.95f - 0.75f * r / Math.Max(1, rings - 1));

    int Alpha()
    {
        float k = age > Life - Fade ? Math.Clamp((Life - age) / Fade, 0, 1) : 1;
        if (clearing) k *= Math.Clamp(1 - clearT / Dissolve, 0, 1);
        return (int)(255 * k);
    }

    public void Render()
    {
        if (!dirty || hidden) return;
        dirty = false;
        lastAlpha = Alpha();
        float k = lastAlpha / 255f;

        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = new Vector2(size / 2f, size / 2f);
        if (shake > 0)
            c += new Vector2((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 4 * s * (shake / 0.3f);
        int n = spokes.Length;

        using var spokePen = new Pen(Color.FromArgb((int)(120 * k), Palette.Line), 1f);
        using var spiralPen = new Pen(Color.FromArgb((int)(90 * k), Palette.Line), 0.9f);
        // Nearly invisible, wider strokes along each thread so they're easy to click.
        using var hitPen = new Pen(Color.FromArgb(clearing ? 0 : 1, 0, 0, 0), 6 * s);
        using var fill = new SolidBrush(Color.FromArgb((int)(230 * k), Palette.NodeFill));
        using var ring = new Pen(Color.FromArgb((int)(220 * k), 255, 255, 255), 1f * s);

        float spokeProgress = Math.Clamp(Progress / 0.35f, 0, 1) * n;
        for (int i = 0; i < n; i++)
        {
            float grown = Math.Clamp(spokeProgress - i, 0, 1);
            float len = Math.Min(grown, spokeLen[i]);
            if (len <= 0) continue;
            var end = c + Dir(spokes[i]) * Radius * len;
            g.DrawLine(hitPen, c.X, c.Y, end.X, end.Y);
            g.DrawLine(spokePen, c.X, c.Y, end.X, end.Y);
            if (grown >= 1 && spokeLen[i] >= 1) NodeAt(end, 1.4f * s, fill, ring);
        }

        float spiralProgress = Math.Clamp((Progress - 0.35f) / 0.65f, 0, 1);
        int segments = (int)(spiralProgress * rings * n);
        int drawn = 0;
        for (int r = 0; r < rings && drawn < segments; r++)
        {
            float rad = RingRadius(r);
            for (int j = 0; j < n && drawn < segments; j++, drawn++)
            {
                if (broken[r, j]) continue;
                int j2 = (j + 1) % n;
                // A spiral thread hangs between two spokes; if either was cut short of it, it's gone too.
                if (rad > Radius * spokeLen[j] || rad > Radius * spokeLen[j2]) continue;
                var p1 = c + Dir(spokes[j]) * rad * jitter[r, j];
                var p2 = c + Dir(spokes[j2]) * rad * jitter[r, j2];
                g.DrawLine(hitPen, p1.X, p1.Y, p2.X, p2.Y);
                g.DrawLine(spiralPen, p1.X, p1.Y, p2.X, p2.Y);
            }
        }

        NodeAt(c, 2f * s, fill, ring);

        // Flies the spider caught and wrapped up.
        using var silk = new SolidBrush(Color.FromArgb((int)(210 * k), 235, 240, 255));
        using var silkLine = new Pen(Color.FromArgb((int)(150 * k), Palette.Line), 0.8f);
        foreach (var b in bundles)
        {
            var p = b - Hub + c;
            g.FillEllipse(silk, p.X - 2.5f * s, p.Y - 3.5f * s, 5 * s, 7 * s);
            g.DrawLine(silkLine, p.X - 2.5f * s, p.Y - 1 * s, p.X + 2.5f * s, p.Y);
            g.DrawLine(silkLine, p.X - 2.5f * s, p.Y + 1.5f * s, p.X + 2.5f * s, p.Y + 2 * s);
        }

        win.Present(bmp, (int)(Hub.X - size / 2f), (int)(Hub.Y - size / 2f));
    }

    void NodeAt(Vector2 p, float r, Brush fill, Pen ring)
    {
        g.FillEllipse(fill, p.X - r, p.Y - r, r * 2, r * 2);
        g.DrawEllipse(ring, p.X - r, p.Y - r, r * 2, r * 2);
    }

    static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));

    public void Dispose()
    {
        win.MouseDown -= OnMouseDown;
        win.Close();
        win.Dispose();
        g.Dispose();
        bmp.Dispose();
    }
}
