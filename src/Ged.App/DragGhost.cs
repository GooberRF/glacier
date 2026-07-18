using System.Collections.Generic;
using System.Numerics;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Rfg;
using Ged.Core.Model;
using Ged.Rendering;
using Ged.Rendering.Scene;

namespace Ged.App;

/// <summary>A drag ghost's local-space wireframe plus the asset's local bounds (for placement alignment).</summary>
internal readonly record struct GhostGeometry(IReadOnlyList<LineSegment> Lines, Vector3 Min, Vector3 Max, bool HasBounds);

/// <summary>
/// Builds the translucent wireframe GHOST shown in a viewport while an asset drag hovers it (item E,
/// feature 2) and the local bounds that drive placement alignment (round-3 item 2). The line set is
/// built ONCE at drag start in the asset's LOCAL space (origin = the object's frame); the drop handler
/// applies the SAME placement offset (<see cref="PlacementOffset"/>) to both the ghost translation and
/// the final drop point, so the ghost never lies. A hard segment budget keeps a dense mesh cheap: over
/// budget, the wireframe collapses to a bounds box (the bounds themselves are unchanged).
/// </summary>
internal static class DragGhost
{
    /// <summary>Max wireframe segments before falling back to a bounds box.</summary>
    public const int MaxSegments = 10000;

    /// <summary>The ghost tint (distinct translucent cyan — not the yellow selection / gizmo colours).</summary>
    public static readonly uint Tint = Palette.Rgba(120, 220, 255, 200);

    /// <summary>The face-under-cursor highlight tint for a texture drag (distinct orange).</summary>
    public static readonly uint FaceTint = Palette.Rgba(255, 150, 40, 255);

    /// <summary>The blocked (locked-brush) highlight tint for a texture drag (red).</summary>
    public static readonly uint BlockedTint = Palette.Rgba(230, 70, 70, 255);

    /// <summary>
    /// The offset added to the surface hit point to POSITION the asset (used identically by the live
    /// ghost and the final drop). On a surface everything bottom-aligns — the bbox bottom-centre rests
    /// on the hit point — except a pickup, whose bbox CENTRE sits 1&#160;m above it. Off a surface (the
    /// in-front-of-camera fallback, or no bounds) it is zero (centre placement — nothing to rest on).
    /// </summary>
    public static Vector3 PlacementOffset(Vector3 min, Vector3 max, bool onSurface, bool pickup)
    {
        if (!onSurface)
        {
            return Vector3.Zero;
        }

        Vector3 center = (min + max) * 0.5f;
        return pickup
            ? new Vector3(-center.X, 1.0f - center.Y, -center.Z)  // bbox centre 1 m above the surface point
            : new Vector3(-center.X, -min.Y, -center.Z);          // bbox bottom-centre rests on the point
    }

    /// <summary>Mesh LOD0 unique-edge wireframe (local space) + bounds; a unit/bounds box when absent or over budget.</summary>
    public static GhostGeometry MeshWireframe(V3dFile? mesh, uint color)
    {
        if (mesh is null)
        {
            return UnitBox(color);
        }

        var lines = new List<LineSegment>();
        var b = default(Bounds);

        foreach (V3dSubmesh sm in mesh.Submeshes)
        {
            if (sm.Lods.Count == 0)
            {
                continue;
            }

            foreach (V3dBatch batch in sm.Lods[0].Batches)
            {
                var seen = new HashSet<(int, int)>();
                void Edge(int ia, int ib)
                {
                    if (ia < 0 || ib < 0 || ia >= batch.Positions.Length || ib >= batch.Positions.Length)
                    {
                        return;
                    }

                    (int, int) key = ia < ib ? (ia, ib) : (ib, ia);
                    if (!seen.Add(key))
                    {
                        return;
                    }

                    Vector3 a = V(batch.Positions[ia]);
                    Vector3 c = V(batch.Positions[ib]);
                    b.Add(a);
                    b.Add(c);
                    lines.Add(new LineSegment(a, c, color));
                }

                foreach (V3dTriangle t in batch.Triangles)
                {
                    Edge(t.I0, t.I1);
                    Edge(t.I1, t.I2);
                    Edge(t.I2, t.I0);
                }
            }
        }

        if (lines.Count == 0)
        {
            return UnitBox(color);
        }

        return lines.Count > MaxSegments
            ? new GhostGeometry(BoxLines(b.Min, b.Max, color), b.Min, b.Max, true)
            : new GhostGeometry(lines, b.Min, b.Max, true);
    }

