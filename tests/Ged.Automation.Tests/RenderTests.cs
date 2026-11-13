using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation.Rendering;
using Ged.Cli;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>GPU render tests share one device at a time (like Ged.Rendering.Tests' GPU collection).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuRenderCollection
{
    public const string Name = "GPU render";
}

/// <summary>
/// <c>glacier-cli render</c>: the plan slice, ortho / iso / perspective views, CPU overlays, the
/// pixel ↔ world metadata, determinism, the attach transport (snapshot) and usage errors. Tests that
/// need a GPU device skip when none can be created; corpus tests skip without research/example_rfls.
/// Images are written to tests/artifacts/render for inspection.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class RenderTests
{
    private static readonly string ArtifactsDir = InitArtifacts();

    private static string InitArtifacts()
    {
        string dir = TestLevels.RepoRoot is { } root
            ? Path.Combine(root, "tests", "artifacts", "render")
            : Path.Combine(Path.GetTempPath(), "ged-render-artifacts");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GraphicsDevice? TryDevice()
    {
        try
        {
            return RenderDevices.Create().Device;
        }
        catch (AutomationException)
        {
            return null;
        }
    }

    /// <summary>The RF install for textures (GED_RF_DIR, then research/rf-dirs.txt), or null.</summary>
    private static string? RfInstall()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("GED_RF_DIR") is { Length: > 0 } env)
        {
            candidates.Add(env);
        }

        if (TestLevels.RepoRoot is { } root && File.Exists(Path.Combine(root, "research", "rf-dirs.txt")))
        {
            candidates.AddRange(File.ReadAllLines(Path.Combine(root, "research", "rf-dirs.txt"))
                .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        }

        return candidates.FirstOrDefault(c => Directory.Exists(c) && Directory.EnumerateFiles(c, "*.vpp").Any());
    }

    private static CliEnvironment Env(GraphicsDevice? device, string? pipe = null) => new()
    {
        PipeName = pipe ?? "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Clock = () => TestLevels.Fixed,
        RenderDevice = device is null ? null : () => (device, "test device"),
    };

    private static int Lum(byte[] rgba, int w, int x, int y)
    {
        int i = ((y * w) + x) * 4;
        return (rgba[i] * 299 + rgba[i + 1] * 587 + rgba[i + 2] * 114) / 1000;
    }

    private static (byte R, byte G, byte B) Px(byte[] rgba, int w, int x, int y)
    {
        int i = ((y * w) + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }

    /// <summary>The continuous pixel coordinate of a world point, from the result's ortho metadata alone.</summary>
    private static Vector2 MetaToPixel(RenderResult r, Vector3 world)
    {
        var o = new Vector3((float)r.Origin![0], (float)r.Origin[1], (float)r.Origin[2]);
        var ax = new Vector3((float)r.PixelAxes!.X[0], (float)r.PixelAxes.X[1], (float)r.PixelAxes.X[2]);
        var ay = new Vector3((float)r.PixelAxes.Y[0], (float)r.PixelAxes.Y[1], (float)r.PixelAxes.Y[2]);
        Vector3 d = world - o;
        return new Vector2(Vector3.Dot(d, ax) / ax.LengthSquared(), Vector3.Dot(d, ay) / ay.LengthSquared());
    }

    /// <summary>The pixel of a world point through the result's view-projection matrix.</summary>
    private static Vector2 MatrixToPixel(RenderResult r, Vector3 p)
    {
        double[] m = r.ViewProjection;
        double cx = (p.X * m[0]) + (p.Y * m[4]) + (p.Z * m[8]) + m[12];
        double cy = (p.X * m[1]) + (p.Y * m[5]) + (p.Z * m[9]) + m[13];
        double cw = (p.X * m[3]) + (p.Y * m[7]) + (p.Z * m[11]) + m[15];
        return new Vector2((float)(((cx / cw * 0.5) + 0.5) * r.Width), (float)((0.5 - (cy / cw * 0.5)) * r.Height));
    }

    private static int Distinct(byte[] rgba)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < rgba.Length; i += 4 * 7)
        {
            set.Add(rgba[i] | (rgba[i + 1] << 8) | (rgba[i + 2] << 16));
        }

        return set.Count;
    }

    // ---- synthetic level: precise pixel assertions ---------------------------------------------

    [Fact]
    public async Task Plan_Of_A_Built_Room_Has_Floor_Wall_Contrast_Glyphs_At_Their_Pixels_And_A_Scale_Bar()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        using var host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        await host.OpenAsync(level, CancellationToken.None);
        await host.BuildAsync(sealedBuild: true, bakeLighting: false, shadows: false, CancellationToken.None);

        RenderSpec spec = RenderSpec.FromArgs(new JsonObject { ["out"] = dir.File("room.png") });
        var geometry = new RenderGeometryDto { Before = "built", Rendered = "built" };
        (byte[] rgba, RenderResult r) = LevelRenderer.Render(gd, "test", host.Document!, null, spec, null, geometry, dir.File("room.png"));
        File.WriteAllBytes(Path.Combine(ArtifactsDir, "room_plan.png"), Ged.Core.IO.Tex.PngWriter.Encode(r.Width, r.Height, rgba));
        int w = r.Width;

        Assert.Equal("plan", r.View);
        Assert.Equal("orthographic", r.Projection);
        Assert.Equal(0, r.Plan!.Y, 3); // the floor under the player start (0, 1, 0)
        Assert.StartsWith("floor under the player start", r.Plan.YSource);
        Assert.True(Distinct(rgba) > 20, "the plan image is (nearly) blank");

        // Floor (inside the 12 x 12 room) vs solid space outside it, located purely from the metadata.
        Vector2 floor = MetaToPixel(r, new Vector3(-4f, 0f, 4f));
        Vector2 solid = MetaToPixel(r, new Vector3(10f, 0f, -4f)); // beside the corridor (the plan frames the geometry)
        int floorLum = Lum(rgba, w, (int)floor.X, (int)floor.Y);
        int solidLum = Lum(rgba, w, (int)solid.X, (int)solid.Y);
        Assert.True(floorLum - solidLum >= 50, $"floor {floorLum} vs solid {solidLum}: no contrast");

        // The corridor's wall at z = 1.5 is drawn as a dark cut edge (off the 1 m grid lines, which draw over the room's
        // own walls at this zoom).
        Vector2 wall = MetaToPixel(r, new Vector3(9f, 0f, 1.5f));
        int darkest = 255;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                darkest = Math.Min(darkest, Lum(rgba, w, (int)wall.X + dx, (int)wall.Y + dy));
            }
        }

        Assert.True(darkest <= 30 && floorLum - darkest >= 60, $"wall cut edge not dark (darkest {darkest}, floor {floorLum})");

        // The player start glyph sits where the camera projects it (±2 px) and is drawn in its legend colour.
        RenderObjectDto start = Assert.Single(r.Objects!, o => o.Kind == "playerStart");
        var startWorld = new Vector3(0f, 1f, 0f);
        Vector2 expected = MetaToPixel(r, startWorld);
        Assert.InRange(start.Pixel[0], expected.X - 2, expected.X + 2);
        Assert.InRange(start.Pixel[1], expected.Y - 2, expected.Y + 2);
        Vector2 viaMatrix = MatrixToPixel(r, startWorld);
        Assert.InRange(viaMatrix.X, expected.X - 0.75f, expected.X + 0.75f);
        Assert.InRange(viaMatrix.Y, expected.Y - 0.75f, expected.Y + 0.75f);
        RenderLegendDto startLegend = Assert.Single(r.Legend, l => l.Meaning == "player start");
        (byte R, byte G, byte B) green = (Convert.ToByte(startLegend.Color[1..3], 16), Convert.ToByte(startLegend.Color[3..5], 16), Convert.ToByte(startLegend.Color[5..7], 16));
        bool found = false;
        for (int dy = -2; dy <= 2 && !found; dy++)
        {
            for (int dx = -2; dx <= 2 && !found; dx++)
            {
                found = Px(rgba, w, (int)start.Pixel[0] + dx, (int)start.Pixel[1] + dy) == green;
            }
        }

        Assert.True(found, "the player start glyph is not drawn at its projected pixel");

        // The scale bar: a run of pure white in the bottom quarter.
        int bestRun = 0;
        for (int y = r.Height * 3 / 4; y < r.Height; y++)
        {
            int run = 0;
            for (int x = 0; x < w; x++)
            {
                run = Px(rgba, w, x, y) == (255, 255, 255) ? run + 1 : 0;
                bestRun = Math.Max(bestRun, run);
            }
        }

        Assert.True(bestRun >= 20, $"no scale bar (longest white run {bestRun} px)");
        Assert.Contains("scale", r.Overlays);
        Assert.Contains("legend", r.Overlays);
    }

    /// <summary>The agent guide's worked example (two rooms, a corridor, pillars, lights, spawns, items).</summary>
    private const string TwinArena = """
        local kit = ged.import("kit")
        level.props:set_many{ level_name = "Twin Rooms", multiplayer = true }
        local west = kit.room{ name = "west", pos = {-10, 0, 0}, size = {14, 5, 14} }
        local east = kit.room{ name = "east", pos = { 10, 0, 0}, size = {14, 5, 14} }
        local link = kit.corridor{ name = "link", a = west, b = east }
        kit.pillar_row{ name = "east_pillars", from = {7, 0, 0}, to = {13, 0, 0}, count = 2, height = 5 }
        for _, r in ipairs{ west, east, link } do kit.light_room{ room = r } end
        kit.spawn_points{ room = west, count = 4 }
        kit.spawn_points{ room = east, count = 4 }
        level.place{ kind = "item", class = "Shotgun", name = "item_shotgun", pos = {10, 0.5, 0} }
        level.place{ kind = "item", class = "Medical Kit", name = "item_medkit", pos = {-12, 0.5, 3} }
        level.player_start{ pos = {-10, 1, 0}, yaw = 90 }
        """;

    [Theory]
    [InlineData(512)]
    [InlineData(768)]
    [InlineData(1024)]
    public async Task Plan_Legend_Never_Covers_Axis_Labels_Or_The_Level(int size)
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using var dir = new TempDir();
        var host = new HeadlessAutomationHost(EditorSettingsSnapshot.Defaults, rfInstallDir: null, () => TestLevels.Fixed);
        using (host)
        {
            var c = new AutomationCommands(host);
            Assert.True((await c.ExecuteAsync(new AutomationRequest("new", null))).Ok);
            AutomationResponse run = await c.ExecuteAsync(new AutomationRequest("run", new JsonObject { ["name"] = "twin.lua", ["script"] = TwinArena }));
            Assert.True(run.Ok, run.Error?.Message);
            await host.BuildAsync(sealedBuild: true, bakeLighting: false, shadows: false, CancellationToken.None);

            string png = dir.File("twin.png");
            RenderSpec spec = RenderSpec.FromArgs(new JsonObject { ["out"] = png, ["width"] = size, ["height"] = size });
            var geometry = new RenderGeometryDto { Before = "built", Rendered = "built" };
            (byte[] rgba, RenderResult r) = LevelRenderer.Render(gd, "test", host.Document!, null, spec, null, geometry, png);
            File.WriteAllBytes(Path.Combine(ArtifactsDir, $"twin_plan_{size}.png"), Ged.Core.IO.Tex.PngWriter.Encode(r.Width, r.Height, rgba));

            Assert.Equal(size, r.Width);
            var (legend, labels) = r.Layout!.Value;
            (int lx, int ly, int lw, int lh) = Assert.NotNull(legend);
            Assert.NotEmpty(labels);
            static bool Overlaps((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b) =>
                a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
            foreach ((string text, (int X, int Y, int W, int H) rect, bool drawn) in labels)
            {
                Assert.False(Overlaps(rect, (lx, ly, lw, lh)), $"{size}px: the legend covers axis label '{text}'");
                Assert.True(drawn, $"{size}px: axis label '{text}' was not drawn (a corner panel is in its way)");
            }

            // Every x label along the top (one per grid line) is there, from the left edge on.
            Assert.Contains(labels, l => l.Text.StartsWith("x=-1", StringComparison.Ordinal));

            // The level's floor plan stays clear of the legend panel.
            foreach (Vector3 corner in new[] { new Vector3(-17, 0, -7), new Vector3(-17, 0, 7), new Vector3(17, 0, -7), new Vector3(17, 0, 7) })
            {
                Vector2 p = MetaToPixel(r, corner);
                Assert.False(p.X >= lx && p.X < lx + lw && p.Y >= ly && p.Y < ly + lh, $"{size}px: the legend covers the level corner {corner}");
                Assert.InRange(p.X, 0, size);
                Assert.InRange(p.Y, 0, size);
            }
        }
    }

    [Fact]
    public async Task Plan_Slice_Clips_The_Ceiling_On_OpenGl_As_On_Direct3D()
    {
        GraphicsDevice? gl;
        try
        {
            gl = new GraphicsDevice(GraphicsBackend.OpenGl);
        }
        catch (Exception)
        {
            return; // no OpenGL 3.3 context on this box
        }

        using (gl)
        {
            using var dir = new TempDir();
            string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
            using var host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
            await host.OpenAsync(level, CancellationToken.None);
            await host.BuildAsync(sealedBuild: true, bakeLighting: false, shadows: false, CancellationToken.None);
            RenderSpec spec = RenderSpec.FromArgs(new JsonObject { ["out"] = dir.File("gl.png"), ["mode"] = "rooms", ["overlays"] = "none" });
            var geometry = new RenderGeometryDto { Before = "built", Rendered = "built" };
            (byte[] rgba, RenderResult r) = LevelRenderer.Render(gl, "gl", host.Document!, null, spec, null, geometry, dir.File("gl.png"));
            File.WriteAllBytes(Path.Combine(ArtifactsDir, "room_plan_gl.png"), Ged.Core.IO.Tex.PngWriter.Encode(r.Width, r.Height, rgba));

            // With the near plane at the cut, the floor (not the ceiling's back face, nor nothing) shows
            // inside the room, and solid space outside it.
            Vector2 floor = MetaToPixel(r, new Vector3(-4f, 0f, 4f));
            Vector2 solid = MetaToPixel(r, new Vector3(10f, 0f, -4f)); // beside the corridor (the plan frames the geometry)
            int floorLum = Lum(rgba, r.Width, (int)floor.X, (int)floor.Y);
            int solidLum = Lum(rgba, r.Width, (int)solid.X, (int)solid.Y);
            Assert.True(floorLum - solidLum >= 50, $"GL: floor {floorLum} vs solid {solidLum}");
        }
    }

    [Fact]
    public async Task Plan_Region_Framing_Follows_The_Region_Not_The_Level()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        CliEnvironment env = Env(gd);

        (int code, string o, string e) = await CliTests.Cli(env, "render", level, "--out", dir.File("whole.png"), "--json");
        Assert.True(code == 0, o + e);
        JsonNode whole = JsonNode.Parse(o)!["result"]!;

        // A 4 x 3 m region in one corner of the 12 x 12 m room: the image must cover that region (plus the framing
        // margin), not the room.
        (code, o, e) = await CliTests.Cli(env, "render", level, "--region", "1,0,1,5,3,4", "--out", dir.File("corner.png"), "--json");
        Assert.True(code == 0, o + e);
        JsonNode r = JsonNode.Parse(o)!["result"]!;
        Assert.Equal("region", r["framedOn"]!.GetValue<string>());
        Assert.Equal(new[] { 1.0, 0.0, 1.0 }, r["framing"]!["min"]!.AsArray().Select(v => v!.GetValue<double>()));
        Assert.Equal(new[] { 5.0, 3.0, 4.0 }, r["framing"]!["max"]!.AsArray().Select(v => v!.GetValue<double>()));

        int w = r["width"]!.GetValue<int>(), h = r["height"]!.GetValue<int>();
        double wpp = r["worldPerPixel"]!.GetValue<double>();
        double x0 = r["origin"]![0]!.GetValue<double>(), z0 = r["origin"]![2]!.GetValue<double>();
        double x1 = x0 + (w * wpp), z1 = z0 - (h * wpp); // image right = +X, image down = -Z
        Assert.True(x0 <= 1 && x1 >= 5 && z1 <= 1 && z0 >= 4, $"the image (x {x0}..{x1}, z {z1}..{z0}) does not contain the region");
        Assert.True(x1 - x0 <= 4 * 1.6 && z0 - z1 <= 3 * 1.6, $"the image (x {x0}..{x1}, z {z1}..{z0}) is far larger than the 4 x 3 m region");
        Assert.True(wpp < whole["worldPerPixel"]!.GetValue<double>() / 2, "the region plan is not zoomed in relative to the whole-level plan");

        // Without --size the image takes the region's proportions (4:3); a given size is kept.
        Assert.Equal(1024, w);
        Assert.InRange(h, 760, 780);
        var (pw, ph, _) = PngTestReader.Read(dir.File("corner.png"));
        Assert.Equal((w, h), (pw, ph));
        (code, o, e) = await CliTests.Cli(env, "render", level, "--region", "1,0,1,5,3,4", "--size", "512x512", "--out", dir.File("fixed.png"), "--json");
        Assert.True(code == 0, o + e);
        Assert.Equal(512, JsonNode.Parse(o)!["result"]!["height"]!.GetValue<int>());
    }

    [Fact]
    public void Region_Image_Size_Follows_The_Region_Proportions_Within_Limits()
    {
        Assert.Equal((1024, 341), LevelRenderer.FitToRegion(Ged.Rendering.OrthoView.Top, new Vector3(0, 0, 0), new Vector3(30, 5, 10), 1024));
        Assert.Equal((512, 1024), LevelRenderer.FitToRegion(Ged.Rendering.OrthoView.Top, new Vector3(0, 0, 0), new Vector3(10, 99, 20), 1024));
        Assert.Equal((1024, 341), LevelRenderer.FitToRegion(Ged.Rendering.OrthoView.Top, new Vector3(0, 0, 0), new Vector3(100, 1, 1), 1024)); // clamped to 3:1
        Assert.Equal((1024, 1024), LevelRenderer.FitToRegion(Ged.Rendering.OrthoView.Top, new Vector3(-5, 0, -5), new Vector3(5, 0, 5), 1024));
        Assert.Equal((1024, 512), LevelRenderer.FitToRegion(Ged.Rendering.OrthoView.Front, new Vector3(0, 0, 0), new Vector3(20, 10, 1), 1024)); // x by y
    }

    [Fact]
    public async Task Same_Input_Renders_Identical_Png_Bytes_And_Json_Maps_Pixels()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        CliEnvironment env = Env(gd);
        (int code, string o, string e) = await CliTests.Cli(env, "render", level, "--out", dir.File("a.png"), "--json");
        Assert.True(code == 0, o + e);
        (code, _, e) = await CliTests.Cli(env, "render", level, "--out", dir.File("b.png"));
        Assert.True(code == 0, e);
        Assert.Equal(File.ReadAllBytes(dir.File("a.png")), File.ReadAllBytes(dir.File("b.png")));

        JsonNode result = JsonNode.Parse(o)!["result"]!;
        Assert.Equal("render", JsonNode.Parse(o)!["command"]!.GetValue<string>());
        Assert.Equal(dir.File("a.png"), result["path"]!.GetValue<string>());
        Assert.Equal(1024, result["width"]!.GetValue<int>());
        Assert.Equal("plan", result["view"]!.GetValue<string>());
        Assert.Equal("none", result["geometry"]!["before"]!.GetValue<string>()); // never built
        Assert.Equal("preview", result["geometry"]!["builtForRender"]!.GetValue<string>());
        Assert.Contains(result["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("preview build", StringComparison.Ordinal));
        Assert.Equal("rooms", result["modeUsed"]!.GetValue<string>()); // lit without lightmaps or textures
        Assert.True(result["worldPerPixel"]!.GetValue<double>() > 0);
        Assert.NotNull(result["origin"]);
        Assert.NotNull(result["legend"]);
        Assert.Equal(16, result["viewProjection"]!.AsArray().Count);

        // Without --size the plan takes the level's proportions on a 1024 px longest side.
        var (pw, ph, pixels) = PngTestReader.Read(dir.File("a.png"));
        Assert.Equal((result["width"]!.GetValue<int>(), result["height"]!.GetValue<int>()), (pw, ph));
        Assert.Equal(1024, Math.Max(pw, ph));
        Assert.True(Distinct(pixels) > 20);
    }

    [Fact]
    public async Task Rendering_Never_Modifies_The_Level_File()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        byte[] before = File.ReadAllBytes(level);
        (int code, _, string e) = await CliTests.Cli(Env(gd), "render", level, "--out", dir.File("p.png"), "--sealed", "--overlays", "all");
        Assert.True(code == 0, e);
        Assert.Equal(before, File.ReadAllBytes(level));
        Assert.False(File.Exists(Path.ChangeExtension(level, ".gedlayout.json")));
    }

    // ---- attach: the editor sends a snapshot; the CLI renders it -----------------------------

    [Fact]
    public async Task Attach_Renders_A_Chunked_Snapshot_Of_The_Editors_Level_Without_Touching_It()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        string pipe = "glacier-test-" + Guid.NewGuid().ToString("N");
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));

        // The stand-in editor: a session with the level open, built, then edited (geometry now stale).
        using var editor = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        await editor.OpenAsync(level, CancellationToken.None);
        await editor.BuildAsync(sealedBuild: true, bakeLighting: false, shadows: false, CancellationToken.None);
        new BrushEditor(editor.Document!).CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 4, Height = 3, Depth = 4, Air = true },
            new Vec3(0, 1.5f, -9), Mat3.Identity);
        EditorDocument doc = editor.Document!;
        long fingerprint = LevelOperations.BrushFingerprint(doc);
        Geometry compiled = LevelOperations.CompiledGeometry(doc)!;
        int faces = compiled.Faces.Count;
        int undo = doc.Undo.Position;
        bool dirty = doc.IsDirty;
        Assert.True(editor.BuildState.GeometryDirty);

        var commands = new AutomationCommands(editor) { SnapshotChunk = 64 * 1024 };
        var server = new BridgeServer(pipe, (req, ct) => commands.ExecuteAsync(req, ct));
        Assert.True(server.TryStart(out string? reason), reason);
        await using (server)
        {
            (int code, string o, string e) = await CliTests.Cli(Env(gd, pipe), "render", "--attach", "--out", dir.File("live.png"), "--json");
            Assert.True(code == 0, o + e);
            JsonNode result = JsonNode.Parse(o)!["result"]!;
            Assert.Equal("editor", result["source"]!.GetValue<string>());
            Assert.Equal("stale", result["geometry"]!["before"]!.GetValue<string>());
            Assert.Equal("preview", result["geometry"]!["builtForRender"]!.GetValue<string>());
            Assert.True(File.Exists(dir.File("live.png")));
            File.Copy(dir.File("live.png"), Path.Combine(ArtifactsDir, "room_attach_plan.png"), overwrite: true);

            // The new air box (z = -9) is in the render: the private preview build included it.
            JsonNode framing = result["framing"]!;
            Assert.True(framing["min"]![2]!.GetValue<double>() <= -10.9, framing.ToJsonString());
        }

        // Read-only: the editor's document is exactly as it was.
        Assert.Same(compiled, LevelOperations.CompiledGeometry(doc));
        Assert.Equal(faces, compiled.Faces.Count);
        Assert.Equal(fingerprint, LevelOperations.BrushFingerprint(doc));
        Assert.Equal(undo, doc.Undo.Position);
        Assert.Equal(dirty, doc.IsDirty);
        Assert.True(editor.BuildState.GeometryDirty);
    }

    [Fact]
    public async Task Snapshot_Streams_Chunks_With_A_Checksum_And_Rejects_Stale_Tokens()
    {
        using var dir = new TempDir();
        using var editor = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        await editor.OpenAsync(TestLevels.WriteRoomLevel(dir.File("room.rfl")), CancellationToken.None);
        var commands = new AutomationCommands(editor) { SnapshotChunk = 1000 };
        AutomationResponse first = await commands.ExecuteAsync(new AutomationRequest("snapshot"));
        Assert.True(first.Ok, first.Error?.Message);
        int size = first.Result!["size"]!.GetValue<int>();
        Assert.True(size > 1000);
        Assert.False(first.Result["complete"]!.GetValue<bool>());
        string token = first.Result["token"]!.GetValue<string>();
        var data = new MemoryStream();
        data.Write(Convert.FromBase64String(first.Result["data"]!.GetValue<string>()));
        AutomationResponse next = first;
        while (!next.Result!["complete"]!.GetValue<bool>())
        {
            next = await commands.ExecuteAsync(new AutomationRequest("snapshot", new JsonObject { ["token"] = token, ["offset"] = (double)data.Length }));
            Assert.True(next.Ok, next.Error?.Message);
            data.Write(Convert.FromBase64String(next.Result!["data"]!.GetValue<string>()));
        }

        byte[] bytes = data.ToArray();
        Assert.Equal(size, bytes.Length);
        Assert.Equal(first.Result["sha256"]!.GetValue<string>(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal(editor.Document!.Rfl.Save(updateTimestamp: false), bytes);

        // Fully delivered: the token is released.
        AutomationResponse stale = await commands.ExecuteAsync(new AutomationRequest("snapshot", new JsonObject { ["token"] = token, ["offset"] = 0.0 }));
        Assert.False(stale.Ok);
        Assert.Equal(AutomationErrorCodes.Usage, stale.Error!.Code);
    }

    // ---- corpus + golden example: artifacts for inspection -----------------------------------

    [Fact]
    public async Task Dm01_Plan_Storeys_Iso_And_Perspective_Render_To_Artifacts()
    {
        string? dm01 = TestLevels.Corpus("dm01.rfl");
        using GraphicsDevice? gd = TryDevice();
        if (dm01 is null || gd is null)
        {
            return;
        }

        string? rf = RfInstall();
        CliEnvironment env = Env(gd);
        string[] Rf(params string[] a) => rf is null ? a : a.Concat(new[] { "--rf-dir", rf }).ToArray();
        string Out(string name) => Path.Combine(ArtifactsDir, name);

        (string File, string[] Args)[] shots =
        {
            ("dm01_plan.png", new[] { "render", dm01, "--out", Out("dm01_plan.png"), "--json" }),
            ("dm01_plan_lower.png", new[] { "render", dm01, "--plan", "--y", "-11", "--out", Out("dm01_plan_lower.png"), "--json" }),
            ("dm01_iso.png", new[] { "render", dm01, "--view", "iso", "--out", Out("dm01_iso.png"), "--json" }),
            ("dm01_camera.png", new[] { "render", dm01, "--camera", "5,-3,-40", "--look", "10,-9,0", "--fov", "75", "--out", Out("dm01_camera.png"), "--json" }),
            ("dm01_top_rooms.png", new[] { "render", dm01, "--view", "top", "--mode", "rooms", "--overlays", "grid,scale", "--out", Out("dm01_top_rooms.png"), "--json" }),
        };

        var results = new Dictionary<string, JsonNode>();
        foreach ((string file, string[] args) in shots)
        {
            (int code, string o, string e) = await CliTests.Cli(env, Rf(args));
            Assert.True(code == 0, $"{file}: {o}{e}");
            results[file] = JsonNode.Parse(o)!["result"]!;
            var (w, h, px) = PngTestReader.Read(Out(file));
            Assert.Equal(1024, Math.Max(w, h)); // orthographic views take the level's proportions
            Assert.True(file.Contains("plan", StringComparison.Ordinal) || file.Contains("top", StringComparison.Ordinal) || (w, h) == (1024, 1024), file);
            Assert.True(Distinct(px) > 40, $"{file} is (nearly) blank");
        }

        // Default plan height = the floor under the player start; the lower storey is a different slice.
        JsonNode plan = results["dm01_plan.png"];
        Assert.StartsWith("floor under the player start", plan["plan"]!["ySource"]!.GetValue<string>());
        Assert.Equal(-11, results["dm01_plan_lower.png"]["plan"]!["y"]!.GetValue<double>());
        Assert.True(plan["objects"]!.AsArray().Count > 10);
        Assert.Equal("perspective", results["dm01_camera.png"]["projection"]!.GetValue<string>());

        // Every listed glyph pixel agrees with the metadata mapping of its world position.
        var r = new RenderResult
        {
            Width = 1024,
            Height = 1024,
            Origin = plan["origin"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray(),
            PixelAxes = new RenderPixelAxesDto
            {
                X = plan["pixelAxes"]!["x"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray(),
                Y = plan["pixelAxes"]!["y"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray(),
            },
        };
        foreach (JsonNode? obj in plan["objects"]!.AsArray())
        {
            JsonArray pos = obj!["pos"]!.AsArray();
            Vector2 p = MetaToPixel(r, new Vector3((float)pos[0]!.GetValue<double>(), (float)pos[1]!.GetValue<double>(), (float)pos[2]!.GetValue<double>()));
            Assert.InRange(obj["pixel"]![0]!.GetValue<double>(), p.X - 2, p.X + 2);
            Assert.InRange(obj["pixel"]![1]!.GetValue<double>(), p.Y - 2, p.Y + 2);
        }
    }

    [Fact]
    public async Task Kit_Golden_Example_Builds_And_Renders_A_Readable_Plan()
    {
        string? root = TestLevels.RepoRoot;
        using GraphicsDevice? gd = TryDevice();
        if (root is null || gd is null)
        {
            return;
        }

        string script = Path.Combine(root, "scripts", "examples", "kit-tiny-dm.lua");
        if (!File.Exists(script))
        {
            return;
        }

        string? rf = RfInstall();
        string[] Rf(params string[] a) => rf is null ? a : a.Concat(new[] { "--rf-dir", rf }).ToArray();
        using var dir = new TempDir();
        string level = dir.File("kit.rfl");
        CliEnvironment env = Env(gd);
        (int code, string o, string e) = await CliTests.Cli(env, Rf("new", "--out", level));
        Assert.True(code == 0, o + e);
        (code, o, e) = await CliTests.Cli(env, Rf("run", script, level, "--save"));
        Assert.True(code == 0, o + e);
        (code, o, e) = await CliTests.Cli(env, Rf("build", level, "--sealed", "--light", "--save"));
        Assert.True(code == 0, o + e);
        string png = Path.Combine(ArtifactsDir, "kit_plan.png");
        (code, o, e) = await CliTests.Cli(env, Rf("render", level, "--out", png, "--overlays", "all", "--json"));
        Assert.True(code == 0, o + e);
        JsonNode result = JsonNode.Parse(o)!["result"]!;
        Assert.Equal("built", result["geometry"]!["before"]!.GetValue<string>());
        Assert.Equal("lit", result["modeUsed"]!.GetValue<string>());
        Assert.Equal(0, result["holes"]!.GetValue<int>()); // the golden example is sealed
        JsonArray objects = result["objects"]!.AsArray();
        Assert.Contains(objects, x => x!["kind"]!.GetValue<string>() == "playerStart");
        Assert.True(objects.Count(x => x!["kind"]!.GetValue<string>() == "mpRespawnPoint") >= 4);
        var (_, _, px) = PngTestReader.Read(png);
        Assert.True(Distinct(px) > 40);
    }

    // ---- errors ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("render a.rfl")] // no --out
    [InlineData("render a.rfl --out a.jpg")]
    [InlineData("render a.rfl --out a.png --view sideways")]
    [InlineData("render a.rfl --out a.png --plan --view iso")]
    [InlineData("render a.rfl --out a.png --view iso --y 3")]
    [InlineData("render a.rfl --out a.png --camera 1,2,3")]
    [InlineData("render a.rfl --out a.png --camera 1,2 --look 0,0,0")]
    [InlineData("render a.rfl --out a.png --camera 1,2,3 --look 1,2,3")]
    [InlineData("render a.rfl --out a.png --size 5000x100")]
    [InlineData("render a.rfl --out a.png --size big")]
    [InlineData("render a.rfl --out a.png --mode shiny")]
    [InlineData("render a.rfl --out a.png --overlays grid,sparkles")]
    [InlineData("render a.rfl --out a.png --overlays grid --no-overlays")]
    [InlineData("render a.rfl --out a.png --room 1 --uid 2")]
    [InlineData("render a.rfl --out a.png --fov 2 --view iso")]
    [InlineData("render a.rfl --out a.png --thickness 0")]
    [InlineData("render a.rfl --out a.png --sealed --rebuild")]
    [InlineData("render a.rfl --out a.png --save")]
    [InlineData("lint a.rfl --plan")]
    public async Task Usage_Errors_Exit_2(string line)
    {
        (int code, _, string e) = await CliTests.Cli(Env(null), line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.True(code == 2, $"'{line}' exited {code}: {e}");
    }

    [Fact]
    public async Task No_Gpu_Device_Is_A_Clear_Failure_With_Exit_1()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        var env = new CliEnvironment
        {
            PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
            SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
            GetEnvironmentVariable = _ => null,
            RenderDevice = () => throw new AutomationException(RenderCommand.RenderUnavailable, "No GPU device is available for rendering (test)."),
        };
        (int code, string o, string e) = await CliTests.Cli(env, "render", level, "--out", dir.File("x.png"), "--json");
        Assert.Equal(1, code);
        Assert.Equal(RenderCommand.RenderUnavailable, JsonNode.Parse(o)!["error"]!["code"]!.GetValue<string>());
        Assert.False(File.Exists(dir.File("x.png")));
        Assert.Equal(string.Empty, e);
    }

    [Fact]
    public async Task Render_Outside_The_Allowed_Folders_Or_Of_A_Missing_Room_Fails_Cleanly()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        using var host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        using var elsewhere = new TempDir();
        AutomationResponse r = await RenderCommand.ExecuteAsync(host, new AutomationRequest("render", new JsonObject
        {
            ["level"] = level,
            ["out"] = elsewhere.File("x.png"),
        }), () => throw new InvalidOperationException("no GPU needed"));
        Assert.False(r.Ok);
        Assert.Equal(AutomationErrorCodes.PathNotAllowed, r.Error!.Code);

        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        r = await RenderCommand.ExecuteAsync(host, new AutomationRequest("render", new JsonObject
        {
            ["level"] = level,
            ["out"] = dir.File("x.png"),
            ["room"] = 99.0,
        }), () => (gd, "test"));
        Assert.False(r.Ok);
        Assert.Equal(AutomationErrorCodes.Usage, r.Error!.Code);
        Assert.Contains("Room 99 does not exist", r.Error.Message);
    }
}
