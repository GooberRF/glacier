using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>ops</c> global: build / light / hole-check / save / package / playtest (plan §5.5).
/// Each forwards to the <see cref="IScriptOperations"/> backend (progress + cancellation) and is
/// an opaque operation — scripts never drive the compiler/baker per-element (§2.9).
/// </summary>
public sealed class ScriptOps
{
    private readonly ScriptContext _ctx;

    internal ScriptOps(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>ops.build()</c> — compiles geometry and applies it (no lighting bake).</summary>
    public ScriptOpReport Build()
    {
        GuardDryRun("build");
        return _ctx.Operations.Build(bakeLighting: false, shadows: false);
    }

    /// <summary>Lua: <c>ops.light(shadows?)</c> — compiles geometry and bakes lightmaps.</summary>
    public ScriptOpReport Light(bool shadows = true)
    {
        GuardDryRun("light");
        return _ctx.Operations.Build(bakeLighting: true, shadows: shadows);
    }

    /// <summary>Lua: <c>ops.check_holes()</c> — returns the number of leaks (positions logged).</summary>
    public int CheckHoles()
    {
        IReadOnlyList<Vec3> holes = _ctx.Operations.CheckHoles();
        foreach (Vec3 h in holes.Take(50))
        {
            _ctx.Log.Warn($"  hole near {h}");
        }

        return holes.Count;
    }

    /// <summary>Lua: <c>ops.save([path])</c> — saves the document (destructive).</summary>
    public void Save(string? path = null) => _ctx.Operations.Save(path);

    /// <summary>Lua: <c>ops.compat()</c> — a stock-RF compatibility summary (read-only).</summary>
    public string Compat() => _ctx.Operations.CompatibilitySummary();

    /// <summary>Lua: <c>ops.package([path], multiplayer?)</c> — builds a .vpp (destructive).</summary>
    public ScriptOpReport Package(string? path = null, bool multiplayer = false)
    {
        GuardDryRun("package");
        return _ctx.Operations.Package(path, multiplayer);
    }

    /// <summary>Lua: <c>ops.playtest(multiplayer?)</c> — launches the level in-game (destructive).</summary>
    public void Playtest(bool multiplayer = false)
    {
        GuardDryRun("playtest");
        _ctx.Operations.Playtest(multiplayer);
    }

    private void GuardDryRun(string what)
    {
        if (_ctx.DryRun)
        {
            throw new ScriptApiException($"'{what}' is disabled in dry-run mode.", "Run the script for real to allow it.");
        }
    }
}
