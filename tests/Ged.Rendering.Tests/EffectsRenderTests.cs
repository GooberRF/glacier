using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// GPU artifact gates for the live previews + room-graph modes: particle
/// burst, bolt arc, distance fog, portal-culled vs full view, and hole lines.
/// Each renders offscreen, asserts a non-trivial image, and writes a PNG artifact.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class EffectsRenderTests
{
    [Fact]
    public void Particle_Burst_Renders()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        if (RenderTestSupport.FixtureFile("tex", "wallputer1.tga") is null)
        {
            return; // retail-derived fixture not present
        }

        // Bind a distinctive fixture bitmap so the preview shows the *authored*
        // particle art (Task 1b) rather than the generic soft sprite.
        RflFile level = LevelWith(SectionType.ParticleEmitters, new ParticleEmittersSection
        {
            Emitters =
            {
                new ParticleEmitter
                {
                    Header = new ObjectHeader { Uid = 10, Position = new Vec3(0, 0, 6), Rotation = Mat3.Identity },
                    Shape = 2,
                    SphereRadius = 2.5f,
                    SpawnDelay = 0.02f,
                    Velocity = 1.0f,
                    RandomDirection = 1.0f,
                    Decay = 2.0f,
                    ParticleRadius = 0.5f,
                    InitiallyOn = 1,
                    ParticleColor = new RfColor(255, 255, 255, 255),
                    FadeToColor = new RfColor(255, 255, 255, 255),
                    Texture = "wallputer1.tga",
                },
            },
        });

        var scene = new RenderScene();
        EffectsBuilder.Append(level, new EffectsOptions { Time = 1.0f }, scene);
        Assert.NotEmpty(scene.Billboards);
        Assert.All(scene.Billboards, b => Assert.Equal("wallputer1.tga", b.TextureName));

        var sources = new List<IAssetSource>();
        foreach (string dir in RenderTestSupport.FixtureDirs("tex"))
        {
            sources.Add(new DirectoryAssetSource(dir, extensions: SupercedeChain.Extensions));
        }

        using AssetVfs vfs = new(sources.ToArray());

        var camera = new Camera { Position = new Vector3(0, 0, 0) };
        camera.LookAt(new Vector3(0, 0, 0), new Vector3(0, 0, 1));
        byte[] px = OffscreenRenderer.Render(gd, scene, vfs, camera, RenderMode.JustTextures, 640, 480);
        Assert.True(RenderTestSupport.IsNonTrivial(px, out int distinct), $"particles were trivial ({distinct} colors).");
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "effects_particles.png"),
            PngWriter.Encode(640, 480, px));
    }

    [Fact]
    public void Particle_Billboards_Carry_Emitter_Bitmap_Or_Null()
    {
        // Pure (no GPU): the emitter's bitmap flows onto every particle billboard;
        // an emitter with no bitmap yields null so the GPU falls back to the sprite.
        RflFile withTex = LevelWith(SectionType.ParticleEmitters, new ParticleEmittersSection
        {
            Emitters = { NewEmitter("smoke.vbm") },
        });
        RflFile noTex = LevelWith(SectionType.ParticleEmitters, new ParticleEmittersSection
        {
            Emitters = { NewEmitter(string.Empty) },
        });

        var a = new RenderScene();
        EffectsBuilder.Append(withTex, new EffectsOptions { Time = 1.0f }, a);
        Assert.NotEmpty(a.Billboards);
        Assert.All(a.Billboards, b => Assert.Equal("smoke.vbm", b.TextureName));

        var b2 = new RenderScene();
        EffectsBuilder.Append(noTex, new EffectsOptions { Time = 1.0f }, b2);
        Assert.NotEmpty(b2.Billboards);
        Assert.All(b2.Billboards, b => Assert.Null(b.TextureName));
    }

    [Fact]
    public void Liquid_Face_Scroll_Propagates_To_Batch()
    {
        // Pure: a liquid face with a face_scroll_data entry produces a batch carrying
        // that scroll velocity (Task 1c); a non-scrolling face carries zero.
        var level = GeometryLevelWithLiquidScroll(uVel: 3f, vVel: -1.5f);
        RenderScene scene = SceneBuilder.Build(level, new SceneBuildOptions
        {
            IncludeObjects = false,
            IncludeMovers = false,
        });

        GeometryBatch liquid = Assert.Single(scene.Batches, b => b.Pass == RenderPass.Liquid);
        Assert.Equal(3f, liquid.ScrollU);
        Assert.Equal(-1.5f, liquid.ScrollV);
    }

    [Fact]
    public void Liquid_Scroll_Shifts_Sampled_UV_Over_Time()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        // A camera-facing quad textured with a horizontal gradient, scrolling in U.
        // Rendering at t=0 vs t=1 must shift the sampled texture noticeably.
        var scene = new RenderScene();
        var batch = new GeometryBatch("gradient2x2.png", -1, RenderPass.Liquid) { ScrollU = 0.5f };
        void V(float x, float y, float u) => batch.Vertices.Add(new WorldVertex
        {
            Position = new Vector3(x, y, 6f),
            Normal = new Vector3(0, 0, -1),
            TexCoord = new Vector2(u, 0.5f),
            Color = Palette.Rgba(255, 255, 255),
        });
        V(-3f, -3f, 0f);
        V(3f, -3f, 1f);
        V(3f, 3f, 1f);
        V(-3f, 3f, 0f);
        batch.Indices.AddRange(new uint[] { 0, 1, 2, 0, 2, 3 });
        scene.Batches.Add(batch);

        using AssetVfs vfs = new(new IAssetSource[]
        {
            new DirectoryAssetSource(Path.Combine(RenderTestSupport.Fixtures!, "tex"), extensions: SupercedeChain.Extensions),
        });

        var camera = new Camera { Position = new Vector3(0, 0, 0) };
        camera.LookAt(new Vector3(0, 0, 0), new Vector3(0, 0, 1));
        byte[] a = OffscreenRenderer.Render(gd, scene, vfs, camera, RenderMode.JustTextures, 256, 256, time: 0f);
        byte[] b = OffscreenRenderer.Render(gd, scene, vfs, camera, RenderMode.JustTextures, 256, 256, time: 1f);

        long diff = 0;
        for (int i = 0; i + 3 < a.Length; i += 4)
        {
            diff += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        }

        Assert.True(diff > 20000, $"liquid scroll produced no visible shift (diff={diff}).");
    }

    private static RflFile GeometryLevelWithLiquidScroll(float uVel, float vVel)
    {
        var geo = new Geometry();
        geo.Textures.Add("water.tga");
        geo.Vertices.AddRange(new[]
        {
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(0, 1, 0),
        });

        var face = new Face { Texture = 0, FaceId = 7, RoomIndex = 0, Flags = (ushort)FaceFlags.LiquidSurface };
        for (int i = 0; i < 4; i++)
        {
            face.Vertices.Add(new FaceVertex { Index = i, TextureCoords = new Uv(0, 0) });
        }

        geo.Faces.Add(face);
        geo.FaceScrollData.Add(new FaceScrollData { FaceId = 7, UVelocity = uVel, VVelocity = vVel });

        var rfl = NewLevel();
        AddSection(rfl, SectionType.StaticGeometry, new GeometrySection { Geometry = geo });
        return rfl;
    }

    private static ParticleEmitter NewEmitter(string texture) => new()
    {
        Header = new ObjectHeader { Uid = 10, Position = new Vec3(0, 0, 6), Rotation = Mat3.Identity },
        Shape = 2,
        SphereRadius = 2.5f,
        SpawnDelay = 0.02f,
        Velocity = 1.0f,
        RandomDirection = 1.0f,
        Decay = 2.0f,
        ParticleRadius = 0.35f,
        InitiallyOn = 1,
        ParticleColor = new RfColor(255, 180, 60, 255),
        FadeToColor = new RfColor(200, 40, 20, 0),
        Texture = texture,
    };

    [Fact]
    public void Bolt_Arc_Renders()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var level = LevelWith(SectionType.BoltEmitters, new BoltEmittersSection
        {
            Emitters =
            {
                new BoltEmitter
                {
                    Header = new ObjectHeader { Uid = 20, Position = new Vec3(-3, 0, 6) },
                    TargetUid = 21,
                    NumSegments = 12,
                    Jitter = 0.7f,
                    Thickness = 0.1f,
                    InitiallyOn = 1,
                    Color = new RfColor(120, 180, 255, 255),
                },
            },
        });
        AddSection(level, SectionType.Targets, new TargetsSection
        {
            Targets = { new ObjectHeader { Uid = 21, Position = new Vec3(3, 0, 6) } },
        });

        var scene = new RenderScene();
        EffectsBuilder.Append(level, new EffectsOptions { Time = 0.3f }, scene);
        Assert.NotEmpty(scene.Lines);

        byte[] px = RenderFromOrigin(gd, scene);
        Assert.True(ColoredPixels(px) > 100, "bolt arc drew too few pixels.");
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "effects_bolt.png"), PngWriter.Encode(640, 480, px));
    }

    [Fact]
    public void Fog_Changes_The_Image()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        RenderScene scene = DepthWalls();
        var camera = new Camera { Position = new Vector3(0, 0, 0) };
        camera.LookAt(new Vector3(0, 0, 0), new Vector3(0, 0, 1));

        // This exercises fog, not back-face culling; the synthetic DepthWalls fixture is
        // authored for both-sided rendering, so cull is disabled to keep it visible.
        byte[] clear = OffscreenRenderer.Render(gd, scene, null, camera, RenderMode.JustTextures, 640, 480, disableBackfaceCulling: true);
        var fog = FogSettings.FromLevel(new Vector3(0.55f, 0.6f, 0.7f), 40f);
        byte[] foggy = OffscreenRenderer.Render(gd, scene, null, camera, RenderMode.JustTextures, 640, 480, fog, disableBackfaceCulling: true);

        Assert.True(RenderTestSupport.IsNonTrivial(foggy, out _));
        Assert.False(clear.AsSpan().SequenceEqual(foggy), "Fog did not change the image.");
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "effects_fog.png"),
            PngWriter.Encode(640, 480, foggy));
    }

    [Fact]
    public void Portal_Culling_Draws_Fewer_Rooms()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        RflFile level = TwoRoomLevel();

        RenderScene full = SceneBuilder.Build(level, new SceneBuildOptions());
        RenderScene culled = SceneBuilder.Build(level, new SceneBuildOptions { VisibleRooms = new HashSet<int> { 0 } });

        Assert.True(culled.TotalTriangleCount < full.TotalTriangleCount);
        Assert.True(culled.TotalTriangleCount > 0);

        var camera = new Camera { Position = new Vector3(0, 3, -6) };
        camera.LookAt(new Vector3(0, 3, -6), new Vector3(0, 3, 10));

        // Tests room (portal) culling, not back-face culling; the camera sits outside the
        // rooms, so back-face culling is disabled to keep the room shells visible.
        byte[] fullPx = OffscreenRenderer.Render(gd, full, null, camera, RenderMode.JustTextures, 640, 480, disableBackfaceCulling: true);
        byte[] culledPx = OffscreenRenderer.Render(gd, culled, null, camera, RenderMode.JustTextures, 640, 480, disableBackfaceCulling: true);
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "portal_full.png"), PngWriter.Encode(640, 480, fullPx));
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "portal_culled.png"), PngWriter.Encode(640, 480, culledPx));
        Assert.False(fullPx.AsSpan().SequenceEqual(culledPx));
    }

    [Fact]
    public void Sky_Backed_Scene_Renders()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var scene = new RenderScene();

        // A distant sky-pass backdrop (rendered like in-game "Draw Sky").
        var sky = new GeometryBatch("sky", -1, RenderPass.Sky);
        uint skyBlue = Palette.Rgba(90, 130, 210);
        void SkyV(float x, float y) => sky.Vertices.Add(new WorldVertex
        {
            Position = new Vector3(x, y, 60f),
            Normal = new Vector3(0, 0, -1),
            TexCoord = Vector2.Zero,
            LightmapCoord = Vector2.Zero,
            Color = skyBlue,
            PickId = 0,
        });
        SkyV(-60, -60);
        SkyV(60, -60);
        SkyV(60, 60);
        SkyV(-60, 60);
        sky.Indices.AddRange(new uint[] { 0, 1, 2, 0, 2, 3 });
        scene.Batches.Add(sky);

        // A foreground opaque wall in front of the sky.
        var wall = new GeometryBatch("w", -1, RenderPass.Opaque);
        uint tan = Palette.Rgba(200, 170, 120);
        void WallV(float x, float y) => wall.Vertices.Add(new WorldVertex
        {
            Position = new Vector3(x, y, 8f),
            Normal = new Vector3(0, 0, -1),
            TexCoord = Vector2.Zero,
            LightmapCoord = Vector2.Zero,
            Color = tan,
            PickId = 0,
        });
        WallV(-3, -5);
        WallV(3, -5);
        WallV(3, 2);
        WallV(-3, 2);
        wall.Indices.AddRange(new uint[] { 0, 1, 2, 0, 2, 3 });
        scene.Batches.Add(wall);

        // RoomColors so the sky/wall vertex colours show without a VFS (white-textured otherwise).
        var camera = new Camera { Position = new Vector3(0, 0, 0) };
        camera.LookAt(new Vector3(0, 0, 0), new Vector3(0, 0, 1));
        // Exercises the sky pass + foreground wall, not back-face culling; the wall fixture
        // is authored for both-sided rendering, so cull is disabled to keep it visible.
        byte[] px = OffscreenRenderer.Render(gd, scene, null, camera, RenderMode.RoomColors, 640, 480, disableBackfaceCulling: true);
        Assert.True(RenderTestSupport.IsNonTrivial(px, out int distinct), $"sky scene was trivial ({distinct} colors).");
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "effects_sky.png"), PngWriter.Encode(640, 480, px));
    }

    [Fact]
    public void Hole_Lines_Render()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        // A lone triangle -> three open edges the hole detector flags.
        var g = new Geometry();
        g.Vertices.AddRange(new[] { new Vec3(-2, -1, 6), new Vec3(2, -1, 6), new Vec3(0, 2, 6) });
        var face = new Face { Texture = 0 };
        face.Vertices.Add(new FaceVertex { Index = 0 });
        face.Vertices.Add(new FaceVertex { Index = 1 });
        face.Vertices.Add(new FaceVertex { Index = 2 });
        g.Textures.Add("t.tga");
        g.Faces.Add(face);

        List<Vec3> holes = HoleDetector.Detect(g);
        Assert.NotEmpty(holes);

        var scene = new RenderScene();
        uint red = Palette.Rgba(255, 40, 40, 255);
        foreach (Vec3 h in holes)
        {
            var c = new Vector3(h.X, h.Y, h.Z);
            scene.Lines.Add(new LineSegment(c - new Vector3(1.0f, 0, 0), c + new Vector3(1.0f, 0, 0), red));
            scene.Lines.Add(new LineSegment(c - new Vector3(0, 1.0f, 0), c + new Vector3(0, 1.0f, 0), red));
        }

        byte[] px = RenderFromOrigin(gd, scene);
        Assert.True(ColoredPixels(px) > 50, "hole lines drew too few pixels.");
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "hole_lines.png"), PngWriter.Encode(640, 480, px));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static void SaveFromOrigin(GraphicsDevice gd, RenderScene scene, string file)
    {
        byte[] px = RenderFromOrigin(gd, scene);
        Assert.True(RenderTestSupport.IsNonTrivial(px, out int distinct), $"{file} was trivial ({distinct} colors).");
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, file), PngWriter.Encode(640, 480, px));
    }

    private static byte[] RenderFromOrigin(GraphicsDevice gd, RenderScene scene)
    {
        var camera = new Camera { Position = new Vector3(0, 0, 0) };
        camera.LookAt(new Vector3(0, 0, 0), new Vector3(0, 0, 1));
        return OffscreenRenderer.Render(gd, scene, null, camera, RenderMode.JustTextures, 640, 480);
    }

    /// <summary>Counts pixels that differ noticeably from the dark clear colour (26,28,33).</summary>
    private static int ColoredPixels(byte[] rgba)
    {
        int count = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int d = Math.Abs(rgba[i] - 26) + Math.Abs(rgba[i + 1] - 28) + Math.Abs(rgba[i + 2] - 33);
            if (d > 60)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>A backdrop of three quads at increasing depth for the fog gradient.</summary>
    private static RenderScene DepthWalls()
    {
        var scene = new RenderScene();
        foreach ((float z, byte tone) in new[] { (8f, (byte)220), (20f, (byte)200), (35f, (byte)180) })
        {
            var batch = new GeometryBatch("w", -1, RenderPass.Opaque);
            uint col = Palette.Rgba(tone, tone, tone);
            void V(float x, float y) => batch.Vertices.Add(new WorldVertex
            {
                Position = new Vector3(x, y, z),
                Normal = new Vector3(0, 0, -1),
                TexCoord = Vector2.Zero,
                LightmapCoord = Vector2.Zero,
                Color = col,
                PickId = 0,
            });
            V(-8, -8);
            V(8, -8);
            V(8, 8);
            V(-8, 8);
            batch.Indices.AddRange(new uint[] { 0, 1, 2, 0, 2, 3 });
            scene.Batches.Add(batch);
        }

        return scene;
    }

    private static RflFile TwoRoomLevel()
    {
        var g = new Geometry();
        g.Textures.Add("a.tga");
        g.Rooms.Add(new Room { Id = 1, Aabb = new Aabb(new Vec3(-8, 0, 0), new Vec3(0, 6, 8)) });
        g.Rooms.Add(new Room { Id = 2, Aabb = new Aabb(new Vec3(0, 0, 0), new Vec3(8, 6, 8)) });
        AddWall(g, -6f, -1f, 0); // room 0 wall on the left
        AddWall(g, 1f, 6f, 1);   // room 1 wall on the right

        var rfl = NewLevel();
        AddSection(rfl, SectionType.StaticGeometry, new GeometrySection { Geometry = g });
        return rfl;
    }

    private static void AddWall(Geometry g, float x0, float x1, int room)
    {
        const float z = 6f;
        int b = g.Vertices.Count;
        g.Vertices.AddRange(new[]
        {
            new Vec3(x0, 0, z), new Vec3(x1, 0, z), new Vec3(x1, 6, z), new Vec3(x0, 6, z),
        });
        var f = new Face { Texture = 0, RoomIndex = room, Plane = new RfPlane(new Vec3(0, 0, -1), z) };
        f.Vertices.Add(new FaceVertex { Index = b });
        f.Vertices.Add(new FaceVertex { Index = b + 1 });
        f.Vertices.Add(new FaceVertex { Index = b + 2 });
        f.Vertices.Add(new FaceVertex { Index = b + 3 });
        g.Faces.Add(f);
    }

    private static RflFile LevelWith(SectionType type, IRflSectionContent content)
    {
        var rfl = NewLevel();
        AddSection(rfl, type, content);
        return rfl;
    }

    private static RflFile NewLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12D;
        rfl.Header.LevelName = "fx.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static void AddSection(RflFile rfl, SectionType type, IRflSectionContent content)
    {
        var s = new RflSection((uint)type, Array.Empty<byte>()) { Content = content, Dirty = true };
        rfl.Sections.Insert(rfl.Sections.Count - 1, s);
    }
}
