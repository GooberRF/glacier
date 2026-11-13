using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Analysis;

/// <summary>
/// The floor-lattice + ray sampler behind the scale metrics (CORPUS-NORMS-SPEC §3.2) and the spatial queries
/// (<see cref="Spatial.SpatialAnalysis"/>). It works on the compiled geometry stored in the level (world faces; portal
/// faces, sky-room faces and liquid surfaces left out), with a bounding-volume hierarchy (<see cref="Spatial.FaceBvh"/>)
/// accelerating the rays; ray queries are thread-safe.
/// </summary>
internal sealed class SpaceSampler
{
    /// <summary>A floor face: normal.Y at least this (45 degrees, the kit's max_slope).</summary>
    public const double FloorNormalY = 0.70;

    /// <summary>Headroom cap: no ceiling within this distance = open sky.</summary>
    public const double SkyCap = 200;

    /// <summary>Horizontal ray cap.</summary>
    public const double RayCap = 100;

    /// <summary>A floor sample is valid with at least this much headroom.</summary>
    public const double MinHeadroom = 1.0;

    /// <summary>Chest height: horizontal rays start this far above the floor sample.</summary>
    public const double ChestHeight = 1.0;

    /// <summary>The 16 horizontal ray directions, k · 22.5 degrees from +X towards +Z.</summary>
    public static readonly Vec3d[] Directions = Enumerable.Range(0, 16)
        .Select(k => new Vec3d(Math.Cos(k * Math.PI / 8), 0, Math.Sin(k * Math.PI / 8)))
        .ToArray();

    private readonly List<SFace> _faces = new();
    private readonly CancellationToken _ct;
    private readonly Action? _probe;
    private Spatial.FaceBvh _bvh = null!;
    private long _iterations;

    private SpaceSampler(CancellationToken ct, Action? probe)
    {
        _ct = ct;
        _probe = probe;
    }

    /// <summary>One face prepared for ray tests.</summary>
    internal sealed class SFace
    {
        public required Vec3d[] P { get; init; }

        public Vec3d N { get; init; }

        public double D { get; init; }

        public double MinX { get; init; }

        public double MinY { get; init; }

        public double MinZ { get; init; }

        public double MaxX { get; init; }

        public double MaxY { get; init; }

        public double MaxZ { get; init; }

        /// <summary>The two world axes the face projects onto for the inside test (drops the dominant normal axis).</summary>
        public int A0 { get; init; }

        public int A1 { get; init; }

        /// <summary>A floor face that yields lattice samples (walkable slope, not an invisible collision helper).</summary>
        public bool Floor { get; init; }

        /// <summary>The face index in the compiled geometry.</summary>
        public int Source { get; init; }
    }

    /// <summary>One floor lattice sample and what the rays measured from it.</summary>
    internal sealed class Sample
    {
        public required Vec3d P { get; init; }

        public int I { get; init; }

        public int K { get; init; }

        /// <summary>Distance up to the first downward-facing face; +∞ = open sky.</summary>
        public double Headroom { get; set; }

        public bool Valid { get; set; }

        public double[] Dist { get; } = new double[16];

        public double Width { get; set; }

        /// <summary>The travel-axis direction index (0..15), perpendicular to the narrowest pair.</summary>
        public int Axis { get; set; }

        public double Length { get; set; }

        public bool Corridor { get; set; }

        public bool OpenSky => double.IsPositiveInfinity(Headroom);
    }

    /// <summary>The measured lattice.</summary>
    public IReadOnlyList<Sample> Samples => _samples;

    private readonly List<Sample> _samples = new();
    private readonly Dictionary<long, List<int>> _lattice = new();

    /// <summary>The lattice pitch in use (metres).</summary>
    public double Pitch { get; private set; }

    /// <summary>The number of faces kept for ray tests.</summary>
    public int FaceCount => _faces.Count;

    /// <summary>The kept faces (for the stair detector and diagnostics).</summary>
    internal IReadOnlyList<SFace> Faces => _faces;

