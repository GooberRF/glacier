using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

/// <summary>
/// A bounding-volume hierarchy over the compiled level's polygon faces (AI-SUITE-PLAN §5.3, DESIGN-QUALITY-SPEC §(e)
/// E2): nearest-hit and any-hit ray queries. It replaces the sparse uniform grid (4 m cells) the floor-lattice sampler
/// used and answers every ray exactly as that grid did, so the corpus norms and the exemplar study stay comparable:
/// the per-face test is the same (plane intersection, then the fan point-in-polygon test with inclusive edges), and
/// the grid's visiting order is kept where it decides the answer: a face only counts once the ray reaches one of the
/// 4 m cells its bounds overlap (cells are half-open, so a ray running exactly along a cell boundary does not graze a
/// face ending a hair short of it), faces hit at exactly the same distance resolve in the grid's order (faces too large
/// to grid first, then by where the ray enters the face's cells, then by face index), and a hit the polygon test
/// accepts just before its face's cells only stands if the grid would have reached those cells. Immutable after
/// construction and safe to query from many threads at once.
/// </summary>
internal sealed class FaceBvh
{
    /// <summary>Faces per leaf.</summary>
    private const int LeafSize = 4;

    /// <summary>The cell size of the grid whose tie order is kept (m).</summary>
    private const double GridCell = 4.0;

    /// <summary>Faces whose bounds covered more grid cells than this were tested before any cell.</summary>
    private const long MaxCellsPerFace = 4096;

    private readonly IReadOnlyList<SpaceSampler.SFace> _faces;

    /// <summary>Face indices, reordered so every node covers a contiguous range.</summary>
    private readonly int[] _order;

    private readonly List<Node> _build = new();

    /// <summary>The nodes (root first), as an array for the hot query loops.</summary>
    private readonly Node[] _nodes;

    /// <summary>Per face: tested before every cell by the grid (too large to grid).</summary>
    private readonly bool[] _big;

    /// <summary>Per face: its bounds snapped out to grid cells (min x, y, z, max x, y, z; half-open).</summary>
    private readonly double[] _cellBox;

    private struct Node
    {
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        /// <summary>Leaf: the first index into <see cref="_order"/>.</summary>
        public int Start;

        /// <summary>Leaf: face count; interior: 0.</summary>
        public int Count;

        /// <summary>Interior: the two children.</summary>
        public int Left, Right;
    }

    public FaceBvh(IReadOnlyList<SpaceSampler.SFace> faces)
    {
        _faces = faces ?? throw new ArgumentNullException(nameof(faces));
        _order = new int[faces.Count];
        _big = new bool[faces.Count];
        _cellBox = new double[faces.Count * 6];
        for (int i = 0; i < _order.Length; i++)
        {
            _order[i] = i;
            SpaceSampler.SFace f = faces[i];
            long cells = (CellIndex(f.MaxX) - CellIndex(f.MinX) + 1) * (CellIndex(f.MaxY) - CellIndex(f.MinY) + 1) * (CellIndex(f.MaxZ) - CellIndex(f.MinZ) + 1);
            _big[i] = cells > MaxCellsPerFace;
            _cellBox[(i * 6) + 0] = CellIndex(f.MinX) * GridCell;
            _cellBox[(i * 6) + 1] = CellIndex(f.MinY) * GridCell;
            _cellBox[(i * 6) + 2] = CellIndex(f.MinZ) * GridCell;
            _cellBox[(i * 6) + 3] = (CellIndex(f.MaxX) + 1) * GridCell;
            _cellBox[(i * 6) + 4] = (CellIndex(f.MaxY) + 1) * GridCell;
            _cellBox[(i * 6) + 5] = (CellIndex(f.MaxZ) + 1) * GridCell;
        }

        if (faces.Count > 0)
        {
            var cx = new double[faces.Count];
            var cy = new double[faces.Count];
            var cz = new double[faces.Count];
            for (int i = 0; i < faces.Count; i++)
            {
                SpaceSampler.SFace f = faces[i];
                cx[i] = (f.MinX + f.MaxX) * 0.5;
                cy[i] = (f.MinY + f.MaxY) * 0.5;
                cz[i] = (f.MinZ + f.MaxZ) * 0.5;
            }

            BuildNode(0, faces.Count, cx, cy, cz);
        }

        _nodes = _build.ToArray();
        _build.Clear();
    }

