using System;
using System.Collections.Generic;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Scripting.Kit;

/// <summary>
/// The population kit's space probe (scripts/lib/kit_probe.lua runs on it): a 2.5D column map of the editor
/// document's brushes, no compile. Every 0.5 m floor column holds the open-air intervals left by the
/// time-ordered CSG fold (the world starts solid, later air carves, later solid fills); on top of it the
/// floor lattice with walk and foot graphs, line of sight, horizontal rays and exact point tests. Mover
/// brushes count as open (doors open). An air portal brush carves after every ordinary brush, whatever its
/// time index (RED applies its portal list after its brush list, so a kit.doorway{portal = true} opening is
/// carved by its portal brush alone); only detail solids still stand inside it. A solid portal brush adds
/// nothing. Columns are computed lazily and cached; brushes the kit adds are folded in on top (they are the
/// newest in time order).
/// </summary>
internal sealed class KitProbe
{
    public const double Pitch = 0.5;
    public const double Step = 1.5;       // walk step (Alpine game_patch/misc/waypoints.h:41)
    public const double Drop = 8.0;       // one-way drop (exemplar study walk graph)
    public const double FootStep = 0.6;   // foot-graph step (no jump)
    public const double Headroom = 2.0;   // standing headroom (waypoints.h:49)
    public const double Eye = 1.5;        // eye height above the floor for the kit's sight tests

    private const double Bucket = 4.0;
    private const double Inv = 1 / Pitch;

    private readonly Dictionary<long, List<Rec>> _buckets = new();
    private readonly Dictionary<long, double[]> _cols = new();
    private readonly List<(double X0, double Y0, double Z0, double X1, double Y1, double Z1)> _blocks = new();

    /// <summary>The brushes read, in time order.</summary>
    public List<Rec> Records { get; } = new();

    /// <summary>Columns computed so far (a cost counter for the trace).</summary>
    public int ColumnsComputed { get; private set; }

    /// <summary>A brush as read by the probe: bounds, flags and (on first use) its planes.</summary>
    internal sealed class Rec
    {
        public required Brush B;
        public int Uid, T;
        public bool Air, Detail, Portal;
        public double X0, Y0, Z0, X1, Y1, Z1;
        public double[]? Planes;
        public bool Dead, Box;
        public string? Kit;
    }

    /// <summary>Column key (the Lua probe's ckey).</summary>
    public static long Key(long i, long k) => ((i + 1048576L) * 2097152L) + (k + 1048576L);

    private static long BKey(double x, double z) => Key((long)Math.Floor(x / Bucket), (long)Math.Floor(z / Bucket));

    /// <summary>A record for <paramref name="b"/>, or null for a brush the probe ignores (solid portal, mover member).</summary>
    public static Rec? RecordOf(Brush b, int timeIndex, bool moverMember, string? kitTag)
    {
        bool air = (b.Flags & (uint)BrushFlags.Air) != 0;
        bool portal = (b.Flags & (uint)BrushFlags.Portal) != 0;
        if ((portal && !air) || moverMember)
        {
            return null;
        }

        ScriptAabb bb = ScriptBrushHandle.WorldBounds(b);
        return new Rec
        {
            B = b, Uid = b.Uid, T = timeIndex, Air = air, Detail = (b.Flags & (uint)BrushFlags.Detail) != 0, Portal = portal,
            X0 = bb.Min.X, Y0 = bb.Min.Y, Z0 = bb.Min.Z, X1 = bb.Max.X, Y1 = bb.Max.Y, Z1 = bb.Max.Z, Kit = kitTag,
        };
    }

    /// <summary>Adds a record (the newest in time order).</summary>
    public void Insert(Rec r)
    {
        Records.Add(r);
        long bi0 = (long)Math.Floor(r.X0 / Bucket), bi1 = (long)Math.Floor(r.X1 / Bucket);
        long bk0 = (long)Math.Floor(r.Z0 / Bucket), bk1 = (long)Math.Floor(r.Z1 / Bucket);
        for (long bi = bi0; bi <= bi1; bi++)
        {
            for (long bk = bk0; bk <= bk1; bk++)
            {
                long key = Key(bi, bk);
                if (!_buckets.TryGetValue(key, out List<Rec>? l))
                {
                    _buckets[key] = l = new List<Rec>();
                }

                l.Add(r);
            }
        }
    }

