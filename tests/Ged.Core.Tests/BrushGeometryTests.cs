using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.IO.Mesh;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>Unit tests for the cookie-cutter primitive generators and geometry utilities.</summary>
public sealed class BrushGeometryTests
{
    private const string Tex = "test.tga";

    // ---- Vector / matrix math -------------------------------------------------

    [Fact]
    public void Mat3_Transform_Matches_Render_World_Convention()
    {
        var m = Mat3.Identity;
        Assert.True(m.Transform(new Vec3(1, 2, 3)).ApproxEquals(new Vec3(1, 2, 3)));

        // A 90° rotation about +Y maps +X to -Z (right-handed, Y up).
        Mat3 ry = Mat3Math.RotationY(MathF.PI / 2f);
        Assert.True(ry.Transform(new Vec3(1, 0, 0)).ApproxEquals(new Vec3(0, 0, -1)));
        Assert.True(ry.Transform(new Vec3(0, 0, 1)).ApproxEquals(new Vec3(1, 0, 0)));
    }

    [Fact]
    public void Mat3_Compose_Is_Sequential_And_Inverse_Restores()
    {
        Mat3 a = Mat3Math.RotationX(0.4f);
        Mat3 b = Mat3Math.RotationZ(0.9f);
        var v = new Vec3(0.3f, -1.2f, 2.1f);
        Vec3 composed = Mat3Math.Compose(a, b).Transform(v);
        Vec3 sequential = a.Transform(b.Transform(v));
        Assert.True(composed.ApproxEquals(sequential));

        // Transpose is the inverse for a rotation.
        Mat3 r = Mat3Math.Compose(a, b);
        Assert.True(r.InverseTransform(r.Transform(v)).ApproxEquals(v));
    }

    // ---- Box ------------------------------------------------------------------

    [Fact]
    public void Box_NoSplits_Has_Six_Quads_Eight_Vertices_Closed()
    {
        Geometry g = BrushFactory.Box(4, 6, 8, 0, 0, 0, Tex);
        Assert.Equal(6, g.Faces.Count);
        Assert.Equal(8, g.Vertices.Count);
        Assert.All(g.Faces, f => Assert.Equal(4, f.Vertices.Count));
        AssertClosedManifold(g);
        Assert.True(GeometryUtil.Validate(g));
    }

    [Fact]
    public void Box_Honors_Dimensions()
    {
        Geometry g = BrushFactory.Box(4, 6, 8, 0, 0, 0, Tex);
        Aabb b = GeometryUtil.LocalBounds(g);
        Assert.True(b.P1.ApproxEquals(new Vec3(-2, -3, -4)));
        Assert.True(b.P2.ApproxEquals(new Vec3(2, 3, 4)));
    }

    [Fact]
    public void Box_Splits_Subdivide_Face_Count()
    {
        Geometry g = BrushFactory.Box(4, 4, 4, 1, 1, 1, Tex);
        // 2[(hS+1)(dS+1) + (wS+1)(dS+1) + (wS+1)(hS+1)] with all splits = 1 -> 2*(4+4+4)=24.
        Assert.Equal(24, g.Faces.Count);
        AssertClosedManifold(g);
        Assert.True(GeometryUtil.Validate(g));
    }

    [Fact]
    public void Box_Face_Normals_Point_Outward()
    {
        Geometry g = BrushFactory.Box(2, 2, 2, 0, 0, 0, Tex);
        foreach (Face f in g.Faces)
        {
            Vec3 c = GeometryUtil.Centroid(GeometryUtil.Corners(g, f));
            // Outward: plane normal agrees with direction from origin to the face centroid.
            Assert.True(f.Plane.Normal.Dot(c) > 0.5f, $"Face normal {f.Plane.Normal} not outward at {c}.");
        }

        // The +X face has a normal ~ (1,0,0).
        Face px = g.Faces.First(f => GeometryUtil.Centroid(GeometryUtil.Corners(g, f)).X > 0.9f);
        Assert.True(px.Plane.Normal.ApproxEquals(new Vec3(1, 0, 0), 1e-3f));
    }

    [Fact]
    public void Box_Assigns_Finite_Planar_Uvs()
    {
        Geometry g = BrushFactory.Box(4, 4, 4, 0, 0, 0, Tex);
        foreach (Face f in g.Faces)
        {
            foreach (FaceVertex fv in f.Vertices)
            {
                Assert.True(float.IsFinite(fv.TextureCoords.U));
                Assert.True(float.IsFinite(fv.TextureCoords.V));
            }
        }
    }

    // ---- Cylinder / Cone / Sphere / Wedge / Face ------------------------------

    [Fact]
    public void Cylinder_Face_And_Vertex_Counts()
    {
        Geometry g = BrushFactory.Cylinder(4, 6, 4, 8, 1, Tex);
        Assert.Equal((8 * 1) + 2, g.Faces.Count); // 8 sides + 2 caps
        Assert.Equal(16, g.Vertices.Count); // 2 rings of 8
        AssertClosedManifold(g);
        Assert.True(GeometryUtil.Validate(g));
    }

    [Fact]
    public void Cylinder_Clamps_Sides_To_Minimum_Three()
    {
        Geometry g = BrushFactory.Cylinder(4, 4, 4, 2, 1, Tex);
        Assert.Equal(3 + 2, g.Faces.Count);
        AssertClosedManifold(g);
    }

