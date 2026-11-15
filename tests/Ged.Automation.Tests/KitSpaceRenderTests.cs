using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Automation.Rendering;
using Ged.Cli;
using Ged.Rendering.Graphics;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// Render artifacts of the design kit's space archetypes and set pieces (DESIGN-QUALITY-SPEC Q3 gate): every
/// <c>kit.space</c> archetype and a set-piece level (the four bridge styles over a chasm, the three landmark styles,
/// window / arch / rough openings, a truss) are built through glacier-cli (new → run → build --sealed --light),
/// then rendered as a three-quarter iso view and an eye-height camera to tests/artifacts/render/kit_space for review.
/// Each render must succeed from a built, lit level and show real content. Skips without a GPU device.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class KitSpaceRenderTests
{
    private static readonly string ArtifactsDir = InitArtifacts();

    private static string InitArtifacts()
    {
        string dir = TestLevels.RepoRoot is { } root
            ? Path.Combine(root, "tests", "artifacts", "render", "kit_space")
            : Path.Combine(Path.GetTempPath(), "ged-render-artifacts", "kit_space");
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

    // Stock textures per class (render legibility only; the kit itself defaults to the editor's textures).
    private const string Industrial = "textures = { floor = 'cem_cementflr01.tga', wall = 'cem_bunker04.tga', ceiling = 'cem_cemceil03.tga' }, trim_texture = 'mtl_airgirder01.tga'";
    private const string Rock = "textures = { floor = 'rck_cavernflr01.tga', wall = 'rck_canyon_rock01.tga', ceiling = 'rck_canyon_rock02.tga' }, trim_texture = 'sld_woodpost01.tga'";
    private const string Sky = "textures = { floor = 'cem_concreteflr01.tga', wall = 'rck_canyon_rock01.tga', ceiling = 'cem_cemceil03.tga' }, trim_texture = 'mtl_hazardstripe01.tga'";
    private const string Yard = "textures = { floor = 'cem_cementflr01.tga', wall = 'cem_bigwall02.tga', ceiling = 'cem_cemceil03.tga' }, trim_texture = 'mtl_airgirder01.tga'";

    /// <summary>Archetype, textures, eye camera and look-at point (the space at the origin, facing +z).</summary>
    public static readonly (string Archetype, string Textures, string Camera, string Look)[] Shots =
    {
        ("hall", Industrial, "-10,1.7,-6", "8,4,4"),
        ("atrium", Industrial, "-11,1.7,-10", "6,6,6"),
        ("arena", Industrial, "-13,1.7,-13", "0,3,0"),
        ("ring", Industrial, "13,1.7,13", "-6,4,15.5"),
        ("sunken_court", Industrial, "-17,1.7,-2", "0,-3,0"),
        ("stair_hall", Industrial, "5,1.7,-7", "-3,8,5"),
        ("courtyard", Yard, "-12,1.7,-9", "5,3,10"),
        ("canyon_bridge", Rock, "2,1.7,-24", "0,12,0"),
        ("cave_chamber", Rock, "-14,1.7,0", "6,4,2"),
        ("pit_room", Industrial, "-9,1.7,8", "-3,-2,-3"),
        ("control_room", Industrial, "-13,1.7,-5", "8,6,0"),
        ("storage", Industrial, "8,1.7,-5", "-6,3,2"),
        ("platform", Sky, "8,1.7,6", "40,4,0"),
        ("sky_bridge", Sky, "-8,1.7,-6", "40,3,0"),
        ("open_deck", Sky, "-13,1.7,7", "10,6,0"),
        ("cart_road", Yard, "-6,4.7,-36", "6,2,0"),
        ("overlook", Yard, "-5,1.7,-10", "0,5,10"),
        ("gate", Yard, "0,1.7,-28", "0,6,0"),
        ("finale", Yard, "0,1.7,-33", "0,2,10"),
    };

    public static IEnumerable<object[]> ShotNames() => Shots.Select(s => new object[] { s.Archetype });

    private static async Task BuildAndRender(GraphicsDevice gd, string name, string script, (string Camera, string Look)[] cameras)
    {
        string? rf = RfInstall();
        string[] Rf(params string[] a) => rf is null ? a : a.Concat(new[] { "--rf-dir", rf }).ToArray();
        using var dir = new TempDir();
        string level = dir.File(name + ".rfl");
        string lua = dir.File(name + ".lua");
        File.WriteAllText(lua, script);
        CliEnvironment env = Env(gd);
        (int code, string o, string e) = await CliTests.Cli(env, Rf("new", "--out", level));
        Assert.True(code == 0, o + e);
        (code, o, e) = await CliTests.Cli(env, Rf("run", lua, level, "--save", "--timeout", "120"));
        Assert.True(code == 0, o + e);
        (code, o, e) = await CliTests.Cli(env, Rf("build", level, "--sealed", "--light", "--save"));
        Assert.True(code == 0, o + e);

        var shots = new List<(string File, string[] Args)> { (name + "_iso.png", new[] { "--view", "iso", "--size", "900x700" }) };
        for (int i = 0; i < cameras.Length; i++)
        {
            shots.Add(($"{name}_eye{(i == 0 ? string.Empty : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))}.png",
                new[] { "--camera", cameras[i].Camera, "--look", cameras[i].Look, "--fov", "80", "--size", "900x600" }));
        }

        foreach ((string file, string[] args) in shots)
        {
            string png = Path.Combine(ArtifactsDir, file);
            (code, o, e) = await CliTests.Cli(env, Rf(new[] { "render", level, "--out", png, "--json" }.Concat(args).ToArray()));
            Assert.True(code == 0, o + e);
            JsonNode result = JsonNode.Parse(o)!["result"]!;
            Assert.Equal("built", result["geometry"]!["before"]!.GetValue<string>());
            Assert.Equal("lit", result["modeUsed"]!.GetValue<string>());
            var (_, _, px) = PngTestReader.Read(png);
            Assert.True(Distinct(px) > 40, $"{file} shows almost nothing");
        }
    }

    [Theory]
    [MemberData(nameof(ShotNames))]
    public async Task Archetype_Renders(string archetype)
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        (string _, string textures, string camera, string look) = Shots.Single(s => s.Archetype == archetype);
        string script =
            "local kit = ged.import('kit')\n" +
            "level.props:set_many{ multiplayer = true, ambient_color = {60, 60, 64} }\n" +
            $"local s = kit.space{{ archetype = '{archetype}', pos = {{0, 0, 0}}, {textures} }}\n" +
            "assert(#s.warnings == 0, table.concat(s.warnings, '; '))\n" +
            "return s.name";
        await BuildAndRender(gd, archetype, script, new[] { (camera, look) });
    }

    [Fact]
    public async Task Set_Pieces_Render()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        // A 160 m sky hall with a 22 m chasm across it: the four bridge styles side by side (28 m decks, 10 m drop),
        // three 30-36 m landmarks beyond, and a gallery room joined to the hall by a window, an arch and a rough mouth.
        string script =
            "local kit = ged.import('kit')\n" +
            "level.props:set_many{ multiplayer = true, ambient_color = {60, 60, 64} }\n" +
            "kit.defaults.textures = { floor = 'cem_cementflr01.tga', wall = 'cem_bigwall02.tga', ceiling = 'cem_cemceil03.tga' }\n" +
            "local hall = kit.room{ name = 'hall', pos = {0, 0, 0}, size = {72, 44, 90} }\n" +
            "kit.sky_room{ open = { hall } }\n" +
            "kit.floor_section{ min = {-36, 0, -11}, max = {36, 0, 11}, y = 0, rise = -10 }\n" +
            "for i, style in ipairs({ 'truss', 'arch', 'girder', 'plank' }) do\n" +
            "  local x = -27 + (i - 1) * 18\n" +
            "  kit.bridge{ name = 'bridge_' .. style, from = {x, 0.125, -14}, to = {x, 0.125, 14}, width = 3.5, style = style, frame_texture = 'mtl_airgirder01.tga', purpose = 'report' }\n" +
            "end\n" +
            "kit.landmark{ name = 'tower', pos = {-22, 0, 32}, height = 32, style = 'tower' }\n" +
            "kit.landmark{ name = 'pylon', pos = {0, 0, 34}, height = 30, base = 7, style = 'pylon', texture = 'mtl_airgirder01.tga' }\n" +
            "kit.landmark{ name = 'spire', pos = {22, 0, 32}, height = 36, style = 'spire' }\n" +
            "local gallery = kit.room{ name = 'gallery', pos = {0, 0, -55}, size = {40, 8, 18} }\n" +
            "kit.opening{ a = hall, b = gallery, shape = 'window', offset = -12, width = 6 }\n" +
            "kit.opening{ a = hall, b = gallery, shape = 'arch', offset = 0, width = 5, height = 6 }\n" +
            "kit.opening{ a = hall, b = gallery, shape = 'rough', offset = 12, width = 4, seed = 4 }\n" +
            "kit.truss{ from = {-18, 5, -60}, to = {18, 5, -60}, depth = 2, width = 2, style = 'warren', texture = 'mtl_airgirder01.tga' }\n" +
            "kit.light_room{ room = hall, spacing = 12 }\n" +
            "kit.light_room{ room = gallery, spacing = 6 }\n" +
            "return 'ok'";
        await BuildAndRender(gd, "set_pieces", script, new[] { ("-30,1.7,-30", "0,0,0"), ("-18,-8.3,-9", "-9,-3,2"), ("0,1.7,-60", "0,3,-40") });
    }
}
