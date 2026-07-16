using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using CoreVec3 = Ged.Core.Model.Vec3;
using LineSegment = Ged.Rendering.Scene.LineSegment;

namespace Ged.App;

/// <summary>
/// In-viewport transform progress indicators, shown while a gizmo drag is active (item:
/// transform indicators): MOVE draws a dimension line from the drag-start pivot to the
/// current (snapped) position with a live distance label; ROTATE draws the swept angle arc
/// at the pivot with a degree label; SCALE shows a live percentage label plus a ghost
/// outline of the selection's original wireframe captured at drag start. Indicator LINES
/// ride the always-on-top gizmo overlay channel (never occluded, refreshed by
/// <see cref="RefreshSelectionOverlay"/>); the numeric LABEL is an on-top LabelBitmap
/// billboard (item 3: <see cref="Ged.Rendering.Scene.Billboard.OnTop"/> — drawn depth-disabled
/// like the lines so it is never hidden behind geometry) injected into the scene by
/// <see cref="RebuildScene"/>, which every applied drag step already triggers — and on the
/// frames it skips (snapped value unchanged) the label text is unchanged too, so it is never
/// stale. Everything appears on drag start, tracks the SNAPPED values per frame, and vanishes
/// on commit/cancel. The status-bar readout is untouched.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly uint IndicatorColor = Palette.Rgba(255, 220, 80, 255);
    private static readonly uint GhostColor = Palette.Rgba(160, 160, 170, 150);

    /// <summary>The rotate arc's start direction (in-plane), captured at drag start.</summary>
    private CoreVec3 _dragRingStartDir;

    /// <summary>The selection wireframe snapshot at drag start (the SCALE original-bounds ghost).</summary>
    private IReadOnlyList<LineSegment> _xformScaleGhost = Array.Empty<LineSegment>();

    private string? _xformLabelText;
    private Vector3 _xformLabelPos;
    private bool _xformLabelInScene;

    /// <summary>Captures drag-start state for the indicators (called from OnGizmoDragStarted).</summary>
    private void BeginTransformIndicator(GizmoTool tool)
    {
        _dragRingStartDir = _dragRingPrevDir;
        _xformLabelText = null;
        _xformLabelInScene = false;
        _xformScaleGhost = tool == GizmoTool.Scale
            ? TransformIndicatorBuilder.Recolor(SnapshotSelectionWireframe(), GhostColor)
            : Array.Empty<LineSegment>();
    }

    /// <summary>The current selection's overlay wireframe (object boxes + brush/face/vertex lines).</summary>
    private IEnumerable<LineSegment> SnapshotSelectionWireframe()
    {
        IEnumerable<LineSegment> lines = _session.BuildBrushSelectionLines();
        if (Document is not null)
        {
            lines = lines.Concat(_session.BuildSelectionLines(Document.Selection));
        }

        return lines;
    }

    /// <summary>Sets the live numeric label (world-anchored); the next RebuildScene bakes it.</summary>
    private void UpdateTransformIndicatorLabel(string text, Vector3 position)
    {
        _xformLabelText = text;
        _xformLabelPos = position;
    }

    /// <summary>Clears all indicator state on drag commit/cancel (indicators vanish).</summary>
    private void EndTransformIndicator(bool rebuildIfLabeled)
    {
        bool hadLabel = _xformLabelInScene;
        _xformLabelText = null;
        _xformLabelInScene = false;
        _xformScaleGhost = Array.Empty<LineSegment>();
        if (hadLabel && rebuildIfLabeled)
        {
            RebuildScene(); // drop the baked label billboard from the scene
        }
    }

    /// <summary>
    /// The indicator lines for the current drag, drawn on the on-top gizmo overlay channel.
    /// Empty when no drag is active.
    /// </summary>
    private IEnumerable<LineSegment> BuildTransformIndicatorLines()
    {
        if (!_gizmoDragging)
        {
            return Array.Empty<LineSegment>();
        }

        switch (GizmoMath.ToolOf(_dragHandle))
        {
            case GizmoTool.Move when _dragAppliedDelta.LengthSquared() > 1e-10f:
                return TransformIndicatorBuilder.MoveLine(
                    Vn(_dragPivot), Vn(_dragPivot.Add(_dragAppliedDelta)), IndicatorColor);

            case GizmoTool.Rotate when MathF.Abs(_dragAppliedAngle) > 1e-3f:
                return TransformIndicatorBuilder.RotationArc(
                    Vn(_dragPivot), Vn(_dragAxis), Vn(_dragRingStartDir),
                    _dragAppliedAngle, _dragPose.Length, IndicatorColor);

            case GizmoTool.Scale:
                return _xformScaleGhost;

            default:
                return Array.Empty<LineSegment>();
        }
    }

    /// <summary>Injects the live label into a freshly built scene (called by RebuildScene).</summary>
    private void AppendTransformIndicatorLabel(RenderScene scene)
    {
        if (_gizmoDragging && _xformLabelText is { } text)
        {
            _session.AppendOverlayLabel(scene, text, _xformLabelPos);
            _xformLabelInScene = true;
        }
    }

    // ---- Per-tool label updates (called with the SNAPPED values, before apply) ----

    private void UpdateMoveIndicator(CoreVec3 totalDelta)
    {
        float dist = MathF.Sqrt(totalDelta.LengthSquared());
        Vector3 mid = (Vn(_dragPivot) + Vn(_dragPivot.Add(totalDelta))) * 0.5f;
        UpdateTransformIndicatorLabel(
            TransformIndicatorBuilder.FormatDistance(dist),
            mid + new Vector3(0f, 0.3f, 0f));
    }

    private void UpdateRotateIndicator(float snappedDegrees)
    {
        // Anchor the label just outside the mid-arc point (above the pivot when the ring
        // basis is degenerate).
        Vector3 start = Vn(_dragRingStartDir);
        Vector3 axis = Vn(_dragAxis);
        Vector3 anchor = start.LengthSquared() > 1e-10f && axis.LengthSquared() > 1e-10f
            ? Vn(_dragPivot) + (TransformIndicatorBuilder.RotateAround(
                Vector3.Normalize(start), Vector3.Normalize(axis), snappedDegrees * 0.5f) * _dragPose.Length * 1.35f)
            : Vn(_dragPivot) + new Vector3(0f, _dragPose.Length, 0f);
        UpdateTransformIndicatorLabel(TransformIndicatorBuilder.FormatAngle(snappedDegrees), anchor);
    }

    private void UpdateScaleIndicator(float factor)
    {
        UpdateTransformIndicatorLabel(
            TransformIndicatorBuilder.FormatScale(factor),
            Vn(_dragPivot) + new Vector3(0f, _dragPose.Length * 0.7f, 0f));
    }
}
