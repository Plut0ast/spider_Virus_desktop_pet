using System.Drawing.Drawing2D;
using System.Numerics;

namespace WebCrawler;

/// <summary>
/// The nest: an ordinary app window where creatures can be put to bed. Carry a creature onto the
/// nest and let go, and it curls up there; click one to wake it. Every creature in the nest gets a
/// specimen tag underneath with its stats. Nothing here is spider-specific: each creature draws itself.
/// </summary>
sealed class NestWindow : Form
{
    const float FrameSeconds = 0.033f;

    readonly Func<IReadOnlyList<Creature>> allCreatures;
    readonly NestView nestView;
    readonly Panel tagsPanel;
    readonly TagsView tagsView;
    readonly System.Windows.Forms.Timer anim = new() { Interval = 33 };
    float tagsTimer;

    public World World { get; }
    public float Time { get; private set; }
    public IReadOnlyList<Creature> Everyone => allCreatures();
    public List<Creature> Sleepers => allCreatures().Where(c => c.InNest).ToList();

    public NestWindow(World world, Func<IReadOnlyList<Creature>> creatures, Icon icon)
    {
        World = world;
        allCreatures = creatures;

        Text = "Spider Nest";
        Icon = icon;
        BackColor = Palette.WindowBack;
        ForeColor = Palette.Text;
        Font = new Font("Consolas", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 700);
        MinimumSize = new Size(380, 480);

        nestView = new NestView(this) { Dock = DockStyle.Top, Height = 320 };
        tagsPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Palette.WindowBack };
        tagsView = new TagsView(this);
        tagsPanel.Controls.Add(tagsView);
        // Added in this order so the nest docks to the top and the tags fill the rest.
        Controls.Add(tagsPanel);
        Controls.Add(nestView);

        tagsPanel.Resize += (_, _) => tagsView.UpdateLayout(tagsPanel.ClientSize);
        anim.Tick += (_, _) => Tick();
        anim.Start();
    }

    void Tick()
    {
        Time += FrameSeconds;
        bool showing = Visible && WindowState != FormWindowState.Minimized;
        World.NestZone = showing ? nestView.ScreenDropZone() : null;
        nestView.Invalidate();

        tagsTimer -= FrameSeconds;
        if (tagsTimer <= 0)
        {
            tagsTimer = 0.5f;
            tagsView.UpdateLayout(tagsPanel.ClientSize);
            tagsView.Invalidate();
        }
    }

    public void Wake(Creature creature)
    {
        creature.LeaveNest(WakeSpot());
        tagsView.UpdateLayout(tagsPanel.ClientSize);
        tagsView.Invalidate();
    }

    // Where a woken creature is set down: just outside the window, on a free bit of screen.
    Vector2 WakeSpot()
    {
        var b = Bounds;
        var candidates = new[]
        {
            new Vector2(b.Left + b.Width / 2f, b.Bottom + 40),
            new Vector2(b.Right + 50, b.Top + b.Height / 2f),
            new Vector2(b.Left - 50, b.Top + b.Height / 2f),
            new Vector2(b.Left + b.Width / 2f, b.Top - 40),
        };
        foreach (var p in candidates)
            if (World.IsUsable(p)) return p;
        var area = Screen.FromControl(this).WorkingArea;
        return new Vector2(area.Left + area.Width / 2f, area.Top + area.Height / 2f);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        anim.Stop();
        World.NestZone = null;
        World.NestHover = false;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) anim.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The shape of one nest: a lopsided bowl of crossing, sagging silk strands with loose tufts and
/// knots, hung from the top of the window on a few threads. Everything is generated from a seed
/// (and how scruffy it should be), so it looks the same from frame to frame.
/// </summary>
sealed class NestShape
{
    public readonly record struct Strand(PointF A, PointF C1, PointF C2, PointF B, float Width, int Alpha);

    public readonly List<Strand> Back = new(), Front = new(), Tufts = new();
    public readonly List<(PointF At, float Radius)> Knots = new();
    public readonly List<PointF> Anchors = new();
    public readonly List<PointF> HuskSpots = new();
    public PointF GlitchSpot;
    public PointF[] Bulk, Hollow, FrontWall;

    readonly float rx, ry, wall;
    readonly float[] lumps = new float[6];

