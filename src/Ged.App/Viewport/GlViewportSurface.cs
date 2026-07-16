using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.Core.Assets;
using Ged.Core.Input;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using KeyGesture = Ged.Core.Input.KeyGesture;

namespace Ged.App.Viewport;

/// <summary>
/// The OpenGL viewport host: an <see cref="OpenGlControlBase"/> that renders a live
/// <see cref="Rendering.Viewport"/> through the GL RHI backend, COMPOSITED into the
/// Avalonia visual tree (no native child window — removes the airspace limitation of the
/// Win32 child-HWND host). It is the cross-platform counterpart of the Windows
/// <see cref="ViewportSurface"/> (Direct3D 11) and implements the identical
/// <see cref="IViewportSurface"/> editing contract by driving the SAME shared
/// <see cref="ViewportInputRouter"/> gesture state machine — so transform drags, marquee,
/// gizmo, draw-brush, ruler, point-pick and pick-click behave identically on either backend.
/// <para>
/// THREAD MODEL: Avalonia drives <see cref="OnOpenGlInit"/> / <see cref="OnOpenGlRender"/>
/// / <see cref="OnOpenGlDeinit"/> on its render thread with the GL context CURRENT, so the
/// GL device, the <see cref="Rendering.Viewport"/> and every GL resource live there and
/// NOWHERE else. Input, gestures and camera navigation run on the UI thread and reach the
/// render thread only through immutable snapshots — a <see cref="CameraPose"/> struct, a
/// pending scene / selection / overlay ref and a pending-pick request, each applied at the
/// top of the next frame. A completed pick posts back to the UI thread via the dispatcher.
/// </para>
/// </summary>
public sealed class GlViewportSurface : OpenGlControlBase, IViewportInput, IViewportSurface, IViewportInputHost, Avalonia.Rendering.ICustomHitTest
{
    /// <summary>
    /// Every pixel INSIDE this pane is interactive — and no pixel outside it. An
    /// <see cref="OpenGlControlBase"/> is a bare <see cref="Avalonia.Controls.Control"/> with no
    /// fill brush, so without custom hit-testing it is TRANSPARENT to pointer input (hit-tests
    /// fall through to whatever sits behind it) and the viewport never receives clicks. The
    /// bounds check is ESSENTIAL: a custom hit-test REPLACES the geometry check outright and is
    /// consulted for candidate points anywhere in the window (delivered in this control's local
    /// space, which can be negative / beyond the bounds — verified empirically, the interface's
    /// "global coordinate space" doc comment predates the compositing renderer). Returning true
    /// unconditionally therefore makes every pane claim every point, and the topmost
    /// (last-rendered = bottom-right) pane wins ALL pointer input app-wide — the reported
    /// 4-pane only-bottom-right-selectable bug.
    /// </summary>
    public bool HitTest(Point point) =>
        point.X >= 0 && point.Y >= 0 && point.X < Bounds.Width && point.Y < Bounds.Height;

    private static readonly IReadOnlyList<LineSegment> NoLines = Array.Empty<LineSegment>();

    private readonly ViewportInputRouter _router;
    private readonly CommandDispatcher _dispatcher;
    private readonly object _gate = new();

    private ICameraScheme _scheme;
    private ViewType _viewType;
    private RenderMode _mode = RenderMode.TexturesAndLightmaps;
    private FogSettings _fog = FogSettings.Off;
    private bool _disableCull;
    private float _animationTime;

    private Func<IViewportSurface, int, int, bool>? _gizmoHitTestAt;

    // ---- Render-thread state (touched only inside the OnOpenGl* callbacks) ----
    private AvaloniaGlContext? _context;
    private GraphicsDevice? _device;
    private Rendering.Viewport? _viewport;
    private RenderScene? _renderScene;
    private AssetVfs? _renderVfs;
    private string? _initError;
    private bool _initErrorLogged;

    // ---- Cross-thread hand-off (guarded by _gate) ----
    private RenderScene? _pendingScene;
    private AssetVfs? _pendingVfs;
    private bool _sceneDirty;
    private IReadOnlyList<LineSegment> _pendingSelection = NoLines;
    private bool _selectionDirty;
    private IReadOnlyList<LineSegment> _pendingOverlay = NoLines;
    private bool _overlayDirty;
    private CameraPose _pose = CameraPose.Default;
    private bool _hasPendingPick;
    private int _pickX, _pickY;
    private bool _pickCtrl;
    private int _pixelWidth = 1;
    private int _pixelHeight = 1;

