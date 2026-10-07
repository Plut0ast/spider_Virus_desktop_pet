using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace WebCrawler;

sealed class CrawlerSettings
{
    public float Intensity = 1f;
    public bool Chase = true;
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

/// <summary>
/// One spider: walks the desktop with an alternating gait and glitches what its feet land on.
/// </summary>
sealed class Spider : IDisposable
{
    const int WinSize = 560;
    const float StepDist = 18f;
    static readonly float[] LegAngles = { 0.6f, 1.2f, 1.95f, 2.5f };
    static readonly float[] LegReach = { 52f, 44f, 38f, 48f };

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
    float heading, angVel, time, pauseLeft, chaseLeft, ambientTimer, trailTimer, topTimer;

    // Fake 3D: height shifts a point up the screen, like a camera tilted slightly forward.
    const float Tilt = 0.5f;
    // The last leg segment (metatarsus) rises from the foot at about 70 degrees.
    const float AnkleAngle = 1.2f;

    public Spider(CrawlerSettings settings, Random rng, float scale)
    {
        this.settings = settings;
        this.rng = rng;
        s = scale;
        g = Graphics.FromImage(canvas);
        labelFont = new Font("Consolas", 6f * s, FontStyle.Bold, GraphicsUnit.Pixel);

        // Walk in from the left or right edge of a random screen.
        var screens = Screen.AllScreens;
        var b = screens[rng.Next(screens.Length)].Bounds;
        bool left = rng.NextDouble() < 0.5;
        pos = new Vector2(left ? b.Left - 40 * s : b.Right + 40 * s, b.Top + (float)rng.NextDouble() * b.Height);
        heading = left ? 0 : MathF.PI;
        target = new Vector2(b.Left + b.Width * (left ? 0.3f : 0.7f), pos.Y);

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
            leg.Foot = RestAt(leg, pos, heading);
            legs[i] = leg;
        }

        win.Show();
    }

    // ---------- simulation ----------

