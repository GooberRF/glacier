using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Spatial;

namespace Ged.Automation;

// The spatial queries and the design grader (DESIGN-QUALITY-SPEC §(e) E1–E8, E11): the `spatial` and `grade` commands.
// Both are read-only: they never change the document, its undo history, dirty flag or compiled geometry — a fresh
// build they need is compiled privately and not applied.
public sealed partial class AutomationCommands
{
    /// <summary>The <c>spatial</c> argument names the command forwards to <see cref="SpatialQueries.Run"/> (camelCase → the core's snake_case).</summary>
    private static readonly (string Camel, string Snake)[] SpatialArgs =
    {
        ("from", "from"), ("dir", "dir"), ("max", "max"), ("a", "a"), ("b", "b"), ("pos", "pos"), ("profile", "profile"),
        ("k", "k"), ("kind", "kind"), ("scale", "scale"), ("region", "region"), ("around", "around"), ("radius", "radius"),
        ("maxNodes", "max_nodes"), ("playerClass", "player_class"),
    };

    /// <summary>
    /// <c>spatial</c> — args: <c>op</c> (see <see cref="SpatialQueries.Ops"/>), the operation's arguments (points as
    /// [x, y, z]; <c>p</c> / <c>q</c> stand for an operation's first and second point), <c>build?</c> (auto | preview | sealed | none; default auto: a private preview build when the
    /// level's geometry is missing or stale), <c>maxSamples?</c>, <c>timeoutSeconds?</c> (default 60), <c>level?</c>.
    /// </summary>
    private async Task<AutomationResponse> SpatialAsync(AutomationRequest r, CancellationToken ct)
    {
        string op = (Str(r, "op") ?? throw new AutomationException(AutomationErrorCodes.Usage,
            $"spatial needs an 'op': {string.Join(", ", SpatialQueries.Ops.Select(o => o.Op))}.")).ToLowerInvariant();
        var args = new JsonObject();
        foreach ((string camel, string snake) in SpatialArgs)
        {
            if (r.Args[camel] is JsonNode n)
            {
                args[snake] = n.DeepClone();
            }
        }

        // The generic points (the MCP tool's p and q) stand for each operation's own point arguments.
        if (r.Args["p"] is JsonNode pp)
        {
            string name = op switch
            {
                "raycast" or "walk" => "from",
                "los" or "routes" => "a",
                "point_info" or "clearance" => "pos",
                _ => "around",
            };
            args[name] ??= pp.DeepClone();
        }

        if (r.Args["q"] is JsonNode qq)
        {
            args["b"] ??= qq.DeepClone();
        }

        if (op == "graph" && args["max_nodes"] is null && r.Args["max"] is JsonNode mx)
        {
            args["max_nodes"] = mx.DeepClone();
        }

        SpatialLevel level = await SpatialLevelAsync(r, ct);
        SpatialBudget budget = Budget(r, 60);
        JsonObject result = await Task.Run(() => Spatial(() => SpatialQueries.Run(level, op, args, budget, ct)), ct).ConfigureAwait(true);
        _host.ReportActivity($"{Prefix(r)} spatial {op}");
        return new AutomationResponse { Ok = true, Command = r.Command, Result = result };
    }

