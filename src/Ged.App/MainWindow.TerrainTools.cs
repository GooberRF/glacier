using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Avalonia.Threading;
using Ged.App.Dialogs;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Rendering.Scene;

namespace Ged.App;

/// <summary>
/// The Terrain Tools in the viewport (<see cref="TerrainToolController"/>, docked panel <see cref="TerrainToolsPanel"/>):
/// while armed on the one selected terrain, a left drag on it is a stroke (routed through the gizmo press path, as curve
/// handles are), drawn live over the level from a small overlay scene without rebuilding the level; the release is one
/// undo step and a stroke that changed heights or holes marks the geometry out of date. The brush cursor rides the
/// selection overlay.
/// </summary>
public sealed partial class MainWindow
{
    private TerrainToolController? _terrainTools;
    private DispatcherTimer? _terrainRepeat;
    private DispatcherTimer? _terrainLive;
    private Viewport.IViewportSurface? _terrainSurface;
    private bool _pendingTerrainStroke;

    /// <summary>The Terrain Tools state (shared settings, armed flag, stroke).</summary>
    internal TerrainToolController TerrainTools => _terrainTools ??= CreateTerrainTools();

    private TerrainToolController CreateTerrainTools()
    {
        var c = new TerrainToolController(() => Document, () => Document?.Selection.Count == 1 ? Document.Selection.First() : null, _properties.TerrainSettings);
        c.Committed += geometry =>
        {
            _session.LiveTerrainUid = 0;
            _viewportGrid.SetOverlayScene(null, null);
            if (geometry)
            {
                _buildController?.InvalidateGeometry();
            }

            RebuildScene();
            _history.Refresh();
            _properties.TerrainPanel?.SetNote(c.Note);
            _dispatcher.ShowMessage(geometry ? "Terrain stroke recorded: run Build Geometry before playing." : "Terrain stroke recorded.");
        };
        c.Changed += () =>
        {
            RefreshSelectionOverlay();
            if (c.Stroke is not null)
            {
                _terrainLive?.Start();
            }
        };
        c.SettingsChanged += () => _properties.TerrainPanel?.RefreshSettings();
        return c;
    }

    /// <summary>Hooks the Properties dock's Terrain Tools panel to the shell.</summary>
    private void InitTerrainTools()
    {
        _properties.TerrainArmed = () => TerrainTools.Armed;
        _properties.TerrainDecorationsShown = () => _session.ShowTerrainDecorations;
        _properties.TerrainServices = new TerrainToolServices
        {
            SetArmed = armed =>
            {
                TerrainTools.Armed = armed;
                UpdateGizmoState();
                _dispatcher.ShowMessage(armed ? "Terrain Tools on: drag on the terrain to paint." : "Terrain Tools off.");
            },
            OpenGenerator = () => _ = OpenTerrainGeneratorAsync(),
            AfterEdit = () =>
            {
                RebuildScene();
                _history.Refresh();
            },
            SettingsChanged = RefreshSelectionOverlay,
            ShowDecorationsChanged = show =>
            {
                _session.ShowTerrainDecorations = show;
                RebuildScene();
            },
        };

        // Held still, a sculpt stroke keeps dabbing every 40 ms (terrain_paint.cpp:72-74).
        _terrainRepeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _terrainRepeat.Tick += (_, _) => TerrainTools.Tick();

        // The live drawing of the stroked terrain follows at most ~30 times a second.
        _terrainLive = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _terrainLive.Tick += (_, _) =>
        {
            _terrainLive.Stop();
            if (TerrainTools.LiveScene() is { } live)
            {
                _viewportGrid.SetOverlayScene(live, _session.Vfs);
            }
        };
    }

    private async System.Threading.Tasks.Task OpenTerrainGeneratorAsync()
    {
        if (Document is not { } doc || TerrainTools.Target is not { } obj)
        {
            return;
        }

        if (await TerrainGenerateDialog.ShowAsync(this, doc, obj))
        {
            _buildController?.InvalidateGeometry();
            RebuildScene();
            _history.Refresh();
            _properties.Refresh();
        }
    }

    private bool TerrainToolsArmed() => _terrainTools is { Armed: true } t && t.Target is not null;

    /// <summary>Press hit-test: claims the press when the tools are armed and it lands on the selected terrain.</summary>
    private bool TerrainToolHitTest(Viewport.IViewportSurface s, int x, int y) =>
        TerrainToolsArmed() && s.PixelRay(x, y) is (Vector3 ro, Vector3 rd) && TerrainTools.HitTest(ro, rd);

    private bool TerrainStrokeActive => _terrainSurface is not null;

    private void BeginTerrainStroke(Viewport.IViewportSurface s, int x, int y)
    {
        if (s.PixelRay(x, y) is not (Vector3 ro, Vector3 rd))
        {
            return;
        }

        _terrainSurface = s;
        TerrainTools.Begin(ro, rd, s.CtrlHeld, s.ShiftHeld);
        if (TerrainTools.Stroke is { } stroke)
        {
            // The level scene leaves the terrain out while the overlay draws it live.
            _session.LiveTerrainUid = stroke.Terrain.Uid;
            RebuildScene();
            _terrainLive?.Start();
            _terrainRepeat?.Start();
        }
        else
        {
            _terrainSurface = null;
            RefreshSelectionOverlay();
        }
    }

    private void MoveTerrainStroke(int x, int y)
    {
        if (_terrainSurface is { } s && s.PixelRay(x, y) is (Vector3 ro, Vector3 rd))
        {
            TerrainTools.MoveTo(ro, rd);
        }
    }

    private void EndTerrainStroke()
    {
        _terrainSurface = null;
        _terrainRepeat?.Stop();
        _terrainLive?.Stop();
        if (!TerrainTools.End())
        {
            _session.LiveTerrainUid = 0;
            _viewportGrid.SetOverlayScene(null, null);
            RebuildScene();
        }
    }

    private void CancelTerrainStroke()
    {
        _terrainSurface = null;
        _terrainRepeat?.Stop();
        _terrainLive?.Stop();
        TerrainTools.Cancel();
        _session.LiveTerrainUid = 0;
        _viewportGrid.SetOverlayScene(null, null);
        RebuildScene();
        _dispatcher.ShowMessage("Terrain stroke cancelled.");
    }

    private void OnTerrainToolHover(Viewport.IViewportSurface s, int x, int y)
    {
        if (TerrainToolsArmed() && s.PixelRay(x, y) is (Vector3 ro, Vector3 rd))
        {
            TerrainTools.HoverAt(ro, rd);
        }
    }

    /// <summary>The brush cursor for the selection overlay.</summary>
    private IEnumerable<LineSegment> BuildTerrainToolOverlays() =>
        _terrainTools is { } t ? t.CursorLines() : Array.Empty<LineSegment>();

    /// <summary>Grid Size Up/Down ([ and ]) scale the brush radius by 1.15 while the tools are armed (terrain_paint.cpp:1126-1142).</summary>
    private bool TerrainRadiusStep(bool up)
    {
        if (!TerrainToolsArmed())
        {
            return false;
        }

        TerrainBrushSettings s = TerrainTools.Settings;
        s.Radius = Math.Clamp(up ? s.Radius * 1.15f : s.Radius / 1.15f, TerrainBrush.MinRadius, TerrainBrush.MaxRadius);
        _dispatcher.ShowMessage($"Brush radius {s.Radius:0.##} m");
        _properties.Refresh();
        RefreshSelectionOverlay();
        return true;
    }
}
