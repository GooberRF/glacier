using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Cli;
using Ged.Cli.Mcp;
using Ged.Core.Editor;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ged.Automation.Tests.Mcp;

/// <summary>
/// An in-process MCP server (the real <see cref="McpServerHost"/> over a headless session) connected to the SDK's
/// own client through an in-memory duplex pipe pair — no process spawn. The session's scratch folder and a level
/// folder live in a per-test temp directory; no settings file, no RF install.
/// </summary>
internal sealed class McpTestSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _server = Task.CompletedTask;

    private McpTestSession(TempDir dir, McpToolService service)
    {
        Dir = dir;
        Service = service;
    }

    public TempDir Dir { get; }

    public McpToolService Service { get; }

    public McpClient Client { get; private set; } = null!;

    /// <summary>The session's headless host (its document, undo history and build state).</summary>
    public HeadlessAutomationHost Host => ((HeadlessBackend)Service.Backend).Host;

    public EditorDocument? Document => Host.Document;

    public string Scratch => Path.Combine(Dir.Path, "scratch");

    /// <summary>Isolated launch environment: no settings file, no RF install, no editor.</summary>
    public static CliEnvironment Env() => new()
    {
        PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromMilliseconds(300),
        Clock = () => TestLevels.Fixed,
    };

    /// <summary>Starts a session; <paramref name="options"/> defaults to a plain (mutating, non-destructive) server.</summary>
    public static async Task<McpTestSession> StartAsync(McpLaunchOptions? options = null, bool connect = true)
    {
        var dir = new TempDir();
        string scratch = Path.Combine(dir.Path, "scratch");
        Directory.CreateDirectory(scratch);
        McpToolService service = McpCommand.CreateService(options ?? new McpLaunchOptions(), scratch, TextWriter.Null, Env());
        var session = new McpTestSession(dir, service);
        if (connect)
        {
            await session.ConnectAsync();
        }

        return session;
    }

    /// <summary>Starts a session over a service built by <paramref name="create"/> (given the scratch folder) — e.g. attached to a fake editor.</summary>
    public static async Task<McpTestSession> StartAsync(Func<string, Task<McpToolService>> create)
    {
        var dir = new TempDir();
        string scratch = Path.Combine(dir.Path, "scratch");
        Directory.CreateDirectory(scratch);
        McpToolService service = await create(scratch);
        var session = new McpTestSession(dir, service);
        await session.ConnectAsync();
        return session;
    }

    private async Task ConnectAsync()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var host = new McpServerHost(Service);
        _server = Task.Run(() => host.RunAsync(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), _stop.Token));
        var transport = new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
        Client = await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "glacier-tests", Version = "1" } });
    }

    /// <summary>Writes the two-room test level into the temp folder and returns its path.</summary>
    public string WriteRoomLevel(string name = "room.rfl") => TestLevels.WriteRoomLevel(Dir.File(name));

    /// <summary>Calls a tool; returns (isError, structured JSON, the result).</summary>
    public async Task<(bool IsError, JsonObject Json, CallToolResult Result)> CallAsync(
        string tool, object? args = null, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellation = default)
    {
        Dictionary<string, object?> dict = args switch
        {
            null => new Dictionary<string, object?>(),
            Dictionary<string, object?> d => d,
            _ => JsonSerializer.SerializeToNode(args)!.AsObject().ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        };
        CallToolResult result = await Client.CallToolAsync(tool, dict, progress, cancellationToken: cancellation);
        var text = (TextContentBlock)result.Content[0];
        JsonObject json = JsonNode.Parse(text.Text)!.AsObject();
        return (result.IsError == true, json, result);
    }

    /// <summary>Calls a tool that must succeed.</summary>
    public async Task<JsonObject> OkAsync(string tool, object? args = null)
    {
        (bool isError, JsonObject json, _) = await CallAsync(tool, args);
        Assert.False(isError, $"{tool}: {json.ToJsonString()}");
        return json;
    }

    /// <summary>Calls a tool that must fail with <paramref name="code"/>.</summary>
    public async Task<JsonObject> FailAsync(string tool, string code, object? args = null)
    {
        (bool isError, JsonObject json, _) = await CallAsync(tool, args);
        Assert.True(isError, $"{tool} should fail: {json.ToJsonString()}");
        Assert.Equal(code, json["code"]?.GetValue<string>());
        return json;
    }

    public async ValueTask DisposeAsync()
    {
        if (Client is not null)
        {
            await Client.DisposeAsync();
        }

        _stop.Cancel();
        try
        {
            await _server.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
        {
            // the server stops when the client disconnects or the token fires
        }

        Service.Dispose();
        Dir.Dispose();
        _stop.Dispose();
    }
}
