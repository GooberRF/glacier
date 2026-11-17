using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Ged.Core.Packaging;
using Ged.Core.Scripting;
using Ged.Scripting;

namespace Ged.Automation;

/// <summary>
/// A window-less automation session: one <see cref="EditorDocument"/> (opened from a file or created
/// from the File â–¸ New template), the game asset library mounted from the resolved RF install, and the
/// user's editor settings â€” so a headless build, script run, lint, or save produces exactly what the
/// editor would. It tracks the same compiled-geometry state the editor's build controller does
/// (brushes changed since the last build, unsealed preview applied, lighting changed since the last
/// bake), which drives the shared save rules (<see cref="LevelSavePolicy"/>): a save after a preview
/// build re-seals first, exactly like File â–¸ Save.
/// </summary>
public sealed class HeadlessAutomationHost : IAutomationHost, IDisposable
{
    private readonly EditorSettingsSnapshot _settings;
    private readonly Func<DateTime>? _clock;
    private readonly ScriptRunner _runner = new(new MoonSharpHost());
    private EditorDocument? _doc;
    private BrushEditor? _brushes;
    private TextureTraitsCache? _traits;
    private LightingMethod? _levelLighting;
    private long _builtFingerprint;
    private bool _geometryIsPreview;
    private bool _lightingDirty;
    private bool _staleAtOpen;
    private bool _assetsShared;
    private IDisposable? _sharedRelease;

    /// <param name="settings">The editor settings to behave like (default: the fresh-install defaults).</param>
    /// <param name="rfInstallDir">The RF install to mount (null = none; textures/catalogs unavailable).</param>
    /// <param name="clock">The time a new level's level_info is stamped with (default: now).</param>
    public HeadlessAutomationHost(EditorSettingsSnapshot? settings = null, string? rfInstallDir = null, Func<DateTime>? clock = null)
    {
        _settings = settings ?? EditorSettingsSnapshot.Defaults;
        _clock = clock;
        PathPolicy = new PathAllowList(AllowedRoots);
        if (!string.IsNullOrWhiteSpace(rfInstallDir))
        {
            Mount(rfInstallDir!);
        }
    }

    /// <inheritdoc />
    public string Mode => "headless";

    /// <inheritdoc />
    public EditorDocument? Document => _doc;

    /// <inheritdoc />
    public AssetVfs? Assets { get; private set; }

    /// <inheritdoc />
    public string? RfInstallDir { get; private set; }

    /// <summary>The settings this session behaves like.</summary>
    public EditorSettingsSnapshot Settings => _settings;

    /// <summary>The per-level lightmap method override loaded from the level's sidecar (null = global default).</summary>
    public LightingMethod? LevelLightingMethod => _levelLighting;

    /// <inheritdoc />
    public BuildSettings BuildSettings => _settings.ToBuildSettings(_levelLighting);

    /// <inheritdoc />
    public LevelBuildState BuildState => _doc is null
        ? default
        : new LevelBuildState(_staleAtOpen || LevelOperations.BrushFingerprint(_doc) != _builtFingerprint, _geometryIsPreview, _lightingDirty);

    /// <inheritdoc />
    public PathAllowList PathPolicy { get; }

