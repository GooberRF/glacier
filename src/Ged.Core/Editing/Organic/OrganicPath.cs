using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing.Organic;

/// <summary>
/// The walkability limits a generator enforces (the kit passes <c>kit.limits</c>; <c>allow_tight</c>
/// passes 0 for the first two). Defaults: the player collision diameter 1.8 m (entity.tbl miner1
/// "$Collision Radius: 0.90"), standing headroom 2 m (Alpine waypoints.h:49) and the 45° incline
/// (waypoints.h:37) — values only; no Alpine code.
/// </summary>
public sealed class WalkLimits
{
    /// <summary>The walkway width the floor must hold everywhere along the path, metres (0: no check).</summary>
    public double MinClearWidth { get; init; } = 1.8;

    /// <summary>The clear height the walkway must have, metres (0: no check).</summary>
    public double MinHeadroom { get; init; } = 2.0;

    /// <summary>The steepest average rise/run of a leg (1 = 45°).</summary>
    public double MaxSlope { get; init; } = 1.0;
}

/// <summary>
/// A floor path for the organic builders (ORGANIC-GEOMETRY-SPEC §3.1): x and z snapped to the 1/1024 m
/// construction grid, y rounded to the 1/16 m floor grid (defined behaviour; the stations report the
/// values used), with the horizontal leg directions and the turn at every interior point.
/// </summary>
public sealed class OrganicPath
{
    /// <summary>The construction grid (metres).</summary>
    public const double G = HullBrush.Grid;

    /// <summary>The floor grid: generated floors and flat tops are multiples of 1/16 m (§0.3 W2).</summary>
    public const double Q = 1.0 / 16;

    private OrganicPath(Vec3d[] points)
    {
        Points = points;
        int legs = points.Length - 1;
        Run = new double[legs];
        Dir = new Vec3d[legs];
        Cumulative = new double[points.Length];
        for (int j = 0; j < legs; j++)
        {
            double dx = points[j + 1].X - points[j].X, dz = points[j + 1].Z - points[j].Z;
            Run[j] = Math.Sqrt((dx * dx) + (dz * dz));
            Dir[j] = Run[j] > 0 ? new Vec3d(dx / Run[j], 0, dz / Run[j]) : new Vec3d(0, 0, 1);
            Cumulative[j + 1] = Cumulative[j] + Run[j];
        }
    }

    /// <summary>The snapped path points.</summary>
    public Vec3d[] Points { get; }

    /// <summary>Horizontal length of each leg.</summary>
    public double[] Run { get; }

    /// <summary>Horizontal unit direction of each leg.</summary>
    public Vec3d[] Dir { get; }

    /// <summary>Horizontal path length up to each point.</summary>
    public double[] Cumulative { get; }

    /// <summary>The number of legs.</summary>
    public int Legs => Run.Length;

    /// <summary>The total horizontal length.</summary>
    public double Length => Cumulative[^1];

    /// <summary>The turn at interior point <paramref name="i"/> in degrees (0 at the ends).</summary>
    public double Turn(int i)
    {
        if (i <= 0 || i >= Points.Length - 1)
        {
            return 0;
        }

        double c = Math.Clamp(Dir[i - 1].Dot(Dir[i]), -1, 1);
        return Math.Acos(c) * 180 / Math.PI;
    }

    /// <summary>The turn in degrees where the last leg meets the first (a closed path).</summary>
    public double ClosedTurn() => Math.Acos(Math.Clamp(Dir[^1].Dot(Dir[0]), -1, 1)) * 180 / Math.PI;

    /// <summary>The station heading at point <paramref name="i"/>: the normalised sum of the two leg directions (a leg direction at the ends).</summary>
    public Vec3d Heading(int i)
    {
        if (i <= 0)
        {
            return Dir[0];
        }

        if (i >= Points.Length - 1)
        {
            return Dir[^1];
        }

        Vec3d s = Dir[i - 1].Add(Dir[i]);
        return s.Length < 1e-9 ? Dir[i] : s.Normalized();
    }

    /// <summary>Right of a horizontal heading <paramref name="t"/>: (t.z, 0, −t.x) — +X when facing +Z.</summary>
    public static Vec3d Right(Vec3d t) => new(t.Z, 0, -t.X);

    /// <summary>Yaw in degrees of a horizontal heading (0 faces +Z, positive turns toward +X).</summary>
    public static double Yaw(Vec3d t)
    {
        double deg = Math.Atan2(t.X, t.Z) * 180 / Math.PI;
        return Math.Round(deg, 9) + 0.0;
    }

    /// <summary>Rounds to the 1/16 m floor grid.</summary>
    public static double RoundQ(double y) => HullBrush.SnapValue(y, Q);

    /// <summary>Snaps to the construction grid.</summary>
    public static double SnapG(double v) => HullBrush.SnapValue(v, G);

