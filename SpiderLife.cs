using System.Numerics;

namespace WebCrawler;

enum Fidget { None, TapLeg, CleanLeg, Freeze, LookAround }

/// <summary>
/// The parts of the spider's life that make it feel like a creature rather than a cursor toy:
/// mood in how it moves, idle fidgets, affection once it trusts you, web upkeep, noticing
/// what you're doing, and a bond that fades if you leave it alone for days.
/// </summary>
sealed partial class Spider
{
    // After a full day with no kind interaction, comfort drains at this rate per real day.
    const double NeglectGraceDays = 1;
    const double NeglectPerDay = 0.1;

    DateTime lastBond, lastDecay;
    float neglectTimer;

    bool dashing;
    float dashTimer;

    Fidget fidget;
    float fidgetT, fidgetDur, fidgetCooldown = 2;
    Leg fidgetLeg;
    float lookFrom, lookAmount;

    bool tapping;
    int tapSide;
    float tapT;
    bool restSettled;
    Vector2 restSpot;

    Web repairing;
    int repairStage;
    float repairCooldown = 10;

    bool peekSettled, peekWatching;
    Rectangle peekWindow;
    float peekCooldown = 30, activityCooldown = 10;

    int investigateStage;
    Vector2 investigateSpot, investigateFace;
    float windowReactCooldown = 5;

    bool userAway;
    Vector2 awayFrom;

    public SpiderMemory Memory => new() { Comfort = Comfort, LastBond = lastBond, LastDecay = lastDecay };

    // ---------- the bond over real days ----------

    void ApplyNeglect(DateTime now)
    {
        var from = lastBond.AddDays(NeglectGraceDays);
        if (lastDecay > from) from = lastDecay;
        if (now <= from) return;
        Comfort = Math.Clamp(Comfort - (float)((now - from).TotalDays * NeglectPerDay), 0, 1);
        lastDecay = now;
    }

    void LifeTick(float dt)
    {
        repairCooldown -= dt;
        peekCooldown -= dt;
        activityCooldown -= dt;
        windowReactCooldown -= dt;

        neglectTimer -= dt;
        if (neglectTimer <= 0)
        {
            neglectTimer = 60;
            ApplyNeglect(DateTime.UtcNow);
        }

        // Note where it was when you stepped away. If it hasn't gone far by the time
        // you're back, it has quietly moved on somewhere new.
        if (!userAway && world.IdleSeconds > 120)
        {
            userAway = true;
            awayFrom = pos;
        }
        if (userAway && world.IdleSeconds < 1)
        {
            userAway = false;
            if (mode is not (Mode.Sleep or Mode.Held or Mode.Thrown) && Vector2.Distance(pos, awayFrom) < 500 * s)
                MoveSomewhereNew();
        }
    }

    void MoveSomewhereNew()
    {
        var screens = world.UsableScreens();
        if (screens.Length == 0) return;
        for (int tries = 0; tries < 20; tries++)
        {
            var a = Rectangle.Inflate(screens[rng.Next(screens.Length)].WorkingArea, (int)(-60 * s), (int)(-60 * s));
            var p = new Vector2(a.Left + (float)rng.NextDouble() * a.Width, a.Top + (float)rng.NextDouble() * a.Height);
            if (Vector2.Distance(p, pos) < 500 * s || !world.IsUsable(p)) continue;

            DropTasks();
            pos = p;
            vel = Vector2.Zero;
            trail.Clear();
            foreach (var leg in legs)
            {
                leg.Foot = RestAt(leg, pos, heading);
                leg.Lift = 0;
                leg.Stepping = false;
            }
            PickTarget();
            return;
        }
    }

    // ---------- mood in movement ----------

