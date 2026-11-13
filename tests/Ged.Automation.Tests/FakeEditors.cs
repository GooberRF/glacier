using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// A window-less stand-in for the running editor on a per-test pipe: this build's <see cref="BridgeServer"/>
/// (progress + cancel) over a headless host, with the editor bridge's AI policy — an <c>origin: "mcp"</c>
/// request loses <c>allowDestructive</c> unless <see cref="AllowAiDestructive"/> is on, and <c>hello</c> reports
/// the setting (the App suite tests the real <c>EditorBridge</c>, which does the same).
/// </summary>
internal sealed class FakeEditor : IAsyncDisposable
{
    private BridgeServer? _server;

    public FakeEditor(string pipe, HeadlessAutomationHost? host = null)
    {
        Pipe = pipe;
        Host = host ?? new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        Commands = new AutomationCommands(Host);
    }

    public string Pipe { get; }

    public HeadlessAutomationHost Host { get; }

    public AutomationCommands Commands { get; }

    /// <summary>The editor's "Allow destructive AI actions" setting.</summary>
    public bool AllowAiDestructive { get; set; }

    /// <summary>Called before each request runs (tests block or observe here).</summary>
    public Func<AutomationRequest, CancellationToken, Task>? BeforeRequest { get; set; }

    /// <summary>Every request the editor received (after its policy).</summary>
    public List<AutomationRequest> Received { get; } = new();

    public void Start()
    {
        _server = new BridgeServer(Pipe, HandleAsync);
        Assert.True(_server.TryStart(out string? reason), reason);
    }

    /// <summary>Closes the pipe (the editor quits); the host and its document stay for a restart.</summary>
    public async Task StopAsync()
    {
        if (_server is { } s)
        {
            _server = null;
            await s.DisposeAsync();
        }
    }

    private async Task<AutomationResponse> HandleAsync(AutomationRequest request, IProgress<AutomationProgress>? progress, CancellationToken ct)
    {
        bool fromAgent = request.Args["origin"]?.GetValue<string>() == "mcp";
        if (fromAgent && !AllowAiDestructive && request.Args.ContainsKey("allowDestructive"))
        {
            request.Args["allowDestructive"] = false;
        }

        lock (Received)
        {
            Received.Add(new AutomationRequest(request.Command, (JsonObject)request.Args.DeepClone()));
        }

        if (BeforeRequest is { } before)
        {
            await before(request, ct);
        }

        AutomationResponse response = await Commands.ExecuteAsync(request, progress, ct);
        if (request.Command == "hello" && response.Ok && response.Result is JsonObject hello)
        {
            hello["destructiveAutomation"] = AllowAiDestructive;
        }

        return response;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Host.Dispose();
    }
}

/// <summary>
/// An editor from before the bridge's optional features: one request line in, one response line out, nothing
/// read while a request runs, no <c>capabilities</c> in <c>hello</c>, and any line that is not a request
/// (e.g. a cancel line) answered with a <c>protocol</c> error. Records every line it receives.
/// </summary>
internal sealed class LegacyEditor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public LegacyEditor(string pipe)
    {
        Host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        Commands = new AutomationCommands(Host);
        _loop = Task.Run(() => ServeAsync(pipe, _stop.Token));
    }

    public HeadlessAutomationHost Host { get; }

    public AutomationCommands Commands { get; }

    /// <summary>Raw request lines received.</summary>
    public List<string> Lines { get; } = new();

    private async Task ServeAsync(string pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(ct);
                var reader = new FrameReader(server);
                while (true)
                {
                    (FrameStatus status, string? line) = await reader.ReadAsync(ct);
                    if (status != FrameStatus.Line)
                    {
                        break;
                    }

                    lock (Lines)
                    {
                        Lines.Add(line!);
                    }

                    ParsedRequest parsed = ParsedRequest.Parse(line!);
                    AutomationResponse response = parsed.Request is { } request
                        ? await Commands.ExecuteAsync(request, ct)
                        : new AutomationResponse { Ok = false, Error = parsed.Error };
                    await BridgeProtocol.WriteLineAsync(server, BridgeProtocol.SerializeResponse(parsed.Id, response), ct);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // client gone / stopping
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
            // stopping
        }

        Host.Dispose();
        _stop.Dispose();
    }
}
