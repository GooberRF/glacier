using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Texture mode: UV projection mapping (box/planar/cylinder), whole-face and
/// 2D unwrap UV transforms, per-face properties, default-texture-by-orientation,
/// selection memory and same-texture selection.
/// </summary>
public sealed class TextureModeTests
{
    private const float Eps = 1e-3f;

    private static Geometry Box2() =>
        BrushFactory.Box(2, 2, 2, 0, 0, 0, "test.tga");

    private static Face FaceWithNormal(Geometry g, Vec3 n) =>
        g.Faces.First(f => f.Plane.Normal.ApproxEquals(n, 1e-2f));

    // ---- Box map --------------------------------------------------------------

    [Fact]
    public void BoxMap_ZFace_Projects_XY_At_PixelsPerMeter()
    {
        Geometry g = Box2();
        Face top = FaceWithNormal(g, new Vec3(0, 0, 1)); // +Z, maps (X,Y)
        UvOps.BoxMap(g, top, pixelsPerMeter: 256f, texWidthPx: 256, texHeightPx: 256); // scale 1 tile/m

        foreach (FaceVertex fv in top.Vertices)
        {
            Vec3 p = g.Vertices[fv.Index];
            Assert.Equal(p.X, fv.TextureCoords.U, 3);
            Assert.Equal(-p.Y, fv.TextureCoords.V, 3); // V is negated
        }
    }

    [Fact]
    public void BoxMap_PixelsPerMeter_Scales_Uv_Linearly()
    {
        Geometry g = Box2();
        Face top = FaceWithNormal(g, new Vec3(0, 0, 1));
        // 128 px/m on a 256 px texture => half a tile per metre.
        UvOps.BoxMap(g, top, pixelsPerMeter: 128f, texWidthPx: 256, texHeightPx: 256);
        FaceVertex corner = top.Vertices.First(v => g.Vertices[v.Index].ApproxEquals(new Vec3(1, 1, 1)));
        Assert.Equal(0.5f, corner.TextureCoords.U, 3);
        Assert.Equal(-0.5f, corner.TextureCoords.V, 3);
    }

    [Fact]
    public void BoxMap_XFace_Projects_ZY()
    {
        Geometry g = Box2();
        Face side = FaceWithNormal(g, new Vec3(1, 0, 0)); // +X, maps (Z,Y)
        UvOps.BoxMap(g, side, 256f, 256, 256);
        foreach (FaceVertex fv in side.Vertices)
        {
            Vec3 p = g.Vertices[fv.Index];
            Assert.Equal(p.Z, fv.TextureCoords.U, 3);
            Assert.Equal(-p.Y, fv.TextureCoords.V, 3);
        }
    }

    [Fact]
    public void BoxMap_NonSquareTexture_Uses_PerAxis_Scale()
    {
        Geometry g = Box2();
        Face top = FaceWithNormal(g, new Vec3(0, 0, 1));
        UvOps.BoxMap(g, top, pixelsPerMeter: 256f, texWidthPx: 256, texHeightPx: 128);
        FaceVertex corner = top.Vertices.First(v => g.Vertices[v.Index].ApproxEquals(new Vec3(1, 1, 1)));
        Assert.Equal(1f, corner.TextureCoords.U, 3);   // 256/256
        Assert.Equal(-2f, corner.TextureCoords.V, 3);  // 256/128
    }

    // ---- Planar map -----------------------------------------------------------

    [Fact]
    public void PlanarMap_Shares_One_Projection_Across_Faces()
    {
        // Two faces at different normals but planar map keeps a common X/Y projection.
        Geometry g = Box2();
        Face top = FaceWithNormal(g, new Vec3(0, 0, 1));
        int topIdx = g.Faces.IndexOf(top);
        Face side = FaceWithNormal(g, new Vec3(1, 0, 0));
        int sideIdx = g.Faces.IndexOf(side);

        UvOps.PlanarMap(g, new[] { topIdx, sideIdx }, new Vec3(0, 0, 1), 256f, 256, 256);

        // Both faces now map (X,Y): a vertex shared on the +X/+Z edge has identical UV.
        foreach (Face f in new[] { top, side })
        {
            foreach (FaceVertex fv in f.Vertices)
            {
                Vec3 p = g.Vertices[fv.Index];
                Assert.Equal(p.X, fv.TextureCoords.U, 3);
                Assert.Equal(-p.Y, fv.TextureCoords.V, 3);
            }
        }
    }

