using System.Numerics;
using System.Text;

namespace WebCrawler;

/// <summary>
/// Everything the spiders share: the cursor, the open windows, flies and webs.
/// </summary>
sealed class World : IDisposable
{
    public readonly CrawlerSettings Settings;
    public readonly Random Rng;
    public readonly float S;
    public readonly DesktopWindows Windows = new();
    public readonly List<Fly> Flies = new();
    public readonly List<Web> Webs = new();

    public Vector2 Cursor, CursorVel;
    Vector2 lastCursor;
    bool hasCursor;

    // A selection box being dragged out on the desktop or in a File Explorer window.
    public bool Selecting { get; private set; }
    public RectangleF SelectionRect { get; private set; }
    Vector2 selectStart;
    bool buttonWasDown, selectStartedOnSurface;
    readonly StringBuilder className = new(64);
    float flyTimer = 12f;

    public World(CrawlerSettings settings, Random rng, float scale)
    {
        Settings = settings;
        Rng = rng;
        S = scale;
    }

    public void Update(float dt)
    {
        var c = System.Windows.Forms.Cursor.Position;
        var cur = new Vector2(c.X, c.Y);
        if (hasCursor && dt > 0)
            CursorVel += ((cur - lastCursor) / dt - CursorVel) * Math.Min(1, dt * 12);
        lastCursor = cur;
        Cursor = cur;
        hasCursor = true;

        Windows.Update(dt);
        TrackSelection();

        if (Settings.Flies)
        {
            flyTimer -= dt;
            if (flyTimer <= 0)
            {
                flyTimer = 20 + (float)Rng.NextDouble() * 30;
                if (Flies.Count < 2) SpawnFly();
            }
        }

        for (int i = Flies.Count - 1; i >= 0; i--)
        {
            Flies[i].Update(dt);
            if (Flies[i].Caught || Flies[i].Gone)
            {
                Flies[i].Dispose();
                Flies.RemoveAt(i);
            }
        }

        for (int i = Webs.Count - 1; i >= 0; i--)
        {
            Webs[i].Update(dt, Cursor);
            if (Webs[i].Dead)
            {
                Webs[i].Dispose();
                Webs.RemoveAt(i);
            }
        }
    }

    public void Render()
    {
        foreach (var web in Webs)
        {
            web.SetHidden(IsBlocked(web.Hub));
            web.Render();
        }
        foreach (var fly in Flies) fly.Render();
    }

    public void SpawnFly()
    {
        if (UsableScreens().Length > 0) Flies.Add(new Fly(this));
    }

    public void ClearWebs()
    {
        foreach (var web in Webs) web.Dispose();
        Webs.Clear();
    }

    void TrackSelection()
    {
        bool down = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;
        if (down && !buttonWasDown)
        {
            selectStart = Cursor;
            selectStartedOnSurface = CanBoxSelectAt(Cursor);
        }
        buttonWasDown = down;

        Selecting = down && selectStartedOnSurface && Vector2.Distance(Cursor, selectStart) > 4;
        if (Selecting)
            SelectionRect = RectangleF.FromLTRB(
                Math.Min(selectStart.X, Cursor.X), Math.Min(selectStart.Y, Cursor.Y),
                Math.Max(selectStart.X, Cursor.X), Math.Max(selectStart.Y, Cursor.Y));
    }

