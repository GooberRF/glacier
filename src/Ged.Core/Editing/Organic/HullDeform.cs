using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing.Organic;

/// <summary>A geometry-edit failure: a complete sentence plus what to change (or null).</summary>
public sealed record GeoEditError(string Message, string? Hint);

/// <summary>
/// The point sets behind the hull-mode <c>geo</c> edits (ORGANIC-GEOMETRY-SPEC §2.1–§2.3). Everything
/// runs on the brush's WORLD vertices in double precision; callers snap the results to the 1/1024 m
/// grid and rebuild with <see cref="HullBrush.TryRebuild(Brush, IReadOnlyList{Vec3d}, float, double, out HullBrushError?)"/>.
/// <para>
/// Slicing (twist / bend / taper): the brush is cut into <c>n</c> equal slabs along the deform axis at
/// grid-snapped planes. The points where the brush's edges cross a cut plane are computed ONCE per plane
/// (reduced to the convex corners of the cross-section) and handed to both neighbouring slabs, so after
/// deforming and snapping both slabs carry bit-identical copies. Twist and taper keep every point's
/// axis coordinate, so a cut stays the exact axis plane it started on and the two slabs abut on one
/// identical planar face (W4). A bend tilts each cut onto a radial plane; its cross-section is placed on
/// the nearest plane that grid points represent exactly (<see cref="BendCap"/>), so bent slabs abut on one
/// identical planar face too.
/// </para>
/// </summary>
public static class HullDeform
{
    /// <summary>The thinnest slab a slice may produce, metres (the hull builder's minimum extent).</summary>
    public const double MinSlab = 0.01;

    /// <summary>The largest lattice step (grid cells per axis) a bend cap's in-plane direction may use.</summary>
    private const int BendLattice = 64;

    /// <summary>A cut closer than this to a vertex (metres) moves onto the vertex.</summary>
    public const double CutClearance = 0.005;

    /// <summary>Cross-section corners closer than this to their neighbour (metres) are dropped.</summary>
    public const double MinSectionEdge = 0.01;

    /// <summary>
    /// Cross-section corners that stick out less than this from the chord of their neighbours (metres)
    /// are dropped: comfortably above the hull builder's 2 mm sliver altitude after deforming and snapping,
    /// so both slabs keep exactly the same corners.
    /// </summary>
    public const double MinSectionAltitude = 0.004;

    /// <summary>The world position of every pool vertex that a face uses, in ascending pool index.</summary>
    public static List<Vec3d> WorldVertices(Brush b)
    {
        ArgumentNullException.ThrowIfNull(b);
        Geometry g = b.Geometry;
        var used = new SortedSet<int>(g.Faces.SelectMany(f => f.Vertices.Select(v => v.Index)).Where(i => i >= 0 && i < g.Vertices.Count));
        return used.Select(i => World(b, g.Vertices[i])).ToList();
    }

    /// <summary><paramref name="local"/> in world space, in double: position + x·Right + y·Up + z·Forward.</summary>
    public static Vec3d World(Brush b, Vec3 local)
    {
        Mat3 r = b.Rotation;
        double x = local.X, y = local.Y, z = local.Z;
        return new Vec3d(
            b.Position.X + (x * r.Right.X) + (y * r.Up.X) + (z * r.Forward.X),
            b.Position.Y + (x * r.Right.Y) + (y * r.Up.Y) + (z * r.Forward.Y),
            b.Position.Z + (x * r.Right.Z) + (y * r.Up.Z) + (z * r.Forward.Z));
    }

    /// <summary>The world bounds (min, max) of the brush's used vertices.</summary>
    public static (Vec3d Min, Vec3d Max) WorldBounds(IReadOnlyList<Vec3d> points)
    {
        double[] lo = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        double[] hi = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (Vec3d p in points)
        {
            for (int a = 0; a < 3; a++)
            {
                lo[a] = Math.Min(lo[a], p[a]);
                hi[a] = Math.Max(hi[a], p[a]);
            }
        }

        return (new Vec3d(lo[0], lo[1], lo[2]), new Vec3d(hi[0], hi[1], hi[2]));
    }

    /// <summary>
    /// Jitter-then-rehull: every used world vertex moves by an independent uniform offset in
    /// ±<paramref name="amount"/> per axis (three draws per vertex, x then y then z, always all three,
    /// so the stream does not depend on which axes are zero), then snaps to the construction grid.
    /// </summary>
    public static List<Vec3d> Jitter(Brush b, Vec3d amount, OrganicRandom rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        var result = new List<Vec3d>();
        foreach (Vec3d p in WorldVertices(b))
        {
            double dx = rng.Range(-1, 1) * amount.X;
            double dy = rng.Range(-1, 1) * amount.Y;
            double dz = rng.Range(-1, 1) * amount.Z;
            result.Add(Snap(new Vec3d(p.X + dx, p.Y + dy, p.Z + dz), HullBrush.Grid));
        }

        return result;
    }