    /// <summary>Adds a brush made after the probe was built, and forgets the columns it covers.</summary>
    public void AddRecord(Rec r)
    {
        Insert(r);
        Invalidate(r.X0, r.Z0, r.X1, r.Z1);
    }

    /// <summary>Adds a virtual solid box (an object's collision footprint, e.g. clutter) on top of every brush.</summary>
    public void AddBlock(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        _blocks.Add((x0, y0, z0, x1, y1, z1));
        Invalidate(x0, z0, x1, z1);
    }

    private void Invalidate(double x0, double z0, double x1, double z1)
    {
        for (long i = (long)Math.Floor(x0 / Pitch) - 1; i <= (long)Math.Floor(x1 / Pitch) + 1; i++)
        {
            for (long k = (long)Math.Floor(z0 / Pitch) - 1; k <= (long)Math.Floor(z1 / Pitch) + 1; k++)
            {
                _cols.Remove(Key(i, k));
            }
        }
    }

    /// <summary>Reads a record's planes the first time (outward, n · p &lt;= d inside); false for a degenerate brush.</summary>
    public static bool Ensure(Rec r)
    {
        if (r.Planes is not null)
        {
            return !r.Dead;
        }

        Brush b = r.B;
        int n = b.Geometry.Faces.Count;
        var planes = new double[n * 4];
        double cx = 0, cy = 0, cz = 0;
        float sign = BrushOrientation.OutwardSign(b);
        for (int f = 0; f < n; f++)
        {
            Face face = b.Geometry.Faces[f];
            Vec3 nn = ScriptBrushHandle.WorldNormal(b, face, sign);
            List<Vec3> corners = GeometryUtil.Corners(b.Geometry, face);
            var world = new Vec3[corners.Count];
            for (int q = 0; q < corners.Count; q++)
            {
                world[q] = BrushTransform.WorldVertex(b, corners[q]);
            }

            Vec3 c = GeometryUtil.Centroid(world);
            planes[(f * 4) + 0] = nn.X;
            planes[(f * 4) + 1] = nn.Y;
            planes[(f * 4) + 2] = nn.Z;
            planes[(f * 4) + 3] = ((double)nn.X * c.X) + ((double)nn.Y * c.Y) + ((double)nn.Z * c.Z);
            cx += c.X;
            cy += c.Y;
            cz += c.Z;
        }

        r.Planes = planes;
        if (n < 4)
        {
            r.Dead = true;
            return false;
        }

        // The mean of the face centres lies inside a convex brush: flip any plane that has it outside, so the
        // test never depends on which way an air brush's faces were wound.
        cx /= n;
        cy /= n;
        cz /= n;
        bool box = true;
        for (int f = 0; f < n; f++)
        {
            int o = f * 4;
            if ((planes[o] * cx) + (planes[o + 1] * cy) + (planes[o + 2] * cz) > planes[o + 3] + 1e-9)
            {
                planes[o] = -planes[o];
                planes[o + 1] = -planes[o + 1];
                planes[o + 2] = -planes[o + 2];
                planes[o + 3] = -planes[o + 3];
            }

            double ax = Math.Abs(planes[o]), ay = Math.Abs(planes[o + 1]), az = Math.Abs(planes[o + 2]);
            if (!((ax > 0.99999 && ay < 1e-5 && az < 1e-5) || (ay > 0.99999 && ax < 1e-5 && az < 1e-5) || (az > 0.99999 && ax < 1e-5 && ay < 1e-5)))
            {
                box = false;
            }
        }

        r.Box = box;
        return true;
    }

    // ---- intervals ------------------------------------------------------------------------------

    private static double[] Union(double[] list, double lo, double hi)
    {
        var o = new List<double>(list.Length + 2);
        bool placed = false;
        for (int q = 0; q < list.Length; q += 2)
        {
            double a = list[q], b = list[q + 1];
            if (b < lo - 1e-6)
            {
                o.Add(a);
                o.Add(b);
            }
            else if (a > hi + 1e-6)
            {
                if (!placed)
                {
                    o.Add(lo);
                    o.Add(hi);
                    placed = true;
                }

                o.Add(a);
                o.Add(b);
            }
            else
            {
                lo = Math.Min(lo, a);
                hi = Math.Max(hi, b);
            }
        }

        if (!placed)
        {
            o.Add(lo);
            o.Add(hi);
        }

        return o.ToArray();
    }