    // ---- Cylinder map ---------------------------------------------------------

    [Fact]
    public void CylinderMap_Wraps_Angle_Times_Radius()
    {
        var g = new Geometry();
        g.Vertices.Add(new Vec3(2, 0, 0));  // angle atan2(X=2, Z=0) = pi/2, radius 2
        g.Vertices.Add(new Vec3(0, 0, 2));  // angle atan2(X=0, Z=2) = 0,    radius 2
        g.Vertices.Add(new Vec3(0, 3, 2));
        g.Vertices.Add(new Vec3(2, 3, 0));
        var f = new Face();
        for (int i = 0; i < 4; i++)
        {
            f.Vertices.Add(new FaceVertex { Index = i });
        }

        g.Faces.Add(f);

        UvOps.CylinderMap(g, f, axis: 1, pixelsPerMeter: 256f, texWidthPx: 256, texHeightPx: 256);
        Assert.Equal(MathF.PI, f.Vertices[0].TextureCoords.U, 2); // pi/2 * radius 2
        Assert.Equal(0f, f.Vertices[1].TextureCoords.U, 3);
        Assert.Equal(0f, f.Vertices[0].TextureCoords.V, 3);       // -Y=0
        Assert.Equal(-3f, f.Vertices[2].TextureCoords.V, 3);      // -Y=-3
    }

    // ---- World-space projection (rotated / positioned brushes) ----------------

    // A simple non-axis-aligned quad; the world-space checks below rely only on its corner positions
    // (box/planar/cylinder do not read this quad's plane winding).
    private static Geometry BuildQuad()
    {
        var g = new Geometry();
        g.Vertices.Add(new Vec3(1, 0, 0));
        g.Vertices.Add(new Vec3(0, 0, 1));
        g.Vertices.Add(new Vec3(0, 2, 1));
        g.Vertices.Add(new Vec3(1, 2, 0));
        var f = new Face();
        for (int i = 0; i < 4; i++)
        {
            f.Vertices.Add(new FaceVertex { Index = i });
        }

        g.Faces.Add(f);
        return g;
    }

    private static Geometry Baked(Mat3 rot, Vec3 pos)
    {
        Geometry g = BuildQuad();
        for (int i = 0; i < g.Vertices.Count; i++)
        {
            g.Vertices[i] = pos.Add(rot.Transform(g.Vertices[i]));
        }

        return g;
    }

    [Fact]
    public void BoxMap_IdentityTransform_Is_ByteIdentical_To_The_NoTransform_Overload()
    {
        // The production path now always threads a brush transform; for an un-rotated brush that is the
        // identity, and the result must be bit-for-bit what the plain (world == local) overload produced.
        Geometry g1 = Box2();
        Geometry g2 = Box2();
        foreach (Vec3 n in new[] { new Vec3(0, 0, 1), new Vec3(1, 0, 0), new Vec3(0, 1, 0) })
        {
            Face a = FaceWithNormal(g1, n);
            Face b = FaceWithNormal(g2, n);
            UvOps.BoxMap(g1, a, 200f, 256, 128);                          // legacy overload
            UvOps.BoxMap(g2, b, Mat3.Identity, Vec3.Zero, 200f, 256, 128); // new overload, identity
            for (int i = 0; i < a.Vertices.Count; i++)
            {
                Assert.Equal(
                    BitConverter.SingleToInt32Bits(a.Vertices[i].TextureCoords.U),
                    BitConverter.SingleToInt32Bits(b.Vertices[i].TextureCoords.U));
                Assert.Equal(
                    BitConverter.SingleToInt32Bits(a.Vertices[i].TextureCoords.V),
                    BitConverter.SingleToInt32Bits(b.Vertices[i].TextureCoords.V));
            }
        }
    }

