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
    [ScriptDoc("Compiles geometry and bakes lightmaps (shadows default true) with the editor's lightmap method. Returns op_report. Not in Dry-Run.", Example = "ops.light(true)")]
    public ScriptOpReport Light(bool shadows = true)
    {
        GuardDryRun("light");
        _ctx.SyncStaleMoverCopies();
        using (_ctx.EnterHostOperation())
        {
            return _ctx.Operations.Build(bakeLighting: true, shadows: shadows);
        }
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

    /// <summary>Lua: <c>ops.package([path], multiplayer?)</c> — builds a .vpp (destructive).</summary>
    [ScriptDoc("Builds a .vpp package of the level and its custom assets (destructive), optionally to path and as multiplayer. Returns op_report.", Example = "ops.package(nil, true)")]
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
