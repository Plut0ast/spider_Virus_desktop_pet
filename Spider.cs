using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace WebCrawler;

sealed class CrawlerSettings
{
    public float Intensity = 1f;
    public bool Chase = true;
    public bool Flies = true;
    public bool Paused;
    public float Speed = 130f;
}

sealed class Leg
{
    public int Side, K, Group;
    public float BaseAngle, Reach;
    public Vector2 Foot, From, To;
    public bool Stepping;
    public float T, StepDur, Lift, LiftH, Idle;
}

enum Mode
{
    Wander,  // roaming to random spots
    Chase,   // following the cursor
    Edge,    // walking along a window border
    Hide,    // tucked into a corner
    Groom,   // rubbing its front legs together
    Flee,    // running from the cursor
    Held,    // picked up by the cursor
    Thrown,  // flying through the air after a throw or a startled hop
    Dizzy,   // wobbling after a hard landing
    Spin,    // building and sitting in a web
    Hunt,    // stalking a fly
    Wrap,    // wrapping a caught fly in silk
    Sleep,   // curled up after being box-selected; a press wakes it
}

/// <summary>
/// One spider: walks the desktop with an alternating gait, glitches what its feet land on,
/// and reacts to the cursor, windows, flies and its own webs.
/// </summary>
sealed class Spider : IDisposable
{
    const int WinSize = 560;
    const float StepDist = 18f;
    const int MaxWebs = 4;
    static readonly float[] LegAngles = { 0.6f, 1.2f, 1.95f, 2.5f };
    static readonly float[] LegReach = { 52f, 44f, 38f, 48f };
    static readonly float[] HipAlong = { 7f, 5f, 3f, 1f };

    // Fake 3D: height shifts a point up the screen, like a camera tilted slightly forward.
    const float Tilt = 0.5f;
    // The last leg segment (metatarsus) rises from the foot at about 70 degrees.
    const float AnkleAngle = 1.2f;

    readonly World world;
    readonly Overlay win = new();
    readonly Bitmap canvas = new(WinSize, WinSize, PixelFormat.Format32bppPArgb);
    readonly Graphics g;
    readonly Font labelFont;
    readonly Random rng;
    readonly CrawlerSettings settings;
    readonly float s;
    readonly Leg[] legs = new Leg[8];
    readonly List<Glitch> glitches = new();
    readonly List<Vector2> trail = new();

    Vector2 pos, vel, target;
    float heading, angVel, time, pauseLeft, ambientTimer, trailTimer, topTimer;

    Mode mode = Mode.Wander;
    float modeLeft;
    float speedMul = 1f;
    float extraH, hVel, spinVel, reachMul = 1f;
    Mode afterLanding;

    bool pressed;
    float pressTime;
    Vector2 pressCursor, grabOffset;

    Vector2 edgeEnd, cornerInward;

    Web web;
    int spinStage;
    Vector2 spinHub;
    float spinRadius, webCooldown, huntCooldown = 5f;
    Fly prey;

    // Every screen has something fullscreen on it, so the spider has stepped out of sight.
    bool away;

    float selectedFor;   // how long a selection box has been covering it
    bool sleepAfterSpin; // spinning a quick web to sleep in
    Web sleepWeb;        // the web it is asleep in, if any
    float startledFlash; // the "!" shown after being woken

