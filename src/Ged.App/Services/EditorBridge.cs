using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation;

namespace Ged.App.Services;

/// <summary>
/// The editor's command-line endpoint (attach mode): a per-user named pipe served
/// while Settings ▸ "Allow command-line tools and AI agents to control the open level" is on. Each
/// request is executed by the shared <see cref="AutomationCommands"/> against the
/// <see cref="EditorAutomationHost"/>, which marshals onto the UI thread — so a command behaves exactly
/// as the same action inside the editor. A second editor instance finds the name taken, logs once, and
/// runs without a bridge.
/// <para>
/// The editor's own policy applies on top of whatever a client sends: a request from an AI agent
/// (<c>origin: "mcp"</c>) may discard or overwrite work only while Settings ▸ "Allow destructive AI actions" is
/// on — the MCP server must also have been started with <c>--allow-destructive</c> to ask at all.
/// </para>
/// </summary>
internal sealed class EditorBridge : IAsyncDisposable
{
    private readonly AutomationCommands _commands;
    private readonly Action<string> _log;
    private readonly Func<bool> _allowAiDestructive;
    private readonly BridgeServer _server;

    /// <param name="host">The editor host the commands run against.</param>
    /// <param name="log">One-line activity sink (the Log panel / status bar); called on worker threads.</param>
    /// <param name="pipeName">The pipe name (default: the per-user name glacier-cli connects to).</param>
    /// <param name="allowAiDestructive">Reads the "Allow destructive AI actions" setting (live, per request); null = off.</param>
    public EditorBridge(IAutomationHost host, Action<string> log, string? pipeName = null, Func<bool>? allowAiDestructive = null)
    {
        _commands = new AutomationCommands(host);
        _log = log ?? (_ => { });
        _allowAiDestructive = allowAiDestructive ?? (() => false);
        _server = new BridgeServer(pipeName ?? BridgeProtocol.PipeName(), (request, progress, ct) => ExecuteAsync(request, progress, ct));
        _server.ClientConnectionChanged += connected =>
            _log(connected ? "A command-line tool connected." : "The command-line tool disconnected.");
        _server.Faulted += ex => CrashHandler.LogNonFatal("automation-bridge", ex);
    }

    /// <summary>True while serving.</summary>
    public bool IsRunning => _server.IsRunning;

    /// <summary>Starts serving; false (logged once) when another editor already serves the name.</summary>
    public bool Start()
    {
        if (_server.TryStart(out string? reason))
        {
            return true;
        }

        _log($"Command-line connections are off for this window: {reason}.");
        return false;
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    /// <summary>Executes a request directly (tests; the pipe path calls the same handler).</summary>
    internal Task<AutomationResponse> ExecuteAsync(AutomationRequest request, CancellationToken ct = default) =>
        ExecuteAsync(request, progress: null, ct);

    /// <summary>
    /// Executes one bridge request under the editor's policy (<see cref="ApplyEditorPolicy"/>), forwarding a
    /// build's progress when the client asked for it; a <c>hello</c> also reports the destructive setting.
    /// </summary>
    internal async Task<AutomationResponse> ExecuteAsync(AutomationRequest request, IProgress<AutomationProgress>? progress, CancellationToken ct)
    {
        bool allowAi = _allowAiDestructive();
        ApplyEditorPolicy(request, allowAi);
        AutomationResponse response = await _commands.ExecuteAsync(request, progress, ct).ConfigureAwait(false);
        if (request.Command == "hello" && response.Ok && response.Result is JsonObject hello)
        {
            hello["destructiveAutomation"] = allowAi;
        }

        return response;
    }

    /// <summary>
    /// The editor wins over the client: an AI agent's request (<c>origin: "mcp"</c>) loses its
    /// <c>allowDestructive</c> unless the editor's "Allow destructive AI actions" setting is on. Command-line
    /// requests (a person's own <c>--allow-destructive</c>) are unaffected.
    /// </summary>
    internal static void ApplyEditorPolicy(AutomationRequest request, bool allowAiDestructive)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool fromAgent = request.Args["origin"] is JsonValue v && v.TryGetValue(out string? origin) && origin == "mcp";
        if (fromAgent && !allowAiDestructive && request.Args.ContainsKey("allowDestructive"))
        {
            request.Args["allowDestructive"] = false;
        }
    }
}
