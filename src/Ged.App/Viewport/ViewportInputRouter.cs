using System;
using System.Numerics;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.Core.Input;

namespace Ged.App.Viewport;

/// <summary>
/// The surface-specific operations the shared <see cref="ViewportInputRouter"/> calls
/// back into: camera navigation, pick requests, repaint, gesture dispatch and the
/// ortho world-point math. The router owns every editing gesture; the host owns the
/// backend-specific plumbing (Direct3D 11 native HWND on Windows, composited OpenGL on
/// all platforms). Both are driven by the SAME router instance, so a gesture behaves
/// identically on either backend.
/// </summary>
internal interface IViewportInputHost
{
    /// <summary>This pane's view direction (perspective or a fixed ortho axis).</summary>
    ViewType ViewType { get; }

    /// <summary>True when the active camera scheme is actively driving a navigation drag/fly.</summary>
    bool IsNavigating();

    /// <summary>True when the active camera scheme consumes <paramref name="token"/> as a bare camera key.</summary>
    bool SchemeConsumesKey(string token);

    /// <summary>Navigates the camera by a pointer drag (scroll-pan or scheme drag).</summary>
    void CameraDrag(int dx, int dy, bool scrollPan);

    /// <summary>Navigates the camera by a wheel notch (the host reads its own client size).</summary>
    void CameraWheel(int delta);

    /// <summary>Requests a pick at the pixel; the completion posts back through <see cref="ViewportInputRouter.RaisePicked"/>.</summary>
    void RequestPick(int x, int y, bool ctrl);

    /// <summary>Marks the surface dirty / schedules a repaint.</summary>
    void RequestRender();

    /// <summary>Routes a resolved gesture to the shared command dispatcher.</summary>
    void DispatchGesture(KeyGesture gesture);

    /// <summary>The world point under an ortho pixel (two-point clip picking); false if unavailable.</summary>
    bool TryWorldPoint(int x, int y, out Vector3 world);

    /// <summary>Moves the ortho pan centre to the world point under the cursor (Ctrl+RMB).</summary>
    void OrthoTeleport(int x, int y);

    /// <summary>True when the physical key for <paramref name="virtualKey"/> is currently down.</summary>
    bool IsKeyPhysicallyDown(int virtualKey);

    /// <summary>
    /// True when <see cref="IsKeyPhysicallyDown"/> reflects the REAL OS keyboard (the Direct3D
    /// native pane, backed by <c>GetAsyncKeyState</c>). The router then reconciles its modifier
    /// bitfield from physical state on every key event / focus gain, so a swallowed modifier
    /// KeyUp (alt-tab, a modal dialog) can never leave a phantom Ctrl/Shift/Alt latched. The GL
    /// pane returns false — its <see cref="IsKeyPhysicallyDown"/> is unconditionally true, so it
    /// stays on the Avalonia-modifier route (its surface syncs modifiers from <c>KeyModifiers</c>
    /// on each key event instead).
    /// </summary>
    bool UsesPhysicalKeyState { get; }

    /// <summary>
    /// True when the active camera scheme treats a bare left button as click-to-select /
    /// drag-to-navigate (UnrealEd): a press without a drag click-picks, a drag navigates the
    /// camera, and there is no plain-left marquee. False for the marquee schemes, where a bare
    /// left drag box-selects.
    /// </summary>
    bool LeftClickSelectsDragNavigates { get; }
}

/// <summary>
/// The shared editing-gesture state machine for a viewport pane. Raw input (Win32
/// virtual keys + pixels from the D3D11 native WndProc, or the same values translated
/// from Avalonia events by the GL surface) flows through <see cref="OnKey"/> /
/// <see cref="OnMouseMove"/> / <see cref="OnButton"/> / <see cref="OnWheel"/>, and this
/// class decides what each input MEANS given the current gesture state — transform drags
/// (M/R/N), marquee box-select, gizmo handle drag, draw-brush, ruler, point-pick,
/// pick-click routing and keymap dispatch — raising the high-level events the editor
/// consumes. Surface-specific work (camera nav, pick execution, repaint) is delegated to
/// the <see cref="IViewportInputHost"/>. This is the single source of gesture truth: the
/// two backends do not duplicate any of it.
/// </summary>
internal sealed class ViewportInputRouter
{
    private readonly IViewportInputHost _host;