    [Fact]
    public void BoxMap_RotatedAndTranslatedBrush_Projects_World_Positions_On_The_World_Axis()
    {
        // Rotate a box 90° about Y and shift it: the local +Z face becomes a world +X face, so the
        // dominant projection axis and the projected coordinates are WORLD, not brush-local.
        Geometry g = Box2();
        Mat3 rot = Mat3Math.RotationY(MathF.PI / 2f);
        Vec3 pos = new(10, 2, -3);
        Face local = FaceWithNormal(g, new Vec3(0, 0, 1));

        UvOps.BoxMap(g, local, rot, pos, 256f, 256, 256); // scale 1 tile/m

        Vec3 worldNormal = rot.Transform(new Vec3(0, 0, 1));
        (int uAxis, int vAxis) = GeometryUtil.DominantProjection(worldNormal);
        foreach (FaceVertex fv in local.Vertices)
        {
            Vec3 world = pos.Add(rot.Transform(g.Vertices[fv.Index]));
            Assert.Equal(world.Component(uAxis), fv.TextureCoords.U, 3);
            Assert.Equal(-world.Component(vAxis), fv.TextureCoords.V, 3);
        }
    }

    [Fact]
    public void BoxMap_CoplanarFaces_On_Rotated_And_Unrotated_Brushes_Tile_Continuously()
    {
        // Owner scenario: brush B rotated 90° sits adjacent to brush A. Their top faces are coplanar
        // (world y = 1, +Y) and share the seam edge at world x = 2. Box-mapping each in world space maps
        // them in the same direction with a continuous, corner-exact seam.
        Geometry a = Box2();
        Mat3 aRot = Mat3.Identity;
        Vec3 aPos = new(1, 0, 0);                       // world x in [0, 2]
        Geometry b = Box2();
        Mat3 bRot = Mat3Math.RotationY(MathF.PI / 2f);  // rotation about Y keeps the +Y top face +Y
        Vec3 bPos = new(3, 0, 0);                       // world x in [2, 4]

        Face aTop = FaceWithNormal(a, new Vec3(0, 1, 0));
        Face bTop = FaceWithNormal(b, new Vec3(0, 1, 0));

        UvOps.BoxMap(a, aTop, aRot, aPos, 256f, 256, 256);
        UvOps.BoxMap(b, bTop, bRot, bPos, 256f, 256, 256);

        // World +Y face maps (X, Z): U = worldX, V = -worldZ at unit scale — for BOTH brushes.
        AssertBoxWorldProjection(a, aTop, aRot, aPos);
        AssertBoxWorldProjection(b, bTop, bRot, bPos);

        // Corner-exact seam: the shared world corners (2, 1, ±1) get identical UVs on both faces.
        foreach (Vec3 seam in new[] { new Vec3(2, 1, -1), new Vec3(2, 1, 1) })
        {
            Uv ua = WorldCornerUv(a, aTop, aRot, aPos, seam);
            Uv ub = WorldCornerUv(b, bTop, bRot, bPos, seam);
            Assert.Equal(ua.U, ub.U, 3);
            Assert.Equal(ua.V, ub.V, 3);
            Assert.Equal(2f, ua.U, 3); // U tracks world X across the seam
        }
    }

    private static void AssertBoxWorldProjection(Geometry g, Face f, Mat3 rot, Vec3 pos)
    {
        foreach (FaceVertex fv in f.Vertices)
        {
            Vec3 w = pos.Add(rot.Transform(g.Vertices[fv.Index]));
            Assert.Equal(w.X, fv.TextureCoords.U, 3);
            Assert.Equal(-w.Z, fv.TextureCoords.V, 3);
        }
    }

    private static Uv WorldCornerUv(Geometry g, Face f, Mat3 rot, Vec3 pos, Vec3 world) =>
        f.Vertices.First(fv => pos.Add(rot.Transform(g.Vertices[fv.Index])).ApproxEquals(world, 1e-3f)).TextureCoords;

