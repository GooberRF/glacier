using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// RED's per-output-face vertex cleanup (BuildFinalRenderSolid <c>FUN_00496150</c> →
/// <c>FUN_0048acd0</c>/<c>FUN_0048cb10</c> duplicate-vertex skip + <c>Face_BuildWinding</c>
/// <c>FUN_00496120</c> winding rebuild). RED rebuilds every output face's winding and drops
/// vertices that repeat or are redundant along a straight edge; GED's default output path had no
/// equivalent, so authored/near-coincident duplicate corners and redundant collinear runs survive
/// verbatim into the compiled faces.
/// <para>
/// This choked the in-game geomod cap triangulator: Alpine Faction's <c>ear_clip_triangulate</c>
/// (game_patch/misc/destruction.cpp) stalls on a loop with a repeated vertex (a self-touching pinch)
/// and logs "[CapFace] Ear clip stuck: remaining=N of M" — exactly Goober's report. Measured on
/// dmabrupt: RED's geoable/breakable faces ear-clip cleanly (0 stuck, 0 repeated) while GED's had 32
/// stuck, every one carrying a repeated vertex.
/// </para>
/// <para>
/// SCOPE: detail (geoable/breakable) faces only — the surfaces geomod destruction actually caps, and
/// the set where RED's output is provably clean. World faces are left untouched because RED itself
/// leaves some self-touching world faces (its dedup is vertex-identity, not position), so cleaning them
/// would DIVERGE from RED's pixels.
/// </para>
/// <para>
/// WATERTIGHT BY CONSTRUCTION: a collinear vertex is removed only when it is <b>not a genuine corner on
/// any face</b> — i.e. it is a mid-edge subdivision point on every face that references it. Removing such
/// a point from all of them keeps every shared edge matched (both flanks lose the same station), so no
/// neighbour's T-junction seam reopens. A load-bearing corner (a real turn on some face) is always kept.
/// Repeated pool indices within one face are always degenerate (the same welded vertex twice) and are
/// dropped unconditionally.
/// </para>
/// </summary>
internal static class OutputFaceCleanup
{
    /// <summary>RED's uniform weld / collinear epsilon (0.1 mm) — the tie band its whole pipeline uses.</summary>
    private const float Weld = 1e-4f;

    /// <summary>
    /// Cleans repeated + non-load-bearing-collinear vertices from every detail face, rewriting
    /// <paramref name="faces"/> and <paramref name="poolIndices"/> (parallel) in place. Faces that collapse
    /// below three vertices are dropped (RED frees them). Returns the number of faces removed.
    /// </summary>
    public static int Clean(List<CsgFace> faces, List<int[]> poolIndices, List<Vec3> pool)
    {
        // Load-bearing corners: any pool index that is a genuine (non-collinear) turn on SOME face. These
        // are never removed, so a collinear T-junction point a neighbour actually corners at stays put.
        HashSet<int> corners = BuildCornerSet(faces, poolIndices);

        var outF = new List<CsgFace>(faces.Count);
        var outI = new List<int[]>(faces.Count);
        int removed = 0;

        for (int fi = 0; fi < faces.Count; fi++)
        {
            CsgFace f = faces[fi];
            int[] idx = poolIndices[fi];
            if (!IsDetailWall(f) || idx.Length < 3 || f.Vertices.Count != idx.Length)
            {
                outF.Add(f);
                outI.Add(idx);
                continue;
            }

            if (CleanFace(f, idx, pool, corners, out List<CsgVertex> verts, out List<int> newIdx))
            {
                if (newIdx.Count >= 3)
                {
                    CsgFace nf = f.CloneAttributes();
                    nf.Vertices = verts;
                    nf.LightmapUvs = null; // recomputed by the surface stage after cleanup
                    outF.Add(nf);
                    outI.Add(newIdx.ToArray());
                }
                else
                {
                    removed++; // collapsed to a sliver — drop, exactly as RED frees < 3-vertex faces
                }
            }
            else
            {
                outF.Add(f);
                outI.Add(idx);
            }
        }

        faces.Clear();
        faces.AddRange(outF);
        poolIndices.Clear();
        poolIndices.AddRange(outI);
        return removed;
    }

