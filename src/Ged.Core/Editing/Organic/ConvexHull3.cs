using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ged.Core.Editing.Organic;

/// <summary>A double-precision 3D vector (metres) for organic-geometry maths; converted to float only at the grid.</summary>
public readonly record struct Vec3d(double X, double Y, double Z)
{
    public Vec3d Add(Vec3d o) => new(X + o.X, Y + o.Y, Z + o.Z);

    public Vec3d Sub(Vec3d o) => new(X - o.X, Y - o.Y, Z - o.Z);

    public Vec3d Scale(double s) => new(X * s, Y * s, Z * s);

    public double Dot(Vec3d o) => (X * o.X) + (Y * o.Y) + (Z * o.Z);

    public Vec3d Cross(Vec3d o) => new((Y * o.Z) - (Z * o.Y), (Z * o.X) - (X * o.Z), (X * o.Y) - (Y * o.X));

    public double Length => Math.Sqrt(Dot(this));

    /// <summary>The unit vector (zero stays zero).</summary>
    public Vec3d Normalized()
    {
        double l = Length;
        return l > 0 ? Scale(1.0 / l) : this;
    }

    /// <summary>Component <paramref name="axis"/> (0 = X, 1 = Y, 2 = Z).</summary>
    public double this[int axis] => axis switch { 0 => X, 1 => Y, _ => Z };

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:0.######}, {Y:0.######}, {Z:0.######})");
}

/// <summary>
/// One hull face: <paramref name="Corners"/> index <see cref="HullResult.Vertices"/>, wound so the
/// polygon's right-hand (Newell) normal points outward — the winding every Glacier brush face uses
/// (<see cref="BrushFactory.OrientOutward"/>). The plane is <c>Normal·x + Offset = 0</c> (the
/// <see cref="Model.RfPlane"/> convention).
/// </summary>
public sealed record HullFace(int[] Corners, Vec3d Normal, double Offset);

/// <summary>
/// A convex hull: its vertices (ascending source index), the input index of each vertex, the faces
/// (sorted by their corner sequences), the enclosed volume (m³), how many input points ended up inside
/// or on the hull without being a corner (duplicates included), and how many hull vertices were deleted
/// to remove sliver faces.
/// </summary>
public sealed record HullResult(IReadOnlyList<Vec3d> Vertices, IReadOnlyList<int> SourceIndex,
                                IReadOnlyList<HullFace> Faces, double Volume, int InteriorPoints, int Simplified)
{
    /// <summary>The input indices of the hull vertices deleted to remove slivers, in deletion order.</summary>
    public IReadOnlyList<int> SimplifiedSources { get; init; } = Array.Empty<int>();
}

/// <summary>Tuning for <see cref="ConvexHull3.Build(IReadOnlyList{Vec3d}, HullOptions, out string?)"/>.</summary>
public sealed class HullOptions
{
    /// <summary>Metres: two adjacent hull faces join one polygon when every corner is within this of the common plane.</summary>
    public double CoplanarTolerance = 1e-6;

    /// <summary>Metres: the smallest allowed face altitude (a thinner face is a sliver).</summary>
    public double MinAltitude = 0.002;

    /// <summary>False: a sliver face is an error instead of a reason to drop a vertex.</summary>
    public bool Simplify = true;

    /// <summary>Faces with more corners are split into coplanar fan pieces (Cairn LC074, REFERENCE.md:2912).</summary>
    public int MaxFaceCorners = OrganicLimits.MaxFaceCorners;
}

/// <summary>Why a hull could not be built (internal detail behind the public error string).</summary>
internal enum HullFailureKind
{
    OffGrid,
    TooFew,
    Flat,
    Sliver,
    Internal,
}

/// <summary>A hull failure: the kind, the message, the point set's thickness (flat cases) and the source indices involved.</summary>
internal sealed record HullFailure(HullFailureKind Kind, string Message, double Thickness, int[] SourceIndices);

