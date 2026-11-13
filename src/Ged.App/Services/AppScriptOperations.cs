using System;
using System.Collections.Generic;
using Ged.Core.Model;
using Ged.Core.Playtest;
using Ged.Core.Scripting;

namespace Ged.App.Services;

/// <summary>
/// The editor's <see cref="IScriptOperations"/> backend for script runs. It wraps the headless
/// <see cref="CoreScriptOperations"/> — so builds, hole checks, saves and packaging compile through
/// the same shared options factory with the user's actual build settings (supplied via
/// <see cref="ScriptServices.BuildSettings"/>) — and adds what only the interactive editor can do:
/// <list type="bullet">
/// <item>after <c>ops.build</c> / <c>ops.light</c>, the build controller adopts the applied result
/// (dirty flags, brush-overlay stash, viewport, Build Output) exactly as after a toolbar build;</item>
/// <item>hole checks update the editor's hole markers;</item>
/// <item><c>ops.playtest</c> queues the Play-button flow (save, stage, launch) to run once the script
/// finishes — scripts run synchronously on the UI thread, and the launch saves the post-script
/// document.</item>
/// </list>
/// </summary>
internal sealed class AppScriptOperations : IScriptOperations
{
    private readonly ScriptContext _ctx;
    private readonly CoreScriptOperations _core;
    private readonly GeometryBuildController? _builds;
    private readonly Action<IReadOnlyList<Vec3>>? _showHoles;
    private readonly Action<PlaytestMode>? _queuePlaytest;

    public AppScriptOperations(
        ScriptContext ctx,
        GeometryBuildController? builds,
        Action<IReadOnlyList<Vec3>>? showHoles,
        Action<PlaytestMode>? queuePlaytest)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        // Read the user's build settings live: a console session outlives settings changes.
        _core = new CoreScriptOperations(ctx, builds is null ? null : () => builds.CurrentBuildSettings);
        _builds = builds;
        _showHoles = showHoles;
        _queuePlaytest = queuePlaytest;
    }

    public ScriptOpReport Build(bool bakeLighting, bool shadows)
    {
        if (_builds is { IsUserBuildRunning: true })
        {
            throw new ScriptApiException(
                "A build is already running in the editor.",
                "Wait for the current build or lighting bake to finish, then run the script again.");
        }

        ScriptBuildOutcome outcome;
        using (_builds?.ExternalApplyScope())
        {
            outcome = _core.BuildDetailed(bakeLighting, shadows);
        }

        _builds?.AdoptExternalBuild(outcome.Result, bakeLighting);
        return outcome.Report;
    }

    public IReadOnlyList<Vec3> CheckHoles()
    {
        IReadOnlyList<Vec3> holes = _core.CheckHoles();
        _builds?.AdoptExternalHoleCheck(holes);
        _showHoles?.Invoke(holes);
        return holes;
    }

    public void Save(string? path) => _core.Save(path);

    public string CompatibilitySummary() => _core.CompatibilitySummary();

    public ScriptOpReport Package(string? path, bool multiplayer) => _core.Package(path, multiplayer);

    public void Playtest(bool multiplayer)
    {
        if (_queuePlaytest is null)
        {
            _core.Playtest(multiplayer); // headless message
            return;
        }

        // The Play flow saves the level first, so it is gated like ops.save.
        _ctx.RequireDestructive("save the level and launch a playtest");
        _queuePlaytest(multiplayer ? PlaytestMode.Multi : PlaytestMode.Single);
        _ctx.Log.Info($"Playtest ({(multiplayer ? "multiplayer" : "single-player")}) will launch when the script finishes.");
    }
}
