using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using static WebCrawler.SpiderProportions;

namespace WebCrawler;

enum Mode
{
    Wander,      // roaming to random spots
    Edge,        // walking along a window border
    Hide,        // tucked into a corner
    Groom,       // rubbing its front legs together
    Flee,        // running from the cursor
    Held,        // picked up by the cursor
    Thrown,      // flying through the air after a throw or a startled hop
    Dizzy,       // wobbling after a hard landing
    Spin,        // building and sitting in a web
    Hunt,        // stalking a fly
    Wrap,        // wrapping a caught fly in silk
    Sleep,       // asleep in a web, curled up, or in the nest
    Wary,        // frozen, watching a nearby cursor, ready to bolt
    Follow,      // trailing the cursor at a distance (affection)
    Tap,         // walking up to the cursor and patting it (affection)
    Rest,        // settled down near the cursor (affection)
    Repair,      // mending a torn web
    Peek,        // sitting on top of the window you're using
    Investigate, // checking out a window that just opened or moved
}

/// <summary>
/// The spider's behaviour: it walks the desktop with an alternating gait, glitches what its feet
/// land on, and reacts to the cursor, windows, flies and its own webs. How it looks is entirely
/// up to <see cref="SpiderAppearance"/>.
/// </summary>
sealed partial class Spider : Creature
{
    public const string KindName = "spider";
    const int WinSize = 560;
    const int MaxWebs = 4;

    readonly SpiderAppearance appearance = new();
    readonly SpiderPose pose = new();
    readonly Overlay win = new();
    readonly Bitmap canvas = new(WinSize, WinSize, PixelFormat.Format32bppPArgb);
    readonly Graphics g;
    readonly Font labelFont;
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
    bool hideAtEnd;      // heading for a hiding spot rather than just walking an edge
    float watchedFor;    // how long it has been eyeing the cursor
    Web sleepWeb;        // the web it is asleep in, if any
    float startledFlash; // the "!" shown after a fright

    public override string Kind => KindName;
    public override ICreatureAppearance Appearance => appearance;
    public override bool IsHeld => mode == Mode.Held;

    public Spider(World world, CreatureMemory memory) : base(world, memory)
    {
        g = Graphics.FromImage(canvas);
        labelFont = new Font("Consolas", 6f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        webCooldown = 20 + (float)rng.NextDouble() * 25;
        BuildLegs();

        pose.Scale = s;
        pose.Rng = rng;
        pose.Legs = legs;
        pose.Trail = trail;
        pose.Glitches = glitches;

        RestorePlace(memory);

        win.Cursor = Cursors.Hand;
        win.MouseDown += OnMouseDown;
        win.MouseUp += OnMouseUp;
        if (!InNest) win.Show();
    }

    public override void KeepOnTop() => win.KeepOnTop();

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
        ResetFeet();
    }

    // A screen just went fullscreen under the spider: hop to a free screen, or vanish if none.
    void LeaveBlockedScreen()
    {
        DropTasks();
        pressed = false;
        ClearGlitches();

        var usable = world.UsableScreens();
        if (usable.Length == 0)
        {
            away = true;
            win.Hide();
            return;
        }
        EnterFrom(usable[rng.Next(usable.Length)]);
    }

    // ---------- the nest ----------

    public override void EnterNest()
    {
        DropTasks();
        pressed = false;
        InNest = true;
        mode = Mode.Sleep;
        vel = Vector2.Zero;
        extraH = 0;
        naps++;
        // Being tucked in gently is a kindness.
        AddComfort(0.02f);
        ClearGlitches();
        trail.Clear();
        win.SetClickThrough(true);
        win.Hide();
    }

    public override void LeaveNest(Vector2 at)
    {
        InNest = false;
        pos = at;
        heading = (float)(rng.NextDouble() * Math.PI * 2);
        vel = Vector2.Zero;
        extraH = 0;
        trail.Clear();
        ResetFeet();
        // Wakes up slowly and has a little groom before setting off.
        StartGroom();
        win.Show();
        win.KeepOnTop();
    }