/// <summary>
/// An exact 3D convex hull for organic brushes.
/// <para>
/// <b>Exactness.</b> Every input point must lie on the construction grid <see cref="Grid"/> (1/1024 m,
/// ORGANIC-GEOMETRY-SPEC §0.3), so each coordinate is an integer number of grid steps. All
/// combinatorial decisions — the orientation of a point against a triangle, whether two triangles are
/// coplanar, the enclosed volume used to choose which vertex to delete — are evaluated on those
/// integers in <see cref="Int128"/> and are therefore exact (coordinates up to 2^30 steps: differences
/// &lt; 2^31, plane normals &lt; 2^64, plane tests &lt; 2^96). Only the reported normals, offsets,
/// altitudes and the near-coplanar merge use <c>double</c>.
/// </para>
/// <para>
/// <b>Algorithm.</b> Incremental insertion in input order (ties are broken by input index): a point
/// strictly in front of no triangle is inside or on the hull and is skipped; otherwise every triangle
/// it is in front of <i>or coplanar with</i> is removed and the hole is coned to the point. Testing
/// strictly first and removing non-strictly means no zero-area triangle is ever created and every
/// vertex that a later point makes non-extreme (inside, on a face, or in the middle of an edge) is
/// removed with its triangles, so the final triangulation's vertices are exactly the extreme points.
/// Adjacent coplanar triangles (exact test) are then joined into the hull's true facets, adjacent
/// facets within <see cref="HullOptions.CoplanarTolerance"/> of a common plane are joined when the
/// result stays strictly convex, sliver facets are removed by deleting the vertex that loses the least
/// volume, and facets over <see cref="HullOptions.MaxFaceCorners"/> corners are fan-split.
/// </para>
/// <para>
/// <b>Determinism.</b> Output vertex order = ascending source index; every face starts at its lowest
/// corner index; faces are sorted by their corner sequences. The facets are a property of the point
/// set, so permuting the input changes only the source indices, never the set of faces.
/// </para>
/// </summary>
public static class ConvexHull3
{
    /// <summary>The construction grid step in metres (1/1024 m).</summary>
    public const double Grid = 1.0 / 1024;

    /// <summary>Grid steps per metre.</summary>
    private const double StepsPerMetre = 1024;

    /// <summary>The largest coordinate magnitude accepted, in grid steps (2^30, about 1 048 576 m).</summary>
    private const long MaxSteps = 1L << 30;

    /// <summary>Minimum hull volume (m³) and minimum AABB extent (m) — below either the points are flat.</summary>
    public const double MinVolume = 1e-6;

    /// <summary>The smallest AABB extent of a hull, in metres (matches <c>level.brush</c>'s size check).</summary>
    public const double MinExtent = 0.01;

    /// <summary>
    /// Builds the convex hull of <paramref name="points"/> (each on the 1/1024 m grid). Returns null and
    /// sets <paramref name="error"/> when there are fewer than four distinct points, the points are flat
    /// (volume &lt; 1e-6 m³ or an AABB extent &lt; 0.01 m), a point is off the grid, or (with
    /// <see cref="HullOptions.Simplify"/> false) a face is a sliver.
    /// </summary>
    public static HullResult? Build(IReadOnlyList<Vec3d> points, HullOptions options, out string? error)
    {
        HullResult? r = Build(points, options, out HullFailure? failure);
        error = failure?.Message;
        return r;
    }

