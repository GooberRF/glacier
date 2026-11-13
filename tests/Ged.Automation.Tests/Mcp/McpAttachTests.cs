using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Cli;
using Ged.Cli.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ged.Automation.Tests.Mcp;

/// <summary>
/// The MCP server attached to an editor (MCP-SERVER-SPEC §3.2, §3.5, §7, §9 "Attach round trip" / "Attach
/// unavailable"): an in-process bridge server over a window-less host stands in for the editor — this build's
/// (<see cref="FakeEditor"/>: progress + cancel, the editor's AI destructive setting) and an older one
/// (<see cref="LegacyEditor"/>: no capabilities) — and the real MCP server talks to it over a named pipe.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class McpAttachTests
{
    private static string NewPipe() => "glacier-test-" + Guid.NewGuid().ToString("N");

    private static CliEnvironment EnvFor(string pipe, double connectSeconds = 5) => new()
    {
        PipeName = pipe,
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromSeconds(connectSeconds),
        Clock = () => TestLevels.Fixed,
    };

    private static Task<McpTestSession> AttachAsync(string pipe, McpLaunchOptions? options = null, double connectSeconds = 5) =>
        McpTestSession.StartAsync(scratch => McpCommand.CreateServiceAsync(
            options ?? new McpLaunchOptions { Mode = McpMode.Attach }, scratch, TextWriter.Null, EnvFor(pipe, connectSeconds)));

    private static async Task OpenInEditorAsync(AutomationCommands editor, string level)
    {
        AutomationResponse r = await editor.ExecuteAsync(new AutomationRequest("open", new JsonObject { ["path"] = level }));
        Assert.True(r.Ok, r.Error?.Message);
    }

    [Fact]
    public async Task Tools_Run_In_The_Editor_As_AI_Undo_Steps_And_Renders_Draw_A_Copy()
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var editor = new FakeEditor(pipe);
        editor.Start();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        await OpenInEditorAsync(editor.Commands, level);

        await using McpTestSession s = await AttachAsync(pipe);
        Assert.IsType<AttachBackend>(s.Service.Backend);
        Assert.Contains("attached to the running editor", s.Client.ServerInstructions);

        JsonObject info = await s.OkAsync("session_info");
        Assert.Equal("attach", info["mode"]!.GetValue<string>());
        Assert.Equal("--attach", info["modeChosenBy"]!.GetValue<string>());
        Assert.True(info["editor"]!["connected"]!.GetValue<bool>());
        Assert.Equal(new[] { "cancel", "progress" }, info["editor"]!["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(level, info["document"]!["path"]!.GetValue<string>());
        Assert.Contains(Path.GetDirectoryName(level)!, info["writeRoots"]!.AsArray().Select(r => r!.GetValue<string>()));

        // A script: one undo step in the editor, labelled AI, with provenance tags.
        int depth = editor.Host.Document!.Undo.Position;
        JsonObject run = await s.OkAsync("run_lua", new { code = "local l = level.place('Light', 1, 2, 3)\nreturn l.uid", label = "add a light" });
        Assert.Equal(depth + 1, editor.Host.Document.Undo.Position);
        Assert.Equal("AI: mcp-1.lua — add a light", editor.Host.Document.Undo.Current.Description);
        int uid = run["created"]!.AsArray().Single()!.GetValue<int>();
        JsonObject inspected = await s.OkAsync("inspect", new { uid });
        Assert.Equal("mcp-1.lua", inspected["tags"]!["ai.script"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(inspected["tags"]!["ai.session"]?.GetValue<string>()));

        // A render works on a snapshot copy: the editor's undo history, dirty flag and geometry state stay.
        bool dirty = editor.Host.Document.IsDirty;
        var state = editor.Host.BuildState;
        (bool isError, JsonObject map, CallToolResult result) = await s.CallAsync("render_plan", new { size = "256x256" });
        Assert.False(isError, map.ToJsonString());
        Assert.Equal("editor", map["source"]!.GetValue<string>());
        ImageContentBlock image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        (int width, int height, _) = PngTestReader.Decode(image.DecodedData.ToArray());
        Assert.Equal((256, 256), (width, height));
        Assert.Equal(depth + 1, editor.Host.Document.Undo.Position);
        Assert.Equal(dirty, editor.Host.Document.IsDirty);
        Assert.Equal(state, editor.Host.BuildState);
        lock (editor.Received)
        {
            Assert.Contains(editor.Received, r => r.Command == "snapshot");
            Assert.DoesNotContain(editor.Received, r => r.Command == "render");
        }

        // undo takes the AI step back.
        JsonObject undo = await s.OkAsync("undo");
        Assert.Equal("AI: mcp-1.lua — add a light", undo["undone"]![0]!.GetValue<string>());
        Assert.Equal(depth, editor.Host.Document.Undo.Position);
    }

    [Fact]
    public async Task A_Build_Streams_Progress_From_The_Editor_And_A_Canceled_Script_Is_Rolled_Back_There()
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var editor = new FakeEditor(pipe);
        editor.Start();
        await OpenInEditorAsync(editor.Commands, TestLevels.WriteRoomLevel(dir.File("room.rfl")));
        await using McpTestSession s = await AttachAsync(pipe);

        int reports = 0;
        ToolOutcome build = await s.Service.CallAsync("build", null, _ => Interlocked.Increment(ref reports), CancellationToken.None);
        Assert.False(build.IsError, build.Structured.ToJsonString());
        Assert.True(reports > 0);
        lock (editor.Received)
        {
            Assert.Contains(editor.Received, r => r.Command == "build");
        }

        int objects = editor.Host.Document!.Objects.Count;
        int depth = editor.Host.Document.Undo.Position;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        ToolOutcome canceled = await s.Service.CallAsync("run_lua", new JsonObject
        {
            ["code"] = "level.place('Light', 1, 2, 3)\nwhile true do end",
            ["timeout_s"] = 300,
        }, null, cts.Token);
        Assert.True(canceled.IsError);
        Assert.Equal(AutomationErrorCodes.Canceled, canceled.Structured["code"]!.GetValue<string>());
        Assert.Equal(objects, editor.Host.Document.Objects.Count);
        Assert.Equal(depth, editor.Host.Document.Undo.Position);

        // The cancel was answered in band: the same connection carries on (no reconnect note).
        JsonObject info = await s.OkAsync("session_info");
        Assert.Null(info["notes"]);
        Assert.True(((AttachBackend)s.Service.Backend).IsConnected);
    }

    [Fact]
    public async Task Attached_To_An_Older_Editor_Tools_Work_Without_Progress_Or_Cancel_Lines()
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var legacy = new LegacyEditor(pipe);
        await OpenInEditorAsync(legacy.Commands, TestLevels.WriteRoomLevel(dir.File("room.rfl")));
        await using McpTestSession s = await AttachAsync(pipe);

        JsonObject info = await s.OkAsync("session_info");
        Assert.Empty(info["editor"]!["capabilities"]!.AsArray());

        int reports = 0;
        ToolOutcome build = await s.Service.CallAsync("build", null, _ => Interlocked.Increment(ref reports), CancellationToken.None);
        Assert.False(build.IsError, build.Structured.ToJsonString());
        Assert.Equal(0, reports);

        await s.OkAsync("run_lua", new { code = "level.place('Light', 1, 2, 3)", name = "old-editor.lua" });
        Assert.Equal("AI: old-editor.lua", legacy.Host.Document!.Undo.Current.Description);

        // Canceling cannot be signalled to an older editor: the call reports canceled, the connection is dropped,
        // and the next call reconnects (once the editor has finished the abandoned request) and says so.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        ToolOutcome canceled = await s.Service.CallAsync("run_lua", new JsonObject { ["code"] = "while true do end", ["timeout_s"] = 2 }, null, cts.Token);
        Assert.Equal(AutomationErrorCodes.Canceled, canceled.Structured["code"]!.GetValue<string>());
        JsonObject after = await s.OkAsync("session_info");
        Assert.Contains(after["notes"]!.AsArray(), n => n!.GetValue<string>().StartsWith("Reconnected to the editor", StringComparison.Ordinal));

        lock (legacy.Lines)
        {
            Assert.All(legacy.Lines, l =>
            {
                JsonObject o = JsonNode.Parse(l)!.AsObject();
                Assert.False(o.ContainsKey("progress"), l);
                Assert.False(o.ContainsKey("cancel"), l);
            });
        }
    }

    [Fact]
    public async Task With_No_Editor_The_Server_Starts_Reports_Editor_Unavailable_And_Connects_Once_One_Runs()
    {
        string pipe = NewPipe();
        await using McpTestSession s = await AttachAsync(pipe, connectSeconds: 0.3);
        IList<McpClientTool> tools = await s.Client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "run_lua");

        JsonObject error = await s.FailAsync("session_info", AutomationErrorCodes.EditorUnavailable);
        Assert.Contains("Allow command-line tools and AI agents", error["message"]!.GetValue<string>());
        await s.FailAsync("level_summary", AutomationErrorCodes.EditorUnavailable);

        await using var editor = new FakeEditor(pipe);
        editor.Start();
        JsonObject info = await s.OkAsync("session_info");
        Assert.Equal("attach", info["mode"]!.GetValue<string>());
        Assert.True(info["editor"]!["connected"]!.GetValue<bool>());
        Assert.Null(info["notes"]); // a first connection is not a reconnect
    }

    [Fact]
    public async Task When_The_Editor_Restarts_The_Next_Call_Reconnects_And_Says_So()
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var editor = new FakeEditor(pipe);
        editor.Start();
        await OpenInEditorAsync(editor.Commands, TestLevels.WriteRoomLevel(dir.File("room.rfl")));
        await using McpTestSession s = await AttachAsync(pipe, connectSeconds: 0.5);
        await s.OkAsync("level_summary", new { detail = "brief" });

        await editor.StopAsync();
        await s.FailAsync("level_summary", AutomationErrorCodes.EditorDisconnected);
        await s.FailAsync("level_summary", AutomationErrorCodes.EditorDisconnected); // still gone: retried, still lost

        editor.Start();
        JsonObject summary = await s.OkAsync("level_summary", new { detail = "brief" });
        Assert.Contains(summary["notes"]!.AsArray(), n => n!.GetValue<string>().StartsWith("Reconnected to the editor", StringComparison.Ordinal));
        JsonObject again = await s.OkAsync("level_summary", new { detail = "brief" });
        Assert.Null(again["notes"]); // said once
    }

    [Fact]
    public async Task The_Default_Mode_Attaches_When_An_Editor_Answers_At_Startup_And_Is_Headless_Otherwise()
    {
        string pipe = NewPipe();
        await using (McpTestSession headless = await AttachAsync(pipe, new McpLaunchOptions(), connectSeconds: 0.3))
        {
            Assert.IsType<HeadlessBackend>(headless.Service.Backend);
            JsonObject info = await headless.OkAsync("session_info");
            Assert.Equal("headless", info["mode"]!.GetValue<string>());
            Assert.Equal("startup probe", info["modeChosenBy"]!.GetValue<string>());

            // An editor started later does not switch a running headless session.
            await using var late = new FakeEditor(pipe);
            late.Start();
            Assert.Equal("headless", (await headless.OkAsync("session_info"))["mode"]!.GetValue<string>());
        }

        await using var editor = new FakeEditor(pipe);
        editor.Start();
        await using McpTestSession attached = await AttachAsync(pipe, new McpLaunchOptions());
        Assert.IsType<AttachBackend>(attached.Service.Backend);
        JsonObject a = await attached.OkAsync("session_info");
        Assert.Equal("attach", a["mode"]!.GetValue<string>());
        Assert.Equal("startup probe", a["modeChosenBy"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Destructive_Actions_In_The_Editor_Need_Both_The_Server_Flag_And_The_Editor_Setting(bool serverFlag, bool editorSetting)
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var editor = new FakeEditor(pipe) { AllowAiDestructive = editorSetting };
        editor.Start();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        await OpenInEditorAsync(editor.Commands, level);
        Assert.True((await editor.Commands.ExecuteAsync(new AutomationRequest("run", new JsonObject { ["script"] = "level.place('Light', 1, 2, 3)" }))).Ok);
        Assert.True(editor.Host.Document!.IsDirty);

        await using McpTestSession s = await AttachAsync(pipe, new McpLaunchOptions { Mode = McpMode.Attach, AllowDestructive = serverFlag });
        JsonObject info = await s.OkAsync("session_info");
        Assert.Equal(serverFlag && editorSetting, info["safety"]!["destructive"]!.GetValue<bool>());
        Assert.Equal(editorSetting, info["editor"]!["destructiveSetting"]!.GetValue<bool>());

        (bool isError, JsonObject json, _) = await s.CallAsync("new_level");
        if (serverFlag && editorSetting)
        {
            Assert.False(isError, json.ToJsonString());
            Assert.Null(editor.Host.Document!.Path); // the unsaved work was discarded for a new level
        }
        else
        {
            Assert.True(isError);
            Assert.Equal(AutomationErrorCodes.DestructiveNotAllowed, json["code"]!.GetValue<string>());
            Assert.Contains("Allow destructive AI actions", json["message"]!.GetValue<string>());
            Assert.Contains(serverFlag ? "setting" : "--allow-destructive", json["message"]!.GetValue<string>());
            Assert.Equal(level, editor.Host.Document!.Path);
        }
    }

    [Fact]
    public async Task An_Older_Editor_Never_Receives_A_Destructive_Request()
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var legacy = new LegacyEditor(pipe);
        await OpenInEditorAsync(legacy.Commands, TestLevels.WriteRoomLevel(dir.File("room.rfl")));
        Assert.True((await legacy.Commands.ExecuteAsync(new AutomationRequest("run", new JsonObject { ["script"] = "level.place('Light', 1, 2, 3)" }))).Ok);

        await using McpTestSession s = await AttachAsync(pipe, new McpLaunchOptions { Mode = McpMode.Attach, AllowDestructive = true });
        Assert.False((await s.OkAsync("session_info"))["safety"]!["destructive"]!.GetValue<bool>());
        JsonObject error = await s.FailAsync("new_level", AutomationErrorCodes.DestructiveNotAllowed);
        Assert.Contains("does not offer", error["message"]!.GetValue<string>());
        lock (legacy.Lines)
        {
            Assert.DoesNotContain(legacy.Lines, l => l.Contains("allowDestructive", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_Read_Only_Attached_Server_Hides_Mutators_And_Never_Sends_Them_Or_A_Callers_AllowDestructive()
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var editor = new FakeEditor(pipe) { AllowAiDestructive = true };
        editor.Start();
        await OpenInEditorAsync(editor.Commands, TestLevels.WriteRoomLevel(dir.File("room.rfl")));
        await using McpTestSession s = await AttachAsync(pipe, new McpLaunchOptions { Mode = McpMode.Attach, ReadOnly = true, AllowDestructive = true });

        IList<McpClientTool> tools = await s.Client.ListToolsAsync();
        Assert.DoesNotContain(tools, t => t.Name is "run_lua" or "build" or "save_level" or "undo");
        ToolOutcome refused = await s.Service.CallAsync("run_lua", new JsonObject { ["code"] = "return 1" }, null, CancellationToken.None);
        Assert.Equal(AutomationErrorCodes.ToolNotAllowed, refused.Structured["code"]!.GetValue<string>());

        // Even below the tool layer: a mutating command is refused before it is sent, and a request's own
        // allowDestructive never reaches the editor.
        var backend = (AttachBackend)s.Service.Backend;
        AutomationResponse direct = await backend.ExecuteAsync(new AutomationRequest("new", new JsonObject { ["allowDestructive"] = true, ["origin"] = "mcp" }), null, CancellationToken.None);
        Assert.Equal(AutomationErrorCodes.ToolNotAllowed, direct.Error!.Code);
        await backend.ExecuteAsync(new AutomationRequest("info", new JsonObject { ["allowDestructive"] = true, ["origin"] = "mcp" }), null, CancellationToken.None);
        lock (editor.Received)
        {
            Assert.DoesNotContain(editor.Received, r => r.Command is "run" or "new");
            Assert.All(editor.Received.Where(r => r.Command == "info"), r => Assert.False(r.Args.ContainsKey("allowDestructive")));
        }
    }
}
