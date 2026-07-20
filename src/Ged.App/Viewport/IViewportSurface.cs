using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Controls;
using Ged.App.Camera;
using Ged.Core.Assets;
using Ged.Core.Input;
using Ged.Rendering;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;

namespace Ged.App.Viewport;

/// <summary>
/// The editing surface both live viewport panes implement: the Windows
/// <see cref="ViewportSurface"/> (Direct3D 11, native child HWND) and the cross-platform
/// <see cref="GlViewportSurface"/> (OpenGL 3.3, composited <c>OpenGlControlBase</c>). It is
/// the full contract the rest of the app depends on — the render/scene plumbing, camera
/// queries and the complete editing-gesture event surface (transform drags, marquee, gizmo,
/// draw-brush, ruler, point-pick, pick-click). Both surfaces drive the SAME shared
/// <see cref="ViewportInputRouter"/> gesture state machine, so an identical input sequence
/// produces identical events and document outcomes regardless of backend.
/// </summary>
public interface IViewportSurface
{
    // ---- Editing-gesture events (raised by the shared router) ----

    /// <summary>Raised when the pointer enters this pane (drives active-pane focus).</summary>
    event Action<IViewportSurface>? Activated;

    /// <summary>Raised (UI thread) when a left-click pick completes (bool = Ctrl held / additive).</summary>
    event Action<PickId, bool>? Picked;

    /// <summary>Raised periodically with (fps, cameraPosition).</summary>
    event Action<double, Vector3>? StatsUpdated;

    /// <summary>Raised on M+Arrow: a unit world direction to move the selection one grid step.</summary>
    event Action<Vector3>? NudgeMove;

    /// <summary>Raised on R+Arrow: a world axis (unit, signed) to rotate the selection one step.</summary>
    event Action<Vector3>? NudgeRotate;

    /// <summary>Raised when an M/N+LMB drag begins (resets snap accumulation).</summary>
    event Action? BrushDragStarted;

    /// <summary>Raised during an M/N+LMB drag: pixel delta and whether to axis-constrain (N).</summary>
    event Action<int, int, bool>? BrushDragPixels;

    /// <summary>Raised when an M/N+LMB drag ends (breaks undo coalescing).</summary>
    event Action? BrushDragEnded;

    /// <summary>Raised when a manipulator LMB-drag begins, at the press pixel.</summary>
    event Action<int, int>? GizmoDragStarted;

    /// <summary>Raised during a manipulator drag with the absolute cursor pixel.</summary>
    event Action<int, int>? GizmoDragMovedTo;

    /// <summary>Raised when a manipulator drag ends normally (commit one undo entry).</summary>
    event Action? GizmoDragEnded;

    /// <summary>Raised when a manipulator drag is cancelled with ESC (revert).</summary>
    event Action? GizmoDragCancelled;

    /// <summary>Raised on pointer move while the gizmo is shown and idle: the hover pixel.</summary>
    event Action<int, int>? GizmoHover;

    /// <summary>Marquee box-select begins at the press pixel.</summary>
    event Action<int, int>? MarqueeStarted;

    /// <summary>Marquee box-select updates to the current pixel.</summary>
    event Action<int, int>? MarqueeMovedTo;

    /// <summary>Marquee box-select ends at the release pixel (bool = Ctrl-add).</summary>
    event Action<int, int, bool>? MarqueeEnded;

    /// <summary>Raised with the world point under an armed ortho click (two-point clip).</summary>
    event Action<Vector3>? WorldPointPicked;

    /// <summary>Draw-brush tool: pointer moved while armed and not navigating (hover preview).</summary>
    event Action<int, int>? DrawHover;

    /// <summary>Draw-brush tool: plain LMB press while armed (consumed — no pick/marquee starts).</summary>
    event Action<int, int>? DrawClick;

    /// <summary>Draw-brush tool: ESC pressed while armed (cancel the in-progress draw).</summary>
    event Action? DrawCancelRequested;

    /// <summary>Ruler tool: a plain LMB press while armed, at the click pixel (consumed).</summary>
    event Action<int, int>? RulerClick;

    /// <summary>Ruler tool: pointer moved while armed and idle (live distance preview).</summary>
    event Action<int, int>? RulerHover;

    /// <summary>Ruler tool: ESC pressed while armed (cancel the measurement).</summary>
    event Action? RulerCancelRequested;

    /// <summary>Raised when <see cref="Mode"/> changes (keeps the pane toolbar label in sync).</summary>
    event Action<RenderMode>? ModeChanged;

    // ---- Editing-gesture arming flags / hooks ----

    /// <summary>When true, a gizmo is shown for the selection: LMB over a handle drags it.</summary>
    bool GizmoActive { get; set; }

    /// <summary>Press-time gate: returns true when the pixel is over a gizmo handle.</summary>
    Func<IViewportSurface, int, int, bool>? GizmoHitTestAt { get; set; }

