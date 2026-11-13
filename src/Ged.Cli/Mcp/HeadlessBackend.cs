using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation;
using Ged.Automation.Rendering;
using Ged.Rendering.Graphics;

namespace Ged.Cli.Mcp;

/// <summary>
/// Where the MCP server's tool calls execute: a headless session in this process (<see cref="HeadlessBackend"/>),
/// or the running editor over the bridge (<see cref="AttachBackend"/>). Both run the same
/// <see cref="AutomationCommands"/> handlers.
/// </summary>
internal interface IMcpBackend : IDisposable
{
    /// <summary>"headless", or "attach" when the calls run in the editor.</summary>
    string Mode { get; }

    /// <summary>
    /// Whether the session's host permits destructive actions at all (on top of the launch flag): always for a
    /// headless session; in the editor, its "Allow destructive AI actions" setting.
    /// </summary>
    bool HostAllowsDestructive => true;

    /// <summary>A one-time note for the next tool result (e.g. "reconnected to the editor"), or null.</summary>
    string? TakeNotice() => null;

    /// <summary>Mode-specific facts for <c>session_info</c> (the editor connection), or null.</summary>
    JsonObject? Describe() => null;

    /// <summary>The folders tools may write under right now.</summary>
    IReadOnlyList<string> WriteRoots { get; }

    /// <summary>The mounted RF install, or null.</summary>
    string? RfInstallDir { get; }

    /// <summary>Executes one command (never throws; failures are error responses).</summary>
    Task<AutomationResponse> ExecuteAsync(AutomationRequest request, IProgress<AutomationProgress>? progress, CancellationToken cancellation);

    /// <summary>Renders a private copy of the open level (never the document itself).</summary>
    Task<AutomationResponse> RenderAsync(AutomationRequest request, CancellationToken cancellation);
}

/// <summary>
/// A window-less session for the server's lifetime: one <see cref="HeadlessAutomationHost"/> (the user's
/// editor settings and RF install, no document until <c>open_level</c> / <c>new_level</c>) and one command set
/// whose <see cref="AutomationPolicy"/> comes from the launch flags. Renders copy the level out with
/// <c>snapshot</c> into a private host (<see cref="RenderOrchestrator"/>), so they never touch the document.
/// </summary>
internal sealed class HeadlessBackend : IMcpBackend
{
    private readonly HeadlessAutomationHost _host;
    private readonly AutomationCommands _commands;
    private readonly Func<string?, HeadlessAutomationHost> _renderHost;
    private readonly Func<(GraphicsDevice Device, string Name)>? _device;

    /// <param name="host">The session host (owned: disposed with the backend).</param>
    /// <param name="policy">The launch-flag policy.</param>
    /// <param name="renderHost">Creates the private host a render draws on, given the session's RF install.</param>
    /// <param name="device">The GPU device factory (null = the default device order).</param>
    /// <param name="sessionId">The <c>ai.session</c> tag value of this server process.</param>
    public HeadlessBackend(
        HeadlessAutomationHost host,
        AutomationPolicy policy,
        Func<string?, HeadlessAutomationHost> renderHost,
        Func<(GraphicsDevice Device, string Name)>? device,
        string sessionId)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _renderHost = renderHost ?? throw new ArgumentNullException(nameof(renderHost));
        _device = device;
        _commands = new AutomationCommands(host) { Policy = policy, SessionId = sessionId };
    }

    public string Mode => _host.Mode;

    public IReadOnlyList<string> WriteRoots => _host.PathPolicy.Roots;

    public string? RfInstallDir => _host.RfInstallDir;

    /// <summary>The session host (tests inspect the document through it).</summary>
    internal HeadlessAutomationHost Host => _host;

    // The headless host runs commands inline; run them off the protocol's message loop so a long build
    // or script never stalls the reading of a cancellation notification.
    public Task<AutomationResponse> ExecuteAsync(AutomationRequest request, IProgress<AutomationProgress>? progress, CancellationToken cancellation) =>
        Task.Run(() => _commands.ExecuteAsync(request, progress, cancellation), CancellationToken.None);

    public Task<AutomationResponse> RenderAsync(AutomationRequest request, CancellationToken cancellation) =>
        Task.Run(() => RenderCoreAsync(request, cancellation), CancellationToken.None);

    private async Task<AutomationResponse> RenderCoreAsync(AutomationRequest request, CancellationToken cancellation)
    {
        try
        {
            var snapshotArgs = new JsonObject();
            if (request.Args["origin"] is JsonNode origin)
            {
                snapshotArgs["origin"] = origin.DeepClone();
            }

            return await RenderOrchestrator.RenderCopyAsync(
                (r, ct) => _commands.ExecuteAsync(r, ct),
                request.Args,
                snapshotArgs,
                _renderHost,
                _device,
                source: null,
                cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AutomationResponse
            {
                Ok = false,
                Command = RenderCommand.Name,
                Error = new AutomationError(AutomationErrorCodes.Canceled, "The render was canceled before it finished."),
            };
        }
        catch (AutomationException ex)
        {
            return new AutomationResponse { Ok = false, Command = RenderCommand.Name, Error = new AutomationError(ex.Code, ex.Message) };
        }
    }

    public void Dispose() => _host.Dispose();
}
