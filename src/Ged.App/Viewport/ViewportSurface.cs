using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.Core.Assets;
using Ged.Core.Input;
using Ged.Rendering;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;

namespace Ged.App.Viewport;

/// <summary>Which direction one pane looks.</summary>
public enum ViewType
{
    Perspective,
    Top,
    Bottom,
    Front,
    Back,
    Left,
    Right,
}

/// <summary>
/// One live render pane: a Direct3D swapchain hosted in a native child window,
/// driven by a UI-thread render loop with idle frame-skipping. Input arrives from
/// the child window's WndProc (the focus-proxy) and is routed through the shared
/// <see cref="ViewportInputRouter"/> gesture state machine (navigation via the active
/// <see cref="ICameraScheme"/>, hotkeys via the shared <see cref="CommandDispatcher"/>),
/// so this pane behaves identically to the cross-platform <see cref="GlViewportSurface"/>.
/// The camera pose and uploaded scene survive native re-creation (layout changes) so
/// views are not lost.
/// </summary>
public sealed class ViewportSurface : NativeControlHost, IViewportInput, IViewportSurface, IViewportInputHost
{
    private readonly ViewportInputRouter _router;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CommandDispatcher _dispatcher;
    private readonly IViewportHost _host = ViewportHost.Current;

    private ICameraScheme _scheme;
    private ViewType _viewType = ViewType.Perspective;
    private RenderMode _mode = RenderMode.TexturesAndLightmaps;
    private FogSettings _fog = FogSettings.Off;
    private bool _disableCull;
    private float _animationTime;

    private Rendering.Viewport? _viewport;
    private nint _hwnd;
    private DispatcherTimer? _timer;
    private double _lastTime;

    private bool _needsRender = true;
    private bool _rendering;      // re-entrancy guard around a render pass
    private bool _renderQueued;   // a forced render is already posted to the dispatcher
    private bool _hasPendingPick;
    private int _pickX, _pickY;
    private bool _pickCtrl;
    private Func<string, bool>? _stillPhysicallyDown; // cached held-key predicate (no per-frame delegate alloc)

    private Func<IViewportSurface, int, int, bool>? _gizmoHitTestAt;

    private double _fpsAccum;
    private int _fpsFrames;
    private double _fps;

    private RenderScene? _scene;
    private AssetVfs? _vfs;
    private RenderScene? _overlayScene;
    private AssetVfs? _overlayVfs;
    private IReadOnlyList<LineSegment> _selection = Array.Empty<LineSegment>();
    private IReadOnlyList<LineSegment> _gizmoOverlay = Array.Empty<LineSegment>();
    private CameraPose _pose = CameraPose.Default;
    private string? _initError;

    public ViewportSurface(CommandDispatcher dispatcher, CameraSchemeKind scheme, ViewType viewType)
    {
        _router = new ViewportInputRouter(this);
        _dispatcher = dispatcher;
        _scheme = CameraSchemes.Create(scheme);
        SetViewType(viewType, invalidate: false);
    }

    /// <summary>The shared input state (guarded / driven by the router).</summary>
    private ViewportInputState _input => _router.Input;

    /// <summary>Raised when the pointer enters this pane (drives active-pane focus).</summary>
    public event Action<IViewportSurface>? Activated;

    /// <summary>Raised periodically with (fps, cameraPosition).</summary>
    public event Action<double, Vector3>? StatsUpdated;

    /// <summary>Raised when <see cref="Mode"/> changes (keeps the pane toolbar label in sync).</summary>
    public event Action<RenderMode>? ModeChanged;

    // ---- Editing-gesture events (raised by the shared router) ----
    public event Action<PickId, bool>? Picked
    {
        add => _router.Picked += value;
        remove => _router.Picked -= value;
    }

    public event Action<Vector3>? NudgeMove
    {
        add => _router.NudgeMove += value;
        remove => _router.NudgeMove -= value;
    }

    public event Action<Vector3>? NudgeRotate
    {
        add => _router.NudgeRotate += value;
        remove => _router.NudgeRotate -= value;
    }

    public event Action? BrushDragStarted
    {
        add => _router.BrushDragStarted += value;
        remove => _router.BrushDragStarted -= value;
    }

    public event Action<int, int, bool>? BrushDragPixels
    {
        add => _router.BrushDragPixels += value;
        remove => _router.BrushDragPixels -= value;
    }

