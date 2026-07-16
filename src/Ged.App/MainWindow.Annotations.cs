using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Editing;
using Ged.Core.Input;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Model;
using CoreVec3 = Ged.Core.Model.Vec3;
using Geometry = Ged.Core.Model.Geometry;
using LineSegment = Ged.Rendering.Scene.LineSegment;

namespace Ged.App;

/// <summary>
/// Measure / annotate (B7): the ruler tool (click two snap-aware points → live distance
/// readout → a persistent dimension annotation), the Annotations View toggle, the
/// annotation highlight overlay, and the editor-only sidecar load/save. Annotations live
/// on the document (undoable) and in the .gedlayout.json sidecar (never the RFL).
/// </summary>
public sealed partial class MainWindow
{
    private int? _selectedAnnotationId;
    private bool _rulerActive;
    private CoreVec3? _rulerFirstPoint;
    private CoreVec3? _rulerHoverPoint;

    /// <summary>The per-level lightmap bake method (feature 1), or null → the global default.</summary>
    private LightingMethod? _levelLightingMethod;

    private LightingMethod? LevelLightingMethodOrNull() => _levelLightingMethod;

    /// <summary>Applies a lighting method loaded from the sidecar (feature 1); menu sync is a partial hook.</summary>
    private void ApplyLoadedLightingMethod(LightingMethod? method)
    {
        _levelLightingMethod = method;
        OnLightingMethodLoaded();
    }

    partial void OnLightingMethodLoaded();

    // ---- IEditorHost annotation members --------------------------------------

    public int? SelectedAnnotationId => _selectedAnnotationId;

    public void SelectAnnotation(int? id)
    {
        _selectedAnnotationId = id;
        if (id is int aid && Document?.FindAnnotation(aid) is { } a)
        {
            CoreVec3 mid = a.A.Add(a.B).Scale(0.5f);
            _viewportGrid.ActiveSurface.FramePoint(new Vector3(mid.X, mid.Y, mid.Z));
        }

        _outliner.Refresh();
        RefreshSelectionOverlay();
    }

    public void DeleteAnnotation(int id)
    {
        if (_selectedAnnotationId == id)
        {
            _selectedAnnotationId = null;
        }

        Document?.RemoveAnnotation(id); // undoable → AnnotationsChanged rebuilds + saves the sidecar
    }

    // ---- Wiring ---------------------------------------------------------------

    private void InitAnnotations()
    {
        _session.ShowAnnotations = _settings.ShowAnnotations;
        _dispatcher.Bind(CommandIds.ToolRuler, () => _toolState.Request(ViewportTool.Ruler), () => Document is not null);
        _dispatcher.Bind(CommandIds.ViewToggleAnnotations, ToggleShowAnnotations);
        _dispatcher.Bind(CommandIds.AnnotationsClear, ClearAnnotations, () => Document is { } d && d.Annotations.Count > 0);

        _viewportGrid.ForEachSurface(s =>
        {
            s.RulerClick += (x, y) => OnRulerClick(s, x, y);
            s.RulerHover += (x, y) => OnRulerHover(s, x, y);
            s.RulerCancelRequested += OnRulerEsc;
        });
    }

    private void ToggleShowAnnotations()
    {
        _settings.ShowAnnotations = !_settings.ShowAnnotations;
        _session.ShowAnnotations = _settings.ShowAnnotations;
        RebuildScene();
        Persist();
        _dispatcher.ShowMessage(_settings.ShowAnnotations ? "Annotations shown." : "Annotations hidden.");
    }

    private void ClearAnnotations()
    {
        if (Document is not { } doc || doc.Annotations.Count == 0)
        {
            return;
        }

        foreach (int id in doc.Annotations.Select(a => a.Id).ToList())
        {
            doc.RemoveAnnotation(id);
        }

        _dispatcher.ShowMessage("Cleared all annotations.");
    }

    // ---- Ruler tool -----------------------------------------------------------

    /// <summary>Arms the ruler tool (called by the exclusive tool coordinator).</summary>
    private void BeginRulerArming()
    {
        _rulerActive = true;
        _rulerFirstPoint = null;
        _rulerHoverPoint = null;
        _snap.ClearGeometrySnap();
        _viewportGrid.ForEachSurface(s => s.RulerArmed = true);
        _dispatcher.ShowMessage("Ruler: click the first point (snaps to geometry), then the second. ESC exits.");
        RefreshSelectionOverlay();
    }

    /// <summary>Disarms the ruler tool (called by the exclusive tool coordinator). Idempotent, quiet.</summary>
    private void DisarmRuler()
    {
        if (!_rulerActive)
        {
            return;
        }

        _rulerActive = false;
        _rulerFirstPoint = null;
        _rulerHoverPoint = null;
        _snap.ClearGeometrySnap();
        _viewportGrid.ForEachSurface(s => s.RulerArmed = false);
        RefreshSelectionOverlay();
    }