    private int _lastMouseX, _lastMouseY;
    private char _transformKey; // 'M', 'R' or 'N' while held, else '\0'
    private bool _draggingBrush;
    private bool _scrollMode;

    // Manipulator drag, marquee box-select, and two-point clip point-picking.
    private bool _gizmoDragging;
    private bool _selectMaybe;   // LMB pressed (not on a gizmo handle): pending click-pick, marquee, or nav-drag
    private bool _selectDragged;
    private bool _marqueeActive;
    private bool _leftDragNavigates; // UnrealEd: a select-armed left DRAG becomes camera navigation, not a marquee
    private int _selStartX, _selStartY;

    public ViewportInputRouter(IViewportInputHost host) => _host = host;

    /// <summary>The shared input state (modifiers, held keys, button flags, pointer, speed).
    /// The surface's camera scheme reads/writes this same instance.</summary>
    public ViewportInputState Input { get; } = new();

    // ---- Editing-gesture events ----
    public event Action<Ged.Rendering.Picking.PickId, bool>? Picked;

    public event Action<Vector3>? NudgeMove;

    public event Action<Vector3>? NudgeRotate;

    public event Action? BrushDragStarted;

    public event Action<int, int, bool>? BrushDragPixels;

    public event Action? BrushDragEnded;

    public event Action<int, int>? GizmoDragStarted;

    public event Action<int, int>? GizmoDragMovedTo;

    public event Action? GizmoDragEnded;

    public event Action? GizmoDragCancelled;

    public event Action<int, int>? GizmoHover;

    public event Action<int, int>? MarqueeStarted;

    public event Action<int, int>? MarqueeMovedTo;

    public event Action<int, int, bool>? MarqueeEnded;

    public event Action<Vector3>? WorldPointPicked;

    public event Action<int, int>? DrawHover;

    public event Action<int, int>? DrawClick;

    public event Action? DrawCancelRequested;

    public event Action<int, int>? RulerClick;

    public event Action<int, int>? RulerHover;

    public event Action? RulerCancelRequested;

    // ---- Arming flags / hooks ----
    public bool GizmoActive { get; set; }

    public Func<int, int, bool>? GizmoHitTestAt { get; set; }

    public bool MarqueeEnabled { get; set; }

    public bool PointPickArmed { get; set; }

    public bool DrawToolArmed { get; set; }

    public bool RulerArmed { get; set; }

    public Func<KeyGesture, bool>? KeyPreDispatch { get; set; }

    /// <summary>Stock END: scroll (pan-drag) mode.</summary>
    public bool ScrollMode => _scrollMode;

    public void ToggleScrollMode() => _scrollMode = !_scrollMode;

    /// <summary>Lets the host post a completed pick back to the editor.</summary>
    public void RaisePicked(Ged.Rendering.Picking.PickId id, bool ctrl) => Picked?.Invoke(id, ctrl);

    // ---- Raw input (from the native WndProc, or translated from Avalonia events) ----

    public void OnKey(int virtualKey, bool down, bool extended)
    {
        UpdateModifier(virtualKey, down);
        ReconcileModifiersFromPhysical();

        // ESC cancels an in-progress manipulator drag (revert) or marquee.
        if (down && virtualKey == 0x1B)
        {
            // The draw-brush / ruler tools cancel first: while armed, plain clicks were
            // consumed by them, so no gizmo drag or marquee can be in progress here.
            if (RulerArmed)
            {
                RulerCancelRequested?.Invoke();
                _host.RequestRender();
                return;
            }

            if (DrawToolArmed)
            {
                DrawCancelRequested?.Invoke();
                _host.RequestRender();
                return;
            }

            if (_gizmoDragging)
            {
                _gizmoDragging = false;
                GizmoDragCancelled?.Invoke();
                _host.RequestRender();
                return;
            }

            if (_marqueeActive || _selectMaybe)
            {
                _marqueeActive = false;
                _selectMaybe = false;
                MarqueeEnded?.Invoke(_selStartX, _selStartY, false); // collapsed rect selects nothing
                _host.RequestRender();
                return;
            }
        }

        // Transform modifier keys (M move, R rotate, N axis-constrained) are held,
        // not dispatched — they arm the arrow/drag transform gestures.
        if (virtualKey is 0x4D or 0x52 or 0x4E)
        {
            _transformKey = down ? (char)virtualKey : '\0';
            _host.RequestRender();
            return;
        }

        // Arrow / PageUp / PageDown while a transform key is held → nudge the selection.
        if (down && _transformKey != '\0' && IsTransformDirectionKey(virtualKey))
        {
            HandleNudge(virtualKey);
            _host.RequestRender();
            return;
        }

        string? token = KeyToken(virtualKey, extended);
        if (token is not null)
        {
            if (down)
            {
                Input.HeldKeys.Add(token);
            }
            else
            {
                Input.HeldKeys.Remove(token);
            }
        }

        if (down)
        {
            // Route the key to the shared keymap unless the active camera scheme is
            // actively driving it as a camera control. A modifier chord is NEVER owned:
            // Shift is a speed-boost STATE the movement ops read, not a consumed key.
            KeyGesture? g = GestureConvert.FromVirtualKey(virtualKey, Input.Modifiers, extended);
            if (g is KeyGesture gesture && !IsPureModifier(virtualKey) && !SchemeOwnsKey(token))
            {
                if (KeyPreDispatch?.Invoke(gesture) != true)
                {
                    _host.DispatchGesture(gesture);
                }
            }
        }

        _host.RequestRender();
    }