    public event Action? BrushDragEnded
    {
        add => _router.BrushDragEnded += value;
        remove => _router.BrushDragEnded -= value;
    }

    public event Action<int, int>? GizmoDragStarted
    {
        add => _router.GizmoDragStarted += value;
        remove => _router.GizmoDragStarted -= value;
    }

    public event Action<int, int>? GizmoDragMovedTo
    {
        add => _router.GizmoDragMovedTo += value;
        remove => _router.GizmoDragMovedTo -= value;
    }

    public event Action? GizmoDragEnded
    {
        add => _router.GizmoDragEnded += value;
        remove => _router.GizmoDragEnded -= value;
    }

    public event Action? GizmoDragCancelled
    {
        add => _router.GizmoDragCancelled += value;
        remove => _router.GizmoDragCancelled -= value;
    }

    public event Action<int, int>? GizmoHover
    {
        add => _router.GizmoHover += value;
        remove => _router.GizmoHover -= value;
    }

    public event Action<int, int>? MarqueeStarted
    {
        add => _router.MarqueeStarted += value;
        remove => _router.MarqueeStarted -= value;
    }

    public event Action<int, int>? MarqueeMovedTo
    {
        add => _router.MarqueeMovedTo += value;
        remove => _router.MarqueeMovedTo -= value;
    }

    public event Action<int, int, bool>? MarqueeEnded
    {
        add => _router.MarqueeEnded += value;
        remove => _router.MarqueeEnded -= value;
    }

    public event Action<Vector3>? WorldPointPicked
    {
        add => _router.WorldPointPicked += value;
        remove => _router.WorldPointPicked -= value;
    }

    public event Action<int, int>? DrawHover
    {
        add => _router.DrawHover += value;
        remove => _router.DrawHover -= value;
    }

    public event Action<int, int>? DrawClick
    {
        add => _router.DrawClick += value;
        remove => _router.DrawClick -= value;
    }

    public event Action? DrawCancelRequested
    {
        add => _router.DrawCancelRequested += value;
        remove => _router.DrawCancelRequested -= value;
    }

    public event Action<int, int>? RulerClick
    {
        add => _router.RulerClick += value;
        remove => _router.RulerClick -= value;
    }

    public event Action<int, int>? RulerHover
    {
        add => _router.RulerHover += value;
        remove => _router.RulerHover -= value;
    }

    public event Action? RulerCancelRequested
    {
        add => _router.RulerCancelRequested += value;
        remove => _router.RulerCancelRequested -= value;
    }

    // ---- Editing-gesture arming flags / hooks (delegated to the router) ----

    /// <summary>When true, a gizmo is shown for the selection: LMB over a handle drags it,
    /// hover highlights it, and clicks off it still pick/marquee.</summary>
    public bool GizmoActive
    {
        get => _router.GizmoActive;
        set => _router.GizmoActive = value;
    }

    /// <summary>Press-time gate: returns true when the pixel is over a gizmo handle.</summary>
    public Func<IViewportSurface, int, int, bool>? GizmoHitTestAt
    {
        get => _gizmoHitTestAt;
        set
        {
            _gizmoHitTestAt = value;
            _router.GizmoHitTestAt = value is null ? null : (x, y) => value(this, x, y);
        }
    }

    /// <summary>When true, an empty-space LMB-drag runs a marquee box-select (select contexts).</summary>
    public bool MarqueeEnabled
    {
        get => _router.MarqueeEnabled;
        set => _router.MarqueeEnabled = value;
    }

    /// <summary>When true, an ortho LMB click reports its world point instead of picking.</summary>
    public bool PointPickArmed
    {
        get => _router.PointPickArmed;
        set => _router.PointPickArmed = value;
    }

    /// <summary>
    /// When true, the draw-brush tool owns plain pointer input: moves raise
    /// <see cref="DrawHover"/>, LMB presses raise <see cref="DrawClick"/> (consumed) and
    /// ESC raises <see cref="DrawCancelRequested"/>. Camera navigation keeps working.
    /// </summary>
    public bool DrawToolArmed
    {
        get => _router.DrawToolArmed;
        set => _router.DrawToolArmed = value;
    }

    /// <summary>When true, the ruler tool owns plain clicks and idle moves.</summary>
    public bool RulerArmed
    {
        get => _router.RulerArmed;
        set => _router.RulerArmed = value;
    }