    public void Update(float dt)
    {
        time += dt;
        Think(dt);

        Vector2 to = target - pos;
        float dist = to.Length();
        float speed = 0;
        if (pauseLeft > 0) pauseLeft -= dt;
        else
        {
            float burst = 0.55f + 0.45f * MathF.Abs(MathF.Sin(time * 4.3f));
            speed = settings.Speed * s * burst * Math.Clamp(dist / (40 * s), 0, 1);
        }
        Vector2 desired = dist > 0.01f ? to / dist * speed : Vector2.Zero;
        vel += (desired - vel) * Math.Min(1, dt * 7);
        pos += vel * dt;

        float prevHeading = heading;
        if (vel.Length() > 6 * s)
        {
            float want = MathF.Atan2(vel.Y, vel.X);
            heading += WrapAngle(want - heading) * Math.Min(1, dt * 6);
        }
        float turnRate = WrapAngle(heading - prevHeading) / Math.Max(dt, 1e-4f);
        angVel += (turnRate - angVel) * Math.Min(1, dt * 10);

        UpdateLegs(dt);

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
            bool active = vel.Length() > 10 * s || chaseLeft > 0;
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

    void Think(float dt)
    {
        if (chaseLeft > 0)
        {
            chaseLeft -= dt;
            var c = Cursor.Position;
            var cur = new Vector2(c.X, c.Y);
            var away = pos - cur;
            float d = away.Length();
            target = d > 1 ? cur + away / d * 28 * s : cur;
            if (chaseLeft <= 0) PickTarget();
            return;
        }

        if (pauseLeft <= 0 && Vector2.Distance(pos, target) < 12 * s)
        {
            if (rng.NextDouble() < 0.3) pauseLeft = 0.3f + (float)rng.NextDouble() * 1.2f;
            PickTarget();
        }
    }

    void PickTarget()
    {
        if (settings.Chase && rng.NextDouble() < 0.18)
        {
            chaseLeft = 3 + (float)rng.NextDouble() * 4;
            return;
        }

        var screens = Screen.AllScreens;
        for (int tries = 0; tries < 20; tries++)
        {
            Vector2 c;
            if (rng.NextDouble() < 0.65)
            {
                float a = (float)(rng.NextDouble() * Math.PI * 2);
                float r = (120 + (float)rng.NextDouble() * 400) * s;
                c = pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
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
        var p = Screen.PrimaryScreen!.Bounds;
        target = new Vector2(p.Left + p.Width / 2f, p.Top + p.Height / 2f);
    }

    Vector2 RestAt(Leg leg, Vector2 at, float h) => at + Dir(h + leg.BaseAngle) * leg.Reach * s;

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
            if (!leg.Stepping) continue;
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
            if (leg.Stepping) continue;
            Vector2 rest = RestAt(leg, pos, heading);
            float d = Vector2.Distance(leg.Foot, rest);
            // A foot drifting toward the body midline (after a sharp turn) has to move now.
            bool crossing = Vector2.Dot(leg.Foot - pos, right * leg.Side) < 8 * s;
            bool urgent = crossing || d > StepDist * s * 2.5f;

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
        foreach (var leg in legs) lifted += leg.Lift;
        return (13 + MathF.Sin(time * 2.1f) * 0.6f) * s - lifted * 0.12f;
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

    static readonly Color LineBlue = Color.FromArgb(150, 172, 255);
    static readonly Color BodyRed = Color.FromArgb(255, 64, 96);
    static readonly float[] HipAlong = { 7f, 5f, 3f, 1f };

    void DrawThreads(Point o)
    {
        // Dragline left behind.
        if (trail.Count > 1)
        {
            using var pen = new Pen(Color.FromArgb(45, LineBlue), 1f);
            var pts = new PointF[trail.Count];
            for (int i = 0; i < trail.Count; i++) pts[i] = L(trail[i], o);
            g.DrawLines(pen, pts);
        }

        var body = BodyOnScreen();
        using var nodeFill = new SolidBrush(Color.FromArgb(230, 12, 14, 30));

        // Silk anchored to highlighted spots, ending in a node.
        foreach (var gl in glitches)
        {
            if (!gl.IsAnchor) continue;
            float a = Math.Clamp(1 - gl.Age / gl.Life, 0, 1);
            using var pen = new Pen(Color.FromArgb((int)(110 * a), LineBlue), 1f);
            g.DrawLine(pen, L(body, o), L(gl.Center, o));
            Node(gl.Center, 1.8f * s, o, nodeFill, pen);
        }

        // Flickering burst of edges while it's busy glitching.
        if (glitches.Count > 2)
        {
            using var pen = new Pen(Color.FromArgb(110, LineBlue), 1f);
            for (int i = 0; i < 5; i++)
            {
                float a = (float)(rng.NextDouble() * Math.PI * 2);
                float len = (14 + (float)rng.NextDouble() * 30) * s;
                var end = body + new Vector2(MathF.Cos(a), MathF.Sin(a)) * len;
                g.DrawLine(pen, L(body, o), L(end, o));
                Node(end, 1.4f * s, o, nodeFill, pen);
            }
        }
    }

    void DrawSpider(Point o)
    {
        var fwd = Dir(heading);
        var right = new Vector2(-fwd.Y, fwd.X);
        float bodyH = BodyHeight();

        // A soft shadow on the ground under the raised body.
        using (var shadow = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
        {
            var c = L(pos, o);
            g.FillEllipse(shadow, c.X - 11 * s, c.Y - 7 * s, 22 * s, 14 * s);
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

        using var limb = new Pen(Color.FromArgb(235, LineBlue), 1.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var ring = new Pen(Color.FromArgb(245, 255, 255, 255), 1.1f * s);
        using var nodeFill = new SolidBrush(Color.FromArgb(235, 12, 14, 30));
        using var liftedFill = new SolidBrush(Color.FromArgb(235, LineBlue));

        foreach (var chain in chains) g.DrawLines(limb, chain);

        // Body: a red box with a node at its core and one at the head.
        var center = P(pos, bodyH, o);
        float deg = heading * 180f / MathF.PI;
        using (var boxPen = new Pen(BodyRed, 1.8f * s))
        using (var boxFill = new SolidBrush(Color.FromArgb(170, 12, 14, 30)))
        using (var core = new SolidBrush(BodyRed))
        {
            var st = g.Save();
            g.TranslateTransform(center.X, center.Y);
            g.RotateTransform(deg);
            g.FillRectangle(boxFill, -9 * s, -4.5f * s, 18 * s, 9 * s);
            g.DrawRectangle(boxPen, -9 * s, -4.5f * s, 18 * s, 9 * s);
            g.FillEllipse(core, -2 * s, -2 * s, 4 * s, 4 * s);
            g.Restore(st);
        }
        NodeAt(P(pos + fwd * 12 * s, bodyH, o), 2.2f * s, nodeFill, ring);

        for (int i = 0; i < legs.Length; i++)
        {
            // Higher joints read as closer to the viewer, so they draw a touch bigger.
            NodeAt(chains[i][1], (1.1f + kneeZ[i] / (60 * s)) * s, nodeFill, ring);
            NodeAt(chains[i][2], 1.3f * s, nodeFill, ring);
            NodeAt(chains[i][3], (1.5f + legs[i].Lift / (12 * s)) * s, legs[i].Stepping ? liftedFill : nodeFill, ring);
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

    void Node(Vector2 p, float r, Point o, Brush fill, Pen ring)
    {
        var c = L(p, o);
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
        foreach (var gl in glitches) gl.Dispose();
        glitches.Clear();
        win.Close();
        win.Dispose();
        g.Dispose();
        canvas.Dispose();
        labelFont.Dispose();
    }
}
