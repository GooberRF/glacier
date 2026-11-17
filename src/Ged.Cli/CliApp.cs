using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation;
using Ged.Automation.Rendering;
using Ged.Core;

namespace Ged.Cli;

/// <summary>Process exit codes (stable; documented in <c>glacier-cli help</c> and the user help).</summary>
internal static class ExitCodes
{
    /// <summary>The command succeeded.</summary>
    public const int Ok = 0;

    /// <summary>The command ran and failed (script error, build errors, save aborted, …).</summary>
    public const int Failed = 1;

    /// <summary>Bad command line (unknown verb/option, missing argument).</summary>
    public const int Usage = 2;

    /// <summary>--attach: no running Glacier editor accepted the connection.</summary>
    public const int NoEditor = 3;

    /// <summary>A file could not be read or written, or a path is outside the allowed folders.</summary>
    public const int Io = 4;

    /// <summary>holes --fail-on-holes: the level leaks.</summary>
    public const int HolesFound = 5;

    /// <summary>grade --fail-on review|fail: the verdict is at least that bad.</summary>
    public const int GradeFailed = 6;
}

/// <summary>Test seams for <see cref="CliApp"/>.</summary>
internal sealed class CliEnvironment
{
    /// <summary>The bridge pipe name (default: the per-user name the editor serves).</summary>
    public string PipeName { get; init; } = BridgeProtocol.PipeName();

    /// <summary>
    /// The editor settings file to read (default: the editor's settings file as
    /// <see cref="EditorSettingsLocator.FindDefault"/> finds it — beside glacier-cli, the profile copy, or a
    /// source checkout's editor build output).
    /// </summary>
    public string? SettingsPath { get; init; }

    /// <summary>Reads the editor settings: <see cref="SettingsPath"/> when set, else the located settings file.</summary>
    public EditorSettingsSnapshot ReadSettings() => EditorSettingsReader.Read(SettingsPath ?? EditorSettingsLocator.FindDefault());

    /// <summary>Environment variable lookup (default: the process environment).</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>How long to wait for the editor to accept a connection.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The time new levels are stamped with (default: now).</summary>
    public Func<DateTime>? Clock { get; init; }

    /// <summary>
    /// The repository checkout glacier-cli runs from (research/ configuration, default corpus output), or null. Default:
    /// the folder holding Glacier.sln above the executable, else above the working directory.
    /// </summary>
    public Func<string?> RepoRoot { get; init; } = () =>
        Ged.Automation.Analysis.CorpusConfig.FindRepoRoot(AppContext.BaseDirectory)
        ?? Ged.Automation.Analysis.CorpusConfig.FindRepoRoot(Environment.CurrentDirectory);

    /// <summary>The GPU device <c>render</c> draws with (default: <see cref="RenderDevices.Create"/>).</summary>
    public Func<(Ged.Rendering.Graphics.GraphicsDevice Device, string Name)>? RenderDevice { get; init; }
}