    /// <summary>Optional hook run before a gesture is dispatched; returning true consumes it.</summary>
    public Func<KeyGesture, bool>? KeyPreDispatch
    {
        get => _router.KeyPreDispatch;
        set => _router.KeyPreDispatch = value;
    }

    public string? InitError => _initError;

    /// <summary>Current swapchain client height in pixels (for pixel→world scaling).</summary>
    public int SurfaceHeight => _viewport?.Height ?? Math.Max(1, (int)Bounds.Height);

    /// <summary>The world-space ray through the most recent pick pixel (Edge-mode CPU edge picking).</summary>
    public (Vector3 Origin, Vector3 Direction)? LastPickRay => PixelRay(_pickX, _pickY);

    public bool ScrollMode => _router.ScrollMode;

    public ViewType ViewType => _viewType;

    public CameraSchemeKind SchemeKind => _scheme.Kind;

    public double Fps => _fps;

    /// <summary>The live camera (present only while the native surface exists).</summary>
    public Rendering.Camera? Camera => _viewport?.Camera;

    /// <summary>
    /// True while the pointer is inside this pane's native render surface (WM_MOUSEMOVE
    /// sets it, WM_MOUSELEAVE clears it). Drives TAB routing: pointer-over-viewport means
    /// TAB toggles maximize instead of traversing focus.
    /// </summary>
    public bool IsPointerInside { get; private set; }