    [Fact]
    public void PlanarMap_With_Transform_Projects_World_Positions()
    {
        // Mapping a brush-local face under a transform equals mapping the same face pre-baked into world
        // space with no transform (both share the world reference normal) — proof the projection is world.
        Mat3 rot = Mat3Math.RotationZ(0.6f);
        Vec3 pos = new(2, -1, 4);
        Vec3 refN = new(0, 0, 1);

        Geometry local = BuildQuad();
        Geometry world = Baked(rot, pos);
        UvOps.PlanarMap(local, new[] { 0 }, rot, pos, refN, 256f, 256, 256);
        UvOps.PlanarMap(world, new[] { 0 }, refN, 256f, 256, 256);

        for (int i = 0; i < local.Faces[0].Vertices.Count; i++)
        {
            Assert.Equal(world.Faces[0].Vertices[i].TextureCoords.U, local.Faces[0].Vertices[i].TextureCoords.U, 4);
            Assert.Equal(world.Faces[0].Vertices[i].TextureCoords.V, local.Faces[0].Vertices[i].TextureCoords.V, 4);
        }
    }

    [Fact]
    public void CylinderMap_With_Transform_Projects_World_Positions()
    {
        Mat3 rot = Mat3Math.RotationY(0.8f);
        Vec3 pos = new(3, 1, -2);

        Geometry local = BuildQuad();
        Geometry world = Baked(rot, pos);
        UvOps.CylinderMap(local, local.Faces[0], rot, pos, axis: 1, 256f, 256, 256);
        UvOps.CylinderMap(world, world.Faces[0], axis: 1, 256f, 256, 256);

        for (int i = 0; i < local.Faces[0].Vertices.Count; i++)
        {
            Assert.Equal(world.Faces[0].Vertices[i].TextureCoords.U, local.Faces[0].Vertices[i].TextureCoords.U, 4);
            Assert.Equal(world.Faces[0].Vertices[i].TextureCoords.V, local.Faces[0].Vertices[i].TextureCoords.V, 4);
        }
    }

    // ---- Whole-face UV edits --------------------------------------------------

    [Fact]
    public void FlipU_Mirrors_About_Centroid()
    {
        Geometry g = Box2();
        Face top = FaceWithNormal(g, new Vec3(0, 0, 1));
        UvOps.BoxMap(g, top, 256f, 256, 256); // centroid (0,0)
        UvOps.FlipU(top);
        foreach (FaceVertex fv in top.Vertices)
        {
            Vec3 p = g.Vertices[fv.Index];
            Assert.Equal(-p.X, fv.TextureCoords.U, 3);
        }
    }

    [Fact]
    public void Scale_And_Snap_Behave()
    {
        Geometry g = Box2();
        Face top = FaceWithNormal(g, new Vec3(0, 0, 1));
        UvOps.BoxMap(g, top, 256f, 256, 256); // corners at (+-1,+-1), centroid (0,0)
        UvOps.Scale(top, 2f, 2f);
        FaceVertex c = top.Vertices.First(v => g.Vertices[v.Index].ApproxEquals(new Vec3(1, 1, 1)));
        Assert.Equal(2f, c.TextureCoords.U, 3);
        Assert.Equal(-2f, c.TextureCoords.V, 3);

        UvOps.Offset(top, 0.1f, 0f);
        UvOps.SnapToGrid(top, 1f);
        Assert.Equal(2f, c.TextureCoords.U, 3); // 2.1 -> 2
    }

    [Fact]
    public void Copy_Paste_Transfers_Uvs()
    {
        Geometry g = Box2();
        Face top = FaceWithNormal(g, new Vec3(0, 0, 1));
        Face bottom = FaceWithNormal(g, new Vec3(0, 0, -1));
        UvOps.BoxMap(g, top, 256f, 256, 256);
        Uv[] copied = UvOps.Copy(top);
        Assert.True(UvOps.Paste(bottom, copied));
        Assert.Equal(top.Vertices.Count, bottom.Vertices.Count);
        for (int i = 0; i < bottom.Vertices.Count; i++)
        {
            Assert.Equal(copied[i], bottom.Vertices[i].TextureCoords);
        }
    }