    // Places where dragging draws a selection box: the desktop and File Explorer windows.
    bool CanBoxSelectAt(Vector2 p)
    {
        var h = Native.WindowFromPoint(new Native.POINT((int)p.X, (int)p.Y));
        if (h == IntPtr.Zero) return false;
        var root = Native.GetAncestor(h, Native.GA_ROOT);
        Native.GetWindowThreadProcessId(root, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        className.Clear();
        Native.GetClassName(root, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW" or "CabinetWClass";
    }

    /// <summary>On a screen that has something fullscreen on it.</summary>
    public bool IsBlocked(Vector2 p) => Windows.IsBlocked(p);

    /// <summary>On a screen, and not one with something fullscreen on it.</summary>
    public bool IsUsable(Vector2 p) => Windows.IsUsable(p);

    public Screen[] UsableScreens() => Windows.UsableScreens();

    public static bool OnAnyScreen(Vector2 p)
    {
        foreach (var sc in Screen.AllScreens)
            if (sc.Bounds.Contains((int)p.X, (int)p.Y)) return true;
        return false;
    }

    public void Dispose()
    {
        foreach (var fly in Flies) fly.Dispose();
        Flies.Clear();
        ClearWebs();
    }
}

/// <summary>
/// The visible, normal app windows on the desktop, front to back, and which screens
/// have something fullscreen on them. Refreshed twice a second.
/// </summary>
sealed class DesktopWindows
{
    public readonly List<Rectangle> Rects = new();
    public readonly List<Rectangle> Fullscreen = new();
    readonly StringBuilder className = new(64);
    float timer;

    public void Update(float dt)
    {
        timer -= dt;
        if (timer > 0) return;
        timer = 0.5f;
        Refresh();
    }

    void Refresh()
    {
        Rects.Clear();
        Fullscreen.Clear();
        var screens = Screen.AllScreens;
        uint myPid = (uint)Environment.ProcessId;

        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == myPid) return true;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            // Tool windows and click-through overlays aren't things people are looking at.
            if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT)) != 0) return true;
            if (Native.DwmGetWindowAttributeInt(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (Native.GetWindowTextLength(h) == 0) return true;

            className.Clear();
            Native.GetClassName(h, className, className.Capacity);
            string cls = className.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;

            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, 16) != 0)
                Native.GetWindowRect(h, out r);
            var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

            // Covering a whole monitor, taskbar area included, means fullscreen. A maximised
            // window with a title bar can do that too when the taskbar auto-hides, so skip those.
            long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64();
            bool maximisedWithTitle = Native.IsZoomed(h) && (style & Native.WS_CAPTION) == Native.WS_CAPTION;
            if (!maximisedWithTitle)
                foreach (var sc in screens)
                    if (rect.Contains(sc.Bounds) && !Fullscreen.Contains(sc.Bounds)) Fullscreen.Add(sc.Bounds);

            if (rect.Width >= 160 && rect.Height >= 100) Rects.Add(rect);
            return true;
        }, IntPtr.Zero);
    }

    public bool IsBlocked(Vector2 p)
    {
        foreach (var b in Fullscreen)
            if (b.Contains((int)p.X, (int)p.Y)) return true;
        return false;
    }

    public bool IsUsable(Vector2 p) => World.OnAnyScreen(p) && !IsBlocked(p);

    public Screen[] UsableScreens() => Screen.AllScreens.Where(sc => !Fullscreen.Contains(sc.Bounds)).ToArray();

    /// <summary>A stretch of a window border to walk along, ending at a corner.</summary>
    public bool TryPickEdge(Random rng, out Vector2 from, out Vector2 to, out Vector2 inward)
    {
        from = to = inward = default;
        if (Rects.Count == 0) return false;
        var r = Rects[rng.Next(Math.Min(5, Rects.Count))];

        Vector2 a, b;
        double roll = rng.NextDouble();
        if (roll < 0.5) { a = new(r.Left, r.Top + 1); b = new(r.Right, r.Top + 1); }          // title bar
        else if (roll < 0.7) { a = new(r.Left + 1, r.Top); b = new(r.Left + 1, r.Bottom); }   // left side
        else if (roll < 0.9) { a = new(r.Right - 1, r.Top); b = new(r.Right - 1, r.Bottom); } // right side
        else { a = new(r.Left, r.Bottom - 1); b = new(r.Right, r.Bottom - 1); }               // bottom
        if (rng.NextDouble() < 0.5) (a, b) = (b, a);

        from = Vector2.Lerp(a, b, (float)rng.NextDouble() * 0.6f);
        to = b;
        var center = new Vector2(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
        inward = Vector2.Normalize(new Vector2(MathF.Sign(center.X - b.X), MathF.Sign(center.Y - b.Y)));
        return IsUsable(from) && IsUsable(to);
    }

    /// <summary>A corner to tuck a web into: a window corner or a corner of a screen.</summary>
    public bool TryPickCorner(Random rng, out Vector2 corner, out Vector2 inward)
    {
        corner = inward = default;
        Rectangle r;
        var usable = UsableScreens();
        if (Rects.Count > 0 && rng.NextDouble() < 0.6) r = Rects[rng.Next(Math.Min(5, Rects.Count))];
        else if (usable.Length > 0) r = usable[rng.Next(usable.Length)].WorkingArea;
        else return false;

        int which = rng.Next(4);
        corner = which switch
        {
            0 => new Vector2(r.Left, r.Top),
            1 => new Vector2(r.Right, r.Top),
            2 => new Vector2(r.Left, r.Bottom),
            _ => new Vector2(r.Right, r.Bottom),
        };
        inward = Vector2.Normalize(new Vector2(which % 2 == 0 ? 1 : -1, which < 2 ? 1 : -1));
        return IsUsable(corner + inward * 4);
    }
}
