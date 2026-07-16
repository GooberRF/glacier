using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Ged.Core.Packaging;

namespace Ged.Core.Scripting;

/// <summary>
/// The default, pure-Core operation backend: drives <c>GeometryBuildService</c>,
/// <c>LevelLighting</c>, <c>HoleDetector</c>, <c>EditorDocument.Save</c>, and
/// <c>PackfileBuilder</c> directly, forwarding progress to the context's progress sink + log and
/// honoring the run's cancellation token (plan §5.5). It never reimplements the compiler/baker —
/// it invokes them as opaque, cancelable operations (§2.9). Playtest requires the interactive
/// editor and is rejected here.
/// </summary>
public sealed class CoreScriptOperations : IScriptOperations
{
    private readonly ScriptContext _ctx;
    private readonly ScriptServices _services;

    internal CoreScriptOperations(ScriptContext ctx, ScriptServices services)
    {
        _ctx = ctx;
        _services = services;
    }

    private EditorDocument Doc => _ctx.Document;

    public ScriptOpReport Build(bool bakeLighting, bool shadows)
    {
        var options = new CompileOptions
        {
            Cancellation = _ctx.Cancellation,
            Progress = p => Report(p.Stage, p.Current, p.Total),
            BakeLighting = bakeLighting,
        };
        if (bakeLighting)
        {
            options.Lighting = new LightingOptions
            {
                CastShadows = shadows,
                Cancellation = _ctx.Cancellation,
                Progress = p => Report(p.Stage, p.Current, p.Total),
            };
        }

        CompiledLevel result = GeometryBuildService.BuildAndApply(Doc.Rfl, options);
        Doc.MarkDirty();
        int faces = result.Geometry?.Faces.Count ?? 0;
        string msg = bakeLighting
            ? $"Built + lit: {faces} faces, {result.Lightmaps?.Count ?? 0} lightmap pages."
            : $"Built: {faces} faces.";
        _ctx.Log.Info(msg);
        return new ScriptOpReport(true, msg) { Count = faces };
    }

    public IReadOnlyList<Vec3> CheckHoles()
    {
        var options = new CompileOptions
        {
            Cancellation = _ctx.Cancellation,
            Progress = p => Report(p.Stage, p.Current, p.Total),
        };
        CompiledLevel result = GeometryBuildService.Build(Doc.Rfl, options);
        List<Vec3> holes = HoleDetector.Detect(result.Geometry);
        _ctx.Log.Info(holes.Count == 0 ? "No holes: geometry is watertight." : $"{holes.Count} hole(s) detected.");
        return holes;
    }

    public void Save(string? path)
    {
        string target = path ?? Doc.Path
            ?? throw new ScriptApiException("No save path.", "Pass ops.save(\"path.rfl\") or open a file first.");
        _ctx.RequireDestructive($"save to {Path.GetFileName(target)}");
        Doc.Save(target);
        _ctx.Log.Info($"Saved {Path.GetFileName(target)}.");
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
