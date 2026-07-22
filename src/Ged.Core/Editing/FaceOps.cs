using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Face-mode operators. Each mutates a brush <see cref="Geometry"/> given the
/// selected face index/indices and returns an <see cref="OpResult"/> carrying the
/// stock error wording on rejection, so the model is never corrupted. Pure; the
/// App wraps them in undo commands and shows failures as toasts.
/// </summary>
public static class FaceOps
{
    private const float CoplanarEps = 1e-3f;

    /// <summary>Ctrl+E: extrudes a face along its outward normal, walling the sides.</summary>
    public static OpResult Extrude(Geometry g, int faceIndex, float distance)
    {
        if (!InRange(g, faceIndex))
        {
            return OpResult.Fail("Select a face to extrude.");
        }

        if (MathF.Abs(distance) < 1e-4f)
        {
            return OpResult.Fail("Extrude distance is zero.");
        }

        Face f = g.Faces[faceIndex];
        List<Vec3> corners = GeometryUtil.Corners(g, f);
        Vec3 normal = f.Plane.Normal;
        Vec3 faceCentroid = GeometryUtil.Centroid(corners);

        int n = f.Vertices.Count;
        var oldIdx = f.Vertices.Select(v => v.Index).ToArray();
        var newIdx = new int[n];
        for (int i = 0; i < n; i++)
        {
            newIdx[i] = GeometryUtil.AddVertex(g, corners[i].Add(normal.Scale(distance)));
        }

        // Move the cap out to the new ring.
        for (int i = 0; i < n; i++)
        {
            f.Vertices[i].Index = newIdx[i];
        }

        // Side walls, oriented outward from the extruded column.
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var wall = new Face { Texture = f.Texture, SurfaceIndex = -1, Flags = f.Flags, RoomIndex = -1, FaceId = GeometryUtil.NextFaceId(g) };
            AddCorners(wall, oldIdx[i], oldIdx[j], newIdx[j], newIdx[i]);
            g.Faces.Add(wall);
            OrientWallOutward(g, wall, faceCentroid, normal);
        }

