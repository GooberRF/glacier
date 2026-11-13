using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation;
using Ged.Automation.Rendering;
using Ged.Core;

namespace Ged.Cli.Mcp;

/// <summary>
/// <c>glacier-cli mcp</c>: a Model Context Protocol server on standard input/output. Standard output carries
/// only protocol messages; every diagnostic goes to standard error or <c>--log</c>.
/// </summary>
internal static class McpCommand
{
    public static async Task<int> RunAsync(CliArguments a, TextWriter stderr, CliEnvironment env)
    {
        McpLaunchOptions options = McpLaunchOptions.From(a);
        TextWriter log = stderr;
        StreamWriter? logFile = null;
        if (options.LogFile is { } logPath)
        {
            try
            {
                logFile = new StreamWriter(logPath, append: true) { AutoFlush = true };
                log = logFile;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AutomationException(AutomationErrorCodes.Io, $"--log: could not open '{logPath}': {ex.Message}");
            }
        }

        bool ownScratch = options.Scratch is null;
        string scratch = options.Scratch ?? Path.Combine(Path.GetTempPath(), $"glacier-mcp-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");
        try
        {
            Directory.CreateDirectory(scratch);
            using McpToolService service = await CreateServiceAsync(options, scratch, log, env).ConfigureAwait(false);
            string where = service.Backend is AttachBackend attach
                ? attach.IsConnected ? "attached to the running editor" : "attached to the editor (none answered yet; every call retries)"
                : "headless";
            log.WriteLine($"glacier-cli mcp: {AutomationCommands.ProductVersion} serving {service.Tools.Count} tools ({where}); scratch folder {scratch}");
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                await new McpServerHost(service).RunStdioAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Ctrl+C: a clean stop.
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }

            log.WriteLine("glacier-cli mcp: the client disconnected; stopping.");
            return ExitCodes.Ok;
        }
        finally
        {
            if (ownScratch)
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best-effort cleanup of the temporary folder
                }
            }

            logFile?.Dispose();
        }
    }

    /// <summary>
    /// The tool service for the launch mode, chosen once (MCP-SERVER-SPEC §3.2): <c>--headless</c> → a headless
    /// session; <c>--attach</c> → the running editor (the server starts even when none answers, and every call
    /// retries); neither → a one-second <c>hello</c> probe: the editor when it answers, else headless.
    /// </summary>
    internal static async Task<McpToolService> CreateServiceAsync(McpLaunchOptions options, string scratch, TextWriter log, CliEnvironment env,
        Func<(Ged.Rendering.Graphics.GraphicsDevice Device, string Name)>? device = null, CancellationToken cancellation = default)
    {
        if (options.Mode == McpMode.Headless)
        {
            return CreateService(options, scratch, log, env, device);
        }

        HostFactories hosts = HostFactory(options, scratch, log, env);
        AutomationPolicy policy = Policy(options);
        Task<BridgeClient?> Connect(CancellationToken ct) => BridgeClient.TryConnectAsync(env.PipeName, env.ConnectTimeout, ct);
        if (options.Mode == McpMode.Attach)
        {
            var attach = new AttachBackend(Connect, policy, hosts.NewRenderHost, device ?? env.RenderDevice, log);
            if (!await attach.TryConnectAsync(cancellation).ConfigureAwait(false))
            {
                log.WriteLine("glacier-cli mcp: no editor accepted the connection yet; tool calls report editor_unavailable and retry until one does.");
            }

            NoteHeadlessOnlyOptions(options, log);
            return new McpToolService(attach, options, scratch, log) { RenderMounts = hosts.Mounts };
        }

        // Default: probe once. The choice is fixed for the process: an editor started later does not switch a
        // headless session (the documents would differ).
        TimeSpan probe = env.ConnectTimeout < ProbeTimeout ? env.ConnectTimeout : ProbeTimeout;
        BridgeClient? client = await BridgeClient.TryConnectAsync(env.PipeName, probe, cancellation).ConfigureAwait(false);
        if (client is not null)
        {
            AutomationResponse? hello;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(ProbeTimeout);
                hello = await client.HelloAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or AutomationException or JsonException)
            {
                hello = null;
            }

            if (hello is { Ok: true, Result: JsonObject result })
            {
                NoteHeadlessOnlyOptions(options, log);
                var attach = new AttachBackend(Connect, policy, hosts.NewRenderHost, device ?? env.RenderDevice, log, client, (JsonObject)result.DeepClone());
                return new McpToolService(attach, options, scratch, log) { RenderMounts = hosts.Mounts };
            }

            client.Dispose();
        }

        log.WriteLine("glacier-cli mcp: no editor answered the startup probe; running a headless session.");
        hosts.Mounts.Dispose();
        return CreateService(options, scratch, log, env, device);
    }

    /// <summary>How long the default mode waits for the editor at startup (connect, then <c>hello</c>).</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The tool service over a headless session: the user's editor settings and RF install (as every
    /// glacier-cli command resolves them), the launch-flag policy, the scratch folder and --allow-write
    /// folders as write roots, --allow-import files readable by scripts.
    /// </summary>
    internal static McpToolService CreateService(McpLaunchOptions options, string scratch, TextWriter log, CliEnvironment env,
        Func<(Ged.Rendering.Graphics.GraphicsDevice Device, string Name)>? device = null)
    {
        HostFactories hosts = HostFactory(options, scratch, log, env);
        var backend = new HeadlessBackend(hosts.NewHost(hosts.RfDir), Policy(options), hosts.NewRenderHost, device ?? env.RenderDevice, AutomationCommands.NewSessionId());
        return new McpToolService(backend, options, scratch, log) { RenderMounts = hosts.Mounts };
    }

    private static AutomationPolicy Policy(McpLaunchOptions options) =>
        new() { AllowDestructive = options.AllowDestructive && !options.ReadOnly, ReadOnly = options.ReadOnly };

    /// <summary>
    /// Creates headless hosts (the session's, or a render's private copy) with the user's settings, the RF install
    /// (the one given, else the one resolved from --rf-dir / GED_RF_DIR / the editor's settings), and the write and
    /// import allowances. Also returns the resolved install.
    /// </summary>
    private static HostFactories HostFactory(McpLaunchOptions options, string scratch, TextWriter log, CliEnvironment env)
    {
        EditorSettingsSnapshot settings = env.ReadSettings();
        if (options.RfDir is not null && !Directory.Exists(options.RfDir))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"--rf-dir: directory not found: {options.RfDir}");
        }

        RfInstallChoice choice = RfInstallResolver.ResolveWithSource(options.RfDir, settings, env.GetEnvironmentVariable);
        string? rfDir = choice.Dir;
        log.WriteLine($"glacier-cli mcp: RF install: {choice.Describe()}");
        var mounts = new RenderMountCache();

        // The session's own host owns its mount; a render's private host borrows the install mount shared by
        // every render of this server (fresh document per render; only the packfiles stay open).
        HeadlessAutomationHost NewHost(string? rf) => Create(rf, shared: false);
        HeadlessAutomationHost NewRenderHost(string? rf) => Create(rf, shared: true);

        HeadlessAutomationHost Create(string? rf, bool shared)
        {
            rf ??= rfDir;
            var host = new HeadlessAutomationHost(settings, rfInstallDir: null, env.Clock);
            if (rf is not null)
            {
                try
                {
                    if (!shared)
                    {
                        host.Mount(rf);
                    }
                    else if (!Directory.Exists(rf))
                    {
                        throw new AutomationException(AutomationErrorCodes.Io, $"RF install directory not found: {rf}");
                    }
                    else
                    {
                        RenderMountLease lease = mounts.Acquire(rf);
                        host.MountShared(lease.Assets, lease.RfInstallDir, lease.Traits, lease);
                    }
                }
                catch (Exception ex) when (options.RfDir is null)
                {
                    log.WriteLine($"glacier-cli mcp: warning: could not mount the RF install '{rf}' ({ex.Message}); textures and game tables are unavailable.");
                }
            }

            host.PathPolicy.AddRoot(scratch);
            foreach (string dir in options.AllowWrite)
            {
                host.PathPolicy.AddRoot(dir);
            }

            foreach (string file in options.AllowImport)
            {
                host.PathPolicy.AllowExplicitRead(file);
            }

            return host;
        }

        return new HostFactories(NewHost, NewRenderHost, mounts, rfDir);
    }

    /// <summary>The host factories of one server: the session's host, a render's private host (sharing <paramref name="Mounts"/>), the resolved install.</summary>
    private sealed record HostFactories(
        Func<string?, HeadlessAutomationHost> NewHost,
        Func<string?, HeadlessAutomationHost> NewRenderHost,
        RenderMountCache Mounts,
        string? RfDir);

    private static void NoteHeadlessOnlyOptions(McpLaunchOptions options, TextWriter log)
    {
        if (options.AllowWrite.Count > 0 || options.AllowImport.Count > 0)
        {
            log.WriteLine("glacier-cli mcp: note: --allow-write and --allow-import apply to headless sessions; attached, the editor's own write rules apply.");
        }
    }
}
