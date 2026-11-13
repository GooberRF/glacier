using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;

namespace Ged.Automation;

/// <summary>
/// The automation command set — one handler per command, shared verbatim by the headless
/// <c>glacier-cli</c> and the editor's command-line bridge, so the same request produces the same
/// result in both. Each handler takes its arguments from the request's <c>args</c> object and returns
/// a serializable result (the types in AutomationResults.cs). Every command runs on the
/// host's document thread (<see cref="IAutomationHost.RunOnDocumentThreadAsync{T}"/>).
/// <para>
/// Common argument: <c>level</c> (string, optional) — a level file to open first when it is not
/// already the open document (opening discards unsaved changes, which needs <c>allowDestructive</c>).
/// </para>
/// </summary>
public sealed partial class AutomationCommands
{
    /// <summary>The command names, in help order.</summary>
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "hello", "info", "new", "open", "run", "build", "holes", "lint", "summary", "analyze", "norms", "save", "undo", "redo",
        "checkpoint", "diff", "inspect", "query", "catalog", "api", "snapshot", "spatial", "grade",
    };

    /// <summary>
    /// The largest raw chunk one <c>snapshot</c> response carries. Base64 inflates it by 4/3 (8 MB),
    /// keeping every response well inside <see cref="BridgeProtocol.MaxMessageBytes"/>; larger levels
    /// stream as several chunks.
    /// </summary>
    public const int SnapshotChunkBytes = 6 * 1024 * 1024;

    /// <summary>The provenance tag keys a <c>run</c> stamps on everything it creates (unless <c>tag: false</c>).</summary>
    public const string ScriptTagKey = "ai.script";

    /// <summary>See <see cref="ScriptTagKey"/>: the session the run belongs to.</summary>
    public const string SessionTagKey = "ai.session";

    private readonly IAutomationHost _host;

    /// <summary>The snapshot chunk size in effect (tests lower it to exercise multi-chunk transfers).</summary>
    internal int SnapshotChunk { get; init; } = SnapshotChunkBytes;

    /// <summary>The snapshot being streamed (one at a time; a new snapshot replaces it).</summary>
    private (string Token, byte[] Bytes, SnapshotResult Header)? _snapshot;

    public AutomationCommands(IAutomationHost host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>
    /// The session's authorisation, set by whoever started the session (an MCP server's launch flags).
    /// When set, the <c>allowDestructive</c> request argument is ignored and the policy decides; when null
    /// (the command line), the request argument carries the human's own <c>--allow-destructive</c>.
    /// </summary>
    public AutomationPolicy? Policy { get; init; }

    /// <summary>
    /// The <c>ai.session</c> value for runs that do not name one (<c>session</c> arg): one per command
    /// set — i.e. per <c>glacier-cli</c> invocation headlessly. The editor bridge supplies a fresh
    /// session per client connection instead (<see cref="BridgeServer"/>).
    /// </summary>
    public string SessionId { get; init; } = NewSessionId();

    /// <summary>A new random session id (provenance metadata only; never feeds script results).</summary>
    public static string NewSessionId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>The InformationalVersion of the running build (shared by the CLI and the editor).</summary>
    public static string ProductVersion { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(AutomationCommands).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>Executes one request; never throws (failures become an error response).</summary>
    public Task<AutomationResponse> ExecuteAsync(AutomationRequest request, CancellationToken cancellation = default) =>
        ExecuteAsync(request, progress: null, cancellation);

    /// <summary>
    /// Executes one request, reporting a build's compile stages to <paramref name="progress"/> (<c>build</c>,
    /// <c>holes</c>, and a <c>run</c> that builds); never throws. A canceled command answers <c>canceled</c>
    /// and leaves the document as it was (a script run is one transaction; a canceled build applies nothing).
    /// </summary>
    public async Task<AutomationResponse> ExecuteAsync(AutomationRequest request, IProgress<AutomationProgress>? progress, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            CheckPolicy(request);
            return await _host.RunOnDocumentThreadAsync(() => DispatchAsync(request, progress, cancellation), cancellation).ConfigureAwait(true);
        }
        catch (AutomationException ex)
        {
            return Fail(request.Command, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail(request.Command, AutomationErrorCodes.Canceled, "The command was canceled before it finished; the level is unchanged by it.");
        }
        catch (Exception ex)
        {
            return Fail(request.Command, AutomationErrorCodes.Internal, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private Task<AutomationResponse> DispatchAsync(AutomationRequest r, IProgress<AutomationProgress>? progress, CancellationToken ct) => r.Command switch
    {
        "hello" => Task.FromResult(Hello()),
        "info" => InfoAsync(r, ct),
        "new" => NewAsync(r, ct),
        "open" => OpenAsync(r, ct),
        "run" => RunAsync(r, progress, ct),
        "build" => BuildAsync(r, progress, ct),
        "holes" => HolesAsync(r, progress, ct),
        "lint" => LintAsync(r, ct),
        "summary" => SummaryAsync(r, ct),
        "analyze" => AnalyzeAsync(r, ct),
        "norms" => NormsAsync(r, ct),
        "save" => SaveCommandAsync(r, ct),
        "undo" => UndoCommandAsync(r, ct),
        "redo" => RedoCommandAsync(r, ct),
        "checkpoint" => CheckpointAsync(r, ct),
        "diff" => DiffAsync(r, ct),
        "inspect" => InspectAsync(r, ct),
        "query" => QueryAsync(r, ct),
        "catalog" => CatalogAsync(r, ct),
        "api" => Task.FromResult(Api(r)),
        "snapshot" => SnapshotAsync(r, ct),
        "spatial" => SpatialAsync(r, ct),
        "grade" => GradeAsync(r, ct),
        _ => throw new AutomationException(AutomationErrorCodes.UnknownCommand,
            $"Unknown command '{r.Command}'. Commands: {string.Join(", ", Names)}."),
    };

    // ---- hello / info / new / open ------------------------------------------------------------

    private AutomationResponse Hello() => Ok("hello", new HelloResult
    {
        Version = ProductVersion,
        Protocol = BridgeProtocol.Version,
        Mode = _host.Mode,
        Document = _host.Document?.Path,
        HasDocument = _host.Document is not null,
        RfInstallDir = _host.RfInstallDir,
    });

    /// <summary><c>info</c> — args: <c>level?</c>.</summary>
    private async Task<AutomationResponse> InfoAsync(AutomationRequest r, CancellationToken ct)
    {
        await EnsureLevelAsync(r, ct);
        return Ok(r.Command, Info());
    }

    /// <summary><c>new</c> — args: <c>out?</c> (save the new level there), <c>allowDestructive?</c>.</summary>
    private async Task<AutomationResponse> NewAsync(AutomationRequest r, CancellationToken ct)
    {
        string? outPath = Str(r, "out");
        string? target = outPath is null ? null : RequireWritable(outPath, AllowDestructive(r));
        RequireCanDiscard(r, "create a new level");
        await _host.NewAsync(ct);
        _host.ReportActivity($"{Prefix(r)} new level");
        if (target is not null)
        {
            await SaveCoreAsync(target, Prefix(r), ct);
        }

        return Ok(r.Command, Info());
    }

    /// <summary><c>open</c> — args: <c>path</c>, <c>allowDestructive?</c>.</summary>
    private async Task<AutomationResponse> OpenAsync(AutomationRequest r, CancellationToken ct)
    {
        string path = Str(r, "path") ?? Str(r, "level")
            ?? throw new AutomationException(AutomationErrorCodes.Usage, "open needs a 'path'.");
        await OpenCoreAsync(path, AllowDestructive(r), Prefix(r), ct);
        return Ok(r.Command, Info());
    }

    private InfoResult Info()
    {
        EditorDocument doc = RequireDocument();
        Geometry? geo = LevelOperations.CompiledGeometry(doc);
        LevelBuildState state = _host.BuildState;
        string geometry = geo is null || (geo.Faces.Count == 0 && geo.Rooms.Count == 0) ? "none"
            : state.GeometryIsPreview ? "preview"
            : state.GeometryDirty ? "stale"
            : "built";
        return new InfoResult
        {
            Path = doc.Path,
            Name = doc.Path is { Length: > 0 } p ? Path.GetFileName(p) : LevelTemplates.DefaultLevelName,
            RflVersion = doc.Rfl.Header.Version,
            Alpine = doc.Rfl.Context.IsAlpine,
            Dirty = doc.IsDirty,
            Brushes = LevelOperations.Brushes(doc).Count,
            Objects = doc.Objects.Count,
            Links = LevelOperations.LinkCount(doc),
            Geometry = geometry,
            Rooms = geo?.Rooms.Count ?? 0,
            Mode = _host.Mode,
            Notes = doc.Rfl.LoadNotes() is { Count: > 0 } notes ? notes : null,
            ReadOnly = doc.IsSaveBlocked ? true : null,
        };
    }

    // ---- run ----------------------------------------------------------------------------------

    /// <summary>
    /// <c>run</c> — args: <c>script</c> (Lua source text), <c>name</c> (script name, default "script.lua"),
    /// <c>dryRun?</c>, <c>allowDestructive?</c>, <c>timeoutSeconds?</c> (script execution limit; default
    /// 30 s, 0 = none, which also lifts the instruction budget), <c>instructionBudget?</c> (Lua VM instructions;
    /// default 200,000,000, 0 = unlimited; overrides what <c>timeoutSeconds: 0</c> implies), <c>save?</c> (save
    /// after a committed run), <c>out?</c> (save path; implies save).
    /// The run is one undo step labelled "CLI: &lt;name&gt;". A failed script is <c>ok: false</c> with
    /// the diagnostics + log in <c>result</c>. Everything a committed run creates is tagged
    /// <c>ai.script = &lt;name&gt;</c> and <c>ai.session = &lt;session&gt;</c> (a tag the script sets itself
    /// wins) unless <c>tag: false</c>; <c>session?</c> overrides the session id. <c>result.created</c>
    /// lists the created UIDs.
    /// </summary>
    private async Task<AutomationResponse> RunAsync(AutomationRequest r, IProgress<AutomationProgress>? progress, CancellationToken ct)
    {
        string source = Str(r, "script") ?? throw new AutomationException(AutomationErrorCodes.Usage, "run needs the script text ('script').");
        string name = Str(r, "name") is { Length: > 0 } n ? Path.GetFileName(n) : "script.lua";
        string session = Str(r, "session") ?? SessionId;
        if (session.Length == 0 || ObjectTags.ValueProblem(session) is not null || ObjectTags.ValueProblem(name) is not null)
        {
            throw new AutomationException(AutomationErrorCodes.Usage,
                "The script name and session id must be 1-256 characters without control characters.");
        }

        string prefix = Prefix(r);
        string? label = Str(r, "label");
        if (label is not null && (label.Length is 0 or > MaxRunLabel || label.Any(char.IsControl)))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"'label' must be 1-{MaxRunLabel} characters without control characters.");
        }

        Dictionary<string, string>? autoTags = BoolOr(r, "tag", true)
            ? new Dictionary<string, string>(StringComparer.Ordinal) { [ScriptTagKey] = name, [SessionTagKey] = session }
            : null;
        bool dryRun = Bool(r, "dryRun");
        bool allowDestructive = AllowDestructive(r);
        string? outPath = Str(r, "out");
        bool save = Bool(r, "save") || outPath is not null;
        if (save && dryRun)
        {
            string given = outPath is not null ? "'out' (a save path)" : "'save'";
            throw new AutomationException(AutomationErrorCodes.Usage,
                $"{given} cannot be combined with 'dryRun': a dry run rolls every change back, so there is nothing to save. " +
                "Drop the save to preview the run, or drop 'dryRun' to run it and save.");
        }

        await EnsureLevelAsync(r, ct);
        RequireDocument();
        string? saveTarget = save ? ResolveSaveTarget(outPath, allowDestructive) : null;

        ScriptExecutionLimits limits = Limits(Num(r, "timeoutSeconds"), Num(r, "instructionBudget"));

        var run = new AutomationScriptRun
        {
            Source = source,
            Name = name,
            DryRun = dryRun,
            AllowDestructive = allowDestructive,
            Limits = limits,
            AutoTags = autoTags,
            LabelPrefix = prefix,
            Label = label,
        };

        var log = new List<LogLineDto>();
        ScriptRunResult result = _host.RunScript(run, e => log.Add(new LogLineDto(Dto.Camel(e.Level.ToString()), e.Message)), ct);
        if (!result.Success && ct.IsCancellationRequested)
        {
            // Stopped by the caller: the run rolled back as one transaction.
            _host.ReportActivity($"{prefix} run {name} — canceled");
            throw new OperationCanceledException(ct);
        }

        BuildResult? built = null;
        bool light = Bool(r, "light");
        if (result.Success && !result.WasDryRun && (Bool(r, "build") || light))
        {
            built = BuildDto(await _host.BuildAsync(sealedBuild: true, bakeLighting: light, BoolOr(r, "shadows", true), ct, progress), sealedBuild: true, light);
            _host.ReportActivity($"{prefix} build after {name} ({(light ? "lit" : "sealed")}) — {built.Rooms} rooms, {built.Faces} faces{(built.HasErrors ? ", with errors" : string.Empty)}");
        }

        SaveResult? saved = null;
        if (result.Success && saveTarget is not null && built is not { HasErrors: true })
        {
            saved = await SaveCoreAsync(saveTarget, prefix, ct, Bool(r, "noBuild"));
        }

        var dto = new RunResult
        {
            Script = name,
            Success = result.Success,
            DryRun = result.WasDryRun,
            Committed = result.Committed,
            UndoLabel = result.Committed ? run.UndoLabel : null,
            Changes = new Dictionary<string, int>(result.Changes),
            ChangeCount = result.ChangeCount,
            ReturnValue = result.ReturnValue,
            Created = result.CreatedUids.ToList(),
            Tags = result.Committed && result.CreatedUids.Count > 0 ? autoTags : null,
            Error = result.Error is { } e ? new ScriptDiagnosticDto
            {
                Kind = Dto.Camel(e.Kind.ToString()),
                Message = e.Message,
                Chunk = e.Chunk,
                Line = e.Line,
                Column = e.Column,
                Hint = e.Hint,
                Display = e.ToDisplayString(),
            } : null,
            Log = log,
            Build = built,
            Saved = saved,
        };

        string summary = result.Success
            ? $"{prefix} run {name} — {(result.WasDryRun ? "dry run, " : string.Empty)}{(result.ChangeCount == 1 ? "1 change" : $"{result.ChangeCount} changes")}"
            : $"{prefix} run {name} — failed: {result.Error?.ToDisplayString()}";
        _host.ReportActivity(summary);

        if (built is { HasErrors: true })
        {
            return new AutomationResponse
            {
                Ok = false,
                Command = r.Command,
                Result = AutomationJson.ToNode(dto),
                Error = new AutomationError(AutomationErrorCodes.BuildFailed,
                    built.Messages.FirstOrDefault(m => m.Severity == "error")?.Text ?? "The build after the run reported errors (not saved)."),
            };
        }

        return result.Success
            ? Ok(r.Command, dto)
            : new AutomationResponse
            {
                Ok = false,
                Command = r.Command,
                Result = AutomationJson.ToNode(dto),
                Error = new AutomationError(AutomationErrorCodes.ScriptFailed, result.Error?.ToDisplayString() ?? "Script failed."),
            };
    }

    // ---- build / holes / lint / summary ---------------------------------------------------------

    /// <summary>
    /// <c>build</c> — args: <c>sealed?</c> (default false = the fast unsealed preview), <c>light?</c>
    /// (bake lighting; implies sealed), <c>shadows?</c> (default true), <c>save?</c>, <c>out?</c>,
    /// <c>allowDestructive?</c>. The result is applied to the document, as the editor's Build is.
    /// </summary>
    private async Task<AutomationResponse> BuildAsync(AutomationRequest r, IProgress<AutomationProgress>? progress, CancellationToken ct)
    {
        bool light = Bool(r, "light");
        bool sealedBuild = Bool(r, "sealed") || light;
        bool shadows = BoolOr(r, "shadows", true);
        string? outPath = Str(r, "out");
        bool save = Bool(r, "save") || outPath is not null;
        await EnsureLevelAsync(r, ct);
        RequireDocument();
        string? saveTarget = save ? ResolveSaveTarget(outPath, AllowDestructive(r)) : null;

        CompiledLevel result = await _host.BuildAsync(sealedBuild, light, shadows, ct, progress);
        BuildReport rep = result.Report;
        SaveResult? saved = saveTarget is null ? null : await SaveCoreAsync(saveTarget, Prefix(r), ct);
        BuildResult dto = BuildDto(result, sealedBuild, light);
        dto.Saved = saved;

        _host.ReportActivity($"{Prefix(r)} build ({(light ? "lit" : sealedBuild ? "sealed" : "preview")}) — {rep.Rooms} rooms, {rep.Faces} faces{(rep.HasErrors ? ", with errors" : string.Empty)}");
        return rep.HasErrors
            ? new AutomationResponse
            {
                Ok = false,
                Command = r.Command,
                Result = AutomationJson.ToNode(dto),
                Error = new AutomationError(AutomationErrorCodes.BuildFailed,
                    rep.Messages.FirstOrDefault(m => m.Severity == BuildSeverity.Error)?.Text ?? "The build reported errors."),
            }
            : Ok(r.Command, dto);
    }

    /// <summary>The build statistics + messages of a compile (RED's report figures).</summary>
    private BuildResult BuildDto(CompiledLevel result, bool sealedBuild, bool light)
    {
        BuildReport rep = result.Report;
        return new BuildResult
        {
            Sealed = sealedBuild,
            Lit = light,
            Method = _host.BuildSettings.UseSharedBsp ? "Shared BSP" : "Incremental",
            Brushes = rep.Brushes,
            Rooms = rep.Rooms,
            Subrooms = rep.Subrooms,
            Portals = rep.Portals,
            Faces = rep.Faces,
            Vertices = rep.Vertices,
            Surfaces = rep.Surfaces,
            LightmapPages = rep.LightmapPages,
            ElapsedMs = Math.Round(rep.ElapsedMs, 1),
            HasErrors = rep.HasErrors,
            Repair = BuildRepairDto.From(rep.Repair),
            Messages = rep.Messages.Select(m => new BuildMessageDto
            {
                Severity = Dto.Camel(m.Severity.ToString()),
                Text = m.Text,
                Pos = Dto.V(m.Location),
                BrushUid = m.BrushUid != 0 ? m.BrushUid : null,
            }).ToList(),
        };
    }

    /// <summary><c>holes</c> — args: <c>level?</c>. Always measured on a sealed build (not applied).</summary>
    private async Task<AutomationResponse> HolesAsync(AutomationRequest r, IProgress<AutomationProgress>? progress, CancellationToken ct)
    {
        await EnsureLevelAsync(r, ct);
        RequireDocument();
        IReadOnlyList<Vec3> holes = await _host.CheckHolesAsync(ct, progress);
        List<double[]> positions = holes
            .Select(Dto.V)
            .OrderBy(p => p[0]).ThenBy(p => p[1]).ThenBy(p => p[2])
            .ToList();
        bool truncated = positions.Count > HolesResult.HolesLimit;
        _host.ReportActivity(holes.Count == 0 ? $"{Prefix(r)} holes — watertight" : $"{Prefix(r)} holes — {holes.Count} leak edge(s)");
        return Ok(r.Command, new HolesResult
        {
            Count = holes.Count,
            Watertight = holes.Count == 0,
            Positions = truncated ? positions.Take(HolesResult.HolesLimit).ToList() : positions,
            Truncated = truncated,
        });
    }

    /// <summary>
    /// <c>lint</c> — args: <c>level?</c>, <c>minSeverity?</c> (info | warning | error: list only findings at or
    /// above it; the counts stay totals), <c>norms?</c> (default true: out-of-norm Info hints from the shipped corpus
    /// norms, at most 6; skipped with a note on preview geometry).
    /// </summary>
    private async Task<AutomationResponse> LintAsync(AutomationRequest r, CancellationToken ct)
    {
        LintSeverity min = (Str(r, "minSeverity") ?? "info").ToLowerInvariant() switch
        {
            "info" => LintSeverity.Info,
            "warning" => LintSeverity.Warning,
            "error" => LintSeverity.Error,
            string other => throw new AutomationException(AutomationErrorCodes.Usage, $"Unknown minSeverity '{other}' (use info, warning, or error)."),
        };
        await EnsureLevelAsync(r, ct);
        (LintReport report, List<string> notes) = LevelLint.Run(_host, checkNorms: BoolOr(r, "norms", true));
        _host.ReportActivity($"{Prefix(r)} lint — {report.Summary()}");
        return Ok(r.Command, new LintResult
        {
            Errors = report.ErrorCount,
            Warnings = report.WarningCount,
            Info = report.InfoCount,
            Findings = report.Findings.Where(f => f.Severity >= min).Select(f => new LintFindingDto
            {
                Severity = Dto.Camel(f.Severity.ToString()),
                Category = Dto.Camel(f.Category.ToString()),
                Message = f.Message,
                Uid = f.Uid,
                SecondaryUid = f.SecondaryUid,
                Pos = Dto.V(f.Location),
                BlocksSave = f.BlocksSave,
            }).ToList(),
            Notes = notes,
        });
    }

    /// <summary><c>summary</c> — args: <c>level?</c>, <c>detail?</c> ("brief" | "normal" (default) | "full").</summary>
    private async Task<AutomationResponse> SummaryAsync(AutomationRequest r, CancellationToken ct)
    {
        SummaryDetail detail = ParseDetail(Str(r, "detail"));
        await EnsureLevelAsync(r, ct);
        JsonObject summary = LevelSummaryBuilder.Build(_host, detail);
        _host.ReportActivity($"{Prefix(r)} summary ({Dto.Camel(detail.ToString())})");
        return new AutomationResponse { Ok = true, Command = r.Command, Result = summary };
    }

    /// <summary>
    /// <c>analyze</c> — args: <c>level?</c>, <c>timeoutSeconds?</c> (default 120, 0 = none). The level's fingerprint
    /// (<c>glacier.fingerprint/1</c>, CORPUS-NORMS-SPEC §3): scale, topology, materials, lighting, gameplay layout and
    /// event idioms measured from the compiled geometry stored in the level. With <c>level</c> the file is read
    /// read-only and is NOT opened as the session's document (no sidecar is loaded, no folder becomes writable);
    /// without it, the open document is fingerprinted from a private serialized copy. Nothing is modified or written.
    /// </summary>
    private async Task<AutomationResponse> AnalyzeAsync(AutomationRequest r, CancellationToken ct)
    {
        Ged.Core.Analysis.LevelFingerprint fp = await FingerprintAsync(r, ct);
        return new AutomationResponse { Ok = true, Command = r.Command, Result = fp.Json };
    }

    /// <summary>
    /// <c>norms</c> — args: <c>metric?</c>, <c>bucket?</c>, <c>compare?</c> (+ <c>level?</c>, <c>timeoutSeconds?</c> as
    /// <c>analyze</c>). The shipped corpus norms (CORPUS-NORMS-SPEC §5.3): without <c>metric</c>, every metric (id, unit,
    /// reliability, use, doc, published buckets); with <c>metric</c>, its numbers in every bucket (or only
    /// <c>bucket</c>); with <c>compare</c>, the level is fingerprinted (read-only, like <c>analyze</c>) and every metric
    /// that drives defaults is compared with its reference bucket: value, p5–p95, verdict below / within / above.
    /// No level, editor or install is needed except for <c>compare</c>.
    /// </summary>
    private async Task<AutomationResponse> NormsAsync(AutomationRequest r, CancellationToken ct)
    {
        if (!Ged.Core.Analysis.CorpusNorms.Available)
        {
            throw new AutomationException(AutomationErrorCodes.Internal, "This build carries no corpus norms.");
        }

        var result = new JsonObject
        {
            ["schema"] = Ged.Core.Analysis.CorpusNorms.Document?["schema"]?.DeepClone(),
            ["generated"] = Ged.Core.Analysis.CorpusNorms.Generated,
            ["referenceBucket"] = Ged.Core.Analysis.CorpusNorms.ReferenceBucket,
            ["corpus"] = Ged.Core.Analysis.CorpusNorms.Document?["corpus"]?.DeepClone(),
        };
        string? metric = Str(r, "metric");
        string? bucket = Str(r, "bucket")?.Trim().ToLowerInvariant();
        // compare: a JSON boolean (CLI) or the string "true" (the compare_to_norms tool's fixed argument).
        bool compare = r.Args["compare"] is JsonValue cv && (cv.TryGetValue(out bool cb) ? cb : cv.TryGetValue(out string? cs) && cs == "true");
        if (compare)
        {
            if (metric is not null || bucket is not null)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, "compare lists every default-driving metric against its reference bucket: drop metric / bucket.");
            }

            Ged.Core.Analysis.LevelFingerprint fp = await FingerprintAsync(r, ct);
            var rows = new JsonArray();
            List<Ged.Core.Analysis.NormComparison> comparisons = Ged.Core.Analysis.NormComparisons.Compare(fp.Json);
            foreach (Ged.Core.Analysis.NormComparison c in comparisons.OrderBy(c => c.Metric, StringComparer.Ordinal))
            {
                rows.Add(new JsonObject
                {
                    ["metric"] = c.Metric,
                    ["value"] = Dto.R(c.Value),
                    ["unit"] = c.Norm.Unit,
                    ["bucket"] = c.Norm.Bucket,
                    ["p5"] = c.Norm.P5,
                    ["p25"] = c.Norm.P25,
                    ["p50"] = c.Norm.P50,
                    ["p75"] = c.Norm.P75,
                    ["p95"] = c.Norm.P95,
                    ["verdict"] = c.Verdict,
                    ["message"] = c.Verdict == "within" ? null : c.Message(),
                });
            }

            result["status"] = fp.Status;
            result["gametype"] = Ged.Core.Analysis.NormComparisons.Gametype(fp.Json);
            result["comparisons"] = rows;
            result["outOfNorm"] = comparisons.Count(c => c.Verdict != "within");
            return new AutomationResponse { Ok = true, Command = r.Command, Result = result };
        }

        if (metric is null)
        {
            if (bucket is not null)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, "bucket needs metric.");
            }

            var list = new JsonArray();
            foreach (string id in Ged.Core.Analysis.CorpusNorms.MetricIds)
            {
                Ged.Core.Analysis.NormMetric m = Ged.Core.Analysis.CorpusNorms.Metric(id)!;
                list.Add(new JsonObject
                {
                    ["id"] = m.Id,
                    ["unit"] = m.Unit,
                    ["reliability"] = m.Reliability,
                    ["use"] = m.Use,
                    ["doc"] = m.Doc,
                    ["buckets"] = new JsonArray(m.Buckets.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()),
                });
            }

            result["metrics"] = list;
            return new AutomationResponse { Ok = true, Command = r.Command, Result = result };
        }

        if (Ged.Core.Analysis.CorpusNorms.Metric(metric) is not { } def)
        {
            throw new AutomationException(AutomationErrorCodes.Usage,
                $"Unknown metric '{metric}'. {Ged.Core.Scripting.ScriptSuggest.Hint(metric, Ged.Core.Analysis.CorpusNorms.MetricIds, "metric")}");
        }

        if (bucket is not null && !def.Buckets.Contains(bucket, StringComparer.Ordinal))
        {
            throw new AutomationException(AutomationErrorCodes.Usage,
                $"'{metric}' is not published for bucket '{bucket}' (published: {string.Join(", ", def.Buckets)}).");
        }

        var buckets = new JsonObject();
        foreach (string b in bucket is null ? def.Buckets : new[] { bucket })
        {
            Ged.Core.Analysis.NormStats s = Ged.Core.Analysis.CorpusNorms.GetBucket(metric, b)!;
            buckets[b] = new JsonObject { ["n"] = s.N, ["p5"] = s.P5, ["p25"] = s.P25, ["p50"] = s.P50, ["p75"] = s.P75, ["p95"] = s.P95 };
        }

        result["metric"] = new JsonObject
        {
            ["id"] = def.Id,
            ["unit"] = def.Unit,
            ["reliability"] = def.Reliability,
            ["use"] = def.Use,
            ["doc"] = def.Doc,
            ["reference"] = Ged.Core.Analysis.CorpusNorms.Get(metric)?.Bucket,
            ["buckets"] = buckets,
        };
        return new AutomationResponse { Ok = true, Command = r.Command, Result = result };
    }

    /// <summary>The fingerprint of <c>level</c> (read-only, never opened as the document) or of the open document.</summary>
    private async Task<Ged.Core.Analysis.LevelFingerprint> FingerprintAsync(AutomationRequest r, CancellationToken ct)
    {
        byte[] bytes;
        string? fileName;
        if (Str(r, "level") is { } level)
        {
            string full = PathAllowList.Normalize(level);
            if (!full.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase))
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"Only level files (.rfl) can be analyzed: {full}");
            }

            if (!File.Exists(full))
            {
                throw new AutomationException(AutomationErrorCodes.Io, $"Level file not found: {full}");
            }

            try
            {
                bytes = Analysis.CorpusReader.ReadAll(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AutomationException(AutomationErrorCodes.Io, $"Could not read '{full}': {ex.Message}");
            }

            fileName = Path.GetFileName(full);
        }
        else
        {
            EditorDocument doc = RequireDocument();
            bytes = doc.Rfl.Save(updateTimestamp: false);
            fileName = doc.Path is { Length: > 0 } p ? Path.GetFileName(p) : null;
        }

        double seconds = Num(r, "timeoutSeconds") ?? 120;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (seconds > 0)
        {
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        Ged.Core.Analysis.MetricOptions options = AnalysisOptions(_host, fileName);
        Ged.Core.Analysis.LevelFingerprint fp;
        try
        {
            fp = await Task.Run(() => Ged.Core.Analysis.LevelMetrics.Analyze(bytes, options, limit.Token), limit.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AutomationException(AutomationErrorCodes.Internal, $"The analysis did not finish within {seconds:0} s (--timeout).");
        }
        catch (Exception ex) when (ex is Ged.Core.IO.Rfl.RflFormatException or EndOfStreamException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"The level could not be read: {ex.Message}");
        }

        _host.ReportActivity($"{Prefix(r)} {r.Command} {fileName ?? LevelTemplates.DefaultLevelName} ({fp.Status})");
        return fp;
    }

    /// <summary>
    /// The metric options for a level analyzed in this session: the mounted install's item table (item categories)
    /// and stock-texture provenance, when an install is mounted.
    /// </summary>
    internal static Ged.Core.Analysis.MetricOptions AnalysisOptions(IAutomationHost host, string? fileName)
    {
        Ged.Core.Assets.AssetVfs? vfs = host.Assets;
        Ged.Core.Tables.ItemCatalog? items = null;
        if (vfs is not null)
        {
            try
            {
                items = vfs.ReadFile("items.tbl") is { } tbl ? Ged.Core.Tables.ItemCatalog.Load(tbl) : null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
            {
                items = null;
            }
        }

        // Stock provenance needs a retail packfile in the mount; without one the fraction is unknown (null), not 0.
        bool hasStock = vfs is not null && vfs.Sources.Any(s => Ged.Core.Assets.StockContent.OriginOf(s) == Ged.Core.Assets.AssetOrigin.Stock);
        return new Ged.Core.Analysis.MetricOptions
        {
            FileName = fileName,
            Items = items,
            IsStockTexture = hasStock ? name => Ged.Core.Assets.StockContent.Describe(vfs!, name)?.Stock == true : null,
        };
    }

    /// <summary>Parses a summary detail name (throws a usage error for anything else).</summary>
    public static SummaryDetail ParseDetail(string? value) => (value ?? "normal").ToLowerInvariant() switch
    {
        "brief" => SummaryDetail.Brief,
        "normal" => SummaryDetail.Normal,
        "full" => SummaryDetail.Full,
        _ => throw new AutomationException(AutomationErrorCodes.Usage, $"Unknown detail '{value}' (use brief, normal, or full)."),
    };

    // ---- undo ---------------------------------------------------------------------------------

    /// <summary>The undo-label prefix of a command-line script run (<see cref="AutomationScriptRun.UndoLabel"/>).</summary>
    public const string CliUndoPrefix = "CLI:";

    /// <summary>The undo-label prefix of a script run requested by an AI agent (request <c>origin: "mcp"</c>).</summary>
    public const string AiUndoPrefix = "AI:";

    /// <summary>The longest <c>run</c> label (appended to the undo label).</summary>
    public const int MaxRunLabel = 60;

    /// <summary>
    /// <c>undo</c> — args: <c>steps?</c> (default 1). Undoes the latest <c>steps</c> undo steps, but only
    /// when EVERY one of them is an automation run (label "CLI: …" or "AI: …"): an agent may take back
    /// automation work, never the designer's. Otherwise nothing is undone and the error names the step in
    /// the way. The steps undone become redoable with <c>redo</c>.
    /// </summary>
    private async Task<AutomationResponse> UndoCommandAsync(AutomationRequest r, CancellationToken ct)
    {
        double requested = Num(r, "steps") ?? 1;
        if (requested < 1 || requested != Math.Floor(requested) || requested > UndoStack.MaxNodes)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"'steps' must be a whole number from 1 to {UndoStack.MaxNodes}.");
        }

        int steps = (int)requested;
        await EnsureLevelAsync(r, ct);
        RequireDocument();
        IReadOnlyList<string> labels = _host.RecentUndoLabels(steps);
        int cli = labels.TakeWhile(IsAutomationLabel).Count();
        if (cli < steps)
        {
            string why = labels.Count == 0
                ? "There is nothing to undo."
                : cli == labels.Count
                ? $"Only {cli} undo step(s) exist; nothing was undone."
                : cli == 0
                ? $"The latest undo step is \"{labels[0]}\", which is not a command-line run; nothing was undone. Undo it in the editor (Ctrl+Z) if that is intended."
                : $"Only the latest {cli} undo step(s) are command-line runs (the next is \"{labels[cli]}\"); nothing was undone. Use --steps {cli} or fewer.";
            throw new AutomationException(AutomationErrorCodes.UndoRefused, why);
        }

        RememberUndone(steps);
        await _host.UndoAsync(steps, ct);
        _host.ReportActivity(steps == 1 ? $"{Prefix(r)} undid \"{labels[0]}\"" : $"{Prefix(r)} undid {steps} automation runs");
        return Ok(r.Command, new UndoResult
        {
            Undone = labels.ToList(),
            Next = _host.RecentUndoLabels(1).FirstOrDefault(),
        });
    }

    /// <summary>True for an automation run's undo label ("CLI: …" from the command line, "AI: …" from an agent).</summary>
    public static bool IsAutomationLabel(string label) =>
        label.StartsWith(CliUndoPrefix, StringComparison.Ordinal) || label.StartsWith(AiUndoPrefix, StringComparison.Ordinal);

    // ---- save ---------------------------------------------------------------------------------

    /// <summary>
    /// <c>save</c> — args: <c>level?</c>, <c>out?</c> (default: the level's own path),
    /// <c>allowDestructive?</c> (needed to overwrite a different existing file that the user did not
    /// name on the command line).
    /// </summary>
    private async Task<AutomationResponse> SaveCommandAsync(AutomationRequest r, CancellationToken ct)
    {
        await EnsureLevelAsync(r, ct);
        RequireDocument();
        string target = ResolveSaveTarget(Str(r, "out"), AllowDestructive(r));
        return Ok(r.Command, await SaveCoreAsync(target, Prefix(r), ct, Bool(r, "noBuild")));
    }

    /// <summary>
    /// Saves the open level. Unless <paramref name="noBuild"/>, a level whose compiled geometry is
    /// missing or out of date is first given a sealed geometry build (an unsealed preview is re-sealed
    /// by the host's own save) — Red Faction
    /// crashes loading a level that has brushes but no compiled geometry, and an automated caller
    /// should never produce one by accident. Lighting stays opt-in (<c>--light</c>); a rebuild that
    /// leaves the level unlit says so in the result. With <paramref name="noBuild"/> a level without
    /// compiled geometry is still written, with a warning.
    /// </summary>
    private async Task<SaveResult> SaveCoreAsync(string target, string prefix, CancellationToken ct, bool noBuild = false)
    {
        EditorDocument doc = RequireDocument();
        RequireSavable(doc);
        LevelBuildState state = _host.BuildState;
        bool missing = CompiledGeometryState.IsMissing(doc.Rfl);
        // An unsealed preview is re-sealed by the host's save itself (the editor's File ▸ Save rule).
        bool stale = CompiledGeometryState.HasBrushes(doc.Rfl) && state.GeometryDirty && !state.GeometryIsPreview;
        string? warning = null;
        bool rebuilt = false;
        if ((missing || stale) && !noBuild)
        {
            await _host.BuildAsync(sealedBuild: true, bakeLighting: false, shadows: false, ct);
            _host.ReportActivity($"{prefix} built geometry for the save");
            rebuilt = true;
        }
        else if (missing)
        {
            warning = "WARNING: saved with no compiled geometry (--no-build) — Red Faction crashes loading this level. Build it before playing.";
        }

        string? previousPath = doc.Path;
        bool saveAs = previousPath is { } current && !AutomationScriptOperations.PathsEqual(current, target);
        HostSaveOutcome outcome = await _host.SaveAsync(target, saveAs, ct);
        _host.ReportActivity($"{prefix} saved {Path.GetFileName(outcome.Path)}");
        return new SaveResult
        {
            Path = outcome.Path,
            RflVersion = doc.Rfl.Header.Version,
            // The first save of a never-saved level also binds the document to a new file: report it as a Save As
            // (the host still writes it as a plain save, exactly like the editor's first File ▸ Save).
            SaveAs = outcome.SaveAs || previousPath is null,
            PreviousPath = previousPath,
            Stamped = outcome.Stamped,
            Notice = rebuilt
                ? "Geometry was built (sealed) for the save; lighting is not baked — build --light and save again for lit surfaces."
                : LevelSavePolicy.NoticeText(outcome.Notice),
            Built = rebuilt,
            Warning = warning,
            UpgradeNotes = doc.LastSaveNotes is { Count: > 0 } upgradeNotes ? upgradeNotes : null,
        };
    }

    /// <summary>Refuses to save a level newer than Glacier writes (it is open read-only).</summary>
    private static void RequireSavable(EditorDocument doc)
    {
        if (doc.SaveBlockedReason is { } reason)
        {
            throw new AutomationException(AutomationErrorCodes.SaveFailed, $"Save refused: {reason}");
        }
    }

    /// <summary>
    /// Resolves and allow-lists the save target (before the script/build runs): an explicit
    /// <paramref name="outPath"/>, else the open document's own path. Throws when there is no path.
    /// </summary>
    private string ResolveSaveTarget(string? outPath, bool allowDestructive)
    {
        if (_host.Document is { } open)
        {
            RequireSavable(open);
        }

        if (outPath is not null)
        {
            return RequireWritable(outPath, allowDestructive);
        }

        // Saving a level back to its own file is File ▸ Save — always allowed.
        string own = _host.Document?.Path
            ?? throw new AutomationException(AutomationErrorCodes.Usage,
                "The level has never been saved: pass a path to save to (--out <path>).");
        return PathAllowList.Normalize(own);
    }

    /// <summary>
    /// Allow-lists a write target. Overwriting an EXISTING file that is neither the open level nor a
    /// path the user named on the command line needs <c>allowDestructive</c>.
    /// </summary>
    private string RequireWritable(string path, bool allowDestructive)
    {
        if (!path.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"Level files must end in .rfl: {path}");
        }

        string full = _host.PathPolicy.Require(path, "write");
        bool own = _host.Document?.Path is { } current && AutomationScriptOperations.PathsEqual(current, full);
        if (File.Exists(full) && !own && !_host.PathPolicy.IsExplicit(full) && !allowDestructive)
        {
            throw new AutomationException(AutomationErrorCodes.DestructiveNotAllowed,
                $"'{full}' already exists. Pass --allow-destructive to overwrite it.");
        }

        return full;
    }

    // ---- snapshot -----------------------------------------------------------------------------

    /// <summary>
    /// <c>snapshot</c> — args: <c>level?</c>, <c>token?</c>, <c>offset?</c>. Copies the open level out as
    /// raw RFL bytes — the in-memory document exactly as it is, including its current compiled geometry
    /// and lightmaps (a plain serialization, NOT a save: nothing is re-sealed, stamped, or reconciled, and
    /// the document is not modified) — plus its compiled-geometry state. <c>glacier-cli render --attach</c>
    /// renders the copy in its own process. The first request (no <c>token</c>) serializes the level and
    /// returns the first chunk; while <c>complete</c> is false, request the next chunk with the returned
    /// <c>token</c> and <c>offset</c> = bytes received so far. Chunks are at most
    /// <see cref="SnapshotChunkBytes"/> raw bytes (base64 in <c>data</c>); <c>sha256</c> covers the whole level.
    /// </summary>
    private async Task<AutomationResponse> SnapshotAsync(AutomationRequest r, CancellationToken ct)
    {
        string? token = Str(r, "token");
        (string Token, byte[] Bytes, SnapshotResult Header) snap;
        if (token is null)
        {
            await EnsureLevelAsync(r, ct);
            EditorDocument doc = RequireDocument();
            byte[] bytes = doc.Rfl.Save(updateTimestamp: false);
            LevelBuildState state = _host.BuildState;
            Geometry? geo = LevelOperations.CompiledGeometry(doc);
            string geometry = geo is null || (geo.Faces.Count == 0 && geo.Rooms.Count == 0) ? "none"
                : state.GeometryIsPreview ? "preview"
                : state.GeometryDirty ? "stale"
                : "built";
            var header = new SnapshotResult
            {
                Token = NewSessionId(),
                Path = doc.Path,
                Name = doc.Path is { Length: > 0 } p ? Path.GetFileName(p) : LevelTemplates.DefaultLevelName,
                Size = bytes.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                Geometry = geometry,
                GeometryDirty = state.GeometryDirty,
                GeometryIsPreview = state.GeometryIsPreview,
                LightingDirty = state.LightingDirty,
                Dirty = doc.IsDirty,
                RfInstallDir = _host.RfInstallDir,
            };
            snap = (header.Token, bytes, header);
            _snapshot = snap;
            _host.ReportActivity($"{Prefix(r)} snapshot of {header.Name} ({bytes.Length:N0} bytes)");
        }
        else if (_snapshot is { } cached && cached.Token == token)
        {
            snap = cached;
        }
        else
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "Unknown or expired snapshot token: request a new snapshot.");
        }

        double offsetValue = Num(r, "offset") ?? 0;
        if (offsetValue < 0 || offsetValue > snap.Bytes.Length || offsetValue != Math.Floor(offsetValue))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"Snapshot offset {offsetValue} is outside the level ({snap.Bytes.Length} bytes).");
        }

        int offset = (int)offsetValue;
        int count = Math.Min(SnapshotChunk, snap.Bytes.Length - offset);
        bool complete = offset + count >= snap.Bytes.Length;
        if (complete)
        {
            _snapshot = null; // fully delivered: release the copy
        }

        SnapshotResult h = snap.Header;
        return Ok(r.Command, new SnapshotResult
        {
            Token = h.Token,
            Path = h.Path,
            Name = h.Name,
            Size = h.Size,
            Sha256 = h.Sha256,
            Geometry = h.Geometry,
            GeometryDirty = h.GeometryDirty,
            GeometryIsPreview = h.GeometryIsPreview,
            LightingDirty = h.LightingDirty,
            Dirty = h.Dirty,
            RfInstallDir = h.RfInstallDir,
            Offset = offset,
            Data = Convert.ToBase64String(snap.Bytes, offset, count),
            Complete = complete,
        });
    }

    // ---- shared helpers -----------------------------------------------------------------------

    /// <summary>Opens the request's <c>level</c> when it is not already the open document.</summary>
    private async Task EnsureLevelAsync(AutomationRequest r, CancellationToken ct)
    {
        if (Str(r, "level") is { } level)
        {
            if (_host.Document?.Path is { } current && AutomationScriptOperations.PathsEqual(current, level))
            {
                return;
            }

            await OpenCoreAsync(level, AllowDestructive(r), Prefix(r), ct);
        }
    }

    private async Task OpenCoreAsync(string path, bool allowDestructive, string prefix, CancellationToken ct)
    {
        // Reading is limited to level files: any existing .rfl may be opened (that is the designer's
        // normal workflow, and the file is only ever parsed as a level). Writes stay allow-listed —
        // opening a level makes its own folder writable, as File ▸ Open + Save would.
        string full = PathAllowList.Normalize(path);
        if (!full.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"Only level files (.rfl) can be opened: {full}");
        }

        if (!File.Exists(full))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"Level file not found: {full}");
        }

        RequireCanDiscard(allowDestructive, $"open {Path.GetFileName(full)}");
        await _host.OpenAsync(full, ct);
        _host.ReportActivity($"{prefix} opened {Path.GetFileName(full)}");
    }

    private void RequireCanDiscard(AutomationRequest r, string what) => RequireCanDiscard(AllowDestructive(r), what);

    private void RequireCanDiscard(bool allowDestructive, string what)
    {
        if (_host.Document is { IsDirty: true } && !allowDestructive)
        {
            throw new AutomationException(AutomationErrorCodes.DestructiveNotAllowed,
                Policy is null
                    ? $"The open level has unsaved changes; refusing to {what}. Save first, or pass --allow-destructive to discard them."
                    : $"The open level has unsaved changes; refusing to {what}. Save first; discarding unsaved work needs a session started with destructive actions allowed.");
        }
    }

    /// <summary>
    /// Whether the request may discard / overwrite work: the session policy when one is set (the request
    /// argument is then ignored), else the request's <c>allowDestructive</c> (the human's own CLI flag).
    /// </summary>
    private bool AllowDestructive(AutomationRequest r) => Policy is { } p ? p.AllowDestructive : Bool(r, "allowDestructive");

    /// <summary>The label prefix for this request: "AI:" for <c>origin: "mcp"</c>, else "CLI:".</summary>
    private static string Prefix(AutomationRequest r) => Str(r, "origin") switch
    {
        null or "cli" => CliUndoPrefix,
        "mcp" => AiUndoPrefix,
        string other => throw new AutomationException(AutomationErrorCodes.Usage, $"Unknown origin '{other}' (use cli or mcp)."),
    };

    /// <summary>Refuses a request the session policy does not permit (before anything runs).</summary>
    private void CheckPolicy(AutomationRequest r)
    {
        if (Policy is not { } p)
        {
            return;
        }

        if (p.AllowedCommands is { } allowed && !allowed.Contains(r.Command))
        {
            throw new AutomationException(AutomationErrorCodes.ToolNotAllowed, $"'{r.Command}' is not enabled for this session.");
        }

        if (p.ReadOnly && AutomationPolicy.MutatingCommands.Contains(r.Command) && !(r.Command == "run" && Bool(r, "dryRun")))
        {
            throw new AutomationException(AutomationErrorCodes.ToolNotAllowed, $"This session is read-only; '{r.Command}' changes the level.");
        }
    }

    private EditorDocument RequireDocument() =>
        _host.Document ?? throw new AutomationException(AutomationErrorCodes.NoDocument,
            "No level is open. Pass a level file, or use 'new' / 'open' first.");

    private static AutomationResponse Ok<T>(string command, T result) =>
        new() { Ok = true, Command = command, Result = AutomationJson.ToNode(result) };

    private static AutomationResponse Fail(string command, string code, string message) =>
        new() { Ok = false, Command = command, Error = new AutomationError(code, message) };

    private static string? Str(AutomationRequest r, string name)
    {
        if (!r.Args.TryGetPropertyValue(name, out JsonNode? n) || n is null)
        {
            return null;
        }

        if (n is JsonValue v && v.TryGetValue(out string? s))
        {
            return s;
        }

        throw new AutomationException(AutomationErrorCodes.Usage, $"Argument '{name}' must be a string.");
    }

    private static bool Bool(AutomationRequest r, string name) => BoolOr(r, name, false);

    private static bool BoolOr(AutomationRequest r, string name, bool fallback)
    {
        if (!r.Args.TryGetPropertyValue(name, out JsonNode? n) || n is null)
        {
            return fallback;
        }

        if (n is JsonValue v && v.TryGetValue(out bool b))
        {
            return b;
        }

        throw new AutomationException(AutomationErrorCodes.Usage, $"Argument '{name}' must be true or false.");
    }

    /// <summary>
    /// The script limits of a <c>run</c>: the defaults, a <paramref name="timeoutSeconds"/> of 0 (no time limit) also
    /// lifting the instruction budget, and an explicit <paramref name="instructionBudget"/> (0 = unlimited) winning
    /// over both.
    /// </summary>
    internal static ScriptExecutionLimits Limits(double? timeoutSeconds, double? instructionBudget)
    {
        ScriptExecutionLimits defaults = ScriptExecutionLimits.Default;
        if (instructionBudget is double b && (b < 0 || b != Math.Floor(b) || b > long.MaxValue))
        {
            throw new AutomationException(AutomationErrorCodes.Usage,
                $"'instructionBudget' must be a whole number of Lua instructions, 0 for unlimited (got {b.ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
        }

        if (timeoutSeconds is null && instructionBudget is null)
        {
            return defaults;
        }

        long budget = instructionBudget is double given ? (long)given
            : timeoutSeconds is <= 0 ? 0
            : defaults.InstructionBudget;
        TimeSpan timeout = timeoutSeconds is double secs
            ? secs <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(secs)
            : defaults.Timeout;
        return new ScriptExecutionLimits { InstructionBudget = budget, Timeout = timeout, YieldEvery = defaults.YieldEvery };
    }

    private static double? Num(AutomationRequest r, string name)
    {
        if (!r.Args.TryGetPropertyValue(name, out JsonNode? n) || n is null)
        {
            return null;
        }

        if (n is JsonValue v)
        {
            // A parsed request carries JSON numbers; an in-process caller may build the node from an int.
            if (v.TryGetValue(out double d))
            {
                return d;
            }

            if (v.TryGetValue(out int i))
            {
                return i;
            }

            if (v.TryGetValue(out long l))
            {
                return l;
            }
        }

        throw new AutomationException(AutomationErrorCodes.Usage, $"Argument '{name}' must be a number.");
    }
}
