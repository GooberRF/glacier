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

        public Func<string, bool> Consumes { get; set; } = _ => false;

        public readonly List<string> Log = new();

        public (int X, int Y, bool Ctrl)? Pick { get; private set; }

        public Vector3 WorldPointResult { get; set; } = new(1f, 2f, 3f);

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

        public void DispatchGesture(KeyGesture gesture) => Log.Add($"Dispatch {gesture.Key}");

        public bool TryWorldPoint(int x, int y, out Vector3 world)
        {
            world = WorldPointResult;
            return true;
        }

        public void OrthoTeleport(int x, int y) => Log.Add($"OrthoTeleport {x},{y}");

        public bool IsKeyPhysicallyDown(int virtualKey) => true;
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
}