    // ---- Unwrap (2D UV-set) transforms ----------------------------------------

    [Fact]
    public void Unwrap_Rotate_90_Maps_U_To_V()
    {
        var uvs = new List<Uv> { new(0, 0), new(1, 0) }; // centroid (0.5,0)
        UnwrapOps.Rotate(uvs, new[] { 0, 1 }, 90f);
        // (1,0) about centroid (0.5,0): offset (0.5,0) -> (0,0.5) -> (0.5,0.5)
        Assert.Equal(0.5f, uvs[1].U, 3);
        Assert.Equal(0.5f, uvs[1].V, 3);
    }

    [Fact]
    public void Unwrap_Move_Scale_Flip_Align()
    {
        var uvs = new List<Uv> { new(0, 0), new(2, 4) };
        var all = new[] { 0, 1 };

        UnwrapOps.Move(uvs, all, 1f, 1f);
        Assert.Equal(new Uv(1, 1), uvs[0]);
        Assert.Equal(new Uv(3, 5), uvs[1]);

        UnwrapOps.Scale(uvs, all, 2f, 1f); // centroid (2,3)
        Assert.Equal(0f, uvs[0].U, 3); // 1 -> 2+(1-2)*2 = 0
        Assert.Equal(4f, uvs[1].U, 3); // 3 -> 2+(3-2)*2 = 4

        UnwrapOps.AlignV(uvs, all);
        Assert.Equal(uvs[0].V, uvs[1].V, 3); // shared minimum V

        UnwrapOps.FlipU(uvs, all);
        // Flip about centroid preserves the centroid U.
        Uv centroid = UnwrapOps.Centroid(uvs, all);
        Assert.Equal(2f, centroid.U, 3);
    }

    // ---- Per-face properties --------------------------------------------------

    /// <summary>
    /// Item 0f/0h: the shared face inspector (Properties panel + Face mode's Texture/UV tab)
    /// edits every user-facing face flag, and multi-select mixed values are detectable via the
    /// same FaceProps getters the controls use for their tri-state checkboxes.
    /// </summary>
    [Fact]
    public void Face_Inspector_Metadata_Is_Complete_And_Mixed_Values_Detectable()
    {
        // The exact flag set both face-editing surfaces expose.
        FaceFlags[] inspectorFlags =
        {
            FaceFlags.FullBright, FaceFlags.HasAlpha, FaceFlags.HasHoles, FaceFlags.IsInvisible,
            FaceFlags.ShowSky, FaceFlags.Mirrored, FaceFlags.LiquidSurface, FaceFlags.IsDetail,
        };

        var f0 = new Face();
        var f1 = new Face();

        // Every flag round-trips through the shared getter/setter (completeness).
        foreach (FaceFlags flag in inspectorFlags)
        {
            FaceProps.Set(f0, flag, true);
            Assert.True(FaceProps.Get(f0, flag));
            FaceProps.Set(f0, flag, false);
            Assert.False(FaceProps.Get(f0, flag));
        }

        // Mixed-value detection: one face has ShowSky, the other doesn't → the tri-state
        // "all/none" computation the controls use resolves to indeterminate (mixed).
        FaceProps.Set(f0, FaceFlags.ShowSky, true);
        var faces = new[] { f0, f1 };
        bool all = faces.All(x => FaceProps.Get(x, FaceFlags.ShowSky));
        bool none = faces.All(x => !FaceProps.Get(x, FaceFlags.ShowSky));
        Assert.False(all);
        Assert.False(none); // indeterminate ⇒ the checkbox shows "mixed"

        // Uniform value ⇒ definite.
        FaceProps.Set(f1, FaceFlags.ShowSky, true);
        Assert.True(faces.All(x => FaceProps.Get(x, FaceFlags.ShowSky)));

        // Lightmap-resolution + scroll mixed detection use the same pattern.
        FaceProps.SetLightmapResolution(f0, 3);
        FaceProps.SetLightmapResolution(f1, 1);
        Assert.True(faces.Select(FaceProps.GetLightmapResolution).Distinct().Count() > 1);
    }

