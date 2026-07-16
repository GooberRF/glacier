using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Offscreen-render evidence for the new decal/arrow visuals: a floor with a selected decal that
/// shows both its facing face (semi-transparent orange quad) and its projected texture ("Draw
/// Decals" on), plus an MP respawn facing arrow. Writes a PNG artifact and asserts it is
/// non-trivial. Skips gracefully when no GPU device is available.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class DecalAndArrowRenderTests
{
    [Fact]
    public void Decal_Face_Projection_And_Arrow_RenderArtifact()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        RflFile file = Level();
        var options = new SceneBuildOptions
        {
            DrawDecals = true,
            SelectedDecalUids = new HashSet<int> { 3 }, // only the floating decal is selected → its facing face
            EventFacingArrows = true,
        };
        RenderScene scene = SceneBuilder.Build(file, options);

        // Sanity: all three new visuals made it into the scene.
        Assert.Contains(scene.Batches, b => b.Pass == RenderPass.Alpha && b.TextureName == "decalproj.tga"); // projection
        Assert.Contains(scene.Batches, b => b.Pass == RenderPass.Alpha && b.TextureName.Length == 0 && b.Vertices.Count == 4); // facing face
        Assert.NotEmpty(scene.Lines); // respawn arrow

        var camera = new Camera { Position = new Vector3(4, 5, -6) };
        camera.LookAt(new Vector3(4, 5, -6), new Vector3(0, 0.5f, 0));
        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, 640, 480);

        Assert.Equal(640 * 480 * 4, pixels.Length);
        Assert.True(RenderTestSupport.IsNonTrivial(pixels, out int distinct), $"trivial image ({distinct} colors).");
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "decal_face_projection_arrow.png"), PngWriter.Encode(640, 480, pixels));
    }

    private static RflFile Level()
    {
        var rfl = new RflFile();

        // A floor to receive the projection.
        var geo = new Geometry();
        geo.Textures.Add("floor.tga");
        geo.Vertices.Add(new Vec3(-4, 0, -4));
        geo.Vertices.Add(new Vec3(4, 0, -4));
        geo.Vertices.Add(new Vec3(4, 0, 4));
        geo.Vertices.Add(new Vec3(-4, 0, 4));
        geo.Faces.Add(new Face
        {
            Texture = 0, SurfaceIndex = -1, RoomIndex = 0, Plane = new RfPlane(new Vec3(0, 1, 0), 0f),
            Vertices = new List<FaceVertex>
            {
                new() { Index = 0, TextureCoords = new Uv(0, 1) },
                new() { Index = 1, TextureCoords = new Uv(1, 1) },
                new() { Index = 2, TextureCoords = new Uv(1, 0) },
                new() { Index = 3, TextureCoords = new Uv(0, 0) },
            },
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.StaticGeometry, Array.Empty<byte>())
        {
            Content = new GeometrySection { Geometry = geo },
            Dirty = true,
        });

        var decals = new DecalsSection();

        // Decal 3 (SELECTED): floats, aiming straight up — no ceiling to project onto, so its
        // facing face shows on its own as a distinct semi-transparent orange quad (the +up face).
        decals.Decals.Add(new Decal
        {
            Header = new ObjectHeader
            {
                Uid = 3, Position = new Vec3(-3, 3, -1),
                Rotation = new Mat3(new Vec3(0, 1, 0), new Vec3(1, 0, 0), new Vec3(0, 0, 1)),
            },
            Extents = new Vec3(2.5f, 2.5f, 1),
            Texture = "facedecal.tga",
        });

        // Decal 4: aims straight down at the floor → its texture is projected onto the floor.
        decals.Decals.Add(new Decal
        {
            Header = new ObjectHeader
            {
                Uid = 4, Position = new Vec3(1.5f, 2, 0),
                Rotation = new Mat3(new Vec3(0, -1, 0), new Vec3(1, 0, 0), new Vec3(0, 0, 1)),
            },
            Extents = new Vec3(4, 4, 6),
            Texture = "decalproj.tga",
            Alpha = 255,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.Decals, Array.Empty<byte>()) { Content = decals, Dirty = true });

        // An MP respawn with a facing arrow.
        var respawns = new MpRespawnPointsSection();
        respawns.Points.Add(new MpRespawnPoint { Uid = 10, Position = new Vec3(0, 0.2f, 3.5f), Rotation = Mat3.Identity });
        rfl.Sections.Add(new RflSection((uint)SectionType.MpRespawnPoints, Array.Empty<byte>()) { Content = respawns, Dirty = true });

        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }
}