    public override void LiftOutOfNest(Vector2 cursor)
    {
        InNest = false;
        vel = Vector2.Zero;
        trail.Clear();
        mode = Mode.Held;
        grabOffset = Vector2.Zero;
        hVel = 0;
        extraH = 30 * s;
        pos = cursor + new Vector2(0, CurrentBodyHeight() * Tilt);
        ResetFeet();
        // Treated as a press that's still held down, so letting go throws, drops, or tucks it back in.
        pressed = true;
        pressTime = time;
        pressCursor = cursor;
        win.Show();
        win.KeepOnTop();
    }

    // ---------- main loop ----------

    public override void Update(float dt)
    {
        TickBond(dt);
        if (InNest)
        {
            time += dt;
            return;
        }

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
        KeepingCompany(dt);
        LifeTick(dt);
        float prevHeading = heading;

        HandleInput();

        if (mode == Mode.Held) UpdateHeld(dt);
        else if (mode == Mode.Thrown) UpdateThrown(dt);
        else
        {
            Think(dt);
            UpdateFidget(dt);
            Walk(dt);
            float wantH = mode switch
            {
                Mode.Hide => -5 * s,
                Mode.Sleep => sleepWeb != null ? -2 * s : -6 * s,
                Mode.Wary => -3 * s,
                Mode.Rest => restSettled ? -3 * s : 0,
                Mode.Peek => peekSettled ? -2 * s : 0,
                Mode.Groom => 2 * s,
                // Wary spiders keep low as they dart about.
                Mode.Wander or Mode.Edge => -2.5f * s * Wariness(),
                _ => 0,
            };
            extraH += (wantH - extraH) * Math.Min(1, dt * 6);
        }

        float wantReach = mode switch
        {
            Mode.Hide => 0.8f,
            Mode.Sleep => sleepWeb != null ? 0.9f : 0.55f,
            Mode.Wary => 0.9f,
            Mode.Rest => restSettled ? 0.85f : 1f,
            _ => 1f,
        };
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
                AddComfort(-0.03f);
                startledFlash = 0.8f;
                Startle();
            }
            return;
        }

        // Sweeping a selection box over it shouldn't scare it off.
        bool drowsy = mode == Mode.Spin && sleepAfterSpin;
        if (mode is not (Mode.Flee or Mode.Dizzy) && !drowsy && !world.Selecting && CursorThreat())
        {
            AddComfort(-0.02f);
            Remember(scary, pos);
            StartFlee(1.1f * (1 + Wariness()));
            return;
        }

        // A wary spider stops whatever it's doing when the cursor comes close.
        if (Wariness() > 0 && !drowsy
            && mode is Mode.Wander or Mode.Edge or Mode.Hide or Mode.Groom or Mode.Hunt or Mode.Spin
                or Mode.Repair or Mode.Peek or Mode.Investigate or Mode.Follow or Mode.Rest or Mode.Tap
            && Vector2.Distance(world.Cursor, pos) < WatchRadius())
        {
            StartWary();
            return;
        }

        if (TryReactToWindow()) return;

        // A fly struggling in a web is worth dropping things for.
        if (mode is Mode.Hide or Mode.Groom or Mode.Rest or Mode.Peek or Mode.Investigate or Mode.Follow
            && StuckFlyNearby() && TryStartHunt()) return;

        switch (mode)
        {
            case Mode.Wary: UpdateWary(dt); break;
            case Mode.Follow: UpdateFollow(dt); break;
            case Mode.Tap: UpdateTap(dt); break;
            case Mode.Rest: UpdateRest(dt); break;
            case Mode.Repair: UpdateRepair(dt); break;
            case Mode.Peek: UpdatePeek(dt); break;
            case Mode.Investigate: UpdateInvestigate(dt); break;
            case Mode.Spin: UpdateSpin(dt); break;
            case Mode.Hunt: UpdateHunt(); break;

            case Mode.Wander:
                speedMul = 1;
                if (TryStartHunt() || TryStartRepair() || TryReactToActivity() || TryStartSpin()) return;
                if (pauseLeft <= 0 && Vector2.Distance(pos, target) < 12 * s)
                {
                    double r = rng.NextDouble();
                    if (Comfort > 0.6f && r < 0.45)
                    {
                        // A relaxed spider stops to take in its surroundings.
                        pauseLeft = 1.8f + (float)rng.NextDouble() * 1.2f;
                        StartFidget(Fidget.LookAround);
                        PickTarget();
                        return;
                    }
                    if (r < 0.15) { StartGroom(); return; }
                    if (r < 0.4) pauseLeft = 0.3f + (float)rng.NextDouble() * 1.2f;
                    PickTarget();
                }
                break;

            case Mode.Edge:
                if (TryStartHunt() || TryStartRepair()) return;
                if (Vector2.Distance(pos, target) < 10 * s)
                {
                    if (Vector2.Distance(target, edgeEnd) < 1)
                    {
                        if (hideAtEnd || rng.NextDouble() < 0.5) StartHide();
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
                if (modeLeft <= 0)
                {
                    // Hid here in peace: a good spot to come back to.
                    Remember(favourites, pos);
                    PickTarget();
                }
                break;
            }

            case Mode.Groom:
                modeLeft -= dt;
                if (modeLeft <= 0) PickTarget();
                break;

            case Mode.Flee:
            {
                modeLeft -= dt;
                var fromCursor = pos - world.Cursor;
                float d = fromCursor.Length();
                target = pos + (d > 1 ? fromCursor / d : Dir(heading)) * 220 * s;
                speedMul = 2.3f;
                if (modeLeft <= 0) PickTarget();
                break;
            }

            case Mode.Dizzy:
                modeLeft -= dt;
                heading += MathF.Sin(time * 7) * dt * 5;
                if (modeLeft <= 0) StartFlee(0.9f);
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
        hideAtEnd = false;
        tapping = false;
        restSettled = false;
        peekSettled = false;
        double r = rng.NextDouble();

        float wary = Wariness();
        if (wary > 0 && rng.NextDouble() < 0.3 + 0.7 * wary && TryPickHidingSpot())
            return;

        // Fond of you: come over to say hello.
        if (settings.Chase && Comfort >= 0.75f && r < 0.22 && !world.IsBlocked(world.Cursor))
        {
            StartAffection();
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

        // Now and then it heads back to a place it likes.
        if (rng.NextDouble() < 0.2 && TryGoToFavourite()) return;

        var screens = world.UsableScreens();
        if (screens.Length == 0) return;
        for (int tries = 0; tries < 20; tries++)
        {
            Vector2 c;
            // While you're away it ranges much further afield.
            bool exploring = world.IdleSeconds > 60;
            if (!exploring && rng.NextDouble() < 0.65)
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

            if (NearScarySpot(c)) continue;
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
        // The more at ease it is, the faster a rush has to be to scare it.
        if (speed < 1400 * s * (1 + Comfort * 1.2f)) return false;
        return Vector2.Dot(v / speed, toMe / d) > 0.6f;
    }

    void StartFlee(float seconds)
    {
        DropTasks();
        mode = Mode.Flee;
        modeLeft = seconds;
        pauseLeft = 0;
    }

    // A calm cursor resting nearby slowly wins it over.
    void KeepingCompany(float dt)
    {
        if (mode is Mode.Held or Mode.Thrown or Mode.Flee or Mode.Sleep) return;
        bool near = Vector2.Distance(world.Cursor, BodyOnScreen()) < Math.Max(120 * s, WatchRadius());
        // Only counts while you're actually at the PC.
        bool calm = world.CursorVel.Length() < 300 * s && world.IdleSeconds < 90;
        if (near && calm) AddComfort(dt * 0.004f);
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
        if (world.Windows.TryFindNearestWebSpot(pos, radius, s, out var hub))
        {
            // Wander sleepily to the nearest open bit of desktop, spin a quick web, and sleep in it.
            spinHub = hub;
            spinRadius = radius;
            target = hub;
            spinStage = 0;
            sleepAfterSpin = true;
            speedMul = 0.7f;
            mode = Mode.Spin;
            return;
        }
        // No desktop showing anywhere: curl up where it is.
        naps++;
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
        float wary = Wariness();
        modeLeft = (3 + (float)rng.NextDouble() * 5) * (1 + wary * 1.5f);
        hideAtEnd = false;
    }

    // ---------- wariness ----------

    // 0 when comfortable (half-way to green or more), rising to 1 when fully red.
    float Wariness() => Math.Clamp((0.5f - Comfort) / 0.5f, 0, 1);

    // How close the cursor can get before a wary spider stops to watch it.
    float WatchRadius() => (120 + 200 * Wariness()) * s;

    void StartWary()
    {
        DropTasks();
        mode = Mode.Wary;
        watchedFor = 0;
        pauseLeft = 0;
    }

    // Crouched and facing the cursor: backs off if it's too close, bolts if it moves suddenly.
    void UpdateWary(float dt)
    {
        var toCursor = world.Cursor - pos;
        float d = toCursor.Length();
        var dir = d > 1 ? toCursor / d : Dir(heading);
        heading += WrapAngle(MathF.Atan2(dir.Y, dir.X) - heading) * Math.Min(1, dt * 6);

        float wary = Wariness();
        float jumpSpeed = (220 + 380 * (1 - wary)) * s;
        if (world.CursorVel.Length() > jumpSpeed)
        {
            startledFlash = 0.6f;
            Startle();
            return;
        }

        float radius = WatchRadius();
        if (d < radius * 0.7f)
        {
            target = pos - dir * 80 * s;
            speedMul = 0.35f;
        }
        else
        {
            target = pos;
            speedMul = 0;
        }

        // Once the cursor is out of range, or it has stared long enough, go and hide.
        watchedFor += dt;
        if (d > radius * 1.5f || wary <= 0 || watchedFor > 8 + 6 * (1 - wary)) PickTarget();
    }

    // Somewhere far from the cursor, preferring corners it can tuck into.
    bool TryPickHidingSpot()
    {
        var screens = world.UsableScreens();
        if (screens.Length == 0) return false;

        var spots = new List<(Vector2 at, Vector2 facing)>();
        foreach (var sc in screens)
        {
            var a = Rectangle.Inflate(sc.WorkingArea, (int)(-20 * s), (int)(-20 * s));
            var center = new Vector2(a.Left + a.Width / 2f, a.Top + a.Height / 2f);
            foreach (var c in new[] { new Vector2(a.Left, a.Top), new Vector2(a.Right, a.Top), new Vector2(a.Left, a.Bottom), new Vector2(a.Right, a.Bottom) })
                spots.Add((c, Vector2.Normalize(center - c)));
        }
        foreach (var r in world.Windows.Rects.Take(5))
        {
            var center = new Vector2(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
            foreach (var c in new[] { new Vector2(r.Left, r.Top + 1), new Vector2(r.Right, r.Top + 1) })
                if (world.IsUsable(c)) spots.Add((c, Vector2.Normalize(new Vector2(MathF.Sign(center.X - c.X), 1))));
        }
        if (spots.Count == 0) return false;

        // Furthest from the cursor wins, with a little randomness so it doesn't always pick the same one.
        (Vector2 at, Vector2 facing) best = spots[0];
        float bestScore = float.MinValue;
        foreach (var spot in spots)
        {
            float score = Vector2.Distance(spot.at, world.Cursor) * (0.8f + (float)rng.NextDouble() * 0.4f);
            if (score > bestScore) { bestScore = score; best = spot; }
        }

        mode = Mode.Edge;
        target = best.at;
        edgeEnd = best.at;
        cornerInward = best.facing;
        hideAtEnd = true;
        speedMul = 0.85f;
        return true;
    }

    // Walking away from a web in progress or a fly being stalked.
    void DropTasks()
    {
        if (prey != null) { prey.Hunter = null; prey = null; }
        web = null;
        spinStage = 0;
        sleepAfterSpin = false;
        sleepWeb = null;
        repairing = null;
        tapping = false;
        restSettled = false;
        peekSettled = false;
        EndFidget();
    }

    // ---------- webs ----------

    bool TryStartSpin()
    {
        if (webCooldown > 0 || world.Webs.Count >= MaxWebs) return false;
        webCooldown = 5; // try again soon if no good spot turns up
        float radius = (55 + (float)rng.NextDouble() * 45) * s;
        // Prefers to build somewhere it has slept or rested before.
        if (!TryFavouriteWebSpot(radius, out var hub) && !world.Windows.TryFindWebSpot(rng, radius, out hub)) return false;

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
            AddComfort(-0.03f);
            StartFlee(1.2f);
            return;
        }

        switch (spinStage)
        {
            case 0: // walking to the hub
                if (Vector2.Distance(pos, spinHub) < 10 * s)
                {
                    if (sleepAfterSpin && world.Webs.Count >= MaxWebs)
                    {
                        // Make room: the oldest web goes.
                        world.Webs[0].Dispose();
                        world.Webs.RemoveAt(0);
                    }
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
                    naps++;
                    Remember(favourites, pos);
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

    Vector2 Mouth => pos + Dir(heading) * MouthAhead * s;

    bool TryStartHunt()
    {
        // Just eaten: only a fly struggling in a web is tempting enough, unless it's getting hungry.
        bool fed = huntCooldown > 0 && Hunger < 0.6f;
        // Hungrier spiders spot flies from further away.
        float reach = 1 + Hunger;
        Fly best = null;
        float bestScore = float.MaxValue;
        foreach (var f in world.Flies)
        {
            if (f.Caught || f.Gone || (f.Hunter != null && f.Hunter != this)) continue;
            if (fed && f.StuckIn == null) continue;
            if (Wariness() > 0 && Vector2.Distance(f.Pos, world.Cursor) < WatchRadius() * 1.2f) continue;
            float d = Vector2.Distance(pos, f.Pos);
            float range = (f.StuckIn != null ? 1500 : 450) * s * reach;
            float score = f.StuckIn != null ? d * 0.3f : d;
            if (d < range && score < bestScore) { best = f; bestScore = score; }
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
        // Creep up, then pounce. A fly stuck in a web gets a full sprint.
        speedMul = prey.StuckIn != null ? 2.2f : d < 90 * s ? 2.6f : 1f;

        if (d > (prey.StuckIn != null ? 1700 : 600) * s * (1 + Hunger))
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

        fliesEaten++;
        Feed(0.3f);
        // A good meal, better still one stored in its own web.
        AddComfort(home != null ? 0.06f : 0.05f);
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
        if (e.Button != MouseButtons.Left || mode == Mode.Thrown || InNest) return;
        if (mode == Mode.Sleep)
        {
            // Woken with a jolt.
            AddComfort(-0.02f);
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
        if (mode == Mode.Held)
        {
            // Dropped into the open nest: tucked in for a sleep.
            if (world.NestZone is RectangleF nest && nest.Contains(world.Cursor.X, world.Cursor.Y))
            {
                EnterNest();
                return;
            }
            Throw();
        }
        else
        {
            AddComfort(-0.03f);
            Remember(scary, pos);
            Startle();
        }
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
        pos = world.Cursor + grabOffset + new Vector2(0, CurrentBodyHeight() * Tilt);
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
        // Set down gently it trusts you a little more; hurled across the screen, a lot less.
        AddComfort(speed > 500 * s ? -0.06f : 0.01f);
        if (speed > 500 * s)
        {
            timesThrown++;
            Remember(scary, pos);
        }
        mode = Mode.Thrown;
    }

    // A quick click makes it hop back, then run.
    void Startle()
    {
        DropTasks();
        var fromCursor = pos - world.Cursor;
        float d = fromCursor.Length();
        var dir = d > 1 ? fromCursor / d : -Dir(heading);
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
        ResetFeet(6 * s);

        switch (afterLanding)
        {
            case Mode.Dizzy:
                mode = Mode.Dizzy;
                modeLeft = 1.6f;
                break;
            case Mode.Flee:
                StartFlee(1f);
                break;
            case Mode.Investigate:
                StartInvestigate();
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
                        || (mode == Mode.Spin && spinStage > 0)
                        || (mode == Mode.Tap && tapping)
                        || (mode == Mode.Rest && restSettled)
                        || (mode == Mode.Peek && peekSettled)
                        || (mode == Mode.Repair && repairStage > 0)
                        || (mode == Mode.Investigate && investigateStage == 1);

        Vector2 to = target - pos;
        float dist = to.Length();
        float speed = 0;
        if (pauseLeft > 0) pauseLeft -= dt;
        else if (!standing)
        {
            float burst = mode == Mode.Flee ? 1f : MoodPace(dt);
            speed = settings.Speed * s * burst * speedMul * Math.Clamp(dist / (40 * s), 0, 1);
        }

        Vector2 desired = dist > 0.01f ? to / dist * speed : Vector2.Zero;
        float accel = mode == Mode.Flee ? 10 : 7;
        vel += (desired - vel) * Math.Min(1, dt * accel);
        pos += vel * dt;

        if (!standing && mode != Mode.Wary && vel.Length() > 6 * s)
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

    // ---------- glitches ----------

    void SpawnGlitch(Vector2 at)
    {
        if (glitches.Count >= 2 + 3 * settings.Intensity) return;
        glitches.Add(Glitch.Spawn(rng, at, s));
    }

    void ClearGlitches()
    {
        foreach (var gl in glitches) gl.Dispose();
        glitches.Clear();
    }

    // ---------- rendering ----------

    public override void Render()
    {
        if (away || InNest) return;

        // Asleep in a web that a window now covers: hidden along with the web.
        bool tucked = mode == Mode.Sleep && sleepWeb != null
                      && world.Windows.CoveredPoints(sleepWeb.Hub, sleepWeb.Radius) >= 3;
        if (tucked)
        {
            if (win.Visible) win.Hide();
            return;
        }
        if (!win.Visible) win.Show();

        var origin = new Point((int)pos.X - WinSize / 2, (int)pos.Y - WinSize / 2);
        g.Clear(Color.Transparent);

        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        foreach (var gl in glitches) gl.Draw(g, origin, labelFont);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Default;

        pose.Pos = pos;
        pose.Mouth = Mouth;
        pose.Cursor = world.Cursor;
        pose.Heading = heading;
        pose.BodyHeight = CurrentBodyHeight();
        pose.ExtraH = extraH;
        pose.Time = time;
        pose.ShownComfort = shownComfort;
        pose.ComfortPulse = comfortPulse;
        pose.StartledFlash = startledFlash;
        pose.Sleeping = mode == Mode.Sleep || (mode == Mode.Spin && sleepAfterSpin);
        pose.Dizzy = mode == Mode.Dizzy;
        pose.Wrapping = mode == Mode.Wrap;
        pose.Held = mode == Mode.Held;
        appearance.Draw(g, origin, pose);

        win.Present(canvas, origin.X, origin.Y);

        topTimer -= 1 / 60f;
        if (topTimer <= 0) { topTimer = 2; win.KeepOnTop(); }
    }

    public override void Dispose()
    {
        DropTasks();
        win.MouseDown -= OnMouseDown;
        win.MouseUp -= OnMouseUp;
        ClearGlitches();
        win.Close();
        win.Dispose();
        g.Dispose();
        canvas.Dispose();
        labelFont.Dispose();
    }
}
