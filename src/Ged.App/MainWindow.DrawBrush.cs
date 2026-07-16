using System;
using System.Collections.Generic;
using System.Numerics;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Editing;
using Ged.Core.Input;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Brush = Ged.Core.Model.Brush;
using CoreVec3 = Ged.Core.Model.Vec3;
using Geometry = Ged.Core.Model.Geometry;
using LineSegment = Ged.Rendering.Scene.LineSegment;

namespace Ged.App;

/// <summary>
/// Item 8: the interactive Draw Brush tool — a SketchUp/Blender-style three-stage
/// box creation in the viewport. The Draw Brush command arms the tool (switching to
/// Brush mode): stage 1 clicks the base plane (the face under the cursor when the
/// ray hits compiled geometry, else the world grid plane), stage 2 rubber-bands the
/// base rectangle with a live W×D readout, stage 3 extrudes the height and the third
/// click commits ONE undo-able box brush built from the current Brush-panel settings
/// (air/solid, flags, default textures). The tool then stays armed at stage 1 for
/// repeat draws; ESC cancels the in-progress draw and disarms fully. All math lives
/// in the unit-tested <see cref="DrawBrushTool"/>; this partial only converts pixels
/// to rays, renders the ghost through the shared cutter-ghost path and commits.
/// </summary>
public sealed partial class MainWindow
{
    private DrawBrushTool? _drawTool;

    /// <summary>The exclusive Select | Draw | Ruler viewport-tool selector (item 11).</summary>
    private readonly ViewportToolState _toolState = new();

    /// <summary>True while the draw tool is armed (any stage past Idle).</summary>
    private bool DrawToolActive => _drawTool is not null && _drawTool.Stage != DrawBrushStage.Idle;

    private void InitDrawBrush()
    {
        // Route the tool commands through the exclusive state machine so activating one
        // deactivates the others and deactivating Draw/Ruler returns to Select (item 11).
        _dispatcher.Bind(CommandIds.BrushDraw, () =>
        {
            if (BrushEd is null)
            {
                _dispatcher.ShowMessage("Open or create a level first.");
                return;
            }

            _toolState.Request(ViewportTool.Draw);
        });
        _dispatcher.Bind(CommandIds.ToolSelect, () => _toolState.Request(ViewportTool.Select));
        _toolState.Changed += OnViewportToolChanged;

        _viewportGrid.ForEachSurface(s =>
        {
            s.DrawHover += (x, y) => OnDrawHover(s, x, y);
            s.DrawClick += (x, y) => OnDrawClick(s, x, y);
            s.DrawCancelRequested += () => _toolState.Request(ViewportTool.Select); // ESC exits to Select
        });
    }

    /// <summary>
    /// Reacts to an exclusive tool change: disarms both interactive tools (idempotent) and
    /// arms the requested one, then syncs the toolbar highlights. This is the single place
    /// that arms/disarms Draw and Ruler, so they can never both be active.
    /// </summary>
    private void OnViewportToolChanged(ViewportTool tool)
    {
        CancelDrawBrushTool();
        DisarmRuler();
        switch (tool)
        {
            case ViewportTool.Draw:
                BeginDrawBrushTool();
                break;
            case ViewportTool.Ruler:
                BeginRulerArming();
                break;
        }

        UpdateToolButtons();
    }

    /// <summary>Arms the draw tool (entering Brush mode). Precondition: a document is open.</summary>
    private void BeginDrawBrushTool()
    {
        if (BrushEd is null)
        {
            return;
        }

        if (BrushEd.Mode != EditMode.Brush)
        {
            ApplyMode(EditMode.Brush, announce: false); // the draw tool is a Brush-mode tool
        }

        _drawTool ??= new DrawBrushTool { PlaneProvider = DrawPlaneProvider };
        SyncDrawToolSettings();
        _drawTool.Begin();
        _viewportGrid.ForEachSurface(s => s.DrawToolArmed = true);
        _dispatcher.ShowMessage("Draw Brush: click to set the base plane (face under cursor, else grid; ESC cancels).");
        RefreshSelectionOverlay();
    }

