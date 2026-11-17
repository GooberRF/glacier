using System.Text.Json.Nodes;
using Ged.Automation;
using Ged.Core.Lighting.Alpine;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// The lightmap pipeline through automation (CLI / MCP <c>build</c>, ALPINE-COMPAT-SPEC C6): <c>pipeline</c> and
/// <c>density</c> store the level's Alpine level properties before the bake, exactly as ops.lightmaps and the editor
/// do, and the result reports what the bake produced.
/// </summary>
public sealed class AlpineLightmapBuildTests
{
    [Fact]
    public async Task Build_Stores_The_Pipeline_And_Reports_The_Alpine_Lightmaps()
    {
        using var host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        await host.NewAsync(default);
        var commands = new AutomationCommands(host);
        AutomationResponse run = await commands.ExecuteAsync(new AutomationRequest("run", new JsonObject
        {
            ["script"] = "local kit = ged.import('kit')\nkit.room{ name = 'hall', pos = {0, 0, 0}, size = {10, 5, 10} }\nlevel.place('light', 0, 3, 0)",
            ["name"] = "room.lua",
            ["timeoutSeconds"] = 240,
        }));
        Assert.True(run.Ok, run.Error?.Message);

        AutomationResponse legacy = await commands.ExecuteAsync(new AutomationRequest("build", new JsonObject
        {
            ["light"] = true, ["pipeline"] = "legacy", ["density"] = 32,
        }));
        Assert.True(legacy.Ok, legacy.Error?.Message);
        Assert.Equal("legacy", legacy.Result!["lightmaps"]!["pipeline"]!.GetValue<string>());
        Assert.Equal(0, legacy.Result["lightmaps"]!["charts"]!.GetValue<int>());
        AlpineLightmapSettings stored = AlpineLightmapSettings.FromLevel(host.Document!.Rfl);
        Assert.Equal(LightmapPipeline.Legacy, stored.Pipeline);
        Assert.Equal(32, stored.Density);

        AutomationResponse alpine = await commands.ExecuteAsync(new AutomationRequest("build", new JsonObject
        {
            ["light"] = true, ["pipeline"] = "alpine", ["density"] = 0,
        }));
        Assert.True(alpine.Ok, alpine.Error?.Message);
        JsonNode lm = alpine.Result!["lightmaps"]!;
        Assert.Equal("alpine", lm["pipeline"]!.GetValue<string>());
        Assert.True(lm["charts"]!.GetValue<int>() > 0);
        Assert.Equal(8, lm["density"]!.GetValue<int>());
        Assert.NotNull(host.Document.Rfl.AlpineLightmaps);

        AutomationResponse bad = await commands.ExecuteAsync(new AutomationRequest("build", new JsonObject { ["light"] = true, ["pipeline"] = "fancy" }));
        Assert.False(bad.Ok);
    }
}
