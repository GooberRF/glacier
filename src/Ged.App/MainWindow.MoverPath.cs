using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Ged.App.Dialogs;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;

namespace Ged.App;

/// <summary>
/// Mover editing in the viewport: the Keyframe panel's services, the mover preview ghost and timing ticks, keyframe
/// double-click (Keyframe Properties window), and dragging a selected keyframe's curve handles
/// (<see cref="MoverHandleTool"/>; Esc cancels a drag, and each drag is one undo step).
/// </summary>
public sealed partial class MainWindow
{
    private const long KeyframeDoubleClickMs = 400;

    private MoverPreviewController? _moverPreview;
    private MoverHandleTool? _handleTool;
    private MoverHandle? _hoverMoverHandle;
    private MoverHandle? _pendingMoverHandle;
    private Viewport.IViewportSurface? _handleDragSurface;
    private int _lastKeyframeClickUid = -1;
    private long _lastKeyframeClickTicks;

    /// <summary>The viewport mover preview.</summary>
    internal MoverPreviewController MoverPreview => _moverPreview ??= CreateMoverPreview();

    private MoverHandleTool? HandleTool =>
        Document is null ? null : _handleTool is { } t && ReferenceEquals(_handleToolDoc, Document) ? t : NewHandleTool();

    private EditorDocument? _handleToolDoc;

    private MoverHandleTool NewHandleTool()
    {
        _handleToolDoc = Document;
        return _handleTool = new MoverHandleTool(Document!);
    }

    private MoverPreviewController CreateMoverPreview()
    {
        var preview = new MoverPreviewController(() => Document);
        preview.Changed += RefreshSelectionOverlay;
        return preview;
    }

    /// <summary>Hooks the Properties dock's Keyframe panel to the shell.</summary>
    private void InitMoverPanel()
    {
        _properties.MoverServices = new MoverPanelServices
        {
            SelectObject = o => _session.Selection.SelectObject(o),
            PlaySound = PlaySoundPreview,
            StopSound = StopSoundPreview,
            PickSoundFile = PickSoundFileAsync,
            Preview = MoverPreview,
            AfterEdit = () =>
            {
                RebuildScene();
                _history.Refresh();
            },
            ShowMessage = m => _dispatcher.ShowMessage(m),
        };
    }

