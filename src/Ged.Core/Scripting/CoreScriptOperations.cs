using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Packaging;

namespace Ged.Core.Scripting;

/// <summary>
/// The default, pure-Core operation backend: drives <c>GeometryBuildService</c>,
/// <c>LevelLighting</c>, <c>HoleDetector</c>, <c>EditorDocument.Save</c>, and
/// <c>PackfileBuilder</c> directly, forwarding progress to the context's progress sink + log and
/// honoring the run's cancellation token. It never reimplements the compiler/baker —
/// it invokes them as opaque, cancelable operations. Builds are assembled by the shared
/// <see cref="CompileOptionsFactory"/> from <see cref="ScriptServices.BuildSettings"/> (default:
/// the editor's defaults), so a script build is byte-identical to an editor build with the same
/// settings — including the Alpine geoable/breakable room tables. This is the headless backend;
/// the editor wraps it to refresh its viewport and launch playtests. Playtest needs the
/// interactive editor and is rejected here.
/// </summary>
public sealed class CoreScriptOperations : IScriptOperations
{
    private readonly ScriptContext _ctx;
    private readonly ScriptServices _services;
    private readonly Func<BuildSettings>? _liveSettings;
    private TextureTraitsCache? _traits;

    /// <summary>
    /// Creates the Core backend for a run (hosts that wrap it construct it from their
    /// <see cref="ScriptServices.OperationsFactory"/>). <paramref name="liveSettings"/>, when given,
    /// is read at each build instead of the <see cref="ScriptServices.BuildSettings"/> snapshot — for
    /// long-lived sessions (the editor console) whose user settings can change between lines.
    /// </summary>
    public CoreScriptOperations(ScriptContext ctx, Func<BuildSettings>? liveSettings = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        _services = ctx.Services;
        _liveSettings = liveSettings;
    }

    private EditorDocument Doc => _ctx.Document;

    /// <summary>The build settings the next build compiles with (live provider, else the services
    /// snapshot, else the editor defaults).</summary>
    public BuildSettings Settings => _liveSettings?.Invoke() ?? _services.BuildSettings ?? BuildSettings.EditorDefaults;

    public ScriptOpReport Build(bool bakeLighting, bool shadows) => BuildDetailed(bakeLighting, shadows).Report;

    /// <summary>
    /// Compiles a SEALED build (optionally baking lighting) with the shared options factory, applies
    /// it to the document, and returns both the Lua-facing report and the compiled result (so a host
    /// can refresh its own view state from it).
    /// </summary>
    public ScriptBuildOutcome BuildDetailed(bool bakeLighting, bool shadows)
    {
        var warnings = new List<string>();
        CompileOptions options = OptionsFor(bakeLighting, shadows, warnings);
        CompiledLevel result = GeometryBuildService.Build(Doc.Rfl, options);
        foreach (string w in warnings)
        {
            result.Report.Add(BuildSeverity.Warning, w);
            _ctx.Log.Warn(w);
        }

        GeometryBuildService.Apply(Doc.Rfl, result);
        Doc.MarkDirty();
        int faces = result.Geometry?.Faces.Count ?? 0;
        ScriptLightmapReport? lightmaps = bakeLighting && result.BakeStats is { } bake ? new ScriptLightmapReport(bake) : null;
        string msg = bakeLighting
            ? $"Built + lit: {faces} faces, {result.Lightmaps?.Count ?? 0} lightmap pages" +
              (lightmaps is { Charts: > 0 } l ? $", Alpine lightmaps: {l.Charts} charts on {l.Pages} pages at density {l.Density}." : ".")
            : $"Built: {faces} faces.";
        _ctx.Log.Info(msg);
        if (result.Report.SurfaceBudget?.Message() is { } cap)
        {
            _ctx.Log.Warn(cap);
        }

        ScriptRepairReport? repair = result.Report.Repair.Ran ? new ScriptRepairReport(result.Report.Repair) : null;
        return new ScriptBuildOutcome(new ScriptOpReport(true, msg)
        {
            Count = faces, Repair = repair, Lightmaps = lightmaps,
            Surfaces = result.Report.Surfaces, SurfacesWanted = result.Report.SurfaceBudget?.Wanted ?? result.Report.Surfaces,
            UnlitFaces = result.Report.UnlitFaces,
        }, result);
    }

    /// <summary>
    /// Compiles a SEALED build (never a preview — unsealed geometry reports thousands of false
    /// leaks) without applying it, and returns the world-space midpoints of any leak edges.
    /// </summary>
    public IReadOnlyList<Vec3> CheckHoles()
    {
        CompileOptions options = OptionsFor(bakeLighting: false, shadows: false, warnings: null);
        CompiledLevel result = GeometryBuildService.Build(Doc.Rfl, options);
        List<Vec3> holes = HoleDetector.Detect(result.Geometry);
        _ctx.Log.Info(holes.Count == 0 ? "No holes: geometry is watertight." : $"{holes.Count} hole(s) detected.");
        return holes;
    }

