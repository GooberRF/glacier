using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Cli;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// The agent conveniences of glacier-cli: <c>api</c> (the generated reference, no level needed),
/// <c>light</c> (= build --sealed --light), <c>run --build / --light</c>, <c>undo --attach</c> (command-line
/// runs only), and provenance tags in <c>summary</c>.
/// </summary>
public sealed class CliAgentCommandTests
{
    private static CliEnvironment Env(string? pipe = null) => new()
    {
        PipeName = pipe ?? "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Clock = () => TestLevels.Fixed,
    };

    private static Task<(int Code, string Out, string Err)> Cli(CliEnvironment env, params string[] args) => CliTests.Cli(env, args);

    private const string RoomScript = "level.brush{shape = 'box', pos = {0, 2, 0}, size = {10, 4, 10}, air = true}\nlevel.place{kind = 'light', pos = {0, 3, 0}, name = 'l1'}";

    // ---- api ----------------------------------------------------------------------------------

    [Fact]
    public async Task Api_Prints_The_Generated_Reference_Without_A_Level()
    {
        (int code, string o, string e) = await Cli(Env(), "api");
        Assert.True(code == 0, e);
        Assert.Contains("## Conventions", o, StringComparison.Ordinal);
        Assert.Contains(ScriptConventions.Text.Split('\n')[0], o, StringComparison.Ordinal);
        Assert.Contains("### `level`", o, StringComparison.Ordinal);
        Assert.Contains("## Design kit", o, StringComparison.Ordinal);
        Assert.Contains("kit.room{", o, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not edit by hand", o, StringComparison.Ordinal);

        (code, string md, _) = await Cli(Env(), "api", "--markdown");
        Assert.Equal(0, code);
        Assert.Equal(o, md);

        (code, o, _) = await Cli(Env(), "api", "--lua");
        Assert.Equal(0, code);
        Assert.Equal(ScriptApiReference.GenerateLuaStub().Replace("\r\n", "\n", StringComparison.Ordinal),
            o.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Api_Member_Prints_One_Entry_With_Its_Options()
    {
        (int code, string o, string e) = await Cli(Env(), "api", "--member", "level.brush");
        Assert.True(code == 0, e);
        Assert.StartsWith("level.brush(args: table) -> brush", o, StringComparison.Ordinal);
        Assert.Contains("Options:", o, StringComparison.Ordinal);
        Assert.Contains("    shape: string", o, StringComparison.Ordinal);

        (code, o, _) = await Cli(Env(), "api", "--member", "brush:set_pos");
        Assert.Equal(0, code);
        Assert.StartsWith("brush:set_pos(", o, StringComparison.Ordinal);

        (code, o, _) = await Cli(Env(), "api", "--member", "kit.room");
        Assert.Equal(0, code);
        Assert.StartsWith("kit.room{", o, StringComparison.Ordinal);
        Assert.Contains("Options:", o, StringComparison.Ordinal);

        (code, o, _) = await Cli(Env(), "api", "--member", "Brush");
        Assert.Equal(0, code);
        Assert.Equal(ScriptApiReference.Describe("brush")!.Text.Replace("\r\n", "\n", StringComparison.Ordinal),
            o.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));

        (code, o, _) = await Cli(Env(), "api", "--member", "kit.room", "--json");
        Assert.Equal(0, code);
        JsonNode j = JsonNode.Parse(o)!;
        Assert.True(j["ok"]!.GetValue<bool>());
        Assert.Equal("api", j["command"]!.GetValue<string>());
        Assert.Equal("kit.room", j["result"]!["name"]!.GetValue<string>());
        Assert.Equal("text", j["result"]!["format"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("api --member brsh", "Did you mean: brush")]
    [InlineData("api --member brush.set_poz", "set_pos")]
    [InlineData("api --lua --markdown", "alternatives")]
    [InlineData("api --lua --member level", "--member")]
    [InlineData("api --attach", "--attach")]
    [InlineData("api extra", "Unexpected argument")]
    [InlineData("api --out x.rfl", "only")]
    [InlineData("lint a.rfl --member level", "only apply to 'api'")]
    public async Task Api_Usage_Errors_Exit_2(string commandLine, string message)
    {
        (int code, _, string e) = await Cli(Env(), commandLine.Split(' '));
        Assert.Equal(2, code);
        Assert.Contains(message, e, StringComparison.Ordinal);
    }

    // ---- light / run --build ------------------------------------------------------------------------

    [Fact]
    public async Task Light_Is_Build_Sealed_Light_And_Run_Can_Build_Before_Saving()
    {
        using var dir = new TempDir();
        string level = dir.File("t.rfl");
        File.WriteAllText(dir.File("room.lua"), RoomScript);
        CliEnvironment env = Env();

        (int code, string o, string e) = await Cli(env, "run", dir.File("room.lua"), "--out", level, "--build", "--json");
        Assert.True(code == 0, e + o);
        JsonNode run = JsonNode.Parse(o)!["result"]!;
        Assert.True(run["build"]!["sealed"]!.GetValue<bool>());
        Assert.False(run["build"]!["lit"]!.GetValue<bool>());
        Assert.True(run["build"]!["rooms"]!.GetValue<int>() >= 1);
        Assert.Equal(level, run["saved"]!["path"]!.GetValue<string>());
        Assert.False(run["saved"]!["built"]!.GetValue<bool>()); // built before the save: nothing for the save to rebuild

        (code, o, e) = await Cli(env, "light", level, "--save", "--json");
        Assert.True(code == 0, e + o);
        JsonNode light = JsonNode.Parse(o)!;
        Assert.Equal("build", light["command"]!.GetValue<string>());
        Assert.True(light["result"]!["sealed"]!.GetValue<bool>());
        Assert.True(light["result"]!["lit"]!.GetValue<bool>());
        Assert.Equal(level, light["result"]!["saved"]!["path"]!.GetValue<string>());

        (code, o, e) = await Cli(env, "run", dir.File("room.lua"), level, "--light", "--no-shadows");
        Assert.True(code == 0, e + o);
        Assert.Contains("Built (sealed + lit", o, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run a.lua --build --dry-run")]
    [InlineData("run a.lua x.rfl --no-shadows")]
    [InlineData("lint a.rfl --build")]
    [InlineData("light")]
    public async Task Build_Flag_Usage_Errors_Exit_2(string commandLine)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.lua"), "return 1");
        string[] args = commandLine.Split(' ').Select(a => a == "a.lua" ? dir.File("a.lua") : a).ToArray();
        (int code, _, _) = await Cli(Env(), args);
        Assert.Equal(2, code);
    }

    // ---- undo --attach --------------------------------------------------------------------------

    [Fact]
    public async Task Undo_Takes_Back_Only_Command_Line_Runs()
    {
        string pipe = "glacier-test-" + Guid.NewGuid().ToString("N");
        using var host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        var commands = new AutomationCommands(host);
        var server = new BridgeServer(pipe, (req, ct) => commands.ExecuteAsync(req, ct));
        Assert.True(server.TryStart(out string? reason), reason);
        await using (server)
        {
            using var dir = new TempDir();
            CliEnvironment env = Env(pipe);
            File.WriteAllText(dir.File("a.lua"), "level.place{kind = 'light', pos = {0, 3, 0}, name = 'a'}");
            File.WriteAllText(dir.File("b.lua"), "level.place{kind = 'light', pos = {1, 3, 0}, name = 'b'}");

            (int code, string o, string e) = await Cli(env, "new", "--attach");
            Assert.True(code == 0, e + o);
            (code, o, e) = await Cli(env, "undo", "--attach", "--json");
            Assert.Equal(1, code); // a fresh level: nothing to undo
            Assert.Equal(AutomationErrorCodes.UndoRefused, JsonNode.Parse(o)!["error"]!["code"]!.GetValue<string>());

            Assert.Equal(0, (await Cli(env, "run", dir.File("a.lua"), "--attach")).Code);
            await host.RunOnDocumentThreadAsync(() => Task.FromResult(host.Document!.PlaceObject(LevelObjectKind.Light, new Vec3(2, 3, 0))), default); // the designer's own edit
            Assert.Equal(0, (await Cli(env, "run", dir.File("b.lua"), "--attach")).Code);
            int lights() => host.Document!.Objects.Count(x => x.Kind == LevelObjectKind.Light);
            Assert.Equal(3, lights());

            // Two steps would reach the designer's edit: refused, nothing undone.
            (code, o, e) = await Cli(env, "undo", "--attach", "--steps", "2");
            Assert.Equal(1, code);
            Assert.Contains("Place Light", e, StringComparison.Ordinal);
            Assert.Contains("--steps 1", e, StringComparison.Ordinal);
            Assert.Equal(3, lights());

            (code, o, e) = await Cli(env, "undo", "--attach", "--json");
            Assert.True(code == 0, e + o);
            JsonNode undo = JsonNode.Parse(o)!["result"]!;
            Assert.Equal("CLI: b.lua", undo["undone"]![0]!.GetValue<string>());
            Assert.Equal("Place Light", undo["next"]!.GetValue<string>());
            Assert.Equal(2, lights());
            Assert.DoesNotContain(host.Document!.Objects, x => x.ScriptName == "b");

            // Now the designer's edit is on top: refused.
            (code, _, e) = await Cli(env, "undo", "--attach");
            Assert.Equal(1, code);
            Assert.Contains("not a command-line run", e, StringComparison.Ordinal);
            Assert.Equal(2, lights());
        }
    }

    [Theory]
    [InlineData("undo")]
    [InlineData("undo --attach --steps 0")]
    [InlineData("undo --attach --steps two")]
    [InlineData("lint a.rfl --steps 2")]
    public async Task Undo_Usage_Errors_Exit_2(string commandLine)
    {
        (int code, _, _) = await Cli(Env(), commandLine.Split(' '));
        Assert.Equal(2, code);
    }

    // ---- summary tags --------------------------------------------------------------------------

    [Fact]
    public async Task Summary_Lists_Tags_Per_Item_And_Tag_Key_Counts()
    {
        using var dir = new TempDir();
        string level = dir.File("t.rfl");
        File.WriteAllText(dir.File("kit.lua"), "local kit = ged.import('kit')\nlocal r = kit.room{name = 'hall', pos = {0, 0, 0}, size = {8, 4, 8}}\nkit.light_room{room = r, fixture = false}");
        CliEnvironment env = Env();
        (int code, string o, string e) = await Cli(env, "run", dir.File("kit.lua"), "--out", level);
        Assert.True(code == 0, e + o);

        (code, o, _) = await Cli(env, "summary", level, "--detail", "brief", "--json");
        Assert.Equal(0, code);
        JsonNode brief = JsonNode.Parse(o)!["result"]!;
        Assert.Equal(1, brief["brushes"]!["tagKeys"]!["kit.name"]!.GetValue<int>());
        Assert.Equal(1, brief["brushes"]!["tagKeys"]!["ai.session"]!.GetValue<int>());
        Assert.True(brief["objects"]!["tagKeys"]!["kit"]!.GetValue<int>() >= 1);
        Assert.Null(brief["brushes"]!["list"]);

        (code, o, _) = await Cli(env, "summary", level, "--json");
        Assert.Equal(0, code);
        JsonNode normal = JsonNode.Parse(o)!["result"]!;
        JsonNode room = normal["brushes"]!["list"]![0]!;
        Assert.Equal("hall", room["tags"]!["kit.name"]!.GetValue<string>());
        Assert.Equal("kit.lua", room["tags"]!["ai.script"]!.GetValue<string>());
        JsonNode start = normal["objects"]!["list"]!.AsArray().Single(x => x!["kind"]!.GetValue<string>() == "PlayerStart")!;
        Assert.Null(start["tags"]); // the template's Player Start was not made by the run

        (code, o, _) = await Cli(env, "summary", level);
        Assert.Equal(0, code);
        Assert.Contains("Tags:    brushes (", o, StringComparison.Ordinal);
        // Spawns usable by both teams count for each team: the line says "usable by", never a sum.
        Assert.Matches(@"Spawns:  player start (yes|no), \d+ multiplayer spawns \(\d+ usable by red, \d+ by blue, \d+ by bots\)", o);
    }
}
