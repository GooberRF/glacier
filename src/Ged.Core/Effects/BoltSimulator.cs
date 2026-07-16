using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Effects;

/// <summary>
/// Generates the animated, jittered segmented polyline of a bolt (lightning)
/// emitter between its source and a target point. Endpoints are pinned; interior
/// points are displaced perpendicular to the source→target axis by a jitter that
/// re-seeds on a coarse time grid so the arc visibly flickers. Deterministic for a
/// given (emitter, time bucket) — unit-testable.
/// </summary>
public static class BoltSimulator
{
    /// <summary>Re-jitter rate: the arc is regenerated this many times per second.</summary>
    public const float FlickerHz = 18f;

    /// <summary>The polyline points from <paramref name="source"/> to <paramref name="target"/> at <paramref name="time"/>.</summary>
    public static IReadOnlyList<Vec3> Polyline(BoltEmitter bolt, Vec3 source, Vec3 target, float time)
    {
        ArgumentNullException.ThrowIfNull(bolt);
        int segments = Math.Clamp(bolt.NumSegments, 1, 128);
        var points = new List<Vec3>(segments + 1);

        Vec3 axis = target.Sub(source);
        float len = axis.Length();
        if (len < 1e-4f)
        {
            points.Add(source);
            points.Add(target);
            return points;
        }

        Vec3 dir = axis.Scale(1f / len);
        // Two perpendicular basis vectors for the jitter plane.
        Vec3 up = MathF.Abs(dir.Y) > 0.9f ? new Vec3(1f, 0f, 0f) : new Vec3(0f, 1f, 0f);
        Vec3 p1 = dir.Cross(up).Normalized();
        Vec3 p2 = dir.Cross(p1).Normalized();

        int seed = bolt.Header.Uid != 0 ? bolt.Header.Uid : 0x1B07;
        int bucket = (int)MathF.Floor(time * FlickerHz);
        float jitter = MathF.Max(bolt.Jitter, 0f);
        // A gentle static bow from the control distances (source/target tangent pull).
        float bowMag = (bolt.SrcCtrlDist + bolt.TrgCtrlDist) * 0.25f;

        points.Add(source);
        for (int s = 1; s < segments; s++)
        {
            float f = s / (float)segments;
            Vec3 baseP = source.Add(axis.Scale(f));
            float taper = MathF.Sin(f * MathF.PI); // 0 at ends, 1 at middle

            float a = (SimRandom.Signed(seed, (bucket * 131) + s, 1) * jitter * taper) + (bowMag * taper);
            float b = SimRandom.Signed(seed, (bucket * 131) + s, 2) * jitter * taper;
            Vec3 offset = p1.Scale(a).Add(p2.Scale(b));
            points.Add(baseP.Add(offset));
        }

        points.Add(target);
        return points;
    }

    /// <summary>Whether the bolt is active at <paramref name="t"/> (respects InitiallyOn).</summary>
    public static bool IsActiveAt(BoltEmitter bolt, float t)
    {
        ArgumentNullException.ThrowIfNull(bolt);
        _ = t;
        return bolt.InitiallyOn != 0;
    }
}
