using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation.Tools;
using Ged.Cli;
using Ged.Core.Editor;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// The perception / checkpoint / redo commands and the policy, origin, cancellation and progress additions to the
/// shared handlers, on a headless session; plus their CLI verbs and the agent guide's generated tool table.
/// </summary>
public sealed class InspectionCommandTests
{
    private static (HeadlessAutomationHost Host, AutomationCommands Commands) Session(AutomationPolicy? policy = null)
    {
        var host = new HeadlessAutomationHost(EditorSettingsSnapshot.Defaults, rfInstallDir: null, () => TestLevels.Fixed);
        return (host, new AutomationCommands(host) { Policy = policy });
    }

    private static async Task<JsonNode> Ok(AutomationCommands c, string command, JsonObject? args = null)
    {
        AutomationResponse r = await c.ExecuteAsync(new AutomationRequest(command, args));
        Assert.True(r.Ok, $"{command}: {r.Error?.Code} {r.Error?.Message}");
        return r.Result!;
    }

    private static async Task<AutomationError> Fail(AutomationCommands c, string command, JsonObject? args = null)
    {
        AutomationResponse r = await c.ExecuteAsync(new AutomationRequest(command, args));
        Assert.False(r.Ok, $"{command} should fail");
        return r.Error!;
    }

    [Fact]
    public async Task Inspect_Lists_The_Same_Fields_As_A_Lua_Read()
    {
        using var dir = new TempDir();
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            await Ok(c, "open", new JsonObject { ["path"] = TestLevels.WriteRoomLevel(dir.File("room.rfl")) });
            int light = host.Document!.Objects.First(o => o.Kind == LevelObjectKind.Light).Uid;
            JsonNode inspected = await Ok(c, "inspect", new JsonObject { ["uid"] = light });
            Assert.Equal("Light", inspected["kind"]!.GetValue<string>());
            Assert.Equal(new[] { 0.0, 3, 0 }, inspected["pos"]!.AsArray().Select(v => v!.GetValue<double>()));

            // A Lua read of the same object through the script facade.
            JsonNode lua = await Ok(c, "run", new JsonObject
            {
                ["script"] = $"local o = level.find_uid({light})\nlocal names = {{}}\nfor _, f in ipairs(o:fields()) do names[#names + 1] = f.name end\nreturn table.concat(names, ',')",
                ["dryRun"] = true,
            });
            Assert.Equal(lua["returnValue"]!.GetValue<string>(), string.Join(",", inspected["fields"]!.AsArray().Select(f => f!["name"]!.GetValue<string>())));

            int brush = LevelOperations.Brushes(host.Document).First().Uid;
            JsonNode b = await Ok(c, "inspect", new JsonObject { ["uid"] = brush });
            Assert.Equal("Brush", b["kind"]!.GetValue<string>());
            Assert.Contains("air", b["brush"]!["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
            Assert.Equal(6, b["brush"]!["faces"]!.GetValue<int>());

            Assert.Equal(AutomationErrorCodes.Usage, (await Fail(c, "inspect", new JsonObject { ["uid"] = 99999 })).Code);
            Assert.False(host.Document.IsDirty);
        }
    }

    [Fact]
    public async Task Query_Filters_Sorts_And_Pages()
    {
        using var dir = new TempDir();
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            await Ok(c, "open", new JsonObject { ["path"] = TestLevels.WriteRoomLevel(dir.File("room.rfl")) });
            await Ok(c, "run", new JsonObject { ["script"] = "for i = 1, 5 do level.place{kind = 'Light', pos = {i, 2, 0}, name = 'lamp' .. i} end", ["name"] = "lamps.lua" });
            JsonNode all = await Ok(c, "query", new JsonObject { ["kind"] = "Light" });
            Assert.Equal(6, all["total"]!.GetValue<int>());
            List<int> uids = all["items"]!.AsArray().Select(i => i!["uid"]!.GetValue<int>()).ToList();
            Assert.Equal(uids.OrderBy(u => u), uids);

            JsonNode named = await Ok(c, "query", new JsonObject { ["name"] = "lamp*", ["limit"] = 2, ["offset"] = 1 });
            Assert.Equal(5, named["total"]!.GetValue<int>());
            Assert.Equal(2, named["returned"]!.GetValue<int>());
            Assert.True(named["truncated"]!.GetValue<bool>());
            Assert.Contains("offset 3", named["hint"]!.GetValue<string>());

            JsonNode tagged = await Ok(c, "query", new JsonObject { ["tag"] = "ai.script=lamps.lua" });
            Assert.Equal(5, tagged["total"]!.GetValue<int>());
            JsonNode region = await Ok(c, "query", new JsonObject { ["region"] = new JsonArray(0.5, 0, -1, 2.5, 4, 1) });
            Assert.Equal(2, region["total"]!.GetValue<int>());
            JsonNode brushes = await Ok(c, "query", new JsonObject { ["what"] = "brushes" });
            Assert.Equal(2, brushes["total"]!.GetValue<int>());
            Assert.Equal(AutomationErrorCodes.Usage, (await Fail(c, "query", new JsonObject { ["kind"] = "Lite" })).Code);
        }
    }