/// <summary>
/// <c>glacier-cli</c>: runs one automation command headlessly against a level file, or — with
/// <c>--attach</c> — sends it to the running editor, which executes it on the open level (Script Log,
/// viewport, and one undo step per run, exactly as if run inside the editor). Both paths execute the
/// same <see cref="AutomationCommands"/> handlers, so the JSON output is identical.
/// </summary>
internal static class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CliEnvironment? env = null)
    {
        env ??= new CliEnvironment();
        CliArguments a;
        try
        {
            a = CliArguments.Parse(args);
        }
        catch (UsageException ex)
        {
            stderr.WriteLine($"glacier-cli: {ex.Message}");
            stderr.WriteLine("Run 'glacier-cli help' for usage.");
            return ExitCodes.Usage;
        }

        if (a.Version)
        {
            stdout.WriteLine($"glacier-cli {AutomationCommands.ProductVersion}");
            return ExitCodes.Ok;
        }

        if (a.Verb is null || a.Verb == "help" || a.Help)
        {
            string? topic = a.Verb == "help" ? a.Positionals.FirstOrDefault() : a.Verb;
            stdout.Write(CliHelp.Text(topic));
            return a.Verb is null && !a.Help ? ExitCodes.Usage : ExitCodes.Ok;
        }

        try
        {
            if (a.Verb == "modules")
            {
                return ModulesCli.Run(a, stdout, stderr, env);
            }

            if (a.ModuleOptions.Count > 0)
            {
                throw new UsageException($"{string.Join(", ", a.ModuleOptions.Keys)} only apply to 'modules'.", a.Verb);
            }

            if (a.Verb == "api")
            {
                return Api(a, stdout);
            }

            if (a.Verb == "corpus")
            {
                return await CorpusCli.RunAsync(a, stdout, stderr, env);
            }

            if (a.Verb == "mcp")
            {
                return await Mcp.McpCommand.RunAsync(a, stderr, env);
            }

            if (a.Verb == "ref")
            {
                return await RefCli.RunAsync(a, stdout, stderr, env);
            }

            if (a.Verb == RecreateCli.Verb)
            {
                return RecreateCli.Run(a, stdout);
            }

            if (a.Verb == AuditCli.Verb)
            {
                return AuditCli.Run(a, stdout);
            }

            if (a.RecreateOptions.Count > 0)
            {
                throw new UsageException($"{string.Join(", ", a.RecreateOptions.Keys)} only apply to '{RecreateCli.Verb}'.", a.Verb);
            }

            AutomationRequest request = BuildRequest(a, out IReadOnlyList<string> explicitPaths, out bool needsAssets);
            AutomationResponse response = request.Command == RenderCommand.Name
                ? await RenderAsync(a, request, explicitPaths, env, stderr)
                : a.Attach
                ? await SendToEditorAsync(a, request, env, stderr)
                : await RunHeadlessAsync(a, request, explicitPaths, needsAssets, env, stderr);
            return Report(a, response, stdout, stderr);
        }
        catch (UsageException ex)
        {
            stderr.WriteLine($"glacier-cli {a.Verb}: {ex.Message}");
            stderr.Write(CliHelp.Usage(ex.Verb ?? a.Verb));
            return ExitCodes.Usage;
        }
        catch (EditorUnavailableException ex)
        {
            if (a.Json)
            {
                stdout.WriteLine(AutomationJson.Serialize(new AutomationResponse
                {
                    Ok = false,
                    Command = a.Verb ?? string.Empty,
                    Error = new AutomationError("no_editor", ex.Message),
                }, indented: true));
            }

            stderr.WriteLine($"glacier-cli: {ex.Message}");
            return ExitCodes.NoEditor;
        }
        catch (AutomationException ex)
        {
            return Report(a, new AutomationResponse
            {
                Ok = false,
                Command = a.Verb ?? string.Empty,
                Error = new AutomationError(ex.Code, ex.Message),
            }, stdout, stderr);
        }
    }

    // ---- api ----------------------------------------------------------------------------------

    /// <summary>
    /// <c>api [--markdown|--lua] [--member &lt;name&gt;]</c>: the scripting reference generated from THIS
    /// build's API (no level, no editor, no RF install needed), so an install always documents itself.
    /// </summary>
    private static int Api(CliArguments a, TextWriter stdout)
    {
        const string Verb = "api";
        Max(a, 0);
        if (a.Attach)
        {
            throw new UsageException("'api' does not use --attach: the reference comes from this glacier-cli's own build.", Verb);
        }

        if (a.Markdown && a.Lua)
        {
            throw new UsageException("--markdown and --lua are alternatives: give one.", Verb);
        }

        if (a.Member is not null && a.Lua)
        {
            throw new UsageException("--member prints one entry as text; drop --lua.", Verb);
        }

        if (a.Out is not null || a.Save || a.DryRun || a.AllowDestructive || a.Build || a.Light || a.Sealed || a.Steps is not null
            || a.Detail is not null || a.NoTag || a.NoBuild || a.FailOnHoles || a.RenderOptions.Count > 0)
        {
            throw new UsageException("'api' takes only --markdown, --lua, --member <name> and --json.", Verb);
        }

        string format;
        string text;
        string? name = null;
        if (a.Member is { } member)
        {
            try
            {
                (name, text) = ApiReferenceText.Member(member);
            }
            catch (AutomationException ex)
            {
                throw new UsageException(ex.Message, Verb);
            }
            format = "text";
        }
        else if (a.Lua)
        {
            text = Ged.Core.Scripting.ScriptApiReference.GenerateLuaStub();
            format = "lua";
        }
        else
        {
            text = ApiReferenceText.Markdown();
            format = "markdown";
        }

        if (a.Json)
        {
            var result = new JsonObject { ["format"] = format, ["apiVersion"] = Ged.Core.Scripting.ScriptApiV1.Version };
            if (name is not null)
            {
                result["name"] = name;
            }

            result["text"] = text;
            stdout.WriteLine(AutomationJson.Serialize(new AutomationResponse { Ok = true, Command = Verb, Result = result }, indented: true));
        }
        else
        {
            stdout.Write(text.EndsWith('\n') ? text : text + Environment.NewLine);
        }

        return ExitCodes.Ok;
    }

    // ---- request construction -----------------------------------------------------------------

    /// <summary>Maps the command line to the shared request (absolute paths, so the editor resolves them identically).</summary>
    private static AutomationRequest BuildRequest(CliArguments a, out IReadOnlyList<string> explicitPaths, out bool needsAssets)
    {
        string verb = a.Verb!;
        var args = new JsonObject();
        var paths = new List<string>();
        needsAssets = true;

        string? Full(string? p) => p is null ? null : PathAllowList.Normalize(p);
        void Level(string? level)
        {
            if (level is not null)
            {
                string full = Full(level)!;
                args["level"] = full;
                paths.Add(full);
            }
        }

        void Out(string? path)
        {
            if (path is not null)
            {
                string full = Full(path)!;
                args["out"] = full;
                paths.Add(full);
            }
        }

        string? SaveTarget() =>
            a.Out is not null && a.SavePath is not null && !string.Equals(Full(a.Out), Full(a.SavePath), StringComparison.Ordinal)
                ? throw new UsageException("Give the save path once: --out <path> or --save=<path>.", verb)
                : a.SavePath ?? a.Out;

        if (a.AllowImports.Count > 0 && (verb != "run" || a.Attach))
        {
            throw new UsageException(a.Attach
                ? "--allow-import is for headless runs only; the editor bridge never accepts extra file paths."
                : "--allow-import only applies to 'run'.", verb);
        }

        if (a.RefOptions.Count > 0)
        {
            throw new UsageException($"{a.RefOptions.Keys.First()} only applies to 'ref'.", verb);
        }

        if (a.DryRun && verb != "run")
        {
            throw new UsageException("--dry-run only applies to 'run'.", verb);
        }

        if (a.Budget is not null && verb != "run")
        {
            throw new UsageException("--budget only applies to 'run' (the Lua instruction budget).", verb);
        }

        switch (verb)
        {
            case "hello":
                Max(a, 0);
                needsAssets = false;
                break;

            case "info":
                Max(a, 1);
                Level(RequireLevel(a, 0));
                needsAssets = false;
                break;

            case "open":
                Max(a, 1);
                if (!a.Attach)
                {
                    throw new UsageException("'open' opens a level in the running editor; use it with --attach.", verb);
                }

                args["path"] = Full(a.Positionals.FirstOrDefault() ?? throw new UsageException("open needs a level file.", verb));
                break;

            case "new":
            {
                // `new <path.rfl>` is the same as `new --out <path.rfl>`.
                Max(a, 1);
                string? target = a.Out;
                if (a.Positionals.Count == 1)
                {
                    target = target is null || string.Equals(Full(target), Full(a.Positionals[0]), StringComparison.Ordinal)
                        ? a.Positionals[0]
                        : throw new UsageException("Give the new level's path once: new <path.rfl> or new --out <path.rfl>.", verb);
                }

                if (!a.Attach && target is null)
                {
                    throw new UsageException("new needs a path: new <path.rfl> (or --out <path.rfl>), where to write the new level.", verb);
                }

                Out(target);
                needsAssets = false;
                break;
            }

            case "run":
            {
                Max(a, 2);
                string script = a.Positionals.FirstOrDefault() ?? throw new UsageException("run needs a Lua script file.", verb);
                string source;
                Dictionary<string, string> modules;
                try
                {
                    source = File.ReadAllText(script);

                    // The modules it imports from its own folder travel with the run (sources, not paths), so an
                    // attached editor reads nothing from disk for them.
                    modules = Ged.Core.Scripting.ScriptModuleResolver.CollectLocal(script, source);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new AutomationException(AutomationErrorCodes.Io, $"Could not read script '{script}': {ex.Message}");
                }

                args["script"] = source;
                args["name"] = Path.GetFileName(script);
                if (modules.Count > 0)
                {
                    var m = new JsonObject();
                    foreach ((string key, string text) in modules.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        m[key] = text;
                    }

                    args["modules"] = m;
                }
                Level(a.Positionals.Count > 1 ? a.Positionals[1] : null);
                if (a.DryRun)
                {
                    args["dryRun"] = true;
                }

                if (a.TimeoutSeconds is double t)
                {
                    args["timeoutSeconds"] = t;
                }

                if (a.Budget is long budget)
                {
                    args["instructionBudget"] = budget;
                }

                if (a.NoTag)
                {
                    args["tag"] = false;
                }

                if (a.Label is { } label)
                {
                    args["label"] = label;
                }

                if (a.NoBuild)
                {
                    args["noBuild"] = true;
                }

                if (a.Build || a.Light)
                {
                    if (a.DryRun)
                    {
                        throw new UsageException("--build / --light cannot be combined with --dry-run (a dry run changes nothing to build).", verb);
                    }

                    args["build"] = true;
                    if (a.Light)
                    {
                        args["light"] = true;
                    }

                    if (a.NoShadows)
                    {
                        args["shadows"] = false;
                    }
                }
                else if (a.NoShadows)
                {
                    throw new UsageException("--no-shadows needs --light.", verb);
                }

                string? runSave = SaveTarget();
                if (a.DryRun && (runSave is not null || a.Save))
                {
                    string given = a.Out is not null ? "--out" : a.SavePath is not null ? "--save=<path>" : "--save";
                    throw new UsageException(
                        $"{given} cannot be combined with --dry-run: a dry run rolls every change back, so there is nothing to save. " +
                        $"Drop {given} to preview the run, or drop --dry-run to run it and save.", verb);
                }

                SaveArgs(a, args, runSave, Out);
                if (!a.Attach && a.Positionals.Count < 2 && runSave is null && !a.DryRun)
                {
                    throw new UsageException("Without a level, run builds a new level: pass --out <path.rfl> to keep it (or --dry-run).", verb);
                }

                break;
            }

            case "build":
            case "light":
                Max(a, 1);
                Level(RequireLevel(a, 0));
                if (a.Sealed || verb == "light")
                {
                    args["sealed"] = true;
                }

                if (a.Light || verb == "light")
                {
                    args["light"] = true;
                }

                if (a.NoShadows)
                {
                    args["shadows"] = false;
                }

                if (a.Pipeline is { } pipeline)
                {
                    args["pipeline"] = pipeline.ToLowerInvariant() is "alpine" or "legacy"
                        ? pipeline.ToLowerInvariant()
                        : throw new UsageException($"--pipeline needs alpine or legacy (got '{pipeline}').", verb);
                }

                if (a.Density is int density)
                {
                    args["density"] = density;
                }
                if (a.Codec is { } codec)
                {
                    args["codec"] = codec.ToLowerInvariant() is "bc7" or "raw"
                        ? codec.ToLowerInvariant()
                        : throw new UsageException($"--codec needs bc7 or raw (got '{codec}').", verb);
                }

                SaveArgs(a, args, SaveTarget(), Out);
                break;

            case "holes":
            case "lint":
                Max(a, 1);
                Level(RequireLevel(a, 0));
                if (a.MinSeverity is { } minSeverity)
                {
                    if (verb != "lint")
                    {
                        throw new UsageException("--min-severity only applies to 'lint'.", verb);
                    }

                    args["minSeverity"] = minSeverity.ToLowerInvariant() is "info" or "warning" or "error"
                        ? minSeverity.ToLowerInvariant()
                        : throw new UsageException($"--min-severity needs info, warning, or error (got '{minSeverity}').", verb);
                }

                break;

            case "summary":
                Max(a, 1);
                Level(RequireLevel(a, 0));
                string detail = (a.Detail ?? "normal").ToLowerInvariant();
                try
                {
                    AutomationCommands.ParseDetail(detail);
                }
                catch (AutomationException ex)
                {
                    throw new UsageException(ex.Message, verb);
                }

                args["detail"] = detail;
                break;

            case "analyze":
                Max(a, 1);
                Level(RequireLevel(a, 0));
                if (a.TimeoutSeconds is double analyzeTimeout)
                {
                    args["timeoutSeconds"] = analyzeTimeout;
                }

                if (a.Out is not null || a.Save)
                {
                    throw new UsageException("analyze prints the fingerprint (use --json); it writes no files.", verb);
                }

                break;

            case "norms":
                Max(a, 1);
                needsAssets = false;
                if (a.Out is not null || a.Save)
                {
                    throw new UsageException("norms prints the shipped corpus norms (use --json); it writes no files.", verb);
                }

                if (a.Compare)
                {
                    args["compare"] = true;
                    needsAssets = true; // item categories / stock textures when an install is mounted
                    if (a.Metric is not null || a.Bucket is not null)
                    {
                        throw new UsageException("--compare lists every default-driving metric: drop --metric / --gametype.", verb);
                    }

                    if (a.Positionals.Count > 0)
                    {
                        Level(a.Positionals[0]);
                    }
                    else if (!a.Attach)
                    {
                        throw new UsageException("norms --compare needs a level file (or --attach to compare the editor's level).", verb);
                    }

                    if (a.TimeoutSeconds is double normsTimeout)
                    {
                        args["timeoutSeconds"] = normsTimeout;
                    }
                }
                else
                {
                    if (a.Positionals.Count > 0)
                    {
                        throw new UsageException($"Unexpected argument '{a.Positionals[0]}' (a level is only read with --compare).", verb);
                    }

                    if (a.Bucket is not null && a.Metric is null)
                    {
                        throw new UsageException("--gametype / --bucket needs --metric.", verb);
                    }

                    if (a.Metric is { } metric)
                    {
                        args["metric"] = metric;
                    }

                    if (a.Bucket is { } bucket)
                    {
                        args["bucket"] = bucket;
                    }
                }

                break;

            case "spatial":
                if (a.Positionals.Count == 0)
                {
                    throw new UsageException($"spatial needs an operation: {string.Join(", ", Ged.Core.Spatial.SpatialQueries.Ops.Select(o => o.Op))}.", verb);
                }

                Max(a, 2);
                Level(RequireLevel(a, 1));
                SpatialRequest(a, verb, args);
                break;

            case "grade":
                Max(a, 1);
                Level(RequireLevel(a, 0));
                SpatialRequest(a, verb, args);
                break;

            case "save":
                Max(a, 1);
                Level(RequireLevel(a, 0));
                Out(SaveTarget());
                if (a.NoBuild)
                {
                    args["noBuild"] = true;
                }

                break;

            case "render":
                Max(a, 1);
                Level(RequireLevel(a, 0));
                if (a.Out is null)
                {
                    throw new UsageException("render needs --out <image.png>.", verb);
                }

                if (a.Save || a.Light || a.NoShadows)
                {
                    throw new UsageException("render does not save or light the level (drop --save / --light / --no-shadows).", verb);
                }

                args["out"] = Full(a.Out);
                paths.Add(Full(a.Out)!);
                RenderArgs(a, args, verb);
                break;

            case "checkpoint":
                Max(a, 1);
                RequireAttach(a, verb, "remembers the editor's open level between commands");
                args["name"] = a.Positionals.FirstOrDefault() ?? throw new UsageException("checkpoint needs a name.", verb);
                needsAssets = false;
                break;

            case "diff":
                Max(a, 0);
                RequireAttach(a, verb, "compares the editor's open level with a checkpoint taken earlier");
                args["since"] = a.Since ?? throw new UsageException("diff needs --since <checkpoint>.", verb);
                if (a.MaxChanges is int maxChanges)
                {
                    args["maxChanges"] = (double)maxChanges;
                }

                needsAssets = false;
                break;

            case "inspect":
            {
                Max(a, 2);
                string uidText = a.Positionals.FirstOrDefault() ?? throw new UsageException("inspect needs a UID.", verb);
                if (!int.TryParse(uidText, NumberStyles.None, CultureInfo.InvariantCulture, out int uid))
                {
                    throw new UsageException($"inspect needs a UID (a whole number), got '{uidText}'.", verb);
                }

                args["uid"] = (double)uid;
                Level(a.Positionals.Count > 1 ? a.Positionals[1] : a.Attach ? null
                    : throw new UsageException("inspect needs a level file after the UID (or --attach to use the level open in the editor).", verb));
                break;
            }

            case "query":
                Max(a, 1);
                Level(RequireLevel(a, 0));
                if (a.What is { } what)
                {
                    args["what"] = what.ToLowerInvariant();
                }

                if (a.Kind is { } qkind)
                {
                    args["kind"] = qkind;
                }

                if (a.Class is { } qclass)
                {
                    args["class"] = qclass;
                }

                if (a.Name is { } qname)
                {
                    args["name"] = qname;
                }

                if (a.Tag is { } tag)
                {
                    args["tag"] = tag;
                }

                if (a.Limit is int qlimit)
                {
                    args["limit"] = (double)qlimit;
                }

                if (a.Offset is int offset)
                {
                    args["offset"] = (double)offset;
                }

                if (a.RenderOptions.TryGetValue("--region", out string? qregion))
                {
                    try
                    {
                        args["region"] = new JsonArray(RenderSpec.ParseList(qregion, "--region", 6).Select(f => (JsonNode?)JsonValue.Create((double)f)).ToArray());
                    }
                    catch (AutomationException ex)
                    {
                        throw new UsageException(ex.Message, verb);
                    }
                }

                break;

            case "catalog":
            {
                Max(a, 1);
                string op = (a.Positionals.FirstOrDefault() ?? throw new UsageException("catalog needs kinds, classes, describe or textures.", verb)).ToLowerInvariant();
                if (op is not ("kinds" or "classes" or "describe" or "textures"))
                {
                    throw new UsageException($"Unknown catalog list '{op}' (use kinds, classes, describe or textures).", verb);
                }

                args["op"] = op;
                if (a.Kind is { } ckind)
                {
                    args["kind"] = ckind;
                }

                if (a.Class is { } cclass)
                {
                    args["class"] = cclass;
                }

                if (a.Filter is { } filter)
                {
                    args["filter"] = filter;
                }

                if (a.Limit is int climit)
                {
                    args["limit"] = (double)climit;
                }

                if (a.StockOnly)
                {
                    args["stockOnly"] = true;
                }

                break;
            }

            case "redo":
                Max(a, 0);
                RequireAttach(a, verb, "redoes command-line runs in the running editor (a headless command keeps no history between invocations)");
                if (a.Steps is int redoSteps)
                {
                    args["steps"] = (double)redoSteps;
                }

                needsAssets = false;
                break;

            case "undo":
                Max(a, 0);
                if (!a.Attach)
                {
                    throw new UsageException(
                        "'undo' undoes command-line runs in the running editor; use it with --attach (a headless command keeps no history between invocations).",
                        verb);
                }

                if (a.Steps is int steps)
                {
                    args["steps"] = (double)steps;
                }

                needsAssets = false;
                break;

            default:
                throw new UsageException($"Unknown command '{verb}'.", verb: "");
        }

        if (a.Jobs is not null || (a.Limit is not null && verb is not ("query" or "catalog")) || a.Roots.Count > 0 || a.SnapshotPath is not null || a.NoCollapse || a.NoSnapshot || a.NoInstall
            || a.DebugRender is not null)
        {
            throw new UsageException("--jobs / --root / --snapshot / --debug-render / --no-collapse / --no-snapshot / --no-install only apply to 'corpus' (--limit also to 'query' and 'catalog').", verb);
        }

        if (a.Promote)
        {
            throw new UsageException("--promote only applies to 'corpus aggregate'.", verb);
        }

        if ((a.Metric is not null || a.Compare || (a.Bucket is not null && verb != "grade")) && verb != "norms")
        {
            throw new UsageException("--metric / --gametype / --bucket / --compare only apply to 'norms' (--gametype also to 'grade').", verb);
        }

        if (a.SpatialOptions.Count > 0 && verb is not ("spatial" or "grade"))
        {
            throw new UsageException($"{a.SpatialOptions.Keys.First()} only applies to 'spatial' and 'grade'.", verb);
        }

        if (a.Steps is not null && verb is not ("undo" or "redo"))
        {
            throw new UsageException("--steps only applies to 'undo' and 'redo'.", verb);
        }

        if ((a.Since is not null || a.MaxChanges is not null) && verb != "diff")
        {
            throw new UsageException("--since / --max-changes only apply to 'diff'.", verb);
        }

        if ((a.What is not null || a.Name is not null || a.Tag is not null || a.Offset is not null) && verb != "query")
        {
            throw new UsageException("--what / --name / --tag / --offset only apply to 'query'.", verb);
        }

        if ((a.Kind is not null && verb is not ("query" or "catalog" or "spatial")) || (a.Class is not null && verb is not ("query" or "catalog" or "grade")))
        {
            throw new UsageException("--kind / --class only apply to 'query' and 'catalog' (--kind also to 'spatial graph', --class to 'grade').", verb);
        }

        if ((a.Filter is not null || a.StockOnly) && verb != "catalog")
        {
            throw new UsageException("--filter / --stock-only only apply to 'catalog'.", verb);
        }

        if (a.Label is not null && verb != "run")
        {
            throw new UsageException("--label only applies to 'run'.", verb);
        }

        if (a.Headless || a.ReadOnly || a.AllowWrites.Count > 0 || a.Scratch is not null || a.Tools is not null || a.DenyTools is not null
            || a.MaxImage is not null || a.LogFile is not null)
        {
            throw new UsageException("--headless / --read-only / --allow-write / --scratch / --tools / --deny-tools / --max-image / --log only apply to 'mcp'.", verb);
        }

        if (a.Build && verb != "run")
        {
            throw new UsageException("--build only applies to 'run' (use the 'build' command on a level).", verb);
        }

        if (a.Markdown || a.Lua || a.Member is not null)
        {
            throw new UsageException("--markdown / --lua / --member only apply to 'api'.", verb);
        }

        if (a.AllowDestructive)
        {
            args["allowDestructive"] = true;
        }

        if (a.FailOnHoles && verb != "holes")
        {
            throw new UsageException("--fail-on-holes only applies to 'holes'.", verb);
        }

        if (a.Detail is not null && verb != "summary")
        {
            throw new UsageException("--detail only applies to 'summary'.", verb);
        }

        if (a.NoTag && verb != "run")
        {
            throw new UsageException("--no-tag only applies to 'run'.", verb);
        }

        if (a.NoBuild && verb is not ("run" or "save"))
        {
            throw new UsageException("--no-build only applies to 'run' and 'save'.", verb);
        }

        if (a.RenderOptions.Count > 0 && verb != "render" && !(verb is "query" or "spatial" && a.RenderOptions.Count == 1 && a.RenderOptions.ContainsKey("--region")))
        {
            throw new UsageException($"{a.RenderOptions.Keys.First(k => verb != "query" || k != "--region")} only applies to 'render'.", verb);
        }

        explicitPaths = paths;
        return new AutomationRequest(verb == "light" ? "build" : verb, args); // 'light' = build --sealed --light

        // A level argument is required headless; with --attach it defaults to the editor's open level.
        string? RequireLevel(CliArguments c, int index) =>
            c.Positionals.Count > index ? c.Positionals[index]
            : c.Attach ? null
            : throw new UsageException($"{verb} needs a level file (or --attach to use the level open in the editor).", verb);
    }

    /// <summary>Maps the spatial / grade flags onto the request (the command validates the values).</summary>
    private static void SpatialRequest(CliArguments a, string verb, JsonObject args)
    {
        Dictionary<string, string> o = a.SpatialOptions;
        string[] spatialOnly = { "--from", "--dir", "--a", "--b", "--pos", "--profile", "--k", "--scale", "--around", "--radius", "--max-nodes", "--player-class", "--max" };
        string[] gradeOnly = { "--players", "--max-locations", "--metrics", "--fail-on" };
        foreach (string flag in verb == "grade" ? spatialOnly : gradeOnly)
        {
            if (o.ContainsKey(flag))
            {
                throw new UsageException($"{flag} only applies to '{(verb == "grade" ? "spatial" : "grade")}'.", verb);
            }
        }

        JsonArray Point(string flag, string value, int count)
        {
            try
            {
                return new JsonArray(RenderSpec.ParseList(value, flag, count).Select(f => (JsonNode?)JsonValue.Create((double)f)).ToArray());
            }
            catch (AutomationException ex)
            {
                throw new UsageException(ex.Message, verb);
            }
        }

        double Number(string flag, string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)
                ? d
                : throw new UsageException($"{flag} needs a number (got '{value}').", verb);

        if (verb == "spatial")
        {
            args["op"] = a.Positionals[0];
            foreach ((string flag, string arg) in new[] { ("--from", "from"), ("--dir", "dir"), ("--a", "a"), ("--b", "b"), ("--pos", "pos"), ("--around", "around") })
            {
                if (o.TryGetValue(flag, out string? v))
                {
                    args[arg] = Point(flag, v, 3);
                }
            }

            foreach ((string flag, string arg) in new[] { ("--k", "k"), ("--scale", "scale"), ("--radius", "radius"), ("--max-nodes", "maxNodes"), ("--max", "max") })
            {
                if (o.TryGetValue(flag, out string? v))
                {
                    args[arg] = Number(flag, v);
                }
            }

            if (o.TryGetValue("--profile", out string? profile))
            {
                args["profile"] = profile;
            }

            if (o.TryGetValue("--player-class", out string? pc))
            {
                args["playerClass"] = pc;
            }

            if (a.Kind is { } kind)
            {
                args["kind"] = kind;
            }

            if (a.RenderOptions.TryGetValue("--region", out string? region))
            {
                args["region"] = Point("--region", region, 6);
            }
        }
        else
        {
            if (a.Bucket is { } gametype)
            {
                args["gametype"] = gametype;
            }

            if (a.Class is { } cls)
            {
                args["class"] = cls;
            }

            if (o.TryGetValue("--players", out string? players))
            {
                args["players"] = Number("--players", players);
            }

            if (o.TryGetValue("--max-locations", out string? ml))
            {
                args["maxLocations"] = Number("--max-locations", ml);
            }

            if (o.ContainsKey("--metrics"))
            {
                args["metrics"] = true;
            }

            if (o.TryGetValue("--fail-on", out string? failOn) && failOn is not ("review" or "fail"))
            {
                throw new UsageException($"--fail-on needs review or fail (got '{failOn}').", verb);
            }
        }

        if (o.TryGetValue("--fresh", out string? fresh))
        {
            args["build"] = fresh.ToLowerInvariant() is "auto" or "preview" or "sealed" or "none"
                ? fresh.ToLowerInvariant()
                : throw new UsageException($"--fresh needs auto, preview, sealed or none (got '{fresh}').", verb);
        }

        if (o.TryGetValue("--max-samples", out string? samples))
        {
            args["maxSamples"] = Number("--max-samples", samples);
        }

        if (a.TimeoutSeconds is double t)
        {
            args["timeoutSeconds"] = t;
        }

        if (a.Out is not null || a.Save)
        {
            throw new UsageException($"{verb} prints its result (use --json); it writes no files.", verb);
        }
    }

    /// <summary>Maps the render flags onto the request (validated by <see cref="RenderSpec.FromArgs"/>).</summary>
    private static void RenderArgs(CliArguments a, JsonObject args, string verb)
    {
        Dictionary<string, string> o = a.RenderOptions;
        string? Opt(string name) => o.TryGetValue(name, out string? v) ? v : null;
        JsonArray List(string name, string value, int count) =>
            new(Wrap(() => RenderSpec.ParseList(value, name, count)).Select(f => (JsonNode?)JsonValue.Create((double)f)).ToArray());
        double Number(string name, string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)
                ? d
                : throw new UsageException($"{name} needs a number (got '{value}').", verb);
        T Wrap<T>(Func<T> f)
        {
            try
            {
                return f();
            }
            catch (AutomationException ex)
            {
                throw new UsageException(ex.Message, verb);
            }
        }

        string? view = Opt("--view");
        if (o.ContainsKey("--plan"))
        {
            if (view is not null && !string.Equals(view, "plan", StringComparison.OrdinalIgnoreCase))
            {
                throw new UsageException("--plan and --view are alternatives: give one.", verb);
            }

            view = "plan";
        }

        if (view is not null)
        {
            args["view"] = view;
        }

        foreach ((string flag, string key) in new[] { ("--y", "y"), ("--thickness", "thickness"), ("--below", "below"), ("--fov", "fov"), ("--hfov", "hfov") })
        {
            if (Opt(flag) is { } v)
            {
                args[key] = Number(flag, v);
            }
        }

        if (Opt("--eye-height") is { } eyeHeight)
        {
            args["eyeHeight"] = Wrap(() => Ged.Automation.Reference.ReferenceCamera.ParseHeight(eyeHeight)); // metres or eye / crouch / jump
        }

        foreach ((string flag, string key) in new[] { ("--room", "room"), ("--uid", "uid") })
        {
            if (Opt(flag) is { } v)
            {
                double d = Number(flag, v);
                args[key] = d;
            }
        }

        if (Opt("--camera") is { } cam)
        {
            args["camera"] = List("--camera", cam, 3);
        }

        if (Opt("--look") is { } look)
        {
            args["look"] = List("--look", look, 3);
        }

        if (Opt("--region") is { } region)
        {
            args["region"] = List("--region", region, 6);
        }

        if (Opt("--mode") is { } mode)
        {
            args["mode"] = mode;
        }

        if (Opt("--size") is { } size)
        {
            string[] parts = size.ToLowerInvariant().Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h))
            {
                throw new UsageException($"--size needs WIDTHxHEIGHT, e.g. 1024x768 (got '{size}').", verb);
            }

            args["width"] = (double)w;
            args["height"] = (double)h;
        }

        if (o.ContainsKey("--no-overlays"))
        {
            if (o.ContainsKey("--overlays"))
            {
                throw new UsageException("--overlays and --no-overlays are alternatives: give one.", verb);
            }

            args["overlays"] = "none";
        }
        else if (Opt("--overlays") is { } overlays)
        {
            args["overlays"] = overlays;
        }

        if (o.ContainsKey("--icons") && o.ContainsKey("--no-icons"))
        {
            throw new UsageException("--icons and --no-icons are alternatives: give one.", verb);
        }

        if (o.ContainsKey("--icons") || o.ContainsKey("--no-icons"))
        {
            args["icons"] = o.ContainsKey("--icons");
        }

        if (a.Sealed && o.ContainsKey("--rebuild"))
        {
            throw new UsageException("--sealed and --rebuild are alternatives: give one.", verb);
        }

        if (a.Sealed)
        {
            args["build"] = "sealed";
        }
        else if (o.ContainsKey("--rebuild"))
        {
            args["build"] = "preview";
        }

        // Validate everything now, so a bad flag is a usage error before any build or GPU work.
        Wrap(() => RenderSpec.FromArgs(args));
    }

    private static void RequireAttach(CliArguments a, string verb, string why)
    {
        if (!a.Attach)
        {
            throw new UsageException($"'{verb}' {why}; use it with --attach.", verb);
        }
    }

    private static void SaveArgs(CliArguments a, JsonObject args, string? target, Action<string?> setOut)
    {
        if (target is not null)
        {
            setOut(target);
        }
        else if (a.Save)
        {
            args["save"] = true;
        }
    }

    private static void Max(CliArguments a, int count)
    {
        if (a.Positionals.Count > count)
        {
            throw new UsageException($"Unexpected argument '{a.Positionals[count]}'.", a.Verb);
        }
    }

    // ---- execution ----------------------------------------------------------------------------

    private static async Task<AutomationResponse> RunHeadlessAsync(
        CliArguments a,
        AutomationRequest request,
        IReadOnlyList<string> explicitPaths,
        bool needsAssets,
        CliEnvironment env,
        TextWriter stderr)
    {
        using HeadlessAutomationHost host = CreateHeadlessHost(a, explicitPaths, needsAssets, preferredRfDir: null, env, stderr);

        // Headless sessions start empty: 'run' without a level and 'new' build from the template;
        // everything else opens its level argument (the handler's 'level').
        if (request.Command is "run" && !request.Args.ContainsKey("level"))
        {
            await host.NewAsync(CancellationToken.None);
        }

        var commands = new AutomationCommands(host);
        return await commands.ExecuteAsync(request);
    }

    /// <summary>
    /// A window-less session with the user's editor settings, the resolved RF install mounted (when
    /// <paramref name="needsAssets"/>; <paramref name="preferredRfDir"/> is tried before the
    /// environment/settings), and the command line's explicit paths allow-listed.
    /// </summary>
    private static HeadlessAutomationHost CreateHeadlessHost(
        CliArguments a,
        IReadOnlyList<string> explicitPaths,
        bool needsAssets,
        string? preferredRfDir,
        CliEnvironment env,
        TextWriter stderr)
    {
        EditorSettingsSnapshot settings = env.ReadSettings();
        if (a.RfDir is not null && !Directory.Exists(a.RfDir))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"--rf-dir: directory not found: {a.RfDir}");
        }

        string? rfDir = null;
        if (needsAssets)
        {
            if (a.RfDir is null && preferredRfDir is not null && Directory.Exists(preferredRfDir))
            {
                rfDir = preferredRfDir;
                if (a.Verbose)
                {
                    stderr.WriteLine($"glacier-cli: RF install: {preferredRfDir} (the editor's install)");
                }
            }
            else
            {
                RfInstallChoice choice = RfInstallResolver.ResolveWithSource(a.RfDir, settings, env.GetEnvironmentVariable);
                rfDir = choice.Dir;
                if (a.Verbose)
                {
                    stderr.WriteLine($"glacier-cli: RF install: {choice.Describe()}");
                }
            }
        }
        else if (a.Verbose)
        {
            stderr.WriteLine($"glacier-cli: RF install: not needed for '{a.Verb}'");
        }

        var host = new HeadlessAutomationHost(settings, rfInstallDir: null, env.Clock);
        if (rfDir is not null)
        {
            try
            {
                host.Mount(rfDir);
            }
            catch (Exception ex) when (a.RfDir is null)
            {
                // A stale configured install must not block a headless run: continue unmounted.
                stderr.WriteLine($"glacier-cli: warning: could not mount the RF install '{rfDir}' ({ex.Message}); textures and game tables are unavailable.");
            }
        }

        foreach (string p in explicitPaths)
        {
            host.PathPolicy.AllowExplicit(p);
        }

        // --allow-import files are readable by level.import_mesh, never writable.
        foreach (string p in a.AllowImports)
        {
            host.PathPolicy.AllowExplicitRead(p);
        }

        return host;
    }

    /// <summary>
    /// <c>render</c> always draws in this process: headless on the level file, or — with --attach — on a
    /// private copy of the editor's open level fetched with <c>snapshot</c> (so the editor never needs a
    /// second GPU device, and the render can never touch the user's document).
    /// </summary>
    internal static async Task<AutomationResponse> RenderAsync(
        CliArguments a,
        AutomationRequest request,
        IReadOnlyList<string> explicitPaths,
        CliEnvironment env,
        TextWriter stderr)
    {
        if (!a.Attach)
        {
            using HeadlessAutomationHost headless = CreateHeadlessHost(a, explicitPaths, needsAssets: true, preferredRfDir: null, env, stderr);
            return await RenderCommand.ExecuteAsync(headless, request, env.RenderDevice);
        }

        using BridgeClient client = await ConnectAsync(env);
        using var cts = a.TimeoutSeconds is double t && t > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(t) + TimeSpan.FromSeconds(30))
            : new CancellationTokenSource();
        var snapshotArgs = new JsonObject();
        if (request.Args["level"] is JsonNode level)
        {
            snapshotArgs["level"] = level.DeepClone();
        }

        if (request.Args["allowDestructive"] is JsonNode allow)
        {
            snapshotArgs["allowDestructive"] = allow.DeepClone();
        }

        try
        {
            return await RenderOrchestrator.RenderCopyAsync(
                client.SendAsync,
                request.Args,
                snapshotArgs,
                editorRf => CreateHeadlessHost(a, explicitPaths, needsAssets: true, preferredRfDir: editorRf, env, stderr),
                env.RenderDevice,
                source: "editor",
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            return new AutomationResponse
            {
                Ok = false,
                Command = RenderCommand.Name,
                Error = new AutomationError(AutomationErrorCodes.Internal, "Timed out waiting for the editor's level."),
            };
        }
        catch (IOException ex)
        {
            throw new EditorUnavailableException($"The connection to the editor was lost: {ex.Message}");
        }
    }

    private static async Task<BridgeClient> ConnectAsync(CliEnvironment env) =>
        await BridgeClient.TryConnectAsync(env.PipeName, env.ConnectTimeout)
        ?? throw new EditorUnavailableException(
            "Could not connect to a running Glacier editor. Start Glacier (and check Settings ▸ General ▸ " +
            "\"Allow command-line tools and AI agents to control the open level\"), or run without --attach on a level file.");

    private static async Task<AutomationResponse> SendToEditorAsync(CliArguments a, AutomationRequest request, CliEnvironment env, TextWriter stderr)
    {
        if (a.RfDir is not null)
        {
            stderr.WriteLine("glacier-cli: note: --rf-dir is ignored with --attach (the editor uses its own install).");
        }

        using BridgeClient client = await ConnectAsync(env);
        using var cts = a.TimeoutSeconds is double t && t > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(t) + TimeSpan.FromSeconds(30))
            : new CancellationTokenSource();
        try
        {
            return await client.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return new AutomationResponse
            {
                Ok = false,
                Command = request.Command,
                Error = new AutomationError(AutomationErrorCodes.Internal, "Timed out waiting for the editor's reply (the editor may still finish the command)."),
            };
        }
        catch (IOException ex)
        {
            throw new EditorUnavailableException($"The connection to the editor was lost: {ex.Message}");
        }
    }

    // ---- output -------------------------------------------------------------------------------

    internal static int Report(CliArguments a, AutomationResponse response, TextWriter stdout, TextWriter stderr)
    {
        if (a.Json)
        {
            stdout.WriteLine(AutomationJson.Serialize(response, indented: true));
        }
        else
        {
            CliText.Write(response, stdout, stderr);
        }

        if (!response.Ok)
        {
            return response.Error?.Code switch
            {
                AutomationErrorCodes.Usage or AutomationErrorCodes.UnknownCommand => ExitCodes.Usage,
                AutomationErrorCodes.Io or AutomationErrorCodes.PathNotAllowed => ExitCodes.Io,
                _ => ExitCodes.Failed,
            };
        }

        if (a.FailOnHoles && response.Command == "holes" && response.Result?["count"]?.GetValue<int>() > 0)
        {
            return ExitCodes.HolesFound;
        }

        if (response.Command == "grade" && a.SpatialOptions.TryGetValue("--fail-on", out string? failOn)
            && response.Result?["verdict"]?.GetValue<string>() is { } verdict
            && (verdict == "FAIL" || (failOn == "review" && verdict == "REVIEW")))
        {
            return ExitCodes.GradeFailed;
        }

        return ExitCodes.Ok;
    }
}