    // A spot on the rim, relative to the nest's centre. f scales the radius (1 is the rim itself);
    // u drops a point down the outside of the front wall (0 at the rim, 1 at the bottom).
    PointF Rim(float a, float f, float u = 0)
    {
        float wobble = 1 + lumps[0] * MathF.Sin(2 * a + lumps[1]) + lumps[2] * MathF.Sin(3 * a + lumps[3])
                         + lumps[4] * MathF.Sin(5 * a + lumps[5]);
        // The near side sags a little lower than the far side.
        float sag = MathF.Max(0, MathF.Sin(a)) * 0.06f;
        float x = MathF.Cos(a) * rx * wobble * f;
        float y = MathF.Sin(a) * ry * (wobble + sag) * f;
        float drop = u * wall * MathF.Max(0, MathF.Sin(a));
        return new PointF(x, y + drop);
    }

    public NestShape(float rx, float ry, float scruffy, int seed)
    {
        this.rx = rx;
        this.ry = ry;
        wall = ry * 0.45f;
        var rng = new Random(seed);
        float R(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);

        lumps[0] = R(0.04f, 0.09f); lumps[1] = R(0, 6.28f);
        lumps[2] = R(0.03f, 0.06f); lumps[3] = R(0, 6.28f);
        lumps[4] = R(0.01f, 0.03f); lumps[5] = R(0, 6.28f);

        const int steps = 72;
        var bulk = new List<PointF>();
        for (int i = 0; i <= steps; i++) bulk.Add(Rim(i * MathF.PI * 2 / steps, 1.07f));
        for (int i = steps; i >= 0; i--)
        {
            float a = i * MathF.PI / steps; // front half only: the outer wall you can see
            bulk.Add(Rim(a, 1.07f, 1));
        }
        Bulk = bulk.ToArray();
        Hollow = Enumerable.Range(0, steps).Select(i => Rim(i * MathF.PI * 2 / steps, 0.88f)).ToArray();

        var front = new List<PointF>();
        for (int i = 0; i <= steps / 2; i++) front.Add(Rim(i * MathF.PI / (steps / 2), 0.86f));
        for (int i = steps / 2; i >= 0; i--) front.Add(Rim(i * MathF.PI / (steps / 2), 1.07f, 1));
        FrontWall = front.ToArray();

        // Strands looping and crossing round the rim, sagging a little between their ends.
        int count = (int)(120 + scruffy * 70);
        for (int i = 0; i < count; i++)
        {
            float a1 = R(0, MathF.PI * 2);
            float a2 = a1 + R(0.25f, 1.1f) * (rng.Next(2) == 0 ? -1 : 1);
            float mid = (a1 + a2) / 2;
            bool isFront = MathF.Sin(mid) > 0;
            float spread = 0.06f + scruffy * 0.06f;
            var a = Rim(a1, R(0.88f - spread, 1.04f + spread), isFront ? R(0, 1) : 0);
            var b = Rim(a2, R(0.88f - spread, 1.04f + spread), isFront ? R(0, 1) : 0);
            float sag = R(2, 7) + scruffy * 5;
            var c1 = new PointF(a.X + (b.X - a.X) / 3 + R(-10, 10), a.Y + (b.Y - a.Y) / 3 + sag);
            var c2 = new PointF(a.X + (b.X - a.X) * 2 / 3 + R(-10, 10), a.Y + (b.Y - a.Y) * 2 / 3 + sag);
            var strand = new Strand(a, c1, c2, b, R(0.6f, 1.5f), (int)R(60, 190));
            (isFront ? Front : Back).Add(strand);
            if (rng.NextDouble() < 0.18) Knots.Add((Bezier(strand, R(0.2f, 0.8f)), R(1.0f, 2.8f)));
        }

        // Loose ends sticking out, more of them on a scruffy nest.
        int tufts = (int)(4 + scruffy * 16);
        for (int i = 0; i < tufts; i++)
        {
            float a = R(0, MathF.PI * 2);
            var start = Rim(a, 1.02f, R(0, 0.8f));
            var end = Rim(a + R(-0.25f, 0.25f), R(1.14f, 1.3f), R(0, 1));
            var c1 = new PointF(start.X + R(-8, 8), start.Y + R(-6, 10));
            var c2 = new PointF(end.X + R(-8, 8), end.Y + R(-4, 12));
            Tufts.Add(new Strand(start, c1, c2, end, R(0.5f, 1.0f), (int)R(50, 130)));
        }

        // Threads holding it up, tied to the far rim.
        foreach (float a in new[] { R(3.5f, 3.9f), R(4.5f, 4.9f), R(5.5f, 5.9f) })
            Anchors.Add(Rim(a, 1.0f));

        // Where wrapped flies hang off the front of the nest.
        for (int i = 0; i < 24; i++) HuskSpots.Add(Rim(R(0.25f, 2.9f), R(0.95f, 1.06f), R(0.25f, 0.95f)));

        GlitchSpot = Rim(R(5.8f, 6.2f), 1.05f);
    }