    /// <summary>A .wav picker for the mover sounds; returns the leaf file name (sounds resolve by name), or null.</summary>
    private async Task<string?> PickSoundFileAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a sound",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Sound Files (*.wav)") { Patterns = new[] { "*.wav" } },
                new FilePickerFileType("All Files (*.*)") { Patterns = new[] { "*" } },
            },
        });
        return files.Count == 0 ? null : System.IO.Path.GetFileName(files[0].Name);
    }

    /// <summary>The preview ghost, its timing ticks and the hovered / dragged handle highlight.</summary>
    private IEnumerable<LineSegment> BuildMoverOverlays()
    {
        var lines = new List<LineSegment>();
        if (_moverPreview is { Active: true } preview && _movers is not null)
        {
            lines.AddRange(preview.GhostLines(_movers));
            lines.AddRange(preview.TickLines());
        }

        MoverHandle? lit = HandleTool?.Dragging ?? _hoverMoverHandle;
        if (lit is not null && Document is not null && HandleTool?.Live(lit, Document.Selection) is { } h)
        {
            // Alpine RED draws the hovered or dragged handle white (mover_path.cpp:262-263, 370-384).
            uint white = Palette.Rgba(255, 255, 255);
            var kf = new Vector3(h.Keyframe.Position.X, h.Keyframe.Position.Y, h.Keyframe.Position.Z);
            lines.Add(new LineSegment(kf, h.Point, white));
            const float r = 0.22f;
            lines.Add(new LineSegment(h.Point - new Vector3(r, 0, 0), h.Point + new Vector3(r, 0, 0), white));
            lines.Add(new LineSegment(h.Point - new Vector3(0, r, 0), h.Point + new Vector3(0, r, 0), white));
            lines.Add(new LineSegment(h.Point - new Vector3(0, 0, r), h.Point + new Vector3(0, 0, r), white));
        }

        return lines;
    }

    // ---- keyframe double-click → Keyframe Properties window ---------------------------------------------

    /// <summary>
    /// A second click on the same keyframe within the double-click time opens Keyframe Properties (RED opens its
    /// dialog the same way). Returns true when it consumed the click.
    /// </summary>
    private bool TryOpenKeyframeDialog(PickId id)
    {
        if (Document is null || _session.ObjectForPick(id) is not { Model: Keyframe kf } o)
        {
            _lastKeyframeClickUid = -1;
            return false;
        }

        long now = Environment.TickCount64;
        bool twice = _lastKeyframeClickUid == o.Uid && now - _lastKeyframeClickTicks < KeyframeDoubleClickMs;
        _lastKeyframeClickUid = o.Uid;
        _lastKeyframeClickTicks = now;
        if (!twice)
        {
            return false;
        }

        _lastKeyframeClickUid = -1;
        OpenKeyframeDialog(kf);
        return true;
    }

    /// <summary>Opens the Keyframe Properties window on <paramref name="kf"/> (with the other selected keyframes of its mover).</summary>
    internal KeyframePropertiesDialog? OpenKeyframeDialog(Keyframe kf)
    {
        if (Document is null || _movers?.FindKeyframe(kf.Uid) is not { } found || _properties.MoverServices is not { } services)
        {
            return null;
        }

        var keys = Document.Selection.Select(o => o.Model).OfType<Keyframe>()
            .Where(k => found.Group.MovingData!.Keyframes.Contains(k)).ToList();
        if (!keys.Contains(kf))
        {
            keys = new List<Keyframe> { kf };
        }

        var dlg = new KeyframePropertiesDialog(Document, found.Group, keys, services);
        dlg.Show(this);
        return dlg;
    }

    // ---- curve handle drag (routed through the gizmo press path) ----------------------------------------

    private Func<Vector3, Vector2?> Projector(Viewport.IViewportSurface s) =>
        p => s.WorldToScreen(p, out Vector2 screen) ? screen : null;

    /// <summary>True when the selection shows curve handles a press could grab.</summary>
    private bool MoverHandlesAvailable() =>
        Document is { } doc && doc.Selection.Any(o => o.Model is Keyframe) && HandleTool?.Visible(doc.Selection).Count > 0;

    private MoverHandle? MoverHandleAt(Viewport.IViewportSurface s, int x, int y)
    {
        if (Document is null || HandleTool is not { } tool || s.PixelRay(x, y) is not (Vector3 ro, Vector3 rd))
        {
            return null;
        }

        return MoverHandleTool.Pick(tool.Visible(Document.Selection), Projector(s), new Vector2(x, y), ro, rd);
    }

    /// <summary>Press hit-test: claims the press for a handle drag when it lands on a handle (Alt selects instead).</summary>
    private bool MoverHandleHitTest(Viewport.IViewportSurface s, int x, int y)
    {
        _pendingMoverHandle = s.AltHeld || !MoverHandlesAvailable() ? null : MoverHandleAt(s, x, y);
        return _pendingMoverHandle is not null;
    }

    private void OnMoverHandleHover(Viewport.IViewportSurface s, int x, int y)
    {
        MoverHandle? h = MoverHandlesAvailable() ? MoverHandleAt(s, x, y) : null;
        bool changed = !ReferenceEquals(h?.Keyframe, _hoverMoverHandle?.Keyframe) || h?.Out != _hoverMoverHandle?.Out;
        _hoverMoverHandle = h;
        if (changed)
        {
            RefreshSelectionOverlay();
        }
    }

    private void BeginMoverHandleDrag(Viewport.IViewportSurface s, int x, int y)
    {
        MoverHandle? handle = _pendingMoverHandle;
        _pendingMoverHandle = null;
        if (handle is null || HandleTool is not { } tool || s.PixelRay(x, y) is not (Vector3 ro, Vector3 rd))
        {
            return;
        }

        _handleDragSurface = s;
        tool.Begin(handle, ro, rd);
        RefreshSelectionOverlay();
    }

    private void MoveMoverHandleDrag(int x, int y)
    {
        if (_handleDragSurface is { } s && HandleTool is { } tool && s.PixelRay(x, y) is (Vector3 ro, Vector3 rd) && tool.Move(ro, rd))
        {
            RebuildScene();
        }
    }

    private void EndMoverHandleDrag()
    {
        _handleDragSurface = null;
        if (HandleTool?.End() == true)
        {
            _moverPreview?.Refresh();
            RefreshPanels();
        }

        RebuildScene();
    }

    private void CancelMoverHandleDrag()
    {
        _handleDragSurface = null;
        if (HandleTool?.Cancel() == true)
        {
            RebuildScene();
            _dispatcher.ShowMessage("Handle drag cancelled.");
        }
    }

    private bool MoverHandleDragging => _handleTool?.Dragging is not null;
}
