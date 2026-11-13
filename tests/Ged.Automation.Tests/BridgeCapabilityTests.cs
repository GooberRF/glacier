using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// The bridge's optional features (progress lines, cancel — advertised in <c>hello</c>, protocol version 1
/// unchanged) and both compatibility directions: an older client against this server sees one response line
/// per request, and this client against an older editor never sends what that editor cannot read.
/// </summary>
public sealed class BridgeCapabilityTests
{
    private static string NewPipe() => "glacier-test-" + Guid.NewGuid().ToString("N");

    private static async Task<NamedPipeClientStream> RawConnectAsync(string pipe)
    {
        var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await c.ConnectAsync(5000);
        return c;
    }

    private static async Task<JsonObject> ReadObjectAsync(FrameReader reader, int timeoutMs = 10000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        (FrameStatus status, string? line) = await reader.ReadAsync(cts.Token);
        Assert.Equal(FrameStatus.Line, status);
        return JsonNode.Parse(line!)!.AsObject();
    }

    [Fact]
    public async Task Hello_Advertises_Progress_And_Cancel_And_The_Client_Records_Them()
    {
        string pipe = NewPipe();
        await using var editor = new FakeEditor(pipe);
        editor.Start();
        using BridgeClient client = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!;
        Assert.Empty(client.Capabilities);
        AutomationResponse hello = await client.HelloAsync();
        Assert.True(hello.Ok);
        Assert.Equal(BridgeProtocol.Version, hello.Result!["protocol"]!.GetValue<int>());
        Assert.Equal(new[] { "cancel", "progress" }, hello.Result!["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).OrderBy(c => c));
        Assert.True(client.Capabilities.SetEquals(BridgeCapabilities.All));
    }

    [Fact]
    public async Task A_Build_Asking_For_Progress_Streams_Progress_Lines_Before_Its_Response()
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var editor = new FakeEditor(pipe);
        editor.Start();
        using NamedPipeClientStream s = await RawConnectAsync(pipe);
        var reader = new FrameReader(s);
        await BridgeProtocol.WriteLineAsync(s, BridgeProtocol.SerializeRequest(1, new AutomationRequest("open",
            new JsonObject { ["path"] = TestLevels.WriteRoomLevel(dir.File("room.rfl")) })), CancellationToken.None);
        Assert.True((await ReadObjectAsync(reader))["ok"]!.GetValue<bool>());

        await BridgeProtocol.WriteLineAsync(s, BridgeProtocol.SerializeRequest(2, new AutomationRequest("build",
            new JsonObject { ["sealed"] = true }), progress: true), CancellationToken.None);
        var progress = new List<JsonObject>();
        JsonObject line;
        while (!(line = await ReadObjectAsync(reader, 120000)).ContainsKey("ok"))
        {
            progress.Add(line);
        }

        Assert.True(line["ok"]!.GetValue<bool>(), line.ToJsonString());
        Assert.Equal(2, line["id"]!.GetValue<int>());
        Assert.NotEmpty(progress);
        Assert.All(progress, p =>
        {
            Assert.Equal(2, p["id"]!.GetValue<int>());
            Assert.False(string.IsNullOrEmpty(p["progress"]!["stage"]!.GetValue<string>()));
        });

        // Without the flag, the same build sends exactly one line.
        await BridgeProtocol.WriteLineAsync(s, BridgeProtocol.SerializeRequest(3, new AutomationRequest("build",
            new JsonObject { ["sealed"] = true })), CancellationToken.None);
        JsonObject only = await ReadObjectAsync(reader, 120000);
        Assert.True(only.ContainsKey("ok"));
        Assert.Equal(3, only["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_Client_Reports_Progress_Only_After_The_Editor_Advertised_It()
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var editor = new FakeEditor(pipe);
        editor.Start();
        using BridgeClient client = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!;
        Assert.True((await client.SendAsync(new AutomationRequest("open", new JsonObject { ["path"] = TestLevels.WriteRoomLevel(dir.File("room.rfl")) }))).Ok);

        var seen = new List<AutomationProgress>();
        var sink = new Sync<AutomationProgress>(p => { lock (seen) { seen.Add(p); } });
        AutomationResponse before = await client.SendAsync(new AutomationRequest("build"), sink, CancellationToken.None);
        Assert.True(before.Ok, before.Error?.Message);
        Assert.Empty(seen); // no hello yet: the client did not ask

        await client.HelloAsync();
        AutomationResponse after = await client.SendAsync(new AutomationRequest("build", new JsonObject { ["sealed"] = true }), sink, CancellationToken.None);
        Assert.True(after.Ok, after.Error?.Message);
        Assert.NotEmpty(seen);
    }

    [Fact]
    public async Task A_Cancel_Line_Stops_The_Running_Request_Which_Answers_Canceled_And_The_Connection_Stays_Usable()
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var editor = new FakeEditor(pipe);
        editor.Start();
        using BridgeClient client = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!;
        await client.HelloAsync();
        Assert.True((await client.SendAsync(new AutomationRequest("open", new JsonObject { ["path"] = TestLevels.WriteRoomLevel(dir.File("room.rfl")) }))).Ok);
        int objects = editor.Host.Document!.Objects.Count;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        AutomationResponse r = await client.SendAsync(new AutomationRequest("run", new JsonObject
        {
            ["script"] = "level.place('Light', 1, 2, 3)\nwhile true do end",
            ["timeoutSeconds"] = 300,
        }), null, cts.Token);
        Assert.False(r.Ok);
        Assert.True(r.Error!.Code == AutomationErrorCodes.Canceled, r.Error.Message + " " + r.Result?.ToJsonString());
        Assert.False(client.IsBroken);
        Assert.Equal(objects, editor.Host.Document.Objects.Count); // rolled back

        AutomationResponse next = await client.SendAsync(new AutomationRequest("info"));
        Assert.True(next.Ok, next.Error?.Message);
    }

    [Fact]
    public async Task A_Second_Request_While_One_Runs_Is_Busy_And_A_Stray_Cancel_Gets_No_Reply()
    {
        string pipe = NewPipe();
        await using var editor = new FakeEditor(pipe);
        var gate = new TaskCompletionSource();
        editor.BeforeRequest = (r, ct) => r.Command == "info" ? gate.Task.WaitAsync(ct) : Task.CompletedTask;
        editor.Start();
        using NamedPipeClientStream s = await RawConnectAsync(pipe);
        var reader = new FrameReader(s);

        // A cancel for nothing running: no reply (the next line read is the hello's answer).
        await BridgeProtocol.WriteLineAsync(s, BridgeProtocol.SerializeCancel(99), CancellationToken.None);
        await BridgeProtocol.WriteLineAsync(s, "{\"protocol\":1,\"id\":1,\"command\":\"hello\"}", CancellationToken.None);
        Assert.Equal(1, (await ReadObjectAsync(reader))["id"]!.GetValue<int>());

        await BridgeProtocol.WriteLineAsync(s, "{\"protocol\":1,\"id\":2,\"command\":\"info\"}", CancellationToken.None);
        await BridgeProtocol.WriteLineAsync(s, "{\"protocol\":1,\"id\":3,\"command\":\"hello\"}", CancellationToken.None);
        JsonObject busy = await ReadObjectAsync(reader);
        Assert.Equal(3, busy["id"]!.GetValue<int>());
        Assert.Equal(AutomationErrorCodes.Busy, busy["error"]!["code"]!.GetValue<string>());

        // A cancel naming another id does not touch the running request.
        await BridgeProtocol.WriteLineAsync(s, BridgeProtocol.SerializeCancel(7), CancellationToken.None);
        gate.SetResult();
        JsonObject info = await ReadObjectAsync(reader);
        Assert.Equal(2, info["id"]!.GetValue<int>());
        Assert.Equal(AutomationErrorCodes.NoDocument, info["error"]!["code"]!.GetValue<string>()); // it ran to completion
    }

    [Fact]
    public async Task An_Older_Client_Sees_Exactly_One_Line_Per_Request_From_This_Server()
    {
        // An older client writes plain request lines (no progress flag, no cancel) and reads one line back each.
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var editor = new FakeEditor(pipe);
        editor.Start();
        using NamedPipeClientStream s = await RawConnectAsync(pipe);
        var reader = new FrameReader(s);
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl")).Replace("\\", "\\\\", StringComparison.Ordinal);
        string[] requests =
        {
            "{\"protocol\":1,\"id\":1,\"command\":\"hello\"}",
            "{\"protocol\":1,\"id\":2,\"command\":\"open\",\"args\":{\"path\":\"" + level + "\"}}",
            "{\"protocol\":1,\"id\":3,\"command\":\"build\",\"args\":{\"sealed\":true}}",
            "{\"protocol\":1,\"id\":4,\"command\":\"run\",\"args\":{\"script\":\"level.place_box(0, 9, 0, 1, 1, 1)\",\"name\":\"old.lua\"}}",
        };
        foreach ((string request, int id) in requests.Select((r, i) => (r, i + 1)))
        {
            await BridgeProtocol.WriteLineAsync(s, request, CancellationToken.None);
            JsonObject reply = await ReadObjectAsync(reader, 120000);
            Assert.Equal(id, reply["id"]!.GetValue<int>());
            Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
            Assert.False(reply.ContainsKey("progress"));
        }

        Assert.Equal("CLI: old.lua", editor.Host.Document!.Undo.Current.Description);
    }

    [Fact]
    public async Task Against_An_Older_Editor_The_Client_Never_Sends_Progress_Flags_Or_Cancel_Lines()
    {
        string pipe = NewPipe();
        using var dir = new TempDir();
        await using var legacy = new LegacyEditor(pipe);
        using BridgeClient client = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!;
        AutomationResponse hello = await client.HelloAsync();
        Assert.True(hello.Ok);
        Assert.Null(hello.Result!["capabilities"]);
        Assert.Empty(client.Capabilities);

        Assert.True((await client.SendAsync(new AutomationRequest("open", new JsonObject { ["path"] = TestLevels.WriteRoomLevel(dir.File("room.rfl")) }))).Ok);
        var seen = new List<AutomationProgress>();
        AutomationResponse build = await client.SendAsync(new AutomationRequest("build", new JsonObject { ["sealed"] = true }),
            new Sync<AutomationProgress>(seen.Add), CancellationToken.None);
        Assert.True(build.Ok, build.Error?.Message);
        Assert.Empty(seen);

        // Canceling cannot be signalled to this editor: the client abandons the reply instead (and is then unusable).
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new AutomationRequest("run", new JsonObject
        {
            ["script"] = "while true do end",
            ["timeoutSeconds"] = 2,
        }), null, cts.Token));
        Assert.True(client.IsBroken);

        lock (legacy.Lines)
        {
            Assert.All(legacy.Lines, l =>
            {
                JsonObject o = JsonNode.Parse(l)!.AsObject();
                Assert.False(o.ContainsKey("progress"), l);
                Assert.False(o.ContainsKey("cancel"), l);
                Assert.True(o.ContainsKey("command"), l);
            });
        }
    }

    private sealed class Sync<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public Sync(Action<T> report) => _report = report;

        public void Report(T value) => _report(value);
    }
}