    // Speed factor for ordinary walking. Wary: quick, low dashes broken by frozen pauses.
    // Comfortable: an unhurried, even stroll.
    float MoodPace(float dt)
    {
        float normal = 0.55f + 0.45f * MathF.Abs(MathF.Sin(time * 4.3f));
        if (mode is not (Mode.Wander or Mode.Edge or Mode.Investigate)) return normal;

        float wary = Wariness();
        if (wary > 0.25f)
        {
            dashTimer -= dt;
            if (dashTimer <= 0)
            {
                dashing = !dashing;
                dashTimer = dashing
                    ? 0.25f + (float)rng.NextDouble() * 0.35f
                    : 0.35f + (float)rng.NextDouble() * 0.9f * wary;
            }
            return dashing ? 1.5f + 0.5f * wary : 0f;
        }

        float ease = Math.Clamp((Comfort - 0.6f) / 0.4f, 0, 1);
        return normal + (0.6f - normal) * ease;
    }

    // ---------- idle fidgets ----------

    bool CanFidget() =>
        (mode == Mode.Wander && pauseLeft > 0)
        || mode == Mode.Hide
        || (mode == Mode.Rest && restSettled)
        || (mode == Mode.Spin && spinStage == 2)
        || (mode == Mode.Peek && peekSettled)
        || (mode == Mode.Investigate && investigateStage == 1);

    void UpdateFidget(float dt)
    {
        if (fidget != Fidget.None)
        {
            fidgetT += dt;
            if (fidgetT >= fidgetDur || !CanFidget()) { EndFidget(); return; }
            if (fidget == Fidget.LookAround)
                heading = lookFrom + lookAmount * MathF.Sin(MathF.PI * fidgetT / fidgetDur);
            else if (fidget == Fidget.Freeze && fidgetT > fidgetDur - 0.12f)
                heading += ((float)rng.NextDouble() - 0.5f) * 0.12f; // the twitch that ends a freeze
            return;
        }

        if (!CanFidget()) return;
        fidgetCooldown -= dt;
        if (fidgetCooldown > 0) return;
        fidgetCooldown = 1.5f + (float)rng.NextDouble() * 3;

        // Nervous spiders freeze more; relaxed ones look around and preen.
        double r = rng.NextDouble();
        if (r < 0.2 + 0.25 * Wariness()) StartFidget(Fidget.Freeze);
        else if (r < 0.5) StartFidget(Fidget.LookAround);
        else if (r < 0.75) StartFidget(Fidget.TapLeg);
        else StartFidget(Fidget.CleanLeg);
    }

    void StartFidget(Fidget kind)
    {
        fidget = kind;
        fidgetT = 0;
        fidgetLeg = null;
        switch (kind)
        {
            case Fidget.TapLeg:
                fidgetDur = 0.8f;
                fidgetLeg = legs[(rng.Next(2) == 0 ? 0 : 4) + 1 + rng.Next(2)];
                break;
            case Fidget.CleanLeg:
                fidgetDur = 1.4f;
                fidgetLeg = legs[rng.Next(2) == 0 ? 0 : 4];
                break;
            case Fidget.Freeze:
                fidgetDur = 0.6f + (float)rng.NextDouble() * 0.6f;
                break;
            case Fidget.LookAround:
                fidgetDur = 1.6f + (float)rng.NextDouble() * 0.8f;
                lookFrom = heading;
                lookAmount = (rng.Next(2) == 0 ? -1 : 1) * (0.5f + (float)rng.NextDouble() * 0.4f);
                break;
        }
    }

    void EndFidget()
    {
        fidget = Fidget.None;
        fidgetLeg = null;
    }

    void PoseFidgetLeg(Leg leg, float dt)
    {
        var fwd = Dir(heading);
        var right = new Vector2(-fwd.Y, fwd.X);
        Vector2 want;
        float lift;
        if (fidget == Fidget.TapLeg)
        {
            // Lifts one leg and taps the ground a few times.
            want = RestAt(leg, pos, heading);
            lift = 6 * s * MathF.Max(0, MathF.Sin(fidgetT * MathF.PI * 7));
        }
        else
        {
            // Draws one front leg through its mouth.
            float k = MathF.Sin(fidgetT * 9);
            want = pos + fwd * (12 + k * 3) * s + right * leg.Side * 2.5f * s;
            lift = (9 + k * 1.5f) * s;
        }
        leg.Stepping = false;
        leg.Foot = Vector2.Lerp(leg.Foot, want, Math.Min(1, dt * 14));
        leg.Lift += (lift - leg.Lift) * Math.Min(1, dt * 16);
    }