    // ---- UI-thread state ----
    private readonly Rendering.Camera _uiCamera = new();
    private IReadOnlyList<LineSegment> _selection = NoLines;
    private IReadOnlyList<LineSegment> _overlay = NoLines;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private DispatcherTimer? _timer;
    private double _lastTime;
    private double _fpsAccum;
    private int _fpsFrames;
    private double _fps;

    public GlViewportSurface(CommandDispatcher dispatcher, CameraSchemeKind scheme, ViewType viewType)
    {
        _router = new ViewportInputRouter(this);
        _dispatcher = dispatcher;
        _scheme = CameraSchemes.Create(scheme);
        _viewType = viewType;
        _router.Input.Ortho = viewType != ViewType.Perspective;
        ApplyViewTypeToPose();
        Focusable = true;

        // NOT a TAB stop: TAB is the viewport maximize/restore hotkey (handled by the
        // MainWindow tunnel before traversal), and the D3D11 panes are native child windows
        // that Avalonia focus-traversal cannot visit at all. Without this, any TAB that
        // escapes the tunnel would cycle keyboard focus across the four composited panes —
        // focusable-but-not-traversable is the exact native-pane parity.
        IsTabStop = false;
    }

    /// <summary>The shared input state (driven by the router).</summary>
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
    public bool GizmoActive
    {
        get => _router.GizmoActive;
        set => _router.GizmoActive = value;
    }

    public Func<IViewportSurface, int, int, bool>? GizmoHitTestAt
    {
        get => _gizmoHitTestAt;
        set
        {
            _gizmoHitTestAt = value;
            _router.GizmoHitTestAt = value is null ? null : (x, y) => value(this, x, y);
        }
    }

    public bool MarqueeEnabled
    {
        get => _router.MarqueeEnabled;
        set => _router.MarqueeEnabled = value;
    }

    public bool PointPickArmed
    {
        get => _router.PointPickArmed;
        set => _router.PointPickArmed = value;
    }

    public bool DrawToolArmed
    {
        get => _router.DrawToolArmed;
        set => _router.DrawToolArmed = value;
    }

    public bool RulerArmed
    {
        get => _router.RulerArmed;
        set => _router.RulerArmed = value;
    }

    public Func<KeyGesture, bool>? KeyPreDispatch
    {
        get => _router.KeyPreDispatch;
        set => _router.KeyPreDispatch = value;
    }

    /// <summary>Optional render-thread hook: the RGBA framebuffer readback after a frame (for capture/smoke verification).</summary>
    public Action<int, int, byte[]>? FrameCaptured { get; set; }

    /// <summary>Message from the last GL initialization/render failure, or null.</summary>
    public string? InitError => _initError;

    public int SurfaceHeight => PixelSize().Height;

    public (int Width, int Height) SurfaceSize => PixelSize();

    public (Vector3 Origin, Vector3 Direction)? LastPickRay => PixelRay(_pickX, _pickY);

    public bool ScrollMode => _router.ScrollMode;

    public ViewType ViewType => _viewType;

    public CameraSchemeKind SchemeKind => _scheme.Kind;

    public double Fps => _fps;

    /// <summary>A UI-thread camera synced from the persisted pose + current pixel size, for
    /// pixel/world queries (ray-picks, gizmo hit-tests, snap). The render-thread camera lives
    /// on the GL device and is never touched here.</summary>
    public Rendering.Camera? Camera
    {
        get
        {
            SyncUiCamera();
            return _uiCamera;
        }
    }

    /// <summary>True while the pointer is inside this pane (drives active-pane focus / TAB routing).</summary>
    public bool IsPointerInside { get; private set; }

    public Vector3 CameraPosition
    {
        get
        {
            lock (_gate)
            {
                return _pose.Position;
            }
        }
    }

    public Vector3 CameraForward
    {
        get
        {
            CameraPose pose;
            lock (_gate)
            {
                pose = _pose;
            }

            if (pose.Projection == CameraProjection.Perspective)
            {
                float cp = MathF.Cos(pose.Pitch);
                return Vector3.Normalize(new Vector3(
                    cp * MathF.Sin(pose.Yaw), MathF.Sin(pose.Pitch), cp * MathF.Cos(pose.Yaw)));
            }

            SyncUiCamera();
            return _uiCamera.Forward;
        }
    }

