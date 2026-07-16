using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>Unit tests for brush transforms and the brush/face/vertex geometry operators.</summary>
public sealed class BrushOpsTests
{
    private const string Tex = "test.tga";

    private static Brush UnitBox(Vec3 pos = default) => new()
    {
        Uid = 1,
        Position = pos,
        Rotation = Mat3.Identity,
        Geometry = BrushFactory.Box(2, 2, 2, 0, 0, 0, Tex),
    };

    // ---- Transform math -------------------------------------------------------

    [Fact]
    public void Snap_Rounds_To_Grid_And_Angle()
    {
        Assert.Equal(2f, TransformMath.Snap(2.3f, 1f));
        Assert.Equal(2.5f, TransformMath.Snap(2.4f, 0.5f));
        Assert.Equal(0.5f, TransformMath.Snap(0.49f, 0.25f));
        Assert.Equal(MathF.PI / 2f, TransformMath.SnapAngle(1.4f, 90f), 3);
    }

    [Fact]
    public void RotateAboutPivot_Preserves_World_Vertex_Positions()
    {
        Brush b = UnitBox(new Vec3(3, 0, 0));
        Vec3 cornerBefore = BrushTransform.WorldVertex(b, b.Geometry.Vertices[0]);
        var pivot = new Vec3(0, 0, 0);
        Mat3 r = Mat3Math.RotationY(MathF.PI / 2f);
        Vec3 expected = pivot.Add(r.Transform(cornerBefore.Sub(pivot)));

        BrushTransform.RotateAboutPivot(b, r, pivot);
        Vec3 cornerAfter = BrushTransform.WorldVertex(b, b.Geometry.Vertices[0]);
        Assert.True(expected.ApproxEquals(cornerAfter, 1e-3f));
    }

    [Fact]
    public void Reorient_Bakes_Rotation_Keeping_World_Geometry()
    {
        Brush b = UnitBox(new Vec3(1, 2, 3));
        b.Rotation = Mat3Math.RotationZ(0.7f);
        Vec3[] worldBefore = b.Geometry.Vertices.Select(v => BrushTransform.WorldVertex(b, v)).ToArray();

        BrushTransform.Reorient(b);
        Assert.True(b.Rotation.ApproxEquals(Mat3.Identity));
        Vec3[] worldAfter = b.Geometry.Vertices.Select(v => BrushTransform.WorldVertex(b, v)).ToArray();
        for (int i = 0; i < worldBefore.Length; i++)
        {
            Assert.True(worldBefore[i].ApproxEquals(worldAfter[i], 1e-3f));
        }
    }

    [Fact]
    public void MoveCenter_Keeps_Geometry_But_Moves_Origin()
    {
        Brush b = UnitBox(new Vec3(0, 0, 0));
        Vec3[] worldBefore = b.Geometry.Vertices.Select(v => BrushTransform.WorldVertex(b, v)).ToArray();

        BrushTransform.MoveCenter(b, new Vec3(5, 0, 0));
        Assert.True(b.Position.ApproxEquals(new Vec3(5, 0, 0)));
        Vec3[] worldAfter = b.Geometry.Vertices.Select(v => BrushTransform.WorldVertex(b, v)).ToArray();
        for (int i = 0; i < worldBefore.Length; i++)
        {
            Assert.True(worldBefore[i].ApproxEquals(worldAfter[i], 1e-3f));
        }
    }

    [Fact]
    public void StretchToDimensions_Sets_New_Size()
    {
        Brush b = UnitBox();
        BrushTransform.StretchToDimensions(b, 6, 2, 2);
        Vec3 dims = BrushTransform.Dimensions(b);
        Assert.True(dims.ApproxEquals(new Vec3(6, 2, 2), 1e-3f));
    }

    // ---- Clip -----------------------------------------------------------------