    /// <summary>Every used world vertex snapped to <paramref name="grid"/> (a multiple of the construction grid).</summary>
    public static List<Vec3d> SnapAll(Brush b, double grid) => WorldVertices(b).Select(p => Snap(p, grid)).ToList();

    /// <summary>
    /// Cuts the brush into <paramref name="pieces"/> slabs along <paramref name="def"/>'s axis, deforms
    /// each slab's points and snaps them to the grid. Returns one point list per slab, low end first,
    /// or null with <paramref name="error"/>.
    /// </summary>
    public static List<List<Vec3d>>? Slice(Brush b, DeformMath def, int pieces, out GeoEditError? error) =>
        Slice(b, def, pieces, out _, out error);

    /// <summary>
    /// <see cref="Slice(Brush, DeformMath, int, out GeoEditError?)"/>, also returning the exact plane of every
    /// joint (joint k lies between slab k and slab k+1) on which both neighbouring slabs carry the same cap.
    /// </summary>
    public static List<List<Vec3d>>? Slice(Brush b, DeformMath def, int pieces, out List<GridPlane> joints, out GeoEditError? error)
    {
        joints = new List<GridPlane>();
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(def);
        error = null;
        Geometry g = b.Geometry;
        int axis = def.Axis;
        var world = g.Vertices.Select(v => World(b, v)).ToArray();
        var usedIdx = new SortedSet<int>(g.Faces.SelectMany(f => f.Vertices.Select(v => v.Index)).Where(i => i >= 0 && i < world.Length)).ToList();
        double lo = def.Lo, hi = def.Lo + def.Span;

        // Cut planes on the grid, strictly increasing, each slab at least MinSlab thick.
        var cuts = new double[pieces + 1];
        cuts[0] = lo;
        cuts[pieces] = hi;
        for (int k = 1; k < pieces; k++)
        {
            cuts[k] = HullBrush.SnapValue(lo + (def.Span * k / pieces), HullBrush.Grid);
        }

        double thinnest = Enumerable.Range(1, pieces).Min(k => cuts[k] - cuts[k - 1]);
        if (thinnest < MinSlab)
        {
            int most = Math.Max(1, (int)Math.Floor(def.Span / MinSlab));
            error = new GeoEditError(
                $"pieces = {pieces} makes slabs {Format(thinnest)} m thin; a slab must be at least {Format(MinSlab)} m.",
                $"Use at most {Math.Max(1, Math.Min(most, pieces - 1))} pieces.");
            return null;
        }

        // A vertex just off a cut would make sliver faces against the cut, which the hull builder
        // simplifies differently on the two sides: move the cut onto that vertex instead (the cut's points
        // all share one axis value, so they still snap to one identical plane) — unless that would make a
        // slab thinner than MinSlab.
        for (int k = 1; k < pieces; k++)
        {
            double near = double.PositiveInfinity;
            foreach (int i in usedIdx)
            {
                double w = world[i][axis], d = w - cuts[k];
                if (d != 0 && Math.Abs(d) < CutClearance && (Math.Abs(d) < Math.Abs(near - cuts[k]) || (Math.Abs(d) == Math.Abs(near - cuts[k]) && w < near)))
                {
                    near = w;
                }
            }

            if (!double.IsPositiveInfinity(near) && near - cuts[k - 1] >= MinSlab && cuts[k + 1] - near >= MinSlab)
            {
                cuts[k] = near;
            }
        }

        bool bend = def.Kind == DeformKind.Bend;

        // Undirected face edges, sorted (deterministic cut-point order).
        var edges = new SortedSet<(int, int)>();
        foreach (Face f in g.Faces)
        {
            for (int i = 0; i < f.Vertices.Count; i++)
            {
                int u = f.Vertices[i].Index, v = f.Vertices[(i + 1) % f.Vertices.Count].Index;
                if (u != v && u >= 0 && v >= 0 && u < world.Length && v < world.Length)
                {
                    edges.Add((Math.Min(u, v), Math.Max(u, v)));
                }
            }
        }

        // Each plane's deformed + snapped cross-section, computed once and shared by both slabs using it.
        var sections = new List<Vec3d>[pieces + 1];
        for (int k = 1; k < pieces; k++)
        {
            List<Vec3d> raw = CrossSection(world, usedIdx, edges, axis, cuts[k]);
            if (bend)
            {
                List<Vec3d>? cap = BendCap(raw, def, cuts[k], out GridPlane plane);
                if (cap is null)
                {
                    error = new GeoEditError(
                        $"the bend's cut {k} of {pieces - 1} has a cross-section too thin to place on an exact plane.",
                        $"Use fewer pieces (at most {Math.Max(1, pieces - 1)}), or bend a thicker brush.");
                    return null;
                }

                sections[k] = cap;
                joints.Add(plane);
            }
            else
            {
                sections[k] = raw.Select(p => Snap(def.Apply(p), HullBrush.Grid)).ToList();
                joints.Add(GridPlane.Axis(axis, cuts[k]));
            }
        }

        var result = new List<List<Vec3d>>(pieces);
        for (int k = 1; k <= pieces; k++)
        {
            double a = k == 1 ? lo : cuts[k - 1];
            double z = k == pieces ? hi : cuts[k];
            var pts = new List<Vec3d>();
            foreach (int i in usedIdx)
            {
                double w = world[i][axis];
                bool inside = (w > a && w < z) || (k == 1 && w == lo) || (k == pieces && w == hi);
                if (inside)
                {
                    pts.Add(Snap(def.Apply(world[i]), HullBrush.Grid));
                }
            }

            if (k > 1)
            {
                pts.AddRange(sections[k - 1]);
            }

            if (k < pieces)
            {
                pts.AddRange(sections[k]);
            }

            result.Add(pts);
        }

        if (bend && pieces > 1)
        {
            // A bend cap sits on the nearest exact plane, not on the ideal radial one: every other point of each
            // neighbouring slab must stay clearly on its own side of it, or the slabs would cross at the joint.
            for (int k = 0; k + 1 < pieces; k++)
            {
                GridPlane plane = joints[k];
                double side = Math.Sign(plane.Side(Snap(def.Apply(Centroid(world, usedIdx, axis, cuts[k + 1], hi)), HullBrush.Grid)));
                double below = Clearance(plane, result[k], sections[k + 1], -side), above = Clearance(plane, result[k + 1], sections[k + 1], side);
                if (side == 0 || Math.Min(below, above) < MinSectionAltitude)
                {
                    error = new GeoEditError(
                        $"the bend's cut {k + 1} of {pieces - 1} passes {Format(Math.Max(0, Math.Min(below, above)) * 1000)} mm from a corner of the brush once bent (at least {Format(MinSectionAltitude * 1000)} mm is needed for the pieces to meet on one exact face).",
                        $"Use fewer pieces (at most {Math.Max(1, pieces - 1)}), bend fewer degrees, or bend a brush with fewer corners along its length.");
                    return null;
                }
            }
        }

        return result;
    }