    [Fact]
    public void FaceProps_Flags_And_Lightmap_Resolution_RoundTrip()
    {
        var f = new Face();
        FaceProps.Set(f, FaceFlags.FullBright, true);
        FaceProps.Set(f, FaceFlags.LiquidSurface, true);
        Assert.True(FaceProps.Get(f, FaceFlags.FullBright));
        Assert.True(FaceProps.Get(f, FaceFlags.LiquidSurface));

        FaceProps.SetLightmapResolution(f, 3);
        Assert.Equal(3, FaceProps.GetLightmapResolution(f));
        FaceProps.SetLightmapResolution(f, 1);
        Assert.Equal(1, FaceProps.GetLightmapResolution(f));
        // Setting resolution must not disturb other flag bits.
        Assert.True(FaceProps.Get(f, FaceFlags.FullBright));
    }

    [Fact]
    public void FaceProps_SmoothingGroups_ToggleBits()
    {
        var f = new Face();
        FaceProps.SetSmoothingGroup(f, 0, true);
        FaceProps.SetSmoothingGroup(f, 31, true);
        Assert.True(FaceProps.GetSmoothingGroup(f, 0));
        Assert.True(FaceProps.GetSmoothingGroup(f, 31));
        Assert.False(FaceProps.GetSmoothingGroup(f, 15));
        Assert.Equal(0x80000001u, f.SmoothingGroups);
        FaceProps.SetSmoothingGroup(f, 0, false);
        Assert.Equal(0x80000000u, f.SmoothingGroups);
    }

    [Fact]
    public void FaceProps_Scroll_Adds_Marks_And_Removes()
    {
        var g = new Geometry();
        var f = new Face { FaceId = 7 };
        g.Faces.Add(f);

        FaceProps.SetScroll(g, f, 0.5f, -0.25f);
        Assert.True(FaceProps.Get(f, FaceFlags.ScrollTexture));
        Assert.Single(g.FaceScrollData);
        Assert.Equal(new Uv(0.5f, -0.25f), FaceProps.GetScroll(g, f));

        FaceProps.SetScroll(g, f, 0f, 0f);
        Assert.False(FaceProps.Get(f, FaceFlags.ScrollTexture));
        Assert.Empty(g.FaceScrollData);
    }

    // ---- Default textures by orientation --------------------------------------

    [Fact]
    public void OrientationTextures_Assign_By_Face_Normal()
    {
        Brush b = BrushFactory.Create(
            new BrushCreateParams
            {
                Shape = BrushShape.Box,
                Width = 2,
                Height = 2,
                Depth = 2,
                FloorTexture = "floor.tga",
                WallTexture = "wall.tga",
                CeilingTexture = "ceil.tga",
            },
            uid: 1);

        Geometry g = b.Geometry;
        string TexOf(Vec3 n)
        {
            Face f = FaceWithNormal(g, n);
            return g.Textures[f.Texture];
        }

        Assert.Equal("floor.tga", TexOf(new Vec3(0, 1, 0)));   // up = floor
        Assert.Equal("ceil.tga", TexOf(new Vec3(0, -1, 0)));   // down = ceiling
        Assert.Equal("wall.tga", TexOf(new Vec3(1, 0, 0)));    // vertical = wall
        Assert.Equal("wall.tga", TexOf(new Vec3(0, 0, 1)));
    }

    [Fact]
    public void Blank_Preferences_Still_Texture_New_Brushes_With_The_Stock_Default()
    {
        // Item 3 root cause: a fresh portable settings.cfg has EMPTY texture defaults, so
        // the app hands blank orientation preferences AND a blank current texture to
        // creation. The brush must still come out fully textured (stock rock default),
        // never with an empty / nonexistent texture.
        Brush b = BrushFactory.Create(
            new BrushCreateParams
            {
                Shape = BrushShape.Box,
                Width = 2,
                Height = 2,
                Depth = 2,
                Texture = string.Empty,
                FloorTexture = string.Empty,
                WallTexture = null,
                CeilingTexture = string.Empty,
            },
            uid: 1);

        Assert.NotEmpty(b.Geometry.Textures);
        Assert.All(b.Geometry.Faces, f =>
        {
            Assert.InRange(f.Texture, 0, b.Geometry.Textures.Count - 1);
            Assert.Equal(BrushCreateParams.StockWallTexture, b.Geometry.Textures[f.Texture]);
        });
        Assert.DoesNotContain(b.Geometry.Textures, t => string.IsNullOrEmpty(t));
    }