    /// <summary>Mounts an RF install (throws <see cref="AutomationException"/> when it is not a folder).</summary>
    public void Mount(string rfInstallDir)
    {
        if (!Directory.Exists(rfInstallDir))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"RF install directory not found: {rfInstallDir}");
        }

        ReleaseAssets();
        Assets = GameMount.Mount(rfInstallDir);
        RfInstallDir = rfInstallDir;
        _traits = null;
    }

    /// <summary>
    /// Uses an install mount this session does not own (a <see cref="Rendering.RenderMountCache"/> lease): the
    /// mount is never disposed by this session; <paramref name="release"/> (the lease) is disposed instead when
    /// the session is disposed or mounts something else.
    /// </summary>
    public void MountShared(AssetVfs assets, string rfInstallDir, TextureTraitsCache? traits, IDisposable? release)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrEmpty(rfInstallDir);
        ReleaseAssets();
        Assets = assets;
        RfInstallDir = rfInstallDir;
        _traits = traits;
        _sharedRelease = release;
        _assetsShared = true;
    }

    private void ReleaseAssets()
    {
        if (_assetsShared)
        {
            _sharedRelease?.Dispose();
        }
        else
        {
            Assets?.Dispose();
        }

        _assetsShared = false;
        _sharedRelease = null;
        Assets = null;
    }

    /// <inheritdoc />
    public DependencyScanOptions CreateScanOptions() => LevelOperations.ScanOptions(Assets, _doc?.Path);

    /// <inheritdoc />
    public Task<T> RunOnDocumentThreadAsync<T>(Func<Task<T>> work, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellation.ThrowIfCancellationRequested();
        return work();
    }

    /// <inheritdoc />
    public Task OpenAsync(string path, CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"Level file not found: {path}");
        }

        EditorDocument doc;
        try
        {
            doc = EditorDocument.Open(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable, truncated, or not an RFL at all: a file problem, not an internal failure.
            throw new AutomationException(AutomationErrorCodes.Io, $"Could not read level '{path}': {ex.Message}", ex);
        }

        // The editor loads the sidecar on open: annotations survive a save, and the per-level
        // lightmap method drives bakes.
        LevelSidecar sidecar = LevelSidecarStore.Load(LevelSidecarStore.SidecarPathFor(doc.Path!));
        doc.SetAnnotations(sidecar.Annotations);
        Adopt(doc, sidecar.Lighting);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adopts a copy of another session's level (the editor's <c>snapshot</c>): the serialized level
    /// bytes plus that session's compiled-geometry state, so build decisions match the source. The copy
    /// is private â€” nothing done to it reaches the source document.
    /// </summary>
    public void OpenSnapshot(byte[] data, string? path, LevelBuildState state)
    {
        ArgumentNullException.ThrowIfNull(data);
        EditorDocument doc;
        try
        {
            doc = EditorDocument.OpenBytes(data, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"Could not read the editor's level: {ex.Message}", ex);
        }

        Adopt(doc, levelLighting: null);
        _staleAtOpen = state.GeometryDirty;
        _geometryIsPreview = state.GeometryIsPreview;
        _lightingDirty = state.LightingDirty;
    }

    /// <inheritdoc />
    public Task NewAsync(CancellationToken cancellation)
    {
        Adopt(LevelTemplates.CreateNew(_clock?.Invoke()), levelLighting: null);
        return Task.CompletedTask;
    }

    private void Adopt(EditorDocument doc, LightingMethod? levelLighting)
    {
        _doc = doc;
        _brushes = new BrushEditor(doc);
        _levelLighting = levelLighting;
        _builtFingerprint = LevelOperations.BrushFingerprint(doc);
        _geometryIsPreview = false;
        _lightingDirty = false;
        _staleAtOpen = false;
    }

    /// <inheritdoc />
    public ScriptRunResult RunScript(AutomationScriptRun run, Action<ScriptLogEntry> capture, CancellationToken cancellation)
    {
        EditorDocument doc = RequireDocument();
        ScriptMetadata meta = ScriptMetadata.Parse(run.Source);
        string? pending = string.IsNullOrEmpty(doc.Path) && run.SaveTarget is { Length: > 0 } target
            ? Path.GetDirectoryName(Path.GetFullPath(target))
            : null;
        var log = new ScriptLog(new DelegateSink(capture));
        var services = new ScriptServices
        {
            Document = doc,
            Brushes = _brushes,
            Assets = Assets,
            InstallDirectory = RfInstallDir,
            ScanOptionsFactory = CreateScanOptions,
            BuildSettings = BuildSettings,
            TextureTraits = Traits(),
            Confirmation = new DenyAllConfirmation(),
            AutoTags = run.AutoTags,
            LocalModules = run.Modules,
            PrefabsDirectory = _settings.PrefabDirectory,
            DefaultFloorTexture = _settings.DefaultFloorTexture,
            DefaultWallTexture = _settings.DefaultWallTexture,
            DefaultCeilingTexture = _settings.DefaultCeilingTexture,
            ImportFileAccess = path => PathPolicy.RequireScriptRead(path, "level.import_mesh"),
            MeshWriteAccess = path => PathPolicy.RequireScriptWrite(path, "level.import_mesh"),
            FileReadAccess = (path, api) => PathPolicy.RequireScriptRead(path, api),
            FileWriteAccess = (path, api) => PathPolicy.RequireScriptWrite(path, api),
            PendingLevelFolder = pending,
            ShotRender = request => Rendering.ShotRenderer.Render(doc, Assets, request),
            OperationsFactory = ctx => new AutomationScriptOperations(
                ctx,
                doc,
                new CoreScriptOperations(ctx),
                PathPolicy,
                onBuilt: bake => MarkBuilt(sealedBuild: true, bake),
                save: (target, saveAs) => SaveCore(target, saveAs, cancellation)),
        };

        var options = new ScriptRunOptions
        {
            ChunkName = run.Name,
            DryRun = run.DryRun,
            AllowDestructive = run.AllowDestructive || meta.AllowDestructive,
            DeclaredApiVersion = meta.ApiVersion,
            Seed = 0,
            Limits = run.Limits,
            UndoLabel = run.UndoLabel,
        };

        // A new level saved with --out: its folder is the level's folder for this run (reading a reference picture
        // beside it, writing grids and masks there).
        _pendingLevelFolder = pending;
        ScriptRunResult result;
        try
        {
            result = _runner.Run(services, run.Source, options, log, cancellation);
        }
        finally
        {
            _pendingLevelFolder = null;
        }

        if (result.Committed)
        {
            _lightingDirty = true; // the editor marks lighting stale on any committed document change
        }

        return result;
    }

    /// <inheritdoc />
    public Ged.Core.Editing.BrushEditor? Brushes => _brushes;

    /// <inheritdoc />
    public Task<CompiledLevel> BuildAsync(bool sealedBuild, bool bakeLighting, bool shadows, CancellationToken cancellation, IProgress<AutomationProgress>? progress = null)
    {
        EditorDocument doc = RequireDocument();
        doc.SyncStaleMoverCopies(); // the build bakes the movers-section copies: refresh edited ones first
        CompiledLevel result = LevelOperations.Compile(doc, Assets, BuildSettings, sealedBuild, bakeLighting, shadows, Traits(), cancellation, progress);
        LevelOperations.Apply(doc, result);
        MarkBuilt(sealedBuild || bakeLighting, bakeLighting);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Vec3>> CheckHolesAsync(CancellationToken cancellation, IProgress<AutomationProgress>? progress = null)
    {
        EditorDocument doc = RequireDocument();
        CompiledLevel result = LevelOperations.Compile(doc, Assets, BuildSettings, sealedBuild: true, bakeLighting: false, shadows: false, Traits(), cancellation, progress);
        IReadOnlyList<Vec3> holes = HoleDetector.Detect(result.Geometry);
        return Task.FromResult(holes);
    }

    /// <inheritdoc />
    public Task<HostSaveOutcome> SaveAsync(string path, bool saveAs, CancellationToken cancellation) =>
        Task.FromResult(SaveCore(path, saveAs, cancellation));

    /// <summary>
    /// The editor's save sequence (<see cref="LevelSavePolicy"/>): decide the stamp, re-seal unsealed
    /// preview geometry with a geometry-only sealed build, write v306 + sidecar.
    /// </summary>
    private HostSaveOutcome SaveCore(string path, bool saveAs, CancellationToken cancellation)
    {
        EditorDocument doc = RequireDocument();
        LevelBuildState state = BuildState;
        bool stamp = LevelSavePolicy.StampsTimestamp(saveAs, doc.IsDirty, state);

        LevelSaveNotice notice;
        if (LevelSavePolicy.RequiresSeal(state.GeometryIsPreview))
        {
            // The editor's seal guard runs its interactive Build (geometry only, no bake).
            doc.SyncStaleMoverCopies();
            CompiledLevel sealedResult = LevelOperations.Compile(doc, Assets, BuildSettings, sealedBuild: true, bakeLighting: false, shadows: true, Traits(), cancellation);
            LevelOperations.Apply(doc, sealedResult);
            MarkBuilt(sealedBuild: true, bakedLighting: false);
            notice = LevelSaveNotice.GeometryResealed;
        }
        else
        {
            notice = LevelSavePolicy.NoticeForDirtySave(state.GeometryDirty, state.LightingDirty);
        }

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            LevelSavePolicy.Write(doc, path, saveAs, stamp, _levelLighting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"Could not write '{path}': {ex.Message}", ex);
        }

        return new HostSaveOutcome(path, saveAs, stamp, notice);
    }

    /// <inheritdoc />
    public void ReportActivity(string message)
    {
    }

    /// <summary>Records that a build was applied (sealed or preview; lit or not).</summary>
    private void MarkBuilt(bool sealedBuild, bool bakedLighting)
    {
        if (_doc is null)
        {
            return;
        }

        _builtFingerprint = LevelOperations.BrushFingerprint(_doc);
        _staleAtOpen = false;
        _geometryIsPreview = !sealedBuild;
        if (bakedLighting)
        {
            _lightingDirty = false;
        }
    }

    private TextureTraitsCache? Traits()
    {
        if (_traits is null && Assets is { } vfs)
        {
            _traits = new TextureTraitsCache(vfs);
        }

        return _traits;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> RecentUndoLabels(int max) =>
        _doc is null ? Array.Empty<string>() : UndoHistory.Recent(_doc.Undo, max);

    /// <inheritdoc />
    public Task UndoAsync(int steps, CancellationToken cancellation)
    {
        EditorDocument doc = RequireDocument();
        for (int i = 0; i < steps && doc.Undo.CanUndo; i++)
        {
            doc.Undo.Undo();
            _lightingDirty = true; // as after any document change
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RedoAsync(int steps, CancellationToken cancellation)
    {
        EditorDocument doc = RequireDocument();
        for (int i = 0; i < steps && doc.Undo.CanRedo; i++)
        {
            doc.Undo.Redo();
            _lightingDirty = true; // as after any document change
        }

        return Task.CompletedTask;
    }

    private EditorDocument RequireDocument() =>
        _doc ?? throw new AutomationException(AutomationErrorCodes.NoDocument, "No level is open.");

    /// <summary>The save target's folder while a script runs on a level with no file yet (see <see cref="AutomationScriptRun.SaveTarget"/>).</summary>
    private string? _pendingLevelFolder;

    private IEnumerable<string?> AllowedRoots()
    {
        yield return _doc?.Path is { Length: > 0 } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : _pendingLevelFolder;
        yield return RfInstallDir is { Length: > 0 } rf ? Path.Combine(rf, "user_maps") : null;
        yield return AppPaths.ScriptsDirectory;
    }

    public void Dispose() => ReleaseAssets();

    private sealed class DelegateSink : IScriptLogSink
    {
        private readonly Action<ScriptLogEntry> _write;

        public DelegateSink(Action<ScriptLogEntry> write) => _write = write;

        public void Write(ScriptLogEntry entry) => _write(entry);
    }
}