    [Fact]
    public void Cone_Has_Base_Plus_Side_Triangles()
    {
        Geometry g = BrushFactory.Cone(4, 6, 4, 8, Tex);
        Assert.Equal(8 + 1, g.Faces.Count);
        Assert.Equal(9, g.Vertices.Count); // 8 base + apex
        Assert.Equal(8, g.Faces.Count(f => f.Vertices.Count == 3)); // side triangles
        AssertClosedManifold(g);
        Assert.True(GeometryUtil.Validate(g));
    }

    [Fact]
    public void Sphere_Is_Closed_And_Valid()
    {
        Geometry g = BrushFactory.Sphere(4, 4, 4, 8, 4, Tex);
        // top fan (lon) + bottom fan (lon) + (lat-2) quad bands * lon
        Assert.Equal(8 + 8 + (2 * 8), g.Faces.Count);
        AssertClosedManifold(g);
        Assert.True(GeometryUtil.Validate(g));

        // Every vertex lies on the ellipsoid radius (here a sphere of radius 2).
        foreach (Vec3 v in g.Vertices)
        {
            Assert.True(MathF.Abs(v.Length() - 2f) < 1e-3f);
        }
    }

    [Fact]
    public void Wedge_Is_Triangular_Prism()
    {
        Geometry g = BrushFactory.Wedge(4, 4, 6, Tex);
        Assert.Equal(5, g.Faces.Count);
        Assert.Equal(6, g.Vertices.Count);
        AssertClosedManifold(g);
        Assert.True(GeometryUtil.Validate(g));
    }

    [Fact]
    public void Face_Shape_Is_Single_Quad_Facing_Z()
    {
        Geometry g = BrushFactory.FaceQuad(4, 4, 0, 0, Tex);
        Assert.Single(g.Faces);
        Assert.Equal(4, g.Vertices.Count);
        Assert.True(g.Faces[0].Plane.Normal.ApproxEquals(new Vec3(0, 0, 1)));
    }

    [Fact]
    public void Face_Shape_Splits_Into_Grid()
    {
        Geometry g = BrushFactory.FaceQuad(4, 4, 1, 1, Tex);
        Assert.Equal(4, g.Faces.Count); // 2x2 cells
        Assert.Equal(9, g.Vertices.Count);
    }

    [Fact]
    public void Create_Sets_Flags_From_Params()
    {
        var p = new BrushCreateParams { Shape = BrushShape.Box, Air = true, Portal = true, Geoable = true };
        Brush b = BrushFactory.Create(p, 42);
        Assert.Equal(42, b.Uid);
        var flags = (BrushFlags)b.Flags;
        Assert.True(flags.HasFlag(BrushFlags.Air));
        Assert.True(flags.HasFlag(BrushFlags.Portal));
        Assert.True(flags.HasFlag(BrushFlags.Geoable));
        Assert.True(flags.HasFlag(BrushFlags.Detail)); // geoable implies detail
    }

    // ---- Mesh converter -------------------------------------------------------

    [Fact]
    public void FromMesh_Converts_Lod0_Triangles_To_Faces()
    {
        var mesh = new V3dFile();
        var sm = new V3dSubmesh();
        sm.Materials.Add(new V3dMaterial { DiffuseMapName = "wood.tga" });
        var lod = new V3dLod();
        lod.Textures.Add(new V3dLodTexture { Id = 0, Filename = "wood.tga" });
        var batch = new V3dBatch
        {
            TextureIndex = 0,
            NumVertices = 3,
            NumTriangles = 1,
            Positions = new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) },
            TexCoords = new[] { new Uv(0, 0), new Uv(1, 0), new Uv(0, 1) },
            Triangles = new[] { new V3dTriangle(0, 1, 2, 0) },
        };
        lod.Batches.Add(batch);
        sm.Lods.Add(lod);
        mesh.Submeshes.Add(sm);

        Geometry g = BrushFactory.FromMesh(mesh);
        Assert.Single(g.Faces);
        Assert.Equal(3, g.Faces[0].Vertices.Count);
        Assert.Contains("wood.tga", g.Textures);
    }

    // ---- Utilities ------------------------------------------------------------

    [Fact]
    public void WeldVertices_Merges_Coincident_And_Compacts()
    {
        var g = new Geometry();
        g.Textures.Add(Tex);
        g.Vertices.AddRange(new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 0.00001f) });
        var f = new Face();
        f.Vertices.AddRange(new[]
        {
            new FaceVertex { Index = 0 }, new FaceVertex { Index = 1 }, new FaceVertex { Index = 2 },
        });
        g.Faces.Add(f);

        GeometryUtil.WeldVertices(g);
        Assert.Equal(3, g.Vertices.Count); // duplicate origin dropped, plus unused compacted
    }

    [Fact]
    public void Validate_Rejects_Degenerate_And_Undersized_Faces()
    {
        var g = new Geometry();
        g.Vertices.AddRange(new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0) });
        var f = new Face();
        f.Vertices.AddRange(new[] { new FaceVertex { Index = 0 }, new FaceVertex { Index = 1 } });
        g.Faces.Add(f);
        Assert.False(GeometryUtil.Validate(g)); // fewer than three vertices
    }

    // ---- Helpers --------------------------------------------------------------

    /// <summary>Asserts that every undirected edge is shared by exactly two faces (closed 2-manifold).</summary>
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
