using System.Drawing.Drawing2D;
using System.Numerics;

namespace WebCrawler;

/// <summary>
/// The nest: an ordinary app window where creatures can be put to bed. Carry a creature onto the
/// nest and let go, and it curls up there; click one to wake it. Every creature in the nest gets a
/// card underneath showing its stats. Nothing here is spider-specific: each creature draws itself.
/// </summary>
sealed class NestWindow : Form
{
    const float FrameSeconds = 0.033f;

    readonly Func<IReadOnlyList<Creature>> allCreatures;
    readonly Action<Creature> wake;
    readonly NestView nestView;
    readonly Panel cardsPanel;
    readonly StatsView statsView;
    readonly System.Windows.Forms.Timer anim = new() { Interval = 33 };
    float cardsTimer;

    public World World { get; }
    public float Time { get; private set; }
    public List<Creature> Sleepers => allCreatures().Where(c => c.InNest).ToList();

    public NestWindow(World world, Func<IReadOnlyList<Creature>> creatures, Action<Creature> wake, Icon icon)
    {
        World = world;
        allCreatures = creatures;
        this.wake = wake;

        Text = "Spider Nest";
        Icon = icon;
        BackColor = Palette.WindowBack;
        ForeColor = Palette.Text;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 640);
        MinimumSize = new Size(380, 460);

