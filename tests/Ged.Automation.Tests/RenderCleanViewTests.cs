using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation.Rendering;
using Ged.Core.Editor;
using Ged.Rendering.Graphics;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// Clean in-game views from <c>glacier-cli render</c>: camera and iso views draw no editor object icons by default (and
/// none with overlays none), show-sky faces show the level's sky room drawn from its centre (a flat sky without one), the
/// sky room is never framed or drawn as part of the level, and iso and orthographic views frame the drawn geometry.
/// Images are written to tests/artifacts/render for inspection.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class RenderCleanViewTests
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

    /// <summary>A 16 x 16 m yard open to the sky (show-sky ceiling) with a sky room far off, a light, spawns and an item.</summary>
    private const string SkyYard = """
        local kit = ged.import("kit")
        level.props:set("multiplayer", true)
        local yard = kit.room{ name = "yard", pos = {0, 0, 0}, size = {16, 8, 16} }
        kit.sky_room{ name = "sky", pos = {400, 0, 0}, size = {12, 12, 12}, open = { yard } }
        level.place{ kind = "light", pos = {0, 4, 4}, props = { range = 8 } }
        level.place{ kind = "respawn", pos = {-3, 0, 4}, props = { red_team = true, blue_team = true } }
        level.place{ kind = "respawn", pos = {3, 0, 4}, props = { red_team = true, blue_team = true } }
        level.place{ kind = "item", class = "Medical Kit", pos = {0, 0.5, 4} }
        level.player_start{ pos = {0, 1, -6}, yaw = 0 }
        """;

    /// <summary>The same yard with a show-sky ceiling and no sky room.</summary>
    private const string BareYard = """
        local kit = ged.import("kit")
        local yard = kit.room{ name = "yard", pos = {0, 0, 0}, size = {16, 8, 16} }
        for _, b in ipairs(yard.brushes or { yard.brush }) do
          if b.air then for _, f in ipairs(b:faces_facing("up")) do f:set_flag("show_sky", true) end end
        end
        level.place{ kind = "respawn", pos = {0, 0, 4} }
        level.player_start{ pos = {0, 1, -6}, yaw = 0 }
        """;

    private static async Task<HeadlessAutomationHost> Build(string script)
    {
        var host = new HeadlessAutomationHost(EditorSettingsSnapshot.Defaults, rfInstallDir: null, () => TestLevels.Fixed);
        var c = new AutomationCommands(host);
        Assert.True((await c.ExecuteAsync(new AutomationRequest("new", null))).Ok);
        AutomationResponse run = await c.ExecuteAsync(new AutomationRequest("run", new JsonObject { ["name"] = "yard.lua", ["script"] = script }));
        Assert.True(run.Ok, run.Error?.Message);
        await host.BuildAsync(sealedBuild: true, bakeLighting: false, shadows: false, CancellationToken.None);
        return host;
    }

    private static (byte[] Rgba, RenderResult Result) Render(GraphicsDevice gd, EditorDocument doc, JsonObject args, string artifact)
    {
        string png = Path.Combine(Path.GetTempPath(), "ged-clean-" + Guid.NewGuid().ToString("N") + ".png");
        args["out"] = png;
        RenderSpec spec = RenderSpec.FromArgs(args);
        var geometry = new RenderGeometryDto { Before = "built", Rendered = "built" };
        (byte[] rgba, RenderResult r) = LevelRenderer.Render(gd, "test", doc, null, spec, null, geometry, png);
        File.WriteAllBytes(Path.Combine(ArtifactsDir, artifact), Ged.Core.IO.Tex.PngWriter.Encode(r.Width, r.Height, rgba));
        return (rgba, r);
    }

    private static Vector2 Project(RenderResult r, Vector3 p)
    {
        double[] m = r.ViewProjection;
        double cx = (p.X * m[0]) + (p.Y * m[4]) + (p.Z * m[8]) + m[12];
        double cy = (p.X * m[1]) + (p.Y * m[5]) + (p.Z * m[9]) + m[13];
        double cw = (p.X * m[3]) + (p.Y * m[7]) + (p.Z * m[11]) + m[15];
        return new Vector2((float)(((cx / cw * 0.5) + 0.5) * r.Width), (float)((0.5 - (cy / cw * 0.5)) * r.Height));
    }

    private static (byte R, byte G, byte B) Px(byte[] rgba, int w, int x, int y)
    {
        int i = ((y * w) + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }

    [Fact]
    public void Icon_Defaults_Follow_The_View_And_Overlays_None()
    {
        static bool Icons(JsonObject a)
        {
            a["out"] = "x.png";
            return RenderSpec.FromArgs(a).Icons;
        }

        Assert.False(Icons(new JsonObject { ["camera"] = "0,1,0", ["look"] = "0,1,5" }));
        Assert.False(Icons(new JsonObject { ["view"] = "iso" }));
        Assert.True(Icons(new JsonObject { ["view"] = "top" }));
        Assert.False(Icons(new JsonObject { ["view"] = "top", ["overlays"] = "none" }));
        Assert.True(Icons(new JsonObject { ["camera"] = "0,1,0", ["look"] = "0,1,5", ["icons"] = true }));
        Assert.False(Icons(new JsonObject { ["view"] = "front", ["icons"] = "off" }));
        Assert.True(Icons(new JsonObject { ["view"] = "iso", ["overlays"] = "icons" })); // the MCP form
        Assert.False(Icons(new JsonObject { ["view"] = "top", ["overlays"] = "grid,noicons" }));
        Assert.True(Icons(new JsonObject { ["view"] = "top", ["overlays"] = "grid,scale" })); // a side view keeps them
        AutomationException ex = Assert.Throws<AutomationException>(() => Icons(new JsonObject { ["view"] = "plan", ["icons"] = true }));
        Assert.Contains("icons", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Camera_Views_Are_Clean_By_Default_And_Show_Icons_On_Request()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using HeadlessAutomationHost host = await Build(SkyYard);
        JsonObject Args(bool? icons)
        {
            var a = new JsonObject { ["camera"] = "0,1.7,-7", ["look"] = "0,2,4", ["width"] = 640, ["height"] = 400, ["mode"] = "rooms" };
            if (icons is bool b)
            {
                a["icons"] = b;
            }

            return a;
        }

        (byte[] clean, RenderResult rc) = Render(gd, host.Document!, Args(null), "clean_camera_default.png");
        (byte[] none, _) = Render(gd, host.Document!, new JsonObject { ["camera"] = "0,1.7,-7", ["look"] = "0,2,4", ["width"] = 640, ["height"] = 400, ["mode"] = "rooms", ["overlays"] = "none" }, "clean_camera_overlays_none.png");
        (byte[] icons, RenderResult ri) = Render(gd, host.Document!, Args(true), "clean_camera_icons.png");
        Assert.False(rc.Icons);
        Assert.True(ri.Icons);
        Assert.Equal(clean, none); // overlays none is the clean view

        // The light's icon is drawn at its projected pixel only when asked for.
        Vector2 light = Project(rc, new Vector3(0, 4, 4));
        int differing = 0;
        for (int dy = -12; dy <= 12; dy++)
        {
            for (int dx = -12; dx <= 12; dx++)
            {
                int x = (int)light.X + dx, y = (int)light.Y + dy;
                if (Px(clean, rc.Width, x, y) != Px(icons, ri.Width, x, y))
                {
                    differing++;
                }
            }
        }

        Assert.True(differing > 20, $"the light icon does not show with icons on ({differing} pixels differ around it)");
    }

    [Fact]
    public async Task Show_Sky_Faces_Show_The_Sky_Room_In_A_Camera_View()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using HeadlessAutomationHost host = await Build(SkyYard);
        Ged.Core.Model.Geometry geo = LevelOperations.CompiledGeometry(host.Document!)!;
        int skyRoom = Enumerable.Range(0, geo.Rooms.Count).Single(i => geo.Rooms[i].IsSkyroom != 0);

        // Looking up at the yard's show-sky ceiling: the sky room (drawn from its centre, the same way up) fills it, in
        // the sky room's own colour in this untextured room-colour render.
        (byte[] rgba, RenderResult r) = Render(gd, host.Document!, new JsonObject
        {
            ["camera"] = "0,1.7,0", ["look"] = "0,9,0.5", ["width"] = 320, ["height"] = 320, ["mode"] = "rooms",
        }, "clean_sky_room_ceiling.png");
        Assert.Equal($"sky room {skyRoom}", r.Sky);
        uint c = Ged.Rendering.Scene.Palette.RoomColor(skyRoom);
        (byte R, byte G, byte B) skyColor = ((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        Assert.Equal(skyColor, Px(rgba, r.Width, r.Width / 2, r.Height / 2));

        // Without a sky room the faces show a flat sky; a view with no show-sky face in it has no sky.
        using HeadlessAutomationHost bare = await Build(BareYard);
        (byte[] fill, RenderResult rf) = Render(gd, bare.Document!, new JsonObject
        {
            ["camera"] = "0,1.7,0", ["look"] = "0,9,0.5", ["width"] = 320, ["height"] = 320, ["mode"] = "rooms",
        }, "clean_sky_fill_ceiling.png");
        Assert.Equal("fill", rf.Sky);
        (byte R, byte G, byte B) mid = Px(fill, rf.Width, rf.Width / 2, rf.Height / 2);
        Assert.InRange(mid.B, LevelRenderer.SkyFillTop.B, LevelRenderer.SkyFillHorizon.B);
        Assert.True(mid.B > mid.R, $"the sky fill is not sky-coloured: {mid}");
        (_, RenderResult floor) = Render(gd, bare.Document!, new JsonObject
        {
            ["camera"] = "0,1.7,0", ["look"] = "0,0,0.5", ["width"] = 160, ["height"] = 160, ["mode"] = "rooms", ["fov"] = 30,
        }, "clean_sky_none_floor.png");
        Assert.Null(floor.Sky);
    }

    [Fact]
    public async Task Iso_Frames_The_Level_Not_Its_Far_Sky_Room()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using HeadlessAutomationHost host = await Build(SkyYard);
        (byte[] rgba, RenderResult r) = Render(gd, host.Document!, new JsonObject { ["view"] = "iso", ["width"] = 512, ["height"] = 512, ["mode"] = "rooms" }, "clean_iso_sky_room_level.png");
        Assert.False(r.Icons);

        // The yard (16 m) fills the frame: the drawn pixels span most of the image on its long axis, and the sky room
        // 400 m away is neither framed nor drawn.
        int x0 = r.Width, x1 = -1, y0 = r.Height, y1 = -1;
        (byte R, byte G, byte B) bg = Px(rgba, r.Width, 0, 0);
        for (int y = 0; y < r.Height; y++)
        {
            for (int x = 0; x < r.Width; x++)
            {
                if (Px(rgba, r.Width, x, y) != bg)
                {
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y);
                }
            }
        }

        Assert.True(Math.Max(x1 - x0, y1 - y0) >= r.Width * 0.8, $"the level spans only {x1 - x0} x {y1 - y0} px of {r.Width}");
        Assert.True(Math.Abs(((x0 + x1) / 2.0) - (r.Width / 2.0)) < r.Width * 0.08, "the level is not centred horizontally");
        Assert.True(r.Framing.Max[0] < 100, "the framing reaches the sky room");
    }

    [Fact]
    public async Task Plan_Without_A_Size_Takes_The_Level_Proportions_And_Keeps_The_Legend_Off_It()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        // An L: a long hall and a side room at one end, so a corner of the frame is empty for the legend.
        using HeadlessAutomationHost host = await Build("""
            local kit = ged.import("kit")
            local hall = kit.room{ name = "hall", pos = {0, 0, 0}, size = {60, 5, 10} }
            local side = kit.room{ name = "side", pos = {25, 0, 20}, size = {10, 5, 12} }
            kit.corridor{ a = hall, b = side, width = 4 }
            level.place{ kind = "respawn", pos = {-20, 0, 0} }
            level.player_start{ pos = {-25, 1, 0}, yaw = 90 }
            """);
        (byte[] rgba, RenderResult r) = Render(gd, host.Document!, new JsonObject { ["maxSide"] = 1024 }, "clean_plan_l_shape.png");
        Assert.Equal(1024, r.Width);
        Assert.InRange(r.Height, 500, 700); // 60 x ~35 m of geometry: the image takes its proportions

        // Tight framing: the level's 61 m width fills the image (not shrunk beside or under the legend) ...
        Assert.InRange(r.WorldPerPixel, 0.055, 0.075);

        // ... and the legend sits clear of the level's floor.
        (int lx, int ly, int lw, int lh) = Assert.NotNull(r.Layout!.Value.Legend);
        foreach (Vector3 p in new[] { new Vector3(-29, 0, -4), new Vector3(-29, 0, 4), new Vector3(29, 0, 4), new Vector3(28, 0, 24), new Vector3(0, 0, 0) })
        {
            var o = new Vector3((float)r.Origin![0], (float)r.Origin[1], (float)r.Origin[2]);
            float px = (p.X - o.X) / (float)r.WorldPerPixel, py = (o.Z - p.Z) / (float)r.WorldPerPixel;
            Assert.False(px >= lx && px < lx + lw && py >= ly && py < ly + lh, $"the legend covers the level at {p}");
        }

        // A region far bigger than the storey frames the part of it the slice has geometry in.
        (_, RenderResult rr) = Render(gd, host.Document!, new JsonObject { ["maxSide"] = 1024, ["region"] = "-200,-10,-200,200,20,200" }, "clean_plan_l_shape_big_region.png");
        Assert.Equal("region (geometry in the slice)", rr.FramedOn);
        Assert.InRange(rr.Framing.Min[0], -31, -29);
        Assert.InRange(rr.Framing.Max[2], 25, 27);
        Assert.InRange(rr.WorldPerPixel, 0.055, 0.075);
    }
}
