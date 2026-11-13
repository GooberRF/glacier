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
        string msg = bakeLighting
            ? $"Built + lit: {faces} faces, {result.Lightmaps?.Count ?? 0} lightmap pages."
            : $"Built: {faces} faces.";
        _ctx.Log.Info(msg);
        ScriptRepairReport? repair = result.Report.Repair.Ran ? new ScriptRepairReport(result.Report.Repair) : null;
        return new ScriptBuildOutcome(new ScriptOpReport(true, msg) { Count = faces, Repair = repair }, result);
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

        Doc.Save(target);
        _ctx.Log.Info($"Saved {Path.GetFileName(target)}.");
        foreach (string note in Doc.LastSaveNotes)
        {
            _ctx.Log.Info(note); // the save log: upgrade notes, a dropped Alpine lightmap chunk
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
        string levelFile = Doc.Path is { Length: > 0 } p ? Path.GetFileName(p)
            : throw new ScriptApiException("Save the level before packaging.");

        string outPath = path ?? DefaultPackagePath(levelFile, multiplayer);
        _ctx.RequireDestructive($"package to {Path.GetFileName(outPath)}");

        DependencyScanOptions opts = _services.ScanOptionsFactory?.Invoke() ?? new DependencyScanOptions();
        Report("Scanning dependencies", 0, 0);
        DependencyScanResult scan = DependencyScanner.Scan(Doc.Rfl, new VfsDependencyResolver(vfs), opts);
        foreach (string note in Doc.Rfl.ReconcileAlpineLightmaps())
        {
            _ctx.Log.Info(note); // a stale Alpine lightmap chunk is not packaged
        }

        byte[] rflBytes = Doc.SaveToBytes();
        Report("Writing package", 0, 0);
        PackfileBuildResult result = PackfileBuilder.Build(rflBytes, levelFile, scan.Included, outPath);
        string msg = $"Packaged {result.PackedFiles.Count} file(s), {result.TotalBytes / 1024} KiB → {Path.GetFileName(outPath)}.";
        _ctx.Log.Info(msg);
        return new ScriptOpReport(true, msg) { Count = result.PackedFiles.Count, Path = outPath };
    }

    public void Playtest(bool multiplayer) =>
        throw new ScriptApiException("Playtest is only available in the interactive editor.",
            "Run the level from the editor's Play button.");

    private string DefaultPackagePath(string levelFile, bool multiplayer)
    {
        if (_services.InstallDirectory is { Length: > 0 } dir)
        {
            return PackfileBuildPlan.DefaultOutputPath(dir, levelFile, multiplayer);
        }

        // Fall back to next to the level file.
        string baseDir = Doc.Path is { Length: > 0 } p ? Path.GetDirectoryName(p)! : Directory.GetCurrentDirectory();
        return Path.Combine(baseDir, Path.GetFileNameWithoutExtension(levelFile) + ".vpp");
    }

    private void Report(string stage, int current, int total)
    {
        _ctx.Cancellation.ThrowIfCancellationRequested();
        _services.Progress?.Report(new ScriptProgress(stage, current, total));
    }
}

/// <summary>The outcome of <see cref="CoreScriptOperations.BuildDetailed"/>: the Lua-facing report
/// plus the compiled level that was applied to the document.</summary>
public sealed record ScriptBuildOutcome(ScriptOpReport Report, CompiledLevel Result);
