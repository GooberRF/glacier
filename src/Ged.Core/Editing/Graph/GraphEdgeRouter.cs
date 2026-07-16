using System;
using System.Collections.Generic;

namespace Ged.Core.Editing.Graph;

/// <summary>A 2D point in graph space (framework-free).</summary>
public readonly record struct GraphPoint(double X, double Y);

/// <summary>An axis-aligned rectangle in graph space (framework-free).</summary>
public readonly record struct GraphRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    /// <summary>This rect grown by <paramref name="amount"/> on every side.</summary>
    public GraphRect Inflate(double amount) =>
        new(X - amount, Y - amount, Width + (2 * amount), Height + (2 * amount));

    public bool Contains(GraphPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
}

/// <summary>One cubic bezier segment of a routed edge path.</summary>
public readonly record struct GraphBezierSegment(GraphPoint P0, GraphPoint C1, GraphPoint C2, GraphPoint P1)
{
    /// <summary>Evaluates the cubic at parameter <paramref name="t"/> ∈ [0, 1].</summary>
    public GraphPoint At(double t)
    {
        double u = 1 - t;
        double a = u * u * u;
        double b = 3 * u * u * t;
        double c = 3 * u * t * t;
        double d = t * t * t;
        return new GraphPoint(
            (a * P0.X) + (b * C1.X) + (c * C2.X) + (d * P1.X),
            (a * P0.Y) + (b * C1.Y) + (c * C2.Y) + (d * P1.Y));
    }
}

/// <summary>
/// A routed edge path: the cubic bezier segments the canvas draws plus the flattened
/// polyline used for hit-testing, obstacle checks, and CPU-rendered artifacts.
/// </summary>
public sealed class GraphEdgePath
{
    internal GraphEdgePath(IReadOnlyList<GraphBezierSegment> segments, IReadOnlyList<GraphPoint> polyline)
    {
        Segments = segments;
        Polyline = polyline;
    }

    /// <summary>The smooth cubic segments, in order from source port to target port.</summary>
    public IReadOnlyList<GraphBezierSegment> Segments { get; }

    /// <summary>The flattened path (shared endpoints deduplicated), from start to end.</summary>
    public IReadOnlyList<GraphPoint> Polyline { get; }

    /// <summary>The source port (right-middle of the source rect).</summary>
    public GraphPoint Start => Segments[0].P0;

    /// <summary>The target port (left-middle of the target rect).</summary>
    public GraphPoint End => Segments[^1].P1;

