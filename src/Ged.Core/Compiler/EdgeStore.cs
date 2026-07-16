using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// CONSTRUCTION-TIME shared vertex identity for the incremental fold (flagship 19 — the
/// on-edge-arithmetic swap). RED's watertightness at a divergent organic corner is a TOPOLOGICAL
/// property, not an arithmetic one: every cut vertex is computed ON the edge being cut
/// (<c>t = -((edgeStart·N)+d)/(edgeDir·N)</c>, <c>point = edgeStart + t·edgeDir</c>, binary-verified
/// <c>FUN_0048e2c0</c>), and adjacent faces reference SHARED vertex ids in their per-face loops
/// (<c>{vertexId,x,y,z,w,next,prev}</c>), so a shared edge cut by one plane yields the byte-identical
/// point in every flanking face automatically. Prior GED attempts keyed the cut on the PLANE TRIPLE
/// (<c>{facePlane, edgePlane, cutter}</c>), which only coincides across faces when the same triple is
/// used — near-parallel terrain neighbours reach the same physical edge bounded by DIFFERENT edge
/// planes, so the "exact" point diverges 0.1–3 mm (the residual station cohort) unfixable by any weld.
/// <para>
/// This store realises RED's mechanism directly. Authored corners are interned by canonical position
/// (coincident corners across brushes collapse to ONE id + ONE position — the shared-vertex-id
/// property the RFL's per-brush vertex indices only give WITHIN a brush). A cut of the edge
/// (idA, idB) by registry plane <c>cutter</c> is interned ONCE by the canonical key
/// (min id, max id, cutter): the first writer computes the on-edge lerp on the endpoints' stored
/// positions, and every later face carrying the SAME edge and cutter reuses the byte-identical
/// point + id. Two flanking faces that share an edge therefore share the cut automatically, and the
/// on-edge lerp keeps ill-conditioned near-parallel edges at float noise instead of amplifying a
/// registry fold by 1/sin(angle).
/// </para>
/// Used only on the incremental fold under <c>CompileOptions.EdgeLerpSplit</c>; the fold is
/// single-threaded, so plain dictionaries suffice.
/// </summary>
internal sealed class EdgeStore
{
    private readonly float _tol;
    private readonly float _cell;
    private readonly Dictionary<(int, int, int), List<(Vec3 Pos, int Id)>> _corners = new();
    private readonly Dictionary<(int, int, int), (int Id, Vec3 Pos)> _cuts = new();
    private int _next;

    /// <param name="mergeTol">Coincident-corner merge tolerance (metres). 0 = exact bit match only;
    /// a positive value unifies authored/cut corners within that distance to one shared id (RED's
    /// 1e-4 fixer scale up through the measured station cohort).</param>
    public EdgeStore(float mergeTol)
    {
        _tol = mergeTol;
        _cell = MathF.Max(mergeTol, 1e-4f);
    }

    /// <summary>Distinct vertex ids issued so far.</summary>
    public int VertexCount => _next;

    /// <summary>Corner merges (an intern that returned an existing id).</summary>
    public int CornerMerges { get; private set; }

    /// <summary>Distinct interned edge cuts.</summary>
    public int CutCount => _cuts.Count;

    /// <summary>
    /// Interns an authored/original corner by canonical position. Coincident corners (within the merge
    /// tolerance) collapse to the FIRST-seen id and position, so every face through that corner uses one
    /// shared vertex id and one shared position. Returns the canonical id + position (the caller adopts
    /// the canonical position so a divergent-triple snap does not re-open the seam).
    /// </summary>
    public (int Id, Vec3 Pos) InternCorner(Vec3 p)
    {
        (int cx, int cy, int cz) = Cell(p);
        float tolSq = _tol * _tol;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!_corners.TryGetValue((cx + dx, cy + dy, cz + dz), out List<(Vec3 Pos, int Id)>? bucket))
                    {
                        continue;
                    }

                    foreach ((Vec3 pos, int id) in bucket)
                    {
                        float dsq = pos.Sub(p).LengthSquared();
                        if (_tol <= 0f ? dsq == 0f : dsq <= tolSq)
                        {
                            CornerMerges++;
                            return (id, pos);
                        }
                    }
                }
            }
        }

        int newId = _next++;
        if (!_corners.TryGetValue((cx, cy, cz), out List<(Vec3 Pos, int Id)>? cell))
        {
            _corners[(cx, cy, cz)] = cell = new List<(Vec3 Pos, int Id)>();
        }

        cell.Add((p, newId));
        return (newId, p);
    }

    /// <summary>
    /// Interns the cut of edge (<paramref name="idA"/>→<paramref name="idB"/>) by registry plane
    /// <paramref name="cutter"/>. Keyed by the canonical (low id, high id, cutter) triple so the winding
    /// order does not matter; the first writer computes the on-edge lerp from the low-id endpoint and every
    /// later flanking face carrying the same edge + cutter reuses the byte-identical point and id.
    /// </summary>
    /// <param name="tA">Cut parameter measured from endpoint A (<c>da/(da-db)</c>).</param>
    public (int Id, Vec3 Pos) InternCut(int idA, int idB, int cutter, Vec3 posA, Vec3 posB, float tA)
    {
        int lo, hi;
        Vec3 loPos, hiPos;
        float tLo;
        if (idA <= idB)
        {
            lo = idA;
            hi = idB;
            loPos = posA;
            hiPos = posB;
            tLo = tA;
        }
        else
        {
            lo = idB;
            hi = idA;
            loPos = posB;
            hiPos = posA;
            tLo = 1f - tA;
        }

        var key = (lo, hi, cutter);
        if (_cuts.TryGetValue(key, out (int Id, Vec3 Pos) existing))
        {
            return existing;
        }

        (int Id, Vec3 Pos) result = (_next++, Vec3Math.Lerp(loPos, hiPos, tLo));
        _cuts[key] = result;
        return result;
    }

    private (int, int, int) Cell(Vec3 p) =>
        ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));
}
