using System;
using System.Collections.Generic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// CPU-only model regressions (no GPU):
/// (item) portal fragments in the brush overlay must be classified from the COMPILED
/// fragment, not the authored face — otherwise a community-map portal (real texture +
/// portal_index_plus_2) leaks through as an opaque textured quad under Portal Faces = None;
/// (item) show_sky faces render as the semitransparent sky-blue "SHOW SKY" editor aid in
/// both the compiled and overlay emitters.
/// </summary>
public sealed class SkyAndPortalFragmentTests
{
    private static Brush TexturedBox(int uid) => new()
    {
        Uid = uid,
        Rotation = Mat3.Identity,
        Geometry = Ged.Core.Editing.BrushFactory.Box(2, 2, 2, 0, 0, 0, "wall.tga"),
    };

    /// <summary>
    /// A fragment index whose single compiled fragment carries a REAL texture but a portal
    /// marker (portal_index_plus_2 >= 2) — mapped to authored face 0 of <paramref name="uid"/>,
    /// whose authored polygon is a plain textured (non-portal) face.
    /// </summary>
    private static BrushFragmentIndex PortalFragmentIndexFor(int uid)
    {
        var fg = new Geometry();
        fg.Textures.Add("wall.tga");
        fg.Vertices.Add(new Vec3(-1, -1, 1));
        fg.Vertices.Add(new Vec3(1, -1, 1));
        fg.Vertices.Add(new Vec3(1, 1, 1));
        fg.Vertices.Add(new Vec3(-1, 1, 1));
        fg.Faces.Add(new Face
        {
            Texture = 0,          // valid texture, NOT the -1 sentinel
            PortalIndexPlus2 = 2, // → IsPortalFace true on the COMPILED fragment
            FaceId = 100,
            SurfaceIndex = -1,
            Plane = new RfPlane(new Vec3(0, 0, 1), 1),
            Vertices =
            {
                new FaceVertex { Index = 0 }, new FaceVertex { Index = 1 },
                new FaceVertex { Index = 2 }, new FaceVertex { Index = 3 },
            },
        });

        var start = new Dictionary<int, int> { [uid] = 100 };
        var survival = new Dictionary<int, bool[]> { [uid] = new bool[6] }; // 6 authored faces
        return BrushFragmentIndex.Build(fg, start, survival);
    }

    [Fact]
    public void Portal_Fragment_Is_Hidden_Not_Opaque_When_PortalFaces_None()
    {
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { TexturedBox(2) }, BrushPickGranularity.Face,
            solidFill: true, survivingFragments: PortalFragmentIndexFor(2),
            portalFaces: PortalFaceDrawMode.None);

