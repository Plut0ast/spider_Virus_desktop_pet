using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace WebCrawler;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool openNest = args.Any(a => a.Equals("--nest", StringComparison.OrdinalIgnoreCase));

        // Launching it again while it's already running opens the nest instead.
        using var nestSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "WebCrawler.OpenNest");
        using var mutex = new Mutex(true, "WebCrawler.SingleInstance", out bool first);
        if (!first)
        {
            nestSignal.Set();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new CrawlerContext(nestSignal, openNest));
    }
}

sealed class CrawlerContext : ApplicationContext
{
    const int MaxCreatures = 6;
    const float EscHoldToQuit = 1f;

    readonly CrawlerSettings settings = new();
    readonly List<Creature> creatures = new();
    readonly Random rng = new();
    readonly NotifyIcon tray;
    readonly Icon icon;
    readonly System.Windows.Forms.Timer timer;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly float scale;
    readonly World world;
    readonly EventWaitHandle nestSignal;
    readonly ToolStripMenuItem pauseItem;
    SavedState saved = SavedState.Load();
    NestWindow nest;
    float saveTimer = 30, tooltipTimer;
    double last;
    float escHeld;

    public CrawlerContext(EventWaitHandle nestSignal, bool openNest)
    {
        this.nestSignal = nestSignal;
        using (var screen = Graphics.FromHwnd(IntPtr.Zero))
            scale = screen.DpiX / 96f * 1.15f;
        world = new World(settings, rng, scale);
        icon = MakeIcon();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open nest", null, (_, _) => OpenNest());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Add spider", null, (_, _) => AddCreature());
        menu.Items.Add("Remove spider", null, (_, _) => RemoveCreature());
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
        menu.Items.Add("About your spider", null, (_, _) => ShowAbout());
        menu.Items.Add("Forget everything", null, (_, _) => ForgetEverything());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        tray = new NotifyIcon
        {
            Icon = icon,
            Text = "Web Crawler",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => OpenNest();

        // Webs first, so a spider that was asleep in one comes back asleep in it.
        world.LoadWebs(saved.Webs);
        int count = Math.Clamp(saved.Spiders.Count(m => m.X != null || m.InNest), 1, MaxCreatures);
        for (int i = 0; i < count; i++) AddCreature();

        // Save if Windows shuts down or you sign out while it's running.
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;

        timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += OnTick;
        timer.Start();

        if (openNest) OpenNest();
        else
            tray.ShowBalloonTip(4000, "Web Crawler",
                "A spider is loose on your screen. Hold Esc for 1 second to get rid of it, or right-click the tray icon.",
                ToolTipIcon.Info);
    }

    void OnTick(object sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.05, now - last);
        last = now;

        if (nestSignal.WaitOne(0)) OpenNest();

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
            string text = creatures.Count > 0 ? $"{creatures[0].Name}: comfort {creatures[0].Comfort:P0}" : "Web Crawler";
            // The tray tooltip can't be longer than 63 characters.
            tray.Text = text.Length > 63 ? text[..63] : text;
        }

        world.NestHover = world.NestZone is RectangleF zone
                          && creatures.Any(c => c.IsHeld)
                          && zone.Contains(world.Cursor.X, world.Cursor.Y);

        world.Update(dt);
        foreach (var c in creatures) c.Update(dt);
        world.Render();
        foreach (var c in creatures) c.Render();
    }

    void OpenNest()
    {
        if (nest == null || nest.IsDisposed)
        {
            nest = new NestWindow(world, () => creatures, icon);
            nest.FormClosed += (_, _) => nest = null;
            // Save a rename straight away rather than waiting for the next autosave.
            nest.Renamed += SaveState;
            nest.Show();
        }
        else
        {
            if (nest.WindowState == FormWindowState.Minimized) nest.WindowState = FormWindowState.Normal;
            nest.Show();
        }
        nest.Activate();
    }

    void AddCreature()
    {
        if (creatures.Count >= MaxCreatures) return;
        int slot = creatures.Count;
        var memory = slot < saved.Spiders.Count ? saved.Spiders[slot] : new CreatureMemory();
        var creature = CreatureFactory.Create(world, memory);
        if (string.IsNullOrEmpty(creature.Name)) creature.Name = $"{creature.Appearance.DisplayName} {slot + 1}";
        creatures.Add(creature);
    }

    void RemoveCreature()
    {
        if (creatures.Count == 0) return;
        SaveState();
        var leaving = creatures[^1];
        creatures.RemoveAt(creatures.Count - 1);
        leaving.Dispose();
    }

    void SaveState()
    {
        var webs = world.SaveableWebs();
        saved.Webs = webs.Select(w => w.ToMemory()).ToList();

        // Keep entries for creatures that were removed so re-adding one brings it back as it was,
        // but they're no longer out, so drop where they were.
        for (int i = creatures.Count; i < saved.Spiders.Count; i++)
        {
            saved.Spiders[i].X = saved.Spiders[i].Y = null;
            saved.Spiders[i].Asleep = false;
            saved.Spiders[i].InNest = false;
            saved.Spiders[i].SleepingInWeb = -1;
        }
        for (int i = 0; i < creatures.Count; i++)
        {
            var memory = creatures[i].GetMemory(webs);
            if (i < saved.Spiders.Count) saved.Spiders[i] = memory;
            else saved.Spiders.Add(memory);
        }
        saved.Save();
    }

    void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e) => SaveState();

    void ShowAbout()
    {
        string text = creatures.Count > 0 ? creatures[0].Summary() : "No spider is out right now.";
        MessageBox.Show(text, "Your spider", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void ForgetEverything()
    {
        var answer = MessageBox.Show(
            "Your spiders will forget you and start over as wary strangers, and their webs will be cleared. This can't be undone.",
            "Forget everything?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        int count = Math.Max(1, creatures.Count);
        foreach (var c in creatures) c.Dispose();
        creatures.Clear();
        world.ClearWebs();
        saved = new SavedState();
        saved.Save();
        for (int i = 0; i < count; i++) AddCreature();
    }

    void Quit()
    {
        timer.Stop();
        Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
        SaveState();
        nest?.Close();
        foreach (var c in creatures) c.Dispose();
        creatures.Clear();
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