    /// <summary>ESC while the ruler is armed: cancel an in-progress measurement (stay in Ruler),
    /// otherwise exit to the Select tool.</summary>
    private void OnRulerEsc()
    {
        if (_rulerFirstPoint is not null)
        {
            _rulerFirstPoint = null; // cancel the in-progress measurement, stay armed
            _snap.ClearGeometrySnap();
            _dispatcher.ShowMessage("Ruler: measurement cancelled — click a new first point.");
            RefreshSelectionOverlay();
        }
        else
        {
            _toolState.Request(ViewportTool.Select);
        }
    }

    private void OnRulerClick(IViewportSurface s, int x, int y)
    {
        if (!_rulerActive || Document is null || ResolveViewportPoint(s, x, y) is not CoreVec3 p)
        {
            return;
        }

        if (_rulerFirstPoint is not CoreVec3 first)
        {
            _rulerFirstPoint = p;
            _rulerHoverPoint = p; // seed the live line immediately (before the first mouse move)
            _dispatcher.ShowMessage("Ruler: first point set — click the second point.");
        }
        else
        {
            Annotation a = Document.AddAnnotation(first, p);
            _rulerFirstPoint = null;
            _dispatcher.ShowMessage($"Measured {a.Distance:0.###} m — annotation added.");
        }

        RefreshSelectionOverlay();
    }

    private void OnRulerHover(IViewportSurface s, int x, int y)
    {
        if (!_rulerActive || ResolveViewportPoint(s, x, y) is not CoreVec3 p)
        {
            return;
        }

        _rulerHoverPoint = p;
        if (_rulerFirstPoint is CoreVec3 first)
        {
            float d = p.Sub(first).Length();
            _dispatcher.ShowMessage($"Ruler: {d:0.###} m — click to place the annotation.");
        }

        RefreshSelectionOverlay();
    }

    /// <summary>Resolves a viewport click to a world point: compiled-geometry raycast, else the
    /// world grid plane, then snapped to nearby geometry (feature 2).</summary>
    private CoreVec3? ResolveViewportPoint(IViewportSurface s, int x, int y)
    {
        if (Document is null || s.PixelRay(x, y) is not (Vector3 ro, Vector3 rd))
        {
            return null;
        }

        var origin = new CoreVec3(ro.X, ro.Y, ro.Z);
        var dir = new CoreVec3(rd.X, rd.Y, rd.Z);

        if (FindCompiledGeometry(Document.Rfl) is Geometry g && GeometryRaycast.Raycast(g, origin, dir) is (CoreVec3 hit, _))
        {
            return SnapFreePoint(hit, s);
        }

        return GizmoMath.RayPlane(CoreVec3.Zero, new CoreVec3(0, 1, 0), origin, dir, out CoreVec3 gp)
            ? SnapFreePoint(gp, s)
            : null;
    }

    // ---- Overlay --------------------------------------------------------------

    /// <summary>Highlight lines for the selected annotation and the live ruler preview.</summary>
    private IEnumerable<LineSegment> BuildAnnotationOverlay()
    {
        if (_selectedAnnotationId is int id && Document?.FindAnnotation(id) is { } a)
        {
            uint hi = Ged.Rendering.Scene.Palette.Rgba(255, 240, 60);
            var pa = new Vector3(a.A.X, a.A.Y, a.A.Z);
            var pb = new Vector3(a.B.X, a.B.Y, a.B.Z);
            yield return new LineSegment(pa, pb, hi);
        }

        if (_rulerActive && _rulerFirstPoint is CoreVec3 f && _rulerHoverPoint is CoreVec3 h)
        {
            uint col = Ged.Rendering.Scene.Palette.Rgba(255, 180, 60);
            yield return new LineSegment(new Vector3(f.X, f.Y, f.Z), new Vector3(h.X, h.Y, h.Z), col);
        }
    }

    partial void UpdateToolButtons();

    // ---- Sidecar (editor-only) ------------------------------------------------

    private string? SidecarPath() =>
        Document?.Path is string p ? LevelSidecarStore.SidecarPathFor(p) : null;

    private void LoadSidecarInto(string rflPath)
    {
        try
        {
            LevelSidecar sc = LevelSidecarStore.Load(LevelSidecarStore.SidecarPathFor(rflPath));
            Document?.SetAnnotations(sc.Annotations);
            ApplyLoadedLightingMethod(sc.Lighting); // feature 1
            _selectedAnnotationId = null;
        }
        catch (Exception)
        {
            // A missing/corrupt sidecar is non-fatal.
        }
    }

    private void SaveSidecarFor(string rflPath)
    {
        try
        {
            string sp = LevelSidecarStore.SidecarPathFor(rflPath);
            LevelSidecarStore.SaveAnnotations(sp, Document?.Annotations ?? (IReadOnlyList<Annotation>)Array.Empty<Annotation>());
            LevelSidecarStore.SaveLighting(sp, LevelLightingMethodOrNull());
        }
        catch (Exception)
        {
            // Non-fatal.
        }
    }

    private void SaveAnnotationSidecar()
    {
        if (SidecarPath() is string sp && Document is { } doc)
        {
            try
            {
                LevelSidecarStore.SaveAnnotations(sp, doc.Annotations);
            }
            catch (Exception)
            {
                // Non-fatal.
            }
        }
    }
}