    /// <summary>Prefab payload wireframe: member brush edges + a small box at each object member, with bounds.</summary>
    public static GhostGeometry PrefabWireframe(RfgFile payload, uint color)
    {
        var lines = new List<LineSegment>();
        var b = default(Bounds);

        foreach (RfgGroup g in payload.Groups)
        {
            foreach (Brush br in g.Brushes.Brushes)
            {
                foreach ((Vec3 pa, Vec3 pb) in BrushEdges(br))
                {
                    Vector3 a = V(pa);
                    Vector3 c = V(pb);
                    b.Add(a);
                    b.Add(c);
                    lines.Add(new LineSegment(a, c, color));
                }
            }

            foreach (Vec3 pos in ObjectPositions(g))
            {
                var center = V(pos);
                b.Add(center);
                AddBox(lines, center, 0.4f, color);
            }
        }

        if (lines.Count == 0)
        {
            return UnitBox(color);
        }

        return lines.Count > MaxSegments
            ? new GhostGeometry(BoxLines(b.Min, b.Max, color), b.Min, b.Max, true)
            : new GhostGeometry(lines, b.Min, b.Max, true);
    }

    /// <summary>A small unit box (the fallback ghost for a class with no resolvable mesh).</summary>
    public static GhostGeometry UnitBox(uint color)
    {
        var min = new Vector3(-0.75f);
        var max = new Vector3(0.75f);
        return new GhostGeometry(BoxLines(min, max, color), min, max, true);
    }

    /// <summary>Translates a local-space ghost line set to a world placement point.</summary>
    public static IReadOnlyList<LineSegment> Translate(IReadOnlyList<LineSegment> local, Vector3 to)
    {
        var lines = new List<LineSegment>(local.Count);
        foreach (LineSegment l in local)
        {
            lines.Add(new LineSegment(l.A + to, l.B + to, l.Color));
        }

        return lines;
    }

    private static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);

    private static IEnumerable<(Vec3 A, Vec3 B)> BrushEdges(Brush b)
    {
        var seen = new HashSet<(int, int)>();
        foreach (Face f in b.Geometry.Faces)
        {
            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                int ia = f.Vertices[i].Index;
                int ib = f.Vertices[(i + 1) % n].Index;
                (int, int) key = ia < ib ? (ia, ib) : (ib, ia);
                if (!seen.Add(key) || ia < 0 || ib < 0 || ia >= b.Geometry.Vertices.Count || ib >= b.Geometry.Vertices.Count)
                {
                    continue;
                }

                Vec3 wa = b.Position.Add(b.Rotation.Transform(b.Geometry.Vertices[ia]));
                Vec3 wb = b.Position.Add(b.Rotation.Transform(b.Geometry.Vertices[ib]));
                yield return (wa, wb);
            }
        }
    }

    private static IEnumerable<Vec3> ObjectPositions(RfgGroup g)
    {
        foreach (Light l in g.Lights.Lights)
        {
            yield return l.Position;
        }

        foreach (Entity e in g.Entities.Entities)
        {
            yield return e.Position;
        }

        foreach (Item it in g.Items.Items)
        {
            yield return it.Header.Position;
        }

        foreach (Clutter c in g.Clutters.Clutters)
        {
            yield return c.Header.Position;
        }

        foreach (RflEvent ev in g.Events.Events)
        {
            yield return ev.Position;
        }

        foreach (MpRespawnPoint r in g.MpRespawnPoints.Points)
        {
            yield return r.Position;
        }
    }

    private static void AddBox(List<LineSegment> lines, Vector3 c, float half, uint color)
    {
        var min = c - new Vector3(half);
        var max = c + new Vector3(half);
        lines.AddRange(BoxLines(min, max, color));
    }

    private static IReadOnlyList<LineSegment> BoxLines(Vector3 min, Vector3 max, uint color)
    {
        Vector3[] v =
        {
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z),
        };
        int[,] edges =
        {
            { 0, 1 }, { 0, 2 }, { 1, 3 }, { 2, 3 }, { 4, 5 }, { 4, 6 },
            { 5, 7 }, { 6, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 },
        };
        var lines = new List<LineSegment>(12);
        for (int e = 0; e < 12; e++)
        {
            lines.Add(new LineSegment(v[edges[e, 0]], v[edges[e, 1]], color));
        }

        return lines;
    }

    /// <summary>Accumulates an axis-aligned bounds over added points.</summary>
    private struct Bounds
    {
        private bool _any;

        public Vector3 Min { get; private set; }

        public Vector3 Max { get; private set; }

        public void Add(Vector3 p)
        {
            (Min, Max) = _any ? (Vector3.Min(Min, p), Vector3.Max(Max, p)) : (p, p);
            _any = true;
        }
    }
}
