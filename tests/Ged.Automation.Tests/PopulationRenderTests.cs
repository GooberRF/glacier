using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation.Rendering;
using Ged.Rendering.Graphics;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// Render evidence for the population kit (kit.dress, kit.light_room fixtures, kit.spawn_points,
/// kit.items): a dressed industrial yard and a dressed control room, drawn as a plan view and two
/// eye-height close-ups, each next to the same rooms left bare. Images go to tests/artifacts/render
/// (population_*.png) for eyeballing; the test asserts the dressing is visible (a clear share of the
/// close-up pixels differs from the bare rooms) and that the plan shows the dressing inside the rooms.
/// Skips without a GPU device; textures come from the RF install when one is configured.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class PopulationRenderTests
{
    private const string Rooms = """
        local kit = ged.import("kit")
        level.props:set_many{ level_name = "Dressed yard", multiplayer = true, ambient_color = {70, 70, 74} }
        local yard = kit.room{ name = "yard", pos = {0, 0, 0}, size = {32, 8, 24},
          textures = { floor = "cem_concreteflr01.tga", wall = "cem_cement_panel05.tga", ceiling = "mtl_grated_ceiling01.tga" } }
        local ctrl = kit.room{ name = "ctrl", pos = {27, 0, 0}, size = {20, 4.5, 14},
          textures = { floor = "mtl_cleanpanel00.tga", wall = "mtl_cleanpanel01.tga", ceiling = "mtl_airceilinglite01.tga" } }
        kit.doorway{ name = "yard_ctrl", a = yard, b = ctrl, textures = { floor = "mtl_cleanpanel00.tga", wall = "mtl_cleanpanel01.tga", ceiling = "mtl_cleanpanel01.tga" } }
        kit.light_room{ room = yard, theme = "industrial" }
        kit.light_room{ room = ctrl, theme = "control" }
        level.player_start{ pos = {-12, 1, -8}, yaw = 45 }
        """;

    private const string Dressing = """
        kit.dress{ room = yard, theme = "industrial", seed = 5 }
        kit.dress{ room = ctrl, theme = "control", seed = 5 }
        kit.spawn_points{ rooms = { yard, ctrl }, count = 6 }
        kit.items{ rooms = { yard, ctrl }, seed = 2 }
        """;

    private static readonly (string Name, JsonObject Args)[] Views =
    {
        ("plan", new JsonObject { ["width"] = 1100, ["height"] = 700 }),
        ("yard_eye", new JsonObject { ["width"] = 1024, ["height"] = 640, ["camera"] = "14,2.0,10", ["look"] = "-6,0.5,-4", ["overlays"] = "none" }),
        ("yard_eye2", new JsonObject { ["width"] = 1024, ["height"] = 640, ["camera"] = "-14,1.7,-10", ["look"] = "4,1.0,4", ["overlays"] = "none" }),
        ("ctrl_eye", new JsonObject { ["width"] = 1024, ["height"] = 640, ["camera"] = "35,1.7,5", ["look"] = "22,1.0,-2", ["overlays"] = "none" }),
    };

    private static string ArtifactsDir()
    {
        string dir = TestLevels.RepoRoot is { } root
            ? Path.Combine(root, "tests", "artifacts", "render")
            : Path.Combine(Path.GetTempPath(), "ged-render-artifacts");
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    private static async Task<Dictionary<string, byte[]>> RenderLevel(GraphicsDevice gd, string script, string prefix)
    {
        using var host = new HeadlessAutomationHost(EditorSettingsSnapshot.Defaults, RfInstall(), () => TestLevels.Fixed);
        var c = new AutomationCommands(host);
        Assert.True((await c.ExecuteAsync(new AutomationRequest("new", null))).Ok);
        AutomationResponse run = await c.ExecuteAsync(new AutomationRequest("run", new JsonObject { ["name"] = prefix + ".lua", ["script"] = script, ["timeout"] = 120 }));
        Assert.True(run.Ok, run.Error?.Message);
        await host.BuildAsync(sealedBuild: true, bakeLighting: true, shadows: false, CancellationToken.None);

        var images = new Dictionary<string, byte[]>();
        foreach ((string name, JsonObject args) in Views)
        {
            string png = Path.Combine(ArtifactsDir(), $"{prefix}_{name}.png");
            var spec = (JsonObject)args.DeepClone();
            spec["out"] = png;
            RenderSpec rs = RenderSpec.FromArgs(spec);
            var geometry = new RenderGeometryDto { Before = "built", Rendered = "built" };
            (byte[] rgba, RenderResult r) = LevelRenderer.Render(gd, "test", host.Document!, host.Assets, rs, null, geometry, png);
            File.WriteAllBytes(png, Ged.Core.IO.Tex.PngWriter.Encode(r.Width, r.Height, rgba));
            images[name] = rgba;
        }

        return images;
    }

    private static double DiffShare(byte[] a, byte[] b)
    {
        int differ = 0, n = Math.Min(a.Length, b.Length) / 4;
        for (int i = 0; i < n; i++)
        {
            int d = Math.Abs(a[i * 4] - b[i * 4]) + Math.Abs(a[(i * 4) + 1] - b[(i * 4) + 1]) + Math.Abs(a[(i * 4) + 2] - b[(i * 4) + 2]);
            if (d > 48)
            {
                differ++;
            }
        }

        return differ / (double)n;
    }

    [Fact]
    public async Task Dressed_Yard_And_Control_Room_Render_With_Visible_Dressing()
    {
        GraphicsDevice? gd;
        try
        {
            gd = RenderDevices.Create().Device;
        }
        catch (AutomationException)
        {
            return; // no GPU device
        }

        using (gd)
        {
            Dictionary<string, byte[]> bare = await RenderLevel(gd, Rooms, "population_bare");
            Dictionary<string, byte[]> dressed = await RenderLevel(gd, Rooms + Dressing, "population_dressed");
            foreach (string view in new[] { "yard_eye", "yard_eye2", "ctrl_eye" })
            {
                double share = DiffShare(bare[view], dressed[view]);
                Assert.True(share >= 0.05, $"{view}: only {share:P1} of the pixels changed with the dressing");
            }

            Assert.True(DiffShare(bare["plan"], dressed["plan"]) >= 0.02, "the plan view shows no dressing");
        }
    }
}
