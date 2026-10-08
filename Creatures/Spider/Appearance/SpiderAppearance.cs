using System.Drawing.Drawing2D;
using System.Numerics;
using static WebCrawler.SpiderProportions;

namespace WebCrawler;

/// <summary>
/// Everything the appearance needs to know about the spider for one frame.
/// The behaviour fills this in; the appearance only reads it.
/// </summary>
sealed class SpiderPose
{
    public float Scale;
    public Random Rng;
    public Leg[] Legs;
    public List<Vector2> Trail;
    public List<Glitch> Glitches;

    public Vector2 Pos, Mouth, Cursor;
    public float Heading, BodyHeight, ExtraH, Time;
    public float ShownComfort, ComfortPulse, StartledFlash;
    public bool Sleeping, Dizzy, Wrapping, Held;
}

/// <summary>
/// How the spider looks: a node-and-line creature with a box body whose colour shows how
/// comfortable it is, jointed legs drawn in fake 3D, silk threads, and little mood effects.
/// Swap this class (and SpiderProportions) to give the spider a completely different look.
/// </summary>
sealed class SpiderAppearance : ICreatureAppearance
{
    public string DisplayName => "Spider";
    public string Taxon => "Araneae";

    // Red when wary, through orange and yellow, to green when at ease.
    public Color ComfortColor(float comfort, int alpha = 255)
    {
        float hue = (347 + Math.Clamp(comfort, 0, 1) * 148) % 360;
        return FromHsv(alpha, hue, 0.75f, 1f);
    }

    // ---------- out on the desktop ----------

    public void Draw(Graphics g, Point origin, SpiderPose p)
    {
        DrawThreads(g, origin, p);
        DrawBody(g, origin, p);
    }

    void DrawThreads(Graphics g, Point o, SpiderPose p)
    {
        float s = p.Scale;

        // Dragline left behind.
        if (p.Trail.Count > 1)
        {
            using var pen = new Pen(Color.FromArgb(45, Palette.Line), 1f);
            var pts = new PointF[p.Trail.Count];
            for (int i = 0; i < p.Trail.Count; i++) pts[i] = L(p.Trail[i], o);
            g.DrawLines(pen, pts);
        }

        var body = new Vector2(p.Pos.X, p.Pos.Y - p.BodyHeight * Tilt);
        using var nodeFill = new SolidBrush(Color.FromArgb(230, Palette.NodeFill));

        // Dangling from the cursor on a thread while held.
        if (p.Held)
        {
            using var pen = new Pen(Color.FromArgb(160, Palette.Line), 1f);
            g.DrawLine(pen, L(p.Cursor, o), L(body, o));
        }

        // Silk anchored to highlighted spots, ending in a node.
        foreach (var gl in p.Glitches)
        {
            if (!gl.IsAnchor) continue;
            float a = Math.Clamp(1 - gl.Age / gl.Life, 0, 1);
            using var pen = new Pen(Color.FromArgb((int)(110 * a), Palette.Line), 1f);
            g.DrawLine(pen, L(body, o), L(gl.Center, o));
            Node(g, L(gl.Center, o), 1.8f * s, nodeFill, pen);
        }

        // Flickering burst of edges while it's busy glitching.
        if (p.Glitches.Count > 2)
        {
            using var pen = new Pen(Color.FromArgb(110, Palette.Line), 1f);
            for (int i = 0; i < 5; i++)
            {
                float a = (float)(p.Rng.NextDouble() * Math.PI * 2);
                float len = (14 + (float)p.Rng.NextDouble() * 30) * s;
                var end = body + new Vector2(MathF.Cos(a), MathF.Sin(a)) * len;
                g.DrawLine(pen, L(body, o), L(end, o));
                Node(g, L(end, o), 1.4f * s, nodeFill, pen);
            }
        }
    }