/// <summary>No editor accepted the --attach connection (exit code 3).</summary>
internal sealed class EditorUnavailableException : Exception
{
    public EditorUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>Human-readable rendering of command results (the default; <c>--json</c> prints the raw response).</summary>
internal static class CliText
{
    public static void Write(AutomationResponse r, TextWriter o, TextWriter e)
    {
        JsonNode? x = r.Result;
        if (x is not null)
        {
            switch (r.Command)
            {
                case "hello":
                    o.WriteLine($"Connected to Glacier {S(x, "version")} ({S(x, "mode")}); level: {S(x, "document") ?? (B(x, "hasDocument") ? "untitled (unsaved)" : "none open")}");
                    break;
                case "info":
                case "open":
                case "new":
                    Info(x, o);
                    break;
                case "run":
                    Run(x, o);
                    break;
                case "build":
                    Build(x, o);
                    break;
                case "holes":
                    Holes(x, o);
                    break;
                case "lint":
                    Lint(x, o);
                    break;
                case "summary":
                    Summary(x, o);
                    break;
                case "save":
                    Saved(x, o);
                    break;
                case "undo":
                case "redo":
                    Undo(x, o);
                    break;
                case "render":
                    Render(x, o);
                    break;
                case "analyze":
                    Analyze(x, o);
                    break;
                case "norms":
                    Norms(x, o);
                    break;
                case "grade":
                    Grade(x, o);
                    break;
                case Ged.Automation.Reference.ReferenceCommand.Name:
                    RefCli.Text(x, o);
                    break;
                default:
                    o.WriteLine(x.ToJsonString(AutomationJson.Indented));
                    break;
            }
        }

        if (!r.Ok && r.Error is { } err)
        {
            e.WriteLine($"error ({err.Code}): {err.Message}");
        }
    }