    [Fact]
    public void Clip_Split_Produces_Two_Closed_Halves()
    {
        Brush b = UnitBox();
        ClipResult r = BrushOps.Clip(b, new Vec3(0, 0, 0), new Vec3(1, 0, 0), ClipMode.Split, flipNormal: false);
        Assert.True(r.Success);
        Assert.Equal(2, r.Pieces.Count);

        foreach (Geometry piece in r.Pieces)
        {
            Assert.True(GeometryUtil.Validate(piece));
            AssertClosedManifold(piece);
            Assert.Equal(8, piece.Vertices.Count);
            Assert.Equal(6, piece.Faces.Count);
        }

        Aabb a = GeometryUtil.LocalBounds(r.Pieces[0]);
        Aabb c = GeometryUtil.LocalBounds(r.Pieces[1]);
        // One half spans x in [-1,0], the other [0,1].
        Assert.True(MathF.Abs(a.P1.X + 1f) < 1e-3f && MathF.Abs(c.P2.X - 1f) < 1e-3f);
        Assert.True(MathF.Abs(a.P2.X) < 1e-3f && MathF.Abs(c.P1.X) < 1e-3f);
    }

    [Fact]
    public void Clip_Cut_Keeps_One_Half()
    {
        Brush b = UnitBox();
        ClipResult r = BrushOps.Clip(b, new Vec3(0, 0, 0), new Vec3(1, 0, 0), ClipMode.Cut, flipNormal: false);
        Assert.True(r.Success);
        Assert.Single(r.Pieces);
        AssertClosedManifold(r.Pieces[0]);
    }

    [Fact]
    public void Clip_Plane_Outside_Brush_Fails()
    {
        Brush b = UnitBox();
        ClipResult r = BrushOps.Clip(b, new Vec3(5, 0, 0), new Vec3(1, 0, 0), ClipMode.Split, flipNormal: false);
        Assert.False(r.Success);
    }

    // ---- Fuse -----------------------------------------------------------------

    [Fact]
    public void Fuse_Two_Adjacent_Boxes_Removes_Internal_Wall()
    {
        Brush a = UnitBox(new Vec3(0, 0, 0));
        Brush c = UnitBox(new Vec3(2, 0, 0)); // shares the x=1 face
        (OpResult res, Brush? fused) = BrushOps.Fuse(new[] { a, c });
        Assert.True(res.Success);
        Assert.NotNull(fused);
        Assert.Equal(10, fused!.Geometry.Faces.Count); // 12 - 2 shared walls
        Assert.True(GeometryUtil.Validate(fused.Geometry));
        AssertClosedManifold(fused.Geometry);
    }

    [Fact]
    public void Fuse_Requires_Two_Brushes()
    {
        (OpResult res, Brush? fused) = BrushOps.Fuse(new[] { UnitBox() });
        Assert.False(res.Success);
        Assert.Null(fused);
    }

    // ---- Mirror ---------------------------------------------------------------

    [Fact]
    public void Mirror_Keeps_Brush_Valid_And_Reflects_A_Wedge()
    {
        var b = new Brush { Geometry = BrushFactory.Wedge(4, 4, 4, Tex), Rotation = Mat3.Identity };
        float minXBefore = b.Geometry.Vertices.Min(v => v.X);
        float maxXBefore = b.Geometry.Vertices.Max(v => v.X);
        int countHighX = b.Geometry.Vertices.Count(v => v.X > 1.9f);

        BrushOps.Mirror(b, axis: 0);
        Assert.True(GeometryUtil.Validate(b.Geometry));
        AssertClosedManifold(b.Geometry);
        // The wedge's single high-X apex column reflects to the low-X side.
        Assert.Equal(countHighX, b.Geometry.Vertices.Count(v => v.X < -1.9f));
        Assert.Equal(minXBefore, b.Geometry.Vertices.Min(v => v.X), 3);
        Assert.Equal(maxXBefore, b.Geometry.Vertices.Max(v => v.X), 3);
    }

    // ---- Face ops -------------------------------------------------------------

    [Fact]
    public void Extrude_Grows_The_Brush_Along_The_Normal()
    {
        Brush b = UnitBox();
        int topFace = TopZFace(b.Geometry);
        float zMaxBefore = b.Geometry.Vertices.Max(v => v.Z);

        OpResult r = FaceOps.Extrude(b.Geometry, topFace, 2f);
        Assert.True(r.Success, r.Message);
        Assert.True(b.Geometry.Vertices.Max(v => v.Z) > zMaxBefore + 1.9f);
        Assert.True(GeometryUtil.Validate(b.Geometry));
        AssertClosedManifold(b.Geometry);
    }

