using System.Numerics;
using static WebCrawler.SpiderProportions;

namespace WebCrawler;

/// <summary>One leg's walking state: where its foot is, and the step it's taking.</summary>
sealed class Leg
{
    public int Side, K, Group;
    public float BaseAngle, Reach;
    public Vector2 Foot, From, To;
    public bool Stepping;
    public float T, StepDur, Lift, LiftH, Idle;
}

/// <summary>
/// The spider's gait: feet planted on the ground, stepping in the alternating ripple real spiders
/// use, aiming where the body is about to be, plus legs posed for grooming or dangling in the air.
/// </summary>
sealed partial class Spider
{
    const float StepDist = 18f;

    void BuildLegs()
    {
        for (int i = 0; i < 8; i++)
        {
            int side = i < 4 ? -1 : 1, k = i % 4;
            legs[i] = new Leg
            {
                Side = side,
                K = k,
                Group = (k + (side > 0 ? 1 : 0)) % 2,
                BaseAngle = side * LegAngles[k],
                Reach = LegReach[k],
            };
        }
    }

    void ResetFeet(float jitter = 0)
    {
        foreach (var leg in legs)
        {
            leg.Foot = RestAt(leg, pos, heading) + (jitter > 0 ? RandomInDisk(jitter) : Vector2.Zero);
            leg.Lift = 0;
            leg.Stepping = false;
        }
    }

    Vector2 RestAt(Leg leg, Vector2 at, float h) => at + Dir(h + leg.BaseAngle) * leg.Reach * reachMul * s;

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
            if (!leg.Stepping || LegPosed(leg)) continue;
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
            if (LegPosed(leg)) { PoseLeg(leg, dt); continue; }
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
    float CurrentBodyHeight()
    {
        float lifted = 0;
        if (mode is not (Mode.Held or Mode.Thrown))
            foreach (var leg in legs) lifted += leg.Lift;
        // Slow, deep breaths while asleep; held breath while frozen.
        float breath = fidget == Fidget.Freeze ? 0
                     : mode == Mode.Sleep ? MathF.Sin(time * 1.2f) * 1f
                     : MathF.Sin(time * 2.1f) * 0.6f;
        return (BodyHeight + breath) * s - lifted * 0.12f + extraH;
    }

    Vector2 BodyOnScreen() => new(pos.X, pos.Y - CurrentBodyHeight() * Tilt);
}