    private static void Grade(JsonNode x, TextWriter o)
    {
        o.WriteLine($"Verdict:  {S(x, "verdict")} - {I(x, "failures_count")} failing check(s){(B(x, "hard_failure") ? ", a hard check failed" : string.Empty)}");
        o.WriteLine($"Level:    {S(x, "level") ?? "(unsaved)"}: {S(x, "gametype")}, class {S(x, "class")}, player class {S(x, "player_class")}, {x["playable_area"]?.ToJsonString()} m2 playable, {I(x, "spawns")} spawns");
        if (x["geometry"] is JsonNode g)
        {
            o.WriteLine($"Geometry: {S(g, "state")} (measured {S(g, "measured")}){(S(g, "note") is { } note ? " - " + note : string.Empty)}");
        }

        foreach (JsonNode? f in x["failures"]?.AsArray() ?? new JsonArray())
        {
            o.WriteLine();
            string values = string.Join(", ", (f!["values"] as JsonObject ?? new JsonObject()).Select(kv => $"{kv.Key} = {kv.Value?.ToJsonString() ?? "n/a"}"));
            o.WriteLine($"{S(f, "id")}{(B(f, "hard") ? " (hard)" : string.Empty)}{(S(f, "class_limits") is { } cl ? $" [{cl} limits]" : string.Empty)}: {S(f, "check")}");
            o.WriteLine($"  measured: {values}");
            o.WriteLine($"  fix:      {S(f, "fix")}");
            foreach (JsonNode? loc in (f["locations"]?.AsArray() ?? new JsonArray()).Take(4))
            {
                string extra = string.Join(", ", loc!.AsObject().Where(kv => kv.Key is not ("kind" or "pos")).Take(4).Select(kv => $"{kv.Key} {kv.Value?.ToJsonString()}"));
                o.WriteLine($"  at {loc["pos"]?.ToJsonString()} ({S(loc, "kind")}{(extra.Length > 0 ? ": " + extra : string.Empty)})");
            }

            if (f["detail"] is JsonObject detail)
            {
                GradeDetail(detail, o);
            }
        }

        o.WriteLine();
        o.WriteLine($"Passed: {string.Join(" ", (x["passed"]?.AsArray() ?? new JsonArray()).Select(p => p!.GetValue<string>()))}");
        if (x["not_applicable"] is JsonArray na && na.Count > 0)
        {
            o.WriteLine($"Not applicable: {string.Join(" ", na.Select(p => p!.GetValue<string>()))}");
        }

        if (x["advisories"] is JsonArray adv && adv.Count > 0)
        {
            o.WriteLine();
            o.WriteLine(S(x, "class") == "payload"
                ? $"Advisory (payload targets for floor and floor per spawn, player class {S(x, "player_class")} exemplar p25-p75 for the rest; not counted in the verdict):"
                : $"Advisory (player class {S(x, "player_class")} targets, exemplar p25-p75; not counted in the verdict):");
            foreach (JsonNode? a in adv)
            {
                JsonArray t = a!["target"]!.AsArray();
                o.WriteLine($"  {S(a, "metric")} = {a["value"]?.ToJsonString() ?? "n/a"}: {S(a, "status")} (target {t[0]!.ToJsonString()}-{t[1]!.ToJsonString()})");
                if (a["detail"] is JsonObject detail && S(a, "status") != "within")
                {
                    GradeDetail(detail, o, "    ");
                }
            }
        }

        if (x["set_pieces"] is JsonObject sp)
        {
            string N(string key, string unit) => sp[key] is JsonNode v ? v.ToJsonString() + unit : "none";
            string bridge = sp["bridge_span"] is null
                ? $"{sp["bridges"]?.ToJsonString() ?? "0"} bridge(s)"
                : $"{sp["bridges"]?.ToJsonString() ?? "0"} bridge(s); landmark bridge span {N("bridge_span", " m")} (gap {N("bridge_gap_width", " m")}, {N("bridge_gap_depth", " m")} deep, {N("bridge_width", " m")} wide), longest span {N("bridge_span_max", " m")}";
            o.WriteLine($"Set pieces: {bridge}; tallest landmark {N("landmark_height_max", " m")}; tallest atrium {N("atrium_height_max", " m")}");
        }
    }