    /// <summary>
    /// ESC (or toggling the command off): cancels any in-progress draw and disarms
    /// every pane. With the tool already idle this touches neither the undo stack
    /// nor the document — it only clears viewport arming state.
    /// </summary>
    private void CancelDrawBrushTool()
    {
        bool wasActive = DrawToolActive;
        _drawTool?.Cancel();
        _viewportGrid.ForEachSurface(s => s.DrawToolArmed = false);
        if (wasActive)
        {
            _dispatcher.ShowMessage("Draw Brush cancelled.");
            RefreshSelectionOverlay();
        }
    }

    /// <summary>Grid size and the live magnet toggle flow into the tool before every ray.</summary>
    private void SyncDrawToolSettings()
    {
        if (_drawTool is null)
        {
            return;
        }

        _drawTool.GridSize = _settings.GridSize;
        _drawTool.SnapEnabled = _settings.SnapEnabled;
        // B1: snap draw-brush stage points to nearby geometry (vertices/midpoints/faces).
        _drawTool.PointSnap = p => SnapFreePoint(p);
        // Grid plane at Y=0: the world grid level. (The drawn grid recenters on the
        // scene's min Y for display, but the editing grid origin is world zero.)
        _drawTool.GridLevel = 0f;
    }

    /// <summary>Stage-1 face pick: raycast the compiled static geometry, or null → grid-plane fallback.</summary>
    private (CoreVec3 Point, CoreVec3 Normal)? DrawPlaneProvider((CoreVec3 Origin, CoreVec3 Dir) ray)
    {
        if (Document is null)
        {
            return null;
        }

        Geometry? g = FindCompiledGeometry(Document.Rfl);
        return g is null ? null : GeometryRaycast.Raycast(g, ray.Origin, ray.Dir);
    }