    [Fact]
    public void Blank_Preferences_Fall_Back_To_The_Single_Authoring_Texture()
    {
        // With orientation preferences blank but a real current texture chosen, every
        // face takes that texture (the single-texture authoring path).
        Brush b = BrushFactory.Create(
            new BrushCreateParams { Shape = BrushShape.Box, Texture = "metal01.tga" },
            uid: 2);

        Assert.All(b.Geometry.Faces, f => Assert.Equal("metal01.tga", b.Geometry.Textures[f.Texture]));
    }

    [Theory]
    [InlineData(BrushShape.Box)]
    [InlineData(BrushShape.Cylinder)]
    [InlineData(BrushShape.Cone)]
    [InlineData(BrushShape.Sphere)]
    [InlineData(BrushShape.Wedge)]
    [InlineData(BrushShape.Face)]
    public void Every_Cookie_Cutter_Shape_Gets_Orientation_Textures_Matching_The_Settings(BrushShape shape)
    {
        // Item 3 (b): every primitive creation path applies the ceiling/wall/floor
        // preferences per face — no path leaves a face untextured or off-preference.
        Brush b = BrushFactory.Create(
            new BrushCreateParams
            {
                Shape = shape,
                Width = 3,
                Height = 3,
                Depth = 3,
                FloorTexture = "floor.tga",
                WallTexture = "wall.tga",
                CeilingTexture = "ceil.tga",
            },
            uid: 3);

        Assert.NotEmpty(b.Geometry.Faces);
        var allowed = new[] { "floor.tga", "wall.tga", "ceil.tga" };
        Assert.All(b.Geometry.Faces, f =>
        {
            Assert.InRange(f.Texture, 0, b.Geometry.Textures.Count - 1);
            Assert.Contains(b.Geometry.Textures[f.Texture], allowed);
        });
    }