    /// <summary>When true, an empty-space LMB-drag runs a marquee box-select (select contexts).</summary>
    bool MarqueeEnabled { get; set; }

    /// <summary>When true, an ortho LMB click reports its world point instead of picking.</summary>
    bool PointPickArmed { get; set; }

    /// <summary>When true, the draw-brush tool owns plain pointer input.</summary>
    bool DrawToolArmed { get; set; }

    /// <summary>When true, the ruler tool owns plain clicks / idle moves.</summary>
    bool RulerArmed { get; set; }

    /// <summary>Optional hook run before a gesture is dispatched; returning true consumes it.</summary>
    Func<KeyGesture, bool>? KeyPreDispatch { get; set; }

    // ---- State / config ----

    string? InitError { get; }

    /// <summary>Current swapchain client height in pixels (for pixel→world scaling).</summary>
    int SurfaceHeight { get; }

    /// <summary>The swapchain client size in pixels.</summary>
    (int Width, int Height) SurfaceSize { get; }

    /// <summary>The world-space ray through the most recent pick pixel.</summary>
    (Vector3 Origin, Vector3 Direction)? LastPickRay { get; }

    bool ScrollMode { get; }

    ViewType ViewType { get; }

    CameraSchemeKind SchemeKind { get; }

    double Fps { get; }

    /// <summary>The live camera used for pixel/world queries (never null once realized).</summary>
    Rendering.Camera? Camera { get; }

    /// <summary>True while the pointer is inside this pane's render surface.</summary>
    bool IsPointerInside { get; }

    Vector3 CameraPosition { get; }

    Vector3 CameraForward { get; }

    RenderMode Mode { get; set; }

    float CameraSpeed { get; set; }

    FogSettings Fog { get; set; }

    bool DisableBackfaceCulling { get; set; }

    /// <summary>Whether Alt is currently held (temporary snap-invert during a drag).</summary>
    bool AltHeld { get; }

    /// <summary>Whether the temporary snap-invert modifier is held during a drag.</summary>
    bool SnapInvertHeld { get; }

    float AnimationTime { get; set; }

    /// <summary>True while a pointer button is held over this pane (drives autosave deferral).</summary>
    bool IsInteracting { get; }

    // ---- Queries / commands ----

    /// <summary>A world-space ray through a viewport pixel, or null when the surface is absent.</summary>
    (Vector3 Origin, Vector3 Direction)? PixelRay(int x, int y);

    /// <summary>Projects a world point to this pane's pixel space.</summary>
    bool WorldToScreen(Vector3 world, out Vector2 screen);

    void SetScheme(CameraSchemeKind kind);

    void SetViewType(ViewType type, bool invalidate = true);

    /// <summary>Uploads a scene and frames the camera between the given endpoints.</summary>
    void LoadScene(RenderScene scene, AssetVfs? vfs, Vector3 cameraPosition, Vector3 cameraTarget);

    /// <summary>Re-uploads the current scene (used after a grid/brightness rebuild).</summary>
    void RefreshScene(RenderScene scene, AssetVfs? vfs);

    void SetSelection(IReadOnlyList<LineSegment> lines);

    /// <summary>Sets the manipulator/gizmo overlay lines, drawn on top of the scene.</summary>
    void SetGizmoOverlay(IReadOnlyList<LineSegment> lines);

    /// <summary>Sets (or clears with null) a small on-top overlay scene — the transform-drag numeric
    /// label — updated per drag frame without re-emitting the whole level scene.</summary>
    void SetOverlayScene(RenderScene? scene, AssetVfs? vfs);

    /// <summary>Frames a bounds box in this pane's camera.</summary>
    void Frame(Ged.Core.Model.Aabb bounds);

    /// <summary>Frames a small volume around a world point (Jump To / double-click).</summary>
    void FramePoint(Vector3 p);

    /// <summary>Moves the camera to a world point, keeping its orientation (View From).</summary>
    void ViewFrom(Vector3 p);

    /// <summary>Moves the camera to a world point AND aims it along <paramref name="forward"/>.</summary>
    void ViewFrom(Vector3 p, Vector3 forward);

    /// <summary>Snaps the camera to look down the nearest world axis.</summary>
    void AxisOrient();

    /// <summary>Banks (rolls) the perspective camera by degrees.</summary>
    void Bank(float degrees);

    /// <summary>Toggles scroll (pan-drag) mode.</summary>
    void ToggleScrollMode();

    /// <summary>Requests a repaint.</summary>
    void Invalidate();
}

/// <summary>
/// Convenience accessors bridging <see cref="IViewportSurface"/> to the Avalonia visual
/// tree. Every implementation is an Avalonia <see cref="Control"/>, so panes host it as a
/// child through this cast rather than knowing the concrete backend type.
/// </summary>
internal static class ViewportSurfaceExtensions
{
    /// <summary>The surface as an Avalonia control (both backends derive from <see cref="Control"/>).</summary>
    public static Control AsControl(this IViewportSurface surface) => (Control)surface;
}
