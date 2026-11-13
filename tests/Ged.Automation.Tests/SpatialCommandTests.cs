using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// The <c>spatial</c> and <c>grade</c> commands (DESIGN-QUALITY-SPEC §(e)): the CLI verbs, their readable and JSON
/// output, exit codes, freshness reporting, and that they never change the level.
/// </summary>
public sealed class SpatialCommandTests
{
    [Fact]
    public async Task Grade_Prints_A_Verdict_With_Fixes_And_Locations()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (int code, string o, string e) = await CliTests.Cli(CliTests.Env(), "grade", level);
        Assert.True(code == 0, e + o);
        Assert.Contains("Verdict:", o, StringComparison.Ordinal);
        Assert.Contains("fix:", o, StringComparison.Ordinal);
        Assert.Contains("Geometry: none (measured preview)", o, StringComparison.Ordinal);

        (code, o, e) = await CliTests.Cli(CliTests.Env(), "grade", level, "--json", "--gametype", "dm", "--players", "4", "--max-locations", "2");
        Assert.True(code == 0, e + o);
        JsonNode r = JsonNode.Parse(o)!["result"]!;
        Assert.Equal("glacier.grade/1", r["schema"]!.GetValue<string>());
        Assert.Equal("FAIL", r["verdict"]!.GetValue<string>()); // one small room is far too simple
        Assert.Equal("S", r["player_class"]!.GetValue<string>());
        Assert.Equal("preview", r["geometry"]!["measured"]!.GetValue<string>());
        JsonArray failures = r["failures"]!.AsArray();
        Assert.Equal(r["failures_count"]!.GetValue<int>(), failures.Count);
        Assert.All(failures, f =>
        {
            Assert.False(string.IsNullOrEmpty(f!["fix"]!.GetValue<string>()));
            Assert.True(f["locations"]!.AsArray().Count <= 4);
        });
    }

    [Fact]
    public async Task Grade_Exit_Codes_Follow_The_Other_Verbs()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        Assert.Equal(6, (await CliTests.Cli(CliTests.Env(), "grade", level, "--fail-on", "fail")).Code);
        Assert.Equal(6, (await CliTests.Cli(CliTests.Env(), "grade", level, "--fail-on", "review")).Code);
        Assert.Equal(2, (await CliTests.Cli(CliTests.Env(), "grade", level, "--fail-on", "sometimes")).Code);
        Assert.Equal(2, (await CliTests.Cli(CliTests.Env(), "grade", level, "--from", "1,2,3")).Code); // a spatial-only flag
        (int code, _, string e) = await CliTests.Cli(CliTests.Env(), "grade", level, "--fresh", "none");
        Assert.Equal(1, code); // no compiled geometry and no build allowed
        Assert.Contains("no_geometry", e, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spatial_Queries_Run_From_The_Command_Line()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (int code, string o, string e) = await CliTests.Cli(CliTests.Env(), "spatial", "raycast", level, "--from", "0,1,0", "--dir", "1,0,0", "--json");
        Assert.True(code == 0, e + o);
        JsonNode r = JsonNode.Parse(o)!["result"]!;
        Assert.True(r["hit"]!.GetValue<bool>());
        Assert.InRange(r["distance"]!.GetValue<double>(), 5.9, 13.1);

        (code, o, e) = await CliTests.Cli(CliTests.Env(), "spatial", "walk", level, "--from", "0,0,0", "--json");
        Assert.True(code == 0, e + o);
        r = JsonNode.Parse(o)!["result"]!;
        Assert.True(r["playable_area"]!.GetValue<double>() > 100);
        Assert.Equal("preview", r["geometry"]!["measured"]!.GetValue<string>());

        (code, o, _) = await CliTests.Cli(CliTests.Env(), "spatial", "graph", level, "--kind", "cell", "--scale", "8", "--max-nodes", "3", "--json");
        Assert.Equal(0, code);
        Assert.True(JsonNode.Parse(o)!["result"]!["node_list"]!.AsArray().Count <= 3);

        Assert.Equal(2, (await CliTests.Cli(CliTests.Env(), "spatial", "teleport", level)).Code);
        Assert.Equal(2, (await CliTests.Cli(CliTests.Env(), "spatial", "graph", level, "--scale", "6")).Code);
        Assert.Equal(2, (await CliTests.Cli(CliTests.Env(), "spatial", "raycast", level, "--from", "0,1")).Code);
        Assert.Equal(2, (await CliTests.Cli(CliTests.Env(), "spatial")).Code);
    }

    [Fact]
    public async Task Spatial_Commands_Never_Change_The_Level()
    {
        using var host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        await host.OpenAsync(level, default);
        var commands = new AutomationCommands(host);
        byte[] before = host.Document!.Rfl.Save(updateTimestamp: false);
        foreach ((string cmd, JsonObject args) in new[]
        {
            ("grade", new JsonObject()),
            ("grade", new JsonObject { ["build"] = "sealed" }),
            ("spatial", new JsonObject { ["op"] = "area_report", ["around"] = new JsonArray(0, 0, 0), ["radius"] = 6 }),
            ("spatial", new JsonObject { ["op"] = "point_info", ["pos"] = new JsonArray(0, 1, 0), ["build"] = "preview" }),
        })
        {
            AutomationResponse r = await commands.ExecuteAsync(new AutomationRequest(cmd, args));
            Assert.True(r.Ok, r.Error?.Message);
        }

        Assert.False(host.Document.IsDirty);
        Assert.Equal(before, host.Document.Rfl.Save(updateTimestamp: false));

        AutomationResponse bad = await commands.ExecuteAsync(new AutomationRequest("spatial", new JsonObject { ["op"] = "routes", ["a"] = new JsonArray(0, 0, 0) }));
        Assert.False(bad.Ok);
        Assert.Equal(AutomationErrorCodes.Usage, bad.Error!.Code);
        Assert.Contains("'b' is required", bad.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_Pieces_And_Advisories_Reach_The_Command_Line()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (int code, string o, string e) = await CliTests.Cli(CliTests.Env(), "spatial", "setpieces", level, "--max", "2", "--json");
        Assert.True(code == 0, e + o);
        JsonNode r = JsonNode.Parse(o)!["result"]!;
        Assert.Equal(0, r["bridge_count"]!.GetValue<int>()); // one flat room: no bridge, landmark or atrium
        Assert.Null(r["bridge_span_max"]);
        Assert.True(r["largest_spaces"]!.AsArray().Count is >= 1 and <= 2);
        Assert.Equal("preview", r["geometry"]!["measured"]!.GetValue<string>());

        (code, o, e) = await CliTests.Cli(CliTests.Env(), "grade", level, "--players", "4");
        Assert.True(code == 0, e + o);
        Assert.Contains("Advisory (player class S targets", o, StringComparison.Ordinal);
        Assert.Contains("not counted in the verdict", o, StringComparison.Ordinal);
        Assert.Contains("Set pieces: 0 bridge(s)", o, StringComparison.Ordinal);

        (code, o, _) = await CliTests.Cli(CliTests.Env(), "grade", level, "--players", "4", "--json");
        Assert.Equal(0, code);
        r = JsonNode.Parse(o)!["result"]!;
        JsonArray adv = r["advisories"]!.AsArray();
        Assert.Contains(adv, a => a!["metric"]!.GetValue<string>() == "playable_area" && a["status"]!.GetValue<string>() == "below");
        Assert.All(adv, a => Assert.True(a!["advisory"]!.GetValue<bool>()));
        Assert.DoesNotContain(r["failures"]!.AsArray(), f => adv.Any(a => a!["metric"]!.GetValue<string>() == f!["id"]!.GetValue<string>()));
        Assert.Equal(0, r["set_pieces"]!["bridges"]!.GetValue<int>());
    }
}
