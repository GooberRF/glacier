using System;
using System;
using System.Collections.Generic;
using System.Numerics;
using Ged.App;
using Ged.App.Viewport;
using Ged.Core.Input;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Direct regressions for the shared <see cref="ViewportInputRouter"/> — the single gesture
/// state machine BOTH viewport backends drive. A recording <see cref="IViewportInputHost"/>
/// captures the surface-specific callbacks (pick request, camera navigation, ortho world-point,
/// gated hotkey dispatch) so the camera-dependent outcomes that need a live GPU on a real
/// surface can be asserted here headlessly. Proving the router is correct proves both the
/// Direct3D 11 and OpenGL panes are correct, because neither duplicates any of this logic.
/// </summary>
public sealed class ViewportGestureRouterTests
{
    private sealed class RecordingHost : IViewportInputHost
    {
        public ViewType ViewType { get; set; } = ViewType.Perspective;

        public bool Navigating { get; set; }

        /// <summary>UnrealEd-style click-select / drag-navigate scheme flag (implicitly implements the host member).</summary>
        public bool LeftClickSelectsDragNavigates { get; set; }

        public Func<string, bool> Consumes { get; set; } = _ => false;

        public readonly List<string> Log = new();

        public (int X, int Y, bool Ctrl)? Pick { get; private set; }

        public Vector3 WorldPointResult { get; set; } = new(1f, 2f, 3f);

        /// <summary>The last gesture dispatched (key + modifiers), for chord assertions.</summary>
        public KeyGesture? LastGesture { get; private set; }

        /// <summary>Whether physical key state is real (D3D). Default false = GL/incremental route.</summary>
        public bool UsesPhysicalKeyState { get; set; }

        /// <summary>Configurable physical-down predicate, keyed by virtual-key code.</summary>
        public Func<int, bool> PhysicalDown { get; set; } = _ => true;

        ViewType IViewportInputHost.ViewType => ViewType;

        public bool IsNavigating() => Navigating;

        public bool SchemeConsumesKey(string token) => Consumes(token);

        public void CameraDrag(int dx, int dy, bool scrollPan) => Log.Add($"CameraDrag {dx},{dy},{scrollPan}");

        public void CameraWheel(int delta) => Log.Add($"CameraWheel {delta}");

        public void RequestPick(int x, int y, bool ctrl)
        {
            Pick = (x, y, ctrl);
            Log.Add($"RequestPick {x},{y},{ctrl}");
        }

        public void RequestRender() => Log.Add("RequestRender");

        public void DispatchGesture(KeyGesture gesture)
        {
            LastGesture = gesture;
            Log.Add($"Dispatch {gesture.Key}");
        }

        public bool TryWorldPoint(int x, int y, out Vector3 world)
        {
            world = WorldPointResult;
            return true;
        }

        public void OrthoTeleport(int x, int y) => Log.Add($"OrthoTeleport {x},{y}");

        // Implicitly implements IViewportInputHost.IsKeyPhysicallyDown / UsesPhysicalKeyState
        // (the public members above), so the router reads the configurable test state.
        public bool IsKeyPhysicallyDown(int virtualKey) => PhysicalDown(virtualKey);
    }

    private static (ViewportInputRouter Router, RecordingHost Host) New(ViewType view = ViewType.Perspective)
    {
        var host = new RecordingHost { ViewType = view };
        return (new ViewportInputRouter(host), host);
    }

    [Fact]
    public void PlainClick_Requests_A_Pick_At_The_Release_Pixel()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        r.OnButton(ViewportButton.Left, down: true, 64, 48);
        r.OnButton(ViewportButton.Left, down: false, 64, 48);

