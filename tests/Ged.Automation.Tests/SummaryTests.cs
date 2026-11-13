using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>The structured level summary: shape, determinism, and the brief size bound.</summary>
public sealed class SummaryTests
{
    private static async Task<JsonNode> Summary(string level, string detail, bool build = false)
    {
        using var host = new HeadlessAutomationHost();
        host.PathPolicy.AllowExplicit(level);
        var c = new AutomationCommands(host);
        if (build)
        {
            AutomationResponse b = await c.ExecuteAsync(new AutomationRequest("build", new JsonObject { ["level"] = level, ["sealed"] = true }));
            Assert.True(b.Ok, b.Error?.Message);
        }

        AutomationResponse r = await c.ExecuteAsync(new AutomationRequest("summary", new JsonObject { ["level"] = level, ["detail"] = detail }));
        Assert.True(r.Ok, r.Error?.Message);
        return r.Result!;
    }

    [Fact]
    public async Task Summary_Has_Counts_Lists_And_Rooms_After_A_Build()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));

        JsonNode unbuilt = await Summary(level, "brief");
        Assert.Equal("glacier.summary/1", unbuilt["schema"]!.GetValue<string>());
        Assert.Equal("not built", unbuilt["rooms"]!["status"]!.GetValue<string>());
        Assert.Null(unbuilt["brushes"]!["list"]);
        Assert.Equal(2, unbuilt["brushes"]!["air"]!.GetValue<int>());
        Assert.Equal(1, unbuilt["objects"]!["byKind"]!["Light"]!.GetValue<int>());
        Assert.True(unbuilt["objects"]!["spawns"]!["playerStart"]!.GetValue<bool>());
        Assert.Equal(1, unbuilt["objects"]!["spawns"]!["multiplayer"]!.GetValue<int>());
        JsonNode spawns = unbuilt["objects"]!["spawns"]!;
        int red = spawns["red"]!.GetValue<int>(), blue = spawns["blue"]!.GetValue<int>(), both = spawns["bothTeams"]!.GetValue<int>();
        Assert.InRange(both, 0, System.Math.Min(red, blue)); // red and blue each include the spawns both teams use
        Assert.True(red + blue - both <= spawns["multiplayer"]!.GetValue<int>());
        Assert.NotNull(unbuilt["bounds"]);

        JsonNode normal = await Summary(level, "normal", build: true);
        JsonArray brushes = normal["brushes"]!["list"]!.AsArray();
        Assert.Equal(2, brushes.Count);
        Assert.Contains("air", brushes[0]!["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.True(brushes[0]!["uid"]!.GetValue<int>() < brushes[1]!["uid"]!.GetValue<int>()); // ordered by uid
        JsonArray objects = normal["objects"]!["list"]!.AsArray();
        JsonNode light = objects.First(o => o!["kind"]!.GetValue<string>() == "Light")!;
        Assert.NotNull(light["props"]!["range"]);
        Assert.StartsWith("#", light["props"]!["color"]!.GetValue<string>());
        Assert.Equal("built", normal["rooms"]!["status"]!.GetValue<string>());
        Assert.True(normal["rooms"]!["count"]!.GetValue<int>() >= 1);
        Assert.NotNull(normal["rooms"]!["list"]![0]!["volume"]);
    }

    [Fact]
    public async Task Summary_Is_Deterministic()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        foreach (string detail in new[] { "brief", "normal", "full" })
        {
            string a = (await Summary(level, detail, build: true)).ToJsonString();
            string b = (await Summary(level, detail, build: true)).ToJsonString();
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public async Task Brief_Summary_Stays_Small()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        Assert.True((await Summary(level, "brief")).ToJsonString().Length < 2048);

        // dm01-size levels (corpus; skipped when absent).
        if (TestLevels.Corpus("dm01.rfl") is { } dm01)
        {
            string brief = (await Summary(dm01, "brief")).ToJsonString();
            Assert.True(brief.Length < 2048, $"brief summary of dm01 is {brief.Length} bytes");
            JsonNode full = await Summary(dm01, "full");
            Assert.True(full["rooms"]!["count"]!.GetValue<int>() > 10);
            Assert.Equal(full["objects"]!["total"]!.GetValue<int>(), full["objects"]!["list"]!.AsArray().Count);
        }
    }

    [Fact]
    public async Task Unknown_Detail_Is_A_Usage_Error()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        using var host = new HeadlessAutomationHost();
        host.PathPolicy.AllowExplicit(level);
        AutomationResponse r = await new AutomationCommands(host).ExecuteAsync(
            new AutomationRequest("summary", new JsonObject { ["level"] = level, ["detail"] = "huge" }));
        Assert.Equal(AutomationErrorCodes.Usage, r.Error!.Code);
    }
}