    private static double[] Subtract(double[] list, double lo, double hi)
    {
        var o = new List<double>(list.Length + 2);
        for (int q = 0; q < list.Length; q += 2)
        {
            double a = list[q], b = list[q + 1];
            if (b <= lo + 1e-6 || a >= hi - 1e-6)
            {
                o.Add(a);
                o.Add(b);
            }
            else
            {
                if (a < lo - 1e-6)
                {
                    o.Add(a);
                    o.Add(lo);
                }

                if (b > hi + 1e-6)
                {
                    o.Add(hi);
                    o.Add(b);
                }
            }
        }

        return o.ToArray();
    }

    /// <summary>The vertical interval of a convex record over the column (x, z), or false.</summary>
    private static bool ColumnSpan(Rec r, double x, double z, out double lo, out double hi)
    {
        lo = r.Y0 - 1e-4;
        hi = r.Y1 + 1e-4;
        double[] p = r.Planes!;
        for (int q = 0; q < p.Length; q += 4)
        {
            double rhs = p[q + 3] - (p[q] * x) - (p[q + 2] * z);
            double ny = p[q + 1];
            if (ny > 1e-9)
            {
                double v = rhs / ny;
                if (v < hi)
                {
                    hi = v;
                }
            }
            else if (ny < -1e-9)
            {
                double v = rhs / ny;
                if (v > lo)
                {
                    lo = v;
                }
            }
            else if (rhs < -1e-5)
            {
                return false;
            }

            if (hi - lo < 1e-5)
            {
                return false;
            }
        }

        return true;
    }

    private static readonly double[] Empty = Array.Empty<double>();

    /// <summary>The vertical span of a record over the column (x, z), or false (outside it, or degenerate).</summary>
    private static bool SpanAt(Rec r, double x, double z, out double lo, out double hi)
    {
        lo = hi = 0;
        if (x < r.X0 - 1e-6 || x > r.X1 + 1e-6 || z < r.Z0 - 1e-6 || z > r.Z1 + 1e-6 || !Ensure(r))
        {
            return false;
        }

        if (r.Box)
        {
            lo = r.Y0;
            hi = r.Y1;
            return true;
        }

        return ColumnSpan(r, x, z, out lo, out hi);
    }

