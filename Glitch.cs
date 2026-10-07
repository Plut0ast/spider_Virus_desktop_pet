using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace WebCrawler;

enum GlitchKind { Split, Slice, Box, Bar, Echo, Tiles, Invert }

static class Palette
{
    public static readonly Color Pink = Color.FromArgb(255, 63, 164);
    public static readonly Color Blue = Color.FromArgb(61, 90, 254);
    public static readonly Color Cyan = Color.FromArgb(77, 216, 255);
    public static readonly Color Yellow = Color.FromArgb(255, 225, 77);
    public static readonly Color Lime = Color.FromArgb(125, 255, 106);
    public static readonly Color[] All = { Pink, Blue, Cyan, Yellow, Lime };

    // Spider, web and fly linework.
    public static readonly Color Line = Color.FromArgb(150, 172, 255);
    public static readonly Color BodyRed = Color.FromArgb(255, 64, 96);
    public static readonly Color NodeFill = Color.FromArgb(12, 14, 30);
    public static Color Pick(Random rng) => All[rng.Next(All.Length)];
}

/// <summary>
/// One short-lived distortion of a patch of the real screen. The patch is sampled
/// when the glitch spawns and drawn back, mangled, until it fades out.
/// </summary>
sealed class Glitch : IDisposable
{
    static readonly string[] Labels = { "href", "PMID", "doi", "<a>", "0x1F", "node", "#css", "js", "div", "span", "ref", "ISBN" };
    static readonly (GlitchKind kind, int weight)[] Weights =
    {
        (GlitchKind.Split, 22), (GlitchKind.Slice, 16), (GlitchKind.Box, 18), (GlitchKind.Bar, 16),
        (GlitchKind.Echo, 10), (GlitchKind.Tiles, 10), (GlitchKind.Invert, 8),
    };

    public GlitchKind Kind;
    public Rectangle Src;
    public float Age, Life;
    public Color Tint;
    public bool Dead => Age >= Life;
    public bool IsAnchor => Kind is GlitchKind.Box or GlitchKind.Bar;
    public Vector2 Center => new(Src.X + Src.Width / 2f, Src.Y + Src.Height / 2f);

    readonly Random rng;
    readonly float s;
    int[] rawPx;
    Bitmap img;
    float refresh;
    float scale = 1f;
    Vector2 offset;
    string label;

    Glitch(Random rng, float s) { this.rng = rng; this.s = s; }

    public static Glitch Spawn(Random rng, Vector2 at, float s)
    {
        var g = new Glitch(rng, s) { Kind = PickKind(rng), Tint = Palette.Pick(rng) };
        (int w, int h) = g.Kind switch
        {
            GlitchKind.Split => (R(rng, 24, 70), R(rng, 6, 12)),
            GlitchKind.Slice => (R(rng, 30, 80), R(rng, 8, 22)),
            GlitchKind.Box => (R(rng, 18, 60), R(rng, 8, 13)),
            GlitchKind.Bar => (R(rng, 20, 70), R(rng, 7, 12)),
            GlitchKind.Echo => (R(rng, 28, 60), R(rng, 7, 12)),
            GlitchKind.Tiles => (R(rng, 20, 48), R(rng, 10, 24)),
            _ => (R(rng, 20, 60), R(rng, 6, 12)),
        };
        w = Math.Max(4, (int)(w * s));
        h = Math.Max(4, (int)(h * s));
        g.Src = new Rectangle((int)(at.X - w / 2f), (int)(at.Y - h / 2f), w, h);
        g.Life = g.Kind switch
        {
            GlitchKind.Box => F(rng, 0.6f, 1.4f),
            GlitchKind.Echo => F(rng, 0.9f, 1.6f),
            _ => F(rng, 0.25f, 0.9f),
        };

        if (g.Kind != GlitchKind.Box)
            g.rawPx = Capture(g.Src);

        switch (g.Kind)
        {
            case GlitchKind.Split: g.img = Build(RgbSplit(g.rawPx, w, h, Math.Max(1, (int)(R(rng, 1, 3) * s))), w, h); break;
            case GlitchKind.Slice: g.img = Build(SliceShift(g.rawPx, w, h, rng, s), w, h); break;
            case GlitchKind.Bar: g.img = Build(Duotone(g.rawPx, g.Tint), w, h); break;
            case GlitchKind.Tiles: g.img = Build(Tiles(g.rawPx, w, h, rng, s), w, h); break;
            case GlitchKind.Invert: g.img = Build(Invert(g.rawPx), w, h); break;
            case GlitchKind.Echo:
                g.img = Build(RgbSplit(g.rawPx, w, h, Math.Max(1, (int)(1 * s))), w, h);
                g.scale = F(rng, 1.4f, 1.8f);
                g.offset = new Vector2(F(rng, -10, 10) * s - w * (g.scale - 1) / 2f, -h * g.scale - F(rng, 4, 14) * s);
                break;
            case GlitchKind.Box:
                g.label = Labels[rng.Next(Labels.Length)];
                break;
        }
        return g;
    }

    public void Update(float dt)
    {
        Age += dt;
        if (Kind is not (GlitchKind.Slice or GlitchKind.Tiles)) return;
        refresh -= dt;
        if (refresh > 0) return;
        refresh = 0.06f;
        var next = Kind == GlitchKind.Slice
            ? SliceShift(rawPx, Src.Width, Src.Height, rng, s)
            : Tiles(rawPx, Src.Width, Src.Height, rng, s);
        img.Dispose();
        img = Build(next, Src.Width, Src.Height);
    }