    // ---------- posed legs ----------

    bool LegPosed(Leg leg) =>
        leg == fidgetLeg
        || (leg.K == 0 && (mode is Mode.Groom or Mode.Wrap))
        || (leg.K == 0 && mode == Mode.Tap && tapping && leg.Side == tapSide)
        || (leg.K == 0 && mode == Mode.Peek && peekSettled);

    void PoseLeg(Leg leg, float dt)
    {
        if (leg == fidgetLeg) PoseFidgetLeg(leg, dt);
        else if (mode == Mode.Tap) PoseTapLeg(leg, dt);
        else if (mode == Mode.Peek) PosePeekLeg(leg, dt);
        else PoseFrontLeg(leg, dt);
    }

    // ---------- affection ----------

    void StartAffection()
    {
        tapping = false;
        restSettled = false;
        double r = rng.NextDouble();
        if (r < 0.35)
        {
            mode = Mode.Tap;
            modeLeft = 10;
        }
        else if (r < 0.7)
        {
            mode = Mode.Follow;
            modeLeft = 8 + (float)rng.NextDouble() * 6;
        }
        else
        {
            mode = Mode.Rest;
            modeLeft = 10 + (float)rng.NextDouble() * 8;
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            restSpot = world.Cursor + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (70 + (float)rng.NextDouble() * 30) * s;
            if (!world.IsUsable(restSpot))
            {
                var back = pos - world.Cursor;
                restSpot = world.Cursor + (back.Length() > 1 ? Vector2.Normalize(back) : Vector2.UnitX) * 80 * s;
            }
            target = restSpot;
            speedMul = 0.8f;
        }
    }

    // Walks up to the cursor and pats it with a front leg.
    void UpdateTap(float dt)
    {
        if (world.IsBlocked(world.Cursor)) { PickTarget(); return; }
        var to = world.Cursor - pos;
        float d = to.Length();
        var dir = d > 1 ? to / d : Dir(heading);

        if (!tapping)
        {
            target = world.Cursor - dir * 30 * s;
            speedMul = 0.9f;
            modeLeft -= dt;
            if (d < 42 * s)
            {
                tapping = true;
                tapT = 0;
                tapSide = Vector2.Dot(to, Dir(heading + MathF.PI / 2)) >= 0 ? 1 : -1;
            }
            else if (modeLeft <= 0) PickTarget();
            return;
        }

        target = pos;
        heading += WrapAngle(MathF.Atan2(dir.Y, dir.X) - heading) * Math.Min(1, dt * 5);
        tapT += dt;
        if (d > 80 * s) tapping = false;       // the cursor moved off; go after it again
        else if (tapT > 2.4f) PickTarget();
    }

    void PoseTapLeg(Leg leg, float dt)
    {
        var to = world.Cursor - pos;
        float max = leg.Reach * s * 0.95f;
        if (to.Length() > max) to = Vector2.Normalize(to) * max;
        float lift = (2 + 7 * MathF.Max(0, MathF.Sin(tapT * 13))) * s;
        leg.Stepping = false;
        leg.Foot = Vector2.Lerp(leg.Foot, pos + to, Math.Min(1, dt * 14));
        leg.Lift += (lift - leg.Lift) * Math.Min(1, dt * 18);
    }

    // Trails the cursor at a polite distance, stopping when it stops.
    void UpdateFollow(float dt)
    {
        modeLeft -= dt;
        if (modeLeft <= 0 || world.IsBlocked(world.Cursor)) { PickTarget(); return; }
        var to = world.Cursor - pos;
        float d = to.Length();
        const float keep = 110;
        if (d > (keep + 25) * s)
        {
            target = world.Cursor - to / d * keep * s;
            speedMul = d > 300 * s ? 1.4f : 0.9f;
        }
        else
        {
            target = pos;
            speedMul = 0;
            if (d > 1) heading += WrapAngle(MathF.Atan2(to.Y, to.X) - heading) * Math.Min(1, dt * 3);
        }
    }