    /// <summary>A grade detail: its summary, then every counted space (largest first) or the floor-height bins.</summary>
    private static void GradeDetail(JsonObject d, TextWriter o, string indent = "  ")
    {
        o.WriteLine($"{indent}detail:   {S(d, "summary")}");
        if (d["spaces"] is JsonArray spaces && spaces.Count > 0)
        {
            var near = new HashSet<int>((d["near_median_ranks"] as JsonArray ?? new JsonArray()).Select(n => n!.GetValue<int>()));
            o.WriteLine($"{indent}spaces (m2, largest first; * = nearest the median):");
            foreach (JsonNode? sp in spaces)
            {
                int rank = sp!["rank"]!.GetValue<int>();
                o.WriteLine($"{indent}  #{rank}{(near.Contains(rank) ? "*" : string.Empty)} {sp["area"]?.ToJsonString()} {S(sp, "side")} at {sp["pos"]?.ToJsonString()} (floor {sp["min"]?.ToJsonString()} to {sp["max"]?.ToJsonString()}){(S(sp, "archetype") is { } arch ? " " + arch : string.Empty)}");
            }

            if (B(d, "truncated"))
            {
                o.WriteLine($"{indent}  ... ({I(d, "counted")} counted; --json lists the first 80)");
            }
        }

        if (d["bins"] is JsonArray bins)
        {
            string Row(JsonNode b) => $"y {b["y_from"]?.ToJsonString()} to {b["y_to"]?.ToJsonString()}: {b["area"]?.ToJsonString()} m2";
            o.WriteLine($"{indent}classes:  {string.Join(", ", bins.Select(b => Row(b!)))}");
            if (d["near_misses"] is JsonArray near && near.Count > 0)
            {
                o.WriteLine($"{indent}short:    {string.Join(", ", near.Select(b => Row(b!)))} (each needs {d["min_area_m2"]?.ToJsonString()} m2)");
            }
        }
    }