    private CompileOptions OptionsFor(bool bakeLighting, bool shadows, ICollection<string>? warnings)
    {
        if (_traits is null)
        {
            _traits = _services.TextureTraits ?? (_services.Assets is { } vfs ? new TextureTraitsCache(vfs) : null);
        }

        var request = new CompileRequest
        {
            Sealed = true,
            BakeLighting = bakeLighting,
            CastShadows = shadows,
            Cancellation = _ctx.Cancellation,
            Progress = p => Report(p.Stage, p.Current, p.Total),
            BakeProgress = bakeLighting ? p => Report(p.Stage, p.Current, p.Total) : null,
        };
        return CompileOptionsFactory.CreateForDocument(Doc, _services.Assets, Settings, request, _traits, warnings);
    }

    public void Save(string? path)
    {
        string target = path ?? Doc.Path
            ?? throw new ScriptApiException("No save path.", "Pass ops.save(\"path.rfl\") or open a file first.");
        _ctx.RequireDestructive($"save to {Path.GetFileName(target)}");
        if (Doc.SaveBlockedReason is { } blocked)
        {
            throw new ScriptApiException(blocked, "Levels newer than RFL 306 open read-only in Glacier.");
        }

        SaveDocument(target, null);
    }

    /// <summary>Writes the document to <paramref name="target"/> and logs the save notes (the gates are the caller's).</summary>
    private void SaveDocument(string target, string? why)
    {
        Doc.Save(target);
        _ctx.Log.Info(why is null ? $"Saved {Path.GetFileName(target)}." : $"Saved {Path.GetFileName(target)} {why}.");
        foreach (string note in Doc.LastSaveNotes)
        {
            // The save log: upgrade notes, a dropped Alpine lightmap chunk, a stale packfile (not when packaging, which
            // is about to rebuild that packfile).
            if (why is null || !string.Equals(note, Doc.LastStalePackfileNote, StringComparison.Ordinal))
            {
                _ctx.Log.Info(note);
            }
        }
    }

    public string CompatibilitySummary()
    {
        FeatureGateReport gate = Doc.EvaluateSaveTarget(SaveTarget.StockRf);
        return gate.Summary();
    }

    public ScriptOpReport Package(string? path, bool multiplayer)
    {
        AssetVfs vfs = _services.Assets
            ?? throw new ScriptApiException("Packaging needs a mounted asset library.", "Open a level with its game library mounted.");
        string levelPath = Doc.Path is { Length: > 0 } p ? p
            : throw new ScriptApiException("Save the level before packaging.", "The packfile is built from the saved level file and goes next to it: ops.save(\"path.rfl\") first.");
        string levelFile = Path.GetFileName(levelPath);
        _ = multiplayer; // accepted for scripts that pass it; the packfile always goes next to the level

        string outPath = path ?? PackfileBuildPlan.LevelFolderOutputPath(levelPath);
        bool saveFirst = Doc.IsDirty;
        _ctx.RequireDestructive(saveFirst
            ? $"save {levelFile} and package it to {Path.GetFileName(outPath)}"
            : $"package to {Path.GetFileName(outPath)}");
        if (saveFirst)
        {
            if (Doc.SaveBlockedReason is { } blocked)
            {
                throw new ScriptApiException(blocked, "Levels newer than RFL 306 open read-only in Glacier.");
            }

            SaveDocument(levelPath, "before packaging, so the packfile carries the level as it is now");
        }

        DependencyScanOptions opts = _services.ScanOptionsFactory?.Invoke() ?? new DependencyScanOptions();
        Report("Scanning dependencies", 0, 0);
        DependencyScanResult scan = DependencyScanner.Scan(Doc.Rfl, new VfsDependencyResolver(vfs), opts);

        // The saved file itself: the packfile and the loose level are the same bytes.
        byte[] rflBytes = File.ReadAllBytes(levelPath);
        Report("Writing package", 0, 0);
        PackfileBuildResult result = PackfileBuilder.Build(rflBytes, levelFile, scan.Included, outPath);
        string msg = $"Packaged {result.PackedFiles.Count} file(s), {result.TotalBytes / 1024} KiB → {Path.GetFileName(outPath)}.";
        _ctx.Log.Info(msg);
        return new ScriptOpReport(true, msg) { Count = result.PackedFiles.Count, Path = outPath };
    }

    public void Playtest(bool multiplayer) =>
        throw new ScriptApiException("Playtest is only available in the interactive editor.",
            "Run the level from the editor's Play button.");

    private void Report(string stage, int current, int total)
    {
        _ctx.Cancellation.ThrowIfCancellationRequested();
        _services.Progress?.Report(new ScriptProgress(stage, current, total));
    }
}

/// <summary>The outcome of <see cref="CoreScriptOperations.BuildDetailed"/>: the Lua-facing report
/// plus the compiled level that was applied to the document.</summary>
public sealed record ScriptBuildOutcome(ScriptOpReport Report, CompiledLevel Result);