    /// <summary>A textured, non-portal detail (geoable/breakable) wall — geomod's cap target.</summary>
    private static bool IsDetailWall(CsgFace f) =>
        !f.IsPortal
        && f.PortalIndexPlus2 < 2
        && !string.IsNullOrEmpty(f.Texture)
        && (f.Flags & (ushort)FaceFlags.IsDetail) != 0
        && (f.Flags & (ushort)FaceFlags.LiquidSurface) == 0;

    /// <summary>Pool indices that form a real corner (turn beyond the weld band) on at least one face.</summary>
    private static HashSet<int> BuildCornerSet(List<CsgFace> faces, List<int[]> poolIndices)
    {
        var corners = new HashSet<int>();
        for (int fi = 0; fi < faces.Count; fi++)
        {
            CsgFace f = faces[fi];
            List<CsgVertex> v = f.Vertices;
            int[] idx = poolIndices[fi];
            int n = v.Count;
            if (n < 3 || idx.Length != n)
            {
                continue;
            }

            for (int i = 0; i < n; i++)
            {
                Vec3 prev = v[((i - 1) + n) % n].Position;
                Vec3 cur = v[i].Position;
                Vec3 next = v[(i + 1) % n].Position;
                if (!IsCollinear(prev, cur, next))
                {
                    corners.Add(idx[i]);
                }
            }
        }

        return corners;
    }

    /// <summary>
    /// Removes repeated pool indices (keep first) then iteratively removes non-load-bearing collinear
    /// vertices. Returns true if anything changed, with the cleaned parallel loop in
    /// <paramref name="verts"/>/<paramref name="newIdx"/>.
    /// </summary>
    private static bool CleanFace(
        CsgFace f, int[] idx, List<Vec3> pool, HashSet<int> corners,
        out List<CsgVertex> verts, out List<int> newIdx)
    {
        int n = f.Vertices.Count;
        verts = new List<CsgVertex>(n);
        newIdx = new List<int>(n);
        var seen = new HashSet<int>(n);
        bool changed = false;

        // Pass 1 — drop repeated pool indices (a welded vertex used twice = a self-touching pinch).
        for (int i = 0; i < n; i++)
        {
            if (seen.Add(idx[i]))
            {
                verts.Add(f.Vertices[i]);
                newIdx.Add(idx[i]);
            }
            else
            {
                changed = true;
            }
        }

        // Pass 2 — drop redundant collinear vertices that no face corners at (iterate to a fixpoint so a
        // run of collinear points collapses fully). Never drop a load-bearing corner index.
        bool again = true;
        while (again && newIdx.Count > 3)
        {
            again = false;
            int m = newIdx.Count;
            for (int i = 0; i < m; i++)
            {
                if (corners.Contains(newIdx[i]))
                {
                    continue; // load-bearing — a neighbour corners here
                }

                Vec3 prev = verts[((i - 1) + m) % m].Position;
                Vec3 cur = verts[i].Position;
                Vec3 next = verts[(i + 1) % m].Position;
                if (IsCollinear(prev, cur, next))
                {
                    verts.RemoveAt(i);
                    newIdx.RemoveAt(i);
                    changed = true;
                    again = true;
                    break;
                }
            }
        }

        return changed;
    }

    /// <summary>True when <paramref name="cur"/> lies within the weld band of the segment
    /// <paramref name="prev"/>→<paramref name="next"/> (a redundant, on-edge vertex).</summary>
    private static bool IsCollinear(Vec3 prev, Vec3 cur, Vec3 next)
    {
        Vec3 ab = next.Sub(prev);
        float abLen = ab.Length();
        if (abLen < 1e-6f)
        {
            return false; // neighbours coincide — leave it to the repeat pass / degenerate drop
        }

        // Perpendicular distance from cur to the prev→next line = |ab × (cur-prev)| / |ab|.
        float perp = ab.Cross(cur.Sub(prev)).Length() / abLen;
        return perp < Weld;
    }
}