    void DrawBody(Graphics g, Point o, SpiderPose p)
    {
        float s = p.Scale;
        var fwd = Dir(p.Heading);
        var right = new Vector2(-fwd.Y, fwd.X);
        float bodyH = p.BodyHeight;
        var center = P(p.Pos, bodyH, o);

        // Nearly invisible disc so the cursor can grab the spider around its body.
        using (var hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
            g.FillEllipse(hit, center.X - 20 * s, center.Y - 20 * s, 40 * s, 40 * s);

        // A soft shadow on the ground under the raised body, smaller the higher it is.
        float shrink = Math.Clamp(1 - p.ExtraH / (80 * s), 0.4f, 1.1f);
        using (var shadow = new SolidBrush(Color.FromArgb((int)(55 * shrink), 0, 0, 0)))
        {
            var c = L(p.Pos, o);
            g.FillEllipse(shadow, c.X - 11 * s * shrink, c.Y - 7 * s * shrink, 22 * s * shrink, 14 * s * shrink);
        }

        var legs = p.Legs;
        var chains = new PointF[legs.Length][];
        var kneeZ = new float[legs.Length];
        for (int i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            var hip = p.Pos + fwd * HipAlong[leg.K] * s + right * leg.Side * HipSide * s;
            SolveLeg(leg, hip, bodyH, right * leg.Side, s, out var knee, out kneeZ[i], out var ankle, out float ankleZ, out var foot);
            chains[i] = new[] { P(hip, bodyH, o), P(knee, kneeZ[i], o), P(ankle, ankleZ, o), P(foot, leg.Lift, o) };
        }

        using var limb = new Pen(Color.FromArgb(235, Palette.Line), 1.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var ring = new Pen(Color.FromArgb(245, 255, 255, 255), 1.1f * s);
        using var nodeFill = new SolidBrush(Color.FromArgb(235, Palette.NodeFill));
        using var liftedFill = new SolidBrush(Color.FromArgb(235, Palette.Line));

        foreach (var chain in chains) g.DrawLines(limb, chain);

        DrawBodyBox(g, center, p.Heading, s, p.ShownComfort, p.ComfortPulse);
        var head = P(p.Pos + fwd * HeadAhead * s, bodyH, o);
        Node(g, head, 2.2f * s, nodeFill, ring);

        for (int i = 0; i < legs.Length; i++)
        {
            // Higher joints read as closer to the viewer, so they draw a touch bigger.
            Node(g, chains[i][1], (1.1f + kneeZ[i] / (60 * s)) * s, nodeFill, ring);
            Node(g, chains[i][2], 1.3f * s, nodeFill, ring);
            Node(g, chains[i][3], (1.5f + legs[i].Lift / (12 * s)) * s, legs[i].Stepping ? liftedFill : nodeFill, ring);
        }

        if (p.Wrapping) DrawWrapping(g, P(p.Mouth, bodyH * 0.6f, o), s, p.Time);
        if (p.Dizzy) DrawDizzy(g, head, s, p.Time, nodeFill, ring);
        if (p.Sleeping) DrawSleeping(g, head, s, p.Time);
        if (p.StartledFlash > 0) DrawStartled(g, head, s, p.StartledFlash);
    }

    // The box body with a node at its core, coloured by comfort.
    void DrawBodyBox(Graphics g, PointF center, float heading, float s, float comfort, float pulse)
    {
        float w = BodyLength * s, h = BodyWidth * s;
        using var boxPen = new Pen(ComfortColor(comfort), 1.8f * s);
        using var boxFill = new SolidBrush(Color.FromArgb(170, Palette.NodeFill));
        using var core = new SolidBrush(ComfortColor(comfort));
        var st = g.Save();
        g.TranslateTransform(center.X, center.Y);
        g.RotateTransform(heading * 180f / MathF.PI);
        g.FillRectangle(boxFill, -w / 2, -h / 2, w, h);
        g.DrawRectangle(boxPen, -w / 2, -h / 2, w, h);
        g.FillEllipse(core, -2 * s, -2 * s, 4 * s, 4 * s);
        if (pulse > 0)
        {
            // A ring that swells and fades when it warms to you.
            float grow = (1 - pulse) * 8 * s;
            using var ringPen = new Pen(ComfortColor(comfort, (int)(200 * pulse)), 1.4f * s);
            g.DrawRectangle(ringPen, -w / 2 - grow, -h / 2 - grow, w + grow * 2, h + grow * 2);
        }
        g.Restore(st);
    }

    // Z's built from nodes, drifting up from its head and fading.
    static void DrawSleeping(Graphics g, PointF head, float s, float time)
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
            foreach (var pt in pts) Node(g, pt, 0.9f * s, fill, ring);
        }
    }