    [Fact]
    public void FlipNormal_Reverses_Winding_And_Plane()
    {
        Brush b = UnitBox();
        int face = TopZFace(b.Geometry);
        Vec3 before = b.Geometry.Faces[face].Plane.Normal;
        Assert.True(FaceOps.FlipNormal(b.Geometry, face));
        Assert.True(b.Geometry.Faces[face].Plane.Normal.ApproxEquals(before.Negate(), 1e-3f));
    }

    [Fact]
    public void Triangulate_Splits_Quad_Into_Two_Triangles()
    {
        Brush b = UnitBox();
        int before = b.Geometry.Faces.Count;
        Assert.True(FaceOps.Triangulate(b.Geometry, 0));
        Assert.Equal(before + 1, b.Geometry.Faces.Count); // one quad -> two tris (net +1)
    }

    [Fact]
    public void Delete_Face_Rejected_When_All_Selected()
    {
        Brush b = UnitBox();
        var all = Enumerable.Range(0, b.Geometry.Faces.Count).ToList();
        Assert.False(FaceOps.Delete(b.Geometry, all));
        Assert.True(FaceOps.Delete(b.Geometry, new[] { 0 }));
        Assert.Equal(5, b.Geometry.Faces.Count);
    }

    [Fact]
    public void Combine_Reports_Stock_Errors_And_Merges_Coplanar_Faces()
    {
        Brush b = UnitBox();
        // Wrong count.
        Assert.Equal("Must select exactly two faces.", FaceOps.Combine(b.Geometry, new[] { 0 }).Message);

        // Non-coplanar: two different box faces.
        int top = TopZFace(b.Geometry);
        int side = b.Geometry.Faces.FindIndex(f => f.Plane.Normal.ApproxEquals(new Vec3(1, 0, 0), 1e-2f));
        Assert.Equal("Faces aren't coplanar.", FaceOps.Combine(b.Geometry, new[] { top, side }).Message);

        // Coplanar sharing an edge: split the top face in two, then recombine.
        Assert.True(FaceOps.NWaySplit(b.Geometry, top, 2, alongU: true));
        var coplanar = b.Geometry.Faces
            .Select((f, i) => (f, i))
            .Where(x => x.f.Plane.Normal.ApproxEquals(new Vec3(0, 0, 1), 1e-2f))
            .Select(x => x.i)
            .ToList();
        Assert.Equal(2, coplanar.Count);
        OpResult combine = FaceOps.Combine(b.Geometry, coplanar);
        Assert.True(combine.Success, combine.Message);
    }

    // ---- N-way split (arbitrary polygon) --------------------------------------

    [Fact]
    public void NWaySplit_Rectangle_Into_N_Pieces_Preserving_Attributes()
    {
        // An axis-aligned rectangle whose cut planes never touch a vertex: exactly N pieces.
        Geometry g = SinglePolygon(
            new[] { new Vec3(-2, -1, 0), new Vec3(2, -1, 0), new Vec3(2, 1, 0), new Vec3(-2, 1, 0) },
            flags: 0x0140, smoothing: 6u, texture: 3);

        OpResult r = FaceOps.NWaySplit(g, 0, pieces: 4, alongU: true);
        Assert.True(r.Success, r.Message);
        Assert.Equal(4, g.Faces.Count);
        Assert.True(GeometryUtil.Validate(g), "split rectangle should be valid");
        Assert.Equal(4, g.Faces.Select(f => f.FaceId).Distinct().Count()); // fresh, unique ids
        foreach (Face f in g.Faces)
        {
            Assert.Equal((ushort)0x0140, f.Flags);
            Assert.Equal(6u, f.SmoothingGroups);
            Assert.Equal(3, f.Texture);
            Assert.True(f.Plane.Normal.ApproxEquals(new Vec3(0, 0, 1), 1e-2f)); // still coplanar +Z
        }

        // The four strips tile the original span [-2, 2] with no gaps/overlap.
        float area = g.Faces.Sum(f => GeometryUtil.Area(GeometryUtil.Corners(g, f)));
        Assert.Equal(8f, area, 2); // 4 wide * 2 tall
    }