    internal static HullResult? Build(IReadOnlyList<Vec3d> points, HullOptions options, out HullFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(options);
        failure = null;

        // Integer grid coordinates.
        var steps = new long[points.Count][];
        for (int i = 0; i < points.Count; i++)
        {
            Vec3d p = points[i];
            long[] s = new long[3];
            for (int a = 0; a < 3; a++)
            {
                double v = p[a] * StepsPerMetre;
                if (!double.IsFinite(v) || Math.Abs(v) > MaxSteps || Math.Round(v) != v)
                {
                    failure = new HullFailure(HullFailureKind.OffGrid,
                        $"point {i} {p} is not on the 1/1024 m grid (or is not finite / too far from the origin).", 0, new[] { i });
                    return null;
                }

                s[a] = (long)v;
            }

            steps[i] = s;
        }

        // Distinct points (exact duplicates keep the first).
        var seen = new HashSet<(long, long, long)>();
        var active = new List<int>();
        for (int i = 0; i < steps.Length; i++)
        {
            if (seen.Add((steps[i][0], steps[i][1], steps[i][2])))
            {
                active.Add(i);
            }
        }

        if (active.Count < 4)
        {
            failure = new HullFailure(HullFailureKind.TooFew, $"only {active.Count} distinct points; a hull needs at least 4.", 0, active.ToArray());
            return null;
        }

        double thinnestAabb = ThinnestAabbExtent(points, active);
        if (thinnestAabb < MinExtent)
        {
            failure = Flat(thinnestAabb, active);
            return null;
        }

        int maxCorners = Math.Max(3, options.MaxFaceCorners);
        var simplified = new List<int>();
        while (true)
        {
            Polytope? poly;
            try
            {
                poly = Polytope.Build(steps, active);
            }
            catch (InvalidOperationException ex)
            {
                failure = new HullFailure(HullFailureKind.Internal, "internal hull error: " + ex.Message, 0, active.ToArray());
                return null;
            }

            if (poly is null)
            {
                failure = Flat(0, active);
                return null;
            }

            List<int[]> facets = poly.Facets();
            facets = MergeNearCoplanar(points, facets, options.CoplanarTolerance);
            double volume = (double)poly.Volume6 / 6.0 / (StepsPerMetre * StepsPerMetre * StepsPerMetre);
            if (volume < MinVolume)
            {
                failure = Flat(Thickness(points, facets), active);
                return null;
            }

            // Slivers: the thinnest facet below MinAltitude (ties: first in facet order).
            int sliver = -1;
            double worst = double.PositiveInfinity;
            int[] sliverTriple = Array.Empty<int>();
            for (int f = 0; f < facets.Count; f++)
            {
                (double alt, int[] triple) = Altitude(points, facets[f]);
                if (alt < options.MinAltitude && alt < worst)
                {
                    worst = alt;
                    sliver = f;
                    sliverTriple = triple;
                }
            }

            if (sliver >= 0)
            {
                if (!options.Simplify)
                {
                    failure = new HullFailure(HullFailureKind.Sliver,
                        string.Create(CultureInfo.InvariantCulture,
                            $"points {sliverTriple[0]}, {sliverTriple[1]} and {sliverTriple[2]} make a sliver face {worst:0.######} m thin (the minimum is {options.MinAltitude:0.######} m)."),
                        worst, sliverTriple);
                    return null;
                }

                int drop = LeastVolumeLoss(steps, active, facets[sliver], poly.Volume6);
                if (drop < 0)
                {
                    failure = new HullFailure(HullFailureKind.Sliver,
                        $"removing the sliver face made by points {string.Join(", ", sliverTriple)} leaves no volume.", worst, sliverTriple);
                    return null;
                }

                active.Remove(drop);
                simplified.Add(drop);
                if (active.Count < 4)
                {
                    failure = new HullFailure(HullFailureKind.Sliver,
                        $"removing sliver faces left only {active.Count} points.", worst, sliverTriple);
                    return null;
                }

                continue;
            }

            return Assemble(points, facets, volume, points.Count, simplified, maxCorners);
        }
    }

    private static HullFailure Flat(double thickness, List<int> active) =>
        new(HullFailureKind.Flat,
            string.Create(CultureInfo.InvariantCulture, $"the points are coplanar (thinnest extent {thickness:0.######} m); a hull needs volume."),
            thickness, active.ToArray());

    private static double ThinnestAabbExtent(IReadOnlyList<Vec3d> points, List<int> active)
    {
        double thinnest = double.PositiveInfinity;
        for (int a = 0; a < 3; a++)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (int i in active)
            {
                lo = Math.Min(lo, points[i][a]);
                hi = Math.Max(hi, points[i][a]);
            }

            thinnest = Math.Min(thinnest, hi - lo);
        }

