using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>ops</c> global: build / light / hole-check / save / package / playtest.
/// Each forwards to the <see cref="IScriptOperations"/> backend (progress + cancellation) and is
/// an opaque operation — scripts never drive the compiler/baker per-element. Every operation
/// runs as a host operation (<see cref="ScriptContext.EnterHostOperation"/>): its duration does not
/// count toward the script's wall-clock timeout, while the run's cancellation still applies.
/// </summary>
public sealed class ScriptOps
{
    private readonly ScriptContext _ctx;

    internal ScriptOps(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>ops.build()</c> — compiles geometry and applies it (no lighting bake).</summary>
    [ScriptDoc("Compiles a sealed build with the editor's build method and hole-repair setting and applies it (no lighting). Returns op_report (count = faces; repair = repair_report or nil). Not in Dry-Run.", Example = "local r = ops.build(); if r.repair then log.info(r.repair.wall_gaps_filled) end")]
    public ScriptOpReport Build()
    {
        GuardDryRun("build");
        _ctx.SyncStaleMoverCopies();
        using (_ctx.EnterHostOperation())
        {
            return _ctx.Operations.Build(bakeLighting: false, shadows: false);
        }
    }

    /// <summary>Lua: <c>ops.light(shadows?)</c> — compiles geometry and bakes lightmaps.</summary>
    [ScriptDoc("Compiles and bakes lightmaps (shadows default true) with the lightmap method and the level's pipeline (ops.lightmaps). Returns op_report (lightmaps = lightmap_report). Not in Dry-Run.", Example = "local r = ops.light(true); log.info(r.lightmaps.pipeline)")]
    public ScriptOpReport Light(bool shadows = true)
    {
        GuardDryRun("light");
        _ctx.SyncStaleMoverCopies();
        using (_ctx.EnterHostOperation())
        {
            return _ctx.Operations.Build(bakeLighting: true, shadows: shadows);
        }
    }

    /// <summary>
    /// Lua: <c>ops.lightmaps(pipeline?, density?, codec?)</c> — reads, or sets, the level's lightmap pipeline as Alpine RED
    /// stores it (level properties <c>legacy_lighting</c> / <c>lightmap_density</c>, editor_patch/level.h:992-1005), and
    /// the layer codec the next bake writes ("bc7", or "raw" like Alpine RED's <c>-rawlightmaps</c>; not stored).
    /// The next ops.light bakes with it, exactly as Calculate Lighting in the editor does.
    /// </summary>
    [ScriptDoc("Reads or sets the lightmap pipeline (\"alpine\" or \"legacy\"), density (0 = 8 per unit, 1-128, 255 = off, -1 keeps) and bake codec (\"bc7\" or \"raw\"; not saved). Returns lightmap_settings.", Example = "ops.lightmaps(\"alpine\", 16); ops.light()", Since = 2)]
    public ScriptLightmapSettings Lightmaps(string? pipeline = null, double density = -1, string? codec = null)
    {
        if (codec is not null)
        {
            _ctx.Document.Rfl.AlpineLightmapCodec = Lighting.Alpine.AlpineLightmapSettings.ParseCodec(codec)
                ?? throw new ScriptApiException($"Unknown lightmap codec '{codec}'.", "Use \"bc7\" or \"raw\".");
        }

        Lighting.Alpine.LightmapPipeline? p = null;
        if (pipeline is not null)
        {
            p = Lighting.Alpine.AlpineLightmapSettings.Parse(pipeline)
                ?? throw new ScriptApiException($"Unknown lightmap pipeline '{pipeline}'.", "Use \"alpine\" or \"legacy\".");
        }

        int? d = null;
        if (density >= 0)
        {
            if (density != System.Math.Floor(density) || density > 255)
            {
                throw new ScriptApiException($"Lightmap density {density} is not a whole number from 0 to 255.", "0 = default (8), 1-128 texels per unit, 255 = Alpine charts off.");
            }

            d = (int)density;
        }

        if (p is not null || d is not null)
        {
            GuardDryRun("lightmaps");
            if (Lighting.Alpine.AlpineLightmapSettings.Apply(_ctx.Document, p, d))
            {
                _ctx.Document.MarkDirty();
            }
        }

        return new ScriptLightmapSettings(Lighting.Alpine.AlpineLightmapSettings.FromLevel(_ctx.Document.Rfl));
    }

    /// <summary>Lua: <c>ops.check_holes()</c> — returns the number of leaks (positions logged).
    /// Always measured on a sealed build. Use <c>ops.holes()</c> for the positions.</summary>
    [ScriptDoc("Runs the leak check on a sealed build (never a preview) and returns the hole count; positions go to the log. ops.holes() returns them.", Example = "if ops.check_holes() > 0 then error(\"level leaks\") end")]
    public int CheckHoles()
    {
        IReadOnlyList<Vec3> holes = RunCheckHoles();
        foreach (Vec3 h in holes.Take(50))
        {
            _ctx.Log.Warn($"  hole near {h}");
        }

        return holes.Count;
    }

    /// <summary>
    /// Lua: <c>ops.holes()</c> — runs the same sealed-build leak check as <c>ops.check_holes()</c> and
    /// returns a <c>hole_report</c>: <c>count</c>, <c>is_watertight</c>, and <c>positions</c> (one world-space
    /// <c>vec</c> per leak edge midpoint, in metres).
    /// </summary>
    [ScriptDoc("Runs the leak check on a sealed build and returns hole_report (count, is_watertight, positions). Does not change the level.", Example = "assert(ops.holes().is_watertight)")]
    public ScriptHoleReport Holes() => new(RunCheckHoles());

    /// <summary>Lua: <c>ops.save([path])</c> — saves the document (destructive).</summary>
    [ScriptDoc("Saves the level, optionally to path (destructive: needs confirmation or --@allow-destructive; refused in Dry-Run).", Example = "ops.save()")]
    public void Save(string? path = null)
    {
        _ctx.SyncStaleMoverCopies();
        using (_ctx.EnterHostOperation())
        {
            _ctx.Operations.Save(path);
        }
    }

    /// <summary>Lua: <c>ops.compat()</c> — a stock-RF compatibility summary (read-only).</summary>
    [ScriptDoc("A plain-text summary of what in the level needs Alpine Faction rather than stock RF. Read-only.", Example = "log.info(ops.compat())")]
    public string Compat()
    {
        using (_ctx.EnterHostOperation())
        {
            return _ctx.Operations.CompatibilitySummary();
        }
    }

    /// <summary>
    /// Lua: <c>ops.package([path], multiplayer?)</c> — builds <c>&lt;level&gt;.vpp</c> next to the saved level (or at
    /// <c>path</c>), replacing an existing one in place (destructive). It packs the saved level file, so unsaved changes
    /// are saved first; <c>multiplayer</c> has no effect.
    /// </summary>
    [ScriptDoc("Builds <level>.vpp beside the saved level (or at path) from the level file and its custom assets, saving unsaved changes first (destructive). multiplayer has no effect. Returns op_report.", Example = "ops.package()")]
    public ScriptOpReport Package(string? path = null, bool multiplayer = false)
    {
        GuardDryRun("package");
        _ctx.SyncStaleMoverCopies();
        using (_ctx.EnterHostOperation())
        {
            return _ctx.Operations.Package(path, multiplayer);
        }
    }

    /// <summary>Lua: <c>ops.playtest(multiplayer?)</c> — launches the level in-game (destructive).</summary>
    [ScriptDoc("Saves and launches the level in-game once the script finishes (destructive; interactive editor only, an error elsewhere).", Example = "ops.playtest(false)")]
    public void Playtest(bool multiplayer = false)
    {
        GuardDryRun("playtest");
        using (_ctx.EnterHostOperation())
        {
            _ctx.Operations.Playtest(multiplayer);
        }
    }

    /// <summary>The sealed-build leak check, timed as a host operation (excluded from the script timeout).</summary>
    private IReadOnlyList<Vec3> RunCheckHoles()
    {
        using (_ctx.EnterHostOperation())
        {
            return _ctx.Operations.CheckHoles();
        }
    }

    private void GuardDryRun(string what)
    {
        if (_ctx.DryRun)
        {
            throw new ScriptApiException($"'{what}' is disabled in dry-run mode.", "Run the script for real to allow it.");
        }
    }
}

/// <summary>The result of <c>ops.holes()</c>: the leak edges a sealed build leaves open.</summary>
public sealed class ScriptHoleReport
{
    private readonly Vec3[] _positions;

    internal ScriptHoleReport(IReadOnlyList<Vec3> positions) => _positions = positions.ToArray();

    /// <summary>Lua: <c>report.count</c> — the number of leak edges.</summary>
    [ScriptDoc("The number of leak edges (0 = watertight).")]
    public int Count => _positions.Length;

    /// <summary>Lua: <c>report.is_watertight</c> — true when no leak was found.</summary>
    [ScriptDoc("True when the sealed build has no leak edges.")]
    public bool IsWatertight => _positions.Length == 0;

    /// <summary>Lua: <c>report.positions</c> — world-space midpoint of each leak edge (metres).</summary>
    [ScriptDoc("The world-space midpoint of each leak edge (metres), as vec[].")]
    public Vec3[] Positions => (Vec3[])_positions.Clone();

    public override string ToString() => IsWatertight ? "watertight" : $"{Count} hole(s)";
}