    [Theory]
    [InlineData(3)] // triangle
    [InlineData(5)] // pentagon
    [InlineData(6)] // hexagon
    [InlineData(8)] // octagon
    public void NWaySplit_Handles_Arbitrary_Ngon(int n)
    {
        Geometry g = RegularPolygonFace(n, flags: 0x0040, smoothing: 3u, texture: 2);
        float areaBefore = GeometryUtil.Area(GeometryUtil.Corners(g, g.Faces[0]));

        OpResult r = FaceOps.NWaySplit(g, 0, pieces: 3, alongU: true);
        Assert.True(r.Success, r.Message);
        Assert.True(g.Faces.Count >= 2, $"{n}-gon should split into 2+ faces");
        Assert.True(GeometryUtil.Validate(g), $"{n}-gon split should be valid");

        foreach (Face f in g.Faces)
        {
            Assert.Equal((ushort)0x0040, f.Flags);
            Assert.Equal(3u, f.SmoothingGroups);
            Assert.Equal(2, f.Texture);
            Assert.True(f.Plane.Normal.ApproxEquals(new Vec3(0, 0, 1), 1e-2f));
        }

        // The pieces conserve the original area (partition, not resample).
        float areaAfter = g.Faces.Sum(f => GeometryUtil.Area(GeometryUtil.Corners(g, f)));
        Assert.Equal(areaBefore, areaAfter, 2);
    }

    [Fact]
    public void NWaySplit_Interpolates_Uvs_At_The_Cut()
    {
        // UV == world (x, y); the middle cut at x=0 must interpolate to u=0.
        Geometry g = SinglePolygon(
            new[] { new Vec3(-1, -1, 0), new Vec3(1, -1, 0), new Vec3(1, 1, 0), new Vec3(-1, 1, 0) },
            flags: 0, smoothing: 0u, texture: 0);
        foreach (FaceVertex fv in g.Faces[0].Vertices)
        {
            Vec3 p = g.Vertices[fv.Index];
            fv.TextureCoords = new Uv(p.X, p.Y);
        }

        Assert.True(FaceOps.NWaySplit(g, 0, pieces: 2, alongU: true));

        // Every corner's U must still equal its vertex X (linear interpolation is exact here),
        // and the seam corners at x=0 must have u=0 — proving UVs were interpolated, not reprojected.
        bool sawSeam = false;
        foreach (Face f in g.Faces)
        {
            foreach (FaceVertex fv in f.Vertices)
            {
                Assert.Equal(g.Vertices[fv.Index].X, fv.TextureCoords.U, 3);
                if (MathF.Abs(g.Vertices[fv.Index].X) < 1e-4f)
                {
                    sawSeam = true;
                }
            }
        }

        Assert.True(sawSeam, "expected interpolated seam vertices at x=0");
    }

    [Fact]
    public void NWaySplit_Preserves_Lightmap_Surface_Binding_And_Uvs()
    {
        // A face that binds a real lightmap surface carries lm UVs per corner; children
        // must keep the surface index and interpolated lm UVs (Alpine copies surface_index + lm_u/lm_v).
        Geometry g = SinglePolygon(
            new[] { new Vec3(-2, -1, 0), new Vec3(2, -1, 0), new Vec3(2, 1, 0), new Vec3(-2, 1, 0) },
            flags: 0, smoothing: 0u, texture: 1);
        g.Faces[0].SurfaceIndex = 5;
        foreach (FaceVertex fv in g.Faces[0].Vertices)
        {
            fv.LightmapCoords = new Uv(0.25f, 0.75f);
        }

        Assert.True(FaceOps.NWaySplit(g, 0, pieces: 3, alongU: true));
        Assert.Equal(3, g.Faces.Count);
        foreach (Face f in g.Faces)
        {
            Assert.Equal(5, f.SurfaceIndex);
            Assert.All(f.Vertices, fv => Assert.NotNull(fv.LightmapCoords));
        }
    }

