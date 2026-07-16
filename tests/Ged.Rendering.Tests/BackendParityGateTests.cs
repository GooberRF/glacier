using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Core.Tables;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// The CROWN-JEWEL cross-backend gate (CP2/L3): extends the D3D11↔OpenGL parity
/// coverage from the two seed scenes L2 shipped to the FULL gated render set —
/// the same scene categories the D3D11 acceptance gates exercise against RED
/// (compiled geometry, baked lighting, object icons/billboards, particle/bolt
/// effects, three-way portal faces, scrolling liquid, sky pass, distance fog, and
/// world-line overlays). Each view is rendered offscreen through the identical
/// scene-building/rendering code above the RHI on BOTH backends and asserted
/// ≤1% differing pixels per view (per-channel delta &gt; 12); any deviation is a
/// pure backend difference. L2 measured ≤0.005% on the seed scenes; the residual
/// here is edge-only rasterization. Per-view numbers are logged and both PNGs are
/// written to tests/artifacts/backend-parity for inspection. Follows the existing
/// skip-when-unavailable pattern: skips gracefully when either backend, the corpus
/// or the RF install is missing.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class BackendParityGateTests
{
    private const int ChannelTolerance = 12;
    private const double MaxDiffFraction = 0.01;

    private static readonly RenderMode[] AllModes =
    {
        RenderMode.JustTextures,
        RenderMode.TexturesAndLightmaps,
        RenderMode.JustLightmaps,
        RenderMode.RoomColors,
        RenderMode.Wireframe,
        RenderMode.SeeThrough,
    };

    private readonly ITestOutputHelper _out;

    public BackendParityGateTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The flagship corpus levels the visual acceptance gates use, rendered with a
    /// FULL scene (object icons/billboards, links, light ranges, region outlines,
    /// see-thru portal faces, movers) across every render mode. Covers the world
    /// opaque/liquid/alpha/sky passes, the icon-atlas billboard pass and the line
    /// overlay pass in one sweep — the union of the CompiledParity / Bake / Lighting
    /// / Icon gated coverage, cross-checked backend-to-backend.
    /// </summary>
    [Theory]
    [InlineData("dm01.rfl")]
    [InlineData("dm04.rfl")]
    [InlineData("glass_house.rfl")]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("ctf07.rfl")]
    public void CorpusFlagship_AllModes_MatchAcrossBackends(string fileName)
    {
        string? path = RenderTestSupport.CorpusFile(fileName);
        if (path is null)
        {
            return;
        }

        using GraphicsDevice? d3d = RenderTestSupport.TryCreateDevice(GraphicsBackend.Direct3D11, out string dxReason);
        using GraphicsDevice? gl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
        if (d3d is null || gl is null)
        {
            _out.WriteLine($"Skipping (D3D11: {dxReason}; OpenGL: {glReason})");
            return;
        }

        AssetVfs? vfs = RenderTestSupport.RfInstall is null ? null : GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            RflFile file = RflFile.Load(path);
            var options = new SceneBuildOptions
            {
                Entities = vfs is null ? null : TryLoad(vfs, "entity.tbl", EntityCatalog.Load),
                Clutter = vfs is null ? null : TryLoad(vfs, "clutter.tbl", ClutterCatalog.Load),
                Items = vfs is null ? null : TryLoad(vfs, "items.tbl", ItemCatalog.Load),
                ShowAllRanges = true,
                PortalFaces = PortalFaceDrawMode.SeeThru,
                PortalFaceColor = Palette.Rgba(0x40, 0xE0, 0xD0, 255),
            };
            RenderScene scene = SceneBuilder.Build(file, options);
            Vector3 center = (ToVec(scene.Bounds.P1) + ToVec(scene.Bounds.P2)) * 0.5f;
            GridBuilder.Append(scene, center, 40f, 2f, 0.8f, scene.Bounds.P1.Y);

            var overview = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            overview.Frame(scene.Bounds);

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            foreach (RenderMode mode in AllModes)
            {
                CompareView(d3d, gl, scene, vfs, overview, mode, 512, baseName, mode.ToString());
            }
        }
        finally
        {
            vfs?.Dispose();
        }
    }

    /// <summary>
    /// GED-recompiled geometry (RED's authentic shared BSP, the shipping default)
    /// rendered on both backends. Mirrors <see cref="CompiledParityRenderTests"/>'s
    /// scene/camera set but cross-checks backend-to-backend on the recompiled output.
    /// </summary>
    [Theory]
    [InlineData("dm01.rfl")]
    [InlineData("dm04.rfl")]
    public void RecompiledGeometry_MatchesAcrossBackends(string fileName)
    {
        string? orig = RenderTestSupport.CorpusFile(fileName);
        if (orig is null || RenderTestSupport.RfInstall is null)
        {
            return;
        }

        using GraphicsDevice? d3d = RenderTestSupport.TryCreateDevice(GraphicsBackend.Direct3D11, out string dxReason);
        using GraphicsDevice? gl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
        if (d3d is null || gl is null)
        {
            _out.WriteLine($"Skipping (D3D11: {dxReason}; OpenGL: {glReason})");
            return;
        }

        AssetVfs vfs = GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            RflFile recompiled = RflFile.Load(orig);
            var traits = new TextureTraitsCache(vfs);
            GeometryBuildService.BuildAndApply(recompiled, new CompileOptions
            {
                TextureTraits = traits.Get,
                SharedBsp = !RenderTestSupport.ForcePerBrush,
                IncrementalAccumulator = !RenderTestSupport.ForcePerBrush,
            });

            var options = new SceneBuildOptions
            {
                IncludeObjects = false,
                IncludeLinks = false,
                IncludeLightRanges = false,
                IncludeRegionOutlines = false,
            };
            RenderScene scene = SceneBuilder.Build(recompiled, options);

            var overview = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            overview.Frame(scene.Bounds);
            Vector3 c = (ToVec(scene.Bounds.P1) + ToVec(scene.Bounds.P2)) * 0.5f;
            var interior = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            interior.LookAt(c + new Vector3(3f, 1.5f, 0f), c + new Vector3(0f, 1f, 4f));

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            foreach ((string tag, Camera cam) in new[] { ("overview", overview), ("interior", interior) })
            {
                CompareView(d3d, gl, scene, vfs, cam, RenderMode.JustTextures, 640, $"{baseName}_recompiled", $"{tag}_JustTextures");
                CompareView(d3d, gl, scene, vfs, cam, RenderMode.RoomColors, 640, $"{baseName}_recompiled", $"{tag}_RoomColors");
            }
        }
        finally
        {
            vfs.Dispose();
        }
    }

    /// <summary>
    /// GED-recompiled AND GED-baked lighting rendered on both backends
    /// (TexturesAndLightmaps). Mirrors <see cref="BakeParityRenderTests"/>'s scene
    /// but cross-checks the lightmapped output backend-to-backend.
    /// </summary>
    [Theory]
    [InlineData("dm01.rfl")]
    [InlineData("dm04.rfl")]
    public void BakedLighting_MatchesAcrossBackends(string fileName)
    {
        string? orig = RenderTestSupport.CorpusFile(fileName);
        if (orig is null || RenderTestSupport.RfInstall is null)
        {
            return;
        }

        using GraphicsDevice? d3d = RenderTestSupport.TryCreateDevice(GraphicsBackend.Direct3D11, out string dxReason);
        using GraphicsDevice? gl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
        if (d3d is null || gl is null)
        {
            _out.WriteLine($"Skipping (D3D11: {dxReason}; OpenGL: {glReason})");
            return;
        }

        AssetVfs vfs = GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            RflFile recompiled = RflFile.Load(orig);
            var traits = new TextureTraitsCache(vfs);
            GeometryBuildService.BuildAndApply(recompiled, new CompileOptions
            {
                TextureTraits = traits.Get,
                BakeLighting = true,
                SharedBsp = !RenderTestSupport.ForcePerBrush,
                IncrementalAccumulator = !RenderTestSupport.ForcePerBrush,
            });

            var options = new SceneBuildOptions
            {
                IncludeObjects = false,
                IncludeLinks = false,
                IncludeLightRanges = false,
                IncludeRegionOutlines = false,
            };
            RenderScene scene = SceneBuilder.Build(recompiled, options);

            var overview = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            overview.Frame(scene.Bounds);
            Vector3 c = (ToVec(scene.Bounds.P1) + ToVec(scene.Bounds.P2)) * 0.5f;
            var interior = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            interior.LookAt(c + new Vector3(3f, 1.5f, 0f), c + new Vector3(0f, 1f, 4f));

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            CompareView(d3d, gl, scene, vfs, overview, RenderMode.TexturesAndLightmaps, 640, $"{baseName}_baked", "overview");
            CompareView(d3d, gl, scene, vfs, interior, RenderMode.TexturesAndLightmaps, 640, $"{baseName}_baked", "interior");
        }
        finally
        {
            vfs.Dispose();
        }
    }

    /// <summary>
    /// Live-preview effects: a particle burst (billboard pass carrying the emitter's
    /// authored bitmap) and a bolt arc (world line pass). Mirrors
    /// <see cref="EffectsRenderTests"/> and cross-checks both backends.
    /// </summary>
    [Fact]
    public void Effects_Particles_And_Bolt_MatchAcrossBackends()
    {
        using GraphicsDevice? d3d = RenderTestSupport.TryCreateDevice(GraphicsBackend.Direct3D11, out string dxReason);
        using GraphicsDevice? gl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
        if (d3d is null || gl is null)
        {
            _out.WriteLine($"Skipping (D3D11: {dxReason}; OpenGL: {glReason})");
            return;
        }

        if (RenderTestSupport.FixtureFile("tex", "wallputer1.tga") is null)
        {
            return; // retail-derived fixture not present
        }

        var sources = new List<IAssetSource>();
        foreach (string dir in RenderTestSupport.FixtureDirs("tex"))
        {
            sources.Add(new DirectoryAssetSource(dir, extensions: SupercedeChain.Extensions));
        }

        using AssetVfs vfs = new(sources.ToArray());

        var camera = new Camera { Position = Vector3.Zero };
        camera.LookAt(Vector3.Zero, new Vector3(0, 0, 1));

        RflFile particles = LevelWith(SectionType.ParticleEmitters, new ParticleEmittersSection
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
        var particleScene = new RenderScene();
        EffectsBuilder.Append(particles, new EffectsOptions { Time = 1.0f }, particleScene);
        CompareView(d3d, gl, particleScene, vfs, camera, RenderMode.JustTextures, 512, "effects", "particles");

        RflFile bolt = LevelWith(SectionType.BoltEmitters, new BoltEmittersSection
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
        AddSection(bolt, SectionType.Targets, new TargetsSection
        {
            Targets = { new ObjectHeader { Uid = 21, Position = new Vec3(3, 0, 6) } },
        });
        var boltScene = new RenderScene();
        EffectsBuilder.Append(bolt, new EffectsOptions { Time = 0.3f }, boltScene);
        CompareView(d3d, gl, boltScene, null, camera, RenderMode.JustTextures, 512, "effects", "bolt");
    }

    /// <summary>
    /// The three-way portal-face draw mode (None / See-thru alpha / Opaque) on both
    /// backends — covers the translucent alpha pass and the tinted opaque pass.
    /// Mirrors <see cref="PortalFaceRenderTests"/>.
    /// </summary>
    [Fact]
    public void PortalFaces_AllModes_MatchAcrossBackends()
    {
        using GraphicsDevice? d3d = RenderTestSupport.TryCreateDevice(GraphicsBackend.Direct3D11, out string dxReason);
        using GraphicsDevice? gl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
        if (d3d is null || gl is null)
        {
            _out.WriteLine($"Skipping (D3D11: {dxReason}; OpenGL: {glReason})");
            return;
        }

        var camera = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
        foreach (PortalFaceDrawMode mode in new[] { PortalFaceDrawMode.None, PortalFaceDrawMode.SeeThru, PortalFaceDrawMode.Opaque })
        {
            RenderScene scene = SceneBuilder.Build(PortalLevel(), new SceneBuildOptions
            {
                IncludeObjects = false,
                IncludeMovers = false,
                PortalFaces = mode,
                PortalFaceColor = Palette.Rgba(0x40, 0xE0, 0xD0, 255),
            });
            CompareView(d3d, gl, scene, null, camera, RenderMode.JustTextures, 480, "portal", mode.ToString());
        }
    }

    /// <summary>
    /// Scrolling liquid surface (animated UV via the render clock) and the sky +
    /// distance-fog passes, on both backends. Covers the liquid pass's time-driven
    /// UV scroll, the camera-locked sky pass and the fog term.
    /// </summary>
    [Fact]
    public void LiquidSkyFog_MatchAcrossBackends()
    {
        using GraphicsDevice? d3d = RenderTestSupport.TryCreateDevice(GraphicsBackend.Direct3D11, out string dxReason);
        using GraphicsDevice? gl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
        if (d3d is null || gl is null)
        {
            _out.WriteLine($"Skipping (D3D11: {dxReason}; OpenGL: {glReason})");
            return;
        }

        using AssetVfs vfs = new(new IAssetSource[]
        {
            new DirectoryAssetSource(Path.Combine(RenderTestSupport.Fixtures!, "tex"), extensions: SupercedeChain.Extensions),
        });

        var camera = new Camera { Position = Vector3.Zero };
        camera.LookAt(Vector3.Zero, new Vector3(0, 0, 1));

        // Liquid scroll at t=1: the animated UV must land identically on both backends.
        var liquid = new RenderScene();
        var batch = new GeometryBatch("gradient2x2.png", -1, RenderPass.Liquid) { ScrollU = 0.5f };
        void LV(float x, float y, float u) => batch.Vertices.Add(new WorldVertex
        {
            Position = new Vector3(x, y, 6f),
            Normal = new Vector3(0, 0, -1),
            TexCoord = new Vector2(u, 0.5f),
            Color = Palette.Rgba(255, 255, 255),
        });
        LV(-3f, -3f, 0f);
        LV(3f, -3f, 1f);
        LV(3f, 3f, 1f);
        LV(-3f, 3f, 0f);
        batch.Indices.AddRange(new uint[] { 0, 1, 2, 0, 2, 3 });
        liquid.Batches.Add(batch);
        CompareView(d3d, gl, liquid, vfs, camera, RenderMode.JustTextures, 256, "liquid", "scroll_t1", time: 1f);

        // Sky pass behind an opaque wall (RoomColors so the vertex tints show without a VFS).
        CompareView(d3d, gl, SkyScene(), null, camera, RenderMode.RoomColors, 512, "sky", "backdrop", disableBackfaceCulling: true);

        // Distance fog over a depth stack.
        var fog = FogSettings.FromLevel(new Vector3(0.55f, 0.6f, 0.7f), 40f);
        CompareView(d3d, gl, DepthWalls(), null, camera, RenderMode.JustTextures, 512, "fog", "depthwalls", fog: fog, disableBackfaceCulling: true);
    }

    // ─── Compare + diff ──────────────────────────────────────────────────────

    private void CompareView(
        GraphicsDevice d3d,
        GraphicsDevice gl,
        RenderScene scene,
        AssetVfs? vfs,
        Camera camera,
        RenderMode mode,
        int size,
        string label,
        string tag,
        FogSettings? fog = null,
        float time = 0f,
        bool disableBackfaceCulling = false)
    {
        byte[] a = OffscreenRenderer.Render(d3d, scene, vfs, camera, mode, size, size, fog, time, disableBackfaceCulling);
        byte[] b = OffscreenRenderer.Render(gl, scene, vfs, camera, mode, size, size, fog, time, disableBackfaceCulling);

        string outDir = Path.Combine(RenderTestSupport.ArtifactsDir, "backend-parity");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, $"{label}_{tag}_d3d11.png"), PngWriter.Encode(size, size, a));
        File.WriteAllBytes(Path.Combine(outDir, $"{label}_{tag}_opengl.png"), PngWriter.Encode(size, size, b));

        double diff = DiffFraction(a, b);
        _out.WriteLine($"{label} {tag}: {diff * 100:F3}% differing pixels (D3D11 <-> OpenGL)");
        Assert.True(
            diff <= MaxDiffFraction,
            $"{label} {tag}: {diff * 100:F3}% of pixels differ (gate {MaxDiffFraction * 100:F0}%)");
    }

    private static double DiffFraction(byte[] a, byte[] b)
    {
        int pixels = System.Math.Min(a.Length, b.Length) / 4;
        int differing = 0;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            if (System.Math.Abs(a[o] - b[o]) > ChannelTolerance ||
                System.Math.Abs(a[o + 1] - b[o + 1]) > ChannelTolerance ||
                System.Math.Abs(a[o + 2] - b[o + 2]) > ChannelTolerance)
            {
                differing++;
            }
        }

        return pixels == 0 ? 1.0 : differing / (double)pixels;
    }

    // ─── Synthetic scene builders (mirrored from the D3D11 gated tests) ────────

    private static RenderScene SkyScene()
    {
        var scene = new RenderScene();
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
        return scene;
    }

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

    private static RflFile PortalLevel()
    {
        var geo = new Geometry();
        geo.Textures.Add("wall.tga");
        AddQuad(geo, 5f);
        AddQuad(geo, 6f);
        AddQuad(geo, 7f);
        var normal = new Vec3(0f, 0f, -1f);
        geo.Faces.Add(new Face
        {
            Texture = 0, SurfaceIndex = -1, RoomIndex = 0, Plane = new RfPlane(normal, 0f), Vertices = Quad(8),
        });
        geo.Faces.Add(new Face
        {
            Texture = -1, SurfaceIndex = -1, RoomIndex = 0, Plane = new RfPlane(normal, 0f), Vertices = Quad(0),
        });
        geo.Faces.Add(new Face
        {
            Texture = 0, PortalIndexPlus2 = 2, SurfaceIndex = -1, RoomIndex = 0, Plane = new RfPlane(normal, 0f), Vertices = Quad(4),
        });

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "portaltest";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        rfl.Sections.Insert(0, new RflSection((uint)SectionType.StaticGeometry, System.Array.Empty<byte>())
        {
            Content = new GeometrySection { Geometry = geo },
            Dirty = true,
        });
        return rfl;
    }

    private static void AddQuad(Geometry geo, float z)
    {
        geo.Vertices.Add(new Vec3(-3f, -3f, z));
        geo.Vertices.Add(new Vec3(3f, -3f, z));
        geo.Vertices.Add(new Vec3(3f, 3f, z));
        geo.Vertices.Add(new Vec3(-3f, 3f, z));
    }

    private static List<FaceVertex> Quad(int baseIndex) => new()
    {
        new FaceVertex { Index = baseIndex, TextureCoords = new Uv(0f, 1f) },
        new FaceVertex { Index = baseIndex + 1, TextureCoords = new Uv(1f, 1f) },
        new FaceVertex { Index = baseIndex + 2, TextureCoords = new Uv(1f, 0f) },
        new FaceVertex { Index = baseIndex + 3, TextureCoords = new Uv(0f, 0f) },
    };

    private static RflFile LevelWith(SectionType type, IRflSectionContent content)
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12D;
        rfl.Header.LevelName = "fx.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        AddSection(rfl, type, content);
        return rfl;
    }

    private static void AddSection(RflFile rfl, SectionType type, IRflSectionContent content)
    {
        var s = new RflSection((uint)type, System.Array.Empty<byte>()) { Content = content, Dirty = true };
        rfl.Sections.Insert(rfl.Sections.Count - 1, s);
    }

    private static T? TryLoad<T>(AssetVfs vfs, string name, System.Func<byte[], T> parse)
        where T : class
    {
        try
        {
            byte[]? data = vfs.ReadFile(name);
            return data is null ? null : parse(data);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private static Vector3 ToVec(Vec3 v) => new(v.X, v.Y, v.Z);
}
