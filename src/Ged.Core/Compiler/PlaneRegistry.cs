using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// A global registry of the level's face planes, the substrate for RED-style
/// SHARED splitting. RED reaches watertightness because every face is clipped
/// against the SAME per-brush BSP node planes, so adjacent/coincident faces split
/// along identical lines and their vertices coincide by construction
/// (compiler-parity-notes.md, phase-3 <c>FUN_004a8220</c>). GED's exact per-face
/// splitter computed each face's cut points independently, so two faces that
/// should share an edge diverged (float noise on near-coincident cases, and — worse
/// — different fragment extents at brush junctions), leaving open seams.
/// <para>
/// This registry realises RED's shared split directly and more robustly than a BSP:
/// every distinct plane is interned to a canonical id (its orientation folded away,
/// so a plane and its flip share one id), and the intersection point of any three
/// planes is computed ONCE, in double precision, and cached by the sorted id triple.
/// A cut vertex is therefore identified by the three planes through it, not by a
/// float position — so any two faces that cut along the same three planes receive
/// the byte-identical <see cref="Vec3"/>. Coincidence by construction, exactly as in
/// RED, without the fragment explosion of pushing every face through one global BSP.
/// </para>
/// </summary>
public sealed class PlaneRegistry
{
    // Two planes are the same surface when their (orientation-folded) normals are
    // within this dot of parallel and their offsets within OffsetTol. 2 cm-apart
    // parallel walls (the real "extent divergence" gaps) stay distinct.
    private const double NormalDotTol = 0.99997;
    private const double OffsetTol = 2e-3;

    private readonly List<Plane> _planes = new();
    private readonly Dictionary<(int, int, int, int), List<int>> _hash = new();

    // Interning (writes _planes/_hash) happens single-threaded during AddBrush; the
    // triple cache is read+written concurrently while splitting runs under Parallel.For.
    private readonly ConcurrentDictionary<(int, int, int), Vec3?> _triCache = new();

    private readonly record struct Plane(double Nx, double Ny, double Nz, double D);

    /// <summary>Number of distinct interned planes.</summary>
    public int Count => _planes.Count;

    /// <summary>
    /// Per-path policy for <see cref="CsgSharedSplit.CutVertex"/> (set by the solver, measured per path):
    /// when true (the per-brush accumulator), a triple point further than the weld scale from the edge's own
    /// intersection is rejected in favour of the lerp — an ill-conditioned triple (near-parallel member,
    /// registry fold amplified by 1/sin) can land centimetres away and tear an unweldable seam (dmabrupt
    /// 158→138 with the bound). When false (leaf extraction), the triple point is always used: extraction's
    /// watertightness is the BIT-IDENTITY of shared triples across neighbouring portals, and a one-sided
    /// rejection breaks that identity into new &gt;1e-3 seams (measured +52 dm04 / +48 ctf01).
    /// </summary>
    public bool BoundTripleDeviation { get; set; } = true;

    /// <summary>
    /// Construction-time shared vertex identity for the EdgeLerpSplit path (flagship 19). When set (only on
    /// the incremental fold under <c>CompileOptions.EdgeLerpSplit</c>), <see cref="CsgSharedSplit.CutVertex"/>
    /// computes a cut point by on-edge lerp on the endpoints and shares it across flanking faces by the edge
    /// key (endpoint ids + cutter), instead of the plane-triple registry point. Null on every other path
    /// (byte-unchanged: a cut vertex then carries VId = -1 and takes the registry-triple/lerp position).
    /// </summary>
    internal EdgeStore? EdgeStore { get; set; }

