using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Pure hit-testing for the UV Unwrap editor's selection: rubber-band box membership and
/// click picking of vertices / edges / faces over the editor's flat working set of UV
/// points plus its per-face corner rings (each ring is the vertex indices of one face, in
/// winding order). Coordinate-space agnostic — the window passes points already converted
/// to UV space and a UV-space pick radius — so every rule is unit-testable independent of
/// the window and its rendering.
/// </summary>
/// <remarks>
/// Selection is stored by the window as a set of vertex indices. These helpers translate a
/// gesture in the active selection mode into the vertex set it implies: a vertex maps to
/// itself, an edge to its two endpoints, a face to all its corners. That keeps every
/// downstream transform (move/rotate/scale/flip/align/snap) operating on one uniform vertex
/// set regardless of the mode the pick was made in, and lets the selection survive a mode
/// switch unchanged (an edge counts as selected when both endpoints are in the set; a face
/// when all its corners are).
/// </remarks>
public static class UvSelection
{
    // ---- Box (rubber-band) membership -----------------------------------------

    /// <summary>Indices of the UV points inside the axis-aligned UV rect (inclusive bounds).</summary>
    public static List<int> VerticesInRect(IReadOnlyList<Uv> uvs, float minU, float minV, float maxU, float maxV)
    {
        ArgumentNullException.ThrowIfNull(uvs);
        var hits = new List<int>();
        for (int i = 0; i < uvs.Count; i++)
        {
            Uv p = uvs[i];
            if (p.U >= minU && p.U <= maxU && p.V >= minV && p.V <= maxV)
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    /// <summary>
    /// Edge-mode box select: the distinct vertex indices of every edge whose BOTH endpoints
    /// lie in the rect — a partly-boxed edge is not caught, so endpoints move as whole edges.
    /// </summary>
    public static List<int> EdgeVerticesInRect(
        IReadOnlyList<Uv> uvs, IReadOnlyList<IReadOnlyList<int>> rings, float minU, float minV, float maxU, float maxV)
    {
        ArgumentNullException.ThrowIfNull(rings);
        var inRect = new HashSet<int>(VerticesInRect(uvs, minU, minV, maxU, maxV));
        var seen = new HashSet<int>();
        var hits = new List<int>();
        foreach (IReadOnlyList<int> ring in rings)
        {
            int n = ring.Count;
            if (n < 2)
            {
                continue;
            }

            for (int i = 0; i < n; i++)
            {
                int a = ring[i], b = ring[(i + 1) % n];
                if (inRect.Contains(a) && inRect.Contains(b))
                {
                    if (seen.Add(a))
                    {
                        hits.Add(a);
                    }

                    if (seen.Add(b))
                    {
                        hits.Add(b);
                    }
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Face-mode box select: the distinct vertex indices of every face whose ALL corners lie
    /// in the rect (a partly-boxed face is not caught, so faces select as whole islands).
    /// </summary>
    public static List<int> FaceVerticesInRect(
        IReadOnlyList<Uv> uvs, IReadOnlyList<IReadOnlyList<int>> rings, float minU, float minV, float maxU, float maxV)
    {
        ArgumentNullException.ThrowIfNull(rings);
        var inRect = new HashSet<int>(VerticesInRect(uvs, minU, minV, maxU, maxV));
        var seen = new HashSet<int>();
        var hits = new List<int>();
        foreach (IReadOnlyList<int> ring in rings)
        {
            if (ring.Count == 0)
            {
                continue;
            }

            bool all = true;
            foreach (int v in ring)
            {
                if (!inRect.Contains(v))
                {
                    all = false;
                    break;
                }
            }

            if (!all)
            {
                continue;
            }

            foreach (int v in ring)
            {
                if (seen.Add(v))
                {
                    hits.Add(v);
                }
            }
        }

        return hits;
    }

    // ---- Click picking --------------------------------------------------------

    /// <summary>Index of the nearest UV point to (u, v) within <paramref name="maxDist"/>; -1 if none.</summary>
    public static int NearestVertex(IReadOnlyList<Uv> uvs, float u, float v, float maxDist)
    {
        ArgumentNullException.ThrowIfNull(uvs);
        int best = -1;
        float bestSq = maxDist * maxDist;
        for (int i = 0; i < uvs.Count; i++)
        {
            float du = uvs[i].U - u, dv = uvs[i].V - v;
            float d = (du * du) + (dv * dv);
            if (d <= bestSq)
            {
                bestSq = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Endpoints (A, B) of the nearest edge to (u, v) within <paramref name="maxDist"/>;
    /// (-1, -1) if none. Distance is measured to the edge segment, not just its endpoints.
    /// </summary>
    public static (int A, int B) NearestEdge(
        IReadOnlyList<Uv> uvs, IReadOnlyList<IReadOnlyList<int>> rings, float u, float v, float maxDist)
    {
        ArgumentNullException.ThrowIfNull(uvs);
        ArgumentNullException.ThrowIfNull(rings);
        int bestA = -1, bestB = -1;
        float bestSq = maxDist * maxDist;
        foreach (IReadOnlyList<int> ring in rings)
        {
            int n = ring.Count;
            if (n < 2)
            {
                continue;
            }

            for (int i = 0; i < n; i++)
            {
                int a = ring[i], b = ring[(i + 1) % n];
                float d = PointSegmentDistSq(u, v, uvs[a], uvs[b]);
                if (d <= bestSq)
                {
                    bestSq = d;
                    bestA = a;
                    bestB = b;
                }
            }
        }

        return (bestA, bestB);
    }

    /// <summary>
    /// Index of the first face whose UV polygon contains (u, v); -1 if the point is inside no
    /// face. Ties (overlapping islands) resolve to the earliest ring.
    /// </summary>
    public static int FaceContainingPoint(
        IReadOnlyList<Uv> uvs, IReadOnlyList<IReadOnlyList<int>> rings, float u, float v)
    {
        ArgumentNullException.ThrowIfNull(uvs);
        ArgumentNullException.ThrowIfNull(rings);
        for (int f = 0; f < rings.Count; f++)
        {
            if (PointInPolygon(uvs, rings[f], u, v))
            {
                return f;
            }
        }

        return -1;
    }

    // ---- Geometry helpers -----------------------------------------------------

    private static float PointSegmentDistSq(float px, float py, Uv a, Uv b)
    {
        float abx = b.U - a.U, aby = b.V - a.V;
        float apx = px - a.U, apy = py - a.V;
        float denom = (abx * abx) + (aby * aby);
        float t = denom > 1e-12f ? ((apx * abx) + (apy * aby)) / denom : 0f;
        t = Math.Clamp(t, 0f, 1f);
        float cx = a.U + (t * abx), cy = a.V + (t * aby);
        float dx = px - cx, dy = py - cy;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>Even-odd ray-cast point-in-polygon over a ring of UV indices.</summary>
    private static bool PointInPolygon(IReadOnlyList<Uv> uvs, IReadOnlyList<int> ring, float px, float py)
    {
        int n = ring.Count;
        if (n < 3)
        {
            return false;
        }

        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Uv pi = uvs[ring[i]], pj = uvs[ring[j]];
            bool crosses = (pi.V > py) != (pj.V > py);
            if (crosses)
            {
                float x = ((pj.U - pi.U) * (py - pi.V) / (pj.V - pi.V)) + pi.U;
                if (px < x)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }
}
