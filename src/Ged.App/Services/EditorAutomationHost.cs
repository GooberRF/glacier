using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Ged.Automation;
using Ged.Core;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Packaging;
using Ged.Core.Scripting;

namespace Ged.App.Services;

/// <summary>
/// What the editor's automation host needs from the shell (implemented by <see cref="MainWindow"/>;
/// tests supply a window-less stand-in). Every member runs on the UI thread.
/// </summary>
internal interface IEditorAutomationShell
{
    /// <summary>The editor session (document, brush editor, mounted library).</summary>
    EditorSession Session { get; }

    /// <summary>The build controller (null before the shell is fully initialized).</summary>
    GeometryBuildController? BuildController { get; }

    /// <summary>The scripting service — script runs go through it so output reaches the Script Log.</summary>
    ScriptingService Scripting { get; }

    /// <summary>Opens a level exactly like File ▸ Open, without any dialog (no recovery prompt, no install picker).</summary>
    Task OpenLevelAsync(string path);

    /// <summary>Creates a new level exactly like File ▸ New, without any dialog.</summary>
    Task NewLevelAsync();

    /// <summary>The File ▸ Save sequence for an already-chosen path (no dialog).</summary>
    Task<EditorSaveResult> SaveToPathAsync(string path, bool saveAs);

    /// <summary>Draws hole markers (no camera jump).</summary>
    void ShowHoleMarkers(IReadOnlyList<Vec3> holes);

    /// <summary>Refreshes the scene + panels after an external change to the document.</summary>
    void AfterExternalChange();

    /// <summary>Writes one activity line to the Log panel and the status bar.</summary>
    void LogActivity(string message);
}

/// <summary>
/// The running editor as an automation host (attach mode): commands from
/// <c>glacier-cli --attach</c> execute against the LIVE document through the editor's own services —
/// script runs through <see cref="ScriptingService"/> (Script Log output, viewport refresh, one undo
/// step labelled "CLI: &lt;script&gt;"), builds through the shared factory and adopted by the
/// <see cref="GeometryBuildController"/> exactly like a script build (viewport, Build Output, dirty
/// state), saves through File ▸ Save's sequence. Nothing here shows a dialog.
/// </summary>
internal sealed class EditorAutomationHost : IAutomationHost
{
    private readonly IEditorAutomationShell _shell;

    public EditorAutomationHost(IEditorAutomationShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        PathPolicy = new PathAllowList(AllowedRoots);
    }

    public string Mode => "editor";

    public EditorDocument? Document => _shell.Session.Document;

    public AssetVfs? Assets => _shell.Session.Vfs;

    public string? RfInstallDir => _shell.Session.RfInstallDir;

    public BuildSettings BuildSettings => _shell.BuildController?.CurrentBuildSettings ?? BuildSettings.EditorDefaults;

    public LevelBuildState BuildState => _shell.BuildController is { } b
        ? new LevelBuildState(b.GeometryDirty, b.GeometryIsPreview, b.LightingDirty)
        : default;

    public PathAllowList PathPolicy { get; }

    public DependencyScanOptions CreateScanOptions() => _shell.Session.BuildScanOptions();

