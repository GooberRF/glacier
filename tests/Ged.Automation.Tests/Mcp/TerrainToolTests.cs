using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Automation.Tools;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Automation.Tests.Mcp;

/// <summary>
/// The MCP face of Alpine terrain: the terrain tool runs the Lua terrain API (level.terrain, terrain:*) as one AI-labelled
/// undo step per call; op = query is a dry run that changes nothing; to_brushes with keep = false is refused unless the
/// session allows destructive actions.
/// </summary>
public sealed class TerrainToolTests
{
    [Fact]
    public void The_Tool_Builds_The_Lua_Calls()
    {
        ToolSpec spec = ToolCatalog.Find("terrain")!;
        (AutomationRequest create, _) = ToolRequests.Build(spec, new JsonObject
        {
            ["op"] = "create",
            ["center"] = new JsonArray(1.0, 2.0, 3.0),
            ["nx"] = 33,
            ["layers"] = "a.tga; b \"x\".tga",
        }, new ToolRequestOptions());
        string script = create.Args!["script"]!.GetValue<string>();
        Assert.True(script.Contains("level.terrain.create{center = {1, 2, 3}, nx = 33, layers = {\"a.tga\", \"b \\\"x\\\".tga\"}}", System.StringComparison.Ordinal), script);
        Assert.False(create.Args.ContainsKey("dryRun"));

        (AutomationRequest query, _) = ToolRequests.Build(spec, new JsonObject { ["op"] = "query", ["terrain"] = "12", ["points"] = new JsonArray(4.0, 5.0) }, new ToolRequestOptions());
        string q = query.Args!["script"]!.GetValue<string>();
        Assert.Contains("local list = {level.terrain.get(12)}", q, System.StringComparison.Ordinal);
        Assert.Contains("{4, 5}", q, System.StringComparison.Ordinal);
        Assert.True(query.Args["dryRun"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Create_Generate_Edit_Query_And_Convert_Through_The_Tool()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });
        int depth = s.Document!.Undo.Position;

        JsonObject created = await s.OkAsync("terrain", new { op = "create", center = new[] { 0.0, 0, 0 }, nx = 33, nz = 33, layers = "rck_default.tga;rck_comflr04.tga" });
        Assert.True(created["committed"]!.GetValue<bool>(), created.ToJsonString());
        JsonNode info = JsonNode.Parse(created["returnValue"]!.GetValue<string>())!;
        Assert.Equal(33, info["nx"]!.GetValue<double>());
        Assert.Equal(depth + 1, s.Document.Undo.Position);

        await s.OkAsync("terrain", new { op = "generate", seed = 4, type = "ridged", height_range = 20 });
        await s.OkAsync("terrain", new { op = "edit", nx = 17, nz = 17, holes = new[] { 1.0, 1 } });
        AlpineTerrain t = s.Document.Rfl.Sections.Select(x => x.Content).OfType<AlpineTerrainSection>().Single().Terrains.Single();
        Assert.Equal(17, t.Nx);
        Assert.True(t.Grid.IsHole(1, 1));
        Assert.Equal(20f, t.HeightRange);
        Assert.Equal(depth + 3, s.Document.Undo.Position);

        JsonObject query = await s.OkAsync("terrain", new { op = "query", points = new[] { 0.0, 0 } });
        Assert.True(query["dryRun"]!.GetValue<bool>());
        JsonNode facts = JsonNode.Parse(query["returnValue"]!.GetValue<string>())![0]!;
        Assert.Single(facts["heights"]!.AsArray());
        Assert.Contains("run Build Geometry", facts["lint"]![0]!.GetValue<string>(), System.StringComparison.Ordinal);
        Assert.Equal(depth + 3, s.Document.Undo.Position);

        // sculpt: one stroke along the points, one undo step; layers carries mode[:radius:strength:layer].
        JsonObject sculpted = await s.OkAsync("terrain", new { op = "sculpt", layers = "raise:6:1", points = new[] { -4.0, 0, 4, 0 } });
        JsonNode stroke = JsonNode.Parse(sculpted["returnValue"]!.GetValue<string>())!;
        Assert.True(stroke["changed"]!.GetValue<bool>() && stroke["geometry"]!.GetValue<bool>(), stroke.ToJsonString());
        Assert.Equal(depth + 4, s.Document.Undo.Position);

        JsonObject converted = await s.OkAsync("terrain", new { op = "to_brushes", keep = true });
        Assert.Equal(1, JsonNode.Parse(converted["returnValue"]!.GetValue<string>())!["brushes"]!.GetValue<double>());
        Assert.Single(s.Document.Rfl.Sections.Select(x => x.Content).OfType<AlpineTerrainSection>().Single().Terrains);
    }
}