    public void OnMouseMove(int x, int y)
    {
        int dx = x - _lastMouseX;
        int dy = y - _lastMouseY;
        _lastMouseX = x;
        _lastMouseY = y;
        Input.PointerX = x;
        Input.PointerY = y;

        if (_draggingBrush && Input.LeftDown)
        {
            BrushDragPixels?.Invoke(dx, dy, _transformKey == 0x4E);
            _host.RequestRender();
            return;
        }

        if (_gizmoDragging && Input.LeftDown)
        {
            GizmoDragMovedTo?.Invoke(x, y); // absolute pixel → the App unprojects a world ray
            _host.RequestRender();
            return;
        }

        if (_selectMaybe && Input.LeftDown)
        {
            if (!_marqueeActive && (Math.Abs(x - _selStartX) + Math.Abs(y - _selStartY)) > 3)
            {
                _selectDragged = true;
                if (_leftDragNavigates)
                {
                    // UnrealEd click-vs-drag: a left DRAG is camera navigation (dolly + turn), not a
                    // marquee. Drop the select-arm so this move — and the rest of the drag — falls
                    // through to CameraDrag below; a release without a drag still click-selects.
                    _selectMaybe = false;
                }
                else if (MarqueeEnabled)
                {
                    _marqueeActive = true;
                    MarqueeStarted?.Invoke(_selStartX, _selStartY);
                }
            }

            if (_marqueeActive)
            {
                MarqueeMovedTo?.Invoke(x, y);
                _host.RequestRender();
                return;
            }

            if (_selectMaybe)
            {
                return; // still an armed select (sub-threshold, or a no-marquee scheme): never navigate yet
            }

            // Otherwise the drag was just promoted to camera navigation — fall through to CameraDrag.
        }

        // Draw-brush tool: idle pointer moves feed the hover preview. Button-held moves
        // fall through so camera navigation keeps working while the tool is armed.
        if (DrawToolArmed && !Input.LeftDown && !Input.RightDown && !Input.MiddleDown)
        {
            DrawHover?.Invoke(x, y);
            _host.RequestRender();
            return;
        }

        // Ruler tool: idle moves feed the live distance preview.
        if (RulerArmed && !Input.LeftDown && !Input.RightDown && !Input.MiddleDown)
        {
            RulerHover?.Invoke(x, y);
            _host.RequestRender();
            return;
        }

        // Hover-highlight the gizmo handle under the idle cursor.
        if (GizmoActive && !_gizmoDragging && !Input.LeftDown && !Input.RightDown && !Input.MiddleDown)
        {
            GizmoHover?.Invoke(x, y);
        }

        bool pan = _scrollMode && Input.LeftDown;
        if (Input.LeftDown || Input.RightDown || Input.MiddleDown)
        {
            _host.CameraDrag(dx, dy, pan);
            _host.RequestRender();
        }
    }