    public Task<T> RunOnDocumentThreadAsync<T>(Func<Task<T>> work, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);
    }

    public Task OpenAsync(string path, CancellationToken cancellation)
    {
        RequireIdle("open a level");
        return _shell.OpenLevelAsync(path);
    }

    public Task NewAsync(CancellationToken cancellation)
    {
        RequireIdle("create a new level");
        return _shell.NewLevelAsync();
    }

    public ScriptRunResult RunScript(AutomationScriptRun run, Action<ScriptLogEntry> capture, CancellationToken cancellation)
    {
        EditorDocument doc = RequireDocument();
        RequireIdle("run a script");
        ScriptRunResult result = _shell.Scripting.Run(
            run.Source,
            run.Name,
            run.DryRun,
            run.AllowDestructive,
            cancellation,
            new ScriptRunExtras
            {
                UndoLabel = run.UndoLabel,
                Limits = run.Limits,
                LogTap = capture,
                WrapOperations = (ctx, inner) => new AutomationScriptOperations(ctx, doc, inner, PathPolicy),
                AutoTags = run.AutoTags,
            });

        if (result.Committed)
        {
            _shell.AfterExternalChange(); // outliner/properties/history, on top of the service's scene rebuild
        }

        return result;
    }

    public Ged.Core.Editing.BrushEditor? Brushes => _shell.Session.BrushEditor;

    public async Task<CompiledLevel> BuildAsync(bool sealedBuild, bool bakeLighting, bool shadows, CancellationToken cancellation, IProgress<AutomationProgress>? progress = null)
    {
        EditorDocument doc = RequireDocument();
        RequireIdle("build");
        GeometryBuildController? controller = _shell.BuildController;
        BuildSettings settings = BuildSettings;
        AssetVfs? vfs = Assets;
        TextureTraitsCache? traits = controller?.TextureTraits;

        // The build bakes the movers-section copies: refresh any whose member brush was edited (on the UI
        // thread, folded into the latest undo step) before compiling.
        using (controller?.ExternalApplyScope())
        {
            doc.SyncStaleMoverCopies();
        }

        // Compile off the UI thread (as the toolbar build does), then apply + adopt on it.
        CompiledLevel result = await Task.Run(
            () => LevelOperations.Compile(doc, vfs, settings, sealedBuild, bakeLighting, shadows, traits, cancellation, progress),
            cancellation);

        using (controller?.ExternalApplyScope())
        {
            LevelOperations.Apply(doc, result);
        }

        bool preview = !(sealedBuild || bakeLighting);
        if (controller is not null)
        {
            controller.AdoptExternalBuild(result, bakeLighting, preview);
        }
        else
        {
            _shell.AfterExternalChange();
        }

        return result;
    }

    public async Task<IReadOnlyList<Vec3>> CheckHolesAsync(CancellationToken cancellation, IProgress<AutomationProgress>? progress = null)
    {
        EditorDocument doc = RequireDocument();
        RequireIdle("check for holes");
        BuildSettings settings = BuildSettings;
        AssetVfs? vfs = Assets;
        TextureTraitsCache? traits = _shell.BuildController?.TextureTraits;
        IReadOnlyList<Vec3> holes = await Task.Run(() =>
        {
            CompiledLevel sealedResult = LevelOperations.Compile(doc, vfs, settings, sealedBuild: true, bakeLighting: false, shadows: false, traits, cancellation, progress);
            return (IReadOnlyList<Vec3>)HoleDetector.Detect(sealedResult.Geometry);
        }, cancellation);

        _shell.BuildController?.AdoptExternalHoleCheck(holes);
        _shell.ShowHoleMarkers(holes);
        return holes;
    }

    public async Task<HostSaveOutcome> SaveAsync(string path, bool saveAs, CancellationToken cancellation)
    {
        EditorDocument doc = RequireDocument();
        bool stamp = LevelSavePolicy.StampsTimestamp(saveAs, doc.IsDirty, BuildState);
        EditorSaveResult r = await _shell.SaveToPathAsync(path, saveAs);
        if (!r.Written)
        {
            if (r.Error is { } error)
            {
                throw new AutomationException(AutomationErrorCodes.Io, $"Could not write '{path}': {error}");
            }

            throw new AutomationException(
                r.Seal == LevelSaveSealOutcome.AbortBuildRunning ? AutomationErrorCodes.Busy : AutomationErrorCodes.SaveFailed,
                r.Seal == LevelSaveSealOutcome.AbortBuildRunning
                    ? "Save aborted — a build is already running in the editor. Retry when it finishes."
                    : "Save aborted — the geometry re-seal did not complete.");
        }

        return new HostSaveOutcome(path, saveAs, stamp, r.Notice);
    }

    public void ReportActivity(string message) => _shell.LogActivity(message);

    public IReadOnlyList<string> RecentUndoLabels(int max) =>
        Document is { } doc ? UndoHistory.Recent(doc.Undo, max) : Array.Empty<string>();

    /// <summary>Undoes through the build controller exactly like Edit ▸ Undo (stale-build tracking, live preview), then refreshes the panels.</summary>
    public Task UndoAsync(int steps, CancellationToken cancellation)
    {
        EditorDocument doc = RequireDocument();
        RequireIdle("undo");
        for (int i = 0; i < steps && doc.Undo.CanUndo; i++)
        {
            if (_shell.BuildController is { } controller)
            {
                controller.ApplyUndoRedo(redo: false, coalesce: true);
            }
            else
            {
                doc.Undo.Undo();
            }
        }

        _shell.AfterExternalChange();
        return Task.CompletedTask;
    }

    /// <summary>Redoes through the build controller exactly like Edit ▸ Redo, then refreshes the panels.</summary>
    public Task RedoAsync(int steps, CancellationToken cancellation)
    {
        EditorDocument doc = RequireDocument();
        RequireIdle("redo");
        for (int i = 0; i < steps && doc.Undo.CanRedo; i++)
        {
            if (_shell.BuildController is { } controller)
            {
                controller.ApplyUndoRedo(redo: true, coalesce: true);
            }
            else
            {
                doc.Undo.Redo();
            }
        }

        _shell.AfterExternalChange();
        return Task.CompletedTask;
    }

    private EditorDocument RequireDocument() =>
        Document ?? throw new AutomationException(AutomationErrorCodes.NoDocument,
            "No level is open in the editor. Use 'new' or 'open' first (or pass a level).");

    private void RequireIdle(string what)
    {
        if (_shell.BuildController is { IsUserBuildRunning: true })
        {
            throw new AutomationException(AutomationErrorCodes.Busy,
                $"A build is already running in the editor; cannot {what} now. Retry when it finishes.");
        }
    }

    private IEnumerable<string?> AllowedRoots()
    {
        yield return Document?.Path is { Length: > 0 } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null;
        yield return RfInstallDir is { Length: > 0 } rf ? Path.Combine(rf, "user_maps") : null;
        yield return AppPaths.ScriptsDirectory;
    }
}
