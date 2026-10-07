using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace WebCrawler;

/// <summary>
/// An orb web drawn in the same node style as the spider. It is laid down spokes first,
/// then the capture spiral from the outside in, and slowly fades after ten minutes.
/// </summary>
sealed class Web : IDisposable
{
    const float Life = 600f;
    const float Fade = 30f;

    public readonly Vector2 Hub;
    public readonly float Radius;
    public float Progress { get; private set; }
    public bool Dead => age >= Life;

    readonly float s;
    readonly float[] spokes;
    readonly float[,] jitter;
    readonly int rings;
    readonly List<Vector2> bundles = new();
    readonly Overlay win = new();
    readonly Bitmap bmp;
    readonly Graphics g;
    readonly int size;
    float age;
    bool dirty = true;
    int lastAlpha = -1;

    public Web(Vector2 hub, float radius, Random rng, float s)
    {
        Hub = hub;
        Radius = radius;
        this.s = s;

        int n = 11 + rng.Next(4);
        spokes = new float[n];
        float start = (float)(rng.NextDouble() * Math.PI * 2);
        float gap = MathF.PI * 2 / n;
        for (int i = 0; i < n; i++)
            spokes[i] = start + i * gap + ((float)rng.NextDouble() - 0.5f) * 0.25f * gap;

        rings = Math.Max(4, (int)(radius * 0.8f / (7 * s)));
        jitter = new float[rings, n];
        for (int r = 0; r < rings; r++)
        for (int i = 0; i < n; i++)
            jitter[r, i] = 0.95f + (float)rng.NextDouble() * 0.1f;

        size = (int)(radius * 2 + 24 * s);
        bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        g = Graphics.FromImage(bmp);
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

    public bool Holds(Vector2 p, float margin) => Vector2.Distance(p, Hub) < Radius + margin;

    public void Update(float dt)
    {
        age += dt;
        if (Alpha() != lastAlpha) dirty = true;
    }

    int Alpha() => age > Life - Fade ? (int)(255 * Math.Clamp((Life - age) / Fade, 0, 1)) : 255;

    public void Render()
    {
        if (!dirty) return;
        dirty = false;
        lastAlpha = Alpha();
        float k = lastAlpha / 255f;

        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = new Vector2(size / 2f, size / 2f);
        int n = spokes.Length;

        using var spokePen = new Pen(Color.FromArgb((int)(120 * k), Palette.Line), 1f);
        using var spiralPen = new Pen(Color.FromArgb((int)(90 * k), Palette.Line), 0.9f);
        using var fill = new SolidBrush(Color.FromArgb((int)(230 * k), Palette.NodeFill));
        using var ring = new Pen(Color.FromArgb((int)(220 * k), 255, 255, 255), 1f * s);

        float spokeProgress = Math.Clamp(Progress / 0.35f, 0, 1) * n;
        for (int i = 0; i < n; i++)
        {
            float len = Math.Clamp(spokeProgress - i, 0, 1);
            if (len <= 0) continue;
            var end = c + Dir(spokes[i]) * Radius * len;
            g.DrawLine(spokePen, c.X, c.Y, end.X, end.Y);
            if (len >= 1) NodeAt(end, 1.4f * s, fill, ring);
        }

        float spiralProgress = Math.Clamp((Progress - 0.35f) / 0.65f, 0, 1);
        int segments = (int)(spiralProgress * rings * n);
        int drawn = 0;
        for (int r = 0; r < rings && drawn < segments; r++)
        {
            float rad = Radius * (0.95f - 0.75f * r / Math.Max(1, rings - 1));
            for (int j = 0; j < n && drawn < segments; j++, drawn++)
            {
                int j2 = (j + 1) % n;
                var p1 = c + Dir(spokes[j]) * rad * jitter[r, j];
                var p2 = c + Dir(spokes[j2]) * rad * jitter[r, j2];
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
        win.Close();
        win.Dispose();
        g.Dispose();
        bmp.Dispose();
    }
}