    /// <summary>The air intervals of column (i, k), lowest first, as [lo0, hi0, lo1, hi1, …].</summary>
    public double[] Col(long i, long k)
    {
        long key = Key(i, k);
        if (_cols.TryGetValue(key, out double[]? c))
        {
            return c;
        }

        ColumnsComputed++;
        double x = (i + 0.5) * Pitch, z = (k + 0.5) * Pitch;
        double[] air = Empty;
        if (_buckets.TryGetValue(BKey(x, z), out List<Rec>? l))
        {
            // Ordinary brushes in time order; then the air portal brushes carve over the result (after every
            // other brush), and the detail solids inside a portal's span stand again.
            List<(double Lo, double Hi)>? portals = null;
            foreach (Rec r in l)
            {
                if (SpanAt(r, x, z, out double lo, out double hi))
                {
                    if (r.Portal)
                    {
                        (portals ??= new List<(double Lo, double Hi)>()).Add((lo, hi));
                    }
                    else if (r.Air)
                    {
                        air = air.Length == 0 ? new[] { lo, hi } : Union(air, lo, hi);
                    }
                    else if (air.Length > 0)
                    {
                        air = Subtract(air, lo, hi);
                    }
                }
            }

            if (portals is not null)
            {
                foreach ((double plo, double phi) in portals)
                {
                    air = air.Length == 0 ? new[] { plo, phi } : Union(air, plo, phi);
                }

                foreach (Rec r in l)
                {
                    if (r.Detail && !r.Air && SpanAt(r, x, z, out double lo, out double hi))
                    {
                        foreach ((double plo, double phi) in portals)
                        {
                            double a = Math.Max(lo, plo), b = Math.Min(hi, phi);
                            if (b > a && air.Length > 0)
                            {
                                air = Subtract(air, a, b);
                            }
                        }
                    }
                }
            }
        }

        foreach ((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b in _blocks)
        {
            if (x > b.X0 && x < b.X1 && z > b.Z0 && z < b.Z1)
            {
                air = Subtract(air, b.Y0, b.Y1);
            }
        }

        _cols[key] = air;
        return air;
    }

    public static long Index(double v) => (long)Math.Floor(v * Inv);

    /// <summary>True when the world point is open air (column-quantized).</summary>
    public bool Air(double x, double y, double z)
    {
        double[] c = Col(Index(x), Index(z));
        for (int q = 0; q < c.Length; q += 2)
        {
            if (y > c[q] && y < c[q + 1])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the segment a → b is open air (sampled every 0.5 m, the column pitch).</summary>
    public bool Los(double ax, double ay, double az, double bx, double by, double bz)
    {
        double dx = bx - ax, dy = by - ay, dz = bz - az;
        int n = (int)Math.Ceiling(Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) * Inv);
        if (n < 2)
        {
            return true;
        }

        double sx = dx / n, sy = dy / n, sz = dz / n;
        double x = ax, y = ay, z = az;
        for (int t = 1; t <= n - 1; t++)
        {
            x += sx;
            y += sy;
            z += sz;
            if (!Air(x, y, z))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How far a horizontal ray from (x, y, z) along (dx, dz) runs in open air, up to maxd (0.5 m steps).</summary>
    public double Ray(double x, double y, double z, double dx, double dz, double maxd)
    {
        double d = Pitch;
        while (d <= maxd)
        {
            if (!Air(x + (dx * d), y, z + (dz * d)))
            {
                return d - Pitch;
            }

            d += Pitch;
        }

        return maxd;
    }

    private static readonly (double X, double Z)[] Dir16 = BuildDir16();

    private static (double, double)[] BuildDir16()
    {
        var d = new (double, double)[16];
        for (int k = 0; k < 16; k++)
        {
            d[k] = (Math.Cos(k * Math.PI / 8), Math.Sin(k * Math.PI / 8));
        }

        return d;
    }

    /// <summary>Share of 8 horizontal chest-height rays (every 45°) from (x, y, z) that hit something within d metres.</summary>
    public double Protection(double x, double y, double z, double d)
    {
        int hit = 0;
        for (int k = 0; k < 16; k += 2)
        {
            if (Ray(x, y + 1.2, z, Dir16[k].X, Dir16[k].Z, d) < d)
            {
                hit++;
            }
        }

        return hit / 8.0;
    }

    /// <summary>The heading of the longest of 16 chest-height rays (ties: lower index) and its length.</summary>
    public (double Dx, double Dz, double D) LongestRay(double x, double y, double z, double maxd)
    {
        double bd = -1, bx = 0, bz = 1;
        for (int k = 0; k < 16; k++)
        {
            double d = Ray(x, y + 1.2, z, Dir16[k].X, Dir16[k].Z, maxd);
            if (d > bd + 1e-6)
            {
                bd = d;
                bx = Dir16[k].X;
                bz = Dir16[k].Z;
            }
        }

        return (bx, bz, bd);
    }

    /// <summary>The open-air state of a world point from the brush planes (exact, not quantized to columns).</summary>
    public bool AirExact(double x, double y, double z)
    {
        bool air = false;
        if (_buckets.TryGetValue(BKey(x, z), out List<Rec>? l))
        {
            // Ordinary brushes in time order; inside an air portal brush the point is then open unless a detail
            // solid holds it (the same fold as Col).
            bool inPortal = false, inDetailSolid = false;
            foreach (Rec r in l)
            {
                if (Inside(r, x, y, z))
                {
                    if (r.Portal)
                    {
                        inPortal = true;
                    }
                    else
                    {
                        air = r.Air;
                        inDetailSolid |= r.Detail && !r.Air;
                    }
                }
            }

            if (inPortal)
            {
                air = !inDetailSolid;
            }
        }

        if (air)
        {
            foreach ((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b in _blocks)
            {
                if (x > b.X0 && x < b.X1 && z > b.Z0 && z < b.Z1 && y > b.Y0 && y < b.Y1)
                {
                    return false;
                }
            }
        }

        return air;
    }

    /// <summary>True when the world point lies inside a record's convex volume.</summary>
    private static bool Inside(Rec r, double x, double y, double z)
    {
        if (x < r.X0 - 1e-6 || x > r.X1 + 1e-6 || z < r.Z0 - 1e-6 || z > r.Z1 + 1e-6 || y < r.Y0 - 1e-6 || y > r.Y1 + 1e-6 || !Ensure(r))
        {
            return false;
        }

        double[] p = r.Planes!;
        for (int e = 0; e < p.Length; e += 4)
        {
            if ((p[e] * x) + (p[e + 1] * y) + (p[e + 2] * z) > p[e + 3] + 1e-5)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Exact distance along the horizontal unit (dx, dz) to the first solid, up to maxd (0.05 m resolution).</summary>
    public double WallDistance(double x, double y, double z, double dx, double dz, double maxd)
    {
        double d = 0;
        while (d <= maxd)
        {
            if (!AirExact(x + (dx * d), y, z + (dz * d)))
            {
                return Math.Max(0, d - 0.025);
            }

            d += 0.05;
        }

        return maxd;
    }

    /// <summary>True when some brush's bounds cover the 4 m bucket holding (x, z).</summary>
    public bool Covered(double x, double z) => _buckets.ContainsKey(BKey(x, z));

    /// <summary>The floor lattice over a box (see <see cref="KitLattice"/>).</summary>
    public KitLattice Lattice(double x0, double z0, double x1, double z1, double ylo, double yhi, double headroom, int stride) =>
        new(this, x0, z0, x1, z1, ylo, yhi, headroom, stride);
}

/// <summary>
/// A floor lattice of a <see cref="KitProbe"/>: standing nodes (0.5 m columns, floors in [ylo, yhi] with headroom),
/// ids 1-based like the Lua probe's, with the 4- and 8-neighbour links and the walk / foot graphs.
/// </summary>
internal sealed class KitLattice
{
    private static readonly int[][] N4 = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
    private static readonly int[][] N8 = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 }, new[] { 1, 1 }, new[] { 1, -1 }, new[] { -1, 1 }, new[] { -1, -1 } };

    public readonly List<long> I = new() { 0 }, K = new() { 0 };
    public readonly List<double> X = new() { 0 }, Z = new() { 0 }, Y = new() { 0 }, Top = new() { 0 };
    public readonly Dictionary<long, List<int>> At = new();
    public int[][] Nb4 = Array.Empty<int[]>();
    public int[][] Nb8 = Array.Empty<int[]>();

    public KitLattice(KitProbe probe, double x0, double z0, double x1, double z1, double ylo, double yhi, double headroom, int stride)
    {
        Probe = probe;
        X0 = x0;
        Z0 = z0;
        X1 = x1;
        Z1 = z1;
        double p = KitProbe.Pitch;
        long i0 = (long)Math.Ceiling((x0 / p) - 0.5), i1 = (long)Math.Floor((x1 / p) - 0.5);
        long k0 = (long)Math.Ceiling((z0 / p) - 0.5), k1 = (long)Math.Floor((z1 / p) - 0.5);
        stride = Math.Max(1, stride);
        for (long i = i0; i <= i1; i += stride)
        {
            double x = (i + 0.5) * p;
            for (long k = k0; k <= k1; k += stride)
            {
                double z = (k + 0.5) * p;
                if (!probe.Covered(x, z))
                {
                    continue;
                }

                double[] c = probe.Col(i, k);
                for (int q = 0; q < c.Length; q += 2)
                {
                    double y = c[q];
                    if (y >= ylo - 0.05 && y <= yhi && c[q + 1] - y >= headroom)
                    {
                        int id = X.Count;
                        I.Add(i);
                        K.Add(k);
                        X.Add(x);
                        Z.Add(z);
                        Y.Add(y);
                        Top.Add(c[q + 1]);
                        long key = KitProbe.Key(i, k);
                        if (!At.TryGetValue(key, out List<int>? a))
                        {
                            At[key] = a = new List<int>(2);
                        }

                        a.Add(id);
                    }
                }
            }
        }

        if (stride == 1)
        {
            int n = Count;
            Nb4 = new int[n + 1][];
            Nb8 = new int[n + 1][];
            Nb4[0] = Array.Empty<int>();
            Nb8[0] = Array.Empty<int>();
            var buf = new List<int>(8);
            for (int id = 1; id <= n; id++)
            {
                buf.Clear();
                foreach (int[] d in N4)
                {
                    if (At.TryGetValue(KitProbe.Key(I[id] + d[0], K[id] + d[1]), out List<int>? a))
                    {
                        buf.AddRange(a);
                    }
                }

                Nb4[id] = buf.ToArray();
                var n8 = new int[8];
                for (int q = 0; q < 8; q++)
                {
                    int best = 0;
                    double bd = 0.6;
                    if (At.TryGetValue(KitProbe.Key(I[id] + N8[q][0], K[id] + N8[q][1]), out List<int>? a))
                    {
                        foreach (int j in a)
                        {
                            double dy = Math.Abs(Y[j] - Y[id]);
                            if (dy <= bd)
                            {
                                best = j;
                                bd = dy;
                            }
                        }
                    }

                    n8[q] = best;
                }

                Nb8[id] = n8;
            }
        }
    }

    public KitProbe Probe { get; }

    public double X0 { get; }

    public double Z0 { get; }

    public double X1 { get; }

    public double Z1 { get; }

    /// <summary>Number of nodes (ids 1 … Count).</summary>
    public int Count => X.Count - 1;

    /// <summary>The node of column (i, k) nearest height y within tol, or 0.</summary>
    public int NodeAt(long i, long k, double y, double tol)
    {
        if (!At.TryGetValue(KitProbe.Key(i, k), out List<int>? a))
        {
            return 0;
        }

        int best = 0;
        double bd = tol;
        foreach (int id in a)
        {
            double d = Math.Abs(Y[id] - y);
            if (d <= bd)
            {
                best = id;
                bd = d;
            }
        }

        return best;
    }

    /// <summary>The node nearest a world point within r metres (Lua P.nearest), or 0.</summary>
    public int Nearest(double x, double y, double z, double r, double ytol)
    {
        long ci = (long)Math.Floor(x / KitProbe.Pitch), ck = (long)Math.Floor(z / KitProbe.Pitch);
        long s = (long)Math.Ceiling(r / KitProbe.Pitch);
        int best = 0;
        double bd = double.MaxValue;
        for (long i = ci - s; i <= ci + s; i++)
        {
            for (long k = ck - s; k <= ck + s; k++)
            {
                if (!At.TryGetValue(KitProbe.Key(i, k), out List<int>? a))
                {
                    continue;
                }

                foreach (int id in a)
                {
                    double dy = Y[id] - y;
                    if (dy <= ytol && dy >= -ytol - 1.5)
                    {
                        double d = ((X[id] - x) * (X[id] - x)) + ((Z[id] - z) * (Z[id] - z)) + (0.25 * dy * dy);
                        if (d < bd)
                        {
                            best = id;
                            bd = d;
                        }
                    }
                }
            }
        }

        return best;
    }

    /// <summary>Walk neighbours (steps up to 1.5 m with 1 m of air above, drops up to 8 m one way).</summary>
    public int WalkEdges(int id, int[] o)
    {
        int c = 0;
        double ny = Y[id], nt = Top[id];
        foreach (int j in Nb4[id])
        {
            double dy = Y[j] - ny;
            if (dy <= KitProbe.Step && dy >= -KitProbe.Step)
            {
                double hi = Math.Max(ny, Y[j]), lo = Math.Min(nt, Top[j]);
                if (lo >= hi + 1.0)
                {
                    o[c++] = j;
                }
            }
            else if (dy < -KitProbe.Step && dy >= -KitProbe.Drop)
            {
                if (Top[j] >= ny + 1.0)
                {
                    o[c++] = j;
                }
            }
        }

        return c;
    }

    /// <summary>Foot neighbours (steps up to 0.6 m with standing headroom over both).</summary>
    public int FootEdges(int id, int[] o)
    {
        int c = 0;
        double ny = Y[id], nt = Top[id];
        foreach (int j in Nb4[id])
        {
            double dy = Y[j] - ny;
            if (dy <= KitProbe.FootStep && dy >= -KitProbe.FootStep)
            {
                double hi = Math.Max(ny, Y[j]), lo = Math.Min(nt, Top[j]);
                if (lo >= hi + 2.0)
                {
                    o[c++] = j;
                }
            }
        }

        return c;
    }

    /// <summary>Nodes reachable from the seeds along walk edges (drops one way).</summary>
    public bool[] Reach(IEnumerable<int> seeds, out int count)
    {
        var seen = new bool[Count + 1];
        var queue = new int[Count + 1];
        int head = 0, tail = 0;
        foreach (int s in seeds)
        {
            if (!seen[s])
            {
                seen[s] = true;
                queue[tail++] = s;
            }
        }

        var buf = new int[8];
        while (head < tail)
        {
            int id = queue[head++];
            int c = WalkEdges(id, buf);
            for (int e = 0; e < c; e++)
            {
                int j = buf[e];
                if (!seen[j])
                {
                    seen[j] = true;
                    queue[tail++] = j;
                }
            }
        }

        count = tail;
        return seen;
    }

    /// <summary>Nodes whose 8 neighbouring columns all hold a node within the foot step and 2 m of headroom.</summary>
    public bool[] Wide()
    {
        var w = new bool[Count + 1];
        for (int id = 1; id <= Count; id++)
        {
            bool ok = true;
            foreach (int j in Nb8[id])
            {
                if (j == 0 || Top[j] < Y[id] + 2.0)
                {
                    ok = false;
                    break;
                }
            }

            w[id] = ok;
        }

        return w;
    }

    /// <summary>Nodes within r metres (same floor ± 0.3 m) of a floor edge.</summary>
    public bool[] NearEdge(double r)
    {
        var o = new bool[Count + 1];
        long s = Math.Max(0, (long)Math.Floor((r / KitProbe.Pitch) + 0.5));
        double r2 = (r + 1e-6) * (r + 1e-6);
        for (int id = 1; id <= Count; id++)
        {
            bool edge = false;
            foreach (int j in Nb8[id])
            {
                if (j == 0 || Math.Abs(Y[j] - Y[id]) > 0.3)
                {
                    edge = true;
                    break;
                }
            }

            if (!edge)
            {
                continue;
            }

            o[id] = true;
            for (long i = I[id] - s; i <= I[id] + s; i++)
            {
                for (long k = K[id] - s; k <= K[id] + s; k++)
                {
                    double di = (i - I[id]) * KitProbe.Pitch, dk = (k - K[id]) * KitProbe.Pitch;
                    if ((di * di) + (dk * dk) <= r2)
                    {
                        int j = NodeAt(i, k, Y[id], 0.3);
                        if (j != 0)
                        {
                            o[j] = true;
                        }
                    }
                }
            }
        }

        return o;
    }

    /// <summary>Foot-graph components over the nodes in <paramref name="inside"/> (0 = not inside).</summary>
    public int[] Components(bool[] inside)
    {
        var comp = new int[Count + 1];
        var buf = new int[8];
        var queue = new int[Count + 1];
        int next = 0;
        for (int id = 1; id <= Count; id++)
        {
            if (!inside[id] || comp[id] != 0)
            {
                continue;
            }

            next++;
            comp[id] = next;
            int head = 0, tail = 0;
            queue[tail++] = id;
            while (head < tail)
            {
                int u = queue[head++];
                int c = FootEdges(u, buf);
                for (int e = 0; e < c; e++)
                {
                    int j = buf[e];
                    if (inside[j] && comp[j] == 0)
                    {
                        comp[j] = next;
                        queue[tail++] = j;
                    }
                }
            }
        }

        return comp;
    }

    /// <summary>
    /// The walkable area (m²): nodes reachable along walk edges from every foot component holding at least
    /// 8 m² of interior floor (all 4 foot neighbours present). Returns the area and the reach set.
    /// </summary>
    public double WalkArea(out bool[] reach)
    {
        var all = new bool[Count + 1];
        Array.Fill(all, true);
        all[0] = false;
        int[] comp = Components(all);
        var size = new Dictionary<int, int>();
        var buf = new int[8];
        for (int id = 1; id <= Count; id++)
        {
            if (FootEdges(id, buf) == 4)
            {
                size[comp[id]] = size.GetValueOrDefault(comp[id]) + 1;
            }
        }

        var seeds = new List<int>();
        for (int id = 1; id <= Count; id++)
        {
            if (size.GetValueOrDefault(comp[id]) >= 32)
            {
                seeds.Add(id);
            }
        }

        if (seeds.Count == 0)
        {
            reach = new bool[Count + 1];
            return 0;
        }

        reach = Reach(seeds, out int n);
        return n * KitProbe.Pitch * KitProbe.Pitch;
    }

    /// <summary>
    /// Door nodes: nodes on the box's edge whose outward neighbour column (outside the box) holds open floor
    /// within a walk step — the openings the space is entered by.
    /// </summary>
    public List<int> DoorNodes(double sx0, double sz0, double sx1, double sz1)
    {
        var o = new List<int>();
        for (int id = 1; id <= Count; id++)
        {
            bool found = false;
            foreach (int[] d in N4)
            {
                double ox = X[id] + (d[0] * KitProbe.Pitch), oz = Z[id] + (d[1] * KitProbe.Pitch);
                if (found || !(ox < sx0 || ox > sx1 || oz < sz0 || oz > sz1))
                {
                    continue;
                }

                double[] c = Probe.Col(I[id] + d[0], K[id] + d[1]);
                for (int e = 0; e < c.Length; e += 2)
                {
                    if (c[e] <= Y[id] + KitProbe.Step && c[e] >= Y[id] - KitProbe.Step && c[e + 1] - Math.Max(c[e], Y[id]) >= KitProbe.Headroom)
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (found)
            {
                o.Add(id);
            }
        }

        return o;
    }

    /// <summary>Flat nodes: no neighbouring floor within a foot step at another height (more than <paramref name="tol"/>).</summary>
    public bool[] Flat(double tol)
    {
        var f = new bool[Count + 1];
        for (int id = 1; id <= Count; id++)
        {
            bool ok = true;
            foreach (int j in Nb8[id])
            {
                if (j != 0 && Math.Abs(Y[j] - Y[id]) > tol)
                {
                    ok = false;
                    break;
                }
            }

            f[id] = ok;
        }

        return f;
    }

    /// <summary>Nodes beside a step, ramp or ledge edge: a 4-neighbour at another height whose column holds no floor at this height.</summary>
    public List<int> StepNodes(double tol)
    {
        var o = new List<int>();
        for (int id = 1; id <= Count; id++)
        {
            bool other = false;
            foreach (int j in Nb4[id])
            {
                if (Math.Abs(Y[j] - Y[id]) > tol)
                {
                    other = true;
                }
            }

            if (!other)
            {
                continue;
            }

            foreach (int[] d in N4)
            {
                if (At.TryGetValue(KitProbe.Key(I[id] + d[0], K[id] + d[1]), out List<int>? a))
                {
                    bool same = false;
                    foreach (int e in a)
                    {
                        if (Math.Abs(Y[e] - Y[id]) <= tol)
                        {
                            same = true;
                        }
                    }

                    if (!same)
                    {
                        o.Add(id);
                        break;
                    }
                }
            }
        }

        return o;
    }

    // ---- the dressing route check (kit_dress Space:_routes_ok) ----------------------------------

    private bool[]? _wide;
    private int[][]? _foot;

    /// <summary>The live wide set of a dressing space (starts as <see cref="Wide"/>; commits remove nodes).</summary>
    public bool[] LiveWide => _wide ??= Wide();

    /// <summary>Removes nodes from the live wide set.</summary>
    public void Unwide(IEnumerable<int> ids)
    {
        bool[] w = LiveWide;
        foreach (int id in ids)
        {
            if (id > 0 && id <= Count)
            {
                w[id] = false;
            }
        }
    }

    /// <summary>
    /// True when, for every group of anchor nodes, all of the group is reached from its first anchor along foot
    /// edges through live wide nodes not in <paramref name="gone"/>.
    /// </summary>
    public bool RoutesOk(IReadOnlyList<IReadOnlyList<int>> groups, IReadOnlyCollection<int> gone)
    {
        bool[] wide = LiveWide;
        if (_foot is null)
        {
            _foot = new int[Count + 1][];
            var buf = new int[8];
            _foot[0] = Array.Empty<int>();
            for (int id = 1; id <= Count; id++)
            {
                int c = FootEdges(id, buf);
                _foot[id] = buf.AsSpan(0, c).ToArray();
            }
        }

        var goneSet = new HashSet<int>(gone);
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (IReadOnlyList<int> g in groups)
        {
            if (g.Count <= 1)
            {
                continue;
            }

            var want = new HashSet<int>();
            for (int q = 1; q < g.Count; q++)
            {
                if (g[q] != g[0])
                {
                    want.Add(g[q]);
                }
            }

            int missing = want.Count;
            seen.Clear();
            queue.Clear();
            seen.Add(g[0]);
            queue.Enqueue(g[0]);
            while (queue.Count > 0 && missing > 0)
            {
                int u = queue.Dequeue();
                foreach (int j in _foot[u])
                {
                    if (!seen.Contains(j) && wide[j] && !goneSet.Contains(j))
                    {
                        seen.Add(j);
                        queue.Enqueue(j);
                        if (want.Contains(j))
                        {
                            missing--;
                        }
                    }
                }
            }

            if (missing > 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The kit's seeded stream (Park-Miller minimal standard, the Lua probe's P.stream): the kit never reads the run's rng.</summary>
internal sealed class KitStream
{
    private double _s;

    public KitStream(double seed)
    {
        double s = Math.Floor((seed * 7919) + 104729) % 2147483646;
        if (s <= 0)
        {
            s += 2147483646;
        }

        _s = s;
    }

    public double Next()
    {
        _s = (_s * 48271) % 2147483647;
        return _s / 2147483647;
    }

    public double Range(double a, double b) => a + ((b - a) * Next());

    public int Int(int a, int b)
    {
        int v = a + (int)Math.Floor(Next() * (b - a + 1));
        return v > b ? b : v;
    }
}