    private static Geometry? FindCompiledGeometry(RflFile file)
    {
        file.ParseAllKnownSections();
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                return g.Geometry;
            }
        }

        return null;
    }

    private void OnDrawHover(IViewportSurface s, int x, int y)
    {
        if (!DrawToolActive || _drawTool is null || s.PixelRay(x, y) is not (Vector3 ro, Vector3 rd))
        {
            return;
        }

        SyncDrawToolSettings();
        bool reachable = _drawTool.Hover(V(ro), V(rd));
        UpdateDrawStatus(reachable);
        RefreshSelectionOverlay();
    }

    private void OnDrawClick(IViewportSurface s, int x, int y)
    {
        if (!DrawToolActive || _drawTool is null || s.PixelRay(x, y) is not (Vector3 ro, Vector3 rd))
        {
            return;
        }

        SyncDrawToolSettings();
        DrawBrushResult? result = _drawTool.Click(V(ro), V(rd));
        switch (_drawTool.LastClick)
        {
            case DrawBrushClickOutcome.Committed when result is DrawBrushResult r:
                CommitDrawBrush(r); // the tool reset itself to BasePoint: armed for the next box
                break;
            case DrawBrushClickOutcome.PlaneUnreachable:
                UpdateDrawStatus(planeReachable: false);
                break;
            case DrawBrushClickOutcome.DegenerateRectangle:
                _dispatcher.ShowMessage("Draw Brush: the rectangle needs a non-zero width and depth - move the cursor and click again.");
                break;
            case DrawBrushClickOutcome.ZeroHeight:
                _dispatcher.ShowMessage("Draw Brush: move the mouse to set a non-zero height, then click to create.");
                break;
            default:
                UpdateDrawStatus(planeReachable: true);
                break;
        }

        RefreshSelectionOverlay();
    }

    /// <summary>Per-stage status text, including the live W×D / H readouts.</summary>
    private void UpdateDrawStatus(bool planeReachable)
    {
        if (_drawTool is null)
        {
            return;
        }

        if (!planeReachable)
        {
            // Ortho panes work whenever their ray can reach the work plane (e.g. a
            // Front view onto the Y-up grid plane). Where the ray is parallel to it
            // (e.g. the Top view vs that same plane) there is no intersection, so
            // the tool is perspective-only for that plane.
            _dispatcher.ShowMessage("Draw Brush: this view is parallel to the work plane - use the perspective view.");
            return;
        }

        switch (_drawTool.Stage)
        {
            case DrawBrushStage.BasePoint:
                _dispatcher.ShowMessage("Draw Brush: click to set the base plane…");
                break;
            case DrawBrushStage.Rectangle:
                _dispatcher.ShowMessage($"W×D: {_drawTool.WidthReadout:0.##} × {_drawTool.DepthReadout:0.##} — click to fix the base");
                break;
            case DrawBrushStage.Height:
                _dispatcher.ShowMessage($"H: {_drawTool.HeightReadout:0.##} — click to create");
                break;
        }
    }

    /// <summary>
    /// Commits the drawn box: clones the Brush-panel template (flags, textures, life,
    /// splits preserved) with the drawn dimensions, creates it through
    /// <see cref="BrushEditor.CreateBrush"/> (ONE undo entry) and selects it.
    /// </summary>
    private void CommitDrawBrush(DrawBrushResult r)
    {
        if (BrushEd is null)
        {
            return;
        }

        var p = new BrushCreateParams
        {
            Shape = BrushShape.Box,
            Width = r.Width,
            Height = r.Height,
            Depth = r.Depth,
            WidthSplits = _brushParams.WidthSplits,
            HeightSplits = _brushParams.HeightSplits,
            DepthSplits = _brushParams.DepthSplits,
            Texture = _brushParams.Texture,
            // Same unresolvable-name guard as CreateBrushFromPanel (item: white-brush fix).
            FloorTexture = _session.ResolveDefaultBrushTexture(_settings.DefaultFloorTexture),
            WallTexture = _session.ResolveDefaultBrushTexture(_settings.DefaultWallTexture),
            CeilingTexture = _session.ResolveDefaultBrushTexture(_settings.DefaultCeilingTexture),
            Air = _brushParams.Air,
            Portal = _brushParams.Portal,
            Detail = _brushParams.Detail,
            EmitsSteam = _brushParams.EmitsSteam,
            Geoable = _brushParams.Geoable,
            Life = _brushParams.Life,
        };

        int uid = BrushEd.CreateBrush(p, r.Center, Mat3.Identity);
        _session.Selection.SelectBrush(uid);
        _dispatcher.ShowMessage(
            $"Created box brush {r.Width:0.##}×{r.Height:0.##}×{r.Depth:0.##} (uid {uid}) — Draw Brush ready for the next box.");
        AfterMutation(); // same post-create refresh as CreateBrushFromPanel
    }

    /// <summary>
    /// The draw tool's ghost, contributed through the same overlay path as the
    /// cookie-cutter ghost: stage 1 shows the snapped preview point as a small axis
    /// cross, stages 2–3 show the ghost box's deduped edge lines (a ghost Brush built
    /// by <see cref="BrushFactory"/> with the tool's dims/center, same color).
    /// </summary>
    private IEnumerable<LineSegment> BuildDrawToolGhost()
    {
        if (!DrawToolActive || _drawTool is null)
        {
            yield break;
        }

        uint color = Palette.Rgba(200, 200, 90, 200);
        if (_drawTool.Stage == DrawBrushStage.BasePoint)
        {
            if (_drawTool.PreviewPoint is CoreVec3 p)
            {
                float r = MathF.Max(0.15f, _settings.GridSize * 0.25f);
                var c = new Vector3(p.X, p.Y, p.Z);
                yield return new LineSegment(c - new Vector3(r, 0, 0), c + new Vector3(r, 0, 0), color);
                yield return new LineSegment(c - new Vector3(0, r, 0), c + new Vector3(0, r, 0), color);
                yield return new LineSegment(c - new Vector3(0, 0, r), c + new Vector3(0, 0, r), color);
            }

            yield break;
        }

        if (_drawTool.GhostBox is not (CoreVec3 center, float w, float h, float d))
        {
            yield break;
        }

        Brush ghost = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Box, Width = w, Height = h, Depth = d }, 0);
        ghost.Position = center;
        foreach (LineSegment seg in GhostEdgeLines(ghost, color))
        {
            yield return seg;
        }
    }
}
