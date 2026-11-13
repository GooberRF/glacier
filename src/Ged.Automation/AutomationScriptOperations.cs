using System;
using System.Collections.Generic;
using System.IO;
using Ged.Core.Model;
using Ged.Core.Scripting;

namespace Ged.Automation;

/// <summary>
/// The <see cref="IScriptOperations"/> wrapper for script runs started through automation (the CLI,
/// the editor bridge). It keeps the host's operations (the editor's viewport-adopting builds, the
/// headless Core pipeline) and adds what automation needs:
/// <list type="bullet">
/// <item><c>ops.save</c> / <c>ops.package</c> targets are confined to the path allow-list —
/// a script cannot write outside the level's folder, <c>user_maps</c>, or the scripts library;</item>
/// <item>a headless <c>ops.save</c> goes through the host's save semantics (seal guard, timestamp rule,
/// sidecar) rather than a raw write, and builds update the host's compiled-geometry state.</item>
/// </list>
/// </summary>
public sealed class AutomationScriptOperations : IScriptOperations
{
    private readonly ScriptContext _ctx;
    private readonly Ged.Core.Editor.EditorDocument _doc;
    private readonly IScriptOperations _inner;
    private readonly PathAllowList _paths;
    private readonly Action<bool>? _onBuilt;
    private readonly Action<string, bool>? _save;

    /// <param name="ctx">The run's context (destructive gate, log).</param>
    /// <param name="doc">The run's document.</param>
    /// <param name="inner">The host's operations.</param>
    /// <param name="paths">The allow-list for save / package targets.</param>
    /// <param name="onBuilt">Called after a successful build (argument: lighting was baked).</param>
    /// <param name="save">
    /// The host save (target path, save-as) used for <c>ops.save</c>; null delegates to
    /// <paramref name="inner"/> after the allow-list check.
    /// </param>
    public AutomationScriptOperations(
        ScriptContext ctx,
        Ged.Core.Editor.EditorDocument doc,
        IScriptOperations inner,
        PathAllowList paths,
        Action<bool>? onBuilt = null,
        Action<string, bool>? save = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _onBuilt = onBuilt;
        _save = save;
    }

    public ScriptOpReport Build(bool bakeLighting, bool shadows)
    {
        ScriptOpReport report = _inner.Build(bakeLighting, shadows);
        _onBuilt?.Invoke(bakeLighting);
        return report;
    }

    public IReadOnlyList<Vec3> CheckHoles() => _inner.CheckHoles();

    public void Save(string? path)
    {
        string target = path ?? _doc.Path
            ?? throw new ScriptApiException("No save path.", "Pass ops.save(\"path.rfl\") or open a file first.");
        string full = Allowed(target, "save the level to");
        if (_save is null)
        {
            _inner.Save(full);
            return;
        }

        _ctx.RequireDestructive($"save to {Path.GetFileName(full)}");
        if (Ged.Core.Compiler.CompiledGeometryState.IsMissing(_doc.Rfl))
        {
            // Red Faction crashes loading a level with brushes and no compiled geometry: never write one.
            Build(bakeLighting: false, shadows: false);
            _ctx.Log.Warn("ops.save: the level had no compiled geometry, so it was built (sealed, unlit) before saving; call ops.light() first for lit surfaces.");
        }

        bool saveAs = _doc.Path is { } current && !PathsEqual(current, full);
        _save(full, saveAs);
        _ctx.Log.Info($"Saved {Path.GetFileName(full)}.");
        foreach (string note in _doc.LastSaveNotes)
        {
            _ctx.Log.Info(note); // the save log: upgrade notes, a dropped Alpine lightmap chunk
        }
    }

    public string CompatibilitySummary() => _inner.CompatibilitySummary();

    public ScriptOpReport Package(string? path, bool multiplayer)
    {
        if (path is not null)
        {
            path = Allowed(path, "write a package to");
        }

        return _inner.Package(path, multiplayer);
    }

    public void Playtest(bool multiplayer) => _inner.Playtest(multiplayer);

    private string Allowed(string path, string purpose)
    {
        string full = PathAllowList.Normalize(path);
        if (!_paths.IsAllowed(full))
        {
            throw new ScriptApiException(
                $"Refusing to {purpose} '{full}': scripts run from the command line may only write under the level's folder, user_maps, or the scripts library.",
                "Save inside one of those folders, or pass the path to glacier-cli with --save / --out.");
        }

        return full;
    }

    internal static bool PathsEqual(string a, string b) => string.Equals(
        PathAllowList.Normalize(a), PathAllowList.Normalize(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