    // Settles down near the cursor for a while, keeping an eye on it.
    void UpdateRest(float dt)
    {
        var to = world.Cursor - pos;
        float d = to.Length();
        if (!restSettled)
        {
            if (Vector2.Distance(pos, restSpot) < 10 * s) restSettled = true;
            return;
        }
        target = pos;
        if (d > 1 && fidget != Fidget.LookAround)
            heading += WrapAngle(MathF.Atan2(to.Y, to.X) - heading) * Math.Min(1, dt * 1.5f);
        modeLeft -= dt;
        if (modeLeft <= 0 || d > 320 * s) PickTarget();
    }

    // ---------- web upkeep ----------

    bool TryStartRepair()
    {
        if (repairCooldown > 0) return false;
        repairCooldown = 3;
        foreach (var w in world.Webs)
        {
            // Give you a moment to finish clearing it before coming back to mend it.
            if (!w.Damaged || w.SinceDamage < 15) continue;
            if (Vector2.Distance(pos, w.Hub) > 1200 * s) continue;
            if (Wariness() > 0 && Vector2.Distance(world.Cursor, w.Hub) < WatchRadius() * 1.5f) continue;

            repairing = w;
            repairStage = 0;
            mode = Mode.Repair;
            target = w.Hub;
            speedMul = 1.1f;
            return true;
        }
        return false;
    }

    void UpdateRepair(float dt)
    {
        if (repairing == null || !world.Webs.Contains(repairing))
        {
            repairing = null;
            PickTarget();
            return;
        }

        switch (repairStage)
        {
            case 0:
                if (Vector2.Distance(pos, repairing.Hub) < 10 * s) repairStage = 1;
                break;

            case 1:
                if (repairing.SinceDamage < 0.3f)
                {
                    // Torn again while it worked on it.
                    AddComfort(-0.03f);
                    StartFlee(1.2f);
                    return;
                }
                heading += dt * 1.4f;
                if (repairing.Repair(dt))
                {
                    repairStage = 2;
                    modeLeft = 3;
                }
                break;

            case 2:
                modeLeft -= dt;
                if (TryStartHunt()) return;
                if (modeLeft <= 0)
                {
                    repairing = null;
                    PickTarget();
                }
                break;
        }
    }

    bool StuckFlyNearby()
    {
        foreach (var f in world.Flies)
            if (f.StuckIn != null && !f.Caught && Vector2.Distance(pos, f.Pos) < 1500 * s) return true;
        return false;
    }

    // ---------- noticing you ----------

    bool TryReactToActivity()
    {
        if (world.Foreground is not Rectangle fg) return false;

        // You're typing: come and watch.
        if (world.Typing && Comfort >= 0.5f && activityCooldown <= 0)
        {
            activityCooldown = 25;
            return StartPeek(fg, world.Caret, true);
        }

        // Every so often, just peek at what you're up to.
        if (peekCooldown <= 0 && Comfort >= 0.3f)
        {
            peekCooldown = 40 + (float)rng.NextDouble() * 40;
            if (rng.NextDouble() < 0.6) return StartPeek(fg, null, false);
        }
        return false;
    }

    bool StartPeek(Rectangle fg, Vector2? caret, bool watching)
    {
        float margin = 30 * s;
        float x = caret is Vector2 c
            ? Math.Clamp(c.X, fg.Left + margin, fg.Right - margin)
            : fg.Left + margin + (float)rng.NextDouble() * Math.Max(1, fg.Width - margin * 2);
        var spot = new Vector2(x, fg.Top - 4 * s);
        if (!world.IsUsable(spot)) spot.Y = fg.Top + 4 * s; // window flush with the top of the screen
        if (!world.IsUsable(spot)) return false;

        peekWindow = fg;
        peekWatching = watching;
        peekSettled = false;
        mode = Mode.Peek;
        target = spot;
        speedMul = watching ? 1.2f : 1f;
        modeLeft = watching ? 10 : 5 + (float)rng.NextDouble() * 5;
        return true;
    }