        Assert.Equal((64, 48, false), h.Pick);
    }

    [Fact]
    public void CtrlClick_Requests_An_Additive_Pick()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        r.OnKey(0x11, down: true, extended: false); // Ctrl held
        r.OnButton(ViewportButton.Left, down: true, 10, 10);
        r.OnButton(ViewportButton.Left, down: false, 10, 10);

        Assert.Equal((10, 10, true), h.Pick);
    }

    [Fact]
    public void A_Dragged_Press_Does_Not_Request_A_Pick()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        r.MarqueeEnabled = true;
        r.OnButton(ViewportButton.Left, down: true, 10, 10);
        r.OnMouseMove(60, 60);
        r.OnButton(ViewportButton.Left, down: false, 60, 60);

        Assert.Null(h.Pick);
    }

    [Fact]
    public void OrthoPointPick_Reports_The_World_Point_When_Armed()
    {
        (ViewportInputRouter r, RecordingHost h) = New(ViewType.Top);
        h.WorldPointResult = new Vector3(7f, 8f, 9f);
        r.PointPickArmed = true;
        Vector3? got = null;
        r.WorldPointPicked += p => got = p;

        r.OnButton(ViewportButton.Left, down: true, 30, 30);

        Assert.Equal(new Vector3(7f, 8f, 9f), got);
    }

    [Fact]
    public void PointPick_Is_Ignored_In_A_Perspective_View()
    {
        (ViewportInputRouter r, RecordingHost h) = New(ViewType.Perspective);
        r.PointPickArmed = true;
        bool fired = false;
        r.WorldPointPicked += _ => fired = true;

        r.OnButton(ViewportButton.Left, down: true, 30, 30);

        Assert.False(fired);
        Assert.Null(h.Pick); // perspective point-pick press is consumed by the select-maybe path instead
    }

    [Fact]
    public void CtrlRmb_In_An_Ortho_View_Teleports_The_Pan_Centre()
    {
        (ViewportInputRouter r, RecordingHost h) = New(ViewType.Front);
        r.OnKey(0x11, down: true, extended: false); // Ctrl
        r.OnButton(ViewportButton.Right, down: true, 22, 33);

        Assert.Contains("OrthoTeleport 22,33", h.Log);
    }

    [Fact]
    public void DragWithAButtonHeld_Navigates_The_Camera()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        r.OnButton(ViewportButton.Right, down: true, 0, 0); // right-drag fly
        r.OnMouseMove(12, -5);

        Assert.Contains("CameraDrag 12,-5,False", h.Log);
    }

    [Fact]
    public void Wheel_Is_Forwarded_To_Camera_Navigation()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        r.OnWheel(120);

        Assert.Contains("CameraWheel 120", h.Log);
    }

    [Fact]
    public void A_Bare_Command_Key_Dispatches_When_The_Scheme_Does_Not_Own_It()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        r.OnKey(0x20, down: true, extended: false); // Space

        Assert.Contains("Dispatch Space", h.Log);
    }

    [Fact]
    public void A_Bare_Command_Key_Is_Suppressed_While_The_Scheme_Is_Navigating()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        h.Navigating = true;
        r.OnKey(0x20, down: true, extended: false);

        Assert.DoesNotContain("Dispatch Space", h.Log);
    }

    [Fact]
    public void A_KeyThe_Scheme_Consumes_Is_Not_Dispatched()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        h.Consumes = t => t == "W";
        r.OnKey(0x57, down: true, extended: false); // W

        Assert.DoesNotContain("Dispatch W", h.Log);
        Assert.Contains("W", r.Input.HeldKeys); // still tracked for camera fly
    }

    [Fact]
    public void KeyPreDispatch_Can_Consume_A_Gesture_Before_It_Dispatches()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        r.KeyPreDispatch = _ => true; // e.g. the texture-mode-key hook
        r.OnKey(0x20, down: true, extended: false);

        Assert.DoesNotContain("Dispatch Space", h.Log);
    }

    [Fact]
    public void Transform_Modifier_Keys_Are_Held_Not_Dispatched()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        r.OnKey(0x4D, down: true, extended: false); // M

        Assert.DoesNotContain("Dispatch M", h.Log); // M arms the move transform, never fires a command
    }

    // ---- Phantom-modifier reconciliation: the dead Ctrl+Z / Ctrl+Y fix ----

    [Fact]
    public void Modifiers_Clear_On_Focus_Loss()
    {
        // The core defect: a held modifier whose KeyUp is swallowed by a focus change (alt-tab,
        // a modal Save-As / progress dialog) must not stay latched. OnFocusLost now clears the
        // modifier bitfield alongside the held-key set (was HeldKeys-only, leaving Modifiers stale).
        (ViewportInputRouter r, _) = New();
        r.OnKey(0x12, down: true, extended: false); // Alt down
        Assert.Equal(GestureModifiers.Alt, r.Input.Modifiers);

        r.OnFocusLost();

        Assert.Equal(GestureModifiers.None, r.Input.Modifiers);
        Assert.Empty(r.Input.HeldKeys);
    }

    [Fact]
    public void A_Phantom_Modifier_Cleared_By_A_Focus_Cycle_Does_Not_Corrupt_A_Later_Key()
    {
        // Ctrl latched, then a focus cycle (which now clears it), then a plain Z → dispatches Z,
        // NOT Ctrl+Z. This is exactly the reported failure path (alt-tab / Save-As dialog).
        (ViewportInputRouter r, RecordingHost h) = New();
        r.OnKey(0x11, down: true, extended: false); // phantom-prone Ctrl down
        r.OnFocusLost();                            // focus change clears the latched Ctrl
        r.OnKey(0x5A, down: true, extended: false); // plain Z

        Assert.NotNull(h.LastGesture);
        Assert.Equal("Z", h.LastGesture!.Value.Key);
        Assert.Equal(GestureModifiers.None, h.LastGesture!.Value.Modifiers);
    }

    [Fact]
    public void A_Real_Ctrl_Z_Still_Dispatches_The_Ctrl_Chord()
    {
        // Conversely, a genuine Ctrl+Z (Ctrl held while Z is pressed) still resolves to the Ctrl
        // chord — the fix must not break real undo/redo.
        (ViewportInputRouter r, RecordingHost h) = New();
        r.OnKey(0x11, down: true, extended: false); // Ctrl down
        r.OnKey(0x5A, down: true, extended: false); // Z

        Assert.Equal("Z", h.LastGesture!.Value.Key);
        Assert.Equal(GestureModifiers.Ctrl, h.LastGesture!.Value.Modifiers);
    }

    [Fact]
    public void Physical_Reconcile_Drops_A_Stale_Modifier_And_Picks_Up_A_Real_One()
    {
        // The D3D dispatch-time reconcile: with real physical state (only Ctrl down), a stale
        // phantom Alt is dropped and the real Ctrl is picked up on the next key — fixing BOTH a
        // stuck-on modifier (lost KeyUp) and a stuck-off one (a KeyDown seen while another pane
        // had focus).
        (ViewportInputRouter r, RecordingHost h) = New();
        h.UsesPhysicalKeyState = true;
        h.PhysicalDown = vk => vk == 0x11;        // only Ctrl is really down
        r.Input.Modifiers = GestureModifiers.Alt; // stale phantom Alt (its KeyUp was swallowed)

        r.OnKey(0x5A, down: true, extended: false); // press Z

        Assert.Equal("Z", h.LastGesture!.Value.Key);
        Assert.Equal(GestureModifiers.Ctrl, h.LastGesture!.Value.Modifiers); // Alt dropped, Ctrl added
    }

    [Fact]
    public void Focus_Gain_Resyncs_Modifiers_From_Physical_State()
    {
        // WM_SETFOCUS path: a modifier physically held across the focus change (whose KeyDown the
        // pane never saw) is picked up on focus gain, before any key/mouse gesture.
        (ViewportInputRouter r, RecordingHost h) = New();
        h.UsesPhysicalKeyState = true;
        h.PhysicalDown = vk => vk == 0x10; // Shift physically down
        r.Input.Modifiers = GestureModifiers.None;

        r.OnFocusGained();

        Assert.Equal(GestureModifiers.Shift, r.Input.Modifiers);
    }

    [Fact]
    public void A_Host_Without_Physical_State_Keeps_The_Incremental_Modifier_Chord()
    {
        // Regression pin (Shift-slide etc.): a host without real physical key state (GL) must
        // NOT reconcile — the incremental modifier bitfield drives the chord, so a Shift+S
        // survives even though the (unused) physical predicate would clear everything.
        (ViewportInputRouter r, RecordingHost h) = New();
        h.UsesPhysicalKeyState = false;
        h.PhysicalDown = _ => false; // would wipe modifiers IF it reconciled — it must not
        r.OnKey(0x10, down: true, extended: false); // Shift down (speed-boost / slide state)
        r.OnKey(0x53, down: true, extended: false); // S

        Assert.Equal("S", h.LastGesture!.Value.Key);
        Assert.Equal(GestureModifiers.Shift, h.LastGesture!.Value.Modifiers);
    }

    // ---- Marquee vs gizmo-handle vs click priority (item 1) ----

    [Fact]
    public void A_NonHandle_Drag_Near_Selected_Geometry_Starts_A_Marquee_Not_A_Gizmo_Drag()
    {
        // GizmoActive is true whenever something is selected; the press is NOT on a lit handle
        // (MainWindow.GizmoHitTest returns false unless a handle is hover-highlighted), so the drag
        // must box-select rather than be swallowed by the manipulator.
        (ViewportInputRouter r, _) = New();
        r.MarqueeEnabled = true;
        r.GizmoActive = true;
        r.GizmoHitTestAt = (_, _) => false;
        bool marquee = false, gizmo = false;
        r.MarqueeStarted += (_, _) => marquee = true;
        r.GizmoDragStarted += (_, _) => gizmo = true;

        r.OnButton(ViewportButton.Left, down: true, 10, 10);
        r.OnMouseMove(60, 60);

        Assert.True(marquee);
        Assert.False(gizmo);
    }

    [Fact]
    public void A_Press_On_A_Lit_Gizmo_Handle_Claims_The_Drag_And_Suppresses_The_Marquee()
    {
        // MainWindow.GizmoHitTest only returns true when a handle is hover-highlighted at the press.
        (ViewportInputRouter r, _) = New();
        r.MarqueeEnabled = true;
        r.GizmoActive = true;
        r.GizmoHitTestAt = (_, _) => true;
        bool marquee = false, gizmo = false;
        r.MarqueeStarted += (_, _) => marquee = true;
        r.GizmoDragStarted += (_, _) => gizmo = true;

        r.OnButton(ViewportButton.Left, down: true, 20, 20);
        r.OnMouseMove(70, 70);

        Assert.True(gizmo);
        Assert.False(marquee);
    }

    // ---- UnrealEd click-selects / drag-navigates (item 1, owner rule) ----

    [Fact]
    public void UnrealEd_Left_Click_Without_Drag_Selects()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        h.LeftClickSelectsDragNavigates = true;
        h.Navigating = false; // UnrealEd IsNavigating is RightDown-only, so a bare LMB is not navigating
        r.MarqueeEnabled = true;

        r.OnButton(ViewportButton.Left, down: true, 40, 40);
        r.OnButton(ViewportButton.Left, down: false, 40, 40);

        Assert.Equal((40, 40, false), h.Pick); // a release without a drag click-selects
    }

    [Fact]
    public void UnrealEd_Left_Drag_Navigates_And_Never_Marquees_Or_Picks()
    {
        (ViewportInputRouter r, RecordingHost h) = New();
        h.LeftClickSelectsDragNavigates = true;
        h.Navigating = false;
        r.MarqueeEnabled = true;
        bool marquee = false;
        r.MarqueeStarted += (_, _) => marquee = true;

        r.OnButton(ViewportButton.Left, down: true, 40, 40);
        r.OnMouseMove(80, 80);   // past the click/drag threshold → promotes to camera navigation
        r.OnMouseMove(90, 92);
        r.OnButton(ViewportButton.Left, down: false, 90, 92);

        Assert.False(marquee);                                    // no plain-LMB marquee in this scheme
        Assert.Null(h.Pick);                                      // a drag is navigation, not a click-select
        Assert.Contains(h.Log, s => s.StartsWith("CameraDrag"));  // the drag drove the camera
    }

    [Fact]
    public void A_Marquee_Scheme_Left_Drag_Still_Box_Selects_Not_Navigates()
    {
        // Regression pin for the non-UnrealEd schemes: with the click-select flag off, a bare left
        // drag box-selects (marquee) and never hands the drag to the camera.
        (ViewportInputRouter r, RecordingHost h) = New();
        h.LeftClickSelectsDragNavigates = false;
        h.Navigating = false;
        r.MarqueeEnabled = true;
        bool marquee = false;
        r.MarqueeStarted += (_, _) => marquee = true;

        r.OnButton(ViewportButton.Left, down: true, 10, 10);
        r.OnMouseMove(60, 60);

        Assert.True(marquee);
        Assert.DoesNotContain(h.Log, s => s.StartsWith("CameraDrag"));
    }
}