    /// <summary>
    /// Prepares the geometry: keeps world faces (detail included — they are walkable/collidable) minus portal faces,
    /// faces in sky rooms and liquid surfaces. Faces whose texture name contains "invisible" block rays but never
    /// yield floor samples.
    /// </summary>
    public static SpaceSampler Build(Geometry geo, CancellationToken ct, Action? probe = null)
    {
        ArgumentNullException.ThrowIfNull(geo);
        var s = new SpaceSampler(ct, probe);
        for (int i = 0; i < geo.Faces.Count; i++)
        {
            if ((i & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            Face f = geo.Faces[i];
            if (f.IsPortalFace || (f.Flags & (ushort)FaceFlags.LiquidSurface) != 0 || f.Vertices.Count < 3)
            {
                continue;
            }

            if (f.RoomIndex >= 0 && f.RoomIndex < geo.Rooms.Count && geo.Rooms[f.RoomIndex].IsSkyroom != 0)
            {
                continue;
            }

            if (Prepare(geo, f, i) is { } sf)
            {
                s._faces.Add(sf);
            }
        }

        s._bvh = new Spatial.FaceBvh(s._faces);
        return s;
    }

    /// <summary>True when a texture name marks an invisible collision helper.</summary>
    public static bool IsInvisibleTexture(Geometry geo, Face f) =>
        f.Texture >= 0 && f.Texture < geo.Textures.Count
        && geo.Textures[f.Texture].Contains("invisible", StringComparison.OrdinalIgnoreCase);

    /// <summary>A face's world polygon, unit normal and Newell area; null when degenerate.</summary>
    internal static (Vec3d[] Poly, Vec3d Normal, double Area)? Polygon(Geometry geo, Face f)
    {
        var poly = new Vec3d[f.Vertices.Count];
        for (int k = 0; k < poly.Length; k++)
        {
            int vi = f.Vertices[k].Index;
            if (vi < 0 || vi >= geo.Vertices.Count)
            {
                return null;
            }

            Vec3 v = geo.Vertices[vi];
            poly[k] = new Vec3d(v.X, v.Y, v.Z);
        }

        Vec3d newell = Newell(poly);
        double area = newell.Length / 2;
        if (!(area > 1e-6))
        {
            return null;
        }

        // The stored plane normal faces the visible side (into the air); fall back to the winding when absent.
        var stored = new Vec3d(f.Plane.Normal.X, f.Plane.Normal.Y, f.Plane.Normal.Z);
        double len = stored.Length;
        Vec3d n = len > 0.5 && double.IsFinite(len) ? stored.Scale(1 / len) : newell.Scale(1 / newell.Length);
        return (poly, n, area);
    }

    private static SFace? Prepare(Geometry geo, Face f, int index)
    {
        if (Polygon(geo, f) is not { } pg)
        {
            return null;
        }

        Vec3d[] poly = pg.Poly;
        Vec3d n = pg.Normal;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        Vec3d c = new(0, 0, 0);
        foreach (Vec3d p in poly)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
            maxZ = Math.Max(maxZ, p.Z);
            c = c.Add(p);
        }

        c = c.Scale(1.0 / poly.Length);
        double ax = Math.Abs(n.X), ay = Math.Abs(n.Y), az = Math.Abs(n.Z);
        (int a0, int a1) = ax >= ay && ax >= az ? (1, 2) : ay >= az ? (0, 2) : (0, 1);
        return new SFace
        {
            P = poly,
            N = n,
            D = -n.Dot(c),
            MinX = minX,
            MinY = minY,
            MinZ = minZ,
            MaxX = maxX,
            MaxY = maxY,
            MaxZ = maxZ,
            A0 = a0,
            A1 = a1,
            Floor = n.Y >= FloorNormalY && !IsInvisibleTexture(geo, f),
            Source = index,
        };
    }

    internal static Vec3d Newell(IReadOnlyList<Vec3d> poly)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            Vec3d a = poly[i];
            Vec3d b = poly[(i + 1) % poly.Count];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }

        return new Vec3d(x, y, z);
    }

    private void Tick()
    {
        if ((Interlocked.Increment(ref _iterations) & 1023) == 0)
        {
            _ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Which faces a ray may stop at.</summary>
    public enum HitFilter
    {
        Any,

        /// <summary>Only faces whose normal points down (ceilings seen from below).</summary>
        DownFacing,

        /// <summary>Only faces whose normal points up (floors seen from above).</summary>
        UpFacing,
    }

    /// <summary>The distance along unit direction <paramref name="d"/> to the first accepted face within <paramref name="maxT"/>, or null.</summary>
    public double? Cast(Vec3d o, Vec3d d, double maxT, HitFilter filter) => Cast(o, d, maxT, filter, out _);

    /// <summary>As <see cref="Cast(Vec3d, Vec3d, double, HitFilter)"/>, also returning the hit face (index into <see cref="Faces"/>, −1 on a miss).</summary>
    public double? Cast(Vec3d o, Vec3d d, double maxT, HitFilter filter, out int face)
    {
        Tick();
        return _bvh.Raycast(o, d, maxT, filter, out face);
    }

    /// <summary>True when the ray meets an accepted face within <paramref name="maxT"/> (the same acceptance as <see cref="Cast(Vec3d, Vec3d, double, HitFilter)"/>; cheaper).</summary>
    public bool Blocked(Vec3d o, Vec3d d, double maxT, HitFilter filter)
    {
        Tick();
        return _bvh.AnyHit(o, d, maxT, filter);
    }

    /// <summary>Point-in-polygon on two axes, the polygon taken as a fan of triangles (edges inclusive).</summary>
    internal static bool Inside(Vec3d[] poly, int a0, int a1, double px, double py)
    {
        double x0 = poly[0][a0], y0 = poly[0][a1];
        for (int i = 1; i + 1 < poly.Length; i++)
        {
            double x1 = poly[i][a0], y1 = poly[i][a1], x2 = poly[i + 1][a0], y2 = poly[i + 1][a1];
            double s = ((x1 - x0) * (y2 - y0)) - ((y1 - y0) * (x2 - x0));
            if (Math.Abs(s) < 1e-12)
            {
                continue;
            }

            double sign = Math.Sign(s);
            const double Eps = 1e-6;
            double c1 = (((x1 - x0) * (py - y0)) - ((y1 - y0) * (px - x0))) * sign;
            double c2 = (((x2 - x1) * (py - y1)) - ((y2 - y1) * (px - x1))) * sign;
            double c3 = (((x0 - x2) * (py - y2)) - ((y0 - y2) * (px - x2))) * sign;
            double tol = Eps * Math.Sqrt(Math.Abs(s));
            if (c1 >= -tol && c2 >= -tol && c3 >= -tol)
            {
                return true;
            }
        }

        return false;
    }

    // ---- the floor lattice ---------------------------------------------------------------------

    /// <summary>
    /// Lays the floor lattice (pitch <paramref name="pitch"/>, doubled until at most <paramref name="maxSamples"/>
    /// samples) and measures every sample: headroom, then — for valid samples — the 16 horizontal distances, width,
    /// travel axis, length and the corridor class.
    /// </summary>
    public void Measure(double pitch, int maxSamples) => Measure(pitch, maxSamples, focus: null, parallelism: 1);

    /// <summary>
    /// As <see cref="Measure(double, int)"/>, laying samples only on floor inside the XZ rectangle
    /// <paramref name="focus"/> (null = everywhere; floor faces wholly outside it yield no samples but still block rays)
    /// and measuring with up to <paramref name="parallelism"/> threads (the results do not depend on it).
    /// </summary>
    public void Measure(double pitch, int maxSamples, FocusRect? focus, int parallelism)
    {
        _focus = focus;
        double g = pitch > 0 ? pitch : 0.5;
        double projected = 0;
        foreach (SFace f in _faces.Where(IsLatticeFloor))
        {
            projected += Math.Abs(Newell(f.P).Y) / 2;
        }

        while (projected / (g * g) > maxSamples * 1.05)
        {
            g *= 2;
        }

        while (true)
        {
            _samples.Clear();
            if (Lay(g, maxSamples))
            {
                break;
            }

            g *= 2;
        }

        Pitch = g;
        if (parallelism <= 1 || _probe is not null)
        {
            foreach (Sample s in _samples)
            {
                _probe?.Invoke();
                MeasureSample(s);
            }
        }
        else
        {
            System.Threading.Tasks.Parallel.For(0, _samples.Count,
                new System.Threading.Tasks.ParallelOptions { CancellationToken = _ct, MaxDegreeOfParallelism = parallelism },
                i => MeasureSample(_samples[i]));
        }

        for (int i = 0; i < _samples.Count; i++)
        {
            Sample s = _samples[i];
            if (!s.Valid)
            {
                continue;
            }

            long key = LatticeKey(s.I, s.K);
            if (!_lattice.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>();
                _lattice[key] = list;
            }

            list.Add(i);
        }
    }

    /// <summary>An XZ rectangle the lattice is restricted to (metres, inclusive).</summary>
    public readonly record struct FocusRect(double MinX, double MaxX, double MinZ, double MaxZ);

    private FocusRect? _focus;

    /// <summary>A floor face that yields lattice samples under the current focus.</summary>
    private bool IsLatticeFloor(SFace f) =>
        f.Floor && (_focus is not { } r || !(f.MaxX < r.MinX || f.MinX > r.MaxX || f.MaxZ < r.MinZ || f.MinZ > r.MaxZ));

    private static long LatticeKey(long i, long k) => ((i + (1L << 31)) << 32) | (k + (1L << 31));

    /// <summary>Generates the samples at pitch <paramref name="g"/>; false when more than <paramref name="max"/> would be made.</summary>
    private bool Lay(double g, int max)
    {
        var seen = new HashSet<(int, int, long)>();
        foreach (SFace f in _faces)
        {
            if (!IsLatticeFloor(f))
            {
                continue;
            }

            double minX = f.MinX, maxX = f.MaxX, minZ = f.MinZ, maxZ = f.MaxZ;
            if (_focus is { } r)
            {
                minX = Math.Max(minX, r.MinX);
                maxX = Math.Min(maxX, r.MaxX);
                minZ = Math.Max(minZ, r.MinZ);
                maxZ = Math.Min(maxZ, r.MaxZ);
            }

            int i0 = (int)Math.Ceiling((minX / g) - 0.5), i1 = (int)Math.Floor((maxX / g) - 0.5);
            int k0 = (int)Math.Ceiling((minZ / g) - 0.5), k1 = (int)Math.Floor((maxZ / g) - 0.5);
            for (int i = i0; i <= i1; i++)
            {
                double x = (i + 0.5) * g;
                for (int k = k0; k <= k1; k++)
                {
                    Tick();
                    double z = (k + 0.5) * g;
                    if (!Inside(f.P, 0, 2, x, z))
                    {
                        continue;
                    }

                    double y = -((f.N.X * x) + (f.N.Z * z) + f.D) / f.N.Y;
                    if (!seen.Add((i, k, (long)Math.Round(y * 100))))
                    {
                        continue;
                    }

                    if (_samples.Count >= max)
                    {
                        return false;
                    }

                    _samples.Add(new Sample { P = new Vec3d(x, y, z), I = i, K = k });
                }
            }
        }

        return true;
    }

    private void MeasureSample(Sample s)
    {
        const double Lift = 0.005;
        double? up = Cast(new Vec3d(s.P.X, s.P.Y + Lift, s.P.Z), new Vec3d(0, 1, 0), SkyCap, HitFilter.DownFacing);
        s.Headroom = up is double h ? h + Lift : double.PositiveInfinity;
        s.Valid = s.Headroom >= MinHeadroom;
        if (!s.Valid)
        {
            return;
        }

        var o = new Vec3d(s.P.X, s.P.Y + ChestHeight, s.P.Z);
        Span<int> hitFace = stackalloc int[16];
        for (int k = 0; k < 16; k++)
        {
            s.Dist[k] = Cast(o, Directions[k], RayCap, HitFilter.Any, out hitFace[k]) ?? RayCap;
        }

        // A width pair must end on surfaces that face each other (horizontal normals at least 120 degrees
        // apart): a pair cutting across a corner ends on two perpendicular walls and would read as a sliver.
        double width = double.MaxValue, anyWidth = double.MaxValue;
        int minPair = -1, anyPair = 0;
        for (int k = 0; k < 8; k++)
        {
            double w = s.Dist[k] + s.Dist[k + 8];
            if (w < anyWidth - 1e-9)
            {
                anyWidth = w;
                anyPair = k;
            }

            if (w < width - 1e-9 && Facing(hitFace[k], hitFace[k + 8]))
            {
                width = w;
                minPair = k;
            }
        }

        if (minPair < 0)
        {
            width = anyWidth;
            minPair = anyPair;
        }

        int axis = (minPair + 4) % 16;
        s.Width = width;
        s.Axis = axis;
        s.Length = s.Dist[axis] + s.Dist[(axis + 8) % 16];
        s.Corridor = width <= 8 && s.Length >= 2.5 * width;
    }

    /// <summary>True when two ray hits end on surfaces that face each other (or either ray missed / hit a near-horizontal face).</summary>
    private bool Facing(int a, int b)
    {
        if (a < 0 || b < 0)
        {
            return true;
        }

        Vec3d na = _faces[a].N, nb = _faces[b].N;
        double la = Math.Sqrt((na.X * na.X) + (na.Z * na.Z)), lb = Math.Sqrt((nb.X * nb.X) + (nb.Z * nb.Z));
        if (la < 0.3 || lb < 0.3)
        {
            return true;
        }

        return ((na.X * nb.X) + (na.Z * nb.Z)) / (la * lb) <= -0.5;
    }

    // ---- queries over the measured lattice ------------------------------------------------------

    /// <summary>
    /// The nearest valid sample to <paramref name="p"/> within <paramref name="maxHorizontal"/> horizontally, no more
    /// than <paramref name="maxAbove"/> above it and <paramref name="maxBelow"/> below it; −1 when none.
    /// </summary>
    public int NearestValid(Vec3d p, double maxHorizontal, double maxAbove, double maxBelow)
    {
        if (Pitch <= 0)
        {
            return -1;
        }

        int r = (int)Math.Ceiling(maxHorizontal / Pitch) + 1;
        int ci = (int)Math.Floor(p.X / Pitch), ck = (int)Math.Floor(p.Z / Pitch);
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = ci - r; i <= ci + r; i++)
        {
            for (int k = ck - r; k <= ck + r; k++)
            {
                if (!_lattice.TryGetValue(LatticeKey(i, k), out List<int>? list))
                {
                    continue;
                }

                foreach (int si in list)
                {
                    Vec3d q = _samples[si].P;
                    double dx = q.X - p.X, dz = q.Z - p.Z, dy = q.Y - p.Y;
                    double hd = Math.Sqrt((dx * dx) + (dz * dz));
                    if (hd > maxHorizontal + 1e-9 || dy > maxAbove || -dy > maxBelow)
                    {
                        continue;
                    }

                    double d = Math.Sqrt((hd * hd) + (dy * dy));
                    if (d < bestD - 1e-12 || (Math.Abs(d - bestD) <= 1e-12 && si < best))
                    {
                        bestD = d;
                        best = si;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>Height of <paramref name="p"/> above the first upward-facing face below it (within 200 m), or null.</summary>
    public double? HeightAboveFloor(Vec3d p) => Cast(p, new Vec3d(0, -1, 0), SkyCap, HitFilter.UpFacing);

    /// <summary>One detected opening (a doorway-like narrowing).</summary>
    public sealed record Opening(double Width, double Height, double Depth, Vec3d Center, int Samples, Vec3d Axis);

    /// <summary>
    /// Openings (CORPUS-NORMS-SPEC §3.2): a valid sample is an opening sample when the samples 1.5 m either way along
    /// its travel axis are valid and at least 1/0.6 times wider, and it is at most 6 m wide. Opening samples cluster
    /// by 8-neighbour lattice adjacency (heights within 0.6 m); a cluster deeper than 3 m along its mean axis is a
    /// short corridor and is dropped, as are single-sample clusters at the base pitch. The snap radius for the ±1.5 m
    /// probes is 0.5 m, widened to 0.71 × pitch on a coarsened lattice so a probe always reaches its nearest point.
    /// </summary>
    public List<Opening> FindOpenings()
    {
        double snap = Math.Max(0.5, 0.71 * Pitch);
        var flagged = new List<int>();
        for (int i = 0; i < _samples.Count; i++)
        {
            Sample s = _samples[i];
            if (!s.Valid || s.Width > 6)
            {
                continue;
            }

            Tick();
            Vec3d a = Directions[s.Axis];
            int pPlus = NearestValid(s.P.Add(a.Scale(1.5)), snap, 0.6, 0.6);
            int pMinus = NearestValid(s.P.Sub(a.Scale(1.5)), snap, 0.6, 0.6);
            if (pPlus < 0 || pMinus < 0)
            {
                continue;
            }

            if (s.Width <= 0.6 * Math.Min(_samples[pPlus].Width, _samples[pMinus].Width))
            {
                flagged.Add(i);
            }
        }

        // Cluster by 8-neighbour adjacency on the lattice, same height ± 0.6 m.
        var parent = new Dictionary<int, int>();
        foreach (int i in flagged)
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

        var byCell = new Dictionary<long, List<int>>();
        foreach (int i in flagged)
        {
            long key = LatticeKey(_samples[i].I, _samples[i].K);
            if (!byCell.TryGetValue(key, out List<int>? l))
            {
                l = new List<int>();
                byCell[key] = l;
            }

            l.Add(i);
        }

        foreach (int i in flagged)
        {
            Sample s = _samples[i];
            for (int di = -1; di <= 1; di++)
            {
                for (int dk = -1; dk <= 1; dk++)
                {
                    if (!byCell.TryGetValue(LatticeKey(s.I + di, s.K + dk), out List<int>? l))
                    {
                        continue;
                    }

                    foreach (int j in l)
                    {
                        if (j != i && Math.Abs(_samples[j].P.Y - s.P.Y) <= 0.6)
                        {
                            int ri = Find(i), rj = Find(j);
                            if (ri != rj)
                            {
                                parent[Math.Max(ri, rj)] = Math.Min(ri, rj);
                            }
                        }
                    }
                }
            }
        }

        int minSamples = Pitch <= 0.5 + 1e-9 ? 2 : 1;
        var result = new List<Opening>();
        foreach (IGrouping<int, int> cluster in flagged.GroupBy(Find).OrderBy(g => g.Key))
        {
            List<int> members = cluster.OrderBy(i => i).ToList();
            if (members.Count < minSamples)
            {
                continue;
            }

            // Mean travel axis (axes are sign-ambiguous: align each to the first).
            Vec3d first = Directions[_samples[members[0]].Axis];
            Vec3d sum = new(0, 0, 0);
            Vec3d centre = new(0, 0, 0);
            foreach (int m in members)
            {
                Vec3d a = Directions[_samples[m].Axis];
                sum = sum.Add(a.Dot(first) < 0 ? a.Scale(-1) : a);
                centre = centre.Add(_samples[m].P);
            }

            centre = centre.Scale(1.0 / members.Count);
            Vec3d axis = sum.Length > 1e-9 ? sum.Scale(1 / sum.Length) : first;
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (int m in members)
            {
                double t = _samples[m].P.Dot(axis);
                lo = Math.Min(lo, t);
                hi = Math.Max(hi, t);
            }

            double depth = hi - lo + Pitch;
            if (depth > 3.0 + 1e-9)
            {
                continue;
            }

            double width = Median(members.Select(m => _samples[m].Width));
            double[] heads = members.Select(m => _samples[m].Headroom).Where(double.IsFinite).ToArray();
            double height = heads.Length > 0 ? Median(heads) : double.NaN;
            result.Add(new Opening(width, height, depth, centre, members.Count, axis));
        }

        return result;
    }

    internal static double Median(IEnumerable<double> values)
    {
        double[] v = values.ToArray();
        Array.Sort(v);
        return Percentiles.Type7(v, 0.5);
    }
}