    /// <summary>
    /// <c>grade</c> — args: <c>gametype?</c>, <c>players?</c>, <c>class?</c> (standard | open_air | payload),
    /// <c>maxLocations?</c>, <c>metrics?</c>, <c>build?</c>, <c>maxSamples?</c>, <c>timeoutSeconds?</c> (default 120),
    /// <c>level?</c>. The design grader (DESIGN-QUALITY-SPEC §(d)): verdict, failures with values, fix hints and locations.
    /// </summary>
    private async Task<AutomationResponse> GradeAsync(AutomationRequest r, CancellationToken ct)
    {
        double? players = Num(r, "players");
        if (players is double p && (p < 1 || p > 64 || p != Math.Floor(p)))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "'players' must be a whole number from 1 to 64.");
        }

        double maxLoc = Num(r, "maxLocations") ?? 6;
        if (maxLoc < 0 || maxLoc > 50 || maxLoc != Math.Floor(maxLoc))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "'maxLocations' must be a whole number from 0 to 50.");
        }

        var options = new GradeOptions
        {
            Gametype = Str(r, "gametype"),
            Players = players is double pp ? (int)pp : null,
            Class = Str(r, "class"),
            MaxLocations = (int)maxLoc,
            IncludeMetrics = Bool(r, "metrics"),
        };
        SpatialLevel level = await SpatialLevelAsync(r, ct);
        SpatialBudget budget = Budget(r, 120);
        JsonObject result = await Task.Run(() => Spatial(() => SpatialQueries.Grade(level, options, budget, ct)), ct).ConfigureAwait(true);
        _host.ReportActivity($"{Prefix(r)} grade — {result["verdict"]} ({result["failures_count"]} failing)");
        return new AutomationResponse { Ok = true, Command = r.Command, Result = result };
    }

    private static SpatialBudget Budget(AutomationRequest r, double defaultSeconds)
    {
        double seconds = Num(r, "timeoutSeconds") ?? defaultSeconds;
        double samples = Num(r, "maxSamples") ?? SpatialBudget.DefaultMaxSamples;
        if (samples < 10_000 || samples > 4_000_000 || samples != Math.Floor(samples))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "'maxSamples' must be a whole number from 10000 to 4000000.");
        }

        if (seconds < 0)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "'timeoutSeconds' cannot be negative.");
        }

        return new SpatialBudget { MaxSamples = (int)samples, TimeLimit = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null };
    }

    /// <summary>
    /// The level to measure: the open document (opening <c>level</c> first) on its stored geometry, or on a private
    /// fresh build per the <c>build</c> policy (auto: preview when missing or stale; preview; sealed; none). Never applied.
    /// </summary>
    private async Task<SpatialLevel> SpatialLevelAsync(AutomationRequest r, CancellationToken ct)
    {
        string build = (Str(r, "build") ?? "auto").ToLowerInvariant();
        if (build is not ("auto" or "preview" or "sealed" or "none"))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"Unknown build '{build}' (use auto, preview, sealed or none).");
        }

        await EnsureLevelAsync(r, ct);
        EditorDocument doc = RequireDocument();
        string state = Rendering.RenderCommand.GeometryState(_host, doc);
        bool hasBrushes = LevelOperations.Brushes(doc).Count > 0;
        string action = build switch
        {
            "auto" => (state == "stale" || state == "none") && hasBrushes ? "preview" : "none",
            _ => build,
        };
        var sizes = new Ged.Core.Assets.TextureSizeLookup(_host.Assets);
        Func<string, (int, int)?>? textures = sizes.Available ? n => sizes.Get(n) : null;
        if (action == "none")
        {
            if (state == "none")
            {
                throw new AutomationException(SpatialNoGeometry, "The level has no compiled geometry to measure. Build it first, or pass build preview / sealed.");
            }

            return SpatialQueries.DocumentLevel(doc, state, textures);
        }

        CompiledLevel compiled = await Task.Run(() => LevelOperations.Compile(doc, _host.Assets, _host.BuildSettings, sealedBuild: action == "sealed",
            bakeLighting: false, shadows: false, traits: null, ct), ct).ConfigureAwait(true);
        if (compiled.Geometry.Faces.Count == 0)
        {
            throw new AutomationException(SpatialNoGeometry, "The fresh build produced no geometry (the level has no closed rooms).");
        }

        return SpatialQueries.DocumentLevel(doc, state, textures, compiled.Geometry, action);
    }

    /// <summary>Error code: no compiled geometry, or no playable floor, to measure.</summary>
    public const string SpatialNoGeometry = "no_geometry";

    /// <summary>Error code: the measurement ran past its time limit.</summary>
    public const string SpatialTimeout = "timeout";

    /// <summary>Runs a spatial call, turning its errors into automation errors with their hint.</summary>
    private static JsonObject Spatial(Func<JsonObject> work)
    {
        try
        {
            return work();
        }
        catch (SpatialException ex)
        {
            string code = ex.Code switch
            {
                "usage" => AutomationErrorCodes.Usage,
                "timeout" => SpatialTimeout,
                _ => SpatialNoGeometry,
            };
            throw new AutomationException(code, string.IsNullOrEmpty(ex.Hint) ? ex.Message : $"{ex.Message} {ex.Hint}");
        }
    }
}