    /// <summary>The number of faces.</summary>
    public int Count => _faces.Count;

    /// <summary>The number of BVH nodes (diagnostics).</summary>
    public int NodeCount => _nodes.Length;

    private int BuildNode(int start, int end, double[] cx, double[] cy, double[] cz)
    {
        int index = _build.Count;
        var node = new Node
        {
            MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue,
            MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue,
        };
        double cminX = double.MaxValue, cminY = double.MaxValue, cminZ = double.MaxValue;
        double cmaxX = double.MinValue, cmaxY = double.MinValue, cmaxZ = double.MinValue;
        for (int i = start; i < end; i++)
        {
            int fi = _order[i];
            SpaceSampler.SFace f = _faces[fi];
            node.MinX = Math.Min(node.MinX, f.MinX);
            node.MinY = Math.Min(node.MinY, f.MinY);
            node.MinZ = Math.Min(node.MinZ, f.MinZ);
            node.MaxX = Math.Max(node.MaxX, f.MaxX);
            node.MaxY = Math.Max(node.MaxY, f.MaxY);
            node.MaxZ = Math.Max(node.MaxZ, f.MaxZ);
            cminX = Math.Min(cminX, cx[fi]);
            cminY = Math.Min(cminY, cy[fi]);
            cminZ = Math.Min(cminZ, cz[fi]);
            cmaxX = Math.Max(cmaxX, cx[fi]);
            cmaxY = Math.Max(cmaxY, cy[fi]);
            cmaxZ = Math.Max(cmaxZ, cz[fi]);
        }

        _build.Add(node);
        int count = end - start;
        double ex = cmaxX - cminX, ey = cmaxY - cminY, ez = cmaxZ - cminZ;
        if (count <= LeafSize || Math.Max(ex, Math.Max(ey, ez)) <= 1e-9)
        {
            node.Start = start;
            node.Count = count;
            _build[index] = node;
            return index;
        }

        // Median split on the longest centroid axis (stable: ties keep face-index order).
        double[] key = ex >= ey && ex >= ez ? cx : ey >= ez ? cy : cz;
        Array.Sort(_order, start, count, Comparer<int>.Create((a, b) =>
        {
            int c = key[a].CompareTo(key[b]);
            return c != 0 ? c : a.CompareTo(b);
        }));
        int mid = start + (count / 2);
        int left = BuildNode(start, mid, cx, cy, cz);
        int right = BuildNode(mid, end, cx, cy, cz);
        node.Left = left;
        node.Right = right;
        node.Count = 0;
        _build[index] = node;
        return index;
    }

    /// <summary>
    /// The nearest accepted face along unit direction <paramref name="d"/> from <paramref name="o"/> within
    /// <paramref name="maxT"/> (exclusive; hits closer than 1e-6 are ignored), or null. <paramref name="face"/> is
    /// the hit face's index (−1 on a miss).
    /// </summary>
    public double? Raycast(Vec3d o, Vec3d d, double maxT, SpaceSampler.HitFilter filter, out int face)
    {
        face = -1;
        if (_nodes.Length == 0)
        {
            return null;
        }

        double ix = 1.0 / d.X, iy = 1.0 / d.Y, iz = 1.0 / d.Z;
        double best = maxT, bestTe = 0;
        int hit = -1;
        Span<int> stack = stackalloc int[128];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly Node n = ref _nodes[stack[--sp]];
            if (!SlabHit(n, o, ix, iy, iz, best, out _))
            {
                continue;
            }

            if (n.Count > 0)
            {
                for (int k = n.Start; k < n.Start + n.Count; k++)
                {
                    int fi = _order[k];
                    if (!TestFace(_faces[fi], o, d, filter, out double t) || t > best || (t == best && hit < 0))
                    {
                        continue;
                    }

                    double te = CellEntry(fi, o, d, maxT);
                    if (double.IsPositiveInfinity(te))
                    {
                        continue; // the ray never reaches the face's cells
                    }

                    if (t < best || GridBefore(fi, hit, o, d, maxT))
                    {
                        best = t;
                        hit = fi;
                        bestTe = te;
                    }
                }

                continue;
            }

            if (sp + 2 > stack.Length)
            {
                return Simulate(o, d, maxT, maxT, filter, out face);
            }

            // Near child last on the stack so it is visited first.
            ref readonly Node l = ref _nodes[n.Left];
            ref readonly Node r = ref _nodes[n.Right];
            bool hl = SlabHit(l, o, ix, iy, iz, best, out double tl);
            bool hr = SlabHit(r, o, ix, iy, iz, best, out double tr);
            if (hl && hr)
            {
                stack[sp++] = tl <= tr ? n.Right : n.Left;
                stack[sp++] = tl <= tr ? n.Left : n.Right;
            }
            else if (hl)
            {
                stack[sp++] = n.Left;
            }
            else if (hr)
            {
                stack[sp++] = n.Right;
            }
        }