    public static PointF Bezier(Strand s, float t)
    {
        float u = 1 - t;
        float x = u * u * u * s.A.X + 3 * u * u * t * s.C1.X + 3 * u * t * t * s.C2.X + t * t * t * s.B.X;
        float y = u * u * u * s.A.Y + 3 * u * u * t * s.C1.Y + 3 * u * t * t * s.C2.Y + t * t * t * s.B.Y;
        return new PointF(x, y);
    }
}

/// <summary>The hanging nest with its sleepers tucked inside.</summary>
sealed class NestView : Control
{
    readonly NestWindow owner;
    NestShape shape;
    Size shapeSize;
    int shapeScruff = -1;
    int hoverSlot = -1;

    public NestView(NestWindow owner)
    {
        this.owner = owner;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.WindowBack;
    }

    float Rx => Math.Min(Width * 0.36f, 160);
    float Ry => Rx * 0.42f;

    // The nest hangs in the lower part of the view, swaying slowly on its threads.
    PointF Centre(float time, bool hover) =>
        new(Width / 2f + MathF.Sin(time * 0.7f) * 2.5f + (hover ? MathF.Sin(time * 3f) * 3f : 0), Height * 0.58f);

    // A generous area around the nest, in screen coordinates, for dropping a creature in.
    public RectangleF ScreenDropZone()
    {
        var c = Centre(0, false);
        var r = new RectangleF(c.X - Rx - 30, c.Y - Ry - 50, Rx * 2 + 60, Ry * 2 + 90);
        var p = PointToScreen(new Point((int)r.X, (int)r.Y));
        return new RectangleF(p.X, p.Y, r.Width, r.Height);
    }

    PointF Slot(int i, int count, PointF c)
    {
        float spacing = Math.Min(Rx * 0.5f, Rx * 1.15f / Math.Max(1, count));
        float x = (i - (count - 1) / 2f) * spacing;
        // Low in the hollow, so the front lip covers their lower legs.
        float y = Ry * 0.38f + (i % 2 == 0 ? 0 : -Ry * 0.14f);
        return new PointF(c.X + x, c.Y + y);
    }

    static float SlotSize(int count) => count <= 2 ? 88 : count <= 4 ? 68 : 56;

    // The nest is messier when the creatures in it (or out and about, if it's empty) are wary of you.
    int Scruffiness()
    {
        var who = owner.Sleepers.Count > 0 ? owner.Sleepers : owner.Everyone.ToList();
        float comfort = who.Count > 0 ? who.Average(c => c.Comfort) : 0.5f;
        return (int)MathF.Round((1 - comfort) * 3); // 0 tidy .. 3 scruffy
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Palette.WindowBack);

        int scruff = Scruffiness();
        if (shape == null || shapeSize != Size || scruff != shapeScruff)
        {
            shape = new NestShape(Rx, Ry, scruff / 3f, 1207);
            shapeSize = Size;
            shapeScruff = scruff;
        }

        float time = owner.Time;
        bool dropHover = owner.World.NestHover;
        var sleepers = owner.Sleepers;
        var c = Centre(time, dropHover);
        float brighten = dropHover ? 1.35f : 1f;
        // The silk trembles with the sleepers' breathing, and more when you reach for one.
        float tremble = (sleepers.Count > 0 ? MathF.Sin(time * 1.2f) * 0.7f : 0)
                      + (hoverSlot >= 0 ? MathF.Sin(time * 25f) * 0.8f : 0);

        // Threads it hangs from, tied off at the top of the window.
        foreach (var anchor in shape.Anchors)
        {
            var bottom = new PointF(c.X + anchor.X, c.Y + anchor.Y);
            var top = new PointF(Width / 2f + anchor.X * 0.55f, 0);
            using var thread = new Pen(Color.FromArgb(110, Palette.Silk), 1f);
            g.DrawBezier(thread, top, new PointF(top.X, top.Y + 30), new PointF(bottom.X, bottom.Y - 40), bottom);
            Knot(g, bottom, 1.8f, 200);
        }

