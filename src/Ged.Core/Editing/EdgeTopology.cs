using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// An undirected brush edge: a canonical (low, high) pair of vertex-pool indices into
/// <see cref="Geometry.Vertices"/>. This matches the edge-dedup key used throughout the
/// renderer/snap code (<c>a &lt; c ? (a,c) : (c,a)</c>), so edges are genuinely shared
/// across faces (the vertex pool is welded by position).
/// </summary>
public readonly record struct BrushEdge(int V0, int V1)
{
    public static BrushEdge Canonical(int a, int b) => a <= b ? new BrushEdge(a, b) : new BrushEdge(b, a);

    public bool Degenerate => V0 == V1;

    public bool Contains(int v) => V0 == v || V1 == v;

    public int Other(int v) => v == V0 ? V1 : V0;
}

/// <summary>
/// Derives edge topology from a brush's <see cref="Geometry"/>: the unique
/// undirected edges (shared vertex pairs per face winding), the edge→incident-face adjacency,
/// and quad-based loop (edges joined end-to-end) / ring (parallel edges across quads) traversal.
/// Falls back gracefully on triangles / n-gons (returns just the seed edge where the topology
/// isn't a regular quad grid). Pure and fully unit-testable.
/// </summary>
public static class EdgeTopology
{
    /// <summary>The unique undirected edges of a brush, in first-seen order.</summary>
    public static IReadOnlyList<BrushEdge> Edges(Geometry g)
    {
        var seen = new HashSet<BrushEdge>();
        var list = new List<BrushEdge>();
        foreach (Face f in g.Faces)
        {
            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                BrushEdge e = BrushEdge.Canonical(f.Vertices[i].Index, f.Vertices[(i + 1) % n].Index);
                if (!e.Degenerate && seen.Add(e))
                {
                    list.Add(e);
                }
            }
        }

