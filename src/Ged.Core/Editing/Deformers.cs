using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Vertex-set deformers shared by the brush, face and vertex tool panels
/// (Stretch, Bend, Twist, Jitter, Align, snap-to-grid). Each mutates the vertex
/// pool of a brush <see cref="Geometry"/> in place over a chosen set of pool
/// indices and re-derives affected face planes. Pure and deterministic (jitter
/// takes an explicit seed).
/// </summary>
public static class Deformers
{
    private static IEnumerable<int> AllIndices(Geometry g)
    {
        for (int i = 0; i < g.Vertices.Count; i++)
        {
            yield return i;
        }
    }

    private static ICollection<int> Resolve(Geometry g, IReadOnlyCollection<int>? indices) =>
        indices is { Count: > 0 } ? new HashSet<int>(indices) : new HashSet<int>(AllIndices(g));

    /// <summary>Scales the selected vertices about their own centre by a per-axis factor.</summary>
    public static void Stretch(Geometry g, Vec3 factor, IReadOnlyCollection<int>? indices = null)
    {
        ICollection<int> set = Resolve(g, indices);
        Vec3 c = CentreOf(g, set);
        foreach (int i in set)
        {
            Vec3 d = g.Vertices[i].Sub(c);
            g.Vertices[i] = c.Add(new Vec3(d.X * factor.X, d.Y * factor.Y, d.Z * factor.Z));
        }

        GeometryUtil.RecomputeAllPlanes(g);
    }

    /// <summary>
    /// Twists the selected vertices about <paramref name="axis"/>: the rotation
    /// angle ramps linearly from 0 at the low end of the axis extent to
    /// <paramref name="totalDegrees"/> at the high end.
    /// </summary>
    public static void Twist(Geometry g, int axis, float totalDegrees, IReadOnlyCollection<int>? indices = null)
    {
        ICollection<int> set = Resolve(g, indices);
        (float min, float max) = ExtentAlong(g, set, axis);
        float span = max - min;
        if (span < 1e-5f)
        {
            return;
        }

        Vec3 axisVec = TransformMath.Axis(axis);
        Vec3 c = CentreOf(g, set);
        foreach (int i in set)
        {
            Vec3 v = g.Vertices[i];
            float t = (v.Component(axis) - min) / span;
            float angle = TransformMath.DegToRad(totalDegrees * t);
            Mat3 rot = Mat3Math.FromAxisAngle(axisVec, angle);
            // Rotate about the axis line through the set centre.
            Vec3 rel = v.Sub(c);
            g.Vertices[i] = c.Add(rot.Transform(rel));
        }

        GeometryUtil.RecomputeAllPlanes(g);
    }

    /// <summary>
    /// Bends the selected vertices: the extent along <paramref name="lengthAxis"/>
    /// is wrapped into an arc of <paramref name="totalDegrees"/> in the plane of
    /// the length axis and <paramref name="bendAxis"/>.
    /// </summary>
    public static void Bend(Geometry g, int lengthAxis, int bendAxis, float totalDegrees,
        IReadOnlyCollection<int>? indices = null)
    {
        if (lengthAxis == bendAxis || MathF.Abs(totalDegrees) < 1e-4f)
        {
            return;
        }

        ICollection<int> set = Resolve(g, indices);
        (float min, float max) = ExtentAlong(g, set, lengthAxis);
        float span = max - min;
        if (span < 1e-5f)
        {
            return;
        }

        float totalRad = TransformMath.DegToRad(totalDegrees);
        float radius = span / totalRad;
        float mid = (min + max) * 0.5f;

        foreach (int i in set)
        {
            Vec3 v = g.Vertices[i];
            float s = v.Component(lengthAxis) - mid;   // arc length from centre
            float h = v.Component(bendAxis);            // offset from the neutral line
            float angle = s / radius;
            float r = radius - h;
            float newLen = r * MathF.Sin(angle);
            float newBend = radius - (r * MathF.Cos(angle));
            g.Vertices[i] = v
                .WithComponent(lengthAxis, mid + newLen)
                .WithComponent(bendAxis, newBend);
        }

        GeometryUtil.RecomputeAllPlanes(g);
    }

    /// <summary>Randomly perturbs the selected vertices within ±<paramref name="amount"/> per axis.</summary>
    public static void Jitter(Geometry g, float amount, int seed, IReadOnlyCollection<int>? indices = null)
    {
        ICollection<int> set = Resolve(g, indices);
        var rng = new Random(seed);
        foreach (int i in set)
        {
            float dx = (float)((rng.NextDouble() * 2) - 1) * amount;
            float dy = (float)((rng.NextDouble() * 2) - 1) * amount;
            float dz = (float)((rng.NextDouble() * 2) - 1) * amount;
            g.Vertices[i] = g.Vertices[i].Add(new Vec3(dx, dy, dz));
        }

        GeometryUtil.RecomputeAllPlanes(g);
    }

    /// <summary>Aligns the selected vertices to a common value on <paramref name="axis"/> (their mean).</summary>
    public static void Align(Geometry g, int axis, IReadOnlyCollection<int>? indices = null)
    {
        ICollection<int> set = Resolve(g, indices);
        if (set.Count == 0)
        {
            return;
        }

        float sum = 0f;
        foreach (int i in set)
        {
            sum += g.Vertices[i].Component(axis);
        }

        float target = sum / set.Count;
        foreach (int i in set)
        {
            g.Vertices[i] = g.Vertices[i].WithComponent(axis, target);
        }

        GeometryUtil.RecomputeAllPlanes(g);
    }

    /// <summary>Snaps the selected vertices (in local space) to the grid.</summary>
    public static void SnapToGrid(Geometry g, float grid, IReadOnlyCollection<int>? indices = null)
    {
        ICollection<int> set = Resolve(g, indices);
        foreach (int i in set)
        {
            g.Vertices[i] = TransformMath.Snap(g.Vertices[i], grid);
        }

        GeometryUtil.RecomputeAllPlanes(g);
    }

    private static Vec3 CentreOf(Geometry g, ICollection<int> set)
    {
        if (set.Count == 0)
        {
            return default;
        }

        var sum = new Vec3(0, 0, 0);
        foreach (int i in set)
        {
            sum = sum.Add(g.Vertices[i]);
        }

        return sum.Scale(1f / set.Count);
    }

    private static (float Min, float Max) ExtentAlong(Geometry g, ICollection<int> set, int axis)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (int i in set)
        {
            float c = g.Vertices[i].Component(axis);
            min = MathF.Min(min, c);
            max = MathF.Max(max, c);
        }

        return set.Count == 0 ? (0f, 0f) : (min, max);
    }
}
