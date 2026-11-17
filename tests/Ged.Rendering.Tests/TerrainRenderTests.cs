using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// The viewport drawing of an Alpine terrain (C8, C9): its icon; the top drawn from the terrain with Alpine's multi-layer
/// weight blend (a <see cref="TerrainDraw"/> for the terrain shader) and the compiled chunk rooms left out of the static
/// geometry once built; pixel gates on both backends that the blend is per pixel (a smooth ramp between two solid-colour
/// layers, not the compiled faces' dominant layer per cell) and that D3D11 and OpenGL agree; the baked light chart; the
/// brush cursor; decorations. Close-ups go to tests/artifacts/terrain (textured when an RF install is mounted).
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class TerrainRenderTests
{
    private const int W = 960;
    private const int H = 640;
    private readonly ITestOutputHelper _out;

    public TerrainRenderTests(ITestOutputHelper output) => _out = output;

    /// <summary>A 65 x 65 terrain of generated hills, painted rock on its +X half, with a hole, inside an air box.</summary>
    private static (EditorDocument Doc, AlpineTerrain Terrain) Level()
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15));
        LevelObject obj = TerrainOps.Create(doc, new Vec3(0, 0, 0), 65, 65, 2f, "hills");
        var t = (AlpineTerrain)obj.Model;
        TerrainOps.Apply(doc, obj, "shape", x =>
        {
            TerrainOps.SetLayers(x, new[] { "rck_comflr04.tga", "rck_multirock02_flr.tga" }, new[] { 8f, 6f });
            x.HeightRange = 18f;
            double[] h = TerrainGenerator.Generate(new TerrainGenSettings { Seed = 9, FeatureSize = 60f }, x.Nx, x.Nz, x.CellSize);
            TerrainOps.SetNormalizedHeights(x, (i, j) => h[(j * x.Nx) + i]);
            AlpineTerrainGrid g = x.Grid.Clone();
            int map = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, g.WeightResolution);
            int ww = AlpineTerrainRules.WeightWidth(g.Nx, g.WeightResolution);
            for (int texel = 0; texel < map / 4; texel++)
            {
                if (texel % ww >= ww / 2)
                {
                    g.Weights[texel * 4] = 0;
                    g.Weights[(texel * 4) + 1] = 255;
                }
            }

            x.Grid = g;
            for (int cz = 20; cz < 26; cz++)
            {
                for (int cx = 8; cx < 14; cx++)
                {
                    TerrainOps.SetHole(x, cx, cz, true);
                }
            }
        });
        return (doc, t);
    }

    [Fact]
    public void A_Terrain_Draws_Its_Blended_Top_And_Replaces_Its_Compiled_Rooms()
    {
        (EditorDocument doc, AlpineTerrain t) = Level();
        RenderScene scene = SceneBuilder.Build(doc.Rfl);
        Billboard icon = Assert.Single(scene.Billboards, b => b.Kind == BillboardKind.Terrain);
        Assert.Equal((int)EditorIcon.Terrain, icon.Icon);

        TerrainDraw draw = Assert.Single(scene.Terrains);
        Assert.Equal(t.Uid, draw.Uid);
        Assert.Equal(2, draw.LayerCount);
        Assert.Equal(new[] { "rck_comflr04.tga", "rck_multirock02_flr.tga" }, draw.Layers.Take(2));
        int solidCells = (64 * 64) - 36;
        Assert.Equal(solidCells * 2, draw.Indices.Count / 3);
        Assert.Equal(65 * 65, draw.Vertices.Count);
        Assert.Equal(AlpineTerrainRules.WeightMapBytes(65, 65, 2), draw.Weights0.Length);
        Assert.DoesNotContain(scene.Batches, b => b.OwnerUid == t.Uid); // an open sheet has no underside

        // Built: the compiled chunk rooms are left out of the static geometry; the terrain still draws itself.
        CompileOptions options = CompileOptionsFactory.Create(doc.Rfl, BuildSettings.EditorDefaults, new CompileRequest { Sealed = false });
        GeometryBuildService.BuildAndApply(doc.Rfl, options);
        Assert.True(t.BuildIsCurrent);
        RenderScene built = SceneBuilder.Build(doc.Rfl);
        Assert.Single(built.Terrains);
        Geometry geo = doc.Rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        var terrainRooms = t.BuildMapping.Select(m => m.RoomUid).ToHashSet();
        int terrainFaces = geo.Faces.Count(f => f.RoomIndex >= 0 && terrainRooms.Contains(geo.Rooms[f.RoomIndex].Id));
        Assert.True(terrainFaces > 0);
        int drawnTriangles = built.Batches.Sum(b => b.Indices.Count) / 3;
        int allTriangles = SceneBuilder.Build(doc.Rfl, new SceneBuildOptions { IncludeObjects = false }).Batches.Sum(b => b.Indices.Count) / 3;
        Assert.True(drawnTriangles < allTriangles, $"{drawnTriangles} vs {allTriangles}");
    }

    /// <summary>A flat 17 x 17 terrain whose weights ramp from layer 0 (red) at -X to layer 1 (blue) at +X.</summary>
    private static (EditorDocument Doc, AlpineTerrain Terrain) RampLevel()
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15));
        LevelObject obj = TerrainOps.Create(doc, new Vec3(0, 0, 0), 17, 17, 2f, "ramp");
        var t = (AlpineTerrain)obj.Model;
        TerrainOps.Apply(doc, obj, "ramp", x =>
        {
            TerrainOps.SetLayers(x, new[] { "c9_red.tga", "c9_blue.tga" }, new[] { 4f, 4f });
            AlpineTerrainGrid g = x.Grid.Clone();
            int map = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, g.WeightResolution);
            int ww = AlpineTerrainRules.WeightWidth(g.Nx, g.WeightResolution);
            for (int texel = 0; texel < map / 4; texel++)
            {
                int b = (int)Math.Round(255.0 * (texel % ww) / (ww - 1));
                g.Weights[texel * 4] = (byte)(255 - b);
                g.Weights[(texel * 4) + 1] = (byte)b;
            }

            x.Grid = g;
        });
        return (doc, t);
    }

    private static RenderScene RampScene(EditorDocument doc)
    {
        RenderScene scene = SceneBuilder.Build(doc.Rfl, new SceneBuildOptions { IncludeStaticGeometry = false, ObjectIcons = false });
        scene.Billboards.Clear();
        scene.InlineTextures["c9_red.tga"] = Solid(255, 0, 0);
        scene.InlineTextures["c9_blue.tga"] = Solid(0, 0, 255);
        return scene;
    }

    private static InlineTexture Solid(byte r, byte g, byte b)
    {
        var px = new byte[8 * 8 * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = r;
            px[i + 1] = g;
            px[i + 2] = b;
            px[i + 3] = 255;
        }

        return new InlineTexture(8, 8, px);
    }

    private static byte[] RenderTopDown(GraphicsDevice gd, RenderScene scene, RenderMode mode = RenderMode.JustTextures)
    {
        var cam = new Camera { Projection = CameraProjection.Perspective, AspectRatio = (float)W / H };
        cam.LookAt(new Vector3(0.01f, 40f, -0.01f), new Vector3(0, 0, 0));
        return OffscreenRenderer.Render(gd, scene, null, cam, mode, W, H);
    }

    /// <summary>The red / blue channels along the image's middle row, at a few columns across the terrain.</summary>
    private static (int R, int B)[] Row(byte[] px)
    {
        int y = H / 2;
        return Enumerable.Range(0, 9).Select(k =>
        {
            int x = (W / 2) - 200 + (k * 50);
            int o = ((y * W) + x) * 4;
            return ((int)px[o], (int)px[o + 2]);
        }).ToArray();
    }

    [Fact]
    public void The_Blend_Is_Per_Pixel_On_Both_Backends()
    {
        (EditorDocument doc, _) = RampLevel();
        string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "terrain");
        Directory.CreateDirectory(dir);
        byte[]? d3d = null, gl = null;
        foreach (GraphicsBackend backend in new[] { GraphicsBackend.Direct3D11, GraphicsBackend.OpenGl })
        {
            using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(backend, out string reason);
            if (gd is null)
            {
                _out.WriteLine($"{backend} unavailable: {reason}");
                continue;
            }

            byte[] px = RenderTopDown(gd, RampScene(doc));
            File.WriteAllBytes(Path.Combine(dir, $"terrain_blend_ramp_{backend}.png"), PngWriter.Encode(W, H, px));
            (int R, int B)[] row = Row(px);
            _out.WriteLine($"{backend}: " + string.Join(" ", row.Select(c => $"({c.R},{c.B})")));

            // Red falls and blue rises monotonically across the ramp, with blended (both > 40) pixels in the middle:
            // a dominant-layer draw would jump from pure red to pure blue at one cell edge.
            for (int k = 1; k < row.Length; k++)
            {
                Assert.True(row[k].R <= row[k - 1].R + 2 && row[k].B >= row[k - 1].B - 2, $"{backend} column {k}: {row[k - 1]} -> {row[k]}");
            }

            Assert.True(row[0].R > row[0].B && row[^1].B > row[^1].R, $"{backend}: ends {row[0]} / {row[^1]}");
            Assert.Contains(row, c => c.R > 60 && c.B > 60);
            if (backend == GraphicsBackend.Direct3D11)
            {
                d3d = px;
            }
            else
            {
                gl = px;
            }
        }

        if (d3d is not null && gl is not null)
        {
            long diff = 0;
            int max = 0;
            for (int i = 0; i < d3d.Length; i++)
            {
                int dd = Math.Abs(d3d[i] - gl[i]);
                diff += dd;
                max = Math.Max(max, dd);
            }

            double mean = diff / (double)d3d.Length;
            _out.WriteLine($"D3D11 vs OpenGL: mean |d| {mean:0.000}, max {max}");
            Assert.True(mean < 1.0, $"backends disagree: mean |d| {mean:0.000}");
        }
    }

    [Fact]
    public void A_Baked_Chart_Lights_The_Terrain()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        (EditorDocument doc, AlpineTerrain t) = RampLevel();
        RenderScene scene = RampScene(doc);
        TerrainDraw draw = Assert.Single(scene.Terrains);

        // A chart dark at -Z and bright at +Z (RGB8 texels; 128 is neutral), as a baked chart lays them over the footprint.
        int cw = 32, ch = 32;
        var chart = new byte[cw * ch * 3];
        for (int v = 0; v < ch; v++)
        {
            for (int u = 0; u < cw; u++)
            {
                byte val = (byte)(40 + (v * 6));
                chart[((v * cw) + u) * 3] = val;
                chart[(((v * cw) + u) * 3) + 1] = val;
                chart[(((v * cw) + u) * 3) + 2] = val;
            }
        }

        draw.Chart = chart;
        draw.ChartWidth = cw;
        draw.ChartHeight = ch;
        draw.ChartOrigin = new Vector2(t.Origin.X, t.Origin.Z);
        draw.ChartTexelSize = 1f;
        byte[] lit = RenderTopDown(gd, scene, RenderMode.JustLightmaps);
        string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "terrain");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "terrain_chart_lightmaps.png"), PngWriter.Encode(W, H, lit));
        int Lum(int x, int y) => lit[((y * W) + x) * 4];
        int top = Lum(W / 2, (H / 2) - 150), bottom = Lum(W / 2, (H / 2) + 150);
        _out.WriteLine($"chart light: top {top}, bottom {bottom}");
        Assert.True(Math.Abs(top - bottom) > 60, $"top {top} bottom {bottom}");
    }

    [Fact]
    public void The_Brush_Cursor_Drapes_Its_Rings_On_The_Surface()
    {
        (_, AlpineTerrain t) = Level();
        var settings = new TerrainBrushSettings { Tool = TerrainTool.Raise, Radius = 10f };
        float x = t.Origin.X + 64f, z = t.Origin.Z + 64f;
        var hover = new Vector3(x, TerrainOps.HeightAt(t, x, z), z);
        var lines = TerrainBrushCursor.Lines(t, settings, new TerrainCursorState { Hover = hover });
        Assert.True(lines.Count > 48);
        foreach (LineSegment l in lines.Take(lines.Count - 1))
        {
            float d = new Vector2(l.A.X - x, l.A.Z - z).Length();
            Assert.True(Math.Abs(d - 10f) < 0.05f || Math.Abs(d - 5f) < 0.05f, $"ring point at {d}");
            Assert.InRange(l.A.Y - TerrainOps.HeightAt(t, l.A.X, l.A.Z), 0.05f, 0.15f);
        }

        // Constant falloff and the hole tools draw the outer ring only.
        int full = lines.Count;
        settings.Falloff = TerrainFalloff.Constant;
        Assert.True(TerrainBrushCursor.Lines(t, settings, new TerrainCursorState { Hover = hover }).Count < full);

        // Render evidence: the cursor over the terrain.
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        settings.Falloff = TerrainFalloff.Smooth;
        (EditorDocument doc, _) = Level();
        RenderScene scene = SceneBuilder.Build(doc.Rfl, new SceneBuildOptions { IncludeStaticGeometry = false });
        scene.Lines.AddRange(TerrainBrushCursor.Lines(t, settings, new TerrainCursorState { Hover = hover, Stroking = false }));
        var cam = new Camera { Projection = CameraProjection.Perspective, AspectRatio = (float)W / H };
        cam.LookAt(new Vector3(x - 25f, hover.Y + 22f, z - 25f), hover);
        byte[] px = OffscreenRenderer.Render(gd, scene, null, cam, RenderMode.JustTextures, W, H);
        string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "terrain");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "terrain_brush_cursor.png"), PngWriter.Encode(W, H, px));
        int yellow = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            yellow += px[i] > 200 && px[i + 1] > 200 && px[i + 2] < 60 ? 1 : 0;
        }

        _out.WriteLine($"yellow cursor pixels: {yellow}");
        Assert.True(yellow > 200, $"{yellow} cursor pixels");
    }

    [Fact]
    public void Decorations_Place_As_The_Game_Places_Them()
    {
        (EditorDocument doc, AlpineTerrain t) = Level();
        LevelObject obj = doc.Objects.First(o => o.Model == t);
        TerrainOps.Apply(doc, obj, "decorate", x =>
        {
            TerrainOps.SetDecoration(x, 0, new AlpineTerrainDecoration("brush_grass1.v3m", 0.25f, 0.8f, 1.2f, 35f, 80f, 0f, AlpineTerrainRules.DecorationLinkNone,
                AlpineTerrainSurface.DecorationFlagRandomYaw));
            TerrainOps.FillDecorationCoverage(x, 0, 255);
        });
        RenderScene scene = SceneBuilder.Build(doc.Rfl);
        DecorationDraw d = Assert.Single(scene.Decorations);
        int expected = AlpineTerrainSurface.ForEachTerrainDecoration(t, new AlpineTerrainSurface.Budget(), _ => true);
        Assert.Equal(expected, d.Worlds.Count);
        Assert.InRange(expected, 3000, 4100); // 0.25 / m² over ~16 000 m² with holes and steep ground dropped

        // Every instance stands on the surface.
        foreach (Matrix4x4 w in d.Worlds.Take(200))
        {
            Assert.InRange(w.M42 - TerrainOps.HeightAt(t, w.M41, w.M43), -0.01f, 0.01f);
        }

        Assert.Empty(SceneBuilder.Build(doc.Rfl, new SceneBuildOptions { TerrainDecorations = false }).Decorations);
    }

    [Fact]
    public void Closeups_Of_A_Terrain()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        gd.SetIconAtlas(IconAtlas.Build());
        AssetVfs? vfs = RenderTestSupport.RfInstall is { } install ? GameMount.Mount(install) : null;
        try
        {
            (EditorDocument doc, _) = Level();
            RenderScene scene = SceneBuilder.Build(doc.Rfl, new SceneBuildOptions { IncludeStaticGeometry = false, BillboardSize = 1.5f });
            string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "terrain");
            Directory.CreateDirectory(dir);
            var shots = new (string Name, Vector3 Eye, Vector3 At, RenderMode Mode)[]
            {
                ("terrain_overview", new Vector3(-80, 70, -80), new Vector3(0, 4, 0), RenderMode.JustTextures),
                ("terrain_hole_closeup", new Vector3(-30, 22, -20), new Vector3(-42, 6, -18), RenderMode.JustTextures),
                ("terrain_layers_seam", new Vector3(0, 30, -60), new Vector3(0, 6, 0), RenderMode.JustTextures),
                ("terrain_wireframe", new Vector3(-80, 70, -80), new Vector3(0, 4, 0), RenderMode.Wireframe),
            };
            foreach ((string name, Vector3 eye, Vector3 at, RenderMode mode) in shots)
            {
                var cam = new Camera { Projection = CameraProjection.Perspective, AspectRatio = (float)W / H };
                cam.LookAt(eye, at);
                byte[] px = OffscreenRenderer.Render(gd, scene, vfs, cam, mode, W, H);
                File.WriteAllBytes(Path.Combine(dir, name + ".png"), PngWriter.Encode(W, H, px));
                Assert.True(RenderTestSupport.IsNonTrivial(px, out int colours), $"{name}: blank render");
                _out.WriteLine($"{name}: {colours} distinct colours");
            }
        }
        finally
        {
            vfs?.Dispose();
        }
    }

    /// <summary>
    /// Close-ups of corpus terrain levels as Alpine RED baked them (research/fixtures/alpine-ref/*/terrain-bake-raw): the
    /// terrain lit by its loaded chart, its decorations, and the level around it, textured from the mounted install. The
    /// chart must reach the draw (lit and unlit pictures differ) and decorations must be placed.
    /// </summary>
    [Theory]
    [InlineData("tersunmesh1.rfl")]
    [InlineData("c8_terrain_check.rfl")]
    public void Closeups_Of_A_Corpus_Terrain_Lit_By_Alpine_REDs_Chart(string level)
    {
        string? path = RenderTestSupport.ResearchFixtures is { } fx && Directory.Exists(Path.Combine(fx, "alpine-ref"))
            ? Directory.GetFiles(Path.Combine(fx, "alpine-ref"), level, SearchOption.AllDirectories).FirstOrDefault(p => p.Contains("terrain-bake-raw", StringComparison.Ordinal))
            : null;
        if (path is null || RenderTestSupport.RfInstall is null)
        {
            return;
        }

        Ged.Core.IO.Rfl.RflFile rfl = Ged.Core.IO.Rfl.RflFile.Load(path);
        rfl.ParseAllKnownSections();
        RenderScene scene = SceneBuilder.Build(rfl, new SceneBuildOptions { IncludeObjects = true, BillboardSize = 0.01f });
        Assert.NotEmpty(scene.Terrains);
        Assert.All(scene.Terrains, d => Assert.NotNull(d.Chart));
        int decorations = scene.Decorations.Sum(d => d.Worlds.Count);
        _out.WriteLine($"{level}: {scene.Terrains.Count} terrain(s), layers {string.Join(" ", scene.Terrains.SelectMany(d => d.Layers.Take(d.LayerCount)))}, decorations {string.Join(", ", scene.Decorations.Select(d => $"{d.MeshFilename} x{d.Worlds.Count}"))}");

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        gd.SetIconAtlas(IconAtlas.Build());
        using AssetVfs vfs = GameMount.Mount(RenderTestSupport.RfInstall);
        TerrainDraw big = scene.Terrains.OrderByDescending(d => d.Extent.X * d.Extent.Y).First();
        Vector3 centre = big.Origin + new Vector3(big.Extent.X * 0.5f, 0, big.Extent.Y * 0.5f);
        float groundY = big.Vertices.Average(v => v.Position.Y);
        centre.Y = groundY;
        float span = Math.Max(big.Extent.X, big.Extent.Y);
        var shots = new List<(string Name, Vector3 Eye, Vector3 At, RenderMode Mode)>
        {
            ("overview_lit", centre + new Vector3(-span * 0.45f, span * 0.35f, -span * 0.45f), centre, RenderMode.TexturesAndLightmaps),
            ("overview_lightmaps", centre + new Vector3(-span * 0.45f, span * 0.35f, -span * 0.45f), centre, RenderMode.JustLightmaps),
            ("overview_unlit", centre + new Vector3(-span * 0.45f, span * 0.35f, -span * 0.45f), centre, RenderMode.JustTextures),
        };
        if (scene.Decorations.FirstOrDefault() is { Worlds.Count: > 0 } deco)
        {
            Matrix4x4 m = deco.Worlds[deco.Worlds.Count / 2];
            var at = new Vector3(m.M41, m.M42 + 0.5f, m.M43);
            shots.Add(("decorations_closeup", at + new Vector3(-7f, 3.5f, -7f), at, RenderMode.TexturesAndLightmaps));
        }

        string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "terrain");
        Directory.CreateDirectory(dir);
        var pictures = new Dictionary<string, byte[]>();
        foreach ((string name, Vector3 eye, Vector3 at, RenderMode mode) in shots)
        {
            var cam = new Camera { Projection = CameraProjection.Perspective, AspectRatio = (float)W / H, FarPlane = 20000f };
            cam.LookAt(eye, at);
            byte[] px = OffscreenRenderer.Render(gd, scene, vfs, cam, mode, W, H);
            pictures[name] = px;
            File.WriteAllBytes(Path.Combine(dir, $"corpus_{Path.GetFileNameWithoutExtension(level)}_{name}.png"), PngWriter.Encode(W, H, px));
            Assert.True(RenderTestSupport.IsNonTrivial(px, out int colours), $"{name}: blank render");
            _out.WriteLine($"{level} {name}: {colours} distinct colours");
        }

        long diff = 0;
        for (int i = 0; i < pictures["overview_lit"].Length; i++)
        {
            diff += Math.Abs(pictures["overview_lit"][i] - pictures["overview_unlit"][i]);
        }

        Assert.True(diff / (double)pictures["overview_lit"].Length > 4, "the chart did not change the picture");
        if (level == "tersunmesh1.rfl")
        {
            Assert.True(decorations > 0);
        }
    }
}