    private static void Info(JsonNode x, TextWriter o)
    {
        o.WriteLine($"Level:    {S(x, "name")}{(S(x, "path") is { } p ? $"  ({p})" : "  (not saved yet)")}");
        o.WriteLine($"Format:   RFL v{I(x, "rflVersion")}{(B(x, "alpine") ? " (Alpine)" : string.Empty)}{(B(x, "dirty") ? ", unsaved changes" : string.Empty)}");
        o.WriteLine($"Contents: {I(x, "brushes")} brushes, {I(x, "objects")} objects, {I(x, "links")} links");
        o.WriteLine($"Geometry: {S(x, "geometry")}{(I(x, "rooms") > 0 ? $", {I(x, "rooms")} rooms" : string.Empty)}");
        if (x["notes"] is JsonArray notes)
        {
            foreach (JsonNode? note in notes)
            {
                o.WriteLine($"Note:     {note?.GetValue<string>()}");
            }
        }
    }

    private static void Run(JsonNode x, TextWriter o)
    {
        if (x["log"] is JsonArray log)
        {
            foreach (JsonNode? line in log)
            {
                string level = S(line!, "level") ?? "output";
                o.WriteLine(level == "output" ? S(line!, "message") : $"[{level}] {S(line!, "message")}");
            }
        }

        if (B(x, "success"))
        {
            string what = I(x, "changeCount") == 1 ? "1 change" : $"{I(x, "changeCount")} changes";
            o.WriteLine(B(x, "dryRun")
                ? $"Dry run of {S(x, "script")}: would make {what} (nothing applied)."
                : $"Ran {S(x, "script")}: {what}{(S(x, "undoLabel") is { } u ? $" (undo: \"{u}\")" : string.Empty)}.");
            if (S(x, "returnValue") is { Length: > 0 } rv)
            {
                o.WriteLine($"Returned: {rv}");
            }

            if (x["created"] is JsonArray created && created.Count > 0)
            {
                IEnumerable<string> shown = created.Take(20).Select(u => u!.ToJsonString());
                string more = created.Count > 20 ? $", … {created.Count - 20} more (use --json for all)" : string.Empty;
                string tagged = x["tags"] is JsonObject tags && tags.Count > 0
                    ? $"; tagged {string.Join(", ", tags.Select(kv => $"{kv.Key}={kv.Value?.GetValue<string>()}"))}"
                    : string.Empty;
                o.WriteLine($"Created {created.Count}: UID {string.Join(", ", shown)}{more}{tagged}");
            }
        }

        if (x["build"] is JsonNode build)
        {
            Build(build, o);
        }

        if (x["saved"] is JsonNode saved)
        {
            Saved(saved, o);
        }
    }

