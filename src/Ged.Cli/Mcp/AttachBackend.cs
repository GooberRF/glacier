using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation;
using Ged.Automation.Rendering;
using Ged.Core;
using Ged.Rendering.Graphics;

namespace Ged.Cli.Mcp;

/// <summary>
/// Tool calls run in the RUNNING EDITOR over the bridge (<see cref="BridgeClient"/>): the designer watches every
/// change land, each script run is one undo step labelled "AI: …", and the editor's own <c>AutomationCommands</c>
/// execute exactly what the headless session would. Renders never draw in the editor: the open level is copied out
/// with <c>snapshot</c> into a private headless host in this process (<see cref="RenderOrchestrator"/>).
/// <para>
/// Permissions: the launch flags decide what this server asks for (read-only is enforced here; destructive
/// actions are requested only with <c>--allow-destructive</c>, and only from an editor that reports its own
/// destructive setting), and the editor applies its setting on top and wins.
/// </para>
/// <para>
/// Connection: one bridge connection, (re)established on demand. No editor ⇒ <c>editor_unavailable</c> (never
/// connected yet) or <c>editor_disconnected</c> (the connection was lost); every call retries, so restarting the
/// editor resumes the session — with a note that the open level may have changed. Never falls back to headless.
/// </para>
/// </summary>
internal sealed class AttachBackend : IMcpBackend
{
    private readonly Func<CancellationToken, Task<BridgeClient?>> _connect;
    private readonly AutomationPolicy _policy;
    private readonly Func<string?, HeadlessAutomationHost> _renderHost;
    private readonly Func<(GraphicsDevice Device, string Name)>? _device;
    private readonly TextWriter _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private BridgeClient? _client;
    private bool _everConnected;
    private string? _notice;
    private JsonObject? _hello;

