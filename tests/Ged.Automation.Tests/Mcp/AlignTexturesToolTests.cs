using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Automation.Tools;
using Xunit;

namespace Ged.Automation.Tests.Mcp;

/// <summary>
/// The MCP face of mapper-style texture alignment: align_textures runs level.uv_align as one AI-labelled undo step
/// (optionally on tagged brushes only), and with report = true runs level.uv_report as a dry run that changes nothing.
/// </summary>
public sealed class AlignTexturesToolTests
{
    [Fact]
    public void The_Tool_Builds_The_Lua_Call_And_Escapes_The_Tag()
    {
        ToolSpec spec = ToolCatalog.Find("align_textures")!;
        (AutomationRequest align, _) = ToolRequests.Build(spec, new JsonObject { ["tag"] = "kit=room \"a\"" }, new ToolRequestOptions());
        Assert.Equal("run", align.Command);
        string script = align.Args!["script"]!.GetValue<string>();
        Assert.Contains("level.brushes_tagged(\"kit\", \"room \\\"a\\\"\")", script, System.StringComparison.Ordinal);
        Assert.Contains("return json(level.uv_align{brushes = q})", script, System.StringComparison.Ordinal);
        Assert.False(align.Args.ContainsKey("dryRun"));

        (AutomationRequest report, _) = ToolRequests.Build(spec, new JsonObject { ["report"] = true }, new ToolRequestOptions());
        Assert.Contains("return json(level.uv_report{brushes = q})", report.Args!["script"]!.GetValue<string>(), System.StringComparison.Ordinal);
        Assert.True(report.Args["dryRun"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Align_Is_One_Undo_Step_And_Report_Changes_Nothing()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });
        int depth = s.Document!.Undo.Position;

        JsonObject report = await s.OkAsync("align_textures", new { report = true });
        Assert.True(report["dryRun"]!.GetValue<bool>());
        JsonNode measured = JsonNode.Parse(report["returnValue"]!.GetValue<string>())!;
        Assert.NotNull(measured["walls"]!["handedness_major"]);
        Assert.Equal(depth, s.Document.Undo.Position);

        JsonObject run = await s.OkAsync("align_textures", null);
        Assert.True(run["committed"]!.GetValue<bool>());
        Assert.Equal(depth + 1, s.Document.Undo.Position);
        Assert.True(JsonNode.Parse(run["returnValue"]!.GetValue<string>())!["faces"]!.GetValue<double>() > 0);
    }
}