        // The portal fragment must contribute NO solid triangle and NO textured batch —
        // the old code read IsPortalFace off the authored (non-portal) face and drew it opaque.
        Assert.Equal(0, scene.TotalTriangleCount);
        Assert.DoesNotContain(scene.Batches, b => b.TextureName == "wall.tga");
    }

    [Fact]
    public void Portal_Fragment_Is_An_Alpha_Portal_Batch_When_SeeThru()
    {
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { TexturedBox(2) }, BrushPickGranularity.Face,
            solidFill: true, survivingFragments: PortalFragmentIndexFor(2),
            portalFaces: PortalFaceDrawMode.SeeThru);

        GeometryBatch portal = Assert.Single(scene.Batches, b => b.IsPortal);
        Assert.Equal(RenderPass.Alpha, portal.Pass);
        Assert.Equal(string.Empty, portal.TextureName); // flat quad, texture dropped
    }

    // ---- show_sky editor aid (overlay path) -----------------------------------

    [Fact]
    public void ShowSky_Overlay_Face_Binds_The_Baked_Sky_Diffuse_When_Aid_On()
    {
        Brush box = TexturedBox(3);
        box.Geometry.Faces[0].Flags |= (ushort)FaceFlags.ShowSky;

        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { box }, BrushPickGranularity.Brush, solidFill: true, skyFaceAid: true);

        GeometryBatch sky = Assert.Single(scene.Batches, b => b.IsSky);
        Assert.Equal(RenderPass.Alpha, sky.Pass);
        Assert.Equal(SkyFaceAid.TextureKey, sky.TextureName); // baked "SHOW SKY" diffuse, mapped by face UVs
        Assert.True(scene.InlineTextures.ContainsKey(SkyFaceAid.TextureKey), "the baked SHOW SKY texture is present");
    }

    [Fact]
    public void ShowSky_Overlay_Face_Is_Plain_When_Aid_Off()
    {
        Brush box = TexturedBox(3);
        box.Geometry.Faces[0].Flags |= (ushort)FaceFlags.ShowSky;

        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { box }, BrushPickGranularity.Brush, solidFill: true, skyFaceAid: false);

        Assert.DoesNotContain(scene.Batches, b => b.IsSky);
        Assert.False(scene.InlineTextures.ContainsKey(SkyFaceAid.TextureKey));
    }

    [Fact]
    public void Baked_Sky_Texture_Is_Semitransparent_With_Opaque_Label_Pixels()
    {
        InlineTexture tex = SkyFaceAid.BuildTexture();
        bool anyFill = false, anyGlyph = false;
        for (int i = 0; i < tex.Width * tex.Height; i++)
        {
            byte a = tex.Rgba[(i * 4) + 3];
            if (a > 0 && a < 200)
            {
                anyFill = true; // the semitransparent sky-blue fill
            }
            else if (a == 255)
            {
                anyGlyph = true; // the opaque white "SHOW SKY" glyph pixels
            }
        }

        Assert.True(anyFill, "the texture must have a semitransparent fill");
        Assert.True(anyGlyph, "the texture must have opaque label glyph pixels baked in");
    }

    // ---- show_sky editor aid (compiled path) ----------------------------------

    private static RflFile SkyGeometryRfl()
    {
        var geo = new Geometry();
        geo.Textures.Add("wall.tga");
        geo.Vertices.Add(new Vec3(-3, -3, 0));
        geo.Vertices.Add(new Vec3(3, -3, 0));
        geo.Vertices.Add(new Vec3(3, 3, 0));
        geo.Vertices.Add(new Vec3(-3, 3, 0));
        geo.Faces.Add(new Face
        {
            Texture = 0,
            SurfaceIndex = -1,
            RoomIndex = 0,
            Flags = (ushort)FaceFlags.ShowSky,
            Plane = new RfPlane(new Vec3(0, 0, -1), 0),
            Vertices =
            {
                new FaceVertex { Index = 0 }, new FaceVertex { Index = 1 },
                new FaceVertex { Index = 2 }, new FaceVertex { Index = 3 },
            },
        });

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "skytest";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        rfl.Sections.Insert(0, new RflSection((uint)SectionType.StaticGeometry, Array.Empty<byte>())
        {
            Content = new GeometrySection { Geometry = geo },
            Dirty = true,
        });
        return rfl;
    }

    [Fact]
    public void Compiled_ShowSky_Face_Is_Sky_Aid_When_Enabled()
    {
        RenderScene scene = SceneBuilder.Build(SkyGeometryRfl(),
            new SceneBuildOptions { IncludeMovers = false, IncludeObjects = false, ShowSkyFaceAid = true });

        GeometryBatch sky = Assert.Single(scene.Batches, b => b.IsSky);
        Assert.Equal(RenderPass.Alpha, sky.Pass);
        Assert.Equal(SkyFaceAid.TextureKey, sky.TextureName);
        Assert.True(scene.InlineTextures.ContainsKey(SkyFaceAid.TextureKey));
    }

    [Fact]
    public void Compiled_ShowSky_Face_Keeps_Its_Texture_In_The_Sky_Pass_When_Disabled()
    {
        RenderScene scene = SceneBuilder.Build(SkyGeometryRfl(),
            new SceneBuildOptions { IncludeMovers = false, IncludeObjects = false, ShowSkyFaceAid = false });

        Assert.DoesNotContain(scene.Batches, b => b.IsSky);
        Assert.Contains(scene.Batches, b => b.Pass == RenderPass.Sky && b.TextureName == "wall.tga");
    }
}