    public void Draw(Graphics g, Point origin, Font labelFont)
    {
        if (rng.NextDouble() < 0.08) return; // flicker
        float t = Age / Life;
        float alpha = t > 0.7f ? Math.Max(0, 1 - (t - 0.7f) / 0.3f) : 1f;
        float jx = rng.NextDouble() < 0.2 ? F(rng, -1, 1) * s : 0;
        var dst = new RectangleF(Src.X - origin.X + jx, Src.Y - origin.Y, Src.Width, Src.Height);

        switch (Kind)
        {
            case GlitchKind.Box:
                DrawBox(g, dst, alpha, labelFont);
                break;
            case GlitchKind.Echo:
                dst = new RectangleF(Src.X - origin.X + offset.X, Src.Y - origin.Y + offset.Y,
                    Src.Width * scale, Src.Height * scale);
                DrawImage(g, img, dst, alpha);
                break;
            default:
                DrawImage(g, img, dst, alpha);
                break;
        }
    }

    void DrawBox(Graphics g, RectangleF r, float alpha, Font labelFont)
    {
        var c = Color.FromArgb((int)(255 * alpha), Tint);
        float pad = 1 * s;
        var box = RectangleF.Inflate(r, pad, pad);
        using var pen = new Pen(c, 1f * s);
        using var brush = new SolidBrush(c);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        float k = 2.5f * s;
        g.FillRectangle(brush, box.Left - k / 2, box.Top - k / 2, k, k);
        g.FillRectangle(brush, box.Right - k / 2, box.Bottom - k / 2, k, k);
        g.SmoothingMode = old;

        var size = g.MeasureString(label, labelFont);
        var tag = new RectangleF(box.Left, box.Top - size.Height - 1, size.Width, size.Height);
        g.FillRectangle(brush, tag);
        using var text = new SolidBrush(Color.FromArgb((int)(255 * alpha), 10, 10, 18));
        g.DrawString(label, labelFont, text, tag.Location);
    }

    static void DrawImage(Graphics g, Bitmap img, RectangleF dst, float alpha)
    {
        if (img == null) return;
        if (alpha >= 0.99f)
        {
            g.DrawImage(img, dst);
            return;
        }
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
        g.DrawImage(img, Rectangle.Round(dst), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
    }

    public void Dispose() => img?.Dispose();

    // ---- sampling & pixel ops (ARGB ints) ----

    static int[] Capture(Rectangle r)
    {
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
        var data = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[r.Width * r.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);
        return px;
    }

    static Bitmap Build(int[] px, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    static int Pack(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

    static int[] RgbSplit(int[] px, int w, int h, int d)
    {
        var o = new int[px.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int r = (px[row + Math.Clamp(x - d, 0, w - 1)] >> 16) & 0xFF;
                int g = (px[row + x] >> 8) & 0xFF;
                int b = px[row + Math.Clamp(x + d, 0, w - 1)] & 0xFF;
                o[row + x] = Pack(r, g, b);
            }
        }
        return o;
    }

    static int[] SliceShift(int[] px, int w, int h, Random rng, float s)
    {
        var o = new int[px.Length];
        int maxShift = Math.Max(1, (int)(8 * s));
        int y = 0;
        while (y < h)
        {
            int band = R(rng, 1, 4);
            int shift = rng.NextDouble() < 0.6 ? rng.Next(-maxShift, maxShift + 1) : 0;
            int drop = rng.NextDouble() < 0.15 ? rng.Next(3) : -1; // knock out one channel
            for (int yy = y; yy < Math.Min(h, y + band); yy++)
            {
                int row = yy * w;
                for (int x = 0; x < w; x++)
                {
                    int c = px[row + (((x - shift) % w) + w) % w];
                    if (drop >= 0) c &= ~(0xFF << (drop * 8));
                    o[row + x] = c | unchecked((int)0xFF000000);
                }
            }
            y += band;
        }
        return o;
    }

    static int[] Tiles(int[] px, int w, int h, Random rng, float s)
    {
        var o = (int[])px.Clone();
        int t = Math.Max(3, (int)(4 * s));
        int cols = w / t, rows = h / t;
        for (int ty = 0; ty < rows; ty++)
        for (int tx = 0; tx < cols; tx++)
        {
            if (rng.NextDouble() < 0.7) continue;
            int sx = rng.Next(cols), sy = Math.Clamp(ty + rng.Next(-1, 2), 0, rows - 1);
            for (int y = 0; y < t; y++)
                Array.Copy(px, (sy * t + y) * w + sx * t, o, (ty * t + y) * w + tx * t, t);
        }
        return o;
    }

    static int[] Duotone(int[] px, Color tint)
    {
        var o = new int[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            int c = px[i];
            float l = (((c >> 16) & 0xFF) * 0.3f + ((c >> 8) & 0xFF) * 0.59f + (c & 0xFF) * 0.11f) / 255f;
            o[i] = Pack((int)(10 + (tint.R - 10) * l), (int)(10 + (tint.G - 10) * l), (int)(18 + (tint.B - 18) * l));
        }
        return o;
    }

    static int[] Invert(int[] px)
    {
        var o = new int[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            int c = px[i];
            int r = 255 - ((c >> 16) & 0xFF), g = 255 - ((c >> 8) & 0xFF), b = 255 - (c & 0xFF);
            o[i] = Pack((r * 3 + 61) / 4, (g * 3 + 90) / 4, (b * 3 + 254) / 4);
        }
        return o;
    }

    static GlitchKind PickKind(Random rng)
    {
        int total = 0;
        foreach (var (_, wt) in Weights) total += wt;
        int roll = rng.Next(total);
        foreach (var (kind, wt) in Weights)
        {
            if (roll < wt) return kind;
            roll -= wt;
        }
        return GlitchKind.Split;
    }

    static int R(Random rng, int min, int max) => rng.Next(min, max + 1);
    static float F(Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);
}