    /// <param name="connect">Opens a connection to the editor's pipe, or returns null when none answers.</param>
    /// <param name="policy">The launch-flag policy.</param>
    /// <param name="renderHost">Creates the private host a render draws on, given the editor's RF install.</param>
    /// <param name="device">The GPU device factory (null = the default device order).</param>
    /// <param name="log">Diagnostics (standard error or --log).</param>
    /// <param name="connected">An already-connected client whose <c>hello</c> succeeded (the startup probe), or null.</param>
    /// <param name="hello">That client's <c>hello</c> result.</param>
    public AttachBackend(
        Func<CancellationToken, Task<BridgeClient?>> connect,
        AutomationPolicy policy,
        Func<string?, HeadlessAutomationHost> renderHost,
        Func<(GraphicsDevice Device, string Name)>? device,
        TextWriter log,
        BridgeClient? connected = null,
        JsonObject? hello = null)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _renderHost = renderHost ?? throw new ArgumentNullException(nameof(renderHost));
        _device = device;
        _log = log ?? TextWriter.Null;
        if (connected is not null)
        {
            _client = connected;
            _everConnected = true;
            _hello = hello;
        }
    }

    /// <summary>How to make the editor reachable (part of the connection errors).</summary>
    public const string HowToConnect =
        "Start Glacier with Settings ▸ General ▸ \"Allow command-line tools and AI agents to control the open level\" " +
        "switched on (the default); only one tool can be connected to an editor at a time.";

    public string Mode => "attach";

    /// <summary>True while a bridge connection is open.</summary>
    public bool IsConnected => _client is { IsBroken: false };

    /// <summary>
    /// The editor's write rules (it applies them itself; bridge requests never add paths): the open level's
    /// folder, the RF install's <c>user_maps</c>, and the scripts library.
    /// </summary>
    public IReadOnlyList<string> WriteRoots
    {
        get
        {
            var roots = new List<string>();
            if (Str(_hello, "document") is { Length: > 0 } doc && Path.GetDirectoryName(doc) is { Length: > 0 } dir)
            {
                roots.Add(dir);
            }

            if (RfInstallDir is { Length: > 0 } rf)
            {
                roots.Add(Path.Combine(rf, "user_maps"));
            }

            roots.Add(AppPaths.ScriptsDirectory);
            return roots;
        }
    }

    public string? RfInstallDir => Str(_hello, "rfInstallDir");

    /// <summary>Destructive actions need the editor's setting as well: false until an editor reported it on.</summary>
    public bool HostAllowsDestructive => _hello?["destructiveAutomation"] is JsonValue v && v.TryGetValue(out bool on) && on;

    public string? TakeNotice() => Interlocked.Exchange(ref _notice, null);

    public JsonObject Describe() => new()
    {
        ["connected"] = IsConnected,
        ["version"] = Str(_hello, "version"),
        ["capabilities"] = new JsonArray((_client?.Capabilities ?? (IReadOnlySet<string>)new HashSet<string>())
            .OrderBy(c => c, StringComparer.Ordinal).Select(c => (JsonNode?)c).ToArray()),
        ["destructiveSetting"] = _hello?["destructiveAutomation"]?.DeepClone(),
        ["note"] = IsConnected
            ? "Tools act on the level open in the editor; the designer sees each change and can undo it."
            : "Not connected to the editor right now. " + HowToConnect + " The next call reconnects.",
    };

    public async Task<AutomationResponse> ExecuteAsync(AutomationRequest request, IProgress<AutomationProgress>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            CheckReadOnly(request);
            AutomationRequest prepared = Prepare(request);
            AutomationResponse response = await SendAsync(prepared, progress, cancellation).ConfigureAwait(false);
            return Explain(response);
        }
        catch (AutomationException ex)
        {
            return Fail(request.Command, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail(request.Command, AutomationErrorCodes.Canceled,
                "The call was canceled before the editor answered; check the editor's undo history (Edit ▸ Undo) for its outcome.");
        }
    }

    public async Task<AutomationResponse> RenderAsync(AutomationRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var snapshotArgs = new JsonObject();
            if (request.Args["origin"] is JsonNode origin)
            {
                snapshotArgs["origin"] = origin.DeepClone();
            }

            return await RenderOrchestrator.RenderCopyAsync(
                (r, ct) => SendAsync(r, null, ct),
                request.Args,
                snapshotArgs,
                _renderHost,
                _device,
                source: "editor",
                cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Fail(RenderCommand.Name, AutomationErrorCodes.Canceled, "The render was canceled before it finished.");
        }
        catch (AutomationException ex)
        {
            return Fail(RenderCommand.Name, ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Connects now if not connected (the startup attempt of <c>--attach</c>); false when no editor answered.
    /// </summary>
    public async Task<bool> TryConnectAsync(CancellationToken cancellation)
    {
        await _lock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellation).ConfigureAwait(false);
            return true;
        }
        catch (AutomationException)
        {
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- the connection -----------------------------------------------------------------------------

    private async Task<AutomationResponse> SendAsync(AutomationRequest request, IProgress<AutomationProgress>? progress, CancellationToken cancellation)
    {
        await _lock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            BridgeClient client = await EnsureConnectedAsync(cancellation).ConfigureAwait(false);
            AutomationResponse response;
            try
            {
                response = await client.SendAsync(request, progress, cancellation).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                Drop($"the editor connection closed during '{request.Command}' ({ex.Message})");
                throw new AutomationException(AutomationErrorCodes.EditorDisconnected,
                    "The connection to the editor closed during the call (the editor was closed or restarted). " +
                    "Whether the call took effect is unknown: after reconnecting, check session_info and the undo history. " + HowToConnect);
            }
            finally
            {
                if (client.IsBroken && ReferenceEquals(client, _client))
                {
                    Drop("the editor connection was abandoned mid-reply");
                }
            }

            if (request.Command == "hello" && response.Ok && response.Result is JsonObject hello)
            {
                _hello = (JsonObject)hello.DeepClone();
            }

            return response;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The open connection, or a new one (with its <c>hello</c>); throws <c>editor_unavailable</c> / <c>editor_disconnected</c>.</summary>
    private async Task<BridgeClient> EnsureConnectedAsync(CancellationToken cancellation)
    {
        if (_client is { IsBroken: false } open)
        {
            return open;
        }

        Drop(null);
        BridgeClient? client = await _connect(cancellation).ConfigureAwait(false);
        AutomationResponse? hello = null;
        if (client is not null)
        {
            try
            {
                using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                hello = await client.HelloAsync(helloTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or AutomationException or System.Text.Json.JsonException)
            {
                hello = null;
            }

            if (hello is not { Ok: true, Result: JsonObject })
            {
                client.Dispose();
                client = null;
            }
        }

        if (client is null)
        {
            cancellation.ThrowIfCancellationRequested();
            throw _everConnected
                ? new AutomationException(AutomationErrorCodes.EditorDisconnected,
                    "The connection to the editor was lost (it was closed or restarted) and no editor is accepting connections now. " +
                    HowToConnect + " This server stays attached to the editor and reconnects on the next call.")
                : new AutomationException(AutomationErrorCodes.EditorUnavailable,
                    "No running Glacier editor accepted the connection. " + HowToConnect +
                    " This server stays attached to the editor (it never switches to a headless session) and retries on every call.");
        }

        _hello = (JsonObject)hello!.Result!.DeepClone();
        if (_everConnected)
        {
            _notice = "Reconnected to the editor: the open level and its undo history may differ from before (the editor was closed or restarted). Call session_info / level_summary before editing.";
            Log($"reconnected to the editor ({Str(_hello, "version")}).");
        }
        else
        {
            Log($"connected to the editor ({Str(_hello, "version")}).");
        }

        _everConnected = true;
        _client = client;
        return client;
    }

    private void Drop(string? why)
    {
        if (_client is { } old)
        {
            _client = null;
            old.Dispose();
            if (why is not null)
            {
                Log(why + "; the next call reconnects.");
            }
        }
    }

    // ---- policy -------------------------------------------------------------------------------------

    /// <summary>Read-only sessions never send a mutating command (the tool list hides them too).</summary>
    private void CheckReadOnly(AutomationRequest request)
    {
        bool dryRun = request.Args["dryRun"] is JsonValue d && d.TryGetValue(out bool dry) && dry;
        if (_policy.ReadOnly && AutomationPolicy.MutatingCommands.Contains(request.Command) && !(request.Command == "run" && dryRun))
        {
            throw new AutomationException(AutomationErrorCodes.ToolNotAllowed, $"This session is read-only; '{request.Command}' changes the level.");
        }

        if (_policy.AllowedCommands is { } allowed && !allowed.Contains(request.Command))
        {
            throw new AutomationException(AutomationErrorCodes.ToolNotAllowed, $"'{request.Command}' is not enabled for this session.");
        }
    }

    /// <summary>
    /// The request as sent: never the caller's own <c>allowDestructive</c>; this server's flag instead, and only to
    /// an editor that reports its destructive setting (an editor without the setting never receives the request
    /// to discard or overwrite, so both opt-ins always hold).
    /// </summary>
    private AutomationRequest Prepare(AutomationRequest request)
    {
        var args = (JsonObject)request.Args.DeepClone();
        args.Remove("allowDestructive");
        if (_policy.AllowDestructive && !_policy.ReadOnly && _hello?["destructiveAutomation"] is not null)
        {
            args["allowDestructive"] = true;
        }

        return new AutomationRequest(request.Command, args);
    }

    /// <summary>A destructive refusal names both opt-ins (the editor's message speaks of command-line flags).</summary>
    private AutomationResponse Explain(AutomationResponse response)
    {
        if (response.Error is not { Code: AutomationErrorCodes.DestructiveNotAllowed } error)
        {
            return response;
        }

        string why = !_policy.AllowDestructive
            ? "this server was not started with --allow-destructive"
            : _hello?["destructiveAutomation"] is null
                ? "this editor version does not offer destructive AI actions"
                : "the editor's Settings ▸ General ▸ \"Allow destructive AI actions\" is off";
        string first = error.Message.Split(". ", 2)[0].TrimEnd('.');
        return new AutomationResponse
        {
            Ok = false,
            Command = response.Command,
            Result = response.Result,
            Error = new AutomationError(error.Code,
                $"{first}. Discarding or overwriting work in the editor needs both the editor setting \"Allow destructive AI actions\" " +
                $"and a server started with --allow-destructive; {why}. Save first (save_level), or ask the designer."),
        };
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static string? Str(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static AutomationResponse Fail(string command, string code, string message) => new()
    {
        Ok = false,
        Command = command,
        Error = new AutomationError(code, message),
    };

    private void Log(string line)
    {
        try
        {
            lock (_log)
            {
                _log.WriteLine($"glacier-cli mcp: {line}");
                _log.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Diagnostics are best-effort.
        }
    }

    public void Dispose()
    {
        Drop(null);
        _lock.Dispose();
    }
}