    /// <summary>
    /// Snaps and validates a path: <paramref name="minCount"/> to 256 points, legs at least
    /// <paramref name="minLeg"/> metres apart horizontally, turns at most <paramref name="maxTurn"/> degrees.
    /// </summary>
    public static OrganicPath? Create(IReadOnlyList<Vec3d> raw, double minLeg, double maxTurn, out GeoEditError? error)
    {
        ArgumentNullException.ThrowIfNull(raw);
        error = null;
        if (raw.Count < 2 || raw.Count > 256)
        {
            error = new GeoEditError($"'path' must list 2 to 256 floor points (got {raw.Count}).", "For example path = {{0, 0, 0}, {0, 0, 30}, {20, 0, 50}}.");
            return null;
        }

        var pts = new Vec3d[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            Vec3d p = raw[i];
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z))
            {
                error = new GeoEditError($"path[{i + 1}] must be a vec, {{x, y, z}} or {{x=, y=, z=}} of finite numbers.", null);
                return null;
            }

            if (Math.Abs(p.X) > OrganicLimits.MaxCoordinate - 64 || Math.Abs(p.Y) > OrganicLimits.MaxCoordinate - 64 || Math.Abs(p.Z) > OrganicLimits.MaxCoordinate - 64)
            {
                error = new GeoEditError($"path[{i + 1}] lies more than {OrganicLimits.MaxCoordinate - 64} m from the origin.",
                    $"Keep generated geometry within ±{OrganicLimits.MaxCoordinate - 64} m, where every grid point is exact.");
                return null;
            }

            pts[i] = new Vec3d(SnapG(p.X), RoundQ(p.Y), SnapG(p.Z));
        }

        var path = new OrganicPath(pts);
        for (int j = 0; j < path.Legs; j++)
        {
            if (path.Run[j] < minLeg)
            {
                error = new GeoEditError(
                    $"path points {j + 1} and {j + 2} are only {F(path.Run[j])} m apart horizontally; they must be at least {F(minLeg)} m apart.",
                    "Remove one of them, or move it further away.");
                return null;
            }
        }

        for (int i = 1; i < pts.Length - 1; i++)
        {
            double turn = path.Turn(i);
            if (turn > maxTurn + 1e-9)
            {
                error = new GeoEditError($"the path turns {F(turn)}° at point {i + 1}.",
                    $"Add a point to round the corner (at most {F(maxTurn)}° per point).");
                return null;
            }
        }

        return path;
    }

    /// <summary>Linear interpolation of per-point <paramref name="values"/> at horizontal path length <paramref name="s"/>.</summary>
    public double Interpolate(IReadOnlyList<double> values, double s)
    {
        if (values.Count == 1)
        {
            return values[0];
        }

        for (int j = 0; j < Legs; j++)
        {
            if (s <= Cumulative[j + 1] || j == Legs - 1)
            {
                double f = Run[j] > 0 ? Math.Clamp((s - Cumulative[j]) / Run[j], 0, 1) : 0;
                return values[j] + ((values[j + 1] - values[j]) * f);
            }
        }

        return values[^1];
    }

    internal static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>One station of a generated tube: the floor point, its walk floor, width, clear height and heading.</summary>
public sealed record TubeStation(Vec3d Pos, double FloorY, double Width, double Height, double Heading);

/// <summary>
/// One climbing (or falling) leg of a tube: where it starts and ends in path order, the signed rise, the
/// horizontal run, heading and the walkway width guaranteed along it.
/// </summary>
public sealed record TubeClimb(Vec3d From, Vec3d To, double Rise, double Run, double Heading, double Width);

/// <summary>The output of a tube builder: brushes in station order plus the record data.</summary>
public sealed class TubeResult
{
    /// <summary>The air brushes (UID 0; the caller assigns UIDs), in station order.</summary>
    public List<Brush> Brushes { get; } = new();

    /// <summary>How many of <see cref="Brushes"/> are air segments (all of them, for the builders in this batch).</summary>
    public int AirCount { get; set; }

    /// <summary>The floor faces of every air segment (world corners), for floor queries.</summary>
    public List<Vec3d[]> FloorFaces { get; } = new();

    /// <summary>The stations, path order.</summary>
    public List<TubeStation> Stations { get; } = new();

    /// <summary>The climbing or falling legs.</summary>
    public List<TubeClimb> Climbs { get; } = new();

    /// <summary>The common top height of a canyon (null for a cave or passage).</summary>
    public double? TopY { get; set; }

    /// <summary>Ring re-draws and roughness fallbacks it took to place every segment.</summary>
    public int Retries { get; set; }

    /// <summary>The horizontal path length.</summary>
    public double Length { get; set; }

    /// <summary>The bounds of every brush.</summary>
    public Vec3d Min { get; set; }