    private static void Undo(JsonNode x, TextWriter o)
    {
        if (x["redone"] is JsonArray redone)
        {
            foreach (JsonNode? label in redone)
            {
                o.WriteLine($"Redid \"{label!.GetValue<string>()}\".");
            }
        }

        if (x["undone"] is JsonArray undone)
        {
            foreach (JsonNode? label in undone)
            {
                o.WriteLine($"Undid \"{label!.GetValue<string>()}\".");
            }
        }

        o.WriteLine(S(x, "next") is { } next ? $"Next undo step: \"{next}\"." : "Nothing left to undo.");
    }

    private static void Build(JsonNode x, TextWriter o)
    {
        string kind = B(x, "lit") ? "sealed + lit" : B(x, "sealed") ? "sealed" : "preview (unsealed)";
        o.WriteLine($"Built ({kind}, {S(x, "method")}): {I(x, "rooms")} rooms ({I(x, "subrooms")} detail subrooms), {I(x, "portals")} portals, " +
                    $"{I(x, "faces")} faces, {I(x, "vertices")} vertices, {I(x, "surfaces")} surfaces, {I(x, "lightmapPages")} lightmap pages " +
                    $"in {D(x, "elapsedMs").ToString("0", CultureInfo.InvariantCulture)} ms");
        if (x["repair"] is JsonNode repair)
        {
            o.WriteLine($"Repaired holes: {I(repair, "holesAfterRooms")} after building rooms; " +
                        $"{I(repair, "verticesWelded")} vertices welded, {I(repair, "edgesSplit")} edges split, " +
                        $"{I(repair, "collapsedFacesRemoved")} collapsed faces and {I(repair, "portalStripsRemoved")} portal strips removed, " +
                        $"{I(repair, "portalGapsFilled")} portal gaps and {I(repair, "wallGapsFilled")} wall gaps filled, " +
                        $"{I(repair, "portalPlanesUnified")} portal planes unified, {I(repair, "portalCopiesMoved")} portal copies moved, " +
                        $"{I(repair, "splitRoomsJoined")} split rooms joined, {I(repair, "unplacedPortalFacesRemoved")} unplaced portal faces removed");
        }

        if (x["messages"] is JsonArray messages)
        {
            foreach (JsonNode? m in messages)
            {
                o.WriteLine($"  {S(m!, "severity")}: {S(m!, "text")}{Pos(m!["pos"])}");
            }
        }

        if (x["saved"] is JsonNode saved)
        {
            Saved(saved, o);
        }
    }

    private static void Holes(JsonNode x, TextWriter o)
    {
        if (B(x, "watertight"))
        {
            o.WriteLine("No holes: the sealed build is watertight.");
            return;
        }

        o.WriteLine($"{I(x, "count")} hole(s) (open edges on the sealed build):");
        if (x["positions"] is JsonArray ps)
        {
            int shown = 0;
            foreach (JsonNode? p in ps)
            {
                if (shown++ == 25)
                {
                    o.WriteLine($"  … {I(x, "count") - 25} more (use --json for all)");
                    break;
                }

                o.WriteLine($"  near{Pos(p)}");
            }
        }
    }

    private static void Lint(JsonNode x, TextWriter o)
    {
        o.WriteLine($"Lint: {I(x, "errors")} error(s), {I(x, "warnings")} warning(s), {I(x, "info")} info");
        if (x["findings"] is JsonArray fs)
        {
            foreach (JsonNode? f in fs)
            {
                string uid = f!["uid"] is JsonNode u ? $" [uid {u}]" : string.Empty;
                o.WriteLine($"  {S(f, "severity")} {S(f, "category")}: {S(f, "message")}{uid}{Pos(f["pos"])}");
            }
        }

        if (x["notes"] is JsonArray notes)
        {
            foreach (JsonNode? n in notes)
            {
                o.WriteLine($"  note: {n!.GetValue<string>()}");
            }
        }
    }

    private static void Summary(JsonNode x, TextWriter o)
    {
        JsonNode level = x["level"]!;
        o.WriteLine($"Level:   {S(level, "name")} (RFL v{I(level, "rflVersion")}{(B(level, "alpine") ? ", Alpine" : string.Empty)}{(B(level, "dirty") ? ", unsaved changes" : string.Empty)})");
        if (x["bounds"] is JsonNode bounds)
        {
            o.WriteLine($"Bounds:  {Pos(bounds["min"]).Trim()} .. {Pos(bounds["max"]).Trim()}  size{Pos(bounds["size"])}");
        }

        JsonNode b = x["brushes"]!;
        o.WriteLine($"Brushes: {I(b, "total")} ({I(b, "solid")} solid, {I(b, "air")} air, {I(b, "detail")} detail, {I(b, "portal")} portal, {I(b, "geoable")} geoable)");
        JsonNode objs = x["objects"]!;
        string kinds = objs["byKind"] is JsonObject bk ? string.Join(", ", bk.Select(kv => $"{kv.Value} {kv.Key}")) : string.Empty;
        o.WriteLine($"Objects: {I(objs, "total")}{(kinds.Length > 0 ? $" ({kinds})" : string.Empty)}");
        JsonNode sp = objs["spawns"]!;
        o.WriteLine($"Spawns:  player start {(B(sp, "playerStart") ? "yes" : "no")}, {I(sp, "multiplayer")} multiplayer spawns ({I(sp, "red")} usable by red, {I(sp, "blue")} by blue, {I(sp, "bot")} by bots)");
        JsonNode l = x["links"]!;
        o.WriteLine($"Links:   {I(l, "total")} ({I(l, "broken")} broken), {I(l, "events")} events ({I(l, "unlinkedEvents")} unlinked), {I(l, "triggers")} triggers ({I(l, "triggersWithoutLinks")} without links)");
        JsonNode rooms = x["rooms"]!;
        o.WriteLine(S(rooms, "status") == "not built"
            ? "Rooms:   not built"
            : $"Rooms:   {I(rooms, "count")} ({I(rooms, "main")} main, {I(rooms, "sub")} detail subrooms, {I(rooms, "portals")} portals) — geometry {S(rooms, "status")}");
        JsonNode lint = x["lint"]!;
        o.WriteLine($"Lint:    {I(lint, "errors")} error(s), {I(lint, "warnings")} warning(s), {I(lint, "info")} info");
        string TagKeys(JsonNode block) => block["tagKeys"] is JsonObject tk ? string.Join(", ", tk.Select(kv => $"{kv.Key} {kv.Value}")) : string.Empty;
        string brushTags = TagKeys(b), objectTags = TagKeys(objs);
        if (brushTags.Length > 0 || objectTags.Length > 0)
        {
            o.WriteLine($"Tags:    brushes ({(brushTags.Length > 0 ? brushTags : "none")}); objects ({(objectTags.Length > 0 ? objectTags : "none")})");
        }
        if (S(x, "detail") != "brief")
        {
            o.WriteLine("(Per-brush, per-object and per-room lists are in the --json output.)");
        }
    }