    // Sitting on the top edge of the window you're using, looking down into it.
    void UpdatePeek(float dt)
    {
        if (world.Foreground is not Rectangle fg || Math.Abs(fg.Top - peekWindow.Top) > 20
            || fg.Right < pos.X || fg.Left > pos.X)
        {
            // The window moved, or you switched to another one.
            PickTarget();
            return;
        }
        if (!peekSettled)
        {
            if (Vector2.Distance(pos, target) < 8 * s) peekSettled = true;
            return;
        }

        target = pos;
        if (fidget != Fidget.LookAround)
        {
            var look = peekWatching && world.Caret is Vector2 caret ? caret - pos : new Vector2(0, 1);
            heading += WrapAngle(MathF.Atan2(look.Y, look.X) - heading) * Math.Min(1, dt * 4);
        }
        modeLeft -= dt;
        if (peekWatching && world.Typing) modeLeft = Math.Max(modeLeft, 3);
        if (modeLeft <= 0) PickTarget();
    }

    void PosePeekLeg(Leg leg, float dt)
    {
        // Front legs hooked over the window's top edge.
        var fwd = Dir(heading);
        var right = new Vector2(-fwd.Y, fwd.X);
        var want = pos + fwd * 15 * s + right * leg.Side * 6 * s;
        leg.Stepping = false;
        leg.Foot = Vector2.Lerp(leg.Foot, want, Math.Min(1, dt * 10));
        leg.Lift += (1.5f * s - leg.Lift) * Math.Min(1, dt * 10);
    }

    // A window opened or moved nearby: a little jump of surprise, then a closer look.
    bool TryReactToWindow()
    {
        if (windowReactCooldown > 0 || world.WindowEvents.Count == 0) return false;
        if (mode is not (Mode.Wander or Mode.Edge or Mode.Hide or Mode.Groom or Mode.Rest or Mode.Follow)) return false;

        WindowEvent? best = null;
        float bestDist = 900 * s;
        Vector2 spot = default;
        foreach (var ev in world.WindowEvents)
        {
            var p = NearestBorderPoint(ev.Rect, pos);
            float d = Vector2.Distance(p, pos);
            if (d < bestDist && world.IsUsable(p)) { best = ev; bestDist = d; spot = p; }
        }
        if (best is not { } found) return false;

        windowReactCooldown = 10;
        DropTasks();
        investigateSpot = spot;
        investigateFace = new Vector2(found.Rect.Left + found.Rect.Width / 2f, found.Rect.Top + found.Rect.Height / 2f);
        startledFlash = 0.35f;
        vel = Vector2.Zero;
        hVel = 140 * s;
        spinVel = 0;
        // Nervous spiders run and hide instead of looking.
        afterLanding = Wariness() > 0.5f ? Mode.Flee : Mode.Investigate;
        mode = Mode.Thrown;
        return true;
    }

    void StartInvestigate()
    {
        mode = Mode.Investigate;
        investigateStage = 0;
        target = investigateSpot;
        modeLeft = 12;
        speedMul = 0.8f + 0.3f * Comfort;
    }

    void UpdateInvestigate(float dt)
    {
        modeLeft -= dt;
        if (investigateStage == 0)
        {
            if (Vector2.Distance(pos, target) < 10 * s)
            {
                investigateStage = 1;
                modeLeft = 2.5f;
                StartFidget(Fidget.LookAround);
            }
            else if (modeLeft <= 0) PickTarget();
            return;
        }

        target = pos;
        if (fidget == Fidget.None)
        {
            var to = investigateFace - pos;
            heading += WrapAngle(MathF.Atan2(to.Y, to.X) - heading) * Math.Min(1, dt * 3);
        }
        if (modeLeft <= 0) PickTarget();
    }

    static Vector2 NearestBorderPoint(Rectangle r, Vector2 p)
    {
        float x = Math.Clamp(p.X, r.Left, r.Right), y = Math.Clamp(p.Y, r.Top, r.Bottom);
        if (x > r.Left && x < r.Right && y > r.Top && y < r.Bottom)
        {
            // Inside the window: step out to the closest edge.
            float toLeft = x - r.Left, toRight = r.Right - x, toTop = y - r.Top, toBottom = r.Bottom - y;
            float m = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
            if (m == toLeft) x = r.Left;
            else if (m == toRight) x = r.Right;
            else if (m == toTop) y = r.Top;
            else y = r.Bottom;
        }
        return new Vector2(x, y);
    }
}