    [Fact]
    public async Task Catalog_Works_Without_A_Level_Through_The_Lua_Catalog()
    {
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            JsonNode kinds = await Ok(c, "catalog", new JsonObject { ["op"] = "kinds" });
            Assert.Contains("Light", kinds["kinds"]!.AsArray().Select(k => k!.GetValue<string>()));
            JsonNode events = await Ok(c, "catalog", new JsonObject { ["op"] = "classes", ["kind"] = "Event" });
            Assert.Contains("Message", events["classes"]!.AsArray().Select(k => k!.GetValue<string>()));
            JsonNode message = await Ok(c, "catalog", new JsonObject { ["op"] = "describe", ["kind"] = "Event", ["class"] = "Message" });
            Assert.NotEmpty(message["fields"]!.AsArray());
            JsonNode textures = await Ok(c, "catalog", new JsonObject { ["op"] = "textures" });
            Assert.False(textures["available"]!.GetValue<bool>());
            Assert.Null(host.Document);
            AutomationError e = await Fail(c, "catalog", new JsonObject { ["op"] = "describe", ["kind"] = "Event", ["class"] = "Mesage" });
            Assert.Contains("Message", e.Message); // the nearest-name hint
        }
    }

    [Fact]
    public async Task Api_Serves_The_Index_A_Member_Or_The_Whole_Reference()
    {
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            JsonNode index = await Ok(c, "api");
            Assert.Equal("index", index["name"]!.GetValue<string>());
            string text = index["text"]!.GetValue<string>();
            Assert.Contains("  level", text);
            Assert.Contains("kit.room", text);
            Assert.True(text.Length < ApiReferenceText.Markdown().Length / 4);
            Assert.Equal("level.place", (await Ok(c, "api", new JsonObject { ["member"] = "level.place" }))["name"]!.GetValue<string>());
            Assert.Equal(AutomationErrorCodes.Usage, (await Fail(c, "api", new JsonObject { ["member"] = "levl" })).Code);
        }
    }

    [Fact]
    public async Task Checkpoint_And_Diff_Report_Added_Removed_And_Changed_Fields()
    {
        using var dir = new TempDir();
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            await Ok(c, "open", new JsonObject { ["path"] = TestLevels.WriteRoomLevel(dir.File("room.rfl")) });
            int depth = host.Document!.Undo.Position;
            JsonNode cp = await Ok(c, "checkpoint", new JsonObject { ["name"] = "start" });
            Assert.Equal(2, cp["brushes"]!.GetValue<int>());
            Assert.Equal(depth, host.Document.Undo.Position);
            Assert.False(host.Document.IsDirty);

            await Ok(c, "run", new JsonObject
            {
                ["script"] = "local l = level.objects_of('Light')[1]\nl:set_pos(1, 3, 0)\nlevel.place('Light', 5, 2, 0)\nlevel.brushes[1]:move(0, 1, 0)\nlevel.objects_of('MpRespawnPoint')[1]:delete()",
                ["allowDestructive"] = true,
            });
            JsonNode diff = await Ok(c, "diff", new JsonObject { ["since"] = "start" });
            Assert.Equal(1, diff["counts"]!["added"]!.GetValue<int>());
            Assert.Equal(1, diff["counts"]!["removed"]!.GetValue<int>());
            Assert.Equal(2, diff["counts"]!["changed"]!.GetValue<int>());
            JsonNode light = diff["changed"]!.AsArray().First(x => x!["kind"]!.GetValue<string>() == "Light")!;
            JsonNode pos = light["fields"]!.AsArray().First(f => f!["name"]!.GetValue<string>() == "pos")!;
            Assert.Equal(new[] { 0.0, 3, 0 }, pos["before"]!.AsArray().Select(v => v!.GetValue<double>()));
            Assert.Equal(new[] { 1.0, 3, 0 }, pos["after"]!.AsArray().Select(v => v!.GetValue<double>()));
            JsonNode brush = diff["changed"]!.AsArray().First(x => x!["kind"]!.GetValue<string>() == "Brush")!;
            Assert.Contains("position", brush["fields"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()));

            JsonNode capped = await Ok(c, "diff", new JsonObject { ["since"] = "start", ["maxChanges"] = 1 });
            Assert.True(capped["truncated"]!.GetValue<bool>());
            Assert.Equal(AutomationErrorCodes.Usage, (await Fail(c, "diff", new JsonObject { ["since"] = "nope" })).Code);

            for (int i = 0; i < AutomationCommands.MaxCheckpoints + 2; i++)
            {
                await Ok(c, "checkpoint", new JsonObject { ["name"] = "c" + i });
            }

            Assert.Equal(AutomationErrorCodes.Usage, (await Fail(c, "diff", new JsonObject { ["since"] = "start" })).Code); // dropped (oldest)
        }
    }

    [Fact]
    public async Task Redo_Only_Reapplies_Steps_This_Session_Undid()
    {
        using var dir = new TempDir();
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            await Ok(c, "open", new JsonObject { ["path"] = TestLevels.WriteRoomLevel(dir.File("room.rfl")) });
            await Ok(c, "run", new JsonObject { ["script"] = "level.place('Light', 1, 2, 3)", ["name"] = "a.lua" });
            await Ok(c, "run", new JsonObject { ["script"] = "level.place('Light', 2, 2, 3)", ["name"] = "b.lua", ["origin"] = "mcp", ["label"] = "second" });
            Assert.Equal("AI: b.lua — second", host.Document!.Undo.Current.Description);

            JsonNode undone = await Ok(c, "undo", new JsonObject { ["steps"] = 2 });
            Assert.Equal(new[] { "AI: b.lua — second", "CLI: a.lua" }, undone["undone"]!.AsArray().Select(l => l!.GetValue<string>()));
            JsonNode redone = await Ok(c, "redo");
            Assert.Equal("CLI: a.lua", redone["redone"]![0]!.GetValue<string>());

            // The designer undid the other step by hand: not this session's undo, so redo refuses it.
            host.Document.Undo.Redo();
            host.Document.Undo.Undo();
            await Ok(c, "run", new JsonObject { ["script"] = "level.place('Light', 3, 2, 3)", ["name"] = "c.lua" }); // a new branch
            Assert.Equal(AutomationErrorCodes.RedoRefused, (await Fail(c, "redo")).Code);

            // Another session's undo is not redoable from this one either.
            var other = new AutomationCommands(host);
            await Ok(other, "undo");
            Assert.Equal(AutomationErrorCodes.RedoRefused, (await Fail(c, "redo")).Code);
            await Ok(other, "redo");
        }
    }

    [Fact]
    public async Task A_Policy_Overrides_The_Request_Argument_And_Restricts_Commands()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session(new AutomationPolicy { AllowDestructive = false });
        using (host)
        {
            await Ok(c, "open", new JsonObject { ["path"] = level });
            await Ok(c, "run", new JsonObject { ["script"] = "level.place('Light', 1, 2, 3)" });
            AutomationError e = await Fail(c, "open", new JsonObject { ["path"] = level, ["allowDestructive"] = true });
            Assert.Equal(AutomationErrorCodes.DestructiveNotAllowed, e.Code); // the argument is ignored
            AutomationResponse del = await c.ExecuteAsync(new AutomationRequest("run", new JsonObject
            {
                ["script"] = "level.objects_of('Light')[1]:delete()",
                ["allowDestructive"] = true,
            }));
            Assert.Equal(AutomationErrorCodes.ScriptFailed, del.Error!.Code);
        }

        (host, c) = Session(new AutomationPolicy { ReadOnly = true, AllowedCommands = new HashSet<string> { "open", "run", "info" } });
        using (host)
        {
            Assert.Equal(AutomationErrorCodes.ToolNotAllowed, (await Fail(c, "open", new JsonObject { ["path"] = level })).Code);
            Assert.Equal(AutomationErrorCodes.ToolNotAllowed, (await Fail(c, "lint")).Code);
        }
    }

    [Fact]
    public async Task Cancellation_Answers_Canceled_And_Progress_Reports_Compile_Stages()
    {
        using var dir = new TempDir();
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            await Ok(c, "open", new JsonObject { ["path"] = TestLevels.WriteRoomLevel(dir.File("room.rfl")) });
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            AutomationResponse r = await Task.Run(() => c.ExecuteAsync(new AutomationRequest("run", new JsonObject
            {
                ["script"] = "level.place('Light', 1, 2, 3)\nwhile true do end",
                ["timeoutSeconds"] = 0,
            }), cts.Token));
            Assert.Equal(AutomationErrorCodes.Canceled, r.Error!.Code);
            Assert.False(host.Document!.IsDirty);

            var stages = new List<AutomationProgress>();
            JsonNode built = (await c.ExecuteAsync(new AutomationRequest("build", new JsonObject { ["sealed"] = true }), new SyncProgress(stages.Add))).Result!;
            Assert.True(built["sealed"]!.GetValue<bool>());
            Assert.Contains(stages, s => s.Stage.Length > 0);
        }
    }

    [Fact]
    public async Task Cli_Verbs_Run_The_New_Commands()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        CliEnvironment env = Mcp.McpTestSession.Env();
        (int code, string o, string e) = await CliTests.Cli(env, "query", level, "--kind", "Light", "--json");
        Assert.True(code == 0, e);
        int uid = JsonNode.Parse(o)!["result"]!["items"]![0]!["uid"]!.GetValue<int>();
        (code, o, e) = await CliTests.Cli(env, "inspect", uid.ToString(), level);
        Assert.True(code == 0, e);
        Assert.Contains("\"kind\": \"Light\"", o);
        (code, o, _) = await CliTests.Cli(env, "catalog", "classes", "--kind", "Event");
        Assert.Equal(0, code);
        Assert.Contains("Message", o);

        foreach (string[] bad in new[]
        {
            new[] { "checkpoint", "x" }, new[] { "diff", "--since", "x" }, new[] { "redo" }, new[] { "catalog", "frobs" },
            new[] { "inspect", "x", level }, new[] { "lint", level, "--min-severity", "fatal" }, new[] { "summary", level, "--kind", "Light" },
            new[] { "query", level, "--since", "x" }, new[] { "mcp", "--attach", "--headless" }, new[] { "mcp", "--tools", "no_such_tool" },
        })
        {
            (code, _, e) = await CliTests.Cli(env, bad);
            Assert.True(code == 2, $"glacier-cli {string.Join(' ', bad)} should be a usage error: {e}");
        }

        (code, o, _) = await CliTests.Cli(env, "help", "mcp");
        Assert.Equal(0, code);
        Assert.Contains("--allow-destructive", o);
    }

    [Fact]
    public void Agent_Guide_Tool_Table_Is_Current()
    {
        string path = Path.Combine(TestLevels.RepoRoot!, "docs", "ai", "AGENT-GUIDE.md");
        string doc = File.ReadAllText(path);
        string generated = ToolDocs.GuideTable();
        if (Environment.GetEnvironmentVariable("GED_REGEN_SCRIPT_DOCS") == "1")
        {
            File.WriteAllText(path, ToolDocs.ReplaceBlock(doc, generated));
            doc = File.ReadAllText(path);
        }

        Assert.Equal(generated, ToolDocs.CurrentBlock(doc));
        foreach (ToolSpec t in ToolCatalog.All)
        {
            Assert.Contains($"`{t.Name}`", doc);
        }
    }

    private sealed class SyncProgress : IProgress<AutomationProgress>
    {
        private readonly Action<AutomationProgress> _report;

        public SyncProgress(Action<AutomationProgress> report) => _report = report;

        public void Report(AutomationProgress value)
        {
            lock (this)
            {
                _report(value);
            }
        }
    }
}