        var state = g.Save();
        g.TranslateTransform(c.X, c.Y);

        using (var bulk = new SolidBrush(Palette.NestBulk))
            g.FillPolygon(bulk, shape.Bulk);
        using (var path = new GraphicsPath())
        {
            path.AddPolygon(shape.Hollow);
            using var hollow = new PathGradientBrush(path)
            {
                CenterColor = Palette.HollowCentre,
                SurroundColors = new[] { Palette.HollowEdge },
                CenterPoint = new PointF(0, -shape.Hollow.Max(p => p.Y) * 0.15f),
            };
            g.FillPath(hollow, path);
        }

        foreach (var strand in shape.Back) DrawStrand(g, strand, brighten, 0);
        g.Restore(state);

        // Sleepers curled up in the hollow.
        float size = SlotSize(sleepers.Count);
        for (int i = 0; i < sleepers.Count; i++)
        {
            var who = sleepers[i];
            who.Appearance.DrawResting(g, Slot(i, sleepers.Count, c), size, who.ShownComfort, time + i * 1.7f);
        }

        state = g.Save();
        g.TranslateTransform(c.X, c.Y);

        // The front lip in front of them, so they sit in the nest rather than on it.
        using (var lip = new SolidBrush(Palette.NestBulk))
            g.FillPolygon(lip, shape.FrontWall);
        foreach (var strand in shape.Front) DrawStrand(g, strand, brighten, tremble);
        foreach (var strand in shape.Tufts) DrawStrand(g, strand, brighten, tremble * 1.5f);
        foreach (var (at, radius) in shape.Knots) Knot(g, at, radius, 170);

        // One wrapped fly husk caught in the silk for every fly eaten.
        int husks = Math.Min(shape.HuskSpots.Count, owner.Everyone.Sum(x => x.FliesEaten));
        for (int i = 0; i < husks; i++) Husk(g, shape.HuskSpots[i], i);

        // A scrap of glitched screen snagged in the threads.
        GlitchScrap(g, shape.GlitchSpot, time);
        g.Restore(state);

        using var muted = new SolidBrush(Palette.MutedText);
        using var font = new Font("Consolas", 8.5f);
        var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        if (sleepers.Count == 0)
            g.DrawString("carry a spider here\nand let go", font, muted, new RectangleF(c.X - Rx, c.Y - Ry * 0.8f, Rx * 2, Ry * 1.2f), centre);

        string caption = hoverSlot >= 0 && hoverSlot < sleepers.Count ? $"click to wake {sleepers[hoverSlot].Name.ToLowerInvariant()}"
                       : sleepers.Count == 0 ? "empty"
                       : sleepers.Count == 1 ? "1 asleep" : $"{sleepers.Count} asleep";
        g.DrawString(caption, font, muted, new RectangleF(0, Height - 24, Width, 20), centre);
    }

    static void DrawStrand(Graphics g, NestShape.Strand s, float brighten, float tremble)
    {
        int alpha = Math.Min(255, (int)(s.Alpha * brighten));
        using var pen = new Pen(Color.FromArgb(alpha, Palette.Silk), s.Width);
        g.DrawBezier(pen, s.A, new PointF(s.C1.X, s.C1.Y + tremble), new PointF(s.C2.X, s.C2.Y + tremble), s.B);
    }

    static void Knot(Graphics g, PointF p, float r, int alpha)
    {
        using var fill = new SolidBrush(Color.FromArgb(alpha, Palette.Silk));
        g.FillEllipse(fill, p.X - r, p.Y - r, r * 2, r * 2);
    }

    // A fly wrapped in silk, hanging off the nest on a short thread.
    static void Husk(Graphics g, PointF at, int i)
    {
        float drop = 4 + (i * 7 % 5);
        var hang = new PointF(at.X, at.Y + drop);
        using var thread = new Pen(Color.FromArgb(120, Palette.Silk), 0.8f);
        using var silk = new SolidBrush(Color.FromArgb(220, Palette.Silk));
        using var wrap = new Pen(Color.FromArgb(140, Palette.Ink), 0.8f);
        g.DrawLine(thread, at, hang);
        g.FillEllipse(silk, hang.X - 2.4f, hang.Y, 4.8f, 7f);
        g.DrawLine(wrap, hang.X - 2.2f, hang.Y + 2.5f, hang.X + 2.2f, hang.Y + 3.3f);
        g.DrawLine(wrap, hang.X - 2.2f, hang.Y + 4.6f, hang.X + 2.2f, hang.Y + 5.4f);
    }

    // A few offset blocks of the glitch colours, flickering now and then.
    static void GlitchScrap(Graphics g, PointF at, float time)
    {
        bool flick = MathF.Sin(time * 2.3f) > 0.92f;
        float jx = flick ? 3 : 0;
        using var pink = new SolidBrush(Color.FromArgb(170, Palette.Pink));
        using var cyan = new SolidBrush(Color.FromArgb(170, Palette.Cyan));
        using var silk = new SolidBrush(Color.FromArgb(150, Palette.Silk));
        g.FillRectangle(silk, at.X - 1, at.Y - 2, 9, 5);
        g.FillRectangle(pink, at.X + 1 + jx, at.Y - 4, 6, 2);
        g.FillRectangle(cyan, at.X + 4 - jx, at.Y + 2, 5, 2);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var sleepers = owner.Sleepers;
        float size = SlotSize(sleepers.Count);
        var c = Centre(owner.Time, false);
        hoverSlot = -1;
        for (int i = 0; i < sleepers.Count; i++)
        {
            var p = Slot(i, sleepers.Count, c);
            float dx = e.X - p.X, dy = e.Y - p.Y;
            if (dx * dx + dy * dy < size * size * 0.36f) { hoverSlot = i; break; }
        }
        Cursor = hoverSlot >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoverSlot = -1;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || hoverSlot < 0) return;
        var sleepers = owner.Sleepers;
        if (hoverSlot < sleepers.Count) owner.Wake(sleepers[hoverSlot]);
        hoverSlot = -1;
    }
}