    public void OnButton(ViewportButton button, bool down, int x, int y)
    {
        _lastMouseX = x;
        _lastMouseY = y;
        Input.PointerX = x;
        Input.PointerY = y;

        // Re-derive modifiers from the real keyboard before a click reads them (D3D pane only; a
        // no-op on GL, which already syncs KeyModifiers on every pointer event). Without this, a
        // Ctrl KeyUp swallowed by a focus change would leave a phantom Ctrl latched, turning the
        // next plain click into an additive toggle — a "sometimes can't select" symptom.
        ReconcileModifiersFromPhysical();

        switch (button)
        {
            case ViewportButton.Left:
                Input.LeftDown = down;
                bool ctrl = (Input.Modifiers & GestureModifiers.Ctrl) != 0;
                if (down && _transformKey is (char)0x4D or (char)0x4E)
                {
                    _draggingBrush = true; // M/N + LMB drag-move (not a pick)
                    BrushDragStarted?.Invoke();
                }
                else if (!down && _draggingBrush)
                {
                    _draggingBrush = false;
                    BrushDragEnded?.Invoke();
                }
                else if (!down && _gizmoDragging)
                {
                    _gizmoDragging = false;
                    GizmoDragEnded?.Invoke();
                }
                else if (!down && _marqueeActive)
                {
                    _marqueeActive = false;
                    _selectMaybe = false;
                    MarqueeEnded?.Invoke(x, y, ctrl);
                }
                else if (!down && _selectMaybe)
                {
                    // A press with no drag: a plain click-pick.
                    _selectMaybe = false;
                    if (!_selectDragged)
                    {
                        _host.RequestPick(x, y, ctrl);
                    }
                }
                else if (down && !_host.IsNavigating())
                {
                    _selectDragged = false;
                    if (RulerArmed)
                    {
                        RulerClick?.Invoke(x, y); // consume the press: measure, no pick/marquee
                    }
                    else if (DrawToolArmed)
                    {
                        DrawClick?.Invoke(x, y); // consume the press: no pick or marquee starts
                    }
                    else if (PointPickArmed && _host.ViewType != ViewType.Perspective)
                    {
                        if (_host.TryWorldPoint(x, y, out Vector3 world))
                        {
                            WorldPointPicked?.Invoke(world);
                        }
                    }
                    else if (GizmoActive && GizmoHitTestAt?.Invoke(x, y) == true)
                    {
                        _gizmoDragging = true; // press landed on a handle → drag exactly it
                        GizmoDragStarted?.Invoke(x, y);
                    }
                    else
                    {
                        // Any non-tool, non-handle press: a release-without-drag click-picks, a drag
                        // box-selects — or, under a click-select/drag-navigate scheme (UnrealEd),
                        // the drag navigates the camera instead of starting a marquee.
                        _selectMaybe = true;
                        _selStartX = x;
                        _selStartY = y;
                        _leftDragNavigates = _host.LeftClickSelectsDragNavigates;
                    }
                }

                break;
            case ViewportButton.Right:
                Input.RightDown = down;
                if (down && (Input.Modifiers & GestureModifiers.Ctrl) != 0 &&
                    _host.ViewType != ViewType.Perspective)
                {
                    _host.OrthoTeleport(x, y); // Ctrl+RMB ortho teleport
                }

                break;
            case ViewportButton.Middle:
                Input.MiddleDown = down;
                break;
        }

        if (!down && !Input.LeftDown && !Input.RightDown && !Input.MiddleDown)
        {
            Input.HeldKeys.Clear();
        }

        _host.RequestRender();
    }

    public void OnWheel(int delta)
    {
        _host.CameraWheel(delta);
        _host.RequestRender();
    }

    /// <summary>The native surface lost keyboard focus: drop the held-key set AND the modifier
    /// bitfield (a modifier whose KeyUp is swallowed by the focus change must not stay latched).</summary>
    public void OnFocusLost() => Input.ClearHeld();

    /// <summary>The native surface regained keyboard focus: re-derive the modifier bitfield from
    /// the real OS keyboard so a modifier physically held across the focus change (whose KeyDown
    /// the pane never saw) is picked up before the first key/mouse gesture. No-op on backends
    /// without real physical key state (GL).</summary>
    public void OnFocusGained() => ReconcileModifiersFromPhysical();

    // ---- Gesture helpers ----

    /// <summary>
    /// True when the active camera scheme is driving <paramref name="token"/> as a
    /// continuous camera control, so the key must NOT also fire a one-shot keymap command.
    /// A modifier chord is never owned — Shift is a speed-boost STATE, not a consumed key.
    /// </summary>
    private bool SchemeOwnsKey(string? token)
    {
        if (token is null || Input.Modifiers != GestureModifiers.None)
        {
            return false;
        }

        return _host.SchemeConsumesKey(token) || _host.IsNavigating();
    }