    [Fact]
    public void NWaySplit_Rejects_Degenerate_Selection()
    {
        Geometry g = SinglePolygon(
            new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0) },
            flags: 0, smoothing: 0u, texture: 0);
        Assert.Equal("Select a face to split.", FaceOps.NWaySplit(g, 9, 2, alongU: true).Message);
    }

    // ---- Vertex ops -----------------------------------------------------------

    [Fact]
    public void Weld_Merges_Vertices_And_Cleans_Faces()
    {
        Brush b = UnitBox();
        int before = b.Geometry.Vertices.Count;
        // Weld two adjacent corners of the top face.
        Face top = b.Geometry.Faces[TopZFace(b.Geometry)];
        var pair = new[] { top.Vertices[0].Index, top.Vertices[1].Index };
        OpResult r = VertexOps.Weld(b.Geometry, pair);
        Assert.True(r.Success, r.Message);
        Assert.True(b.Geometry.Vertices.Count < before);
        Assert.True(GeometryUtil.Validate(b.Geometry));
    }

    [Fact]
    public void Delete_Vertex_Drops_Incident_Faces_Safely()
    {
        Brush b = UnitBox();
        OpResult r = VertexOps.Delete(b.Geometry, new[] { 0 });
        Assert.True(r.Success, r.Message);
        Assert.True(GeometryUtil.Validate(b.Geometry));
    }

    [Fact]
    public void Bridge_Creates_A_Face_From_Selected_Vertices()
    {
        Brush b = UnitBox();
        int before = b.Geometry.Faces.Count;
        // Three corners of the +Z face form a triangle.
        Face top = b.Geometry.Faces[TopZFace(b.Geometry)];
        var tri = new[] { top.Vertices[0].Index, top.Vertices[1].Index, top.Vertices[2].Index };
        OpResult r = VertexOps.Bridge(b.Geometry, tri);
        Assert.True(r.Success, r.Message);
        Assert.Equal(before + 1, b.Geometry.Faces.Count);
    }

    [Fact]
    public void Bridge_Rejects_Fewer_Than_Three_Vertices()
    {
        Brush b = UnitBox();
        Assert.Equal("Bridge needs at least three vertices.", VertexOps.Bridge(b.Geometry, new[] { 0 }).Message);
        Assert.Equal("Bridge needs at least three vertices.", VertexOps.Bridge(b.Geometry, new[] { 0, 1 }).Message);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    public void Bridge_Accepts_Arbitrary_Vertex_Count(int n)
    {
        // A ring of n coplanar vertices (no cap — stock RED stopped at four).
        var g = new Geometry();
        var ring = new List<int>();
        for (int i = 0; i < n; i++)
        {
            float a = i * MathF.Tau / n;
            ring.Add(GeometryUtil.AddVertex(g, new Vec3(MathF.Cos(a) * 2f, MathF.Sin(a) * 2f, 0f)));
        }

        OpResult r = VertexOps.Bridge(g, ring);
        Assert.True(r.Success, r.Message);
        Assert.Single(g.Faces);
        Assert.Equal(n, g.Faces[0].Vertices.Count);
        Assert.True(GeometryUtil.Validate(g), $"{n}-vertex bridge should be valid");
    }

    [Fact]
    public void Bridge_Orients_The_New_Face_To_Match_Neighbours()
    {
        // A quad face wound so its normal points -Z. Bridging its four corners must
        // orient the new face the same way (Alpine neighbour-normal vote), not +Z.
        var g = new Geometry();
        int v0 = GeometryUtil.AddVertex(g, new Vec3(0, 0, 0));
        int v1 = GeometryUtil.AddVertex(g, new Vec3(0, 1, 0));
        int v2 = GeometryUtil.AddVertex(g, new Vec3(1, 1, 0));
        int v3 = GeometryUtil.AddVertex(g, new Vec3(1, 0, 0));
        var existing = new Face { Texture = 0, SurfaceIndex = -1, RoomIndex = -1, FaceId = 0 };
        foreach (int idx in new[] { v0, v1, v2, v3 }) // CW seen from +Z -> normal -Z
        {
            existing.Vertices.Add(new FaceVertex { Index = idx });
        }

        g.Faces.Add(existing);
        GeometryUtil.RecomputePlane(g, existing);
        Assert.True(existing.Plane.Normal.Z < 0f, "sanity: neighbour faces -Z");

        // Bridge selection in CCW order (best-fit normal would be +Z before the flip).
        OpResult r = VertexOps.Bridge(g, new[] { v0, v3, v2, v1 });
        Assert.True(r.Success, r.Message);
        Face bridge = g.Faces[^1];
        Assert.True(bridge.Plane.Normal.Dot(existing.Plane.Normal) > 0f,
            "bridge normal should match the neighbouring face, not oppose it");
    }

    // ---- Deformers ------------------------------------------------------------

    [Fact]
    public void Twist_Changes_Geometry_Without_Degenerating()
    {
        Geometry g = BrushFactory.Box(2, 8, 2, 0, 4, 0, Tex);
        Vec3 topBefore = g.Vertices.OrderByDescending(v => v.Y).First();
        Deformers.Twist(g, axis: 1, totalDegrees: 90f);
        Assert.True(g.Vertices.All(v => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)));
        // A top-ring vertex rotated ~90° about Y is displaced from its start.
        Vec3 topAfter = g.Vertices.OrderByDescending(v => v.Y).First();
        Assert.True(topBefore.Sub(topAfter).Length() > 0.5f);
    }

    [Fact]
    public void Stretch_Deformer_Scales_About_Centre()
    {
        Geometry g = BrushFactory.Box(2, 2, 2, 0, 0, 0, Tex);
        Deformers.Stretch(g, new Vec3(2, 1, 1));
        Aabb bounds = GeometryUtil.LocalBounds(g);
        Assert.True(bounds.P2.Sub(bounds.P1).ApproxEquals(new Vec3(4, 2, 2), 1e-3f));
    }

    // ---- helpers --------------------------------------------------------------

    private static int TopZFace(Geometry g) =>
        g.Faces.FindIndex(f => f.Plane.Normal.ApproxEquals(new Vec3(0, 0, 1), 1e-2f));

    /// <summary>A geometry holding a single convex polygon face on the z=0 plane (UV = planar).</summary>
    private static Geometry SinglePolygon(IReadOnlyList<Vec3> corners, ushort flags, uint smoothing, int texture)
    {
        var g = new Geometry();
        var face = new Face
        {
            Texture = texture,
            SurfaceIndex = -1,
            Flags = flags,
            SmoothingGroups = smoothing,
            RoomIndex = -1,
            FaceId = 7,
        };
        foreach (Vec3 c in corners)
        {
            int idx = GeometryUtil.AddVertex(g, c);
            face.Vertices.Add(new FaceVertex { Index = idx, TextureCoords = new Uv(c.X, c.Y) });
        }

        g.Faces.Add(face);
        GeometryUtil.RecomputePlane(g, face);
        return g;
    }

    /// <summary>A single regular n-gon face (CCW, +Z normal), rotated off-axis so cuts miss vertices.</summary>
    private static Geometry RegularPolygonFace(int n, ushort flags, uint smoothing, int texture, float radius = 3f, float rot = 0.37f)
    {
        var corners = new List<Vec3>(n);
        for (int i = 0; i < n; i++)
        {
            float a = rot + (i * MathF.Tau / n);
            corners.Add(new Vec3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0f));
        }

        return SinglePolygon(corners, flags, smoothing, texture);
    }

    private static void AssertClosedManifold(Geometry g)
    {
        var edgeCounts = new Dictionary<(int, int), int>();
        foreach (Face f in g.Faces)
        {
            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                int a = f.Vertices[i].Index;
                int b = f.Vertices[(i + 1) % n].Index;
                var key = a < b ? (a, b) : (b, a);
                edgeCounts[key] = edgeCounts.GetValueOrDefault(key) + 1;
            }
        }

        Assert.All(edgeCounts, kv => Assert.Equal(2, kv.Value));
    }
}
