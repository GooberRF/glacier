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
using Xunit.Abstractions;

namespace Ged.Automation.Tests;

/// <summary>
/// The heights overlay end to end: a traced level with a flat hall, a rectangular ramp, a flared ramp, stairs, a 3 m
/// ledge and a 0.3 m step is rendered as a top view in the heights mode and as a plan with the heights overlay; the JSON
/// lists exactly the two ramps (heights within 0.25 m, climb within 10 degrees), the stairs and the one drop, never the
/// step. Lua ref.heights then decodes the heights picture back into the same ramps, stairs and drop, and its regions go
/// straight into kit.trace{units = "px"}. Images are written to tests/artifacts/render/heights. Skips without a GPU.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class HeightsRenderTests
{
    private const string Level = """
        local kit = ged.import("kit")
        kit.trace{ name = "hz", via = "prism", regions = {
          { name = "low", poly = {{0, 0}, {20, 0}, {20, 20}, {0, 20}}, floor = 0, ceiling = 8 },
          { name = "dais", poly = {{20, 0}, {30, 0}, {30, 20}, {20, 20}}, floor = 0.3, ceiling = 8 },
          { name = "ledge", poly = {{30, 0}, {40, 0}, {40, 20}, {30, 20}}, floor = 3.3, ceiling = 8 },
          { name = "rramp", kind = "ramp", poly = {{2, -10}, {8, -10}, {8, 0}, {2, 0}}, floor = {-3, 0}, climb = {0, 1} },
          { name = "flare", kind = "ramp", poly = {{12, -10}, {16, -10}, {18, 0}, {10, 0}}, floor = {-3, 0}, climb = {0, 1} },
          { name = "pit", poly = {{0, -22}, {20, -22}, {20, -10}, {0, -10}}, floor = -3, ceiling = 3 },
          { name = "st", kind = "stairs", poly = {{2, 20}, {6, 20}, {6, 30}, {2, 30}}, floor = {0, 3.3}, climb = {0, 1} },
          { name = "loft", poly = {{0, 30}, {14, 30}, {14, 38}, {0, 38}}, floor = 3.3, ceiling = 7 },
        } }
        level.player_start{ pos = {10, 1, 10}, yaw = 0 }
        return "ok"
        """;

    private static readonly string ArtifactsDir = InitArtifacts();

    private readonly ITestOutputHelper _out;

    public HeightsRenderTests(ITestOutputHelper output) => _out = output;

    private static string InitArtifacts()
    {
        string dir = TestLevels.RepoRoot is { } root
            ? Path.Combine(root, "tests", "artifacts", "render", "heights")
            : Path.Combine(Path.GetTempPath(), "ged-render-artifacts", "heights");
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

    private static CliEnvironment Env(GraphicsDevice device) => new()
    {
        PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Clock = () => TestLevels.Fixed,
        RenderDevice = () => (device, "test device"),
    };

    private static double D(JsonNode? n) => n!.GetValue<double>();

    private static double Angle(double ax, double az, double bx, double bz) =>
        Math.Acos(Math.Clamp(((ax * bx) + (az * bz)) / (Math.Sqrt((ax * ax) + (az * az)) * Math.Sqrt((bx * bx) + (bz * bz))), -1, 1)) * 180 / Math.PI;

    /// <summary>The heights JSON lists exactly the level's two ramps, its stairs and its one drop.</summary>
    private static void AssertFeatures(JsonNode heights, string what)
    {
        JsonArray ramps = heights["ramps"]!.AsArray(), stairs = heights["stairs"]!.AsArray(), drops = heights["drops"]!.AsArray();
        Assert.True(ramps.Count == 2, $"{what}: ramps {heights["ramps"]!.ToJsonString()}");
        foreach (JsonNode? r in ramps)
        {
            Assert.InRange(D(r!["low"]), -3.25, -2.75);
            Assert.InRange(D(r["high"]), -0.25, 0.25);
            JsonArray c = r["climb"]!.AsArray();
            Assert.True(Angle(D(c[0]), D(c[1]), 0, 1) <= 10, $"{what}: ramp climb {c.ToJsonString()}");
            Assert.NotNull(r["label"]);
        }

        JsonNode s = Assert.Single(stairs)!;
        Assert.InRange(D(s["low"]), -0.25, 0.25);
        Assert.InRange(D(s["high"]), 3.05, 3.55);
        JsonNode d = Assert.Single(drops)!;
        Assert.InRange(D(d["high"]), 3.05, 3.55);
        Assert.InRange(D(d["low"]), 0.05, 0.55);
        Assert.Equal("↓ 3 m", d["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task Heights_Render_Marks_Ramps_Stairs_And_Drops_And_Decodes_Back()
    {
        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return;
        }

        using var dir = new TempDir();
        string level = dir.File("heights.rfl");
        File.WriteAllText(dir.File("heights.lua"), Level);
        CliEnvironment env = Env(gd);
        (int code, string o, string e) = await CliTests.Cli(env, "run", dir.File("heights.lua"), "--out", level);
        Assert.True(code == 0, o + e);

        // 1. The heights mode, top view: the clean picture with symbols and colour legend.
        string top = dir.File("top_heights.png");
        (code, o, e) = await CliTests.Cli(env, "render", level, "--out", top, "--view", "top", "--mode", "heights", "--json");
        Assert.True(code == 0, o + e);
        JsonNode result = JsonNode.Parse(o)!["result"]!;
        Assert.Equal("heights", result["modeUsed"]!.GetValue<string>());
        Assert.Contains("heights", result["overlays"]!.AsArray().Select(n => n!.GetValue<string>()));
        JsonNode heights = result["heights"]!;
        Assert.Equal(-3, D(heights["range"]![0]), 3);
        Assert.Equal(3.5, D(heights["range"]![1]), 3);
        AssertFeatures(heights, "top");
        Assert.Contains(heights["flats"]!.AsArray(), f => Math.Abs(D(f!["y"]) - 0.3) < 0.05);
        Assert.Contains(result["legend"]!.AsArray(), l => l!["glyph"]!.GetValue<string>() == "colorbar");
        File.Copy(top, Path.Combine(ArtifactsDir, "top_heights.png"), overwrite: true);

        // 2. A plan of the hall's storey with the heights overlay over the shaded floor.
        string plan = dir.File("plan_heights.png");
        (code, o, e) = await CliTests.Cli(env, "render", level, "--out", plan, "--plan", "--y", "0", "--thickness", "6", "--below", "4",
            "--overlays", "heights,grid,scale,objects,labels,legend", "--json");
        Assert.True(code == 0, o + e);
        AssertFeatures(JsonNode.Parse(o)!["result"]!["heights"]!, "plan");
        File.Copy(plan, Path.Combine(ArtifactsDir, "plan_heights.png"), overwrite: true);

        // Bad requests: heights on an iso view.
        (code, _, _) = await CliTests.Cli(env, "render", level, "--out", dir.File("x.png"), "--view", "iso", "--overlays", "heights");
        Assert.NotEqual(0, code);

        // 3. ref.heights decodes the heights picture back, and its regions go straight into kit.trace.
        double wpp = D(result["worldPerPixel"]), ox = D(result["origin"]![0]), oz = D(result["origin"]![2]);
        string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        string decode = $$"""
            local kit = ged.import("kit")
            ref.open("top_heights.png")
            ref.calibrate{ mpp = {{F(wpp)}}, origin = {0, 0}, at = {{{F(ox)}}, {{F(oz)}}} }
            local h = ref.heights{ legend = {-3, 3.5}, out = "heights_check.png" }
            local out = { string.format("counts %d %d %d %d", h.counts.flats, h.counts.ramps, h.counts.stairs, h.counts.drops) }
            local a = ref.to_world(0, 0)
            for _, r in ipairs(h.ramps) do
              local b = ref.to_world(r.climb[1], r.climb[2])
              out[#out + 1] = string.format("ramp %.3f %.3f %.4f %.4f %.3f", r.floor[1], r.floor[2], b[1] - a[1], b[2] - a[2], r.slope)
            end
            for _, r in ipairs(h.stairs) do
              local b = ref.to_world(r.climb[1], r.climb[2])
              out[#out + 1] = string.format("stairs %.3f %.3f %.4f %.4f", r.floor[1], r.floor[2], b[1] - a[1], b[2] - a[2])
            end
            for _, d in ipairs(h.drops) do out[#out + 1] = string.format("drop %.3f %.3f", d.high, d.low or -99) end
            local tr = kit.trace{ name = "back", units = "px", regions = h.regions, build = false }
            for _, c in ipairs(tr.connections) do
              out[#out + 1] = string.format("conn %s %s %.2f %s", c.a, c.b, c.drop, tostring(c.ledge))
            end
            return table.concat(out, "\n")
            """;
        File.WriteAllText(dir.File("decode.lua"), decode);
        (code, o, e) = await CliTests.Cli(env, "run", dir.File("decode.lua"), level, "--json");
        Assert.True(code == 0, o + e);
        string[] lines = JsonNode.Parse(o)!["result"]!["returnValue"]!.GetValue<string>().Split('\n');
        foreach (string line in lines)
        {
            _out.WriteLine(line);
        }

        double[] Nums(string line) => line.Split(' ').Skip(1).Where(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        int[] counts = Nums(lines[0]).Select(x => (int)x).ToArray();
        Assert.True(counts[0] >= 5, "flats " + lines[0]);
        Assert.Equal(new[] { 2, 1, 1 }, counts.Skip(1).ToArray());
        foreach (string line in lines.Where(l => l.StartsWith("ramp ", StringComparison.Ordinal)))
        {
            double[] n = Nums(line);
            Assert.InRange(n[0], -3.25, -2.75);
            Assert.InRange(n[1], -0.25, 0.25);
            Assert.True(Angle(n[2], n[3], 0, 1) <= 10, line);
            Assert.InRange(n[4], 0.25, 0.35); // 3 m over 10 m
        }

        double[] st = Nums(lines.Single(l => l.StartsWith("stairs ", StringComparison.Ordinal)));
        Assert.InRange(st[0], -0.25, 0.25);
        Assert.InRange(st[1], 3.05, 3.55);
        Assert.True(Angle(st[2], st[3], 0, 1) <= 10, "stairs climb");
        double[] dr = Nums(lines.Single(l => l.StartsWith("drop ", StringComparison.Ordinal)));
        Assert.InRange(dr[0], 3.05, 3.55);
        Assert.InRange(dr[1], 0.05, 0.55);

        // The decoded plan joins like the level: both ramps to the hall and the pit, the stairs to the hall and the loft,
        // the dais to the hall (a 0.3 m step) and to the ledge (a 3 m drop, a ledge).
        string[] conns = lines.Where(l => l.StartsWith("conn ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, conns.Count(c => c.Contains("ramp1", StringComparison.Ordinal)));
        Assert.Equal(2, conns.Count(c => c.Contains("ramp2", StringComparison.Ordinal)));
        Assert.Equal(2, conns.Count(c => c.Contains("stairs1", StringComparison.Ordinal)));
        Assert.Contains(conns, c => c.EndsWith(" 3.00 true", StringComparison.Ordinal));
        Assert.Contains(conns, c => c.EndsWith(" 0.30 false", StringComparison.Ordinal));
        File.Copy(dir.File("heights_check.png"), Path.Combine(ArtifactsDir, "heights_check.png"), overwrite: true);
    }
}
