using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation.Tools;
using Ged.Cli.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ged.Automation.Tests.Mcp;

/// <summary>
/// The MCP server end to end through the SDK's own client over an in-memory duplex (MCP-SERVER-SPEC §9):
/// handshake, the tools/list snapshot and its rules, every tool once, the undo contract, the undo guard,
/// policy, cancellation, progress and concurrency.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class McpServerTests
{
    private static readonly Regex SelfAuthorisation = new("allow|force|destructive", RegexOptions.IgnoreCase);

    [Fact]
    public async Task Handshake_Negotiates_And_Sends_Short_Instructions()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        Assert.False(string.IsNullOrEmpty(s.Client.NegotiatedProtocolVersion));
        Assert.Equal("glacier", s.Client.ServerInfo!.Name);
        Assert.Equal(AutomationCommands.ProductVersion, s.Client.ServerInfo!.Version);
        Assert.NotNull(s.Client.ServerCapabilities.Tools);
        Assert.NotNull(s.Client.ServerCapabilities.Resources);
        Assert.NotNull(s.Client.ServerCapabilities.Prompts);
        string instructions = s.Client.ServerInstructions ?? string.Empty;
        Assert.InRange(instructions.Length, 200, 1200);
        Assert.Contains("run_lua", instructions);
        Assert.Contains("AI:", instructions);
        Assert.Contains("glacier://guide", instructions);
    }

    [Fact]
    public async Task Tools_List_Matches_The_Catalog_The_Snapshot_And_The_Description_Rules()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        IList<McpClientTool> tools = await s.Client.ListToolsAsync();
        Assert.Equal(ToolCatalog.All.Select(t => t.Name), tools.Select(t => t.Name));

        var listed = new JsonArray();
        foreach (McpClientTool t in tools)
        {
            Tool p = t.ProtocolTool;
            listed.Add(new JsonObject
            {
                ["name"] = p.Name,
                ["title"] = p.Title,
                ["description"] = p.Description,
                ["inputSchema"] = JsonNode.Parse(p.InputSchema.GetRawText()),
                ["annotations"] = JsonSerializer.SerializeToNode(p.Annotations, McpJsonUtilities.DefaultOptions),
            });
        }

        string payload = listed.ToJsonString();
        Assert.True(payload.Length <= 24 * 1024, $"tools/list is {payload.Length} characters (limit 24 KB).");

        foreach (ToolSpec spec in ToolCatalog.All)
        {
            Assert.True(spec.Description.Length <= 700, $"{spec.Name}: description is {spec.Description.Length} characters (limit 700).");
            Assert.DoesNotMatch(new Regex(@"\b(phase|coming soon|will be added|later|legacy|not yet|TODO)\b", RegexOptions.IgnoreCase), spec.Description);
            Assert.False(string.IsNullOrWhiteSpace(spec.Summary));
            foreach (ToolArg a in spec.Args)
            {
                Assert.False(string.IsNullOrWhiteSpace(a.Doc), $"{spec.Name}.{a.Name} has no doc");
            }
        }

        string snapshotPath = Path.Combine(TestLevels.RepoRoot!, "tests", "Ged.Automation.Tests", "Mcp", "mcp-tools.snapshot.json");
        string current = JsonSerializer.Serialize(JsonNode.Parse(payload), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        if (Environment.GetEnvironmentVariable("GED_REGEN_SCRIPT_DOCS") == "1")
        {
            File.WriteAllText(snapshotPath, current);
        }

        Assert.True(File.Exists(snapshotPath), "mcp-tools.snapshot.json is missing. Regenerate with GED_REGEN_SCRIPT_DOCS=1.");
        Assert.Equal(File.ReadAllText(snapshotPath).Replace("\r\n", "\n", StringComparison.Ordinal), current);
    }

    [Fact]
    public void No_Tool_Schema_Can_Carry_Its_Own_Authorisation()
    {
        foreach (ToolSpec spec in ToolCatalog.All)
        {
            JsonObject schema = ToolSchema.For(spec);
            foreach (KeyValuePair<string, JsonNode?> p in schema["properties"]!.AsObject())
            {
                Assert.DoesNotMatch(SelfAuthorisation, p.Key);
            }

            foreach (ToolArg a in spec.Args)
            {
                Assert.DoesNotMatch(SelfAuthorisation, a.CommandName);
            }
        }
    }

    [Fact]
    public async Task Unknown_And_Ill_Typed_Arguments_Are_Usage_Errors_Naming_The_Argument()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        JsonObject e = await s.FailAsync("level_summary", AutomationErrorCodes.Usage, new { detail = "huge" });
        Assert.Contains("detail", e["message"]!.GetValue<string>());
        e = await s.FailAsync("undo", AutomationErrorCodes.Usage, new { steps = 1, allowDestructive = true });
        Assert.Contains("allowDestructive", e["message"]!.GetValue<string>());
        e = await s.FailAsync("inspect", AutomationErrorCodes.Usage, new { uid = "seven" });
        Assert.Contains("uid", e["message"]!.GetValue<string>());
        await s.FailAsync("level_summary", AutomationErrorCodes.NoDocument);
    }

    /// <summary>
    /// Minimal valid arguments for every tool, in an order that works on one session (data-driven over the catalog:
    /// a new tool without an entry here fails the test).
    /// </summary>
    private static List<(string Tool, Func<McpTestSession, object?> Args)> EveryToolPlan(string level) => new()
    {
        ("session_info", _ => null),
        ("api_reference", _ => new { member = "kit.room" }),
        ("list_classes", _ => new { kind = "Event" }),
        ("describe_class", _ => new { kind = "Event", @class = "Message" }),
        ("list_textures", _ => new { filter = "rock" }),
        ("new_level", _ => null),
        ("open_level", _ => new { path = level }),
        ("level_summary", _ => new { detail = "brief" }),
        ("query", _ => new { kind = "Light" }),
        ("inspect", s => new { uid = s.Document!.Objects.First().Uid }),
        ("checkpoint", _ => new { name = "before" }),
        ("run_lua", _ => new { code = "level.place('Light', 1, 2, 3)", label = "one light" }),
        ("diff_since", _ => new { checkpoint = "before" }),
        ("undo", _ => null),
        ("redo", _ => null),
        ("build", _ => null),
        ("check_holes", _ => null),
        ("lint", _ => new { min_severity = "warning" }),
        ("norms", _ => new { metric = "opening.width" }),
        ("compare_to_norms", _ => null),
        ("spatial_query", _ => new { op = "walk" }),
        ("grade_level", _ => null),
        ("render_plan", _ => new { size = "128x128" }),
        ("render_view", _ => new { view = "iso", size = "128x96" }),
        ("save_level", _ => null),
    };

    [Fact]
    [Trait("Category", "Mcp")]
    public async Task Every_Tool_Runs_Once_With_Minimal_Arguments()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        string level = s.WriteRoomLevel();
        List<(string Tool, Func<McpTestSession, object?> Args)> plan = EveryToolPlan(level);
        Assert.Equal(ToolCatalog.All.Select(t => t.Name).OrderBy(n => n), plan.Select(p => p.Tool).OrderBy(n => n));
        bool gpu = HasGpu();
        foreach ((string tool, Func<McpTestSession, object?> args) in plan)
        {
            (bool isError, JsonObject json, CallToolResult result) = await s.CallAsync(tool, args(s));
            if (!gpu && tool.StartsWith("render_", StringComparison.Ordinal))
            {
                Assert.Equal("render_unavailable", json["code"]?.GetValue<string>());
                continue;
            }

            Assert.False(isError, $"{tool}: {json.ToJsonString()}");
            Assert.NotNull(result.StructuredContent);
            Assert.Equal(json.ToJsonString(), JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.ToJsonString());
            if (ToolCatalog.Find(tool)!.ReturnsImage)
            {
                var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
                Assert.Equal("image/png", image.MimeType);
            }
        }

        Assert.False(s.Document!.IsDirty); // saved last
    }

    internal static bool HasGpu()
    {
        try
        {
            Ged.Automation.Rendering.RenderDevices.Create().Device.Dispose();
            return true;
        }
        catch (AutomationException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Run_Lua_Is_One_Ai_Labelled_Undo_Step_And_Read_Tools_Change_Nothing()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });
        int depth = s.Document!.Undo.Position;

        JsonObject run = await s.OkAsync("run_lua", new { code = "level.place('Light', 1, 2, 3)", name = "lights.lua", label = "add a light" });
        Assert.Equal("AI: lights.lua — add a light", run["undoLabel"]!.GetValue<string>());
        Assert.Equal(depth + 1, s.Document.Undo.Position);
        Assert.Equal("AI: lights.lua — add a light", s.Document.Undo.Current.Description);

        await s.OkAsync("run_lua", new { code = "level.place('Light', 4, 2, 3)", dry_run = true });
        Assert.Equal(depth + 1, s.Document.Undo.Position);

        // Every read tool: undo depth, dirty flag and geometry state unchanged.
        bool dirty = s.Document.IsDirty;
        string geometry = (await s.OkAsync("session_info"))["document"]!["geometry"]!.GetValue<string>();
        var reads = new (string Tool, object? Args)[]
        {
            ("session_info", null), ("level_summary", null), ("query", null), ("inspect", new { uid = s.Document.Objects.First().Uid }),
            ("checkpoint", new { name = "c" }), ("diff_since", new { checkpoint = "c" }), ("check_holes", null), ("lint", null),
            ("list_classes", new { kind = "Event" }), ("describe_class", new { kind = "Light" }), ("list_textures", null), ("api_reference", null),
            ("norms", null), ("compare_to_norms", null), ("spatial_query", new { op = "point_info", p = new[] { 0.0, 1, 0 } }), ("grade_level", null),
        };
        Assert.Equal(
            ToolCatalog.All.Where(t => t.Safety == ToolSafety.Read && !t.ReturnsImage).Select(t => t.Name).OrderBy(n => n),
            reads.Select(r => r.Tool).OrderBy(n => n));
        foreach ((string tool, object? args) in reads)
        {
            await s.OkAsync(tool, args);
            Assert.Equal(depth + 1, s.Document.Undo.Position);
            Assert.Equal(dirty, s.Document.IsDirty);
            Assert.Equal(geometry, (await s.OkAsync("session_info"))["document"]!["geometry"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Undo_Takes_Back_Automation_Steps_Only_And_Redo_Restores_Them()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });

        // A designer edit (not an automation run) sits under the agent's run.
        s.Document!.PlaceObject(Ged.Core.Editor.LevelObjectKind.Light, new Ged.Core.Model.Vec3(0, 1, 0));
        await s.OkAsync("run_lua", new { code = "level.place('Light', 1, 2, 3)", name = "a.lua" });
        int objects = s.Document.Objects.Count;

        JsonObject undone = await s.OkAsync("undo");
        Assert.Equal("AI: a.lua", undone["undone"]![0]!.GetValue<string>());
        Assert.Equal(objects - 1, s.Document.Objects.Count);

        JsonObject refused = await s.FailAsync("undo", AutomationErrorCodes.UndoRefused);
        Assert.Contains("not a command-line run", refused["message"]!.GetValue<string>());

        JsonObject redone = await s.OkAsync("redo");
        Assert.Equal("AI: a.lua", redone["redone"]![0]!.GetValue<string>());
        Assert.Equal(objects, s.Document.Objects.Count);
        await s.FailAsync("redo", AutomationErrorCodes.RedoRefused);

        // A CLI-labelled run is an automation step too.
        var cli = new AutomationCommands(s.Host);
        AutomationResponse r = await cli.ExecuteAsync(new AutomationRequest("run", new JsonObject { ["script"] = "level.place('Light', 5, 2, 3)", ["name"] = "cli.lua" }));
        Assert.True(r.Ok);
        Assert.Equal("CLI: cli.lua", (await s.OkAsync("undo"))["undone"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Read_Only_Hides_Mutating_Tools_And_Refuses_Them()
    {
        await using McpTestSession s = await McpTestSession.StartAsync(new McpLaunchOptions { ReadOnly = true });
        IList<McpClientTool> tools = await s.Client.ListToolsAsync();
        Assert.Equal(ToolCatalog.All.Where(t => !t.Mutates).Select(t => t.Name), tools.Select(t => t.Name));
        await s.FailAsync("run_lua", AutomationErrorCodes.ToolNotAllowed, new { code = "return 1" });
        JsonObject info = await s.OkAsync("session_info");
        Assert.False(info["safety"]!["mutate"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Tool_Allow_And_Deny_Lists_Apply_To_Listing_And_Dispatch()
    {
        await using McpTestSession s = await McpTestSession.StartAsync(new McpLaunchOptions
        {
            Tools = new HashSet<string> { "session_info", "api_reference", "run_lua" },
            DenyTools = new HashSet<string> { "run_lua" },
        });
        Assert.Equal(new[] { "session_info", "api_reference" }, (await s.Client.ListToolsAsync()).Select(t => t.Name));
        await s.FailAsync("run_lua", AutomationErrorCodes.ToolNotAllowed, new { code = "return 1" });
        await s.FailAsync("level_summary", AutomationErrorCodes.ToolNotAllowed);
        await s.OkAsync("api_reference");
    }

    [Fact]
    public async Task Destructive_Actions_Need_The_Launch_Flag_Never_An_Argument()
    {
        foreach (bool allowed in new[] { false, true })
        {
            await using McpTestSession s = await McpTestSession.StartAsync(new McpLaunchOptions { AllowDestructive = allowed });
            string level = s.WriteRoomLevel();
            await s.OkAsync("open_level", new { path = level });
            await s.OkAsync("run_lua", new { code = "level.place('Light', 1, 2, 3)" }); // now dirty

            // Discarding unsaved changes.
            (bool isError, JsonObject json, _) = await s.CallAsync("open_level", new { path = level });
            Assert.Equal(!allowed, isError);
            if (!allowed)
            {
                Assert.Equal(AutomationErrorCodes.DestructiveNotAllowed, json["code"]!.GetValue<string>());
            }

            // A script's delete.
            await s.OkAsync("save_level");
            await s.OkAsync("open_level", new { path = s.WriteRoomLevel("other.rfl") });
            (isError, json, _) = await s.CallAsync("run_lua", new { code = "level.objects_of('Light')[1]:delete()" });
            Assert.Equal(!allowed, isError);
            if (!allowed)
            {
                Assert.Equal(AutomationErrorCodes.ScriptFailed, json["code"]!.GetValue<string>());
            }
        }
    }

    [Fact]
    public async Task Writes_Outside_The_Allowed_Folders_Are_Refused()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });
        using var elsewhere = new TempDir();
        await s.FailAsync("save_level", AutomationErrorCodes.PathNotAllowed, new { @out = elsewhere.File("x.rfl") });
        await s.OkAsync("save_level", new { @out = Path.Combine(s.Scratch, "copy.rfl") });
    }

    [Fact]
    public async Task A_Script_Canceled_By_The_Client_Leaves_The_Level_Unchanged()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });
        int objects = s.Document!.Objects.Count;
        int depth = s.Document.Undo.Position;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            s.CallAsync("run_lua", new { code = "level.place('Light', 1, 2, 3)\nwhile true do end", timeout_s = 300 }, cancellation: cts.Token));

        // The server finishes the canceled call (rolled back) and takes the next one.
        JsonObject info = await s.OkAsync("session_info");
        Assert.Equal(objects, info["document"]!["objects"]!.GetValue<int>());
        Assert.Equal(depth, s.Document.Undo.Position);

        // The same at the service level, where the outcome is visible: canceled.
        using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        ToolOutcome o = await s.Service.CallAsync("run_lua", new JsonObject { ["code"] = "while true do end", ["timeout_s"] = 300 }, null, cts2.Token);
        Assert.True(o.IsError);
        Assert.Equal(AutomationErrorCodes.Canceled, o.Structured["code"]!.GetValue<string>());
        Assert.Equal(objects, s.Document.Objects.Count);
    }

    [Fact]
    public async Task A_Build_Canceled_Mid_Compile_Applies_Nothing()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });
        using var cts = new CancellationTokenSource();
        int reports = 0;
        ToolOutcome o = await s.Service.CallAsync("build", null, _ =>
        {
            reports++;
            cts.Cancel(); // first compile stage reported: cancel mid-compile
        }, cts.Token);
        Assert.True(reports > 0);
        Assert.True(o.IsError, o.Structured.ToJsonString());
        Assert.Equal(AutomationErrorCodes.Canceled, o.Structured["code"]!.GetValue<string>());
        JsonObject info = await s.OkAsync("session_info");
        Assert.Equal("none", info["document"]!["geometry"]!.GetValue<string>());
        Assert.False(s.Document!.IsDirty);
    }

    [Fact]
    public async Task Build_With_A_Progress_Token_Sends_Progress_Before_The_Result()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });

        // Count the notifications on the session itself (the per-call IProgress is detached when the call
        // returns, and the client raises notifications on its own tasks); the wire order — every notification
        // before the result — is checked on the real process by the stdout test.
        var seen = new List<JsonNode>();
        await using IAsyncDisposable handler = s.Client.RegisterNotificationHandler(NotificationMethods.ProgressNotification, (n, _) =>
        {
            lock (seen)
            {
                seen.Add(JsonNode.Parse(n.Params!.ToJsonString())!);
            }

            return ValueTask.CompletedTask;
        });
        (bool isError, JsonObject json, _) = await s.CallAsync("build", null, new SyncProgress<ProgressNotificationValue>(_ => { }));
        Assert.False(isError, json.ToJsonString());
        Assert.True(json["sealed"]!.GetValue<bool>()); // sealed is the MCP default
        for (int i = 0; i < 250; i++)
        {
            lock (seen)
            {
                if (seen.Count > 0)
                {
                    break;
                }
            }

            await Task.Delay(20);
        }

        lock (seen)
        {
            Assert.NotEmpty(seen);
            Assert.All(seen, v => Assert.False(string.IsNullOrEmpty(v["message"]?.GetValue<string>())));
            // A strictly increasing count 1..n (the client may raise them out of order).
            Assert.Equal(Enumerable.Range(1, seen.Count).Select(i => (double)i), seen.Select(v => v["progress"]!.GetValue<double>()).OrderBy(p => p));
        }
    }

    [Fact]
    public async Task Parallel_Calls_Serialise_In_Arrival_Order_And_Both_Commit()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });
        int depth = s.Document!.Undo.Position;
        Task<(bool, JsonObject, CallToolResult)> first = s.CallAsync("run_lua", new { code = "for i = 1, 2000000 do end\nlevel.place('Light', 1, 2, 3)", name = "first.lua" });
        await Task.Delay(50);
        Task<(bool, JsonObject, CallToolResult)> second = s.CallAsync("run_lua", new { code = "level.place('Light', 4, 2, 3)", name = "second.lua" });
        await Task.WhenAll(first, second);
        Assert.False(first.Result.Item1, first.Result.Item2.ToJsonString());
        Assert.False(second.Result.Item1, second.Result.Item2.ToJsonString());
        Assert.Equal(depth + 2, s.Document.Undo.Position);
        Assert.Equal(new[] { "AI: second.lua", "AI: first.lua" }, UndoHistory.Recent(s.Document.Undo, 2));
    }

    [Fact]
    public async Task Run_Lua_Names_Default_Per_Session_And_Logs_Are_Budgeted()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("new_level");
        JsonObject a = await s.OkAsync("run_lua", new { code = "for i = 1, 500 do print('line ' .. i) end" });
        Assert.Equal("mcp-1.lua", a["script"]!.GetValue<string>());
        Assert.Equal(OutputBudget.LogHead + OutputBudget.LogTail, a["log"]!.AsArray().Count(l => l!["message"]!.GetValue<string>().StartsWith("line ", StringComparison.Ordinal)));
        Assert.True(a["logTruncated"]!.GetValue<int>() > 0);
        Assert.Equal("mcp-2.lua", (await s.OkAsync("run_lua", new { code = "return 1" }))["script"]!.GetValue<string>());

        // A failing script's diagnostics come back with the error.
        JsonObject e = await s.FailAsync("run_lua", AutomationErrorCodes.ScriptFailed, new { code = "local x = nil\nx.y = 1" });
        Assert.Equal(2, e["result"]!["error"]!["line"]!.GetValue<int>());
    }

    /// <summary>An IProgress that invokes the callback synchronously (the SDK raises it on its own thread).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public SyncProgress(Action<T> report) => _report = report;

        public void Report(T value) => _report(value);
    }
}