    /// <summary>True when any flattened sub-segment passes through <paramref name="rect"/>.</summary>
    public bool Intersects(GraphRect rect)
    {
        for (int i = 1; i < Polyline.Count; i++)
        {
            if (GraphEdgeRouter.SegmentIntersectsRect(Polyline[i - 1], Polyline[i], rect))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Framework-free edge router shared by the Link Graph and Dependency Graph. An edge
/// runs from the source rect's right-middle port to the target rect's left-middle
/// port as a smooth S-shaped cubic bezier (horizontal control offsets proportional
/// to the horizontal distance, minimum 40). When the flattened curve passes through
/// a non-endpoint node rect (inflated by a margin), a detour waypoint is inserted
/// above or below the obstacle — whichever side is nearer — and the path is rebuilt
/// as smooth segments through it; this corridor-avoidance repeats a couple of times
/// (no full path search). The flattened polyline is exposed for hit-testing.
/// </summary>
public static class GraphEdgeRouter
{
    /// <summary>Default inflation margin applied to obstacle rects.</summary>
    public const double DefaultMargin = 8;

    private const int FlattenSteps = 20;
    private const int MaxDetourPasses = 6;
    private const double MinControlOffset = 40;
    private const double BaseClearance = 12;

    /// <summary>
    /// Routes an edge from <paramref name="source"/> to <paramref name="target"/>
    /// around <paramref name="obstacles"/> (which must not include the two endpoint
    /// rects). Obstacles that already contain a port are ignored — they cannot be
    /// avoided.
    /// </summary>
    public static GraphEdgePath Route(
        GraphRect source, GraphRect target, IReadOnlyList<GraphRect> obstacles, double margin = DefaultMargin)
    {
        ArgumentNullException.ThrowIfNull(obstacles);

        var start = new GraphPoint(source.Right, source.CenterY);
        var end = new GraphPoint(target.X, target.CenterY);

        var inflated = new List<GraphRect>(obstacles.Count);
        foreach (GraphRect o in obstacles)
        {
            GraphRect r = o.Inflate(margin);
            if (!r.Contains(start) && !r.Contains(end))
            {
                inflated.Add(r);
            }
        }

        var waypoints = new List<GraphPoint> { start, end };
        var clearanceOf = new Dictionary<int, double>();   // obstacle index → last clearance used
        var waypointOf = new Dictionary<int, int>();       // obstacle index → waypoint list index

        List<GraphBezierSegment> segments = BuildSegments(waypoints);
        for (int pass = 0; pass < MaxDetourPasses; pass++)
        {
            if (!FindFirstHit(segments, inflated, out int obstacleIdx, out int segmentIdx, out GraphPoint hit))
            {
                break;
            }

            GraphRect ob = inflated[obstacleIdx];
            double clearance = clearanceOf.TryGetValue(obstacleIdx, out double prev) ? prev * 2 : BaseClearance;
            clearanceOf[obstacleIdx] = clearance;

            // Detour around the nearer horizontal edge of the obstacle.
            bool above = hit.Y <= ob.CenterY;
            var wp = new GraphPoint(ob.CenterX, above ? ob.Y - clearance : ob.Bottom + clearance);

            if (waypointOf.TryGetValue(obstacleIdx, out int existing))
            {
                waypoints[existing] = wp; // push the existing detour further out
            }
            else
            {
                int insertAt = segmentIdx + 1;
                waypoints.Insert(insertAt, wp);
                foreach (int key in new List<int>(waypointOf.Keys))
                {
                    if (waypointOf[key] >= insertAt)
                    {
                        waypointOf[key]++;
                    }
                }

                waypointOf[obstacleIdx] = insertAt;
            }

            segments = BuildSegments(waypoints);
        }

        return new GraphEdgePath(segments, Flatten(segments));
    }

    /// <summary>True when segment a→b passes through <paramref name="rect"/> (Liang–Barsky clip).</summary>
    public static bool SegmentIntersectsRect(GraphPoint a, GraphPoint b, GraphRect rect)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double t0 = 0, t1 = 1;
        return Clip(-dx, a.X - rect.X, ref t0, ref t1)
            && Clip(dx, rect.Right - a.X, ref t0, ref t1)
            && Clip(-dy, a.Y - rect.Y, ref t0, ref t1)
            && Clip(dy, rect.Bottom - a.Y, ref t0, ref t1)
            && t0 <= t1;
    }

    private static bool Clip(double p, double q, ref double t0, ref double t1)
    {
        if (Math.Abs(p) < 1e-12)
        {
            return q >= 0;
        }

        double t = q / p;
        if (p < 0)
        {
            if (t > t1)
            {
                return false;
            }

            if (t > t0)
            {
                t0 = t;
            }
        }
        else
        {
            if (t < t0)
            {
                return false;
            }

            if (t < t1)
            {
                t1 = t;
            }
        }

        return true;
    }

    /// <summary>
    /// Smooth segments through the waypoints: every tangent is horizontal and points
    /// rightward, so consecutive segments join with a continuous direction (and a
    /// lone segment is the classic S-curve between ports).
    /// </summary>
    private static List<GraphBezierSegment> BuildSegments(List<GraphPoint> waypoints)
    {
        var segments = new List<GraphBezierSegment>(waypoints.Count - 1);
        for (int i = 0; i < waypoints.Count - 1; i++)
        {
            GraphPoint a = waypoints[i];
            GraphPoint b = waypoints[i + 1];
            double offset = Math.Max(MinControlOffset, Math.Abs(b.X - a.X) * 0.5);
            segments.Add(new GraphBezierSegment(
                a,
                new GraphPoint(a.X + offset, a.Y),
                new GraphPoint(b.X - offset, b.Y),
                b));
        }

        return segments;
    }

    /// <summary>Flattens all segments into one polyline (shared endpoints deduplicated).</summary>
    private static List<GraphPoint> Flatten(List<GraphBezierSegment> segments)
    {
        var points = new List<GraphPoint>((segments.Count * FlattenSteps) + 1) { segments[0].P0 };
        foreach (GraphBezierSegment seg in segments)
        {
            for (int i = 1; i <= FlattenSteps; i++)
            {
                points.Add(seg.At((double)i / FlattenSteps));
            }
        }

        return points;
    }

    /// <summary>
    /// Finds the earliest flattened sub-segment (walking the path from its start)
    /// that passes through any obstacle; ties on the same sub-segment break on the
    /// lower obstacle index.
    /// </summary>
    private static bool FindFirstHit(
        List<GraphBezierSegment> segments,
        List<GraphRect> obstacles,
        out int obstacleIdx,
        out int segmentIdx,
        out GraphPoint hitPoint)
    {
        obstacleIdx = -1;
        segmentIdx = -1;
        hitPoint = default;
        if (obstacles.Count == 0)
        {
            return false;
        }

        for (int s = 0; s < segments.Count; s++)
        {
            GraphPoint prev = segments[s].P0;
            for (int i = 1; i <= FlattenSteps; i++)
            {
                GraphPoint cur = segments[s].At((double)i / FlattenSteps);
                for (int o = 0; o < obstacles.Count; o++)
                {
                    if (SegmentIntersectsRect(prev, cur, obstacles[o]))
                    {
                        obstacleIdx = o;
                        segmentIdx = s;
                        hitPoint = prev;
                        return true;
                    }
                }

                prev = cur;
            }
        }

        return false;
    }
}