    /// <summary>The centroid of the used vertices beyond the cut (axis coordinate in (c, hi]), a point on the high side.</summary>
    private static Vec3d Centroid(Vec3d[] world, IReadOnlyList<int> used, int axis, double c, double hi)
    {
        double x = 0, y = 0, z = 0;
        int n = 0;
        foreach (int i in used)
        {
            if (world[i][axis] > c && world[i][axis] <= hi)
            {
                x += world[i].X;
                y += world[i].Y;
                z += world[i].Z;
                n++;
            }
        }

        return n == 0 ? world[used[0]] : new Vec3d(x / n, y / n, z / n);
    }

    /// <summary>
    /// How far the points of <paramref name="slab"/> that are not on its cap stay on side <paramref name="side"/>
    /// of <paramref name="plane"/>, metres (negative when one crosses); +infinity when the slab is all cap.
    /// </summary>
    private static double Clearance(GridPlane plane, List<Vec3d> slab, List<Vec3d> cap, double side)
    {
        var onCap = new HashSet<Vec3d>(cap);
        double least = double.PositiveInfinity;
        foreach (Vec3d p in slab)
        {
            if (!onCap.Contains(p))
            {
                least = Math.Min(least, side * plane.Distance(p));
            }
        }

        return least;
    }

    /// <summary>
    /// A bend cut's cap: the cross-section at <c>axis = c</c>, bent, placed on an EXACT plane: the plane through
    /// a grid point next to the bent section that contains the third axis and the integer lattice direction
    /// (p, q) in the (axis, toward) plane, so every grid point A + m (p, q) G + w e3 lies exactly on it. The
    /// direction is chosen to keep the corners nearest their ideal bent positions: tilting it from the ideal
    /// radial line moves the far corners, and a longer lattice step coarsens where corners may sit along it.
    /// Both neighbouring slabs carry the returned corners, so they abut on one identical planar face. Null when
    /// the placed section collapses below a triangle.
    /// </summary>
    internal static List<Vec3d>? BendCap(List<Vec3d> section, DeformMath def, double c, out GridPlane plane)
    {
        int la = def.Axis, ta = def.Toward, wa = 3 - la - ta;
        double mid = def.Lo + (def.Span / 2);
        double angle = (c - mid) / def.BendRadius;
        double ul = Math.Sin(angle), ut = -Math.Cos(angle);
        List<Vec3d> bent = section.Select(def.Apply).ToList();
        double cl = bent.Average(p => p[la]), ct = bent.Average(p => p[ta]);
        double half = bent.Max(p => Math.Abs(((p[la] - cl) * ul) + ((p[ta] - ct) * ut)));
        long al = (long)Math.Round(cl / HullBrush.Grid), at = (long)Math.Round(ct / HullBrush.Grid);

        (int P, int Q) best = (0, 1);
        double bestCost = double.PositiveInfinity;
        for (int p = -BendLattice; p <= BendLattice; p++)
        {
            for (int q = -BendLattice; q <= BendLattice; q++)
            {
                if ((p == 0 && q == 0) || Gcd(Math.Abs(p), Math.Abs(q)) != 1)
                {
                    continue;
                }

                double len = Math.Sqrt((p * p) + (q * q));
                double cos = ((p * ul) + (q * ut)) / len;
                if (cos <= 0)
                {
                    continue;
                }

                double tilt = Math.Sqrt(Math.Max(0, 1 - (cos * cos)));
                double cost = (half * tilt) + (len * HullBrush.Grid / 2);
                if (cost < bestCost - 1e-15)
                {
                    best = (p, q);
                    bestCost = cost;
                }
            }
        }

        (int bp, int bq) = best;
        double step2 = (bp * bp) + (bq * bq);
        var placed = new List<Vec3d>();
        foreach (Vec3d v in bent)
        {
            double dl = (v[la] / HullBrush.Grid) - al, dt = (v[ta] / HullBrush.Grid) - at;
            long m = (long)Math.Round(((dl * bp) + (dt * bq)) / step2, MidpointRounding.AwayFromZero);
            var e = new double[3];
            e[la] = (al + (m * bp)) * HullBrush.Grid;
            e[ta] = (at + (m * bq)) * HullBrush.Grid;
            e[wa] = HullBrush.SnapValue(v[wa], HullBrush.Grid);
            placed.Add(new Vec3d(e[0], e[1], e[2]));
        }

        // Reduce to strictly convex, sliver-free corners in the cap's own 2-D frame (along the lattice line, e3).
        double stepLen = Math.Sqrt(step2) * HullBrush.Grid;
        var frame = new Dictionary<Vec3d, Vec3d>();
        foreach (Vec3d v in placed.Distinct())
        {
            double along = ((((v[la] / HullBrush.Grid) - al) * bp) + (((v[ta] / HullBrush.Grid) - at) * bq)) / step2 * stepLen;
            frame[new Vec3d(along, v[wa], 0)] = v;
        }

        List<Vec3d> corners = DropSlivers(ConvexCorners(frame.Keys.ToList(), 0, 1), 0, 1);
        long nl = bq, nt = -bp;
        var normal = new long[3];
        normal[la] = nl;
        normal[ta] = nt;
        plane = new GridPlane(normal[0], normal[1], normal[2], (nl * al) + (nt * at));
        return corners.Count < 3 ? null : corners.Select(f => frame[f]).ToList();
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    /// <summary>
    /// The brush's cross-section at <c>world[axis] == c</c>: the vertices on the plane plus the points where
    /// edges cross it, reduced to the strictly convex corners of the polygon (collinear and repeated points
    /// dropped), in a deterministic order. The axis coordinate is exactly <paramref name="c"/>.
    /// </summary>
    internal static List<Vec3d> CrossSection(Vec3d[] world, IReadOnlyList<int> used, IEnumerable<(int, int)> edges, int axis, double c)
    {
        var raw = new List<Vec3d>();
        foreach (int i in used)
        {
            if (world[i][axis] == c)
            {
                raw.Add(world[i]);
            }
        }

        foreach ((int u, int v) in edges)
        {
            double du = world[u][axis] - c, dv = world[v][axis] - c;
            if ((du < 0 && dv > 0) || (du > 0 && dv < 0))
            {
                double t = du / (du - dv);
                Vec3d p = world[u].Add(world[v].Sub(world[u]).Scale(t));
                raw.Add(With(p, axis, c));
            }
        }

        (int ua, int wa) = DeformMath.Others(axis);
        return DropSlivers(ConvexCorners(raw, ua, wa), ua, wa);
    }

    /// <summary>
    /// Removes cross-section corners that would make sliver faces (an edge shorter than
    /// <see cref="MinSectionEdge"/>, or a corner within <see cref="MinSectionAltitude"/> of its neighbours'
    /// chord), flattest first (ties: lowest index), until none is left or only a triangle remains.
    /// </summary>
    internal static List<Vec3d> DropSlivers(List<Vec3d> corners, int u, int w)
    {
        var c = new List<Vec3d>(corners);
        while (c.Count > 3)
        {
            int worst = -1;
            double worstScore = double.PositiveInfinity;
            for (int i = 0; i < c.Count; i++)
            {
                Vec3d a = c[(i + c.Count - 1) % c.Count], p = c[i], b = c[(i + 1) % c.Count];
                double edge = Math.Min(Dist2(a, p, u, w), Dist2(p, b, u, w));
                double chord = Dist2(a, b, u, w);
                double altitude = chord < 1e-12 ? 0
                    : Math.Abs(((b[u] - a[u]) * (p[w] - a[w])) - ((b[w] - a[w]) * (p[u] - a[u]))) / chord;
                if (edge >= MinSectionEdge && altitude >= MinSectionAltitude)
                {
                    continue;
                }

                double score = Math.Min(edge, altitude);
                if (score < worstScore)
                {
                    worstScore = score;
                    worst = i;
                }
            }

            if (worst < 0)
            {
                break;
            }

            c.RemoveAt(worst);
        }

        return c;
    }

    private static double Dist2(Vec3d a, Vec3d b, int u, int w) => Math.Sqrt(((a[u] - b[u]) * (a[u] - b[u])) + ((a[w] - b[w]) * (a[w] - b[w])));

    /// <summary>The strictly convex corners of a planar point set (2-D monotone chain on axes <paramref name="u"/>, <paramref name="w"/>).</summary>
    internal static List<Vec3d> ConvexCorners(List<Vec3d> raw, int u, int w)
    {
        var pts = raw.Distinct().OrderBy(p => p[u]).ThenBy(p => p[w]).ToList();
        if (pts.Count < 3)
        {
            return pts;
        }

        double scale = 0;
        foreach (Vec3d p in pts)
        {
            scale = Math.Max(scale, Math.Max(Math.Abs(p[u] - pts[0][u]), Math.Abs(p[w] - pts[0][w])));
        }

        double eps = Math.Max(1e-18, scale * scale * 1e-12);
        double Cross(Vec3d o, Vec3d a, Vec3d b) => ((a[u] - o[u]) * (b[w] - o[w])) - ((a[w] - o[w]) * (b[u] - o[u]));

        var hull = new List<Vec3d>(pts.Count * 2);
        foreach (Vec3d p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= eps)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            Vec3d p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= eps)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>Snaps each component to the nearest multiple of <paramref name="grid"/>.</summary>
    public static Vec3d Snap(Vec3d p, double grid) =>
        new(HullBrush.SnapValue(p.X, grid), HullBrush.SnapValue(p.Y, grid), HullBrush.SnapValue(p.Z, grid));

    private static Vec3d With(Vec3d p, int axis, double value) => axis switch
    {
        0 => p with { X = value },
        1 => p with { Y = value },
        _ => p with { Z = value },
    };

    private static string Format(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>
/// A plane through grid points with an integer normal: N . P = D with P in grid units (1/1024 m), so the on-plane
/// test of a grid point is exact.
/// </summary>
public readonly record struct GridPlane(long Nx, long Ny, long Nz, long D)
{
    /// <summary>The axis plane <c>world[axis] = value</c> (value on the grid).</summary>
    public static GridPlane Axis(int axis, double value)
    {
        long u = (long)Math.Round(value / HullBrush.Grid);
        return axis switch
        {
            0 => new GridPlane(1, 0, 0, u),
            1 => new GridPlane(0, 1, 0, u),
            _ => new GridPlane(0, 0, 1, u),
        };
    }

    /// <summary>The exact side of grid point <paramref name="p"/>: N . P - D in grid units (0 = on the plane).</summary>
    public long Side(Vec3d p) =>
        (Nx * (long)Math.Round(p.X / HullBrush.Grid)) + (Ny * (long)Math.Round(p.Y / HullBrush.Grid)) + (Nz * (long)Math.Round(p.Z / HullBrush.Grid)) - D;

    /// <summary>The signed distance of grid point <paramref name="p"/> from the plane, metres.</summary>
    public double Distance(Vec3d p) => Side(p) * HullBrush.Grid / Math.Sqrt((Nx * Nx) + (Ny * Ny) + (Nz * Nz));
}
