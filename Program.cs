using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace WebCrawler;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "WebCrawler.SingleInstance", out bool first);
        if (!first) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new CrawlerContext());
    }
}

sealed class CrawlerContext : ApplicationContext
{
    const int MaxSpiders = 6;
    const float EscHoldToQuit = 1f;

    readonly CrawlerSettings settings = new();
    readonly List<Spider> spiders = new();
    readonly Random rng = new();
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly float scale;
    readonly World world;
    readonly SavedState saved = SavedState.Load();
    float saveTimer = 30, tooltipTimer;
    readonly ToolStripMenuItem pauseItem;
    double last;
    float escHeld;

    public CrawlerContext()
    {
        using (var screen = Graphics.FromHwnd(IntPtr.Zero))
            scale = screen.DpiX / 96f * 1.15f;
        world = new World(settings, rng, scale);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Add spider", null, (_, _) => AddSpider());
        menu.Items.Add("Remove spider", null, (_, _) => RemoveSpider());
        menu.Items.Add(new ToolStripSeparator());

        var chase = new ToolStripMenuItem("Affection (follow, tap, rest)") { Checked = settings.Chase, CheckOnClick = true };
        chase.CheckedChanged += (_, _) => settings.Chase = chase.Checked;
        menu.Items.Add(chase);

        var flies = new ToolStripMenuItem("Flies") { Checked = settings.Flies, CheckOnClick = true };
        flies.CheckedChanged += (_, _) => settings.Flies = flies.Checked;
        menu.Items.Add(flies);
        menu.Items.Add("Release a fly", null, (_, _) => world.SpawnFly());
        menu.Items.Add("Clear webs", null, (_, _) => world.ClearWebs());

        var intensity = new ToolStripMenuItem("Glitch intensity");
        foreach (var (name, value) in new[] { ("Low", 0.5f), ("Medium", 1f), ("High", 1.7f) })
        {
            var item = new ToolStripMenuItem(name) { Checked = value == settings.Intensity, Tag = value };
            item.Click += (_, _) =>
            {
                settings.Intensity = (float)item.Tag;
                foreach (ToolStripMenuItem other in intensity.DropDownItems) other.Checked = other == item;
            };
            intensity.DropDownItems.Add(item);
        }
        menu.Items.Add(intensity);

        pauseItem = new ToolStripMenuItem("Pause") { CheckOnClick = true };
        pauseItem.CheckedChanged += (_, _) => settings.Paused = pauseItem.Checked;
        menu.Items.Add(pauseItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Web Crawler",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => AddSpider();

        AddSpider();

        timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += OnTick;
        timer.Start();

        tray.ShowBalloonTip(4000, "Web Crawler",
            "A spider is loose on your screen. Hold Esc for 1 second to get rid of it, or right-click the tray icon.",
            ToolTipIcon.Info);
    }

    void OnTick(object sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.05, now - last);
        last = now;

        if ((Native.GetAsyncKeyState(Native.VK_ESCAPE) & 0x8000) != 0)
        {
            escHeld += dt;
            if (escHeld >= EscHoldToQuit) { Quit(); return; }
        }
        else escHeld = 0;

        if (settings.Paused) return;

        saveTimer -= dt;
        if (saveTimer <= 0) { saveTimer = 30; SaveState(); }
        tooltipTimer -= dt;
        if (tooltipTimer <= 0)
        {
            tooltipTimer = 1;
            tray.Text = spiders.Count > 0 ? $"Web Crawler: comfort {spiders[0].Comfort:P0}" : "Web Crawler";
        }

        world.Update(dt);
        foreach (var spider in spiders) spider.Update(dt);
        world.Render();
        foreach (var spider in spiders) spider.Render();
    }

    void AddSpider()
    {
        if (spiders.Count >= MaxSpiders) return;
        int slot = spiders.Count;
        var memory = slot < saved.Spiders.Count ? saved.Spiders[slot] : new SpiderMemory();
        spiders.Add(new Spider(world, memory));
    }

    void SaveState()
    {
        // Keep entries for spiders that were removed so re-adding one brings it back as it was.
        for (int i = 0; i < spiders.Count; i++)
        {
            if (i < saved.Spiders.Count) saved.Spiders[i] = spiders[i].Memory;
            else saved.Spiders.Add(spiders[i].Memory);
        }
        saved.Save();
    }

    void RemoveSpider()
    {
        if (spiders.Count == 0) return;
        SaveState();
        var last = spiders[^1];
        spiders.RemoveAt(spiders.Count - 1);
        last.Dispose();
    }

    void Quit()
    {
        timer.Stop();
        SaveState();
        foreach (var spider in spiders) spider.Dispose();
        spiders.Clear();
        world.Dispose();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var leg = new Pen(Color.White, 2f);
            float[] ys = { 8, 13, 19, 24 };
            foreach (var y in ys)
            {
                g.DrawLines(leg, new PointF[] { new(13, 16), new(7, y - 4), new(2, y) });
                g.DrawLines(leg, new PointF[] { new(19, 16), new(25, y - 4), new(30, y) });
            }
            using var body = new SolidBrush(Color.FromArgb(14, 14, 20));
            using var outline = new Pen(Palette.Pink, 2f);
            g.FillEllipse(body, 10, 9, 12, 15);
            g.DrawEllipse(outline, 10, 9, 12, 15);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