    /// <summary>
    /// Interns <paramref name="p"/> and returns its canonical id. A plane and its
    /// flip fold to one id (orientation is discarded for vertex identity).
    /// </summary>
    public int Intern(CsgPlane p)
    {
        // Fold orientation: make the dominant component positive so P and -P agree.
        double nx = p.Normal.X, ny = p.Normal.Y, nz = p.Normal.Z, d = p.Offset;
        double len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (len < 1e-12)
        {
            return -1;
        }

        nx /= len; ny /= len; nz /= len; d /= len;
        double ax = Math.Abs(nx), ay = Math.Abs(ny), az = Math.Abs(nz);
        double dom = ax >= ay && ax >= az ? nx : (ay >= az ? ny : nz);
        if (dom < 0)
        {
            nx = -nx; ny = -ny; nz = -nz; d = -d;
        }

        var key = ((int)Math.Round(nx * 256), (int)Math.Round(ny * 256), (int)Math.Round(nz * 256), (int)Math.Round(d * 64));
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dd = -1; dd <= 1; dd++)
                    {
                        var probe = (key.Item1 + dx, key.Item2 + dy, key.Item3 + dz, key.Item4 + dd);
                        if (!_hash.TryGetValue(probe, out List<int>? bucket))
                        {
                            continue;
                        }

                        foreach (int id in bucket)
                        {
                            Plane q = _planes[id];
                            if ((q.Nx * nx) + (q.Ny * ny) + (q.Nz * nz) >= NormalDotTol && Math.Abs(q.D - d) <= OffsetTol)
                            {
                                return id;
                            }
                        }
                    }
                }
            }
        }

        int newId = _planes.Count;
        _planes.Add(new Plane(nx, ny, nz, d));
        if (!_hash.TryGetValue(key, out List<int>? cell))
        {
            _hash[key] = cell = new List<int>();
        }

        cell.Add(newId);
        return newId;
    }

    /// <summary>
    /// The canonical geometry of interned plane <paramref name="id"/> (orientation folded to the
    /// dominant-positive convention). Used by the B-rep cap re-cut to split a cap face by the SAME
    /// registry planes the flanking world faces were cut by, so the cut vertices land on shared triples.
    /// </summary>
    public bool TryGetPlane(int id, out CsgPlane plane)
    {
        if ((uint)id >= (uint)_planes.Count)
        {
            plane = default;
            return false;
        }

        Plane p = _planes[id];
        plane = new CsgPlane(new Vec3((float)p.Nx, (float)p.Ny, (float)p.Nz), (float)p.D);
        return true;
    }

    /// <summary>
    /// Exact intersection point of three interned planes, cached by the sorted id
    /// triple so every caller gets the byte-identical point. Returns null when the
    /// planes are (near) parallel / ill-conditioned (caller falls back to lerp).
    /// </summary>
    public Vec3? Intersect(int a, int b, int c)
    {
        if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c)
        {
            return null;
        }

        // Sort the triple so the cache key is order-independent.
        int i0 = a, i1 = b, i2 = c;
        if (i0 > i1)
        {
            (i0, i1) = (i1, i0);
        }

        if (i1 > i2)
        {
            (i1, i2) = (i2, i1);
        }

        if (i0 > i1)
        {
            (i0, i1) = (i1, i0);
        }

        return _triCache.GetOrAdd((i0, i1, i2), static (k, planes) => Solve(planes[k.Item1], planes[k.Item2], planes[k.Item3]), _planes);
    }

    /// <summary>Cramer's-rule solve of three planes n·x = -d, in double precision.</summary>
    private static Vec3? Solve(Plane p, Plane q, Plane r)
    {
        // Rows are normals; RHS is -offset (plane is n·x + d = 0).
        double a11 = p.Nx, a12 = p.Ny, a13 = p.Nz;
        double a21 = q.Nx, a22 = q.Ny, a23 = q.Nz;
        double a31 = r.Nx, a32 = r.Ny, a33 = r.Nz;
        double det =
            (a11 * ((a22 * a33) - (a23 * a32))) -
            (a12 * ((a21 * a33) - (a23 * a31))) +
            (a13 * ((a21 * a32) - (a22 * a31)));
        if (Math.Abs(det) < 1e-9)
        {
            return null; // near-parallel; ill-conditioned
        }

        double b1 = -p.D, b2 = -q.D, b3 = -r.D;
        double x =
            ((b1 * ((a22 * a33) - (a23 * a32))) -
             (a12 * ((b2 * a33) - (a23 * b3))) +
             (a13 * ((b2 * a32) - (a22 * b3)))) / det;
        double y =
            ((a11 * ((b2 * a33) - (a23 * b3))) -
             (b1 * ((a21 * a33) - (a23 * a31))) +
             (a13 * ((a21 * b3) - (b2 * a31)))) / det;
        double z =
            ((a11 * ((a22 * b3) - (b2 * a32))) -
             (a12 * ((a21 * b3) - (b2 * a31))) +
             (b1 * ((a21 * a32) - (a22 * a31)))) / det;
        return new Vec3((float)x, (float)y, (float)z);
    }
}
