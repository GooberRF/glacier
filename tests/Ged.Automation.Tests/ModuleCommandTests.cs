using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Automation.Tests.Mcp;
using Ged.Cli;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// <c>glacier-cli modules harvest | list | show | stats</c> and the <c>harvest_modules</c> / <c>module_library</c> MCP
/// tools on an in-test level (a room with three identical pilasters against one wall).
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class ModuleCommandTests
{
    /// <summary>A room 12 × 5 × 10 m with three base + shaft + cap pilasters against its +Z wall, saved to <paramref name="path"/>.</summary>
    internal static string WritePilasterLevel(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        EditorDocument doc = LevelTemplates.CreateNew(TestLevels.Fixed);
        var ed = new BrushEditor(doc);
        void Box(float cx, float cy, float cz, float w, float h, float d, bool air = false, bool detail = false) =>
            ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = w, Height = h, Depth = d, Air = air, Detail = detail, Texture = "cem_wall01.tga" },
                new Vec3(cx, cy, cz), Mat3.Identity);
        Box(0, 2.5f, 0, 12, 5, 10, air: true);
        foreach (float x in new[] { -3f, 0f, 3f })
        {
            Box(x, 0.2f, 4.75f, 0.8f, 0.4f, 0.5f, detail: true);
            Box(x, 2.0f, 4.8f, 0.6f, 3.2f, 0.4f, detail: true);
            Box(x, 3.8f, 4.75f, 0.8f, 0.4f, 0.5f, detail: true);
        }

        doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 4, 0));
        File.WriteAllBytes(path, doc.SaveToBytes());
        return path;
    }

    private static CliEnvironment Env() => McpTestSession.Env();

    [Fact]
    public async Task Cli_Harvests_A_Level_And_Lists_Shows_And_Counts_Modules()
    {
        using var dir = new TempDir();
        string level = WritePilasterLevel(Path.Combine(dir.Path, "levels", "pilasters.rfl"));
        string lib = Path.Combine(dir.Path, "library", "modules");

        (int code, string o, string e) = await CliTests.Cli(Env(), "modules", "harvest", level, "--group", "test", "--out", lib, "--json");
        Assert.True(code == 0, e + o);
        JsonNode result = JsonNode.Parse(o)!["result"]!;
        Assert.Equal(1, result["levels_read"]!.GetValue<int>());
        Assert.True(result["modules"]!.GetValue<int>() >= 1);
        Assert.True(File.Exists(Path.Combine(lib, "test", "index.json")));
        Assert.True(File.Exists(Path.Combine(lib, "harvest-report.json")));

        (code, o, e) = await CliTests.Cli(Env(), "modules", "list", "--modules", lib, "--category", "pilaster", "--attach", "wall", "--json");
        Assert.True(code == 0, e + o);
        JsonNode list = JsonNode.Parse(o)!["result"]!;
        Assert.True(list["available"]!.GetValue<bool>());
        JsonNode row = Assert.Single(list["modules"]!.AsArray())!;
        Assert.Equal(3, row["occurrences"]!.GetValue<int>());
        Assert.Equal("wall", row["attach"]!.GetValue<string>());
        string id = row["id"]!.GetValue<string>();

        (code, o, e) = await CliTests.Cli(Env(), "modules", "show", id, "--modules", lib, "--json");
        Assert.True(code == 0, e + o);
        JsonNode show = JsonNode.Parse(o)!["result"]!["module"]!;
        Assert.Equal(id, show["id"]!.GetValue<string>());
        Assert.Equal(3, show["provenance"]!.AsArray().Count);
        Assert.True(File.Exists(show["rfg"]!.GetValue<string>()));
        Assert.NotNull(show["stretch"]!["y"]);

        (code, o, e) = await CliTests.Cli(Env(), "modules", "stats", "--modules", lib, "--json");
        Assert.True(code == 0, e + o);
        Assert.True(JsonNode.Parse(o)!["result"]!["stats"]!["by_category"]!["pilaster"]!.GetValue<int>() >= 1);

        // Text output names the module; show prints the anchor.
        (code, o, _) = await CliTests.Cli(Env(), "modules", "list", "--modules", lib);
        Assert.Equal(0, code);
        Assert.Contains(id, o);
        (code, o, _) = await CliTests.Cli(Env(), "modules", "show", id, "--modules", lib);
        Assert.Equal(0, code);
        Assert.Contains("wall plane at z=0", o);

        // A second identical harvest gives identical files.
        string lib2 = Path.Combine(dir.Path, "library2", "modules");
        (code, _, e) = await CliTests.Cli(Env(), "modules", "harvest", level, "--group", "test", "--out", lib2);
        Assert.True(code == 0, e);
        foreach (string f in Directory.EnumerateFiles(lib, "*", SearchOption.AllDirectories))
        {
            string other = Path.Combine(lib2, Path.GetRelativePath(lib, f));
            Assert.True(File.ReadAllBytes(f).AsSpan().SequenceEqual(File.ReadAllBytes(other)), $"{f} differs");
        }

        // A missing library is a normal answer, not an error.
        (code, o, _) = await CliTests.Cli(Env(), "modules", "stats", "--modules", Path.Combine(dir.Path, "nothing"), "--json");
        Assert.Equal(0, code);
        Assert.False(JsonNode.Parse(o)!["result"]!["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Cli_Refuses_Bad_Usage_And_Output_Inside_A_Source_Folder()
    {
        using var dir = new TempDir();
        string level = WritePilasterLevel(Path.Combine(dir.Path, "levels", "pilasters.rfl"));
        (int code, _, string e) = await CliTests.Cli(Env(), "modules", "harvest", level, "--group", "test", "--out", Path.Combine(dir.Path, "levels", "out"));
        Assert.True(code == ExitCodes.Io, e);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "levels", "out", "test")));

        foreach (string[] bad in new[]
        {
            new[] { "modules" }, new[] { "modules", "frob" }, new[] { "modules", "show" }, new[] { "modules", "harvest", level, "--out", dir.Path },
            new[] { "modules", "list", "--out", dir.Path }, new[] { "summary", level, "--category", "pilaster" },
        })
        {
            (code, _, e) = await CliTests.Cli(Env(), bad);
            Assert.True(code == ExitCodes.Usage, $"glacier-cli {string.Join(' ', bad)} should be a usage error (got {code}): {e}");
        }

        (code, string o, _) = await CliTests.Cli(Env(), "help", "modules");
        Assert.Equal(0, code);
        Assert.Contains("--sources", o);
        Assert.Contains("--attach <kind>", o);
    }

    [Fact]
    [Trait("Category", "Mcp")]
    public async Task Mcp_Harvest_Then_Library_Tools()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        string level = WritePilasterLevel(Path.Combine(s.Dir.Path, "levels", "pilasters.rfl"));
        string lib = Path.Combine(s.Scratch, "modules");
        JsonObject harvest = await s.OkAsync("harvest_modules", new { source = level, group = "test", @out = lib });
        Assert.True(harvest["modules"]!.GetValue<int>() >= 1);

        JsonObject list = await s.OkAsync("module_library", new { library = lib, category = "pilaster" });
        Assert.True(list["available"]!.GetValue<bool>());
        string id = list["modules"]!.AsArray()[0]!["id"]!.GetValue<string>();
        JsonObject one = await s.OkAsync("module_library", new { library = lib, id });
        Assert.Equal(id, one["module"]!["id"]!.GetValue<string>());

        // Writing outside the session's folders is refused.
        await s.FailAsync("harvest_modules", AutomationErrorCodes.PathNotAllowed, new { source = level, group = "test", @out = Path.Combine(s.Dir.Path, "elsewhere") });

        // A read-only session does not offer the harvest.
        await using McpTestSession ro = await McpTestSession.StartAsync(new Ged.Cli.Mcp.McpLaunchOptions { ReadOnly = true });
        var tools = await ro.Client.ListToolsAsync();
        Assert.DoesNotContain(tools, t => t.Name == "harvest_modules");
        Assert.Contains(tools, t => t.Name == "module_library");
    }
}
