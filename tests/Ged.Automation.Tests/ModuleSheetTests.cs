using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ged.Automation.Rendering;
using Ged.Automation.Tests.Mcp;
using Ged.Cli;
using Ged.Rendering.Graphics;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// The module curation workflow end to end on a small harvested library (a room with three pilasters): the mapper's
/// <c>curation.json</c> through <c>modules list --include-rejected</c>, <c>modules stats</c> and the
/// <c>module_library</c> tool, and <c>modules sheet</c> writing thumbnails plus the self-contained contact sheet
/// (skipped without a GPU device or an RF install).
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class ModuleSheetTests
{
    private static async Task<(string Lib, string Id)> HarvestAsync(string root)
    {
        string level = ModuleCommandTests.WritePilasterLevel(Path.Combine(root, "levels", "pilasters.rfl"));
        string lib = Path.Combine(root, "library", "modules");
        (int code, string o, string e) = await CliTests.Cli(McpTestSession.Env(), "modules", "harvest", level, "--group", "test", "--out", lib, "--json");
        Assert.True(code == 0, e + o);
        (code, o, e) = await CliTests.Cli(McpTestSession.Env(), "modules", "list", "--modules", lib, "--category", "pilaster", "--json");
        Assert.True(code == 0, e + o);
        return (lib, JsonNode.Parse(o)!["result"]!["modules"]!.AsArray()[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Curation_Shows_In_List_Stats_And_The_Library_Tool()
    {
        using var dir = new TempDir();
        (string lib, string id) = await HarvestAsync(dir.Path);
        File.WriteAllText(Path.Combine(lib, "curation.json"),
            "{\"schema\": \"glacier.module-curation/1\", \"modules\": {\"" + id + "\": {\"verdict\": \"reject\", \"category\": \"column\", \"note\": \"n\"}, \"gone_00000000\": {\"verdict\": \"keep\"}}}");

        (int code, string o, string e) = await CliTests.Cli(McpTestSession.Env(), "modules", "list", "--modules", lib, "--json");
        Assert.True(code == 0, e + o);
        JsonNode list = JsonNode.Parse(o)!["result"]!;
        Assert.DoesNotContain(list["modules"]!.AsArray(), m => m!["id"]!.GetValue<string>() == id);
        Assert.Contains(list["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("gone_00000000", StringComparison.Ordinal));

        (code, o, e) = await CliTests.Cli(McpTestSession.Env(), "modules", "list", "--modules", lib, "--include-rejected", "--json");
        Assert.True(code == 0, e + o);
        JsonNode row = JsonNode.Parse(o)!["result"]!["modules"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == id)!;
        Assert.Equal("reject", row["verdict"]!.GetValue<string>());
        Assert.Equal("column", row["category"]!.GetValue<string>());

        (code, o, e) = await CliTests.Cli(McpTestSession.Env(), "modules", "stats", "--modules", lib, "--json");
        Assert.True(code == 0, e + o);
        JsonNode cur = JsonNode.Parse(o)!["result"]!["stats"]!["curation"]!;
        Assert.Equal(1, cur["rejected"]!.GetValue<int>());
        Assert.Equal(1, cur["relabelled"]!.GetValue<int>());
        Assert.Equal(1, cur["unknown_ids"]!.GetValue<int>());
        (code, o, _) = await CliTests.Cli(McpTestSession.Env(), "modules", "stats", "--modules", lib);
        Assert.Equal(0, code);
        Assert.Contains("1 rejected", o, StringComparison.Ordinal);

        (code, o, e) = await CliTests.Cli(McpTestSession.Env(), "modules", "show", id, "--modules", lib, "--json");
        Assert.True(code == 0, e + o);
        JsonNode shown = JsonNode.Parse(o)!["result"]!["module"]!["curation"]!;
        Assert.Equal("reject", shown["verdict"]!.GetValue<string>());
        Assert.Equal("pilaster", shown["harvested"]!["category"]!.GetValue<string>());

        // --include-rejected and the sheet's options belong to their own subcommands.
        (code, _, _) = await CliTests.Cli(McpTestSession.Env(), "modules", "stats", "--modules", lib, "--include-rejected");
        Assert.Equal(ExitCodes.Usage, code);
        (code, _, _) = await CliTests.Cli(McpTestSession.Env(), "modules", "list", "--modules", lib, "--top", "3");
        Assert.Equal(ExitCodes.Usage, code);
        (code, _, _) = await CliTests.Cli(McpTestSession.Env(), "modules", "sheet", "--modules", lib);
        Assert.Equal(ExitCodes.Usage, code);

        await using McpTestSession s = await McpTestSession.StartAsync();
        JsonObject plain = await s.OkAsync("module_library", new { library = lib });
        Assert.DoesNotContain(plain["modules"]!.AsArray(), m => m!["id"]!.GetValue<string>() == id);
        JsonObject all = await s.OkAsync("module_library", new { library = lib, include_rejected = true });
        Assert.Contains(all["modules"]!.AsArray(), m => m!["id"]!.GetValue<string>() == id);
    }

    [Fact]
    public async Task Sheet_Writes_Thumbnails_And_A_Self_Contained_Page()
    {
        string? rf = RenderTestSupport.RfInstall();
        GraphicsDevice? device = RenderTestSupport.TryDevice();
        if (rf is null || device is null)
        {
            device?.Dispose();
            return; // needs an RF install (textures) and a GPU device
        }

        try
        {
            using var dir = new TempDir();
            (string lib, string id) = await HarvestAsync(dir.Path);
            File.WriteAllText(Path.Combine(lib, "curation.json"),
                "{\"schema\": \"glacier.module-curation/1\", \"modules\": {\"" + id + "\": {\"verdict\": \"keep\", \"note\": \"good\"}, \"other_00000000\": {\"verdict\": \"reject\"}}}");
            string sheet = Path.Combine(dir.Path, "sheet");
            var env = new CliEnvironment
            {
                PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
                SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
                GetEnvironmentVariable = _ => null,
                Clock = () => TestLevels.Fixed,
                RenderDevice = () => (device, "test device"),
            };

            (int code, string o, string e) = await CliTests.Cli(env, "modules", "sheet", "--modules", lib, "--out", sheet, "--rf-dir", rf, "--size", "96", "--json");
            Assert.True(code == 0, e + o);
            JsonNode result = JsonNode.Parse(o)!["result"]!;
            int selected = result["selected"]!.GetValue<int>();
            Assert.True(selected >= 1);
            Assert.Equal(selected, result["rendered"]!.GetValue<int>());
            Assert.Empty(result["failed"]!.AsArray());

            string png = Path.Combine(sheet, "thumbs", id + ".png");
            Assert.True(File.Exists(png));
            (int w, int h, byte[] rgba) = PngTestReader.Decode(File.ReadAllBytes(png));
            Assert.Equal((96, 96), (w, h));
            var colours = new HashSet<int>();
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                colours.Add((rgba[i] << 16) | (rgba[i + 1] << 8) | rgba[i + 2]);
            }

            Assert.True(colours.Count > 40, $"the thumbnail looks empty ({colours.Count} colours)");

            // The page: no external resources, relative thumbnail paths, the data and the current curation embedded.
            string html = File.ReadAllText(Path.Combine(sheet, "index.html"));
            Assert.DoesNotMatch(new Regex("(src|href)=\"(https?:)?//", RegexOptions.IgnoreCase), html);
            Match data = Regex.Match(html, "<script id=\"sheet-data\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
            Assert.True(data.Success);
            JsonNode d = JsonNode.Parse(data.Groups[1].Value)!;
            Assert.Equal("glacier.module-curation/1", d["schema"]!.GetValue<string>());
            JsonNode mod = d["modules"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == id)!;
            Assert.Equal("thumbs/" + id + ".png", mod["thumb"]!.GetValue<string>());
            Assert.Equal("keep", mod["verdict"]!.GetValue<string>());
            Assert.Equal("good", mod["note"]!.GetValue<string>());
            Assert.NotNull(mod["sources"]!.AsArray().FirstOrDefault());
            Assert.Equal("reject", d["curation"]!["other_00000000"]!["verdict"]!.GetValue<string>());
            Assert.Contains("Download curation.json", html, StringComparison.Ordinal);
            Assert.Contains("localStorage", html, StringComparison.Ordinal);

            // The sheet never writes into the library.
            (code, _, e) = await CliTests.Cli(env, "modules", "sheet", "--modules", lib, "--out", Path.Combine(lib, "sheet"), "--rf-dir", rf);
            Assert.True(code == ExitCodes.Io, e);
        }
        finally
        {
            device.Dispose();
        }
    }
}

/// <summary>The GPU device and RF install lookups the render tests share.</summary>
internal static class RenderTestSupport
{
    public static GraphicsDevice? TryDevice()
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
    public static string? RfInstall()
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
}