        return thinnest;
    }

    /// <summary>The polytope's width: the smallest, over facets, of the farthest vertex behind the facet's plane.</summary>
    private static double Thickness(IReadOnlyList<Vec3d> points, List<int[]> facets)
    {
        var all = facets.SelectMany(f => f).Distinct().ToList();
        double best = double.PositiveInfinity;
        foreach (int[] f in facets)
        {
            (Vec3d n, Vec3d c) = NewellPlane(points, f);
            double far = 0;
            foreach (int v in all)
            {
                far = Math.Max(far, -n.Dot(points[v].Sub(c)));
            }

            best = Math.Min(best, far);
        }

        return double.IsFinite(best) ? best : 0;
    }

    /// <summary>
    /// A polygon's altitude: for each edge, the farthest corner from that edge's line; the smallest of
    /// those (a triangle's smallest altitude). Returns the edge's two corners and the far corner.
    /// </summary>
    private static (double Altitude, int[] Triple) Altitude(IReadOnlyList<Vec3d> points, int[] loop)
    {
        double best = double.PositiveInfinity;
        int[] triple = { loop[0], loop[1], loop[2] };
        for (int e = 0; e < loop.Length; e++)
        {
            Vec3d a = points[loop[e]];
            Vec3d b = points[loop[(e + 1) % loop.Length]];
            Vec3d dir = b.Sub(a);
            double len = dir.Length;
            double far = 0;
            int farIdx = loop[(e + 2) % loop.Length];
            for (int k = 0; k < loop.Length; k++)
            {
                double d = len > 0 ? points[loop[k]].Sub(a).Cross(dir).Length / len : 0;
                if (d > far)
                {
                    far = d;
                    farIdx = loop[k];
                }
            }

            if (far < best)
            {
                best = far;
                int[] t = { loop[e], loop[(e + 1) % loop.Length], farIdx };
                Array.Sort(t);
                triple = t;
            }
        }

        return (best, triple);
    }

    /// <summary>
    /// The corner of <paramref name="facet"/> whose deletion loses the least volume (exact, in grid
    /// units; ties: highest source index), or -1 when no deletion leaves a solid.
    /// </summary>
    private static int LeastVolumeLoss(long[][] steps, List<int> active, int[] facet, Int128 volume6)
    {
        int best = -1;
        Int128 bestLoss = Int128.MaxValue;
        foreach (int v in facet.OrderBy(x => x))
        {
            var rest = active.Where(i => i != v).ToList();
            Polytope? p = rest.Count >= 4 ? Polytope.Build(steps, rest) : null;
            if (p is null)
            {
                continue;
            }

            Int128 loss = volume6 - p.Volume6;
            if (loss <= bestLoss)
            {
                bestLoss = loss;
                best = v;
            }
        }

        return best;
    }

    /// <summary>The Newell normal (unit) and centroid of a polygon of source points.</summary>
    internal static (Vec3d Normal, Vec3d Centroid) NewellPlane(IReadOnlyList<Vec3d> points, IReadOnlyList<int> loop)
    {
        Vec3d origin = points[loop[0]];
        double nx = 0, ny = 0, nz = 0, cx = 0, cy = 0, cz = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            Vec3d a = points[loop[i]].Sub(origin);
            Vec3d b = points[loop[(i + 1) % loop.Count]].Sub(origin);
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
            cx += a.X;
            cy += a.Y;
            cz += a.Z;
        }

        int n = loop.Count;
        return (new Vec3d(nx, ny, nz).Normalized(), new Vec3d(cx / n, cy / n, cz / n).Add(origin));
    }

    /// <summary>
    /// Joins adjacent facets whose corners all lie within <paramref name="tolerance"/> of the merged
    /// polygon's plane, when the merged polygon stays strictly convex. Deterministic: pairs are tried
    /// in facet order and the scan restarts after every merge.
    /// </summary>
    private static List<int[]> MergeNearCoplanar(IReadOnlyList<Vec3d> points, List<int[]> facets, double tolerance)
    {
        if (tolerance <= 0)
        {
            return facets;
        }

        bool merged = true;
        while (merged)
        {
            merged = false;
            var owner = new Dictionary<(int, int), int>();
            for (int f = 0; f < facets.Count; f++)
            {
                int[] loop = facets[f];
                for (int i = 0; i < loop.Length; i++)
                {
                    owner[(loop[i], loop[(i + 1) % loop.Length])] = f;
                }
            }

            for (int f = 0; f < facets.Count && !merged; f++)
            {
                int[] a = facets[f];
                for (int i = 0; i < a.Length && !merged; i++)
                {
                    int u = a[i], v = a[(i + 1) % a.Length];
                    if (!owner.TryGetValue((v, u), out int g) || g <= f)
                    {
                        continue;
                    }

                    int[]? joined = Splice(a, i, facets[g], v, u);
                    if (joined is null || !WithinPlane(points, joined, tolerance) || !StrictlyConvex(points, joined))
                    {
                        continue;
                    }

                    facets[f] = joined;
                    facets.RemoveAt(g);
                    merged = true;
                }
            }
        }

        return facets;
    }

    /// <summary>Joins loop <paramref name="a"/> (edge u→v at position <paramref name="ia"/>) with loop <paramref name="b"/> (edge v→u).</summary>
    private static int[]? Splice(int[] a, int ia, int[] b, int v, int u)
    {
        int ib = Array.IndexOf(b, v);
        if (ib < 0 || b[(ib + 1) % b.Length] != u)
        {
            return null;
        }

        // a: … a[ia]=u, a[ia+1]=v …  ;  b: … b[ib]=v, b[ib+1]=u …
        // Merged: u, (b after u … up to v exclusive), v, (a after v … up to u exclusive).
        var result = new List<int> { u };
        for (int k = 2; k < b.Length; k++)
        {
            result.Add(b[(ib + k) % b.Length]);
        }

        result.Add(v);
        for (int k = 2; k < a.Length; k++)
        {
            result.Add(a[(ia + k) % a.Length]);
        }

        return result.Distinct().Count() == result.Count ? result.ToArray() : null;
    }

    private static bool WithinPlane(IReadOnlyList<Vec3d> points, int[] loop, double tolerance)
    {
        (Vec3d n, Vec3d c) = NewellPlane(points, loop);
        return loop.All(i => Math.Abs(n.Dot(points[i].Sub(c))) <= tolerance);
    }

    private static bool StrictlyConvex(IReadOnlyList<Vec3d> points, int[] loop)
    {
        (Vec3d n, _) = NewellPlane(points, loop);
        for (int i = 0; i < loop.Length; i++)
        {
            Vec3d a = points[loop[i]];
            Vec3d b = points[loop[(i + 1) % loop.Length]];
            Vec3d c = points[loop[(i + 2) % loop.Length]];
            if (b.Sub(a).Cross(c.Sub(b)).Dot(n) <= 1e-12)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Canonical output: vertices by source index, faces rotated to their lowest corner, fan-split, sorted.</summary>
    private static HullResult Assemble(IReadOnlyList<Vec3d> points, List<int[]> facets, double volume, int inputCount,
                                       List<int> simplified, int maxCorners)
    {
        int[] sources = facets.SelectMany(f => f).Distinct().OrderBy(i => i).ToArray();
        var remap = new Dictionary<int, int>();
        for (int i = 0; i < sources.Length; i++)
        {
            remap[sources[i]] = i;
        }

        var faces = new List<HullFace>();
        foreach (int[] facet in facets)
        {
            (Vec3d normal, Vec3d centroid) = NewellPlane(points, facet);
            normal = ExactNormal(points, facet) ?? normal;
            double offset = -normal.Dot(centroid);
            int[] loop = facet.Select(i => remap[i]).ToArray();
            int start = Array.IndexOf(loop, loop.Min());
            int[] rotated = new int[loop.Length];
            for (int k = 0; k < loop.Length; k++)
            {
                rotated[k] = loop[(start + k) % loop.Length];
            }

            if (Math.Abs(normal.X) == 1 || Math.Abs(normal.Y) == 1 || Math.Abs(normal.Z) == 1)
            {
                // Axis-exact: the offset from a corner is exact on the grid.
                offset = -normal.Dot(points[facet[0]]);
            }

            foreach (int[] piece in FanSplit(rotated, maxCorners))
            {
                faces.Add(new HullFace(piece, normal, offset));
            }
        }

        faces.Sort((x, y) => CompareCorners(x.Corners, y.Corners));
        var vertices = sources.Select(i => points[i]).ToArray();
        int interior = inputCount - sources.Length - simplified.Count;
        return new HullResult(vertices, sources, faces, volume, interior, simplified.Count)
        {
            SimplifiedSources = simplified.ToArray(),
        };
    }

    /// <summary>
    /// The exact facet normal when the facet's corners are exactly coplanar on the grid (always, except for
    /// near-coplanar merges): the integer cross product of two edges, normalised. An axis-aligned facet
    /// therefore gets an exactly axis-aligned normal.
    /// </summary>
    private static Vec3d? ExactNormal(IReadOnlyList<Vec3d> points, int[] facet)
    {
        static Int128 S(double v) => (Int128)(long)(v * StepsPerMetre);
        Vec3d p0 = points[facet[0]];
        Int128 ax = S(points[facet[1]].X) - S(p0.X), ay = S(points[facet[1]].Y) - S(p0.Y), az = S(points[facet[1]].Z) - S(p0.Z);
        for (int k = 2; k < facet.Length; k++)
        {
            Int128 bx = S(points[facet[k]].X) - S(p0.X), by = S(points[facet[k]].Y) - S(p0.Y), bz = S(points[facet[k]].Z) - S(p0.Z);
            Int128 nx = (ay * bz) - (az * by), ny = (az * bx) - (ax * bz), nz = (ax * by) - (ay * bx);
            if (nx == 0 && ny == 0 && nz == 0)
            {
                continue;
            }

            // Every corner must lie exactly on this plane.
            foreach (int c in facet)
            {
                Int128 dx = S(points[c].X) - S(p0.X), dy = S(points[c].Y) - S(p0.Y), dz = S(points[c].Z) - S(p0.Z);
                if ((nx * dx) + (ny * dy) + (nz * dz) != 0)
                {
                    return null;
                }
            }

            Vec3d n = new((double)nx, (double)ny, (double)nz);
            if (nx == 0 && ny == 0)
            {
                return new Vec3d(0, 0, nz > 0 ? 1 : -1);
            }

            if (nx == 0 && nz == 0)
            {
                return new Vec3d(0, ny > 0 ? 1 : -1, 0);
            }

            if (ny == 0 && nz == 0)
            {
                return new Vec3d(nx > 0 ? 1 : -1, 0, 0);
            }

            return n.Normalized();
        }

        return null;
    }

    /// <summary>Fan-splits a loop that starts at its lowest corner into runs of at most <paramref name="max"/> corners.</summary>
    private static IEnumerable<int[]> FanSplit(int[] loop, int max)
    {
        if (loop.Length <= max)
        {
            yield return loop;
            yield break;
        }

        int next = 1;
        while (next < loop.Length - 1)
        {
            int take = Math.Min(max - 1, loop.Length - next);
            var piece = new int[take + 1];
            piece[0] = loop[0];
            Array.Copy(loop, next, piece, 1, take);
            yield return piece;
            next += take - 1;
        }
    }

    private static int CompareCorners(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>
    /// The exact triangulated hull of a set of grid points (integer coordinates), built incrementally.
    /// Triangles are wound so their right-hand normal points outward.
    /// </summary>
    private sealed class Polytope
    {
        private readonly long[][] _p;
        private readonly List<Tri> _tris = new();
        private readonly Dictionary<(int, int), int> _edges = new();

        private Polytope(long[][] p) => _p = p;

        /// <summary>Six times the enclosed volume, in cubic grid steps (exact).</summary>
        public Int128 Volume6 { get; private set; }

        private sealed class Tri
        {
            public int A, B, C;
            public Int128 Nx, Ny, Nz, D;
            public bool Alive = true;
        }

        /// <summary>The hull of <paramref name="order"/> (indices into <paramref name="p"/>, distinct points), or null when they are coplanar.</summary>
        public static Polytope? Build(long[][] p, IReadOnlyList<int> order)
        {
            var poly = new Polytope(p);
            int i0 = order[0], i1 = order[1];
            int i2 = -1, i3 = -1;
            foreach (int k in order.Skip(2))
            {
                if (!Collinear(p[i0], p[i1], p[k]))
                {
                    i2 = k;
                    break;
                }
            }

            if (i2 < 0)
            {
                return null;
            }

            foreach (int k in order.Skip(2))
            {
                if (k != i2 && Orient(p[i0], p[i1], p[i2], p[k]) != 0)
                {
                    i3 = k;
                    break;
                }
            }

            if (i3 < 0)
            {
                return null;
            }

            // Wind the tetrahedron so the fourth vertex of every face is behind it.
            if (Orient(p[i0], p[i1], p[i2], p[i3]) > 0)
            {
                poly.Add(i0, i2, i1);
                poly.Add(i0, i1, i3);
                poly.Add(i1, i2, i3);
                poly.Add(i2, i0, i3);
            }
            else
            {
                poly.Add(i0, i1, i2);
                poly.Add(i0, i3, i1);
                poly.Add(i1, i3, i2);
                poly.Add(i2, i3, i0);
            }

            foreach (int k in order)
            {
                if (k != i0 && k != i1 && k != i2 && k != i3)
                {
                    poly.Insert(k);
                }
            }

            poly.Volume6 = poly.ComputeVolume6();
            return poly;
        }

        private static bool Collinear(long[] a, long[] b, long[] c)
        {
            Int128 ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            Int128 vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            return (uy * vz) - (uz * vy) == 0 && (uz * vx) - (ux * vz) == 0 && (ux * vy) - (uy * vx) == 0;
        }

        /// <summary>The sign of det(b−a, c−a, d−a): positive when d is in front of triangle (a, b, c).</summary>
        private static int Orient(long[] a, long[] b, long[] c, long[] d)
        {
            Int128 ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            Int128 vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            Int128 wx = d[0] - a[0], wy = d[1] - a[1], wz = d[2] - a[2];
            Int128 det = (wx * ((uy * vz) - (uz * vy))) + (wy * ((uz * vx) - (ux * vz))) + (wz * ((ux * vy) - (uy * vx)));
            return det.CompareTo(Int128.Zero);
        }

        private void Add(int a, int b, int c)
        {
            long[] pa = _p[a], pb = _p[b], pc = _p[c];
            Int128 ux = pb[0] - pa[0], uy = pb[1] - pa[1], uz = pb[2] - pa[2];
            Int128 vx = pc[0] - pa[0], vy = pc[1] - pa[1], vz = pc[2] - pa[2];
            var t = new Tri
            {
                A = a,
                B = b,
                C = c,
                Nx = (uy * vz) - (uz * vy),
                Ny = (uz * vx) - (ux * vz),
                Nz = (ux * vy) - (uy * vx),
            };
            if (t.Nx == 0 && t.Ny == 0 && t.Nz == 0)
            {
                throw new InvalidOperationException("a zero-area triangle was created.");
            }

            t.D = (t.Nx * pa[0]) + (t.Ny * pa[1]) + (t.Nz * pa[2]);
            int index = _tris.Count;
            _tris.Add(t);
            foreach ((int u, int v) in new[] { (a, b), (b, c), (c, a) })
            {
                if (!_edges.TryAdd((u, v), index))
                {
                    throw new InvalidOperationException("an edge is used twice in the same direction.");
                }
            }
        }

        private int Side(Tri t, int k)
        {
            long[] q = _p[k];
            return ((t.Nx * q[0]) + (t.Ny * q[1]) + (t.Nz * q[2]) - t.D).CompareTo(Int128.Zero);
        }

        private void Insert(int k)
        {
            bool outside = false;
            foreach (Tri t in _tris)
            {
                if (t.Alive && Side(t, k) > 0)
                {
                    outside = true;
                    break;
                }
            }

            if (!outside)
            {
                return; // inside or on the hull: never a corner
            }

            var visible = new HashSet<int>();
            for (int i = 0; i < _tris.Count; i++)
            {
                if (_tris[i].Alive && Side(_tris[i], k) >= 0)
                {
                    visible.Add(i);
                }
            }

            var horizon = new List<(int U, int V)>();
            foreach (int i in visible.OrderBy(i => i))
            {
                Tri t = _tris[i];
                foreach ((int u, int v) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                {
                    if (!_edges.TryGetValue((v, u), out int twin))
                    {
                        throw new InvalidOperationException("the hull surface is open.");
                    }

                    if (!visible.Contains(twin))
                    {
                        horizon.Add((u, v));
                    }
                }
            }

            foreach (int i in visible)
            {
                Tri t = _tris[i];
                t.Alive = false;
                _edges.Remove((t.A, t.B));
                _edges.Remove((t.B, t.C));
                _edges.Remove((t.C, t.A));
            }

            foreach ((int u, int v) in horizon)
            {
                Add(u, v, k);
            }
        }

        private Int128 ComputeVolume6()
        {
            Int128 sum = 0;
            long[] r = _p[_tris.First(t => t.Alive).A];
            foreach (Tri t in _tris)
            {
                if (!t.Alive)
                {
                    continue;
                }

                long[] a = _p[t.A], b = _p[t.B], c = _p[t.C];
                Int128 ax = a[0] - r[0], ay = a[1] - r[1], az = a[2] - r[2];
                Int128 bx = b[0] - r[0], by = b[1] - r[1], bz = b[2] - r[2];
                Int128 cx = c[0] - r[0], cy = c[1] - r[1], cz = c[2] - r[2];
                sum += (ax * ((by * cz) - (bz * cy))) + (ay * ((bz * cx) - (bx * cz))) + (az * ((bx * cy) - (by * cx)));
            }

            return sum;
        }

        /// <summary>
        /// The hull's facets as outward-wound corner loops (source indices): adjacent triangles that are
        /// exactly coplanar are joined and each group's boundary is chained into one loop.
        /// </summary>
        public List<int[]> Facets()
        {
            var alive = Enumerable.Range(0, _tris.Count).Where(i => _tris[i].Alive).ToList();
            var parent = new Dictionary<int, int>();
            foreach (int i in alive)
            {
                parent[i] = i;
            }

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }

                return x;
            }

            foreach (int i in alive)
            {
                Tri t = _tris[i];
                foreach ((int u, int v) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                {
                    int j = _edges[(v, u)];
                    Tri o = _tris[j];
                    int opposite = o.A != u && o.A != v ? o.A : (o.B != u && o.B != v ? o.B : o.C);
                    if (Side(t, opposite) == 0)
                    {
                        int ri = Find(i), rj = Find(j);
                        if (ri != rj)
                        {
                            parent[Math.Max(ri, rj)] = Math.Min(ri, rj);
                        }
                    }
                }
            }

            var groups = alive.GroupBy(Find).OrderBy(g => g.Key);
            var facets = new List<int[]>();
            foreach (IGrouping<int, int> group in groups)
            {
                var members = new HashSet<int>(group);
                var next = new Dictionary<int, int>();
                foreach (int i in group)
                {
                    Tri t = _tris[i];
                    foreach ((int u, int v) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                    {
                        if (!members.Contains(_edges[(v, u)]) && !next.TryAdd(u, v))
                        {
                            throw new InvalidOperationException("a facet boundary is not a simple loop.");
                        }
                    }
                }

                int start = next.Keys.Min();
                var loop = new List<int> { start };
                int cur = next[start];
                while (cur != start)
                {
                    if (loop.Count > next.Count)
                    {
                        throw new InvalidOperationException("a facet boundary does not close.");
                    }

                    loop.Add(cur);
                    cur = next[cur];
                }

                if (loop.Count != next.Count)
                {
                    throw new InvalidOperationException("a facet has more than one boundary loop.");
                }

                facets.Add(loop.ToArray());
            }

            return facets;
        }
    }
}