    /// <summary>The bounds of every brush.</summary>
    public Vec3d Max { get; set; }

    /// <summary>The walkway width guaranteed along the path (0 when not checked).</summary>
    public double ClearWidth { get; set; }

    /// <summary>
    /// The walk floor at (x, z): the lowest height of the floor faces whose outline contains the point
    /// (within 1 mm), or null when none does.
    /// </summary>
    public double? FloorAt(double x, double z)
    {
        double? best = null;
        foreach (Vec3d[] f in FloorFaces)
        {
            if (!Inside(f, x, z, 0.001))
            {
                continue;
            }

            (Vec3d n, Vec3d c) = ConvexHull3.NewellPlane(f, Enumerable.Range(0, f.Length).ToArray());
            double y = Math.Abs(n.Y) < 1e-9 ? c.Y : c.Y - (((n.X * (x - c.X)) + (n.Z * (z - c.Z))) / n.Y);
            if (best is null || y < best)
            {
                best = y;
            }
        }

        return best;
    }

    /// <summary>True when (x, z) lies inside the convex XZ polygon (or within <paramref name="tolerance"/> of it).</summary>
    internal static bool Inside(Vec3d[] poly, double x, double z, double tolerance)
    {
        // Orientation-free: the point is inside when it is on the same side of every edge (or on one).
        int sign = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            Vec3d a = poly[i], b = poly[(i + 1) % poly.Length];
            double ex = b.X - a.X, ez = b.Z - a.Z;
            double len = Math.Sqrt((ex * ex) + (ez * ez));
            if (len < 1e-12)
            {
                continue;
            }

            double cross = ((ex * (z - a.Z)) - (ez * (x - a.X))) / len;
            if (Math.Abs(cross) <= tolerance)
            {
                continue;
            }

            int s = Math.Sign(cross);
            if (sign == 0)
            {
                sign = s;
            }
            else if (s != sign)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The polyline position, walk floor, width, height and heading at horizontal length <paramref name="s"/> along the stations.</summary>
    public (Vec3d Pos, double Floor, double Width, double Height, Vec3d Heading) Sample(double s)
    {
        if (Stations.Count == 1)
        {
            TubeStation only = Stations[0];
            return (only.Pos, only.FloorY, only.Width, only.Height, HeadingVector(only.Heading));
        }

        double acc = 0;
        for (int i = 0; i + 1 < Stations.Count; i++)
        {
            TubeStation a = Stations[i], b = Stations[i + 1];
            double dx = b.Pos.X - a.Pos.X, dz = b.Pos.Z - a.Pos.Z;
            double len = Math.Sqrt((dx * dx) + (dz * dz));
            if (s <= acc + len || i + 2 == Stations.Count)
            {
                double f = len > 0 ? Math.Clamp((s - acc) / len, 0, 1) : 0;
                var dir = len > 0 ? new Vec3d(dx / len, 0, dz / len) : HeadingVector(a.Heading);
                var pos = new Vec3d(a.Pos.X + (dx * f), 0, a.Pos.Z + (dz * f));
                return (pos with { Y = Math.Max(a.FloorY, b.FloorY) }, Math.Max(a.FloorY, b.FloorY),
                    a.Width + ((b.Width - a.Width) * f), Math.Min(a.Height, b.Height), dir);
            }

            acc += len;
        }

        TubeStation last = Stations[^1];
        return (last.Pos, last.FloorY, last.Width, last.Height, HeadingVector(last.Heading));
    }

    /// <summary>The horizontal length of the station polyline.</summary>
    public double StationLength()
    {
        double acc = 0;
        for (int i = 0; i + 1 < Stations.Count; i++)
        {
            double dx = Stations[i + 1].Pos.X - Stations[i].Pos.X, dz = Stations[i + 1].Pos.Z - Stations[i].Pos.Z;
            acc += Math.Sqrt((dx * dx) + (dz * dz));
        }

        return acc;
    }

    private static Vec3d HeadingVector(double yaw) =>
        new(Math.Sin(yaw * Math.PI / 180), 0, Math.Cos(yaw * Math.PI / 180));

    /// <summary>Fills <see cref="Min"/> / <see cref="Max"/> from the brushes' world vertices.</summary>
    internal void ComputeBounds()
    {
        double[] lo = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        double[] hi = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (Brush b in Brushes)
        {
            foreach (Vec3d p in HullDeform.WorldVertices(b))
            {
                for (int a = 0; a < 3; a++)
                {
                    lo[a] = Math.Min(lo[a], p[a]);
                    hi[a] = Math.Max(hi[a], p[a]);
                }
            }
        }

        Min = new Vec3d(lo[0], lo[1], lo[2]);
        Max = new Vec3d(hi[0], hi[1], hi[2]);
    }
}