        if (hit < 0 || bestTe <= best)
        {
            face = hit;
            return hit >= 0 ? best : null;
        }

        // The nearest hit lies before its face's cells (the polygon test's edge tolerance): the grid only reached it
        // if no hit it met earlier stopped it first. Replay the grid's order over the faces it could have met.
        return Simulate(o, d, maxT, bestTe, filter, out face);
    }

    /// <summary>True when any accepted face lies along the ray within <paramref name="maxT"/> (the same acceptance as <see cref="Raycast"/>).</summary>
    public bool AnyHit(Vec3d o, Vec3d d, double maxT, SpaceSampler.HitFilter filter)
    {
        if (_nodes.Length == 0)
        {
            return false;
        }

        double ix = 1.0 / d.X, iy = 1.0 / d.Y, iz = 1.0 / d.Z;
        Span<int> stack = stackalloc int[128];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly Node n = ref _nodes[stack[--sp]];
            if (!SlabHit(n, o, ix, iy, iz, maxT, out _))
            {
                continue;
            }

            if (n.Count > 0)
            {
                for (int k = n.Start; k < n.Start + n.Count; k++)
                {
                    int fi = _order[k];
                    if (TestFace(_faces[fi], o, d, filter, out double t) && t < maxT && !double.IsPositiveInfinity(CellEntry(fi, o, d, maxT)))
                    {
                        return true;
                    }
                }

                continue;
            }

            if (sp + 2 > stack.Length)
            {
                return Simulate(o, d, maxT, maxT, filter, out _) is not null;
            }

            stack[sp++] = n.Left;
            stack[sp++] = n.Right;
        }

        return false;
    }

    /// <summary>
    /// The grid's answer, replayed: every face with an accepted hit before <paramref name="bound"/> whose cells the ray
    /// reaches is a candidate, and the grid's own cell walk decides which of them it met first.
    /// </summary>
    private double? Simulate(Vec3d o, Vec3d d, double maxT, double bound, SpaceSampler.HitFilter filter, out int face)
    {
        var candidates = new List<(int Fi, double T)>();
        double ix = 1.0 / d.X, iy = 1.0 / d.Y, iz = 1.0 / d.Z;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            ref readonly Node n = ref _nodes[stack.Pop()];
            if (!SlabHit(n, o, ix, iy, iz, Math.Min(maxT, bound + 1e-6), out _))
            {
                continue;
            }

            if (n.Count > 0)
            {
                for (int k = n.Start; k < n.Start + n.Count; k++)
                {
                    int fi = _order[k];
                    if (TestFace(_faces[fi], o, d, filter, out double t) && t < maxT && t <= bound)
                    {
                        candidates.Add((fi, t));
                    }
                }

                continue;
            }

            stack.Push(n.Left);
            stack.Push(n.Right);
        }

        candidates.Sort((a, b) => a.Fi.CompareTo(b.Fi));
        double best = maxT;
        int hit = -1;

        // Faces too large to grid come first, in index order.
        foreach ((int fi, double t) in candidates)
        {
            if (_big[fi] && t < best)
            {
                best = t;
                hit = fi;
            }
        }

        var pending = candidates.Where(c => !_big[c.Fi]).ToList();
        WalkCells(o, d, maxT, (cx, cy, cz, tExit) =>
        {
            for (int i = 0; i < pending.Count; i++)
            {
                if (InCells(pending[i].Fi, cx, cy, cz))
                {
                    if (pending[i].T < best)
                    {
                        best = pending[i].T;
                        hit = pending[i].Fi;
                    }

                    pending.RemoveAt(i--);
                }
            }

            return pending.Count > 0 && !(hit >= 0 && best <= tExit);
        });
        face = hit;
        return hit >= 0 ? best : null;
    }

    private static long CellIndex(double v) => (long)Math.Floor(v / GridCell);

    /// <summary>True when cell (cx, cy, cz) lies in face <paramref name="fi"/>'s cell range.</summary>
    private bool InCells(int fi, long cx, long cy, long cz)
    {
        int b = fi * 6;
        return cx >= (long)Math.Floor(_cellBox[b] / GridCell) && cx < (long)Math.Floor(_cellBox[b + 3] / GridCell)
            && cy >= (long)Math.Floor(_cellBox[b + 1] / GridCell) && cy < (long)Math.Floor(_cellBox[b + 4] / GridCell)
            && cz >= (long)Math.Floor(_cellBox[b + 2] / GridCell) && cz < (long)Math.Floor(_cellBox[b + 5] / GridCell);
    }

    /// <summary>
    /// The grid's cell walk (3D DDA, same arithmetic): visits each cell along the ray in order, calling
    /// <paramref name="visit"/> with the cell and its exit distance, until it returns false or the walk passes
    /// <paramref name="maxT"/>.
    /// </summary>
    private static void WalkCells(Vec3d o, Vec3d d, double maxT, Func<long, long, long, double, bool> visit)
    {
        long ix = CellIndex(o.X), iy = CellIndex(o.Y), iz = CellIndex(o.Z);
        int sx = Math.Sign(d.X), sy = Math.Sign(d.Y), sz = Math.Sign(d.Z);
        double tMaxX = sx != 0 ? (((ix + (sx > 0 ? 1 : 0)) * GridCell) - o.X) / d.X : double.PositiveInfinity;
        double tMaxY = sy != 0 ? (((iy + (sy > 0 ? 1 : 0)) * GridCell) - o.Y) / d.Y : double.PositiveInfinity;
        double tMaxZ = sz != 0 ? (((iz + (sz > 0 ? 1 : 0)) * GridCell) - o.Z) / d.Z : double.PositiveInfinity;
        double tdX = sx != 0 ? GridCell / Math.Abs(d.X) : double.PositiveInfinity;
        double tdY = sy != 0 ? GridCell / Math.Abs(d.Y) : double.PositiveInfinity;
        double tdZ = sz != 0 ? GridCell / Math.Abs(d.Z) : double.PositiveInfinity;
        for (int guard = 0; guard < 4096; guard++)
        {
            double tExit = Math.Min(tMaxX, Math.Min(tMaxY, tMaxZ));
            if (!visit(ix, iy, iz, tExit) || tExit > maxT)
            {
                return;
            }

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                ix += sx;
                tMaxX += tdX;
            }
            else if (tMaxY <= tMaxZ)
            {
                iy += sy;
                tMaxY += tdY;
            }
            else
            {
                iz += sz;
                tMaxZ += tdZ;
            }
        }
    }

    /// <summary>True when the grid would have tested face <paramref name="a"/> before face <paramref name="b"/> along the ray.</summary>
    private bool GridBefore(int a, int b, Vec3d o, Vec3d d, double maxT)
    {
        if (_big[a] || _big[b])
        {
            return _big[a] && (!_big[b] || a < b);
        }

        int first = -1;
        WalkCells(o, d, maxT, (cx, cy, cz, _) =>
        {
            bool ia = InCells(a, cx, cy, cz), ib = InCells(b, cx, cy, cz);
            if (ia || ib)
            {
                first = ia && ib ? Math.Min(a, b) : ia ? a : b;
                return false;
            }

            return true;
        });
        return first == a || (first < 0 && a < b);
    }

    /// <summary>
    /// Where the ray first enters face <paramref name="fi"/>'s cells (its bounds snapped out to the 4 m grid; half-open,
    /// so a ray parallel to a cell boundary plane and exactly on it is outside the cells below that plane), or +∞ when it
    /// does not within <paramref name="maxT"/>. Faces too large to grid count as entered at 0.
    /// </summary>
    private double CellEntry(int fi, Vec3d o, Vec3d d, double maxT)
    {
        if (_big[fi])
        {
            return 0;
        }

        int b = fi * 6;
        double t0 = 0, t1 = maxT;
        return HalfOpenAxis(o.X, d.X, _cellBox[b], _cellBox[b + 3], ref t0, ref t1)
            && HalfOpenAxis(o.Y, d.Y, _cellBox[b + 1], _cellBox[b + 4], ref t0, ref t1)
            && HalfOpenAxis(o.Z, d.Z, _cellBox[b + 2], _cellBox[b + 5], ref t0, ref t1)
            ? t0
            : double.PositiveInfinity;
    }

    private static bool HalfOpenAxis(double o, double d, double lo, double hi, ref double t0, ref double t1)
    {
        if (d == 0)
        {
            return o >= lo && o < hi;
        }

        double a = (lo - o) / d, b = (hi - o) / d;
        if (a > b)
        {
            (a, b) = (b, a);
        }

        t0 = Math.Max(t0, a);
        t1 = Math.Min(t1, b);
        return t0 <= t1;
    }

    /// <summary>Ray vs node box (slab test, inclusive, padded); <paramref name="tEnter"/> is the entry distance (≥ 0).</summary>
    private static bool SlabHit(in Node n, Vec3d o, double ix, double iy, double iz, double maxT, out double tEnter)
    {
        // Wider than the polygon test's edge tolerance, so a hit it accepts just outside a face's bounds is never pruned.
        const double Pad = 1e-3;
        double t0 = 0, t1 = maxT;
        if (!Axis(o.X, ix, n.MinX - Pad, n.MaxX + Pad, ref t0, ref t1)
            || !Axis(o.Y, iy, n.MinY - Pad, n.MaxY + Pad, ref t0, ref t1)
            || !Axis(o.Z, iz, n.MinZ - Pad, n.MaxZ + Pad, ref t0, ref t1))
        {
            tEnter = 0;
            return false;
        }

        tEnter = t0;
        return true;
    }

    private static bool Axis(double o, double inv, double lo, double hi, ref double t0, ref double t1)
    {
        if (double.IsInfinity(inv))
        {
            // Parallel to this slab: inside or never.
            return o >= lo && o <= hi;
        }

        double a = (lo - o) * inv, b = (hi - o) * inv;
        if (a > b)
        {
            (a, b) = (b, a);
        }

        if (a > t0)
        {
            t0 = a;
        }

        if (b < t1)
        {
            t1 = b;
        }

        return t0 <= t1;
    }

    /// <summary>The sampler's face test: the plane hit at t &gt; 1e-6 lies inside the polygon.</summary>
    internal static bool TestFace(SpaceSampler.SFace f, Vec3d o, Vec3d d, SpaceSampler.HitFilter filter, out double t)
    {
        t = 0;
        if ((filter == SpaceSampler.HitFilter.DownFacing && f.N.Y >= -1e-6) || (filter == SpaceSampler.HitFilter.UpFacing && f.N.Y <= 1e-6))
        {
            return false;
        }

        double denom = f.N.Dot(d);
        if (Math.Abs(denom) < 1e-12)
        {
            return false;
        }

        t = -(f.N.Dot(o) + f.D) / denom;
        if (!(t > 1e-6))
        {
            return false;
        }

        Vec3d p = o.Add(d.Scale(t));
        return SpaceSampler.Inside(f.P, f.A0, f.A1, p[f.A0], p[f.A1]);
    }
}
