using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Model;

namespace Ged.App;

/// <summary>
/// Drives geometry builds for the editor: a cancelable background compile with
/// progress, applying the compiled sections into the document (dirty) and
/// refreshing the viewport; geometry-dirty tracking; a debounced live-CSG
/// preview on small levels; and Check-for-Holes leak detection. The compile runs
/// off the UI thread; document mutation + scene refresh marshal back onto it.
/// </summary>
public sealed class GeometryBuildController
{
    /// <summary>Auto-preview only below this brush count (a build stays interactive, ~&lt;300&#160;ms).</summary>
    public const int LivePreviewBrushLimit = 350;

    private readonly EditorSession _session;
    private readonly Action<string> _status;
    private readonly Action _refreshScene;
    private readonly Action<string, BuildReport> _showReport;
    private readonly DispatcherTimer _previewTimer;

    private readonly DispatcherTimer _relightTimer;

    private CancellationTokenSource? _cts;
    private bool _building;

    // The in-flight build's kind + task handle (Fix B): a USER build (interactive Build / Lighting)
    // preempts a seamless BACKGROUND build (stash-only or live-CSG preview) by cancelling and
    // awaiting it, but refuses to run alongside another user build. Null task = nothing in flight.
    private bool _currentBuildIsBackground;
    private Task? _runningBuild;
    private bool _applying;
    private Ged.Core.Assets.TextureTraitsCache? _traits;
    private Aabb? _dirtyLightRegion;
    private int _lastLightCount;

    public GeometryBuildController(
        EditorSession session, Action<string> status, Action refreshScene, Action<string, BuildReport> showReport)
    {
        _session = session;
        _status = status;
        _refreshScene = refreshScene;
        _showReport = showReport;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = BuildAsync(interactive: false); };

