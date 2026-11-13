using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Render artifacts of the layout graph (DESIGN-QUALITY-SPEC §(g) Q4): one seeded <c>kit.layout</c> per gametype × class
/// (dm, ctf, koth × standard and open-air, plus payload) is built through glacier-cli (new → run → build --sealed) and
/// drawn as a plan of its two lowest storeys, a top view of the whole layout and a three-quarter iso view to tests/artifacts/render/kit_layout for review
/// (textured when an RF install is available, room colours otherwise; the layouts are not lit, so the renders show the
/// structure). Each render must succeed from the built level and show real content. Skips without a GPU device.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class KitLayoutRenderTests
{
    private static readonly string ArtifactsDir = InitArtifacts();

    private static string InitArtifacts()
    {
        string dir = TestLevels.RepoRoot is { } root
            ? Path.Combine(root, "tests", "artifacts", "render", "kit_layout")
            : Path.Combine(Path.GetTempPath(), "ged-render-artifacts", "kit_layout");
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

    public static IEnumerable<object[]> Layouts() => new[]
    {
        new object[] { "dm", "standard", 1, 12 }, new object[] { "dm", "open_air", 1, 12 },
        new object[] { "ctf", "standard", 1, 12 }, new object[] { "ctf", "open_air", 1, 12 },
        new object[] { "koth", "standard", 1, 12 }, new object[] { "koth", "open_air", 1, 12 },
        new object[] { "payload", "payload", 1, 12 },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task Layout_Renders(string gametype, string cls, int seed, int players)
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        string? rf = RfInstall();
        string[] Rf(params string[] a) => rf is null ? a : a.Concat(new[] { "--rf-dir", rf }).ToArray();
        string name = $"{gametype}_{cls}_seed{seed}";
        using var dir = new TempDir();
        string level = dir.File(name + ".rfl");
        string lua = dir.File(name + ".lua");
        string call = gametype == "payload"
            ? FormattableString.Invariant($"kit.layout{{ gametype = 'payload', seed = {seed}, players = {players} }}")
            : FormattableString.Invariant($"kit.layout{{ gametype = '{gametype}', class = '{cls}', seed = {seed}, players = {players} }}");
        File.WriteAllText(lua,
            "local kit = ged.import('kit')\n" +
            "level.props:set_many{ ambient_color = {60, 60, 64} }\n" +
            $"local g = {call}\n" +
            "local rep = kit.build_layout{ graph = g, name = 'L' }\n" +
            "local lv = {} for _, y in ipairs(g.levels) do lv[#lv + 1] = string.format('%g', y) end\n" +
            "return table.concat(lv, ',')");
        CliEnvironment env = Env(gd);
        (int code, string o, string e) = await CliTests.Cli(env, Rf("new", "--out", level));
        Assert.True(code == 0, o + e);
        (code, o, e) = await CliTests.Cli(env, Rf("run", lua, level, "--save", "--timeout", "240", "--json"));
        Assert.True(code == 0, o + e);
        string[] levels = JsonNode.Parse(o)!["result"]!["returnValue"]!.GetValue<string>().Split(',');
        (code, o, e) = await CliTests.Cli(env, Rf("build", level, "--sealed", "--save"));
        Assert.True(code == 0, o + e);

        // Plans of the two lowest storeys (the slice starts half a metre above each floor), then the whole layout from above.
        var shots = new List<(string File, string[] Args)>();
        for (int i = 0; i < Math.Min(2, levels.Length); i++)
        {
            double y = double.Parse(levels[i], CultureInfo.InvariantCulture);
            shots.Add(($"{name}_plan{i}.png", new[] { "--plan", "--y", (y + 0.5).ToString("0.###", CultureInfo.InvariantCulture), "--size", "1200x1200" }));
        }

        shots.Add(($"{name}_top.png", new[] { "--view", "top", "--size", "1400x1400", "--mode", rf is null ? "rooms" : "textured" }));
        shots.Add(($"{name}_iso.png", new[] { "--view", "iso", "--size", "1400x1000", "--mode", rf is null ? "rooms" : "textured" }));
        foreach ((string file, string[] args) in shots)
        {
            string png = Path.Combine(ArtifactsDir, file);
            (code, o, e) = await CliTests.Cli(env, Rf(new[] { "render", level, "--out", png, "--json" }.Concat(args).ToArray()));
            Assert.True(code == 0, o + e);
            JsonNode result = JsonNode.Parse(o)!["result"]!;
            Assert.Equal("built", result["geometry"]!["before"]!.GetValue<string>());
            var (_, _, px) = PngTestReader.Read(png);
            Assert.True(Distinct(px) > 20, $"{file} shows almost nothing");
        }
    }
}