    private static bool IsTransformDirectionKey(int vk) => vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x21 or 0x22;

    private void HandleNudge(int vk)
    {
        if (_transformKey == 0x52)
        {
            NudgeRotate?.Invoke(RotateAxisFor(vk));
        }
        else
        {
            NudgeMove?.Invoke(MoveDirFor(vk));
        }
    }

    /// <summary>World move direction for an arrow/page key given this pane's view.</summary>
    private Vector3 MoveDirFor(int vk)
    {
        // Screen axes per ortho view; perspective uses the world X/Z plane + Y.
        (Vector3 right, Vector3 up, Vector3 depth) = _host.ViewType switch
        {
            ViewType.Top => (Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            ViewType.Bottom => (Vector3.UnitX, -Vector3.UnitZ, -Vector3.UnitY),
            ViewType.Front => (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
            ViewType.Back => (-Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ),
            ViewType.Left => (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
            ViewType.Right => (Vector3.UnitZ, Vector3.UnitY, -Vector3.UnitX),
            _ => (Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
        };
        return vk switch
        {
            0x25 => -right, // Left
            0x27 => right,  // Right
            0x26 => up,     // Up
            0x28 => -up,    // Down
            0x21 => depth,  // PageUp
            _ => -depth,    // PageDown
        };
    }

    /// <summary>World rotation axis (signed) for an arrow/page key.</summary>
    private static Vector3 RotateAxisFor(int vk) => vk switch
    {
        0x25 => Vector3.UnitY,   // Left  → +Y
        0x27 => -Vector3.UnitY,  // Right → -Y
        0x26 => Vector3.UnitX,   // Up    → +X
        0x28 => -Vector3.UnitX,  // Down  → -X
        0x21 => Vector3.UnitZ,   // PageUp → +Z
        _ => -Vector3.UnitZ,     // PageDown → -Z
    };

    private void UpdateModifier(int vk, bool down)
    {
        GestureModifiers flag = vk switch
        {
            0x10 => GestureModifiers.Shift,
            0x11 => GestureModifiers.Ctrl,
            0x12 => GestureModifiers.Alt,
            _ => GestureModifiers.None,
        };
        if (flag == GestureModifiers.None)
        {
            return;
        }

        if (down)
        {
            Input.Modifiers |= flag;
        }
        else
        {
            Input.Modifiers &= ~flag;
        }
    }

    /// <summary>
    /// On the native (Direct3D) pane, re-derives the modifier bitfield from the real OS keyboard
    /// (<c>GetAsyncKeyState</c> via the host), so a modifier whose KeyUp was swallowed by a focus
    /// change — alt-tab, or a modal Save-As / progress dialog stealing focus — cannot leave a
    /// phantom Ctrl/Shift/Alt latched and silently break Ctrl+Z / Ctrl+Y (and every other chord).
    /// Fixes BOTH a stuck-on modifier (lost KeyUp) and a stuck-off one (a KeyDown that arrived
    /// while another pane held focus). The GL pane has no real per-key state
    /// (<see cref="IViewportInputHost.UsesPhysicalKeyState"/> is false) and stays on the
    /// Avalonia-modifier route, so this is a no-op there.
    /// </summary>
    private void ReconcileModifiersFromPhysical()
    {
        if (!_host.UsesPhysicalKeyState)
        {
            return;
        }

        GestureModifiers m = GestureModifiers.None;
        if (_host.IsKeyPhysicallyDown(0x10))
        {
            m |= GestureModifiers.Shift;
        }

        if (_host.IsKeyPhysicallyDown(0x11))
        {
            m |= GestureModifiers.Ctrl;
        }

        if (_host.IsKeyPhysicallyDown(0x12))
        {
            m |= GestureModifiers.Alt;
        }

        Input.Modifiers = m;
    }

    private static bool IsPureModifier(int vk) => vk is 0x10 or 0x11 or 0x12;

    private static string? KeyToken(int vk, bool extended = false)
    {
        KeyGesture? g = GestureConvert.FromVirtualKey(vk, GestureModifiers.None, extended);
        return g?.Key;
    }
}