    /// <summary>The swapchain client size in pixels.</summary>
    public (int Width, int Height) SurfaceSize =>
        _viewport is null ? (Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height)) : _host.GetClientSize(_hwnd);

    /// <summary>A world-space ray through a viewport pixel, or null when the surface is absent.</summary>
    public (Vector3 Origin, Vector3 Direction)? PixelRay(int x, int y)
    {
        if (_viewport is null)
        {
            return null;
        }

        (int w, int h) = _host.GetClientSize(_hwnd);
        return _viewport.Camera.PixelRay(x, y, w, h);
    }

    /// <summary>Projects a world point to this pane's pixel space.</summary>
    public bool WorldToScreen(Vector3 world, out Vector2 screen)
    {
        screen = default;
        if (_viewport is null)
        {
            return false;
        }

        (int w, int h) = _host.GetClientSize(_hwnd);
        return _viewport.Camera.WorldToScreen(world, w, h, out screen);
    }

    public Vector3 CameraPosition => _viewport?.Camera.Position ?? _pose.Position;

    /// <summary>
    /// The camera's forward (view) direction — the live camera when present, else derived
    /// from the persisted pose's yaw/pitch (0 yaw looks +Z, mirroring
    /// <see cref="Rendering.Camera.Forward"/>). Headless-safe; used by View-From pose assertions.
    /// </summary>
    public Vector3 CameraForward
    {
        get
        {
            if (_viewport is not null)
            {
                return _viewport.Camera.Forward;
            }

            float cp = MathF.Cos(_pose.Pitch);
            return Vector3.Normalize(new Vector3(
                cp * MathF.Sin(_pose.Yaw), MathF.Sin(_pose.Pitch), cp * MathF.Cos(_pose.Yaw)));
        }
    }

    public RenderMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            if (_viewport is not null)
            {
                _viewport.Mode = value;
            }

            Invalidate();
            ModeChanged?.Invoke(value);
        }
    }

    public float CameraSpeed
    {
        get => _input.Speed;
        set => _input.Speed = value;
    }

    /// <summary>Distance-fog settings for this pane's world/mesh passes.</summary>
    public FogSettings Fog
    {
        get => _fog;
        set
        {
            _fog = value;
            if (_viewport is not null)
            {
                _viewport.Fog = value;
            }

            Invalidate();
        }
    }

    /// <summary>Render both faces of solid geometry (disable RED-parity back-face culling).</summary>
    public bool DisableBackfaceCulling
    {
        get => _disableCull;
        set
        {
            _disableCull = value;
            if (_viewport is not null)
            {
                _viewport.DisableBackfaceCulling = value;
            }

            Invalidate();
        }
    }

    /// <summary>Whether Alt is currently held (temporary snap-invert during a drag).</summary>
    public bool AltHeld => (_input.Modifiers & GestureModifiers.Alt) != 0;

    /// <summary>
    /// Whether the temporary snap-invert modifier is held during a drag. This is Alt,
    /// which also covers the documented Linux fallback <b>Ctrl+Alt</b>: many X11 window
    /// managers claim plain Alt+drag for window moves, but a Ctrl+Alt+drag is not
    /// intercepted (the WM's passive button grab matches the Alt-only modifier state), so
    /// it still reaches the viewport with Alt down and inverts the snap. See docs/internal/HOTKEYS.md.
    /// </summary>
    public bool SnapInvertHeld => AltHeld;

    /// <summary>Animation clock (seconds) for in-shader liquid UV scroll; drives a redraw.</summary>
    public float AnimationTime
    {
        get => _animationTime;
        set
        {
            _animationTime = value;
            if (_viewport is not null)
            {
                _viewport.Time = value;
                _needsRender = true;
            }
        }
    }

    public void SetScheme(CameraSchemeKind kind)
    {
        if (_scheme.Kind != kind)
        {
            _scheme = CameraSchemes.Create(kind);
        }
    }

    public void SetViewType(ViewType type, bool invalidate = true)
    {
        _viewType = type;
        ApplyViewTypeToPose();
        if (_viewport is not null)
        {
            _pose.ApplyTo(_viewport.Camera);
        }

        if (invalidate)
        {
            Invalidate();
        }
    }

    /// <summary>Uploads a scene and frames the camera between the given endpoints.</summary>
    public void LoadScene(RenderScene scene, AssetVfs? vfs, Vector3 cameraPosition, Vector3 cameraTarget)
    {
        _scene = scene;
        _vfs = vfs;
        if (_viewType == ViewType.Perspective)
        {
            _pose.Position = cameraPosition;
            _pose.LookAt(cameraPosition, cameraTarget);
        }
        else
        {
            _pose.Position = cameraTarget;
        }

        if (_viewport is not null)
        {
            _viewport.SetScene(scene, vfs);
            _pose.ApplyTo(_viewport.Camera);
        }

        Invalidate();
    }

    /// <summary>Re-uploads the current scene (used after a grid/brightness rebuild).</summary>
    public void RefreshScene(RenderScene scene, AssetVfs? vfs)
    {
        _scene = scene;
        _vfs = vfs;
        _viewport?.SetScene(scene, vfs);
        SetSelection(_selection);
        _viewport?.SetGizmoOverlay(_gizmoOverlay);
        // A full scene rebuild is a DISCRETE, non-interactive event (mode switch, render-option
        // toggle, mutation) — not a per-frame drag update. Present it synchronously instead of
        // relying solely on the coalesced render post, which the render-priority dispatcher can
        // starve while a native-HWND pane holds keyboard focus. That starvation is exactly what
        // left the first brush overlay (e.g. the "Draw unmerged brushwork" clip filter) stale on
        // mode entry until the user nudged something. RenderFrameSafe guards re-entrancy and
        // no-ops when there is no live surface (headless), so this is safe everywhere.
        RenderFrameSafe();
    }

    public void SetSelection(IReadOnlyList<LineSegment> lines)
    {
        _selection = lines;
        _viewport?.SetSelection(lines);
        Invalidate();
    }

    /// <summary>Sets the manipulator/gizmo overlay lines, drawn on top of the scene (item 12).</summary>
    public void SetGizmoOverlay(IReadOnlyList<LineSegment> lines)
    {
        _gizmoOverlay = lines;
        _viewport?.SetGizmoOverlay(lines);
        Invalidate();
    }

    /// <summary>Sets (or clears) the on-top transform-label overlay scene (drag Δ/∠/% readout).</summary>
    public void SetOverlayScene(RenderScene? scene, AssetVfs? vfs)
    {
        _overlayScene = scene;
        _overlayVfs = vfs;
        _viewport?.SetOverlayScene(scene, vfs);
        Invalidate();
    }

    /// <summary>Frames a bounds box in this pane's camera. Updates the persisted pose even
    /// when the native surface is absent (detached pane), so a "frame the brush / jump to"
    /// from another panel reaches the perspective pane and survives to its next realization
    /// (the pose is what <see cref="CreateNativeControlCore"/> applies on attach).</summary>
    public void Frame(Ged.Core.Model.Aabb bounds)
    {
        if (_viewport is not null)
        {
            _viewport.Camera.Frame(bounds);
            _pose.CaptureFrom(_viewport.Camera);
        }
        else
        {
            var tmp = new Rendering.Camera();
            _pose.ApplyTo(tmp); // seed projection/ortho/orientation from the persisted pose
            tmp.Frame(bounds);
            _pose.CaptureFrom(tmp);
        }

        Invalidate();
    }

    /// <summary>True while a pointer button is held over this pane (drives autosave deferral).</summary>
    public bool IsInteracting => _input.LeftDown || _input.RightDown || _input.MiddleDown;

    /// <summary>Frames a small volume around a world point (Jump To / double-click).</summary>
    public void FramePoint(Vector3 p)
    {
        const float r = 4f;
        Frame(new Ged.Core.Model.Aabb(
            new Ged.Core.Model.Vec3(p.X - r, p.Y - r, p.Z - r),
            new Ged.Core.Model.Vec3(p.X + r, p.Y + r, p.Z + r)));
    }

    /// <summary>Moves the camera to a world point, keeping its orientation (View From).</summary>
    public void ViewFrom(Vector3 p)
    {
        if (_viewport is not null)
        {
            _viewport.Camera.Position = p;
            _pose.CaptureFrom(_viewport.Camera);
        }
        else
        {
            _pose.Position = p; // persist so a detached pane keeps the move on next realization
        }

        Invalidate();
    }

    /// <summary>
    /// Moves the camera to a world point AND aims it along <paramref name="forward"/> — "View
    /// From" an object, matching its position and orientation (yaw/pitch from the forward
    /// vector; roll is not applied). A near-zero forward keeps the current orientation
    /// (falls back to the position-only <see cref="ViewFrom(Vector3)"/>).
    /// </summary>
    public void ViewFrom(Vector3 p, Vector3 forward)
    {
        if (forward.LengthSquared() < 1e-6f)
        {
            ViewFrom(p);
            return;
        }

        Vector3 target = p + Vector3.Normalize(forward);
        if (_viewport is not null)
        {
            _viewport.Camera.LookAt(p, target);
            _pose.CaptureFrom(_viewport.Camera);
        }
        else
        {
            _pose.LookAt(p, target); // persist for a detached / headless pane
        }

        Invalidate();
    }

    /// <summary>Stock C: snaps the camera to look down the nearest world axis.</summary>
    public void AxisOrient()
    {
        _viewport?.Camera.OrientToNearestAxis();
        Invalidate();
    }

    /// <summary>Numpad 7/9: banks (rolls) the perspective camera by degrees.</summary>
    public void Bank(float degrees)
    {
        if (_viewport is not null)
        {
            _viewport.Camera.Roll += degrees * MathF.PI / 180f;
            Invalidate();
        }
    }

    /// <summary>Stock END: toggles scroll (pan-drag) mode.</summary>
    public void ToggleScrollMode()
    {
        _router.ToggleScrollMode();
        Invalidate();
    }

    /// <summary>
    /// Requests a repaint. Marks the pane dirty AND posts a forced render to the UI dispatcher
    /// (coalesced), so a scene/overlay/selection change is presented on the next dispatcher cycle
    /// even if the render timer is momentarily starved while a native-HWND pane holds focus —
    /// the one invalidation contract every state change flows through.
    /// </summary>
    public void Invalidate()
    {
        _needsRender = true;
        if (_renderQueued || _viewport is null)
        {
            return;
        }

        _renderQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                _renderQueued = false;
                if (_needsRender)
                {
                    RenderFrameSafe();
                }
            },
            Avalonia.Threading.DispatcherPriority.Render);
    }

    /// <summary>Renders one frame immediately (guarded against re-entrancy), independent of the timer.</summary>
    private void RenderFrameSafe()
    {
        if (_rendering || _viewport is null)
        {
            return;
        }

        _rendering = true;
        try
        {
            _viewport.Render();
            _pose.CaptureFrom(_viewport.Camera);
            _needsRender = false;
        }
        catch (Exception ex)
        {
            _initError = ex.Message;
        }
        finally
        {
            _rendering = false;
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        (int w, int h) = (Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        _hwnd = _host.CreateChild(parent.Handle, w, h, this);

        try
        {
            _viewport = new Rendering.Viewport(GpuHost.Device, _hwnd, w, h) { Mode = _mode, Fog = _fog, Time = _animationTime, DisableBackfaceCulling = _disableCull };
            _pose.ApplyTo(_viewport.Camera);
            if (_scene is not null)
            {
                _viewport.SetScene(_scene, _vfs);
                _viewport.SetSelection(_selection);
                _viewport.SetGizmoOverlay(_gizmoOverlay);
            }

            if (_overlayScene is not null)
            {
                _viewport.SetOverlayScene(_overlayScene, _overlayVfs);
            }
        }
        catch (Exception ex)
        {
            _initError = ex.Message;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += OnTick;
        _timer.Start();
        _lastTime = _clock.Elapsed.TotalSeconds;
        _needsRender = true;
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_viewport is not null)
        {
            _pose.CaptureFrom(_viewport.Camera);
        }

        _timer?.Stop();
        _timer = null;
        _viewport?.Dispose();
        _viewport = null;
        _host.DestroyChild(_hwnd);
        _hwnd = nint.Zero;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_viewport is null)
        {
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _lastTime, 0.0, 0.1);
        _lastTime = now;

        (int w, int h) = _host.GetClientSize(_hwnd);
        if (w != _viewport.Width || h != _viewport.Height)
        {
            _viewport.Resize(w, h);
            _needsRender = true;
        }

        // Reconcile the held-key set against the real keyboard before driving the camera:
        // a swallowed KeyUp (NumpadEnter interleaving, alt-tab) can otherwise leave a
        // navigation key stuck down forever (item 6b). Only runs while keys are held. The
        // predicate is cached so keyboard-fly frames don't allocate a fresh delegate each tick.
        if (_input.HeldKeys.Count > 0)
        {
            _input.ValidateHeld(_stillPhysicallyDown ??= StillPhysicallyDown);
        }

        CameraPose before = default;
        before.CaptureFrom(_viewport.Camera);
        _scheme.Move(_viewport.Camera, dt, _input);
        bool moved = !before.Equals(CameraPose.Capture(_viewport.Camera));
        if (moved)
        {
            _needsRender = true;
        }

        bool navigating = _scheme.IsNavigating(_input);
        if (!_needsRender && !navigating && !_hasPendingPick)
        {
            return; // idle: skip the frame entirely
        }

        try
        {
            _viewport.Render();
        }
        catch (Exception ex)
        {
            _initError = ex.Message;
            _timer?.Stop();
            return;
        }

        _pose.CaptureFrom(_viewport.Camera);
        _needsRender = false;

        if (_hasPendingPick)
        {
            _hasPendingPick = false;
            PickId id = _viewport.Pick(_pickX, _pickY);
            _router.RaisePicked(id, _pickCtrl);
        }

        _fpsFrames++;
        _fpsAccum += dt;
        if (_fpsAccum >= 0.5)
        {
            _fps = _fpsFrames / _fpsAccum;
            StatsUpdated?.Invoke(_fps, _viewport.Camera.Position);
            _fpsFrames = 0;
            _fpsAccum = 0;
        }
    }

    private void ApplyViewTypeToPose()
    {
        _input.Ortho = _viewType != ViewType.Perspective;
        if (_viewType == ViewType.Perspective)
        {
            _pose.Projection = CameraProjection.Perspective;
        }
        else
        {
            _pose.Projection = CameraProjection.Orthographic;
            _pose.Ortho = _viewType switch
            {
                ViewType.Top => OrthoView.Top,
                ViewType.Bottom => OrthoView.Bottom,
                ViewType.Front => OrthoView.Front,
                ViewType.Back => OrthoView.Back,
                ViewType.Left => OrthoView.Left,
                _ => OrthoView.Right,
            };
        }
    }

    // ---- IViewportInput (from the native child window's WndProc) → shared router ----

    void IViewportInput.OnKey(int virtualKey, bool down, bool extended) => _router.OnKey(virtualKey, down, extended);

    void IViewportInput.OnMouseMove(int x, int y) => _router.OnMouseMove(x, y);

    void IViewportInput.OnButton(ViewportButton button, bool down, int x, int y) => _router.OnButton(button, down, x, y);

    void IViewportInput.OnWheel(int delta) => _router.OnWheel(delta);

    void IViewportInput.OnPointerActivate()
    {
        IsPointerInside = true;
        Activated?.Invoke(this);
    }

    void IViewportInput.OnPointerLeave() => IsPointerInside = false;

    void IViewportInput.OnFocusLost() => _router.OnFocusLost();

    void IViewportInput.OnFocusGained() => _router.OnFocusGained();

    // ---- IViewportInputHost (the router's surface-specific callbacks) ----

    ViewType IViewportInputHost.ViewType => _viewType;

    bool IViewportInputHost.IsNavigating() => _scheme.IsNavigating(_input);

    bool IViewportInputHost.LeftClickSelectsDragNavigates => _scheme.LeftClickSelectsDragNavigates;

    bool IViewportInputHost.SchemeConsumesKey(string token) =>
        _viewport is not null && _scheme.ConsumesKey(_viewport.Camera, token);

    void IViewportInputHost.CameraDrag(int dx, int dy, bool scrollPan)
    {
        if (scrollPan)
        {
            _viewport?.Camera?.Let(cam => cam.MoveLocal(-dx * 0.05f, dy * 0.05f, 0f));
        }
        else
        {
            _viewport?.Camera?.Let(cam => _scheme.Drag(cam, dx, dy, _input));
        }
    }

    void IViewportInputHost.CameraWheel(int delta)
    {
        (int w, int h) = _host.GetClientSize(_hwnd);
        _input.ViewWidth = w;
        _input.ViewHeight = h;
        _viewport?.Camera?.Let(cam => _scheme.Wheel(cam, delta, _input));
    }

    void IViewportInputHost.RequestPick(int x, int y, bool ctrl)
    {
        _pickX = x;
        _pickY = y;
        _pickCtrl = ctrl;
        _hasPendingPick = true;
    }

    void IViewportInputHost.RequestRender() => Invalidate();

    void IViewportInputHost.DispatchGesture(KeyGesture gesture) => _dispatcher.Dispatch(gesture);

    bool IViewportInputHost.TryWorldPoint(int x, int y, out Vector3 world) => TryOrthoWorldPoint(x, y, out world);

    void IViewportInputHost.OrthoTeleport(int x, int y) => OrthoTeleport(x, y);

    bool IViewportInputHost.IsKeyPhysicallyDown(int virtualKey) => _host.IsKeyPhysicallyDown(virtualKey);

    // The router reconciles its modifier bitfield from real physical key state ONLY while this
    // pane owns a live native child window (`_hwnd != 0`) — i.e. when key events actually arrive
    // from the Win32 WndProc and GetAsyncKeyState reflects the same keyboard. Without a native
    // window (headless tests drive OnKey synthetically), physical state does not match the
    // injected events, so the incremental UpdateModifier path is used instead.
    bool IViewportInputHost.UsesPhysicalKeyState => _hwnd != nint.Zero;

    /// <summary>Reports the world point under an ortho click (two-point clip picking).</summary>
    private bool TryOrthoWorldPoint(int x, int y, out Vector3 world)
    {
        world = default;
        if (_viewport is null)
        {
            return false;
        }

        Rendering.Camera cam = _viewport.Camera;
        (int w, int h) = _host.GetClientSize(_hwnd);
        float nx = ((x / (float)Math.Max(1, w)) * 2f) - 1f;
        float ny = ((y / (float)Math.Max(1, h)) * 2f) - 1f;
        float halfH = cam.OrthoZoom;
        float halfW = halfH * Math.Max(cam.AspectRatio, 0.01f);
        world = cam.Position + (cam.Right * (nx * halfW)) - (cam.Up * (ny * halfH));
        return true;
    }

    /// <summary>Moves the ortho pan centre to the world point under the cursor.</summary>
    private void OrthoTeleport(int x, int y)
    {
        if (_viewport is null)
        {
            return;
        }

        Rendering.Camera cam = _viewport.Camera;
        (int w, int h) = _host.GetClientSize(_hwnd);
        float nx = ((x / (float)Math.Max(1, w)) * 2f) - 1f;
        float ny = ((y / (float)Math.Max(1, h)) * 2f) - 1f;
        float halfH = cam.OrthoZoom;
        float halfW = halfH * Math.Max(cam.AspectRatio, 0.01f);
        Vector3 world = cam.Position + (cam.Right * (nx * halfW)) - (cam.Up * (ny * halfH));
        cam.Position = world;
        _pose.CaptureFrom(cam);
        Invalidate();
    }

    /// <summary>Held-key reconciliation predicate: is the token's physical key still down?
    /// Tokens with no resolvable virtual key are kept (never dropped on uncertainty).</summary>
    private bool StillPhysicallyDown(string token)
    {
        int vk = GestureConvert.VirtualKeyForToken(token);
        return vk < 0 || _host.IsKeyPhysicallyDown(vk);
    }
}

internal static class CameraExtensions
{
    public static void Let(this Rendering.Camera cam, Action<Rendering.Camera> action) => action(cam);
}