    private static void Render(JsonNode x, TextWriter o)
    {
        string view = S(x, "view") ?? "?";
        string what = view == "plan" && x["plan"] is JsonNode p
            ? string.Create(CultureInfo.InvariantCulture, $"plan at y={D(p, "y"):0.##} (slice {D(p, "bottom"):0.##}..{D(p, "cut"):0.##}, {S(p, "ySource")})")
            : view;
        string scale = S(x, "projection") == "orthographic"
            ? string.Create(CultureInfo.InvariantCulture, $", {D(x, "worldPerPixel"):0.####} m/px")
            : string.Empty;
        o.WriteLine($"Rendered {S(x, "path")} ({I(x, "width")}x{I(x, "height")}, {what}{scale}, mode {S(x, "modeUsed")})");
        if (S(x, "mapping") is { } mapping)
        {
            o.WriteLine($"  {mapping}");
        }

        if (x["notes"] is JsonArray notes)
        {
            foreach (JsonNode? n in notes)
            {
                o.WriteLine($"  note: {n!.GetValue<string>()}");
            }
        }

        if (x["objects"] is JsonArray objs)
        {
            o.WriteLine($"  {objs.Count} object glyph(s) drawn (pixel positions are in the --json output).");
        }
    }

    private static void Norms(JsonNode x, TextWriter o)
    {
        string F(JsonNode? n) => n is JsonValue v && Number(v) is double d ? string.Create(CultureInfo.InvariantCulture, $"{d:0.##}") : "-";
        if (x["comparisons"] is JsonArray rows)
        {
            o.WriteLine($"Compared with the corpus norms (reference: {S(x, "referenceBucket")}; gametype {S(x, "gametype") ?? "unknown"}):");
            foreach (JsonNode? row in rows)
            {
                o.WriteLine($"  {S(row!, "metric"),-20} {F(row!["value"]),8} {S(row!, "unit"),-9} {S(row!, "verdict"),-7} p5-p95 {F(row!["p5"])}-{F(row!["p95"])}  (p50 {F(row!["p50"])}, {S(row!, "bucket")})");
            }

            o.WriteLine($"{I(x, "outOfNorm")} metric(s) outside the p5-p95 range.");
            return;
        }

        if (x["metric"] is JsonObject m)
        {
            o.WriteLine($"{S(m, "id")} ({S(m, "unit")}, reliability {S(m, "reliability")}, use {S(m, "use")}): {S(m, "doc")}");
            o.WriteLine($"Reference bucket: {S(m, "reference") ?? "-"}");
            foreach ((string bucket, JsonNode? b) in (JsonObject)m["buckets"]!)
            {
                o.WriteLine($"  {bucket,-14} n {F(b!["n"]),5}  p5 {F(b["p5"])}  p25 {F(b["p25"])}  p50 {F(b["p50"])}  p75 {F(b["p75"])}  p95 {F(b["p95"])}");
            }

            return;
        }

        o.WriteLine($"Corpus norms {S(x, "generated")} (reference bucket: {S(x, "referenceBucket")}); metric (unit, reliability, use):");
        foreach (JsonNode? row in x["metrics"] as JsonArray ?? new JsonArray())
        {
            o.WriteLine($"  {S(row!, "id"),-26} {S(row!, "unit"),-9} {S(row!, "reliability"),-7} {S(row!, "use"),-10} {S(row!, "doc")}");
        }
    }

    private static void Analyze(JsonNode x, TextWriter o)
    {
        JsonNode id = x["identity"]!;
        string P50(string group, string field) =>
            x[group]?[field] is JsonObject d && d["p50"] is JsonValue v && Number(v) is double p ? string.Create(CultureInfo.InvariantCulture, $"{p:0.##}") : "-";
        string Num(string group, string field) =>
            x[group]?[field] is JsonValue v && Number(v) is double d ? string.Create(CultureInfo.InvariantCulture, $"{d:0.##}") : "-";
        string name = S(id, "name") is { Length: > 0 } n ? n : S(id, "fileName") ?? S(id, "headerName") ?? "?";
        JsonNode? gt = id["gametype"];
        string gametype = gt is null ? "?" : (S(gt, "value") ?? "?") + (B(gt, "conflict") ? ", gametype conflict" : string.Empty);
        o.WriteLine($"Level:     {name} (RFL v{I(id, "rflVersion")}, {gametype}) - {S(x, "status")}");
        if (x["space"] is JsonObject)
        {
            o.WriteLine($"Floor:     {Num("space", "floor_area")} m2, headroom p50 {P50("space", "headroom")} m, clear width p50 {P50("space", "clear_width")} m, open sky {Num("space", "open_sky_fraction")}");
            o.WriteLine($"Corridors: width p50 {P50("corridor", "width")} m, height p50 {P50("corridor", "height")} m, {Num("corridor", "fraction")} of the floor");
            o.WriteLine($"Openings:  {Num("opening", "count")} (width p50 {P50("opening", "width")} m, height p50 {P50("opening", "height")} m); vertical portals {Num("portal", "vertical_count")} (width p50 {P50("portal", "width")} m)");
            o.WriteLine($"Stairs:    {Num("stair", "count")} (riser p50 {P50("stair", "riser")} m, tread p50 {P50("stair", "tread")} m); vertical span {Num("vertical", "span")} m, {Num("vertical", "layers")} floor level(s)");
            o.WriteLine($"Rooms:     {Num("topology", "rooms")} main, {Num("topology", "portals")} portal links, {Num("topology", "loops")} loop(s), {Num("topology", "dead_ends")} dead end(s)");
        }
        else if (x["status"] is not null && S(x, "status") is "ok" or "ok_partial")
        {
            o.WriteLine("Geometry:  none compiled (object metrics only)");
        }

        if (x["lights"] is JsonObject)
        {
            o.WriteLine($"Objects:   {Num("lights", "count")} lights, {Num("spawns", "count")} respawn points, {Num("items", "count")} items, {Num("events", "count")} events, {Num("events", "links")} links");
        }

        if (S(x, "message") is { } message)
        {
            o.WriteLine($"Note:      {message}");
        }

        if (x["skipped"] is JsonObject skipped && skipped.Count > 0)
        {
            o.WriteLine("Skipped:   " + string.Join(", ", skipped.Select(kv => $"{kv.Key} ({kv.Value})")));
        }

        o.WriteLine("(The full fingerprint is in the --json output.)");
    }

    private static double? Number(JsonValue v) =>
        v.TryGetValue(out double d) ? d : v.TryGetValue(out int i) ? i : v.TryGetValue(out long l) ? l : v.TryGetValue(out uint u) ? u : null;

    private static void Saved(JsonNode x, TextWriter o)
    {
        o.WriteLine($"Saved {S(x, "path")} [Alpine v{I(x, "rflVersion")}]");
        if (x["upgradeNotes"] is JsonArray notes)
        {
            foreach (JsonNode? note in notes)
            {
                o.WriteLine($"  {note?.GetValue<string>()}"); // the save log (upgrade notes, dropped Alpine lightmaps)
            }
        }

        if (S(x, "notice") is { } n)
        {
            o.WriteLine($"  {n}");
        }

        if (S(x, "warning") is { } w)
        {
            o.WriteLine(w);
        }
    }

    private static string? S(JsonNode x, string name) => x[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static int I(JsonNode x, string name) => x[name] is JsonValue v && v.TryGetValue(out int i) ? i : 0;

    private static double D(JsonNode x, string name) => x[name] is JsonValue v && v.TryGetValue(out double d) ? d : 0;

    private static bool B(JsonNode x, string name) => x[name] is JsonValue v && v.TryGetValue(out bool b) && b;

    private static string Pos(JsonNode? p) => p is JsonArray a && a.Count == 3
        ? string.Create(CultureInfo.InvariantCulture, $" ({a[0]!.GetValue<double>():0.###}, {a[1]!.GetValue<double>():0.###}, {a[2]!.GetValue<double>():0.###})")
        : string.Empty;
}