    // ---- Selection memory + same-texture --------------------------------------

    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "t.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void ReselectPrevious_Swaps_Selection()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, default, Mat3.Identity);
        int b = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, default, Mat3.Identity);

        ed.SelectBrush(a);
        ed.SelectBrush(b); // replacing selection captures {a} as memory
        Assert.Contains(b, ed.SelectedBrushes);

        ed.ReselectPrevious();
        Assert.Contains(a, ed.SelectedBrushes);
        Assert.DoesNotContain(b, ed.SelectedBrushes);

        ed.ReselectPrevious(); // swap back
        Assert.Contains(b, ed.SelectedBrushes);
    }

    [Fact]
    public void GrowFacesToBrush_Selects_All_Faces_Of_Owning_Brush()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, default, Mat3.Identity);
        ed.SetMode(EditMode.Face);
        ed.SelectFace(a, 0);
        ed.GrowFacesToBrush();
        Assert.Equal(6, ed.SelectedFaces.Count); // all six box faces
    }

    [Fact]
    public void SelectSameTexture_Only_Expands_Within_Brushes_That_Have_A_Selected_Face()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        // Two boxes sharing the same texture, but only brush a has a selected face.
        int a = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Texture = "shared.tga" }, default, Mat3.Identity);
        int b = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Texture = "shared.tga" }, new Vec3(10, 0, 0), Mat3.Identity);

        ed.SetMode(EditMode.Face);
        ed.SelectFace(a, 0);
        ed.SelectSameTexture();

        // Only brush a (which held the selected face) expands to its 6 faces; brush b is
        // untouched even though every one of its faces shares the texture.
        Assert.Equal(6, ed.SelectedFaces.Count);
        Assert.All(ed.SelectedFaces, x => Assert.Equal(a, x.Brush));
        Assert.DoesNotContain(ed.SelectedFaces, x => x.Brush == b);
    }

    [Fact]
    public void SelectSameTexture_Unions_The_Selected_Faces_Textures_Within_The_Brush()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Texture = "a.tga" }, default, Mat3.Identity);
        ed.SetMode(EditMode.Face);

        // Retexture face 3 -> b.tga and face 5 -> c.tga (faces 0,1,2,4 stay a.tga).
        ed.SelectFace(a, 3);
        ed.EditSelectedFaces("Apply b", (g, fi) => g.Faces[fi].Texture = GeometryUtil.EnsureTexture(g, "b.tga"));
        ed.ClearSelection();
        ed.SelectFace(a, 5);
        ed.EditSelectedFaces("Apply c", (g, fi) => g.Faces[fi].Texture = GeometryUtil.EnsureTexture(g, "c.tga"));
        ed.ClearSelection();

        // Select an a.tga face and the b.tga face -> the union of wanted textures is {a, b}.
        ed.SelectFace(a, 0);
        ed.SelectFace(a, 3, additive: true);
        ed.SelectSameTexture();

        // Faces 0,1,2,3,4 match a or b; face 5 (c.tga) is not in the union.
        Assert.Equal(5, ed.SelectedFaces.Count);
        Assert.DoesNotContain(ed.SelectedFaces, x => x.Brush == a && x.Face == 5);
    }

    [Fact]
    public void EditSelectedFaces_Is_Undoable_And_Dirties_Brushes()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Texture = "a.tga" }, default, Mat3.Identity);
        ed.SelectFace(a, 0);
        ed.SelectFace(a, 1, additive: true);

        ed.EditSelectedFaces("Apply texture", (g, fi) =>
            g.Faces[fi].Texture = GeometryUtil.EnsureTexture(g, "b.tga"));

        Brush brush = ed.FindBrush(a)!;
        Assert.Equal("b.tga", brush.Geometry.Textures[brush.Geometry.Faces[0].Texture]);
        Assert.True(doc.IsDirty);

        doc.Undo.Undo();
        Assert.Equal("a.tga", ed.FindBrush(a)!.Geometry.Textures[ed.FindBrush(a)!.Geometry.Faces[0].Texture]);
    }

    [Fact]
    public void TextureEdit_Changes_Only_The_Brushes_Section()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string? path = Corpus.RflFiles.FirstOrDefault(p =>
        {
            RflFile f = RflFile.Load(p);
            f.ParseAllKnownSections();
            return f.Sections.Any(s => s.Content is BrushesSection bs && bs.Brushes.Count > 0);
        });
        if (path is null)
        {
            return;
        }

        var doc = EditorDocument.Open(path);
        var ed = new BrushEditor(doc);
        RflFile original = RflFile.Load(path);
        var before = original.Sections.Select(s => s.RawBytes).ToList();

        // Box-map the first face of the first brush.
        Brush b0 = ed.Brushes[0];
        ed.SetMode(EditMode.Face);
        ed.SelectFace(b0.Uid, 0);
        ed.EditSelectedFaces("Box map", (g, fi) => UvOps.BoxMap(g, g.Faces[fi], 256f, 256, 256));

        byte[] saved = doc.SaveToBytes(updateTimestamp: false);
        RflFile reloaded = RflFile.Load(saved);
        Assert.Equal(before.Count, reloaded.Sections.Count);

        bool brushesChanged = false;
        for (int i = 0; i < reloaded.Sections.Count; i++)
        {
            RflSection sec = reloaded.Sections[i];
            if (sec.TypeId == (uint)SectionType.Brushes)
            {
                brushesChanged = !sec.RawBytes.AsSpan().SequenceEqual(before[i]);
            }
            else
            {
                Assert.True(sec.RawBytes.AsSpan().SequenceEqual(before[i]),
                    $"Section[{i}] 0x{sec.TypeId:X8} changed but should not have.");
            }
        }

        Assert.True(brushesChanged, "The brushes section should have changed.");
    }
}