        return list;
    }

    /// <summary>edge → the (faceIndex, cornerIndex) slots that emit it (2 for a manifold edge).</summary>
    public static Dictionary<BrushEdge, List<(int Face, int Corner)>> Adjacency(Geometry g)
    {
        var adj = new Dictionary<BrushEdge, List<(int, int)>>();
        for (int fi = 0; fi < g.Faces.Count; fi++)
        {
            List<FaceVertex> vs = g.Faces[fi].Vertices;
            int n = vs.Count;
            for (int i = 0; i < n; i++)
            {
                BrushEdge e = BrushEdge.Canonical(vs[i].Index, vs[(i + 1) % n].Index);
                if (e.Degenerate)
                {
                    continue;
                }

                if (!adj.TryGetValue(e, out List<(int, int)>? l))
                {
                    adj[e] = l = new List<(int, int)>();
                }

                l.Add((fi, i));
            }
        }

        return adj;
    }

    /// <summary>vertex-pool index → the unique edges incident to it.</summary>
    public static Dictionary<int, List<BrushEdge>> IncidentEdges(Geometry g)
    {
        var map = new Dictionary<int, List<BrushEdge>>();
        foreach (BrushEdge e in Edges(g))
        {
            AddIncident(map, e.V0, e);
            AddIncident(map, e.V1, e);
        }

        return map;
    }

    private static void AddIncident(Dictionary<int, List<BrushEdge>> map, int v, BrushEdge e)
    {
        if (!map.TryGetValue(v, out List<BrushEdge>? l))
        {
            map[v] = l = new List<BrushEdge>();
        }

        l.Add(e);
    }

    /// <summary>
    /// The edge ring through <paramref name="start"/>: parallel edges connected across quads
    /// (the opposite edge in each quad, hopping to the quad on the far side). Includes the seed.
    /// </summary>
    public static IReadOnlyCollection<BrushEdge> Ring(Geometry g, BrushEdge start)
    {
        var adj = Adjacency(g);
        var result = new HashSet<BrushEdge> { start };
        if (!adj.TryGetValue(start, out List<(int Face, int Corner)>? faces))
        {
            return result;
        }

        foreach ((int startFace, int _) in faces.ToList())
        {
            BrushEdge cur = start;
            int face = startFace;
            while (true)
            {
                if (g.Faces[face].Vertices.Count != 4 || OppositeEdgeInQuad(g, face, cur) is not { } opp)
                {
                    break;
                }

                if (!result.Add(opp))
                {
                    break; // met the other direction / closed the ring
                }

                if (OtherFace(adj, opp, face) is not { } next)
                {
                    break; // boundary edge — ring ends
                }

                cur = opp;
                face = next;
            }
        }

        return result;
    }

    /// <summary>
    /// The edge loop through <paramref name="start"/>: edges joined end-to-end across regular
    /// (valence-4) grid vertices, continuing to the non-face-mate edge at each end. Includes the seed.
    /// </summary>
    public static IReadOnlyCollection<BrushEdge> Loop(Geometry g, BrushEdge start)
    {
        var adj = Adjacency(g);
        var incident = IncidentEdges(g);
        var result = new HashSet<BrushEdge> { start };
        foreach (int startV in new[] { start.V0, start.V1 })
        {
            BrushEdge cur = start;
            int v = startV;
            while (ContinuationEdge(g, adj, incident, cur, v) is { } next && result.Add(next))
            {
                v = next.Other(v);
                cur = next;
            }
        }

        return result;
    }

    private static BrushEdge? OppositeEdgeInQuad(Geometry g, int faceIndex, BrushEdge edge)
    {
        List<FaceVertex> vs = g.Faces[faceIndex].Vertices;
        if (vs.Count != 4)
        {
            return null;
        }

        for (int i = 0; i < 4; i++)
        {
            if (BrushEdge.Canonical(vs[i].Index, vs[(i + 1) % 4].Index) == edge)
            {
                return BrushEdge.Canonical(vs[(i + 2) % 4].Index, vs[(i + 3) % 4].Index);
            }
        }

        return null;
    }

    private static int? OtherFace(Dictionary<BrushEdge, List<(int Face, int Corner)>> adj, BrushEdge edge, int face)
    {
        if (!adj.TryGetValue(edge, out List<(int Face, int Corner)>? faces))
        {
            return null;
        }

        foreach ((int f, int _) in faces)
        {
            if (f != face)
            {
                return f;
            }
        }

        return null;
    }

    private static BrushEdge? ContinuationEdge(
        Geometry g, Dictionary<BrushEdge, List<(int Face, int Corner)>> adj,
        Dictionary<int, List<BrushEdge>> incident, BrushEdge edge, int v)
    {
        if (!adj.TryGetValue(edge, out List<(int Face, int Corner)>? faces) ||
            !incident.TryGetValue(v, out List<BrushEdge>? edgesAtV) || edgesAtV.Count != 4)
        {
            return null; // only continue through regular 4-valent grid vertices
        }

        var faceMates = new HashSet<BrushEdge>();
        foreach ((int fi, int _) in faces)
        {
            if (OtherEdgeAtVertexInFace(g, fi, edge, v) is { } mate)
            {
                faceMates.Add(mate);
            }
        }

        BrushEdge? continuation = null;
        int count = 0;
        foreach (BrushEdge e in edgesAtV)
        {
            if (e == edge || faceMates.Contains(e))
            {
                continue;
            }

            continuation = e;
            count++;
        }

        return count == 1 ? continuation : null;
    }

    private static BrushEdge? OtherEdgeAtVertexInFace(Geometry g, int faceIndex, BrushEdge edge, int v)
    {
        List<FaceVertex> vs = g.Faces[faceIndex].Vertices;
        int n = vs.Count;
        for (int i = 0; i < n; i++)
        {
            if (vs[i].Index != v)
            {
                continue;
            }

            BrushEdge prev = BrushEdge.Canonical(vs[(i - 1 + n) % n].Index, v);
            BrushEdge next = BrushEdge.Canonical(v, vs[(i + 1) % n].Index);
            if (prev == edge)
            {
                return next;
            }

            if (next == edge)
            {
                return prev;
            }
        }

        return null;
    }
}
