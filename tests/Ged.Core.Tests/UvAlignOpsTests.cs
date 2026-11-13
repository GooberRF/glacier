using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;
using V3 = Ged.Core.Editing.UvAlignOps.Vec3d;

namespace Ged.Core.Tests;

/// <summary>
/// The edge- and neighbour-aware UV operations (<see cref="UvAlignOps"/>): align to an edge (stand / hang, offset,
/// kept or explicit scale, true length on slopes), wrap across a corner (shared edge, plane intersection, coplanar,
/// rotated brushes), per-face fitting in the brush frame, the affine read-back they build on, and the texture-size
/// lookup the ppm mapping uses.
/// </summary>
public sealed class UvAlignOpsTests
{
    private const double Tol = 1e-4;

    private static Brush Box(float w, float h, float d, Vec3 pos = default, Mat3? rot = null)
    {
        Brush b = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Box, Width = w, Height = h, Depth = d }, 1);
        b.Position = pos;
        b.Rotation = rot ?? Mat3.Identity;
        return b;
    }

    /// <summary>The face whose world normal is nearest <paramref name="dir"/>.</summary>
    private static Face FaceToward(Brush b, V3 dir) =>
        b.Geometry.Faces.OrderByDescending(f => UvAlignOps.Normal(UvAlignOps.WorldCorners(b.Geometry, f, b.Rotation, b.Position)).Dot(dir)).First();

    private static List<V3> Corners(Brush b, Face f) => UvAlignOps.WorldCorners(b.Geometry, f, b.Rotation, b.Position);

    private static void BoxMap(Brush b, Face f, float tileMetres = 4f) =>
        UvOps.BoxMap(b.Geometry, f, b.Rotation, b.Position, 1f / tileMetres, 1, 1);

    private static UvAlignOps.UvAffine Map(Brush b, Face f)
    {
        Assert.True(UvAlignOps.TryGetUvMap(b.Geometry, f, b.Rotation, b.Position, out UvAlignOps.UvAffine m));
        return m;
    }

    // ---- read-back --------------------------------------------------------------------------------------

    [Fact]
    public void TryGetUvMap_Recovers_The_Box_Map_Gradient()
    {
        Brush b = Box(8, 4, 6, new Vec3(10, 2, -3));
        Face front = FaceToward(b, new V3(0, 0, 1));
        BoxMap(b, front);
        UvAlignOps.UvAffine m = Map(b, front);
        // +Z face box-maps (X, Y): U = x / 4, V = -y / 4.
        Assert.Equal(0.25, m.Gu.X, 5);
        Assert.Equal(0, m.Gu.Y, 5);
        Assert.Equal(-0.25, m.Gv.Y, 5);
        foreach ((V3 p, FaceVertex fv) in Corners(b, front).Zip(front.Vertices))
        {
            Assert.Equal(fv.TextureCoords.U, m.U(p), 4);
            Assert.Equal(fv.TextureCoords.V, m.V(p), 4);
        }
    }

    [Fact]
    public void TryGetUvMap_Of_Constant_Uvs_Has_A_Zero_Gradient()
    {
        Brush b = Box(4, 4, 4);
        Face f = b.Geometry.Faces[0];
        foreach (FaceVertex fv in f.Vertices)
        {
            fv.TextureCoords = new Uv(0.5f, 0.5f);
        }

        // All corners on one UV: the map exists with a zero gradient (align_to_edge then falls back to the new-brush tile).
        Assert.True(UvAlignOps.TryGetUvMap(b.Geometry, f, b.Rotation, b.Position, out UvAlignOps.UvAffine m));
        Assert.True(m.Gu.Length < 1e-9 && m.Gv.Length < 1e-9);
    }

    // ---- edge selection -----------------------------------------------------------------------------------

    [Fact]
    public void ExtremeEdge_Finds_The_Bottom_Of_A_Wall_And_None_On_A_Floor()
    {
        Brush b = Box(8, 4, 6, new Vec3(0, 2, 0));
        Face wall = FaceToward(b, new V3(1, 0, 0));
        List<V3> c = Corners(b, wall);
        int bottom = UvAlignOps.ExtremeEdge(c, new V3(0, -1, 0));
        Assert.InRange(bottom, 0, c.Count - 1);
        Assert.Equal(0, c[bottom].Y, 5);
        Assert.Equal(0, c[(bottom + 1) % c.Count].Y, 5);

        Face top = FaceToward(b, new V3(0, 1, 0));
        Assert.Equal(-1, UvAlignOps.ExtremeEdge(Corners(b, top), new V3(0, -1, 0)));
        Assert.InRange(UvAlignOps.ExtremeEdge(Corners(b, top), new V3(0, 0, -1)), 0, 3);
    }

    // ---- align to edge ------------------------------------------------------------------------------------

    [Fact]
    public void AlignToEdge_Bottom_Stands_The_Texture_On_The_Floor_Line_At_The_Kept_Scale()
    {
        // A 6 m wall from y = 0.3 to 4.3 (the floor line is not on a tile boundary under the box map).
        Brush b = Box(6, 4, 2, new Vec3(1, 2.3f, 0));
        Face wall = FaceToward(b, new V3(0, 0, 1));
        BoxMap(b, wall);
        UvAlignOps.UvAffine before = Map(b, wall);
        List<V3> c = Corners(b, wall);
        int bottom = UvAlignOps.ExtremeEdge(c, new V3(0, -1, 0));

        Assert.True(UvAlignOps.AlignToEdge(b.Geometry, wall, b.Rotation, b.Position, bottom, hang: false, offsetMetres: 0));
        UvAlignOps.UvAffine after = Map(b, wall);
        Assert.Equal(before.Gu.Length, after.Gu.Length, 5); // scale kept
        Assert.Equal(before.Gv.Length, after.Gv.Length, 5);
        Assert.Equal(-0.25, after.Gv.Y, 5);                 // V still grows downward
        Assert.Equal(before.Gu.X, after.Gu.X, 5);           // U runs along +X like the box map (not mirrored)
        for (int i = 0; i < c.Count; i++)
        {
            Uv uv = wall.Vertices[i].TextureCoords;
            if (Math.Abs(c[i].Y - 0.3) < Tol)
            {
                Assert.Equal(0, uv.V, 4);                    // a tile boundary on the floor line
            }
            else
            {
                Assert.Equal(-1, uv.V, 4);                   // 4 m up = one tile
            }
        }
    }

    [Fact]
    public void AlignToEdge_Top_Hangs_The_Texture_From_The_Ceiling_Line_With_An_Offset()
    {
        Brush b = Box(6, 4, 2, new Vec3(0, 2.3f, 0));
        Face wall = FaceToward(b, new V3(1, 0, 0));
        BoxMap(b, wall);
        List<V3> c = Corners(b, wall);
        int top = UvAlignOps.ExtremeEdge(c, new V3(0, 1, 0));
        Assert.True(UvAlignOps.AlignToEdge(b.Geometry, wall, b.Rotation, b.Position, top, hang: true, offsetMetres: 1, 0.5, 0.5));
        for (int i = 0; i < c.Count; i++)
        {
            double depth = 4.3 - c[i].Y; // metres below the ceiling line
            Assert.Equal((depth - 1) * 0.5, wall.Vertices[i].TextureCoords.V, 4); // boundary 1 m below, V grows down
        }
    }

    [Fact]
    public void AlignToEdge_On_A_Slope_Maps_True_Length_Up_The_Slope()
    {
        // A wedge 4 m long rising 2 m: the slope is sqrt(20) m long. At 64 px/m on a 256 px texture (0.25 tiles/m)
        // the slope spans sqrt(20) / 4 tiles — the box map would show only its 4 m plan length (stretched).
        Brush w = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Wedge, Width = 4, Height = 2, Depth = 3 }, 2);
        Face slope = w.Geometry.Faces.First(f =>
        {
            double ny = UvAlignOps.Normal(Corners(w, f)).Y;
            return ny > 0.2 && ny < 0.98;
        });
        List<V3> c = Corners(w, slope);
        int foot = UvAlignOps.ExtremeEdge(c, new V3(0, -1, 0));
        Assert.True(UvAlignOps.AlignToEdge(w.Geometry, slope, w.Rotation, w.Position, foot, hang: false, 0, 0.25, 0.25));

        double vMin = slope.Vertices.Min(v => v.TextureCoords.V), vMax = slope.Vertices.Max(v => v.TextureCoords.V);
        Assert.Equal(Math.Sqrt(20) / 4, vMax - vMin, 4);
        double uMin = slope.Vertices.Min(v => v.TextureCoords.U), uMax = slope.Vertices.Max(v => v.TextureCoords.U);
        Assert.Equal(3 / 4.0, uMax - uMin, 4); // U along the 3 m foot edge
        V3 a = c[foot], b2 = c[(foot + 1) % c.Count];
        Assert.Equal(slope.Vertices[foot].TextureCoords.V, slope.Vertices[(foot + 1) % c.Count].TextureCoords.V, 5); // foot edge = one row
        Assert.Equal(0, slope.Vertices[foot].TextureCoords.V, 5);
        Assert.Equal(a.Y, b2.Y, 5);
    }

    [Fact]
    public void AlignToEdge_Follows_A_Rotated_Brush_In_World_Space()
    {
        Mat3 yaw = Mat3Math.RotationY(MathF.PI / 6);
        Brush b = Box(6, 4, 2, new Vec3(3, 2, 7), yaw);
        Face wall = FaceToward(b, V3.From(yaw.Transform(new Vec3(0, 0, 1)))); // the rotated 6 m wide face
        BoxMap(b, wall);
        List<V3> c = Corners(b, wall);
        int bottom = UvAlignOps.ExtremeEdge(c, new V3(0, -1, 0));
        Assert.True(UvAlignOps.AlignToEdge(b.Geometry, wall, b.Rotation, b.Position, bottom, false, 0, 0.25, 0.25));
        double uMin = wall.Vertices.Min(v => v.TextureCoords.U), uMax = wall.Vertices.Max(v => v.TextureCoords.U);
        Assert.Equal(6 / 4.0, uMax - uMin, 4); // true width along the rotated wall (the box map would foreshorten it)
        for (int i = 0; i < c.Count; i++)
        {
            Assert.Equal(-c[i].Y / 4, wall.Vertices[i].TextureCoords.V, 4);
        }
    }

    [Fact]
    public void AlignToEdge_Rejects_A_Bad_Edge_Index()
    {
        Brush b = Box(4, 4, 4);
        Face f = b.Geometry.Faces[0];
        Uv[] before = f.Vertices.Select(v => v.TextureCoords).ToArray();
        Assert.False(UvAlignOps.AlignToEdge(b.Geometry, f, b.Rotation, b.Position, 7, false, 0));
        Assert.Equal(before, f.Vertices.Select(v => v.TextureCoords).ToArray());
    }

    // ---- wrap from --------------------------------------------------------------------------------------

    [Fact]
    public void WrapFrom_Continues_Across_A_Shared_Corner_At_The_Same_Scale()
    {
        Brush b = Box(3, 4, 5, new Vec3(0.7f, 2, -1.1f));
        Face front = FaceToward(b, new V3(0, 0, 1));
        Face side = FaceToward(b, new V3(1, 0, 0));
        BoxMap(b, front);
        UvOps.Offset(front, 0.37f, 0.11f); // an arbitrary offset that must carry over
        Assert.True(UvAlignOps.WrapFrom(b.Geometry, side, b.Rotation, b.Position, b.Geometry, front, b.Rotation, b.Position));

        // Along the shared vertical edge both faces agree.
        List<V3> cf = Corners(b, front), cs = Corners(b, side);
        int shared = 0;
        for (int i = 0; i < cs.Count; i++)
        {
            int j = cf.FindIndex(p => p.Sub(cs[i]).Length < 1e-4);
            if (j >= 0)
            {
                shared++;
                Assert.Equal(front.Vertices[j].TextureCoords.U, side.Vertices[i].TextureCoords.U, 4);
                Assert.Equal(front.Vertices[j].TextureCoords.V, side.Vertices[i].TextureCoords.V, 4);
            }
        }

        Assert.Equal(2, shared);
        UvAlignOps.UvAffine m = Map(b, side);
        Assert.Equal(0.25, m.Gu.Length, 5);
        Assert.Equal(-0.25, m.Gv.Y, 5); // still upright
        double uMin = side.Vertices.Min(v => v.TextureCoords.U), uMax = side.Vertices.Max(v => v.TextureCoords.U);
        Assert.Equal(5 / 4.0, uMax - uMin, 4);
    }

    [Fact]
    public void WrapFrom_Uses_The_Plane_Intersection_When_No_Edge_Is_Shared()
    {
        // Two separate brushes: a wall and a perpendicular wall 2 m away. Continuity holds on the line where
        // their planes meet.
        Brush a = Box(4, 4, 1, new Vec3(0, 2, 0));       // front face on z = 0.5
        Brush c = Box(1, 4, 4, new Vec3(5, 2, 3));       // -x face on x = 4.5
        Face fa = FaceToward(a, new V3(0, 0, 1));
        Face fc = FaceToward(c, new V3(-1, 0, 0));
        BoxMap(a, fa);
        Assert.True(UvAlignOps.WrapFrom(c.Geometry, fc, c.Rotation, c.Position, a.Geometry, fa, a.Rotation, a.Position));
        UvAlignOps.UvAffine ma = Map(a, fa), mc = Map(c, fc);
        var hinge = new V3(4.5, 1.7, 0.5); // on both planes
        Assert.Equal(ma.U(hinge), mc.U(hinge), 4);
        Assert.Equal(ma.V(hinge), mc.V(hinge), 4);
        Assert.Equal(0.25, mc.Gu.Length, 5);
    }

    [Fact]
    public void WrapFrom_Coplanar_Neighbour_Takes_The_Same_Map()
    {
        Brush a = Box(4, 4, 1, new Vec3(0, 2, 0));
        Brush c = Box(4, 4, 1, new Vec3(4, 2, 0));
        Face fa = FaceToward(a, new V3(0, 0, 1));
        Face fc = FaceToward(c, new V3(0, 0, 1));
        BoxMap(a, fa);
        UvOps.Offset(fa, 0.3f, 0.2f);
        UvOps.Scale(fa, 2f, 2f);
        Assert.True(UvAlignOps.WrapFrom(c.Geometry, fc, c.Rotation, c.Position, a.Geometry, fa, a.Rotation, a.Position));
        UvAlignOps.UvAffine ma = Map(a, fa);
        foreach ((V3 p, FaceVertex fv) in Corners(c, fc).Zip(fc.Vertices))
        {
            Assert.Equal(ma.U(p), fv.TextureCoords.U, 4);
            Assert.Equal(ma.V(p), fv.TextureCoords.V, 4);
        }
    }

    [Fact]
    public void WrapFrom_Fails_On_A_Degenerate_Source()
    {
        Brush b = Box(4, 4, 4);
        Face src = b.Geometry.Faces[0];
        Face dst = b.Geometry.Faces[1];
        foreach (FaceVertex fv in src.Vertices)
        {
            fv.TextureCoords = new Uv(1, 1);
        }

        // Zero gradient: the destination gets a constant map (the source's single UV) — still a valid continuation.
        Assert.True(UvAlignOps.WrapFrom(b.Geometry, dst, b.Rotation, b.Position, b.Geometry, src, b.Rotation, b.Position));
        Assert.All(dst.Vertices, v => Assert.Equal(1f, v.TextureCoords.U, 4));

        var g = new Geometry();
        g.Vertices.Add(new Vec3(0, 0, 0));
        g.Vertices.Add(new Vec3(1, 0, 0));
        var line = new Face();
        line.Vertices.Add(new FaceVertex { Index = 0 });
        line.Vertices.Add(new FaceVertex { Index = 1 });
        g.Faces.Add(line);
        Assert.False(UvAlignOps.WrapFrom(b.Geometry, dst, b.Rotation, b.Position, g, line, Mat3.Identity, default));
    }

    // ---- fit in the brush frame -------------------------------------------------------------------------

    [Fact]
    public void FitLocal_Both_Gives_One_Tile_Per_Face_Whatever_The_Brush_Rotation()
    {
        Brush b = Box(2, 1, 3, new Vec3(5, 0.5f, 5), Mat3Math.RotationY(0.7f));
        foreach (Face f in b.Geometry.Faces)
        {
            Assert.True(UvAlignOps.FitLocal(b.Geometry, f, UvFitAxes.Both, 1, 1, 256, 256));
            Assert.Equal(0, f.Vertices.Min(v => v.TextureCoords.U), 4);
            Assert.Equal(1, f.Vertices.Max(v => v.TextureCoords.U), 4);
            Assert.Equal(0, f.Vertices.Min(v => v.TextureCoords.V), 4);
            Assert.Equal(1, f.Vertices.Max(v => v.TextureCoords.V), 4);
        }

        // On a side face V runs down (the top corners are V = 0).
        Face side = b.Geometry.Faces.First(f => Math.Abs(UvAlignOps.Normal(Corners(b, f)).Y) < 0.1);
        List<V3> c = Corners(b, side);
        for (int i = 0; i < c.Count; i++)
        {
            Assert.Equal(c[i].Y > 0.5 ? 0 : 1, side.Vertices[i].TextureCoords.V, 4);
        }
    }

    [Fact]
    public void FitLocal_One_Axis_Keeps_Square_Texels()
    {
        // A 6 m x 0.75 m strip with a 256x64 trim texture, fitted in V: one tile tall, and along U the tile is as
        // long as square texels make it (0.75 m x 256/64 = 3 m), so the 6 m strip shows 2 repeats.
        Brush strip = Box(6, 0.75f, 0.1f, new Vec3(0, 0.375f, 0));
        Face front = FaceToward(strip, new V3(0, 0, 1));
        Assert.True(UvAlignOps.FitLocal(strip.Geometry, front, UvFitAxes.V, 1, 1, 256, 64));
        Assert.Equal(1, front.Vertices.Max(v => v.TextureCoords.V) - front.Vertices.Min(v => v.TextureCoords.V), 4);
        Assert.Equal(2, front.Vertices.Max(v => v.TextureCoords.U) - front.Vertices.Min(v => v.TextureCoords.U), 4);

        // Fitted in U with 2 repeats on a 0.4 m post of a 128x128 texture: 0.2 m per tile on both axes.
        Brush post = Box(0.4f, 3, 0.15f);
        Face face = FaceToward(post, new V3(0, 0, 1));
        Assert.True(UvAlignOps.FitLocal(post.Geometry, face, UvFitAxes.U, 2, 1, 128, 128));
        Assert.Equal(2, face.Vertices.Max(v => v.TextureCoords.U), 4);
        Assert.Equal(15, face.Vertices.Max(v => v.TextureCoords.V), 3);
    }

    // ---- texture sizes ------------------------------------------------------------------------------------

    [Fact]
    public void TextureSizeLookup_Is_Unknown_Without_A_Library_And_Reads_Real_Sizes_With_One()
    {
        var none = new TextureSizeLookup(null);
        Assert.False(none.Available);
        Assert.Null(none.Get("rck_default.tga"));
        Assert.Equal((256, 256), none.GetOrDefault("rck_default.tga", (256, 256)));

        if (TestPaths.RfInstall is not { } install)
        {
            return; // no RF install: the mounted half is skipped
        }

        using AssetVfs vfs = GameMount.Mount(install);
        var sizes = new TextureSizeLookup(vfs);
        Assert.True(sizes.Available);
        Assert.Equal((32, 32), sizes.Get("rck_default.tga"));          // the editor's default texture
        Assert.Equal((256, 256), sizes.Get("mtl_outerwall02.tga"));
        Assert.Equal((128, 128), sizes.Get("mtl_hazardstripe01.tga"));
        Assert.Null(sizes.Get("no_such_texture_q2.tga"));
    }
}
