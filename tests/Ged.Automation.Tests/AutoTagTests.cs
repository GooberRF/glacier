using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Cli;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// Provenance auto-tagging of command-line runs: headless and attached
/// <c>run</c> commands tag everything they create with <c>ai.script</c> + <c>ai.session</c> (one
/// session per CLI invocation / bridge connection; a script's own tag wins), report the created
/// UIDs, and <c>--no-tag</c> turns it off.
/// </summary>
public sealed class AutoTagTests
{
    private static CliEnvironment Env() => new()
    {
        PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Clock = () => TestLevels.Fixed,
    };

    private const string Script = "level.place{kind = 'Light', pos = {0, 3, 0}}\nlevel.brush{pos = {0, 2, 0}, size = 4, air = true, tags = {['ai.script'] = 'mine'}}";

    [Fact]
    public async Task Headless_Run_Tags_Created_Objects_And_Reports_Them()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("gen.lua"), Script);
        (int code, string o, string e) = await CliTests.Cli(Env(), "run", dir.File("gen.lua"), "--out", dir.File("gen.rfl"), "--json");
        Assert.True(code == 0, e + o);
        JsonNode result = JsonNode.Parse(o)!["result"]!;
        int[] created = result["created"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
        Assert.Equal(2, created.Length);
        string session = result["tags"]!["ai.session"]!.GetValue<string>();
        Assert.Equal("gen.lua", result["tags"]!["ai.script"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(session));

        EditorDocument saved = EditorDocument.Open(dir.File("gen.rfl"));
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = new GedObjectMetadataService(saved).AllTags();
        Assert.Equal(created.OrderBy(u => u), tags.Keys.OrderBy(u => u));
        Assert.All(tags.Values, t => Assert.Equal(session, t["ai.session"]));
        Assert.Contains(tags.Values, t => t["ai.script"] == "mine");     // the script's own tag wins
        Assert.Contains(tags.Values, t => t["ai.script"] == "gen.lua");

        // Human-readable output names the created UIDs and the tags.
        (code, o, e) = await CliTests.Cli(Env(), "run", dir.File("gen.lua"), "--out", dir.File("gen2.rfl"));
        Assert.True(code == 0, e + o);
        Assert.Contains("Created 2: UID ", o, StringComparison.Ordinal);
        Assert.Contains("ai.script=gen.lua", o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_Tag_Turns_Auto_Tagging_Off()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("gen.lua"), "level.place{kind = 'Light', pos = {0, 3, 0}}");
        (int code, string o, string e) = await CliTests.Cli(Env(), "run", dir.File("gen.lua"), "--out", dir.File("gen.rfl"), "--no-tag", "--json");
        Assert.True(code == 0, e + o);
        JsonNode result = JsonNode.Parse(o)!["result"]!;
        Assert.Single(result["created"]!.AsArray());
        Assert.Null(result["tags"]);
        EditorDocument saved = EditorDocument.Open(dir.File("gen.rfl"));
        Assert.DoesNotContain(saved.Rfl.Sections, s => s.TypeId == (uint)SectionType.GedObjectMetadata);

        (code, _, _) = await CliTests.Cli(Env(), "lint", dir.File("gen.rfl"), "--no-tag");
        Assert.Equal(2, code);
        (code, o, _) = await CliTests.Cli(Env(), "help", "run");
        Assert.Equal(0, code);
        Assert.Contains("--no-tag", o, StringComparison.Ordinal);
        Assert.Contains("ai.session", o, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_Bridge_Connection_Is_One_Session_And_A_Named_Session_Wins()
    {
        string pipe = "glacier-test-" + Guid.NewGuid().ToString("N");
        using var host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        var commands = new AutomationCommands(host);
        await using var server = new BridgeServer(pipe, (req, ct) => commands.ExecuteAsync(req, ct));
        Assert.True(server.TryStart(out string? reason), reason);

        async Task<string> RunOn(BridgeClient c, string? session = null)
        {
            var args = new JsonObject { ["name"] = "a.lua", ["script"] = "level.place{kind = 'Light', pos = {0, 3, 0}}" };
            if (session is not null)
            {
                args["session"] = session;
            }

            AutomationResponse r = await c.SendAsync(new AutomationRequest("run", args));
            Assert.True(r.Ok, r.Error?.Message);
            return r.Result!["tags"]!["ai.session"]!.GetValue<string>();
        }

        using (BridgeClient c = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!)
        {
            Assert.True((await c.SendAsync(new AutomationRequest("new"))).Ok);
        }

        string first, second, third, named;
        using (BridgeClient c = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!)
        {
            first = await RunOn(c);
            second = await RunOn(c);
            named = await RunOn(c, "agent-42");
        }

        using (BridgeClient c = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!)
        {
            third = await RunOn(c);
        }

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
        Assert.Equal("agent-42", named);
        Assert.Equal(4, new GedObjectMetadataService(host.Document!).AllTags().Count);
    }
}
