using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ged.Automation.Rendering;
using Ged.Cli;
using Ged.Rendering.Graphics;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// Render evidence for the texture defaults and UV tools (DESIGN-QUALITY-SPEC §(c.3)): the shipped
/// <c>scripts/examples/kit-textured.lua</c> is run, sealed-built and rendered textured with the RF install mounted,
/// as one wide shot and close-ups of each new mapping — slope-aligned stair treads, a ramp, a wall kit's base band
/// and door frame, a column wrapped around its corners, a hazard stripe — written to
/// <c>tests/artifacts/render/kit_uv_*.png</c> for eyeballing. A wall close-up at the kit's 64 px/m is compared with
/// the same wall at the old 8 m rock tile (31 px/m): the texture must resolve visibly more detail. Skips without a
/// GPU device or an RF install (without textures there is nothing to judge).
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class KitTexturingRenderTests
{
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

    /// <summary>Mean absolute luminance step between horizontal and vertical neighbours: how much texture detail resolves.</summary>
    private static double Detail(byte[] rgba, int w, int h)
    {
        static double Lum(byte[] p, int i) => (p[i] * 0.299) + (p[i + 1] * 0.587) + (p[i + 2] * 0.114);
        double sum = 0;
        int n = 0;
        for (int y = h / 8; y < h * 7 / 8; y++)
        {
            for (int x = w / 8; x < w * 7 / 8; x++)
            {
                int i = ((y * w) + x) * 4;
                sum += Math.Abs(Lum(rgba, i) - Lum(rgba, i + 4)) + Math.Abs(Lum(rgba, i) - Lum(rgba, i + (w * 4)));
                n++;
            }
        }

        return sum / n;
    }

    /// <summary>The example's source with <paramref name="prelude"/> inserted after its header comments.</summary>
    private static string WithPrelude(string source, string prelude)
    {
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        int header = lines.TakeWhile(l => l.StartsWith("--@", StringComparison.Ordinal)).Count();
        return string.Join("\n", lines.Take(header).Append(prelude).Concat(lines.Skip(header)));
    }

    private static async Task<string> Build(CliEnvironment env, string rf, TempDir dir, string script, string name)
    {
        string path = dir.File(name + ".lua");
        await File.WriteAllTextAsync(path, script);
        string level = dir.File(name + ".rfl");
        (int code, string o, string e) = await CliTests.Cli(env, "run", path, "--out", level, "--build", "--rf-dir", rf);
        Assert.True(code == 0, o + e);
        Assert.Contains("holes = 0", o, StringComparison.Ordinal);
        return level;
    }

    private static async Task<(int W, int H, byte[] Px)> Shot(CliEnvironment env, string rf, string level, string file, string camera, string look, string fov = "70")
    {
        string png = Path.Combine(ArtifactsDir(), file);
        (int code, string o, string e) = await CliTests.Cli(env, "render", level, "--out", png, "--camera", camera, "--look", look, "--fov", fov,
            "--mode", "textured", "--size", "640x480", "--no-overlays", "--rf-dir", rf);
        Assert.True(code == 0, $"{file}: {o}{e}");
        (int w, int h, byte[] px) = PngTestReader.Read(png);
        Assert.Equal((640, 480), (w, h));
        Assert.True(Distinct(px) > 40, $"{file} is (nearly) blank");
        return (w, h, px);
    }

    [Fact]
    public async Task Textured_Example_Close_Ups_Render_And_Resolve_At_The_Norm_Density()
    {
        string? root = TestLevels.RepoRoot;
        string? rf = RfInstall();
        using GraphicsDevice? gd = TryDevice();
        if (root is null || rf is null || gd is null)
        {
            return;
        }

        string example = await File.ReadAllTextAsync(Path.Combine(root, "scripts", "examples", "kit-textured.lua"));
        CliEnvironment env = Env(gd);
        using var dir = new TempDir();
        string level = await Build(env, rf, dir, example, "kit-textured");

        await Shot(env, rf, level, "kit_uv_hall.png", "9,2.5,-7.5", "-6,2,4", "75");
        await Shot(env, rf, level, "kit_uv_stairs.png", "7,1.7,0", "-1,1.2,4");
        await Shot(env, rf, level, "kit_uv_ramp.png", "23,1.7,-1.5", "20,0.6,4");
        await Shot(env, rf, level, "kit_uv_door_band.png", "7,1.2,-5", "10,0.6,-1", "60");
        await Shot(env, rf, level, "kit_uv_column.png", "-1.5,3,-6.5", "-4,3,-4", "60");

        // The deck's open edge carries the hazard stripe: a close-up shows plenty of its yellow.
        (int sw, int sh, byte[] stripe) = await Shot(env, rf, level, "kit_uv_stripe.png", "-5,2.4,-2", "-5,2.85,0", "50");
        int yellow = 0;
        for (int i = 0; i < stripe.Length; i += 4)
        {
            if (stripe[i] > 140 && stripe[i + 1] > 100 && stripe[i + 2] < 90 && stripe[i] - stripe[i + 2] > 80)
            {
                yellow++;
            }
        }

        Assert.True(yellow > sw * sh / 50, $"the hazard stripe close-up has only {yellow} yellow pixels");

        // Texel density: the same wall close-up at the kit's 64 px/m and at the old 8 m tile.
        string old = await Build(env, rf, dir,
            WithPrelude(example, "local kit_defaults = ged.import('kit').defaults kit_defaults.ppm = nil kit_defaults.tile_metres = 8"), "kit-textured-8m");
        (int w, int h, byte[] dense) = await Shot(env, rf, level, "kit_uv_wall_64ppm.png", "0,2,-5.5", "0,2,-8", "60");
        (_, _, byte[] coarse) = await Shot(env, rf, old, "kit_uv_wall_8m_tile.png", "0,2,-5.5", "0,2,-8", "60");
        double d64 = Detail(dense, w, h), d8 = Detail(coarse, w, h);
        Assert.True(d64 > d8 * 1.15, $"64 px/m wall detail {d64:0.00} vs 8 m tile {d8:0.00}");
    }
}
