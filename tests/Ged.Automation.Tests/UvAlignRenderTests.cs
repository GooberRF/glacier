using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Automation.Rendering;
using Ged.Cli;
using Ged.Core.Analysis;
using Ged.Core.IO.Rfl;
using Ged.Core.Spatial;
using Ged.Rendering.Graphics;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// Render evidence for mapper-style texture alignment (level.uv_align, kit.defaults.uv_align): one kit scene — a hall
/// whose floor sits off the 4 m tile grid, a side room, stairs, a platform with a ramp, a pillar row and a rock canyon
/// with boulders — is built twice, with the kit's alignment off (the world box map) and on, sealed and rendered
/// textured from the same cameras into <c>tests/artifacts/render/uv_align_{off,on}_*.png</c> for eyeballing. The
/// compiled geometry of each is measured (<see cref="UvQuality"/>): aligned, no wall reads mirrored and more wall
/// corners wrap without a jump; box mapped, about half the walls are mirrored. Skips without a GPU device or an RF install.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class UvAlignRenderTests
{
    private const string Scene = """
        local kit = ged.import("kit")
        kit.defaults.uv_align = ALIGN
        local wall = "mtl_outerwall02.tga"
        local hall = kit.room{ name = "hall", pos = {0, 1.5, 0}, size = {20, 4.5, 16}, textures = { floor = "mtl_merc_floor01.tga", wall = wall, ceiling = "mtl_dirtytechceiling01.tga" } }
        local side = kit.room{ name = "side", pos = {0, 3, 15}, size = {12, 3.5, 10}, textures = { floor = "cem_dark_floor01.tga", wall = "cem_bunker04.tga", ceiling = "cem_cementflr01.tga" } }
        kit.doorway{ a = hall, b = side, width = 3, height = 3, floor_y = 3 }
        kit.stairs{ name = "st", pos = {-6, 1.5, 3}, dir = "+z", rise = 1.5, textures = { floor = "mtl_merc_floor01.tga", wall = wall, ceiling = wall } }
        kit.platform{ name = "pf", pos = {5, 1.5, -3}, size = {6, 5}, height = 2, textures = { floor = "mtl_merc_floor01.tga", wall = wall, ceiling = wall } }
        kit.ramp{ name = "rp", pos = {5, 1.5, 2.5}, dir = "-z", rise = 2, run = 3, width = 3, textures = { floor = "mtl_grittymetal.tga", wall = wall, ceiling = wall } }
        kit.pillar_row{ name = "pl", from = {-6, 1.5, -5}, to = {2, 1.5, -5}, count = 3, height = 4.5, size = 1.2, textures = { floor = wall, wall = wall, ceiling = wall } }
        local cy = kit.canyon{ name = "cy", path = { {0, 0, -16}, {12, 0, -30}, {32, 0, -32} }, width = 10, height = 14, seed = 7 }
        kit.boulders{ name = "bo", along = cy, count = 6, seed = 3 }
        kit.sky_room{}
        level.player_start{ pos = {0, 1.5, 0}, yaw = 0 }
        return "holes = " .. ops.holes().count
        """;

    private static readonly (string Name, string Camera, string Look, string Fov)[] Shots =
    {
        ("corner", "7,3.2,5", "-10,4.5,-8", "70"),
        ("stairs", "-2,3.2,-1", "-6,2.3,4", "60"),
        ("ramp", "10,3.2,7", "5,2.5,-1", "60"),
        ("side_room", "4,4.7,12", "-6,5.5,19", "70"),
        ("canyon", "1,1.7,-17", "14,4,-30", "70"),
    };

    private static string ArtifactsDir()
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

    private static CliEnvironment Env(GraphicsDevice device) => new()
    {
        PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Clock = () => TestLevels.Fixed,
        RenderDevice = () => (device, "test device"),
    };

    private static int Distinct(byte[] rgba)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < rgba.Length; i += 4 * 7)
        {
            set.Add(rgba[i] | (rgba[i + 1] << 8) | (rgba[i + 2] << 16));
        }

        return set.Count;
    }

    private static async Task<string> Build(CliEnvironment env, string rf, TempDir dir, bool align)
    {
        string name = align ? "uv_on" : "uv_off";
        string path = dir.File(name + ".lua");
        await File.WriteAllTextAsync(path, Scene.Replace("ALIGN", align ? "true" : "false", StringComparison.Ordinal));
        string level = dir.File(name + ".rfl");
        (int code, string o, string e) = await CliTests.Cli(env, "run", path, "--out", level, "--build", "--rf-dir", rf);
        Assert.True(code == 0, o + e);
        Assert.Contains("holes = 0", o, StringComparison.Ordinal);
        return level;
    }

    private static JsonObject Measure(string level, string rf)
    {
        using var vfs = Ged.Core.Assets.GameMount.Mount(rf);
        var sizes = new Ged.Core.Assets.TextureSizeLookup(vfs);
        RflFile rfl = RflFile.Load(level);
        rfl.ParseAllKnownSections();
        return UvQuality.Measure(UvQuality.FacesOf(SpatialLevel.StoredGeometry(rfl)!), t => sizes.Get(t));
    }

    [Fact]
    public async Task Aligned_Kit_Scene_Renders_And_Measures_Unmirrored_With_Wrapped_Corners()
    {
        string? rf = RfInstall();
        using GraphicsDevice? gd = TryDevice();
        if (TestLevels.RepoRoot is null || rf is null || gd is null)
        {
            return;
        }

        CliEnvironment env = Env(gd);
        using var dir = new TempDir();
        string off = await Build(env, rf, dir, align: false);
        string on = await Build(env, rf, dir, align: true);

        foreach ((string name, string camera, string look, string fov) in Shots)
        {
            var px = new List<byte[]>();
            foreach ((string level, string tag) in new[] { (off, "off"), (on, "on") })
            {
                string png = Path.Combine(ArtifactsDir(), $"uv_align_{tag}_{name}.png");
                (int code, string o, string e) = await CliTests.Cli(env, "render", level, "--out", png, "--camera", camera, "--look", look, "--fov", fov,
                    "--mode", "textured", "--size", "640x400", "--no-overlays", "--rf-dir", rf);
                Assert.True(code == 0, $"{png}: {o}{e}");
                (int w, int h, byte[] p) = PngTestReader.Read(png);
                Assert.Equal((640, 400), (w, h));
                Assert.True(Distinct(p) > 40, $"{png} is (nearly) blank");
                px.Add(p);
            }

            Assert.False(px[0].SequenceEqual(px[1]), $"{name}: alignment changed nothing on screen");
        }

        JsonObject mOff = Measure(off, rf), mOn = Measure(on, rf);
        double handOff = mOff["walls"]!["handedness_major"]!.GetValue<double>(), handOn = mOn["walls"]!["handedness_major"]!.GetValue<double>();
        Assert.True(handOn >= 0.97, $"aligned wall handedness {handOn:0.00}");
        Assert.True(handOff <= 0.75, $"box-mapped wall handedness {handOff:0.00}");
        double cornerOff = mOff["seams"]!["wall_corner"]!["continuous"]!.GetValue<double>(), cornerOn = mOn["seams"]!["wall_corner"]!["continuous"]!.GetValue<double>();
        Assert.True(cornerOn > cornerOff + 0.1, $"wall corners wrapping: {cornerOff:0.00} -> {cornerOn:0.00}");
        Assert.True(mOn["slope_stretched"]!.GetValue<double>() <= mOff["slope_stretched"]!.GetValue<double>(), "alignment stretched slopes");
    }
}