    public Spider(World world)
    {
        this.world = world;
        settings = world.Settings;
        rng = world.Rng;
        s = world.S;
        g = Graphics.FromImage(canvas);
        labelFont = new Font("Consolas", 6f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        webCooldown = 20 + (float)rng.NextDouble() * 25;

        for (int i = 0; i < 8; i++)
        {
            int side = i < 4 ? -1 : 1, k = i % 4;
            var leg = new Leg
            {
                Side = side,
                K = k,
                Group = (k + (side > 0 ? 1 : 0)) % 2,
                BaseAngle = side * LegAngles[k],
                Reach = LegReach[k],
            };
            legs[i] = leg;
        }

        var screens = world.UsableScreens();
        EnterFrom(screens.Length > 0 ? screens[rng.Next(screens.Length)] : Screen.AllScreens[0]);

        win.Cursor = Cursors.Hand;
        win.MouseDown += OnMouseDown;
        win.MouseUp += OnMouseUp;
        win.Show();
    }

    public void KeepOnTop() => win.KeepOnTop();

    // Walk in from just past the left or right edge of a screen.
    void EnterFrom(Screen screen)
    {
        var b = screen.Bounds;
        bool left = rng.NextDouble() < 0.5;
        pos = new Vector2(left ? b.Left - 40 * s : b.Right + 40 * s, b.Top + b.Height * (0.15f + (float)rng.NextDouble() * 0.7f));
        heading = left ? 0 : MathF.PI;
        target = new Vector2(b.Left + b.Width * (left ? 0.3f : 0.7f), pos.Y);
        vel = Vector2.Zero;
        mode = Mode.Wander;
        speedMul = 1;
        extraH = 0;
        trail.Clear();
        foreach (var leg in legs)
        {
            leg.Foot = RestAt(leg, pos, heading);
            leg.Lift = 0;
            leg.Stepping = false;
        }
    }

    // A screen just went fullscreen under the spider: hop to a free screen, or vanish if none.
    void LeaveBlockedScreen()
    {
        DropTasks();
        pressed = false;
        foreach (var gl in glitches) gl.Dispose();
        glitches.Clear();

        var usable = world.UsableScreens();
        if (usable.Length == 0)
        {
            away = true;
            win.Hide();
            return;
        }
        EnterFrom(usable[rng.Next(usable.Length)]);
    }

    // ---------- main loop ----------

    public void Update(float dt)
    {
        if (away)
        {
            var usable = world.UsableScreens();
            if (usable.Length == 0) return;
            away = false;
            EnterFrom(usable[rng.Next(usable.Length)]);
            win.Show();
        }
        else if (mode != Mode.Held && world.IsBlocked(pos))
        {
            LeaveBlockedScreen();
            if (away) return;
        }

        time += dt;
        webCooldown -= dt;
        huntCooldown -= dt;
        startledFlash -= dt;
        CheckSelectionBox(dt);
        float prevHeading = heading;

        HandleInput();

        if (mode == Mode.Held) UpdateHeld(dt);
        else if (mode == Mode.Thrown) UpdateThrown(dt);
        else
        {
            Think(dt);
            Walk(dt);
            float wantH = mode switch { Mode.Hide => -5 * s, Mode.Sleep => sleepWeb != null ? -2 * s : -6 * s, Mode.Groom => 2 * s, _ => 0 };
            extraH += (wantH - extraH) * Math.Min(1, dt * 6);
        }

        float wantReach = mode switch { Mode.Hide => 0.8f, Mode.Sleep => sleepWeb != null ? 0.9f : 0.55f, _ => 1f };
        reachMul += (wantReach - reachMul) * Math.Min(1, dt * 4);

        float turnRate = WrapAngle(heading - prevHeading) / Math.Max(dt, 1e-4f);
        angVel += (turnRate - angVel) * Math.Min(1, dt * 10);

        if (mode is Mode.Held or Mode.Thrown) UpdateLegsAirborne(dt);
        else UpdateLegs(dt);

        trailTimer -= dt;
        if (trailTimer <= 0)
        {
            trailTimer = 0.03f;
            trail.Add(pos);
            if (trail.Count > 70) trail.RemoveAt(0);
        }

        ambientTimer -= dt;
        if (ambientTimer <= 0)
        {
            ambientTimer = 0.18f;
            bool active = vel.Length() > 10 * s && mode is not (Mode.Held or Mode.Thrown or Mode.Spin);
            if (active && rng.NextDouble() < 0.12 * settings.Intensity)
                SpawnGlitch(pos + RandomInDisk(50 * s));
        }

        for (int i = glitches.Count - 1; i >= 0; i--)
        {
            glitches[i].Update(dt);
            if (glitches[i].Dead)
            {
                glitches[i].Dispose();
                glitches.RemoveAt(i);
            }
        }
    }

    // ---------- decisions ----------

    void Think(float dt)
    {
        if (mode == Mode.Sleep)
        {
            // Tearing the web it's sleeping in wakes it with a jolt.
            if (sleepWeb != null && (sleepWeb.Disturbed || !world.Webs.Contains(sleepWeb)))
            {
                startledFlash = 0.8f;
                Startle();
            }
            return;
        }

        // Sweeping a selection box over it shouldn't scare it off.
        bool drowsy = mode == Mode.Spin && sleepAfterSpin;
        if (mode is not (Mode.Flee or Mode.Dizzy) && !drowsy && !world.Selecting && CursorThreat())
        {
            StartFlee(1.1f);
            return;
        }

        switch (mode)
        {
            case Mode.Wander:
                speedMul = 1;
                if (TryStartHunt() || TryStartSpin()) return;
                if (pauseLeft <= 0 && Vector2.Distance(pos, target) < 12 * s)
                {
                    double r = rng.NextDouble();
                    if (r < 0.15) { StartGroom(); return; }
                    if (r < 0.4) pauseLeft = 0.3f + (float)rng.NextDouble() * 1.2f;
                    PickTarget();
                }
                break;

            case Mode.Chase:
            {
                modeLeft -= dt;
                var away = pos - world.Cursor;
                float d = away.Length();
                target = d > 1 ? world.Cursor + away / d * 28 * s : world.Cursor;
                // Don't follow the cursor onto a fullscreen screen.
                if (modeLeft <= 0 || world.IsBlocked(world.Cursor)) PickTarget();
                break;
            }

            case Mode.Edge:
                if (TryStartHunt()) return;
                if (Vector2.Distance(pos, target) < 10 * s)
                {
                    if (Vector2.Distance(target, edgeEnd) < 1)
                    {
                        if (rng.NextDouble() < 0.5) StartHide();
                        else PickTarget();
                    }
                    else target = StepToward(target, edgeEnd, (90 + (float)rng.NextDouble() * 120) * s);
                }
                break;

            case Mode.Hide:
            {
                // Face out of the corner, toward the open screen.
                float want = MathF.Atan2(cornerInward.Y, cornerInward.X);
                heading += WrapAngle(want - heading) * Math.Min(1, dt * 4);
                modeLeft -= dt;
                if (modeLeft <= 0) PickTarget();
                break;
            }

            case Mode.Groom:
                modeLeft -= dt;
                if (modeLeft <= 0) PickTarget();
                break;

            case Mode.Flee:
            {
                modeLeft -= dt;
                var away = pos - world.Cursor;
                float d = away.Length();
                target = pos + (d > 1 ? away / d : Dir(heading)) * 220 * s;
                speedMul = 2.3f;
                if (modeLeft <= 0) PickTarget();
                break;
            }

            case Mode.Dizzy:
                modeLeft -= dt;
                heading += MathF.Sin(time * 7) * dt * 5;
                if (modeLeft <= 0) StartFlee(0.9f);
                break;

            case Mode.Spin:
                UpdateSpin(dt);
                break;

            case Mode.Hunt:
                UpdateHunt();
                break;

            case Mode.Wrap:
                modeLeft -= dt;
                heading += MathF.Sin(time * 11) * dt * 1.5f;
                if (modeLeft <= 0) FinishWrap();
                break;
        }
    }

    void PickTarget()
    {
        mode = Mode.Wander;
        speedMul = 1;
        double r = rng.NextDouble();

        if (settings.Chase && r < 0.12)
        {
            mode = Mode.Chase;
            modeLeft = 3 + (float)rng.NextDouble() * 4;
            return;
        }

        if (r < 0.5 && world.Windows.TryPickEdge(rng, out var from, out var to, out var inward))
        {
            mode = Mode.Edge;
            target = from;
            edgeEnd = to;
            cornerInward = inward;
            return;
        }

        var screens = world.UsableScreens();
        if (screens.Length == 0) return;
        for (int tries = 0; tries < 20; tries++)
        {
            Vector2 c;
            if (rng.NextDouble() < 0.65)
            {
                float a = (float)(rng.NextDouble() * Math.PI * 2);
                float d = (120 + (float)rng.NextDouble() * 400) * s;
                c = pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
            }
            else
            {
                var b = screens[rng.Next(screens.Length)].Bounds;
                c = new Vector2(b.Left + (float)rng.NextDouble() * b.Width, b.Top + (float)rng.NextDouble() * b.Height);
            }

            foreach (var sc in screens)
            {
                var b = Rectangle.Inflate(sc.Bounds, (int)(-30 * s), (int)(-30 * s));
                if (b.Contains((int)c.X, (int)c.Y)) { target = c; return; }
            }
        }
        var p = screens[0].Bounds;
        target = new Vector2(p.Left + p.Width / 2f, p.Top + p.Height / 2f);
    }

    // A fast cursor heading straight at the spider scares it off.
    bool CursorThreat()
    {
        var toMe = pos - world.Cursor;
        float d = toMe.Length();
        if (d > 220 * s || d < 1) return false;
        var v = world.CursorVel;
        float speed = v.Length();
        if (speed < 1400 * s) return false;
        return Vector2.Dot(v / speed, toMe / d) > 0.6f;
    }

    void StartFlee(float seconds)
    {
        DropTasks();
        mode = Mode.Flee;
        modeLeft = seconds;
        pauseLeft = 0;
    }

    // Holding a selection box over the spider for a moment, or letting go with it
    // inside the box, sends it to sleep.
    void CheckSelectionBox(float dt)
    {
        if (mode is Mode.Held or Mode.Thrown or Mode.Sleep || (mode == Mode.Spin && sleepAfterSpin)) { selectedFor = 0; return; }
        var body = BodyOnScreen();
        bool inside = world.SelectionRect.Contains(body.X, body.Y) || world.SelectionRect.Contains(pos.X, pos.Y);

        if (world.SelectionReleased && inside) { StartSleep(); return; }
        selectedFor = world.Selecting && inside ? selectedFor + dt : 0;
        if (selectedFor > 0.25f) StartSleep();
    }

    void StartSleep()
    {
        selectedFor = 0;
        if (mode == Mode.Spin && sleepAfterSpin) return;
        DropTasks();
        pauseLeft = 0;
        vel = Vector2.Zero;

        float radius = (50 + (float)rng.NextDouble() * 20) * s;
        if (world.Windows.CoveredPoints(pos, radius) == 0)
        {
            // On open desktop: spin a quick web on the spot, then curl up in the middle of it.
            if (world.Webs.Count >= MaxWebs)
            {
                world.Webs[0].Dispose();
                world.Webs.RemoveAt(0);
            }
            web = new Web(pos, radius, rng, s);
            world.Webs.Add(web);
            web.Render();
            win.KeepOnTop();
            mode = Mode.Spin;
            spinStage = 1;
            sleepAfterSpin = true;
            return;
        }
        mode = Mode.Sleep;
    }

    void StartGroom()
    {
        mode = Mode.Groom;
        modeLeft = 1.5f + (float)rng.NextDouble() * 1.5f;
    }

    void StartHide()
    {
        mode = Mode.Hide;
        modeLeft = 3 + (float)rng.NextDouble() * 5;
    }

    // Walking away from a web in progress or a fly being stalked.
    void DropTasks()
    {
        if (prey != null) { prey.Hunter = null; prey = null; }
        web = null;
        spinStage = 0;
        sleepAfterSpin = false;
        sleepWeb = null;
    }

    // ---------- webs ----------

    bool TryStartSpin()
    {
        if (webCooldown > 0 || world.Webs.Count >= MaxWebs) return false;
        webCooldown = 5; // try again soon if no good spot turns up
        float radius = (55 + (float)rng.NextDouble() * 45) * s;
        if (!world.Windows.TryFindWebSpot(rng, radius, out var hub)) return false;

        spinHub = hub;
        spinRadius = radius;
        spinStage = 0;
        mode = Mode.Spin;
        target = hub;
        speedMul = 1.2f;
        return true;
    }

    void UpdateSpin(float dt)
    {
        if (web != null && (!world.Webs.Contains(web) || web.Disturbed))
        {
            // Someone poked or cleared its web: get out of there.
            StartFlee(1.2f);
            return;
        }

        switch (spinStage)
        {
            case 0: // walking to the hub
                if (Vector2.Distance(pos, spinHub) < 10 * s)
                {
                    web = new Web(spinHub, spinRadius, rng, s);
                    world.Webs.Add(web);
                    web.Render();
                    win.KeepOnTop();
                    spinStage = 1;
                }
                break;

            case 1: // turning on the spot while laying silk
                heading += dt * 1.6f;
                web.SetProgress(web.Progress + dt / (sleepAfterSpin ? 3.5f : 7f));
                if (web.Progress >= 1 && sleepAfterSpin)
                {
                    sleepWeb = web;
                    web = null;
                    spinStage = 0;
                    sleepAfterSpin = false;
                    mode = Mode.Sleep;
                    return;
                }
                if (web.Progress >= 1)
                {
                    spinStage = 2;
                    modeLeft = 6 + (float)rng.NextDouble() * 8;
                }
                break;

            case 2: // sitting in the middle, waiting
                modeLeft -= dt;
                if (TryStartHunt()) { web = null; spinStage = 0; return; }
                if (modeLeft <= 0)
                {
                    web = null;
                    spinStage = 0;
                    webCooldown = 70 + (float)rng.NextDouble() * 80;
                    PickTarget();
                }
                break;
        }
    }

    // ---------- hunting ----------

    Vector2 Mouth => pos + Dir(heading) * 14 * s;

    bool TryStartHunt()
    {
        if (huntCooldown > 0) return false;
        Fly best = null;
        float bestDist = 450 * s;
        foreach (var f in world.Flies)
        {
            if (f.Caught || f.Gone || (f.Hunter != null && f.Hunter != this)) continue;
            float d = Vector2.Distance(pos, f.Pos);
            if (d < bestDist) { best = f; bestDist = d; }
        }
        if (best == null) return false;

        prey = best;
        prey.Hunter = this;
        mode = Mode.Hunt;
        return true;
    }

    void UpdateHunt()
    {
        if (prey == null || prey.Caught || prey.Gone)
        {
            prey = null;
            PickTarget();
            return;
        }

        target = prey.Pos;
        float d = Vector2.Distance(Mouth, prey.Pos);
        // Creep up, then pounce.
        speedMul = d < 90 * s ? 2.6f : 1f;

        if (d > 600 * s)
        {
            prey.Hunter = null;
            prey = null;
            huntCooldown = 8;
            PickTarget();
            return;
        }

        if (d < 16 * s || Vector2.Distance(pos, prey.Pos) < 16 * s)
        {
            prey.Caught = true;
            prey = null;
            mode = Mode.Wrap;
            modeLeft = 1.4f;
            vel *= 0.2f;
        }
    }

    void FinishWrap()
    {
        Web home = null;
        foreach (var w in world.Webs)
            if (w.Progress >= 1 && w.Holds(pos, 200 * s)) { home = w; break; }

        if (home != null)
            home.AddBundle(home.Hub + RandomInDisk(home.Radius * 0.6f));
        else
            // No web nearby, so it eats the fly and the screen glitches as it goes down.
            for (int i = 0; i < 3; i++)
                glitches.Add(Glitch.Spawn(rng, Mouth + RandomInDisk(20 * s), s));

        huntCooldown = 12 + (float)rng.NextDouble() * 10;
        PickTarget();
    }

    // ---------- grabbing and throwing ----------

    void HandleInput()
    {
        bool near = Vector2.Distance(world.Cursor, BodyOnScreen()) < 22 * s;
        win.SetClickThrough(!(pressed || mode == Mode.Held || (near && mode != Mode.Thrown)));

        if (!pressed) return;
        // Safety net in case the button-up message went to another window.
        if ((Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) == 0) { Release(); return; }
        if (mode != Mode.Held && (time - pressTime > 0.15f || Vector2.Distance(world.Cursor, pressCursor) > 6 * s))
            StartHeld();
    }

    void OnMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || mode == Mode.Thrown) return;
        if (mode == Mode.Sleep)
        {
            // Woken with a jolt.
            startledFlash = 0.8f;
            Startle();
            return;
        }
        pressed = true;
        pressTime = time;
        pressCursor = world.Cursor;
    }

    void OnMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && pressed) Release();
    }

    void Release()
    {
        pressed = false;
        if (mode == Mode.Held) Throw();
        else Startle();
    }

    void StartHeld()
    {
        DropTasks();
        mode = Mode.Held;
        grabOffset = BodyOnScreen() - world.Cursor;
        hVel = 0;
    }

    void UpdateHeld(float dt)
    {
        extraH += (30 * s - extraH) * Math.Min(1, dt * 10);
        grabOffset *= MathF.Exp(-dt * 6);
        pos = world.Cursor + grabOffset + new Vector2(0, BodyHeight() * Tilt);
        vel = world.CursorVel;
        // The body swings round to trail behind the way it's being dragged.
        if (vel.Length() > 80 * s)
            heading += WrapAngle(MathF.Atan2(-vel.Y, -vel.X) - heading) * Math.Min(1, dt * 3);
    }

    void Throw()
    {
        var v = world.CursorVel;
        float speed = v.Length(), max = 2600 * s;
        if (speed > max) { v *= max / speed; speed = max; }
        vel = v;
        hVel = 80 * s + speed * 0.12f;
        spinVel = ((float)rng.NextDouble() - 0.5f) * (4 + speed / (100 * s));
        afterLanding = speed > 500 * s ? Mode.Dizzy : Mode.Wander;
        mode = Mode.Thrown;
    }

    // A quick click makes it hop back, then run.
    void Startle()
    {
        DropTasks();
        var away = pos - world.Cursor;
        float d = away.Length();
        var dir = d > 1 ? away / d : -Dir(heading);
        vel = dir * 320 * s;
        hVel = 260 * s;
        spinVel = 0;
        afterLanding = Mode.Flee;
        mode = Mode.Thrown;
    }

    void UpdateThrown(float dt)
    {
        pos += vel * dt;
        // Light drag in the air, strong friction once it skids along the ground.
        vel *= MathF.Exp(-dt * (extraH > 0.5f ? 0.6f : 4f));
        hVel -= 1100 * s * dt;
        extraH += hVel * dt;
        if (extraH <= 0)
        {
            extraH = 0;
            if (hVel < -150 * s)
            {
                hVel = -hVel * 0.35f;
                SpawnGlitch(pos);
            }
            else hVel = 0;
        }
        heading += spinVel * dt;
        spinVel *= MathF.Exp(-dt * 1.2f);
        KeepOnScreen();

        if (extraH <= 0 && hVel == 0 && vel.Length() < 30 * s) Land();
    }

    void Land()
    {
        vel = Vector2.Zero;
        spinVel = 0;
        foreach (var leg in legs)
        {
            leg.Foot = RestAt(leg, pos, heading) + RandomInDisk(6 * s);
            leg.Lift = 0;
            leg.Stepping = false;
        }

        switch (afterLanding)
        {
            case Mode.Dizzy:
                mode = Mode.Dizzy;
                modeLeft = 1.6f;
                break;
            case Mode.Flee:
                StartFlee(1f);
                break;
            default:
                PickTarget();
                break;
        }
    }

    void KeepOnScreen()
    {
        var b = Screen.FromPoint(new Point((int)pos.X, (int)pos.Y)).Bounds;
        if (pos.X < b.Left) { pos.X = b.Left; vel.X = MathF.Abs(vel.X) * 0.5f; }
        if (pos.X > b.Right) { pos.X = b.Right; vel.X = -MathF.Abs(vel.X) * 0.5f; }
        if (pos.Y < b.Top) { pos.Y = b.Top; vel.Y = MathF.Abs(vel.Y) * 0.5f; }
        if (pos.Y > b.Bottom) { pos.Y = b.Bottom; vel.Y = -MathF.Abs(vel.Y) * 0.5f; }
    }

    // ---------- movement ----------

    void Walk(float dt)
    {
        bool standing = mode is Mode.Hide or Mode.Groom or Mode.Dizzy or Mode.Wrap or Mode.Sleep
                        || (mode == Mode.Spin && spinStage > 0);

        Vector2 to = target - pos;
        float dist = to.Length();
        float speed = 0;
        if (pauseLeft > 0) pauseLeft -= dt;
        else if (!standing)
        {
            float burst = mode == Mode.Flee ? 1f : 0.55f + 0.45f * MathF.Abs(MathF.Sin(time * 4.3f));
            speed = settings.Speed * s * burst * speedMul * Math.Clamp(dist / (40 * s), 0, 1);
        }

        Vector2 desired = dist > 0.01f ? to / dist * speed : Vector2.Zero;
        float accel = mode == Mode.Flee ? 10 : 7;
        vel += (desired - vel) * Math.Min(1, dt * accel);
        pos += vel * dt;

        if (!standing && vel.Length() > 6 * s)
        {
            float want = MathF.Atan2(vel.Y, vel.X);
            heading += WrapAngle(want - heading) * Math.Min(1, dt * (mode == Mode.Flee ? 10 : 6));
        }
    }

    static Vector2 StepToward(Vector2 from, Vector2 to, float step)
    {
        var d = to - from;
        float len = d.Length();
        return len <= step ? to : from + d / len * step;
    }

    // ---------- legs ----------

    Vector2 RestAt(Leg leg, Vector2 at, float h) => at + Dir(h + leg.BaseAngle) * leg.Reach * reachMul * s;

    bool FrontLegBusy(Leg leg) => leg.K == 0 && mode is Mode.Groom or Mode.Wrap;

    void UpdateLegs(float dt)
    {
        float speed = vel.Length();
        // Fast walking takes quick, low steps; a slow creep takes long, high ones.
        float stepDur = Math.Clamp(0.17f - speed / (s * 1400f), 0.075f, 0.17f);
        // Feet land where the body is about to be, including the turn it is making.
        float lead = stepDur * 1.5f;
        var predPos = pos + vel * lead;
        float predHeading = heading + angVel * lead;
        bool moving = speed > 4 * s || MathF.Abs(angVel) > 0.3f;

        foreach (var leg in legs)
        {
            if (!leg.Stepping || FrontLegBusy(leg)) continue;
            leg.T += dt / leg.StepDur;
            // While the foot is still rising it keeps re-aiming at the predicted spot.
            if (leg.T < 0.5f)
                leg.To = Vector2.Lerp(leg.To, RestAt(leg, predPos, predHeading), Math.Min(1, dt * 10));
            float t = Math.Min(1, leg.T);
            leg.Foot = Vector2.Lerp(leg.From, leg.To, Smooth(t));
            leg.Lift = MathF.Sin(MathF.PI * t) * leg.LiftH;
            if (leg.T >= 1)
            {
                leg.Stepping = false;
                leg.Lift = 0;
                leg.Foot = leg.To;
                if (rng.NextDouble() < 0.1 * settings.Intensity) SpawnGlitch(leg.Foot);
            }
        }

        var right = Dir(heading + MathF.PI / 2);
        foreach (var leg in legs)
        {
            if (FrontLegBusy(leg)) { PoseFrontLeg(leg, dt); continue; }
            if (leg.Stepping) continue;

            Vector2 rest = RestAt(leg, pos, heading);
            float d = Vector2.Distance(leg.Foot, rest);
            // A foot drifting toward the body midline (after a sharp turn) has to move now,
            // and so does one still raised from grooming or a landing.
            bool crossing = Vector2.Dot(leg.Foot - pos, right * leg.Side) < 8 * s;
            bool urgent = crossing || leg.Lift > 0.5f || d > StepDist * s * 2.5f;

            bool want;
            if (moving)
            {
                want = d > StepDist * s;
                leg.Idle = 0;
            }
            else
            {
                // Standing still: feet that are a little off shuffle back into place, one at a time.
                leg.Idle = d > 3 * s ? leg.Idle + dt : 0;
                want = leg.Idle > 0.25f;
            }
            if (!want && !urgent) continue;
            if (!urgent && (NeighbourStepping(leg) || (!moving && AnyStepping()))) continue;

            leg.From = leg.Foot;
            leg.To = moving ? RestAt(leg, predPos, predHeading) : rest;
            leg.T = 0;
            leg.StepDur = moving ? stepDur : 0.14f;
            leg.LiftH = Math.Clamp(d * 0.35f, 4 * s, 12 * s);
            leg.Stepping = true;
            leg.Idle = 0;
        }
    }

    // Front legs held up in front of the face: rubbing together when grooming, or
    // turning a caught fly over and over when wrapping it.
    void PoseFrontLeg(Leg leg, float dt)
    {
        var fwd = Dir(heading);
        var right = new Vector2(-fwd.Y, fwd.X);
        bool wrapping = mode == Mode.Wrap;
        float phase = time * (wrapping ? 22 : 13) + (leg.Side > 0 ? MathF.PI : 0);
        var focus = pos + fwd * (wrapping ? 16 : 13) * s;
        var want = focus + fwd * MathF.Sin(phase) * 3 * s + right * leg.Side * (2.5f + MathF.Cos(phase) * 1.5f) * s;

        leg.Stepping = false;
        leg.Foot = Vector2.Lerp(leg.Foot, want, Math.Min(1, dt * 12));
        leg.Lift += ((10 + MathF.Sin(phase) * 2) * s - leg.Lift) * Math.Min(1, dt * 12);
    }

    // Off the ground the legs dangle and kick: wildly when held, tucked in when flying.
    void UpdateLegsAirborne(float dt)
    {
        bool held = mode == Mode.Held;
        for (int i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            leg.Stepping = false;
            float wiggle = MathF.Sin(time * (held ? 18 : 10) + i * 1.7f);
            float reach = leg.Reach * s * (held ? 0.85f : 0.6f);
            var want = pos + Dir(heading + leg.BaseAngle + wiggle * 0.25f) * reach;
            leg.Foot = Vector2.Lerp(leg.Foot, want, Math.Min(1, dt * 14));
            float wantLift = Math.Max(0, extraH - (held ? 14 : 6) * s + wiggle * 3 * s);
            leg.Lift += (wantLift - leg.Lift) * Math.Min(1, dt * 14);
        }
    }

    // A leg waits while its neighbours (the legs beside it, and its mirror) are in the air,
    // which produces the alternating ripple real spiders walk with.
    bool NeighbourStepping(Leg leg)
    {
        foreach (var other in legs)
        {
            if (other == leg || !other.Stepping) continue;
            if (other.Side == leg.Side && Math.Abs(other.K - leg.K) == 1) return true;
            if (other.Side != leg.Side && other.K == leg.K) return true;
        }
        return false;
    }

    bool AnyStepping()
    {
        foreach (var leg in legs)
            if (leg.Stepping) return true;
        return false;
    }

    // The body rides a little lower while legs are in the air, and breathes when idle.
    float BodyHeight()
    {
        float lifted = 0;
        if (mode is not (Mode.Held or Mode.Thrown))
            foreach (var leg in legs) lifted += leg.Lift;
        // Slow, deep breaths while asleep.
        float breath = mode == Mode.Sleep ? MathF.Sin(time * 1.2f) * 1f : MathF.Sin(time * 2.1f) * 0.6f;
        return (13 + breath) * s - lifted * 0.12f + extraH;
    }

    Vector2 BodyOnScreen() => new(pos.X, pos.Y - BodyHeight() * Tilt);

    void SpawnGlitch(Vector2 at)
    {
        if (glitches.Count >= 2 + 3 * settings.Intensity) return;
        glitches.Add(Glitch.Spawn(rng, at, s));
    }

    // ---------- rendering ----------

    public void Render()
    {
        if (away) return;
        var origin = new Point((int)pos.X - WinSize / 2, (int)pos.Y - WinSize / 2);
        g.Clear(Color.Transparent);

        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        foreach (var gl in glitches) gl.Draw(g, origin, labelFont);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Default;
        DrawThreads(origin);
        DrawSpider(origin);

        win.Present(canvas, origin.X, origin.Y);

        topTimer -= 1 / 60f;
        if (topTimer <= 0) { topTimer = 2; win.KeepOnTop(); }
    }

    void DrawThreads(Point o)
    {
        // Dragline left behind.
        if (trail.Count > 1)
        {
            using var pen = new Pen(Color.FromArgb(45, Palette.Line), 1f);
            var pts = new PointF[trail.Count];
            for (int i = 0; i < trail.Count; i++) pts[i] = L(trail[i], o);
            g.DrawLines(pen, pts);
        }

        var body = BodyOnScreen();
        using var nodeFill = new SolidBrush(Color.FromArgb(230, Palette.NodeFill));

        // Dangling from the cursor on a thread while held.
        if (mode == Mode.Held)
        {
            using var pen = new Pen(Color.FromArgb(160, Palette.Line), 1f);
            g.DrawLine(pen, L(world.Cursor, o), L(body, o));
        }

        // Silk anchored to highlighted spots, ending in a node.
        foreach (var gl in glitches)
        {
            if (!gl.IsAnchor) continue;
            float a = Math.Clamp(1 - gl.Age / gl.Life, 0, 1);
            using var pen = new Pen(Color.FromArgb((int)(110 * a), Palette.Line), 1f);
            g.DrawLine(pen, L(body, o), L(gl.Center, o));
            NodeAt(L(gl.Center, o), 1.8f * s, nodeFill, pen);
        }

        // Flickering burst of edges while it's busy glitching.
        if (glitches.Count > 2)
        {
            using var pen = new Pen(Color.FromArgb(110, Palette.Line), 1f);
            for (int i = 0; i < 5; i++)
            {
                float a = (float)(rng.NextDouble() * Math.PI * 2);
                float len = (14 + (float)rng.NextDouble() * 30) * s;
                var end = body + new Vector2(MathF.Cos(a), MathF.Sin(a)) * len;
                g.DrawLine(pen, L(body, o), L(end, o));
                NodeAt(L(end, o), 1.4f * s, nodeFill, pen);
            }
        }
    }

    void DrawSpider(Point o)
    {
        var fwd = Dir(heading);
        var right = new Vector2(-fwd.Y, fwd.X);
        float bodyH = BodyHeight();
        var center = P(pos, bodyH, o);

        // Nearly invisible disc so the cursor can grab the spider around its body.
        using (var hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
            g.FillEllipse(hit, center.X - 20 * s, center.Y - 20 * s, 40 * s, 40 * s);

        // A soft shadow on the ground under the raised body, smaller the higher it is.
        float shrink = Math.Clamp(1 - extraH / (80 * s), 0.4f, 1.1f);
        using (var shadow = new SolidBrush(Color.FromArgb((int)(55 * shrink), 0, 0, 0)))
        {
            var c = L(pos, o);
            g.FillEllipse(shadow, c.X - 11 * s * shrink, c.Y - 7 * s * shrink, 22 * s * shrink, 14 * s * shrink);
        }

        var chains = new PointF[legs.Length][];
        var kneeZ = new float[legs.Length];
        for (int i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            var hip = pos + fwd * HipAlong[leg.K] * s + right * leg.Side * 3.5f * s;
            SolveLeg(leg, hip, bodyH, right * leg.Side, out var knee, out kneeZ[i], out var ankle, out float ankleZ, out var foot);
            chains[i] = new[] { P(hip, bodyH, o), P(knee, kneeZ[i], o), P(ankle, ankleZ, o), P(foot, leg.Lift, o) };
        }

        using var limb = new Pen(Color.FromArgb(235, Palette.Line), 1.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var ring = new Pen(Color.FromArgb(245, 255, 255, 255), 1.1f * s);
        using var nodeFill = new SolidBrush(Color.FromArgb(235, Palette.NodeFill));
        using var liftedFill = new SolidBrush(Color.FromArgb(235, Palette.Line));

        foreach (var chain in chains) g.DrawLines(limb, chain);

        // Body: a red box with a node at its core and one at the head.
        float deg = heading * 180f / MathF.PI;
        using (var boxPen = new Pen(Palette.BodyRed, 1.8f * s))
        using (var boxFill = new SolidBrush(Color.FromArgb(170, Palette.NodeFill)))
        using (var core = new SolidBrush(Palette.BodyRed))
        {
            var st = g.Save();
            g.TranslateTransform(center.X, center.Y);
            g.RotateTransform(deg);
            g.FillRectangle(boxFill, -9 * s, -4.5f * s, 18 * s, 9 * s);
            g.DrawRectangle(boxPen, -9 * s, -4.5f * s, 18 * s, 9 * s);
            g.FillEllipse(core, -2 * s, -2 * s, 4 * s, 4 * s);
            g.Restore(st);
        }
        var head = P(pos + fwd * 12 * s, bodyH, o);
        NodeAt(head, 2.2f * s, nodeFill, ring);

        for (int i = 0; i < legs.Length; i++)
        {
            // Higher joints read as closer to the viewer, so they draw a touch bigger.
            NodeAt(chains[i][1], (1.1f + kneeZ[i] / (60 * s)) * s, nodeFill, ring);
            NodeAt(chains[i][2], 1.3f * s, nodeFill, ring);
            NodeAt(chains[i][3], (1.5f + legs[i].Lift / (12 * s)) * s, legs[i].Stepping ? liftedFill : nodeFill, ring);
        }

        if (mode == Mode.Wrap) DrawWrapping(o, bodyH);
        if (mode == Mode.Dizzy) DrawDizzy(head, nodeFill, ring);
        if (mode == Mode.Sleep) DrawSleeping(head);
        if (startledFlash > 0) DrawStartled(head);
    }

    // Z's built from nodes, drifting up from its head and fading.
    void DrawSleeping(PointF head)
    {
        for (int i = 0; i < 3; i++)
        {
            float t = (time * 0.45f + i / 3f) % 1f;
            float alpha = MathF.Sin(MathF.PI * t);
            float w = (3.5f + t * 3) * s;
            var o = new PointF(head.X + (6 + t * 10) * s, head.Y - (10 + t * 22) * s);
            var pts = new[] { o, new PointF(o.X + w, o.Y), new PointF(o.X, o.Y + w), new PointF(o.X + w, o.Y + w) };
            using var pen = new Pen(Color.FromArgb((int)(200 * alpha), Palette.Line), 1.1f * s);
            using var fill = new SolidBrush(Color.FromArgb((int)(220 * alpha), Palette.NodeFill));
            using var ring = new Pen(Color.FromArgb((int)(230 * alpha), 255, 255, 255), 0.8f * s);
            g.DrawLines(pen, pts);
            foreach (var p in pts) NodeAt(p, 0.9f * s, fill, ring);
        }
    }

    // A red "!" over its head right after being woken.
    void DrawStartled(PointF head)
    {
        float alpha = Math.Clamp(startledFlash / 0.8f, 0, 1);
        using var pen = new Pen(Color.FromArgb((int)(255 * alpha), Palette.BodyRed), 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var dot = new SolidBrush(Color.FromArgb((int)(255 * alpha), Palette.BodyRed));
        g.DrawLine(pen, head.X, head.Y - 24 * s, head.X, head.Y - 14 * s);
        g.FillEllipse(dot, head.X - 1.4f * s, head.Y - 11 * s, 2.8f * s, 2.8f * s);
    }

    // The caught fly being spun in silk in front of the face.
    void DrawWrapping(Point o, float bodyH)
    {
        var c = P(Mouth, bodyH * 0.6f, o);
        using var silk = new SolidBrush(Color.FromArgb(220, 235, 240, 255));
        using var swirl = new Pen(Color.FromArgb(170, Palette.Line), 1f);
        g.FillEllipse(silk, c.X - 2.5f * s, c.Y - 3.5f * s, 5 * s, 7 * s);
        float spin = time * 600;
        for (int i = 0; i < 3; i++)
            g.DrawArc(swirl, c.X - (5 + i * 2) * s, c.Y - (5 + i * 2) * s, (10 + i * 4) * s, (10 + i * 4) * s, spin + i * 120, 140);
    }

    // Little nodes circling its head after a hard landing.
    void DrawDizzy(PointF head, Brush fill, Pen ring)
    {
        for (int i = 0; i < 3; i++)
        {
            float a = time * 6 + i * MathF.PI * 2 / 3;
            var p = new PointF(head.X + MathF.Cos(a) * 9 * s, head.Y - 6 * s + MathF.Sin(a) * 3 * s);
            NodeAt(p, 1.3f * s, fill, ring);
        }
    }

    /// <summary>
    /// Three-segment leg in its own vertical plane: femur and tibia solved with two-bone IK
    /// (knee always up), then a metatarsus dropping to the foot at a fixed angle.
    /// </summary>
    void SolveLeg(Leg leg, Vector2 hip, float hipZ, Vector2 outward,
        out Vector2 knee, out float kneeZ, out Vector2 ankle, out float ankleZ, out Vector2 foot)
    {
        float femur = leg.Reach * 0.55f * s, tibia = leg.Reach * 0.55f * s, meta = leg.Reach * 0.25f * s;
        foot = leg.Foot;
        Vector2 flat = foot - hip;
        float dist = flat.Length();
        Vector2 u = dist > 0.01f ? flat / dist : outward;

        float ankleU = dist - meta * MathF.Cos(AnkleAngle);
        float ankleV = leg.Lift + meta * MathF.Sin(AnkleAngle);

        float du = ankleU, dv = ankleV - hipZ;
        float len = MathF.Sqrt(du * du + dv * dv);
        float max = femur + tibia - 0.01f;
        if (len > max)
        {
            // Out of reach: straighten toward the foot instead of tearing the leg apart.
            du *= max / len;
            dv *= max / len;
            len = max;
            ankleU = du;
            ankleV = hipZ + dv;
            foot = hip + u * (ankleU + meta * MathF.Cos(AnkleAngle));
        }
        len = Math.Max(len, 0.001f);

        float along = (femur * femur - tibia * tibia + len * len) / (2 * len);
        float h = MathF.Sqrt(Math.Max(0, femur * femur - along * along));
        float nu = du / len, nv = dv / len;
        float pu = -nv, pv = nu;
        if (pv < 0) { pu = -pu; pv = -pv; }

        knee = hip + u * (nu * along + pu * h);
        kneeZ = hipZ + nv * along + pv * h;
        ankle = hip + u * ankleU;
        ankleZ = ankleV;
    }

    static PointF P(Vector2 xy, float z, Point o) => new(xy.X - o.X, xy.Y - z * Tilt - o.Y);

    void NodeAt(PointF c, float r, Brush fill, Pen ring)
    {
        g.FillEllipse(fill, c.X - r, c.Y - r, r * 2, r * 2);
        g.DrawEllipse(ring, c.X - r, c.Y - r, r * 2, r * 2);
    }

    // ---------- helpers ----------

    static PointF L(Vector2 v, Point o) => new(v.X - o.X, v.Y - o.Y);
    static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));
    static float Smooth(float t) => t * t * (3 - 2 * t);

    static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.PI * 2;
        while (a < -MathF.PI) a += MathF.PI * 2;
        return a;
    }

    Vector2 RandomInDisk(float r)
    {
        float a = (float)(rng.NextDouble() * Math.PI * 2);
        float d = MathF.Sqrt((float)rng.NextDouble()) * r;
        return new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
    }

    public void Dispose()
    {
        DropTasks();
        win.MouseDown -= OnMouseDown;
        win.MouseUp -= OnMouseUp;
        foreach (var gl in glitches) gl.Dispose();
        glitches.Clear();
        win.Close();
        win.Dispose();
        g.Dispose();
        canvas.Dispose();
        labelFont.Dispose();
    }
}