        // Preview Lighting: light edits debounce into an incremental relight (~300 ms
        // after the last edit) so the lit view tracks the inspector/gizmo live.
        _relightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _relightTimer.Tick += (_, _) =>
        {
            _relightTimer.Stop();
            switch (DecideRelightTick())
            {
                case RelightTickAction.Bake:
                    _ = CalculateLightingAsync(shadows: false, preview: true);
                    break;
                case RelightTickAction.Reschedule:
                    // A build is in flight (Fix D): retry after it settles instead of dropping the
                    // pending relight, so the preview never goes permanently stale.
                    _relightTimer.Start();
                    break;
            }
        };
    }

    /// <summary>What the debounced Preview-Lighting relight tick should do (extracted for testability).</summary>
    internal enum RelightTickAction
    {
        /// <summary>Nothing to relight (preview off, not dirty, or geometry not ready) — do NOT reschedule.</summary>
        None,

        /// <summary>Kick the incremental preview relight now.</summary>
        Bake,

        /// <summary>A build is in flight — retry the tick after it settles.</summary>
        Reschedule,
    }

    /// <summary>
    /// Decides the debounced Preview-Lighting relight tick (Fix D). Only <see cref="RelightTickAction.Reschedule"/>
    /// re-arms the timer, and only while a build is in flight — every terminal state returns
    /// <see cref="RelightTickAction.None"/> so a preview-off / not-dirty tick can never spin.
    /// </summary>
    internal RelightTickAction DecideRelightTick()
    {
        if (!PreviewLightingEnabled || !LightingDirty)
        {
            return RelightTickAction.None;
        }

        if (_building)
        {
            return RelightTickAction.Reschedule;
        }

        if (GeometryDirty || _session.Document is not { } doc || FindGeometry(doc) is not { Surfaces.Count: > 0 })
        {
            return RelightTickAction.None;
        }

        return RelightTickAction.Bake;
    }

    /// <summary>True when brushes/effects changed since the last build.</summary>
    public bool GeometryDirty { get; private set; }

    /// <summary>
    /// True when the last applied geometry came from a PREVIEW build (interactive == false):
    /// the fast live-CSG/merged-stash path that skips the t-joint seal (FixTJoints) and the
    /// surface stage. Such geometry is unsealed — riddled with open t-joint edges — so it must
    /// never be treated as authoritative. Check-for-Holes and Save re-seal it via a full build
    /// before reporting leaks / persisting the file.
    /// </summary>
    public bool GeometryIsPreview { get; private set; }

    /// <summary>True when a light or lighting-affecting property changed since the last bake (distinct from geometry-dirty).</summary>
    public bool LightingDirty { get; private set; }

    /// <summary>Cast shadows in the interactive lighting bakes (Calculate Lighting vs …w/o shadows).</summary>
    public bool CastShadows { get; set; } = true;

    /// <summary>
    /// The selected lightmap bake method (feature 1). Real bakes use it; the Preview
    /// Lighting path stays on RED Classic (cheap) unless the last full bake was fast
    /// (&lt; ~1.5&#160;s), matching the small-level gating.
    /// </summary>
    public LightingMethod? Method { get; set; }

    /// <summary>Elapsed milliseconds of the last full (non-preview) lighting bake, for the preview gate.</summary>
    public double LastFullBakeMs { get; internal set; }

    private const double PreviewFullMethodThresholdMs = 1500;

    /// <summary>
    /// The method that a bake would apply: the selected method for a real bake, and for a
    /// preview bake only when the last full bake was fast (&lt; ~1.5&#160;s) — otherwise null
    /// (stock RED Classic). This is the Preview-Lighting gate (feature 1).
    /// </summary>
    internal LightingMethod? MethodForBake(bool preview) =>
        !preview || (LastFullBakeMs > 0 && LastFullBakeMs < PreviewFullMethodThresholdMs) ? Method : null;

    /// <summary>Auto-rebuild after brush edits on small levels (default on).</summary>
    public bool LivePreviewEnabled { get; set; } = true;

    /// <summary>
    /// Geometry build method: when true (the default), compiles with RED's authentic SINGLE ACCUMULATED SHARED BSP
    /// (<see cref="CompileOptions.SharedBsp"/>) instead of GED's Incremental accumulator.
    /// Mirrors the Geometry-menu "Build method" choice; persisted via <see cref="AppSettings.UseSharedBspBuild"/>.
    /// </summary>
    public bool UseSharedBspBuild { get; set; } = true;

    /// <summary>
    /// Preview Lighting: while true, light-affecting document changes (property edits,
    /// gizmo/key moves, add/delete) debounce into an automatic incremental relight.
    /// Mirrors the MainWindow "Preview Lighting" menu toggle.
    /// </summary>
    public bool PreviewLightingEnabled { get; set; }

    /// <summary>
    /// While true, an interactive transform drag is in progress: <see cref="OnBrushesChanged"/> still
    /// accumulates the dirty flags + per-brush stale-fragment set (cheap, needed for a correct commit)
    /// but does NOT arm the debounced live-CSG preview or fire <see cref="StateChanged"/> each frame.
    /// The shell clears it on drag commit/cancel and calls <see cref="ArmLivePreviewIfPending"/> ONCE,
    /// so the preview + status refresh happen exactly once for the whole drag — never per mouse-move.
    /// </summary>
    public bool SuspendLivePreview { get; set; }

    /// <summary>True while a debounced Preview-Lighting relight is pending (test hook).</summary>
    internal bool AutoRelightPending => _relightTimer.IsEnabled;

    /// <summary>True while the debounced live-CSG geometry preview is armed (test hook).</summary>
    internal bool LivePreviewPending => _previewTimer.IsEnabled;

    /// <summary>The accumulated changed-light influence region for the next incremental relight (test hook).</summary>
    internal Aabb? PendingLightRegion => _dirtyLightRegion;

    /// <summary>Hole locations from the last Check-for-Holes run (clickable → camera jump).</summary>
    public IReadOnlyList<Vec3> HoleLocations { get; private set; } = Array.Empty<Vec3>();

    /// <summary>Raised when the dirty / hole state changes so the status bar can refresh.</summary>
    public event Action? StateChanged;

    /// <summary>Item 4: appends a tagged entry to the Log output panel — (operation, message).
    /// Set by the shell; covers the operations that don't emit a full <see cref="BuildReport"/>
    /// (incremental relight, remove-lightmaps, hole check).</summary>
    public Action<string, string>? Log { get; set; }

    /// <summary>
    /// Optional unified-notification sink (status bar + Log + bottom-right toast). Set by the shell so
    /// build refusals and failures surface as toasts at the user's configured level. When null (headless
    /// / tests) those messages fall back to the plain <c>status</c> callback, so existing status assertions
    /// still hold. Like <see cref="Log"/>, it is an injected hook, not a hard dependency.
    /// </summary>
    public Action<Ged.App.Services.NotificationSeverity, string>? Notify { get; set; }

    /// <summary>Routes a user-facing message through the notification sink when one is wired (status bar +
    /// Log + toast), else the plain status callback.</summary>
    private void Status(Ged.App.Services.NotificationSeverity severity, string message)
    {
        if (Notify is { } notify)
        {
            notify(severity, message);
        }
        else
        {
            _status(message);
        }
    }

    /// <summary>Item 3: registers a long-running operation with the progress overlay — (name) →
    /// a disposable handle the run reports progress against and disposes when it finishes. Set by
    /// the shell; null in headless/test contexts.</summary>
    internal Func<string, Ged.App.Services.OperationProgress>? BeginOperation { get; set; }

    /// <summary>Subscribes to brush/property edits so builds stay in sync (call after open/new).</summary>
    public void Attach()
    {
        if (_session.BrushEditor is { } be)
        {
            be.BrushesChanged += OnBrushesChanged;
        }

        if (_session.Document is { } doc)
        {
            // Any content edit (light move, property change) marks lighting dirty;
            // brush edits additionally mark geometry dirty via OnBrushesChanged.
            doc.DirtyChanged += OnDocumentChanged;
            _lastLightCount = Lights(doc).Count;
        }
    }

    /// <summary>Marks the built geometry stale (e.g. after the Geometry-menu build method changed), so the next
    /// build / save / hole-check recompiles with the current settings.</summary>
    public void InvalidateGeometry()
    {
        GeometryDirty = true;
        LightingDirty = true;
    }

    /// <summary>
    /// Invalidates compiled geometry after a STRUCTURAL brush mutation that bypassed
    /// <see cref="BrushEditor"/> — prefab placement / propagation deletes and re-imports member
    /// brushes directly, so <c>BrushesChanged</c> never fired. Mirrors the
    /// <see cref="OnBrushesChanged"/> structural (unknown-UIDs) path: marks geometry + lighting
    /// dirty, clears hole locations, drops the merged-brush stash wholesale (fragment↔face identity
    /// no longer holds), and kicks the debounced live-CSG preview on small levels.
    /// </summary>
    public void InvalidateBrushGeometry()
    {
        GeometryDirty = true;
        LightingDirty = true;
        HoleLocations = Array.Empty<Vec3>();
        _session.BrushFaceSurvival = null;
        _session.BrushFragments = null;
        _session.StaleFragmentBrushUids.Clear();
        StateChanged?.Invoke();
        if (LivePreviewEnabled && BrushCount() > 0 && BrushCount() <= LivePreviewBrushLimit)
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }
    }

    private void OnBrushesChanged()
    {
        GeometryDirty = true;
        LightingDirty = true;
        HoleLocations = Array.Empty<Vec3>();

        // Per-brush staleness (item 5b): a pure transform of known brushes (a gizmo /
        // M-N drag) reports exactly those UIDs — only they fall back to authored polygons
        // while every untouched brush keeps its fragment overlay until the live-CSG
        // preview (~500 ms) refreshes the whole stash. A structural/unknown change
        // (create, delete, clip — LastChangedBrushUids null) invalidates the stash
        // wholesale, since fragment↔face identity no longer holds.
        IReadOnlyCollection<int>? edited = _session.BrushEditor?.LastChangedBrushUids;
        if (edited is not null && _session.BrushFragments is not null)
        {
            foreach (int uid in edited)
            {
                _session.StaleFragmentBrushUids.Add(uid);
            }
        }
        else
        {
            _session.BrushFaceSurvival = null;
            _session.BrushFragments = null;
            _session.StaleFragmentBrushUids.Clear();
        }

        // During an interactive drag the dirty state above is accumulated per frame (cheap), but the
        // status refresh + debounced preview are deferred to the single drag commit (ArmLivePreviewIfPending)
        // so a drag of N steps never re-arms the debounce N times.
        if (SuspendLivePreview)
        {
            return;
        }

        StateChanged?.Invoke();
        if (LivePreviewEnabled && BrushCount() > 0 && BrushCount() <= LivePreviewBrushLimit)
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }
    }

    /// <summary>
    /// Fires the deferred status refresh and arms the debounced live-CSG preview ONCE — the drag-commit
    /// counterpart to the per-frame work <see cref="OnBrushesChanged"/> skips while
    /// <see cref="SuspendLivePreview"/> is set. Idempotent and safe to call when nothing is dirty.
    /// </summary>
    public void ArmLivePreviewIfPending()
    {
        StateChanged?.Invoke();
        if (GeometryDirty && LivePreviewEnabled && BrushCount() > 0 && BrushCount() <= LivePreviewBrushLimit)
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }
    }

    private void OnDocumentChanged()
    {
        if (_applying)
        {
            return; // our own build/bake marked the document dirty
        }

        LightingDirty = true;

        // Incremental hint: if the edited (selected) objects are lights, record their
        // influence region so the next Calculate Lighting only re-bakes the affected
        // surfaces (range-overlap). Position edits stay correct for property/colour
        // changes; a large move falls back to a full relight via Calculate Lighting.
        bool lightsAffected = false;
        if (_session.Document is { } doc)
        {
            foreach (LevelObject o in doc.Selection)
            {
                if (o.Kind == LevelObjectKind.Light && FindLight(doc, o.Uid) is Light l)
                {
                    float r = Math.Max(1f, l.Range);
                    var rr = new Vec3(r, r, r);
                    MarkLightChanged(new Aabb(l.Position.Sub(rr), l.Position.Add(rr)));
                    lightsAffected = true;
                }
            }

            // A light add/delete changes the count without the light being selected
            // (place-from-palette, delete). Region unknown → the debounced pass does
            // a full relight, which covers removed influence correctly.
            int lightCount = Lights(doc).Count;
            if (lightCount != _lastLightCount)
            {
                _lastLightCount = lightCount;
                lightsAffected = true;
            }
        }

        if (lightsAffected && PreviewLightingEnabled)
        {
            _relightTimer.Stop();
            _relightTimer.Start();
        }

        StateChanged?.Invoke();
    }

    private static Light? FindLight(EditorDocument doc, int uid)
    {
        foreach (RflSection s in doc.Rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.Lights && s.Content is LightsSection ls)
            {
                return ls.Lights.FirstOrDefault(l => l.Uid == uid);
            }
        }

        return null;
    }

    /// <summary>
    /// Records that a light at <paramref name="bounds"/> changed, so the next
    /// incremental relight only re-bakes the affected surfaces. Accumulates the
    /// union across edits (covers a light that moved or shrank its range).
    /// </summary>
    public void MarkLightChanged(Aabb bounds)
    {
        LightingDirty = true;
        _dirtyLightRegion = _dirtyLightRegion is Aabb a ? Union(a, bounds) : bounds;
        StateChanged?.Invoke();
    }

    /// <summary>Compiles the level on a background thread and swaps in the result (no lighting bake).
    /// Returns true when the build ran, false when a user build was refused (another in flight).</summary>
    public Task<bool> BuildAsync(bool interactive = true) => RunBuildAsync(interactive, bakeLighting: false, CastShadows);

    /// <summary>
    /// "Draw unmerged brushwork" OFF shows the MERGED result, which the brush overlay can only
    /// draw once a build has populated the survival/fragment stash (<see cref="EditorSession.BrushFaceSurvival"/>).
    /// If that stash has never been built (null) while brushes exist, this kicks a background
    /// preview build so toggling the option OFF takes effect on its own — instead of the
    /// user's reported "nothing happens until I nudge a brush", where only the edit's live-CSG
    /// preview populated the stash. No-op when a stash already exists, a build is already in
    /// flight, or there are no brushes. Returns true if it started a build.
    /// </summary>
    public bool EnsureMergedBrushStash()
    {
        if (_building || _session.BrushFaceSurvival is not null || BrushCount() == 0)
        {
            return false;
        }

        // STASH-ONLY (Fix A): populate the brush-overlay survival/fragment stash WITHOUT mutating the
        // document. This build fires on a freshly opened level (the merged brush view is the default),
        // so applying its 0-surface preview geometry would wipe RED's loaded static_geometry + baked
        // lightmaps moments after open. It compiles exactly as the live-CSG preview does, but skips the
        // GeometryBuildService.Apply / MarkDirty / dirty-flag mutations.
        _ = RunBuildAsync(interactive: false, bakeLighting: false, CastShadows, applyToDocument: false);
        return true;
    }

    /// <summary>Calculate Lightmaps: full geometry build incl. the surface/atlas layout, no lighting bake.
    /// Returns true when it ran, false when refused (another user build in flight).</summary>
    public Task<bool> CalculateLightmapsAsync() => RunBuildAsync(interactive: true, bakeLighting: false, shadows: false);

    /// <summary>Calculate Maps and Light: full geometry build + a lighting bake (with or without shadows).
    /// Returns true when it ran, false when refused (another user build in flight). The pre-save
    /// rebuild consumes this to decide whether the save may proceed.</summary>
    public Task<bool> CalculateMapsAndLightAsync(bool shadows) => RunBuildAsync(interactive: true, bakeLighting: true, shadows);

    /// <summary>
    /// Calculate Lighting: bake lighting. If geometry is dirty it recompiles first;
    /// otherwise it relights onto the existing surfaces — incrementally when only a
    /// known light region changed (range-overlap), else a full relight.
    /// <paramref name="preview"/> keeps the cheap RED Classic method (Preview Lighting).
    /// </summary>
    public async Task<bool> CalculateLightingAsync(bool shadows, bool preview = false)
    {
        if (_session.Document is not { } doc)
        {
            return false;
        }

        if (preview)
        {
            // Preview-Lighting auto-relight (debounced / toggle): stay seamless — never queue behind or
            // interrupt an in-flight build. The relight debounce (Fix D) reschedules until it is free.
            if (_building)
            {
                return false;
            }
        }
        else if (!await EnsureCanStartUserBuildAsync())
        {
            return false; // Fix B: another user build is already running (a message was shown).
        }

        if (GeometryDirty || FindGeometry(doc) is not { Surfaces.Count: > 0 })
        {
            return await RunBuildAsync(interactive: true, bakeLighting: true, shadows, preview);
        }

        Task relight = RelightAsync(doc, shadows, preview);
        _runningBuild = relight;
        await relight;
        return true;
    }

    /// <summary>
    /// Applies the selected method (feature 1) onto the bake options. A preview bake stays
    /// on RED Classic unless the last full bake was fast (&lt; ~1.5&#160;s) — the small-level
    /// gating that also drives Preview Lighting.
    /// </summary>
    private void ApplyMethod(LightingOptions options, bool preview) =>
        options.WithMethod(MethodForBake(preview));

    /// <summary>
    /// Item 4: builds the light-cookie resolver for a bake — reads the light-UID→cookie map from
    /// the object-metadata chunk and decodes each cookie image through the VFS. Missing / undecodable
    /// cookies are recorded in <paramref name="warnings"/> and skipped (bake without). Returns null
    /// when the level uses no cookies (the bake then does no cookie work at all).
    /// </summary>
    private Func<int, Ged.Core.Lighting.LightCookie?>? CookieResolverFor(
        EditorDocument doc, Ged.Core.Assets.AssetVfs? vfs, List<string> warnings)
    {
        var meta = new Ged.Core.Editing.GedObjectMetadataService(doc);
        IReadOnlyDictionary<int, string> cookies = meta.AllCookies();
        if (cookies.Count == 0)
        {
            return null;
        }

        return Ged.Core.Lighting.LightCookies.BuildResolver(
            cookies,
            file => LoadCookieImage(vfs, file),
            file => warnings.Add($"Light cookie '{file}' could not be loaded — baked without it."));
    }

    /// <summary>Item 6: resolves each light's cookie sharpness (metadata chunk) for the bake; null when no cookies.</summary>
    private static Func<int, float>? SharpnessResolverFor(EditorDocument doc)
    {
        var meta = new Ged.Core.Editing.GedObjectMetadataService(doc);
        IReadOnlyDictionary<int, float> map = meta.AllCookieSharpness();
        if (map.Count == 0)
        {
            return null;
        }

        return uid => map.TryGetValue(uid, out float s) ? s : 1f;
    }

    private static (int Width, int Height, byte[] Rgba)? LoadCookieImage(Ged.Core.Assets.AssetVfs? vfs, string file)
    {
        if (vfs is null)
        {
            return null;
        }

        try
        {
            Ged.Core.IO.Tex.DecodedTexture? decoded = vfs.LoadTexture(file);
            if (decoded is null)
            {
                return null;
            }

            Ged.Core.IO.Tex.TextureImage img = decoded.Primary;
            return (img.Width, img.Height, img.Pixels);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fix B: resolves build concurrency for a USER-initiated operation. Returns true when the caller
    /// may proceed. A seamless BACKGROUND build in flight (stash-only / live-CSG preview) is cancelled
    /// and awaited first — so its finally cannot clear the new build's state — before the user build
    /// starts; another USER build in flight is refused with a status message (never a silent dead click).
    /// </summary>
    private async Task<bool> EnsureCanStartUserBuildAsync()
    {
        if (!_building)
        {
            return true;
        }

        if (!_currentBuildIsBackground)
        {
            Status(Ged.App.Services.NotificationSeverity.Warning, "A build is already running — wait for it to finish.");
            return false;
        }

        _cts?.Cancel();
        if (_runningBuild is { } running)
        {
            try
            {
                await running; // let the background build unwind fully before the user build starts
            }
            catch
            {
                // it was superseded / cancelled — its own handler already logged anything real
            }
        }

        return true;
    }

    private async Task<bool> RunBuildAsync(
        bool interactive, bool bakeLighting, bool shadows, bool preview = false, bool applyToDocument = true)
    {
        if (_session.Document is not { } doc)
        {
            return false;
        }

        if (interactive)
        {
            // User build: preempt a seamless background build, or refuse a second user build (Fix B).
            if (!await EnsureCanStartUserBuildAsync())
            {
                return false;
            }
        }
        else if (_building)
        {
            // A seamless background build (stash-only / live-CSG preview) never queues behind or
            // interrupts an in-flight build; it is re-armed by the next edit / stash request.
            return false;
        }

        Task body = BuildBodyAsync(doc, interactive, bakeLighting, shadows, preview, applyToDocument);
        _runningBuild = body;
        await body;
        return true; // the build ran (success or a caught failure — the caller checks the dirty flags)
    }

    /// <summary>
    /// The build body: compiles on a background thread and (when <paramref name="applyToDocument"/>)
    /// swaps the result into the document. It ALWAYS populates the brush-overlay stash. The STASH-ONLY
    /// path (<paramref name="applyToDocument"/> false, from <see cref="EnsureMergedBrushStash"/>) leaves
    /// the document — its loaded static_geometry + lightmaps — untouched (Fix A).
    /// </summary>
    private async Task BuildBodyAsync(
        EditorDocument doc, bool interactive, bool bakeLighting, bool shadows, bool preview, bool applyToDocument)
    {
        _building = true;
        _currentBuildIsBackground = !interactive;
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        // Item 3: show a progress card for user-initiated compiles/bakes. The debounced auto-preview
        // (interactive == false) is meant to be seamless, so it stays out of the overlay.
        using Ged.App.Services.OperationProgress? progress = interactive
            ? BeginOperation?.Invoke(bakeLighting ? "Lighting bake" : "Building geometry")
            : null;

        // Texture-derived face flags (invisible / alpha / holes) need the VFS.
        if (_traits is null && _session.Vfs is { } vfs)
        {
            _traits = new Ged.Core.Assets.TextureTraitsCache(vfs);
        }

        Ged.Core.Assets.TextureTraitsCache? traits = _traits;
        var options = new CompileOptions
        {
            Alpine = doc.Rfl.Context.IsAlpine,
            // Geometry menu "Build method": RED-authentic shared BSP vs GED's Incremental fold.
            SharedBsp = UseSharedBspBuild,
            BuildSurfaces = interactive,   // preview skips the surface/atlas stage for speed
            FixTJoints = interactive,
            BakeLighting = bakeLighting,
            // Item 6: High-Resolution Lightmaps raises the surface texel density so cookies resolve.
            // It only affects the surface stage (interactive builds); the cheap preview skips surfaces.
            HighResLightmaps = Method?.HighResLightmaps ?? false,
            Cancellation = token,
            TextureTraits = traits is null ? null : traits.Get,
            Progress = p => Dispatcher.UIThread.Post(() =>
            {
                _status($"{p.Stage}… {p.Current}/{p.Total}");
                progress?.Report(p.Stage, p.Current, p.Total);
            }),
        };
        options.Lighting.CastShadows = shadows;
        options.Lighting.WarnStockLightLimit = !doc.Rfl.Context.IsAlpine;
        if (bakeLighting)
        {
            ApplyMethod(options.Lighting, preview);
        }

        _status(bakeLighting ? "Building geometry + lighting…" : interactive ? "Building geometry…" : "Preview…");
        var cookieWarnings = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            CompiledLevel result = await Task.Run(() =>
            {
                // Item 4: resolve each light's projection cookie (metadata chunk + VFS) into the
                // bake; missing cookies are reported and baked without.
                if (bakeLighting)
                {
                    options.Lighting.CookieResolver = CookieResolverFor(doc, _session.Vfs, cookieWarnings);
                    options.Lighting.CookieSharpnessResolver = SharpnessResolverFor(doc);
                }

                return GeometryBuildService.Build(doc.Rfl, options);
            }, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            foreach (string w in cookieWarnings)
            {
                result.Report.Add(BuildSeverity.Warning, w);
            }

            sw.Stop();

            if (applyToDocument)
            {
                if (bakeLighting && !preview)
                {
                    LastFullBakeMs = sw.Elapsed.TotalMilliseconds;
                }

                _applying = true;
                GeometryBuildService.Apply(doc.Rfl, result);
                doc.MarkDirty();
                _applying = false;
                GeometryDirty = false;
                // A preview build (interactive == false) skips the t-joint SEAL (FixTJoints) and the
                // surface stage for speed, so the geometry it applies is UNSEALED: it carries thousands
                // of open t-joint edges that the sealed interactive build closes (dmabrupt: preview 13k
                // vs sealed 6). That geometry is fine for the viewport/brush-overlay preview but is NOT
                // authoritative — Check-for-Holes and Save must re-seal it, so record the quality here.
                GeometryIsPreview = !interactive;
            }

            // The brush-overlay stash is the reason EVERY build runs — including the stash-only build
            // (Fix A), which populates it without touching the document.
            _session.BrushFaceSurvival = result.SurvivingBrushFaces; // brush-overlay clipped-face filter
            // Item 5: index the compiled fragments so partially-clipped faces draw their
            // surviving area (built once per stash). A fresh stash covers every brush, so
            // the per-brush staleness set is cleared (item 5b).
            _session.BrushFragments = Ged.Rendering.Scene.BrushFragmentIndex.Build(
                result.Geometry, result.BrushFaceIdStart, result.SurvivingBrushFaces);
            _session.StaleFragmentBrushUids.Clear();
            if (applyToDocument && bakeLighting)
            {
                LightingDirty = false;
                _dirtyLightRegion = null;
            }

            StateChanged?.Invoke();
            _refreshScene();

            if (interactive)
            {
                Report(result.Report);
                _showReport(bakeLighting ? "Lighting" : "Build", result.Report);
            }

            // Fix D: a geometry-only build (no bake) that leaves lighting dirty re-arms the
            // Preview-Lighting debounce so the live preview re-bakes onto the fresh surfaces
            // instead of going stale. The debounce no-ops when the geometry has no surfaces yet.
            if (!bakeLighting && PreviewLightingEnabled && LightingDirty)
            {
                _relightTimer.Stop();
                _relightTimer.Start();
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer build
        }
        catch (Exception ex)
        {
            Status(Ged.App.Services.NotificationSeverity.Error, $"Build failed: {ex.Message}");
            CrashHandler.LogNonFatal("geometry-build", ex);
        }
        finally
        {
            _building = false;
            _applying = false;
        }
    }

    /// <summary>Re-bakes lighting onto the already-compiled geometry (incremental when a light region is known).</summary>
    private async Task RelightAsync(EditorDocument doc, bool shadows, bool preview = false)
    {
        Geometry? g = FindGeometry(doc);
        LightmapsSection? lm = FindLightmaps(doc);
        if (g is null || lm is null)
        {
            await RunBuildAsync(interactive: true, bakeLighting: true, shadows, preview);
            return;
        }

        _building = true;
        // Fix B: a preview relight is seamless/background (a user build preempts it); a manual relight
        // is a user build (a second user build is refused). The bake ignores cancellation, so a preempt
        // awaits it to completion — correct, just not instant.
        _currentBuildIsBackground = preview;
        List<Light> lights = Lights(doc);
        RfColor? ambient = FindAmbient(doc);
        Aabb? region = _dirtyLightRegion;
        var opts = new LightingOptions { CastShadows = shadows };
        ApplyMethod(opts, preview);

        _status(region is null ? "Relighting…" : "Relighting changed area…");

        // Item 3: overlay for a manual relight; the debounced Preview-Lighting relight stays seamless.
        using Ged.App.Services.OperationProgress? progress = preview ? null : BeginOperation?.Invoke("Lighting bake");
        progress?.ReportIndeterminate(region is null ? "relighting…" : "relighting changed area…");

        var cookieWarnings = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            BakeStats stats = await Task.Run(() =>
            {
                opts.CookieResolver = CookieResolverFor(doc, _session.Vfs, cookieWarnings); // item 4
                opts.CookieSharpnessResolver = SharpnessResolverFor(doc); // item 6
                return LevelLighting.BakeInto(g, lm.Lightmaps, lights, ambient, opts, region);
            });

            sw.Stop();
            if (!preview)
            {
                LastFullBakeMs = sw.Elapsed.TotalMilliseconds;
            }

            _applying = true;
            // section already marked dirty by FindLightmaps
            doc.MarkDirty();
            _applying = false;
            LightingDirty = false;
            _dirtyLightRegion = null;
            StateChanged?.Invoke();
            _refreshScene();
            string relit = $"Relit {stats.Surfaces} surface(s), {stats.Lights} lights in {stats.ElapsedMs:0} ms" +
                (region is null ? " (full)." : " (changed area).");
            _status(relit);
            Log?.Invoke("Lighting", relit);
        }
        catch (Exception ex)
        {
            // Routes to status + Log + toast when the shell wires Notify (the Log tag becomes "Error");
            // falls back to the plain status callback headless.
            Status(Ged.App.Services.NotificationSeverity.Error, $"Relight failed: {ex.Message}");
        }
        finally
        {
            _building = false;
            _applying = false;
        }
    }

    /// <summary>Remove Lightmaps: reseed the atlas to neutral grey (clears baked lighting).</summary>
    public void RemoveLightmaps()
    {
        if (_session.Document is not { } doc)
        {
            return;
        }

        LightmapsSection? lm = FindLightmaps(doc);
        if (lm is null)
        {
            _status("No lightmaps to remove.");
            Log?.Invoke("Lighting", "Remove lightmaps: none present.");
            return;
        }

        foreach (Lightmap page in lm.Lightmaps)
        {
            Array.Fill(page.Pixels, (byte)128);
        }

        _applying = true;
        // section already marked dirty by FindLightmaps
        doc.MarkDirty();
        _applying = false;
        LightingDirty = true;

        // Fix C: with the live preview active, arm the relight debounce so the greyed pages re-bake
        // automatically — otherwise the preview goes grey and stays grey (OnDocumentChanged is
        // suppressed here via _applying, so nothing else would schedule it).
        bool rebake = PreviewLightingEnabled;
        if (rebake)
        {
            _relightTimer.Stop();
            _relightTimer.Start();
        }

        StateChanged?.Invoke();
        _refreshScene();
        string reset = $"Lightmaps removed (reset to neutral).{(rebake ? " Preview Lighting will re-bake." : string.Empty)}";
        _status(reset);
        Log?.Invoke("Lighting",
            $"Lightmaps removed — {lm.Lightmaps.Count} page(s) reset to neutral grey.{(rebake ? " Preview Lighting will re-bake." : string.Empty)}");
    }

    /// <summary>Builds (if needed) then reports hole/leak locations.</summary>
    public async Task CheckHolesAsync()
    {
        if (_session.Document is not { } doc)
        {
            return;
        }

        // Item 3: overlay for the hole check. When it forces a rebuild below, that build registers
        // its own "Building geometry" card — the two stack, exercising the overlay's multi-op path.
        using Ged.App.Services.OperationProgress? progress = BeginOperation?.Invoke("Check for Holes");
        progress?.ReportIndeterminate("detecting leaks…");

        // The leak check MUST run on SEALED geometry. A stale (dirty) or preview-quality build
        // carries thousands of unsealed t-joint edges the interactive build's SeamSealer closes;
        // reporting those as leaks is the "thousands of holes on dmabrupt" false positive. Force a
        // full interactive (FixTJoints = true) build in either case so the count matches RED's.
        if (GeometryDirty || GeometryIsPreview)
        {
            await BuildAsync();
        }

        Geometry? g = FindGeometry(doc);
        if (g is null)
        {
            _status("No compiled geometry — build first.");
            Log?.Invoke("Holes", "No compiled geometry — build first.");
            return;
        }

        HoleLocations = HoleDetector.Detect(g);
        StateChanged?.Invoke();
        string holes = HoleLocations.Count == 0
            ? "Check for holes: no leaks found."
            : $"Check for holes: {HoleLocations.Count} leak edge(s) found.";
        _status(holes);
        Log?.Invoke("Holes", holes);
    }

    private void Report(BuildReport r)
    {
        _status($"Built: {r.Rooms} rooms, {r.Subrooms} subrooms, {r.Portals} portals, " +
                $"{r.Faces} faces, {r.Vertices} verts, {r.Brushes} brushes, {r.Surfaces} surfaces, " +
                $"{r.LightmapPages} lm pages in {r.ElapsedMs:0} ms" +
                (r.Messages.Count > 0 ? $" ({r.Messages.Count} warning(s))" : string.Empty));
    }

    private int BrushCount() =>
        _session.BrushEditor?.Brushes.Count ?? 0;

    private static Geometry? FindGeometry(EditorDocument doc)
    {
        foreach (var s in doc.Rfl.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                return g.Geometry;
            }
        }

        return null;
    }

    private static LightmapsSection? FindLightmaps(EditorDocument doc)
    {
        foreach (RflSection s in doc.Rfl.Sections)
        {
            if (s.Content is LightmapsSection lm)
            {
                s.Dirty = true; // re-serialize on save
                return lm;
            }
        }

        return null;
    }

    private static List<Light> Lights(EditorDocument doc)
    {
        doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in doc.Rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.Lights && s.Content is LightsSection l)
            {
                return l.Lights;
            }
        }

        return new List<Light>();
    }

    private static RfColor? FindAmbient(EditorDocument doc)
    {
        foreach (RflSection s in doc.Rfl.Sections)
        {
            if (s.Content is LevelPropertiesSection lp)
            {
                return lp.AmbientColor;
            }
        }

        return null;
    }

    private static Aabb Union(Aabb a, Aabb b) => new(
        Vec3Math.Min(a.P1, b.P1), Vec3Math.Max(a.P2, b.P2));
}
