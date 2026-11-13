using System.Text.Json.Nodes;
using Ged.Automation.Tools;
using Xunit;

namespace Ged.Automation.Tests.Mcp;

/// <summary>How MCP tool arguments map onto automation requests: the run_lua budget and the render size.</summary>
public sealed class ToolRequestMappingTests
{
    private static JsonObject ArgsOf(string tool, JsonObject args, int maxImage = ToolCatalog.DefaultImageSide) =>
        ToolRequests.Build(ToolCatalog.Find(tool)!, args, new ToolRequestOptions { MaxImageSide = maxImage }).Request.Args;

    [Fact]
    public void Run_Lua_Budget_Is_The_Instruction_Budget()
    {
        JsonObject a = ArgsOf("run_lua", new JsonObject { ["code"] = "print(1)", ["budget"] = 5000, ["timeout_s"] = 60 });
        Assert.Equal(5000, a["instructionBudget"]!.GetValue<double>());
        Assert.Equal(60, a["timeoutSeconds"]!.GetValue<double>());

        JsonObject unlimited = ArgsOf("run_lua", new JsonObject { ["code"] = "print(1)", ["budget"] = 0 });
        Assert.Equal(0, unlimited["instructionBudget"]!.GetValue<double>());
        Assert.False(ArgsOf("run_lua", new JsonObject { ["code"] = "print(1)" }).ContainsKey("instructionBudget"));

        Assert.Throws<AutomationException>(() => ArgsOf("run_lua", new JsonObject { ["code"] = "print(1)", ["budget"] = 1.5 }));
    }

    [Fact]
    public void Render_Without_A_Size_Passes_The_Longest_Side_So_A_Region_Can_Set_The_Proportions()
    {
        JsonObject free = ArgsOf("render_plan", new JsonObject { ["region"] = new JsonArray(0, 0, 0, 30, 3, 10) }, maxImage: 800);
        Assert.Equal(800, free["maxSide"]!.GetValue<double>());
        Assert.False(free.ContainsKey("width"));

        JsonObject fixedSize = ArgsOf("render_plan", new JsonObject { ["size"] = "640x480" });
        Assert.Equal(640, fixedSize["width"]!.GetValue<double>());
        Assert.Equal(480, fixedSize["height"]!.GetValue<double>());
        Assert.False(fixedSize.ContainsKey("maxSide"));
    }
}