        nestView = new NestView(this) { Dock = DockStyle.Top, Height = 270 };
        cardsPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Palette.WindowBack };
        statsView = new StatsView(this);
        cardsPanel.Controls.Add(statsView);
        // Added in this order so the nest docks to the top and the cards fill the rest.
        Controls.Add(cardsPanel);
        Controls.Add(nestView);

        cardsPanel.Resize += (_, _) => statsView.UpdateLayout(cardsPanel.ClientSize);
        anim.Tick += (_, _) => Tick();
        anim.Start();
    }

    void Tick()
    {
        Time += FrameSeconds;
        bool showing = Visible && WindowState != FormWindowState.Minimized;
        World.NestZone = showing ? nestView.ScreenDropZone() : null;
        nestView.Invalidate();

        cardsTimer -= FrameSeconds;
        if (cardsTimer <= 0)
        {
            cardsTimer = 0.5f;
            statsView.UpdateLayout(cardsPanel.ClientSize);
            statsView.Invalidate();
        }
    }

    public void Wake(Creature creature)
    {
        creature.LeaveNest(WakeSpot());
        statsView.UpdateLayout(cardsPanel.ClientSize);
        statsView.Invalidate();
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

/// <summary>The woven nest itself, with its sleepers curled up inside.</summary>
sealed class NestView : Control
{
    readonly NestWindow owner;
    int hoverSlot = -1;

    public NestView(NestWindow owner)
    {
        this.owner = owner;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.WindowBack;
    }

    RectangleF NestRect
    {
        get
        {
            float rx = Math.Min(Width * 0.38f, 170), ry = rx * 0.5f;
            float cx = Width / 2f, cy = Height / 2f + 8;
            return new RectangleF(cx - rx, cy - ry, rx * 2, ry * 2);
        }
    }

    // A generous area around the nest, in screen coordinates, for dropping a creature in.
    public RectangleF ScreenDropZone()
    {
        var r = RectangleF.Inflate(NestRect, 30, 45);
        var p = PointToScreen(new Point((int)r.X, (int)r.Y));
        return new RectangleF(p.X, p.Y, r.Width, r.Height);
    }

    PointF Slot(int i, int count)
    {
        var r = NestRect;
        var c = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
        if (count == 1) return c;
        double a = i * Math.PI * 2 / count - Math.PI / 2;
        return new PointF(c.X + (float)Math.Cos(a) * r.Width * 0.24f, c.Y + (float)Math.Sin(a) * r.Height * 0.22f);
    }

    static float SlotSize(int count) => count <= 2 ? 72 : count <= 4 ? 60 : 50;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Palette.WindowBack);

        var r = NestRect;
        float time = owner.Time;
        DrawNest(g, r, time, owner.World.NestHover);

        var sleepers = owner.Sleepers;
        float size = SlotSize(sleepers.Count);
        for (int i = 0; i < sleepers.Count; i++)
        {
            var c = sleepers[i];
            var slot = Slot(i, sleepers.Count);
            c.Appearance.DrawResting(g, slot, size, c.ShownComfort, time + i * 1.7f);
            if (i == hoverSlot)
            {
                using var ring = new Pen(Color.FromArgb(140, Palette.Line), 1.5f);
                g.DrawEllipse(ring, slot.X - size * 0.6f, slot.Y - size * 0.6f, size * 1.2f, size * 1.2f);
            }
        }

        using var muted = new SolidBrush(Palette.MutedText);
        using var small = new Font("Segoe UI", 9f);
        var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        if (sleepers.Count == 0)
        {
            g.DrawString("Carry a spider here and let go\nto put it to bed.", small, muted,
                new RectangleF(r.X, r.Y, r.Width, r.Height), centre);
        }

        string caption = hoverSlot >= 0 ? $"Click to wake {sleepers[hoverSlot].Name}"
                       : sleepers.Count == 0 ? "The nest is empty"
                       : sleepers.Count == 1 ? "1 asleep" : $"{sleepers.Count} asleep";
        g.DrawString(caption, small, muted, new RectangleF(0, Height - 28, Width, 22), centre);
    }

    // A woven bowl of silk strands in the same node style as the creatures and webs.
    static void DrawNest(Graphics g, RectangleF r, float time, bool hover)
    {
        var c = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);

        using (var shadow = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillEllipse(shadow, r.X - 10, r.Y + 14, r.Width + 20, r.Height + 10);
        using (var bowl = new SolidBrush(Palette.NodeFill))
            g.FillEllipse(bowl, r);

        // Strands looping round the rim; seeded so the nest looks the same every frame.
        var weave = new Random(11);
        for (int i = 0; i < 70; i++)
        {
            float f = 0.8f + (float)weave.NextDouble() * 0.28f;
            float start = (float)weave.NextDouble() * 360, sweep = 40 + (float)weave.NextDouble() * 90;
            int alpha = 45 + weave.Next(100);
            using var strand = new Pen(Color.FromArgb(alpha, Palette.Line), 0.8f + (float)weave.NextDouble() * 0.9f);
            float w = r.Width * f, h = r.Height * f;
            g.DrawArc(strand, c.X - w / 2, c.Y - h / 2, w, h, start, sweep);
        }

        // Knots around the rim.
        using var fill = new SolidBrush(Palette.NodeFill);
        using var ring = new Pen(Color.FromArgb(200, 255, 255, 255), 1f);
        for (int i = 0; i < 18; i++)
        {
            double a = i * Math.PI * 2 / 18;
            var p = new PointF(c.X + (float)Math.Cos(a) * r.Width / 2 * 0.96f, c.Y + (float)Math.Sin(a) * r.Height / 2 * 0.96f);
            g.FillEllipse(fill, p.X - 2, p.Y - 2, 4, 4);
            g.DrawEllipse(ring, p.X - 2, p.Y - 2, 4, 4);
        }

        // Glows while a creature is being carried over it.
        if (hover)
        {
            int alpha = (int)(110 + 70 * Math.Sin(time * 6));
            using var glow = new Pen(Color.FromArgb(alpha, Palette.Line), 3f);
            g.DrawEllipse(glow, RectangleF.Inflate(r, 10, 8));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var sleepers = owner.Sleepers;
        float size = SlotSize(sleepers.Count);
        hoverSlot = -1;
        for (int i = 0; i < sleepers.Count; i++)
        {
            var p = Slot(i, sleepers.Count);
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

/// <summary>A card of stats for each creature asleep in the nest.</summary>
sealed class StatsView : Control
{
    const int Pad = 14, Gap = 12, HeaderHeight = 54, RowHeight = 22, BarRowHeight = 32;

    readonly NestWindow owner;
    readonly Font nameFont = new("Segoe UI Semibold", 11f);
    readonly Font bodyFont = new("Segoe UI", 9f);

    public StatsView(NestWindow owner)
    {
        this.owner = owner;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.WindowBack;
    }

    static int CardHeight(List<StatLine> lines) =>
        HeaderHeight + lines.Sum(l => l.Bar.HasValue ? BarRowHeight : RowHeight) + Pad;

    public void UpdateLayout(Size available)
    {
        var sleepers = owner.Sleepers;
        int height = Pad + sleepers.Sum(c => CardHeight(c.Stats()) + Gap);
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

        using var text = new SolidBrush(Palette.Text);
        using var muted = new SolidBrush(Palette.MutedText);
        var sleepers = owner.Sleepers;

        if (sleepers.Count == 0)
        {
            var centre = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("Nobody is asleep in the nest yet.", bodyFont, muted, new RectangleF(0, 30, Width, 40), centre);
            return;
        }

        int y = Pad;
        foreach (var c in sleepers)
        {
            var lines = c.Stats();
            var card = new Rectangle(Pad, y, Width - Pad * 2, CardHeight(lines));
            using (var path = Rounded(card, 10))
            using (var fill = new SolidBrush(Palette.Card))
            using (var edge = new Pen(Palette.CardEdge))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }

            // Header: the creature curled up, its name and how it feels about you.
            c.Appearance.DrawResting(g, new PointF(card.X + 30, card.Y + 28), 36, c.ShownComfort, owner.Time);
            g.DrawString(c.Name, nameFont, text, card.X + 58, card.Y + 9);
            g.DrawString(c.MoodWord, bodyFont, muted, card.X + 59, card.Y + 31);

            int rowY = card.Y + HeaderHeight;
            var right = new StringFormat { Alignment = StringAlignment.Far };
            foreach (var line in lines)
            {
                g.DrawString(line.Label, bodyFont, muted, card.X + 16, rowY);
                g.DrawString(line.Value, bodyFont, text, new RectangleF(card.X, rowY, card.Width - 16, RowHeight), right);
                if (line.Bar is float amount)
                {
                    var track = new RectangleF(card.X + 16, rowY + 20, card.Width - 32, 5);
                    using var trackBrush = new SolidBrush(Palette.CardEdge);
                    using var barBrush = new SolidBrush(line.BarColor ?? Palette.Line);
                    g.FillRectangle(trackBrush, track);
                    g.FillRectangle(barBrush, track.X, track.Y, track.Width * Math.Clamp(amount, 0, 1), track.Height);
                    rowY += BarRowHeight;
                }
                else rowY += RowHeight;
            }

            y += card.Height + Gap;
        }
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            nameFont.Dispose();
            bodyFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