/// <summary>
/// A paper specimen tag for each creature asleep in the nest, hung on a thread under the one before.
/// </summary>
sealed class TagsView : Control
{
    const int Pad = 18, Gap = 22, HeaderHeight = 62, RowHeight = 21, Cut = 14;

    readonly NestWindow owner;
    readonly Font smallFont = new("Consolas", 8f);
    readonly Font nameFont = new("Consolas", 12f, FontStyle.Bold);
    readonly Font bodyFont = new("Consolas", 9f);

    public TagsView(NestWindow owner)
    {
        this.owner = owner;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.WindowBack;
    }

    static int TagHeight(List<StatLine> lines) => HeaderHeight + lines.Count * RowHeight + 16;

    public void UpdateLayout(Size available)
    {
        var sleepers = owner.Sleepers;
        int height = Pad + sleepers.Sum(c => TagHeight(c.Stats()) + Gap);
        int width = available.Width;
        if (height > available.Height) width -= SystemInformation.VerticalScrollBarWidth;
        Size = new Size(Math.Max(1, width), Math.Max(available.Height, height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Palette.WindowBack);

        var sleepers = owner.Sleepers;
        if (sleepers.Count == 0)
        {
            using var muted = new SolidBrush(Palette.MutedText);
            var centre = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("no specimens in the nest", bodyFont, muted, new RectangleF(0, 24, Width, 40), centre);
            return;
        }

        var everyone = owner.Everyone;
        int y = Pad;
        float threadTopY = 0;
        foreach (var c in sleepers)
        {
            var lines = c.Stats();
            int w = Width - Pad * 2 - 12, h = TagHeight(lines);
            // Each tag hangs slightly crooked, always the same way for the same creature.
            int index = Math.Max(0, IndexOf(everyone, c));
            float tilt = (index * 37 % 5 - 2) * 0.45f;
            var hole = new PointF(Pad + 6 + Cut + 6, y + 14);

            using (var thread = new Pen(Color.FromArgb(120, Palette.Silk), 1f))
                g.DrawBezier(thread, new PointF(hole.X + 10, threadTopY), new PointF(hole.X + 14, threadTopY + 8),
                             new PointF(hole.X, hole.Y - 14), hole);

            var state = g.Save();
            g.TranslateTransform(Pad + 6, y);
            g.RotateTransform(tilt);
            DrawTag(g, c, lines, index, w, h);
            g.Restore(state);

            threadTopY = y + h - 4;
            y += h + Gap;
        }
    }

    static int IndexOf(IReadOnlyList<Creature> list, Creature c)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == c) return i;
        return -1;
    }

    void DrawTag(Graphics g, Creature c, List<StatLine> lines, int index, int w, int h)
    {
        // A luggage-tag shape: square on the right, corners clipped on the left where it's tied.
        var shape = new[]
        {
            new PointF(Cut, 0), new PointF(w, 0), new PointF(w, h),
            new PointF(Cut, h), new PointF(0, h - Cut), new PointF(0, Cut),
        };
        using (var paper = new SolidBrush(Palette.Paper))
            g.FillPolygon(paper, shape);
        using (var edge = new Pen(Color.FromArgb(50, Palette.Ink)))
            g.DrawPolygon(edge, shape);
        using (var hole = new Pen(Color.FromArgb(90, Palette.Ink), 1.2f))
            g.DrawEllipse(hole, Cut + 3, 11, 6, 6);
        using (var punched = new SolidBrush(Palette.WindowBack))
            g.FillEllipse(punched, Cut + 4, 12, 4, 4);

        using var ink = new SolidBrush(Palette.Ink);
        using var inkMuted = new SolidBrush(Palette.InkMuted);
        float left = Cut + 18;
        var right = new StringFormat { Alignment = StringAlignment.Far };

        g.DrawString($"No. {index + 1:000}", smallFont, inkMuted, left, 8);
        g.DrawString(c.Appearance.Taxon.ToUpperInvariant(), smallFont, inkMuted, new RectangleF(0, 8, w - 12, 14), right);
        g.DrawString(c.Name, nameFont, ink, left - 1, 24);
        using (var rule = new Pen(Color.FromArgb(110, Palette.Ink), 0.8f))
            g.DrawLine(rule, left, HeaderHeight - 10, w - 12, HeaderHeight - 10);

        float valueX = left + 74;
        float rowY = HeaderHeight - 4;
        foreach (var line in lines)
        {
            g.DrawString(line.Label, bodyFont, inkMuted, left, rowY);
            float x = valueX;
            switch (line.Style)
            {
                case StatStyle.Swatch:
                    using (var swatch = new SolidBrush(line.Color ?? Palette.Ink))
                        g.FillRectangle(swatch, x, rowY + 3, 10, 10);
                    x += 16;
                    g.DrawString(line.Value, bodyFont, ink, x, rowY);
                    break;
                case StatStyle.Gauge:
                    Stomach(g, x, rowY + 3, line.Amount);
                    x += 26;
                    g.DrawString(line.Value, bodyFont, ink, x, rowY);
                    break;
                case StatStyle.Tally:
                    Tally(g, x, rowY + 2, (int)line.Amount, w - 16 - x);
                    break;
                default:
                    g.DrawString(line.Value, bodyFont, ink, x, rowY);
                    break;
            }
            rowY += RowHeight;
        }
    }

    // A little stomach outline, filled from the bottom by how full it is.
    static void Stomach(Graphics g, float x, float y, float fullness)
    {
        var r = new RectangleF(x, y, 20, 11);
        using var path = new GraphicsPath();
        path.AddEllipse(r);
        var state = g.Save();
        g.SetClip(path);
        using (var fill = new SolidBrush(Palette.Ink))
            g.FillRectangle(fill, r.X, r.Bottom - r.Height * Math.Clamp(fullness, 0, 1), r.Width, r.Height);
        g.Restore(state);
        using var outline = new Pen(Palette.Ink, 1f);
        g.DrawEllipse(outline, r);
    }

    // Tally marks in fives, with a count once they run out of room.
    void Tally(Graphics g, float x, float y, int count, float room)
    {
        using var pen = new Pen(Palette.Ink, 1.2f);
        using var ink = new SolidBrush(Palette.Ink);
        if (count == 0)
        {
            g.DrawString("none yet", bodyFont, ink, x, y - 2);
            return;
        }
        const float step = 4.5f, groupGap = 6f;
        int drawn = 0;
        float cx = x;
        while (drawn < count && cx + 5 * step + groupGap < x + room - 30)
        {
            int inGroup = Math.Min(5, count - drawn);
            for (int i = 0; i < Math.Min(4, inGroup); i++)
                g.DrawLine(pen, cx + i * step, y, cx + i * step, y + 11);
            if (inGroup == 5) g.DrawLine(pen, cx - 2, y + 9, cx + 3 * step + 2, y + 2);
            drawn += inGroup;
            cx += 4 * step + groupGap;
        }
        if (drawn < count) g.DrawString($"+{count - drawn}", bodyFont, ink, cx, y - 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            smallFont.Dispose();
            nameFont.Dispose();
            bodyFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