        GeometryUtil.RecomputeAllPlanes(g);
        GeometryUtil.AssignPlanarUv(g, f);
        return GeometryUtil.Validate(g) ? OpResult.Ok("Extrude") : OpResult.Fail("Extrude produced degenerate geometry.");
    }

    /// <summary>Bevels (insets) a face inward toward its centroid, connecting a ring of walls.</summary>
    public static OpResult Bevel(Geometry g, int faceIndex, float amount)
    {
        if (!InRange(g, faceIndex))
        {
            return OpResult.Fail("You must select a face to bevel.");
        }

        Face f = g.Faces[faceIndex];
        List<Vec3> corners = GeometryUtil.Corners(g, f);
        Vec3 c = GeometryUtil.Centroid(corners);
        int n = f.Vertices.Count;
        var oldIdx = f.Vertices.Select(v => v.Index).ToArray();
        var insetIdx = new int[n];
        for (int i = 0; i < n; i++)
        {
            Vec3 inset = Vec3Math.Lerp(corners[i], c, Math.Clamp(amount, 0f, 0.99f));
            insetIdx[i] = GeometryUtil.AddVertex(g, inset);
        }

        for (int i = 0; i < n; i++)
        {
            f.Vertices[i].Index = insetIdx[i];
        }

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var ring = new Face { Texture = f.Texture, SurfaceIndex = -1, Flags = f.Flags, RoomIndex = -1, FaceId = GeometryUtil.NextFaceId(g) };
            AddCorners(ring, oldIdx[i], oldIdx[j], insetIdx[j], insetIdx[i]);
            g.Faces.Add(ring);
        }

        GeometryUtil.RecomputeAllPlanes(g);
        return GeometryUtil.Validate(g) ? OpResult.Ok("Bevel") : OpResult.Fail("Bevel produced degenerate geometry.");
    }

    /// <summary>[ALPINE] Reverses a face's winding and its plane normal.</summary>
    public static OpResult FlipNormal(Geometry g, int faceIndex)
    {
        if (!InRange(g, faceIndex))
        {
            return OpResult.Fail("Select a face to flip.");
        }

        g.Faces[faceIndex].Vertices.Reverse();
        GeometryUtil.RecomputePlane(g, g.Faces[faceIndex]);
        return OpResult.Ok("Flip normal");
    }

    /// <summary>Fan-triangulates a polygon face into triangles.</summary>
    public static OpResult Triangulate(Geometry g, int faceIndex)
    {
        if (!InRange(g, faceIndex))
        {
            return OpResult.Fail("Select a face to triangulate.");
        }

        Face f = g.Faces[faceIndex];
        if (f.Vertices.Count <= 3)
        {
            return OpResult.Ok("Face is already a triangle.");
        }

        List<FaceVertex> verts = f.Vertices;
        g.Faces.RemoveAt(faceIndex);
        for (int i = 1; i < verts.Count - 1; i++)
        {
            var tri = NewFaceLike(f);
            tri.Vertices.Add(Copy(verts[0]));
            tri.Vertices.Add(Copy(verts[i]));
            tri.Vertices.Add(Copy(verts[i + 1]));
            g.Faces.Add(tri);
        }

        GeometryUtil.RecomputeAllPlanes(g);
        return OpResult.Ok("Triangulate");
    }

    /// <summary>Triangulates a face around an added centre vertex (a pinwheel fan).</summary>
    public static OpResult Pinwheel(Geometry g, int faceIndex)
    {
        if (!InRange(g, faceIndex))
        {
            return OpResult.Fail("Select a face for the pinwheel.");
        }

        Face f = g.Faces[faceIndex];
        List<Vec3> corners = GeometryUtil.Corners(g, f);
        int centre = GeometryUtil.AddVertex(g, GeometryUtil.Centroid(corners));
        var verts = f.Vertices.ToList();
        g.Faces.RemoveAt(faceIndex);
        for (int i = 0; i < verts.Count; i++)
        {
            int j = (i + 1) % verts.Count;
            var tri = NewFaceLike(f);
            tri.Vertices.Add(new FaceVertex { Index = centre });
            tri.Vertices.Add(Copy(verts[i]));
            tri.Vertices.Add(Copy(verts[j]));
            g.Faces.Add(tri);
        }

        GeometryUtil.RecomputeAllPlanes(g);
        return OpResult.Ok("Pinwheel");
    }

    /// <summary>Collapses a face to a single vertex at its centroid (removing the face).</summary>
    public static OpResult Collapse(Geometry g, int faceIndex)
    {
        if (!InRange(g, faceIndex))
        {
            return OpResult.Fail("Select a face to collapse.");
        }

        Face f = g.Faces[faceIndex];
        List<Vec3> corners = GeometryUtil.Corners(g, f);
        int centre = GeometryUtil.AddVertex(g, GeometryUtil.Centroid(corners));
        var members = f.Vertices.Select(v => v.Index).ToHashSet();
        foreach (Face other in g.Faces)
        {
            foreach (FaceVertex fv in other.Vertices)
            {
                if (members.Contains(fv.Index))
                {
                    fv.Index = centre;
                }
            }
        }

        GeometryUtil.CleanupFaces(g);

        // Remapping the face's corners to one centroid vertex pulls those corners in every
        // neighbouring face too, which can bend them off-plane — triangulate any that bent so brush
        // faces stay flat (RED parity; see FacePlanarizer). Planarize refreshes every face plane, so
        // it subsumes the RecomputeAllPlanes this used to end with.
        int tri = FacePlanarizer.Planarize(g);
        return OpResult.Ok("Collapse") with { FacesTriangulated = tri };
    }

    /// <summary>[ALPINE] Deletes faces (leaving the vertices for neighbours).</summary>
    public static OpResult Delete(Geometry g, IReadOnlyCollection<int> faceIndices)
    {
        if (faceIndices.Count == 0)
        {
            return OpResult.Fail("Select at least one face to delete.");
        }

        if (faceIndices.Count >= g.Faces.Count)
        {
            return OpResult.Fail("Cannot delete every face of a brush.");
        }

        var set = new HashSet<int>(faceIndices);
        g.Faces = g.Faces.Where((_, i) => !set.Contains(i)).ToList();
        return OpResult.Ok("Delete faces");
    }

    /// <summary>[ALPINE] Deletes faces and any vertices they leave unreferenced.</summary>
    public static OpResult DeleteExt(Geometry g, IReadOnlyCollection<int> faceIndices)
    {
        OpResult r = Delete(g, faceIndices);
        if (r)
        {
            GeometryUtil.CompactUnusedVertices(g);
        }

        return r ? OpResult.Ok("Delete faces + vertices") : r;
    }

    /// <summary>
    /// [ALPINE] Splits an arbitrary authored face polygon (any n &gt;= 3, not just
    /// quads) into <paramref name="pieces"/> strips by a family of parallel cut
    /// planes running perpendicular to an in-plane split axis: world-X-derived when
    /// <paramref name="alongU"/> (the "U" radio), world-Y-derived otherwise. Each cut
    /// clips the running polygon in two, interpolating texture and lightmap UVs at the
    /// crossings; every child keeps the original texture, smoothing groups, flags,
    /// surface/portal binding and room, with a fresh face id. Mirrors Alpine
    /// editor_patch/geometry.cpp:566-711 (split_face) + :475-563 (create_split_face).
    /// </summary>
    public static OpResult NWaySplit(Geometry g, int faceIndex, int pieces, bool alongU)
    {
        if (!InRange(g, faceIndex))
        {
            return OpResult.Fail("Select a face to split.");
        }

        Face f = g.Faces[faceIndex];
        if (f.Vertices.Count < 3)
        {
            return OpResult.Fail("N-way split needs a face with at least three sides.");
        }

        int numSplits = Math.Max(2, pieces) - 1; // pieces resulting faces == numSplits cuts + 1
        List<Face>? children = SplitFace(g, f, numSplits, alongU);
        if (children is null || children.Count == 0)
        {
            return OpResult.Fail("N-way split could not divide the face.");
        }

        int nextId = GeometryUtil.NextFaceId(g);
        foreach (Face child in children)
        {
            child.FaceId = nextId++;
        }

        g.Faces.RemoveAt(faceIndex);
        g.Faces.AddRange(children);
        GeometryUtil.RecomputeAllPlanes(g);
        return OpResult.Ok("N-way split");
    }

    /// <summary>
    /// Simple mesh-smooth: subdivides each selected quad face into four. The result is
    /// meant to light smoothly, so faces without a smoothing group are put into group 1
    /// before subdividing (the children inherit it via <see cref="NewFaceLike"/>) —
    /// keeping the op consistent with the baker, which only interpolates vertex normals
    /// across faces whose smoothing-group masks overlap (surface should_smooth).
    /// </summary>
    public static OpResult MeshSmooth(Geometry g, IReadOnlyCollection<int> faceIndices)
    {
        if (faceIndices.Count == 0)
        {
            return OpResult.Fail("Select faces to smooth.");
        }

        foreach (int idx in faceIndices.OrderByDescending(i => i))
        {
            if (!InRange(g, idx) || g.Faces[idx].Vertices.Count != 4)
            {
                continue;
            }

            if (g.Faces[idx].SmoothingGroups == 0)
            {
                g.Faces[idx].SmoothingGroups = 1u;
            }

            NWaySplitQuadToGrid(g, idx);
        }

        // Subdividing a planar quad yields planar cells by construction (bilinear points of a flat
        // quad stay coplanar); a bent source quad, however, produces bent cells — so run the shared
        // planarity guard to triangulate any cell left off-plane (RED parity; see FacePlanarizer).
        int tri = FacePlanarizer.Planarize(g);
        return OpResult.Ok("Mesh smooth") with { FacesTriangulated = tri };
    }

    /// <summary>
    /// Combines two coplanar faces that share exactly one edge into a single
    /// polygon. Reproduces stock RED's validation wording exactly.
    /// </summary>
    public static OpResult Combine(Geometry g, IReadOnlyList<int> faceIndices)
    {
        if (faceIndices.Count != 2)
        {
            return OpResult.Fail("Must select exactly two faces.");
        }

        if (!InRange(g, faceIndices[0]) || !InRange(g, faceIndices[1]))
        {
            return OpResult.Fail("Must select exactly two faces.");
        }

        Face a = g.Faces[faceIndices[0]];
        Face b = g.Faces[faceIndices[1]];

        if (!a.Plane.Normal.ApproxEquals(b.Plane.Normal, 1e-2f) ||
            MathF.Abs(a.Plane.Offset - b.Plane.Offset) > CoplanarEps)
        {
            return OpResult.Fail("Faces aren't coplanar.");
        }

        var shared = a.Vertices.Select(v => v.Index).Intersect(b.Vertices.Select(v => v.Index)).ToList();
        if (shared.Count != 2)
        {
            return OpResult.Fail("Faces must share exactly two vertices.");
        }

        List<int> merged = MergeAlongSharedEdge(a, b, shared[0], shared[1]);
        if (merged.Count < 3)
        {
            return OpResult.Fail("Faces would form a concave polygon.");
        }

        if (!IsConvex(g, merged, a.Plane.Normal))
        {
            return OpResult.Fail("Faces would form a concave polygon.");
        }

        var combined = NewFaceLike(a);
        foreach (int vi in merged)
        {
            combined.Vertices.Add(new FaceVertex { Index = vi });
        }

        int hi = Math.Max(faceIndices[0], faceIndices[1]);
        int lo = Math.Min(faceIndices[0], faceIndices[1]);
        g.Faces.RemoveAt(hi);
        g.Faces.RemoveAt(lo);
        g.Faces.Add(combined);
        GeometryUtil.RecomputePlane(g, combined);
        GeometryUtil.AssignPlanarUv(g, combined);
        return OpResult.Ok("Combine");
    }

    /// <summary>Flips the shared edge of two adjacent triangles (the classic diagonal flip).</summary>
    public static OpResult FlipEdge(Geometry g, IReadOnlyList<int> faceIndices)
    {
        if (faceIndices.Count != 2 || !InRange(g, faceIndices[0]) || !InRange(g, faceIndices[1]))
        {
            return OpResult.Fail("Select exactly two triangles sharing an edge.");
        }

        Face a = g.Faces[faceIndices[0]];
        Face b = g.Faces[faceIndices[1]];
        if (a.Vertices.Count != 3 || b.Vertices.Count != 3)
        {
            return OpResult.Fail("Flip edge needs two triangles.");
        }

        var aSet = a.Vertices.Select(v => v.Index).ToList();
        var bSet = b.Vertices.Select(v => v.Index).ToList();
        var shared = aSet.Intersect(bSet).ToList();
        if (shared.Count != 2)
        {
            return OpResult.Fail("Triangles must share exactly two vertices.");
        }

        int apex = aSet.First(i => !shared.Contains(i));
        int other = bSet.First(i => !shared.Contains(i));

        a.Vertices = new List<FaceVertex>
        {
            new() { Index = apex }, new() { Index = shared[0] }, new() { Index = other },
        };
        b.Vertices = new List<FaceVertex>
        {
            new() { Index = apex }, new() { Index = other }, new() { Index = shared[1] },
        };
        GeometryUtil.RecomputePlane(g, a);
        GeometryUtil.RecomputePlane(g, b);
        return OpResult.Ok("Flip edge");
    }

    /// <summary>Turns selected faces into portal faces (texture index -1).</summary>
    public static OpResult MakePortal(Geometry g, IReadOnlyCollection<int> faceIndices)
    {
        if (faceIndices.Count == 0)
        {
            return OpResult.Fail("Select faces to make a portal.");
        }

        foreach (int i in faceIndices)
        {
            if (InRange(g, i))
            {
                g.Faces[i].Texture = -1;
            }
        }

        return OpResult.Ok("Make portal");
    }

    // ---- helpers --------------------------------------------------------------

    private static bool InRange(Geometry g, int i) => i >= 0 && i < g.Faces.Count;

    private static int Add(Geometry g, Vec3 v) => GeometryUtil.AddVertex(g, v);

    private static FaceVertex Copy(FaceVertex fv) =>
        new() { Index = fv.Index, TextureCoords = fv.TextureCoords, LightmapCoords = fv.LightmapCoords };

    private static Face NewFaceLike(Face f) =>
        new() { Texture = f.Texture, SurfaceIndex = -1, Flags = f.Flags, SmoothingGroups = f.SmoothingGroups, RoomIndex = -1, FaceId = f.FaceId };

    private static void AddCorners(Face face, params int[] indices)
    {
        foreach (int i in indices)
        {
            face.Vertices.Add(new FaceVertex { Index = i });
        }
    }

    private static void OrientWallOutward(Geometry g, Face wall, Vec3 columnCentroid, Vec3 columnAxis)
    {
        Vec3 wc = GeometryUtil.Centroid(GeometryUtil.Corners(g, wall));
        // Outward reference: away from the column centre line, perpendicular to the extrude axis.
        Vec3 radial = wc.Sub(columnCentroid);
        radial = radial.Sub(columnAxis.Scale(radial.Dot(columnAxis)));
        GeometryUtil.RecomputePlane(g, wall);
        if (wall.Plane.Normal.Dot(radial) < 0f)
        {
            wall.Vertices.Reverse();
            GeometryUtil.RecomputePlane(g, wall);
        }
    }

    private static void NWaySplitQuadToGrid(Geometry g, int faceIndex)
    {
        Face f = g.Faces[faceIndex];
        Vec3 p0 = g.Vertices[f.Vertices[0].Index];
        Vec3 p1 = g.Vertices[f.Vertices[1].Index];
        Vec3 p2 = g.Vertices[f.Vertices[2].Index];
        Vec3 p3 = g.Vertices[f.Vertices[3].Index];
        Vec3 Bilerp(float u, float v) => Vec3Math.Lerp(Vec3Math.Lerp(p0, p1, u), Vec3Math.Lerp(p3, p2, u), v);

        g.Faces.RemoveAt(faceIndex);
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                float u0 = i * 0.5f, u1 = u0 + 0.5f, v0 = j * 0.5f, v1 = v0 + 0.5f;
                var cell = NewFaceLike(f);
                AddCorners(cell,
                    Add(g, Bilerp(u0, v0)), Add(g, Bilerp(u1, v0)),
                    Add(g, Bilerp(u1, v1)), Add(g, Bilerp(u0, v1)));
                g.Faces.Add(cell);
            }
        }
    }

    // ---- N-way split (arbitrary polygon, Alpine split_face) -------------------

    /// <summary>
    /// One vertex of a polygon being clipped: its position, texture/lightmap UVs, its
    /// projection onto the split axis, and its source pool index (&gt;= 0) or -1 when it
    /// is a freshly interpolated cut point.
    /// </summary>
    private readonly struct SplitVert
    {
        public SplitVert(Vec3 pos, Uv uv, Uv? lm, float proj, int poolIndex)
        {
            Pos = pos;
            Uv = uv;
            Lm = lm;
            Proj = proj;
            PoolIndex = poolIndex;
        }

        public Vec3 Pos { get; }

        public Uv Uv { get; }

        public Uv? Lm { get; }

        public float Proj { get; }

        public int PoolIndex { get; }
    }

    /// <summary>
    /// Alpine's split_face: projects the polygon onto an in-plane split axis, then makes
    /// <paramref name="numSplits"/> parallel cuts, clipping the running polygon into
    /// <c>numSplits + 1</c> child faces. Returns null / empty when the polygon cannot be
    /// divided (degenerate axis, zero span, or a cut that does not produce two crossings).
    /// </summary>
    private static List<Face>? SplitFace(Geometry g, Face face, int numSplits, bool alongU)
    {
        if (face.Vertices.Count < 3)
        {
            return null;
        }

        Vec3 axis = SplitAxis(face.Plane.Normal, alongU);
        float axisLen = axis.Length();
        if (axisLen < 1e-9f)
        {
            return null;
        }

        axis = axis.Scale(1f / axisLen);

        bool hasLm = FaceHasLightmapUvs(face);
        var verts = new List<SplitVert>(face.Vertices.Count);
        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (FaceVertex fv in face.Vertices)
        {
            Vec3 p = g.Vertices[fv.Index];
            float proj = p.Dot(axis);
            min = MathF.Min(min, proj);
            max = MathF.Max(max, proj);
            verts.Add(new SplitVert(p, fv.TextureCoords, hasLm ? fv.LightmapCoords ?? default : null, proj, fv.Index));
        }

        if (max - min < 1e-6f)
        {
            return null;
        }

        var children = new List<Face>();
        List<SplitVert> remaining = verts;
        for (int cut = 1; cut <= numSplits; cut++)
        {
            float t = (float)cut / (numSplits + 1);
            float cutVal = min + (t * (max - min));
            int rn = remaining.Count;
            if (rn < 3)
            {
                break;
            }

            int cross1 = -1, cross2 = -1, crossings = 0;
            for (int i = 0; i < rn; i++)
            {
                bool li = remaining[i].Proj < cutVal;
                bool lj = remaining[(i + 1) % rn].Proj < cutVal;
                if (li != lj)
                {
                    if (crossings == 0)
                    {
                        cross1 = i;
                    }
                    else if (crossings == 1)
                    {
                        cross2 = i;
                    }

                    crossings++;
                }
            }

            if (crossings != 2)
            {
                break;
            }

            SplitVert sv1 = InterpolateCut(remaining[cross1], remaining[(cross1 + 1) % rn], cutVal);
            SplitVert sv2 = InterpolateCut(remaining[cross2], remaining[(cross2 + 1) % rn], cutVal);

            var polyA = new List<SplitVert> { sv1 };
            for (int i = (cross1 + 1) % rn; ; i = (i + 1) % rn)
            {
                polyA.Add(remaining[i]);
                if (i == cross2)
                {
                    break;
                }
            }

            polyA.Add(sv2);

            var polyB = new List<SplitVert> { sv2 };
            for (int i = (cross2 + 1) % rn; ; i = (i + 1) % rn)
            {
                polyB.Add(remaining[i]);
                if (i == cross1)
                {
                    break;
                }
            }

            polyB.Add(sv1);

            bool polyAisLeft = remaining[(cross1 + 1) % rn].Proj < cutVal;
            List<SplitVert> left = polyAisLeft ? polyA : polyB;
            List<SplitVert> right = polyAisLeft ? polyB : polyA;

            if (left.Count >= 3)
            {
                children.Add(BuildSplitChild(g, face, left, hasLm));
            }

            remaining = right;
        }

        if (remaining.Count >= 3 && children.Count > 0)
        {
            children.Add(BuildSplitChild(g, face, remaining, hasLm));
        }

        return children.Count > 0 ? children : null;
    }

    /// <summary>The in-plane split axis for a face normal (world-X- or world-Y-derived), per Alpine.</summary>
    private static Vec3 SplitAxis(Vec3 normal, bool alongU)
    {
        Vec3 axis;
        if (alongU)
        {
            axis = new Vec3(normal.Z, 0f, -normal.X);
            if (MathF.Sqrt((axis.X * axis.X) + (axis.Z * axis.Z)) < 1e-6f)
            {
                axis = ProjectWorldXOntoPlane(normal);
            }
        }
        else
        {
            float dy = normal.Y;
            axis = new Vec3(-dy * normal.X, 1f - (dy * normal.Y), -dy * normal.Z);
            if (axis.Length() < 1e-6f)
            {
                axis = ProjectWorldXOntoPlane(normal);
            }
        }

        return axis;
    }

    private static Vec3 ProjectWorldXOntoPlane(Vec3 normal)
    {
        float dx = normal.X;
        return new Vec3(1f - (dx * normal.X), -dx * normal.Y, -dx * normal.Z);
    }

    /// <summary>Interpolates a new cut vertex where an edge crosses the cut plane (pos + all UVs).</summary>
    private static SplitVert InterpolateCut(SplitVert a, SplitVert b, float cutVal)
    {
        float denom = b.Proj - a.Proj;
        float frac = MathF.Abs(denom) < 1e-12f ? 0f : (cutVal - a.Proj) / denom;
        Uv? lm = a.Lm is { } la && b.Lm is { } lb ? LerpUv(la, lb, frac) : null;
        return new SplitVert(Vec3Math.Lerp(a.Pos, b.Pos, frac), LerpUv(a.Uv, b.Uv, frac), lm, cutVal, -1);
    }

    private static Uv LerpUv(Uv a, Uv b, float t) => new(a.U + ((b.U - a.U) * t), a.V + ((b.V - a.V) * t));

    /// <summary>Builds a split child face, copying every attribute of the original and its per-corner UVs.</summary>
    private static Face BuildSplitChild(Geometry g, Face original, List<SplitVert> poly, bool hasLm)
    {
        var child = new Face
        {
            Plane = original.Plane,
            Texture = original.Texture,
            SurfaceIndex = original.SurfaceIndex,
            Reserved1A = original.Reserved1A,
            Reserved1B = original.Reserved1B,
            PortalIndexPlus2 = original.PortalIndexPlus2,
            Flags = original.Flags,
            Reserved2 = original.Reserved2,
            SmoothingGroups = original.SmoothingGroups,
            RoomIndex = original.RoomIndex,
        };

        foreach (SplitVert sv in poly)
        {
            int idx = sv.PoolIndex >= 0 ? sv.PoolIndex : GeometryUtil.AddVertex(g, sv.Pos);
            child.Vertices.Add(new FaceVertex
            {
                Index = idx,
                TextureCoords = sv.Uv,
                LightmapCoords = hasLm ? sv.Lm : null,
            });
        }

        return child;
    }

    /// <summary>Whether a face binds a real lightmap surface (mirrors Geometry's serializer test).</summary>
    private static bool FaceHasLightmapUvs(Face f) => (f.SurfaceIndex & 0xFFFF) != 0xFFFF;

    private static List<int> MergeAlongSharedEdge(Face a, Face b, int s0, int s1)
    {
        List<int> ai = a.Vertices.Select(v => v.Index).ToList();
        List<int> bi = b.Vertices.Select(v => v.Index).ToList();
        int n = ai.Count;

        // Find the shared edge in A (its two shared vertices must be adjacent).
        int edge = -1;
        for (int i = 0; i < n; i++)
        {
            int x = ai[i], y = ai[(i + 1) % n];
            if ((x == s0 && y == s1) || (x == s1 && y == s0))
            {
                edge = i;
                break;
            }
        }

        if (edge < 0)
        {
            return new List<int>(); // shared vertices not an edge of A
        }

        int aStart = ai[edge];
        int aEnd = ai[(edge + 1) % n];

        // Walk all of A starting at aEnd, ending at aStart (the shared edge at the ends).
        var result = new List<int>();
        for (int k = 0; k < n; k++)
        {
            result.Add(ai[(edge + 1 + k) % n]);
        }

        // From aStart, walk B's non-shared path back toward aEnd.
        int m = bi.Count;
        int bx = bi.IndexOf(aStart);
        int step = bi[(bx + 1) % m] == aEnd ? -1 : 1;
        for (int k = 0; k < m; k++)
        {
            int v = bi[((bx + (step * (k + 1))) % m + m) % m];
            if (v == aEnd)
            {
                break;
            }

            result.Add(v);
        }

        return result.Distinct().ToList();
    }

    private static bool IsConvex(Geometry g, IReadOnlyList<int> loop, Vec3 normal)
    {
        int n = loop.Count;
        if (n < 3)
        {
            return false;
        }

        bool? sign = null;
        for (int i = 0; i < n; i++)
        {
            Vec3 a = g.Vertices[loop[i]];
            Vec3 b = g.Vertices[loop[(i + 1) % n]];
            Vec3 c = g.Vertices[loop[(i + 2) % n]];
            float cross = b.Sub(a).Cross(c.Sub(b)).Dot(normal);
            if (MathF.Abs(cross) < 1e-5f)
            {
                continue;
            }

            bool positive = cross > 0f;
            if (sign is null)
            {
                sign = positive;
            }
            else if (sign != positive)
            {
                return false;
            }
        }

        return true;
    }
}