    // A red "!" over its head right after a fright.
    static void DrawStartled(Graphics g, PointF head, float s, float flash)
    {
        float alpha = Math.Clamp(flash / 0.8f, 0, 1);
        using var pen = new Pen(Color.FromArgb((int)(255 * alpha), Palette.BodyRed), 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var dot = new SolidBrush(Color.FromArgb((int)(255 * alpha), Palette.BodyRed));
        g.DrawLine(pen, head.X, head.Y - 24 * s, head.X, head.Y - 14 * s);
        g.FillEllipse(dot, head.X - 1.4f * s, head.Y - 11 * s, 2.8f * s, 2.8f * s);
    }

    // The caught fly being spun in silk in front of the face.
    static void DrawWrapping(Graphics g, PointF c, float s, float time)
    {
        using var silk = new SolidBrush(Color.FromArgb(220, 235, 240, 255));
        using var swirl = new Pen(Color.FromArgb(170, Palette.Line), 1f);
        g.FillEllipse(silk, c.X - 2.5f * s, c.Y - 3.5f * s, 5 * s, 7 * s);
        float spin = time * 600;
        for (int i = 0; i < 3; i++)
            g.DrawArc(swirl, c.X - (5 + i * 2) * s, c.Y - (5 + i * 2) * s, (10 + i * 4) * s, (10 + i * 4) * s, spin + i * 120, 140);
    }

    // Little nodes circling its head after a hard landing.
    static void DrawDizzy(Graphics g, PointF head, float s, float time, Brush fill, Pen ring)
    {
        for (int i = 0; i < 3; i++)
        {
            float a = time * 6 + i * MathF.PI * 2 / 3;
            var pt = new PointF(head.X + MathF.Cos(a) * 9 * s, head.Y - 6 * s + MathF.Sin(a) * 3 * s);
            Node(g, pt, 1.3f * s, fill, ring);
        }
    }

    /// <summary>
    /// Three-segment leg in its own vertical plane: femur and tibia solved with two-bone IK
    /// (knee always up), then a metatarsus dropping to the foot at a fixed angle.
    /// </summary>
    static void SolveLeg(Leg leg, Vector2 hip, float hipZ, Vector2 outward, float s,
        out Vector2 knee, out float kneeZ, out Vector2 ankle, out float ankleZ, out Vector2 foot)
    {
        float femur = leg.Reach * Femur * s, tibia = leg.Reach * Tibia * s, meta = leg.Reach * Metatarsus * s;
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

    // ---------- curled up in the nest ----------

    public void DrawResting(Graphics g, PointF center, float size, float comfort, float time)
    {
        // size is roughly the width of the curled-up spider in pixels.
        float s = size / 60f;
        float heading = -0.35f;
        var c = new Vector2(center.X, center.Y);
        var fwd = Dir(heading);
        var right = new Vector2(-fwd.Y, fwd.X);
        float breathe = 1 + MathF.Sin(time * 1.2f) * 0.04f;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var limb = new Pen(Color.FromArgb(225, Palette.Line), Math.Max(1f, 1.2f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var ring = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(0.8f, 1f * s));
        using var nodeFill = new SolidBrush(Color.FromArgb(235, Palette.NodeFill));

        // Legs drawn up over the body: knees raised, feet tucked back in close.
        for (int side = -1; side <= 1; side += 2)
            for (int k = 0; k < 4; k++)
            {
                float angle = heading + side * LegAngles[k];
                var hip = c + fwd * HipAlong[k] * s + right * side * HipSide * s;
                var knee = hip + Dir(angle) * LegReach[k] * 0.42f * s * breathe - new Vector2(0, 7 * s);
                var foot = hip + Dir(heading + side * LegAngles[k] * 0.8f) * LegReach[k] * 0.3f * s;
                g.DrawLines(limb, new[] { V(hip), V(knee), V(foot) });
                Node(g, V(knee), 1.2f * s, nodeFill, ring);
                Node(g, V(foot), 0.9f * s, nodeFill, ring);
            }

        DrawBodyBox(g, V(c), heading, s * breathe, comfort, 0);
        var head = V(c + fwd * HeadAhead * s);
        Node(g, head, 2.2f * s, nodeFill, ring);
        DrawSleeping(g, head, s, time);
        g.SmoothingMode = oldMode;
    }

    // ---------- helpers ----------

    static PointF P(Vector2 xy, float z, Point o) => new(xy.X - o.X, xy.Y - z * Tilt - o.Y);
    static PointF L(Vector2 v, Point o) => new(v.X - o.X, v.Y - o.Y);
    static PointF V(Vector2 v) => new(v.X, v.Y);
    static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));

    static void Node(Graphics g, PointF c, float r, Brush fill, Pen ring)
    {
        g.FillEllipse(fill, c.X - r, c.Y - r, r * 2, r * 2);
        g.DrawEllipse(ring, c.X - r, c.Y - r, r * 2, r * 2);
    }

    static Color FromHsv(int alpha, float h, float sat, float val)
    {
        float c = val * sat;
        float x = c * (1 - MathF.Abs(h / 60 % 2 - 1));
        float m = val - c;
        (float r, float g, float b) = (int)(h / 60) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return Color.FromArgb(alpha, (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}