    public RenderMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            RequestNextFrameRendering();
            ModeChanged?.Invoke(value);
        }
    }

    public float CameraSpeed
    {
        get => _input.Speed;
        set => _input.Speed = value;
    }

    public FogSettings Fog
    {
        get => _fog;
        set
        {
            _fog = value;
            RequestNextFrameRendering();
        }
    }

    public bool DisableBackfaceCulling
    {
        get => _disableCull;
        set
        {
            _disableCull = value;
            RequestNextFrameRendering();
        }
    }

    public bool AltHeld => (_input.Modifiers & GestureModifiers.Alt) != 0;

    public bool SnapInvertHeld => AltHeld;

    public float AnimationTime
    {
        get => _animationTime;
        set
        {
            _animationTime = value;
            RequestNextFrameRendering();
        }
    }

    public bool IsInteracting => _input.LeftDown || _input.RightDown || _input.MiddleDown;

    public (Vector3 Origin, Vector3 Direction)? PixelRay(int x, int y)
    {
        SyncUiCamera();
        (int w, int h) = PixelSize();
        return _uiCamera.PixelRay(x, y, w, h);
    }

    public bool WorldToScreen(Vector3 world, out Vector2 screen)
    {
        SyncUiCamera();
        (int w, int h) = PixelSize();
        return _uiCamera.WorldToScreen(world, w, h, out screen);
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
        _input.Ortho = type != ViewType.Perspective;
        ApplyViewTypeToPose();
        if (invalidate)
        {
            RequestNextFrameRendering();
        }
    }

    /// <summary>Uploads a scene and frames the camera between the given endpoints (UI thread).</summary>
    public void LoadScene(RenderScene scene, AssetVfs? vfs, Vector3 cameraPosition, Vector3 cameraTarget)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (_gate)
        {
            _pendingScene = scene;
            _pendingVfs = vfs;
            _sceneDirty = true;
            if (_viewType == ViewType.Perspective)
            {
                _pose.Position = cameraPosition;
                _pose.LookAt(cameraPosition, cameraTarget);
            }
            else
            {
                _pose.Position = cameraTarget;
            }
        }

        RequestNextFrameRendering();
    }

    /// <summary>Re-uploads the current scene + selection + overlay (grid/brightness rebuild).</summary>
    public void RefreshScene(RenderScene scene, AssetVfs? vfs)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (_gate)
        {
            _pendingScene = scene;
            _pendingVfs = vfs;
            _sceneDirty = true;
            _pendingSelection = _selection;
            _selectionDirty = true;
            _pendingOverlay = _overlay;
            _overlayDirty = true;
        }

        RequestNextFrameRendering();
    }

    public void SetSelection(IReadOnlyList<LineSegment> lines)
    {
        _selection = lines ?? NoLines;
        lock (_gate)
        {
            _pendingSelection = _selection;
            _selectionDirty = true;
        }

        RequestNextFrameRendering();
    }

    public void SetGizmoOverlay(IReadOnlyList<LineSegment> lines)
    {
        _overlay = lines ?? NoLines;
        lock (_gate)
        {
            _pendingOverlay = _overlay;
            _overlayDirty = true;
        }

        RequestNextFrameRendering();
    }

    public void Frame(Ged.Core.Model.Aabb bounds) => ModifyPose(cam => cam.Frame(bounds));

    public void FramePoint(Vector3 p)
    {
        const float r = 4f;
        Frame(new Ged.Core.Model.Aabb(
            new Ged.Core.Model.Vec3(p.X - r, p.Y - r, p.Z - r),
            new Ged.Core.Model.Vec3(p.X + r, p.Y + r, p.Z + r)));
    }

    public void ViewFrom(Vector3 p) => ModifyPose(cam => cam.Position = p);

    public void ViewFrom(Vector3 p, Vector3 forward)
    {
        if (forward.LengthSquared() < 1e-6f)
        {
            ViewFrom(p);
            return;
        }

        Vector3 target = p + Vector3.Normalize(forward);
        ModifyPose(cam => cam.LookAt(p, target));
    }

    public void AxisOrient() => ModifyPose(cam => cam.OrientToNearestAxis());

    public void Bank(float degrees) => ModifyPose(cam => cam.Roll += degrees * MathF.PI / 180f);

    public void ToggleScrollMode()
    {
        _router.ToggleScrollMode();
        RequestNextFrameRendering();
    }

    /// <summary>Requests a repaint on the next compositor frame.</summary>
    public void Invalidate() => RequestNextFrameRendering();

    // ---- OpenGlControlBase (render thread; context current) ----

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _context = new AvaloniaGlContext(gl);
            _device = GraphicsDevice.CreateOpenGlHosted(_context);
            (int w, int h) = PixelSize();
            _viewport = new Rendering.Viewport(_device, hwnd: 0, w, h) { Mode = _mode, Fog = _fog, Time = _animationTime, DisableBackfaceCulling = _disableCull };
        }
        catch (Exception ex)
        {
            RecordGlError("init", ex.Message);
        }
    }

    /// <summary>
    /// Records a composited-GL init/render failure and stamps it to <c>session.log</c> ONCE. Without this a
    /// GL failure — e.g. a host that hands <c>OpenGlControlBase</c> a GL ES context that cannot compile the
    /// RHI's desktop GLSL-330 shaders (observed on Avalonia's X11 EGL path under software Mesa) — leaves only
    /// a silent black pane with no diagnostic. The one-time guard keeps a per-frame render failure from
    /// flooding the log.
    /// </summary>
    private void RecordGlError(string phase, string message)
    {
        _initError = message;
        if (!_initErrorLogged)
        {
            _initErrorLogged = true;
            CrashHandler.LogInfo("viewport", $"OpenGL {phase} failed: {message}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _viewport?.Dispose();
        _viewport = null;
        _device?.Dispose();
        _device = null;
        _context = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_viewport is null || _context is null)
        {
            return;
        }

        try
        {
            // Bind exactly the framebuffer Avalonia handed us (usually a nonzero FBO).
            _context.Framebuffer = (uint)fb;

            RenderScene? newScene = null;
            AssetVfs? newVfs = null;
            IReadOnlyList<LineSegment>? newSelection = null;
            IReadOnlyList<LineSegment>? newOverlay = null;
            CameraPose pose;
            bool pick;
            int px, py, w, h;
            bool ctrl;
            lock (_gate)
            {
                if (_sceneDirty)
                {
                    newScene = _pendingScene;
                    newVfs = _pendingVfs;
                    _sceneDirty = false;
                }

                if (_selectionDirty)
                {
                    newSelection = _pendingSelection;
                    _selectionDirty = false;
                }

                if (_overlayDirty)
                {
                    newOverlay = _pendingOverlay;
                    _overlayDirty = false;
                }

                pose = _pose;
                pick = _hasPendingPick;
                px = _pickX;
                py = _pickY;
                ctrl = _pickCtrl;
                _hasPendingPick = false;
                w = _pixelWidth;
                h = _pixelHeight;
            }

            if (w != _viewport.Width || h != _viewport.Height)
            {
                _viewport.Resize(w, h);
            }

            if (newScene is not null)
            {
                _viewport.SetScene(newScene, newVfs);
                _renderScene = newScene;
                _renderVfs = newVfs;
            }

            if (newSelection is not null)
            {
                _viewport.SetSelection(newSelection);
            }

            if (newOverlay is not null)
            {
                _viewport.SetGizmoOverlay(newOverlay);
            }

            _viewport.Mode = _mode;
            _viewport.Fog = _fog;
            _viewport.DisableBackfaceCulling = _disableCull;
            _viewport.Time = _animationTime;
            pose.ApplyTo(_viewport.Camera);
            _viewport.Render();

            if (pick)
            {
                PickId id = _viewport.Pick(px, py);
                Dispatcher.UIThread.Post(() => _router.RaisePicked(id, ctrl));
            }

            // Verification hook (smoke only): re-render the same scene/camera to an offscreen
            // readback on this hosted device so a caller can prove the GL host drew the real
            // composited scene, without racing the compositor's own buffers.
            if (FrameCaptured is { } cap && _renderScene is not null)
            {
                byte[] rgba = OffscreenRenderer.Render(_device!, _renderScene, _renderVfs, _viewport.Camera, _mode, w, h);
                cap(w, h, rgba);
            }
        }
        catch (Exception ex)
        {
            RecordGlError("render", ex.Message);
        }
    }

    // ---- UI-thread lifecycle ----

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += OnTick;
        _timer.Start();
        _lastTime = _clock.Elapsed.TotalSeconds;
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _lastTime, 0.0, 0.1);
        _lastTime = now;

        UpdatePixelSize();

        // Drive continuous camera motion (WASD fly, ortho slides) on the UI-side pose; the
        // render thread applies it to its own camera each frame.
        var cam = new Rendering.Camera();
        CameraPose before;
        lock (_gate)
        {
            before = _pose;
        }

        before.ApplyTo(cam);
        cam.AspectRatio = Aspect();
        _scheme.Move(cam, dt, _input);
        CameraPose after = CameraPose.Capture(cam);
        bool moved = !before.Equals(after);
        if (moved)
        {
            lock (_gate)
            {
                _pose = after;
            }

            RequestNextFrameRendering();
        }
        else if (_scheme.IsNavigating(_input))
        {
            RequestNextFrameRendering();
        }

        _fpsFrames++;
        _fpsAccum += dt;
        if (_fpsAccum >= 0.5)
        {
            _fps = _fpsFrames / _fpsAccum;
            StatsUpdated?.Invoke(_fps, after.Position);
            _fpsFrames = 0;
            _fpsAccum = 0;
        }
    }

    // ---- Avalonia input → shared router (the same IViewportInput the D3D11 pane drives) ----

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        ((IViewportInput)this).OnPointerActivate();
        Focus();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ((IViewportInput)this).OnPointerLeave();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint p = e.GetCurrentPoint(this);
        (int x, int y) = ToPixels(p.Position);
        SyncModifiers(e.KeyModifiers);
        switch (p.Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.LeftButtonPressed:
                ((IViewportInput)this).OnButton(ViewportButton.Left, down: true, x, y);
                break;
            case PointerUpdateKind.RightButtonPressed:
                ((IViewportInput)this).OnButton(ViewportButton.Right, down: true, x, y);
                break;
            case PointerUpdateKind.MiddleButtonPressed:
                ((IViewportInput)this).OnButton(ViewportButton.Middle, down: true, x, y);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        (int x, int y) = ToPixels(e.GetPosition(this));
        SyncModifiers(e.KeyModifiers);
        ViewportButton? button = e.InitialPressMouseButton switch
        {
            MouseButton.Left => ViewportButton.Left,
            MouseButton.Right => ViewportButton.Right,
            MouseButton.Middle => ViewportButton.Middle,
            _ => null,
        };
        if (button is ViewportButton b)
        {
            ((IViewportInput)this).OnButton(b, down: false, x, y);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // D3D11-WndProc parity: WM_MOUSEMOVE fires OnPointerActivate on EVERY move, not just
        // on enter, so the active pane keeps following the cursor even when an enter event is
        // missed (e.g. the pane was re-parented under a stationary pointer by a TAB layout
        // rebuild). ViewportGrid dedupes repeats. Refresh keyboard focus with it so camera
        // keys follow the hover exactly like the native pane's SetFocus path.
        if (!IsPointerInside)
        {
            Focus();
        }

        ((IViewportInput)this).OnPointerActivate();

        (int x, int y) = ToPixels(e.GetPosition(this));
        SyncModifiers(e.KeyModifiers);
        ((IViewportInput)this).OnMouseMove(x, y);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SyncModifiers(e.KeyModifiers);
        ((IViewportInput)this).OnWheel((int)(e.Delta.Y * 120));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (RouteKey(e.Key, down: true))
        {
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (RouteKey(e.Key, down: false))
        {
            e.Handled = true;
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        ((IViewportInput)this).OnFocusLost();
    }

    private bool RouteKey(Key key, bool down)
    {
        int vk = GestureConvert.AvaloniaKeyToVirtualKey(key);
        if (vk < 0)
        {
            return false;
        }

        ((IViewportInput)this).OnKey(vk, down, extended: false);
        return true;
    }

    private void SyncModifiers(KeyModifiers modifiers)
    {
        GestureModifiers m = GestureModifiers.None;
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            m |= GestureModifiers.Ctrl;
        }

        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            m |= GestureModifiers.Shift;
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            m |= GestureModifiers.Alt;
        }

        _input.Modifiers = m;
    }

    // ---- IViewportInput (the shared raw-input entry points; also driven directly by tests) ----

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

    // ---- IViewportInputHost (the router's surface-specific callbacks) ----

    ViewType IViewportInputHost.ViewType => _viewType;

    bool IViewportInputHost.IsNavigating() => _scheme.IsNavigating(_input);

    bool IViewportInputHost.SchemeConsumesKey(string token)
    {
        SyncUiCamera();
        return _scheme.ConsumesKey(_uiCamera, token);
    }

    void IViewportInputHost.CameraDrag(int dx, int dy, bool scrollPan) => ModifyPose(
        cam =>
        {
            if (scrollPan)
            {
                cam.MoveLocal(-dx * 0.05f, dy * 0.05f, 0f);
            }
            else
            {
                _scheme.Drag(cam, dx, dy, _input);
            }
        },
        requestRender: false);

    void IViewportInputHost.CameraWheel(int delta)
    {
        (int w, int h) = PixelSize();
        _input.ViewWidth = w;
        _input.ViewHeight = h;
        ModifyPose(cam => _scheme.Wheel(cam, delta, _input), requestRender: false);
    }

    void IViewportInputHost.RequestPick(int x, int y, bool ctrl)
    {
        lock (_gate)
        {
            _pickX = x;
            _pickY = y;
            _pickCtrl = ctrl;
            _hasPendingPick = true;
        }
    }

    void IViewportInputHost.RequestRender() => RequestNextFrameRendering();

    void IViewportInputHost.DispatchGesture(KeyGesture gesture) => _dispatcher.Dispatch(gesture);

    bool IViewportInputHost.TryWorldPoint(int x, int y, out Vector3 world)
    {
        SyncUiCamera();
        (int w, int h) = PixelSize();
        float nx = ((x / (float)Math.Max(1, w)) * 2f) - 1f;
        float ny = ((y / (float)Math.Max(1, h)) * 2f) - 1f;
        float halfH = _uiCamera.OrthoZoom;
        float halfW = halfH * Math.Max(_uiCamera.AspectRatio, 0.01f);
        world = _uiCamera.Position + (_uiCamera.Right * (nx * halfW)) - (_uiCamera.Up * (ny * halfH));
        return true;
    }

    void IViewportInputHost.OrthoTeleport(int x, int y) => ModifyPose(
        cam =>
        {
            (int w, int h) = PixelSize();
            float nx = ((x / (float)Math.Max(1, w)) * 2f) - 1f;
            float ny = ((y / (float)Math.Max(1, h)) * 2f) - 1f;
            float halfH = cam.OrthoZoom;
            float halfW = halfH * Math.Max(cam.AspectRatio, 0.01f);
            cam.Position += (cam.Right * (nx * halfW)) - (cam.Up * (ny * halfH));
        });

    // GL relies on Avalonia's reliable KeyUp + lost-focus to release held keys, so no
    // physical-key reconciliation is needed (and none is available cross-platform).
    bool IViewportInputHost.IsKeyPhysicallyDown(int virtualKey) => true;

    // ---- Helpers ----

    private void ApplyViewTypeToPose()
    {
        lock (_gate)
        {
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
    }

    /// <summary>Applies a camera mutation to the persisted pose (UI thread) and re-renders.</summary>
    private void ModifyPose(Action<Rendering.Camera> mutate, bool requestRender = true)
    {
        var cam = new Rendering.Camera();
        CameraPose pose;
        lock (_gate)
        {
            pose = _pose;
        }

        pose.ApplyTo(cam);
        cam.AspectRatio = Aspect();
        mutate(cam);
        lock (_gate)
        {
            _pose = CameraPose.Capture(cam);
        }

        if (requestRender)
        {
            RequestNextFrameRendering();
        }
    }

    private void SyncUiCamera()
    {
        CameraPose pose;
        int w, h;
        lock (_gate)
        {
            pose = _pose;
            w = _pixelWidth;
            h = _pixelHeight;
        }

        pose.ApplyTo(_uiCamera);
        _uiCamera.AspectRatio = (float)w / Math.Max(1, h);
    }

    private float Aspect()
    {
        (int w, int h) = PixelSize();
        return (float)w / Math.Max(1, h);
    }

    private (int Width, int Height) PixelSize()
    {
        lock (_gate)
        {
            return (_pixelWidth, _pixelHeight);
        }
    }

    private void UpdatePixelSize()
    {
        double scale = (this.GetVisualRoot()?.RenderScaling) ?? 1.0;
        int w = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        int h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        lock (_gate)
        {
            _pixelWidth = w;
            _pixelHeight = h;
        }
    }

    private (int X, int Y) ToPixels(Point p)
    {
        double scale = (this.GetVisualRoot()?.RenderScaling) ?? 1.0;
        return ((int)Math.Round(p.X * scale), (int)Math.Round(p.Y * scale));
    }
}
