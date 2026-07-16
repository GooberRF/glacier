using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.App.Viewport;
using Ged.Core.Editing;
using CoreVec3 = Ged.Core.Model.Vec3;
using LineSegment = Ged.Rendering.Scene.LineSegment;

namespace Ged.App;

/// <summary>
/// Snap-to-geometry (B1): the magnet split-button (main toggle + a chevron flyout of
/// snap-type checkboxes) and the shared helpers that arm the geometry snap index, derive
/// the ~8&#160;px world radius, and draw the highlight marker on the snapped target.
/// </summary>
public sealed partial class MainWindow
{
    private const float SnapPixels = 8f; // query radius in screen pixels

    private Control BuildMagnetSplitButton()
    {
        _magnetButton = new ToggleButton
        {
            Content = "🧲 Snap",
            Padding = new Avalonia.Thickness(8, 3),
            FontSize = 12,
            IsChecked = _settings.SnapEnabled,
            [ToolTip.TipProperty] = "Magnet snap for mouse drags: grid + geometry (vertices/midpoints/faces). Hold Alt to invert. Click ▾ to choose targets.",
        };
        _magnetButton.IsCheckedChanged += (_, _) => SetSnapEnabled(_magnetButton.IsChecked == true);

        var chevron = new Button
        {
            Content = "▾",
            Padding = new Avalonia.Thickness(4, 3),
            FontSize = 10,
            [ToolTip.TipProperty] = "Choose snap targets (Grid / Vertices / Midpoints / Faces)",
            Flyout = new Flyout { Content = BuildSnapKindsPanel() },
        };

        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { _magnetButton, chevron } };
    }

    private Control BuildSnapKindsPanel()
    {
        var p = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(8) };
        p.Children.Add(new TextBlock { Text = "Snap targets", FontWeight = FontWeight.Bold, FontSize = 12 });
        p.Children.Add(SnapKindCheck("Grid", SnapKinds.Grid));
        p.Children.Add(SnapKindCheck("Vertices", SnapKinds.Vertices));
        p.Children.Add(SnapKindCheck("Midpoints", SnapKinds.Midpoints));
        p.Children.Add(SnapKindCheck("Faces", SnapKinds.Faces));
        return p;
    }

    private CheckBox SnapKindCheck(string label, SnapKinds kind)
    {
        var cb = new CheckBox { Content = label, FontSize = 12, IsChecked = ((SnapKinds)_settings.SnapKinds & kind) != 0 };
        cb.IsCheckedChanged += (_, _) => SetSnapKind(kind, cb.IsChecked == true);
        return cb;
    }

    private void SetSnapKind(SnapKinds kind, bool on)
    {
        var cur = (SnapKinds)_settings.SnapKinds;
        SnapKinds next = on ? cur | kind : cur & ~kind;
        if (next == cur)
        {
            return;
        }

        _settings.SnapKinds = (int)next;
        SyncSnapPolicy();
        Persist();
        _dispatcher.ShowMessage($"Snap targets: {SnapKindsLabel()}");
    }

    private string SnapKindsLabel()
    {
        var cur = (SnapKinds)_settings.SnapKinds;
        var parts = new List<string>();
        if ((cur & SnapKinds.Grid) != 0)
        {
            parts.Add($"Grid {_settings.GridSize:0.##}m");
        }

        if ((cur & SnapKinds.Vertices) != 0)
        {
            parts.Add("Verts");
        }

        if ((cur & SnapKinds.Midpoints) != 0)
        {
            parts.Add("Mids");
        }

        if ((cur & SnapKinds.Faces) != 0)
        {
            parts.Add("Faces");
        }

        return parts.Count == 0 ? "none" : string.Join(" · ", parts);
    }

    /// <summary>Arms the shared snap policy with the current level's geometry index (drag/tool start).</summary>
    private void ArmGeometrySnap()
    {
        _snap.GeometryIndex = _settings.SnapEnabled && ((SnapKinds)_settings.SnapKinds & SnapKinds.Geometry) != 0
            ? _session.GetOrBuildSnapIndex()
            : null;
        _snap.ClearGeometrySnap();
    }

    /// <summary>Releases the geometry index and clears the highlight (drag/tool end).</summary>
    private void DisarmGeometrySnap()
    {
        _snap.GeometryIndex = null;
        _snap.ClearGeometrySnap();
    }

    /// <summary>The world radius that maps to <see cref="SnapPixels"/> screen pixels at a world point.</summary>
    private float SnapWorldRadius(IViewportSurface? surface, CoreVec3 worldPoint)
    {
        var s = surface ?? _viewportGrid.ActiveSurface;
        float wpp = s.Camera?.WorldPerPixel(new Vector3(worldPoint.X, worldPoint.Y, worldPoint.Z), s.SurfaceHeight) ?? 0.05f;
        return SnapPixels * wpp;
    }

    /// <summary>Snaps a free world point to nearby geometry (Draw Brush / placement), honoring the magnet + kinds.</summary>
    private CoreVec3 SnapFreePoint(CoreVec3 point, IViewportSurface? surface = null)
    {
        if (!_settings.SnapEnabled || ((SnapKinds)_settings.SnapKinds & SnapKinds.Geometry) == 0)
        {
            return point;
        }

        _snap.GeometryIndex = _session.GetOrBuildSnapIndex();
        return _snap.SnapWorldPoint(point, SnapWorldRadius(surface, point));
    }

    /// <summary>Snaps a one-shot placement point to nearby geometry (no lingering marker).</summary>
    private CoreVec3 SnapPlacement(CoreVec3 p)
    {
        CoreVec3 snapped = SnapFreePoint(p, _viewportGrid.CameraSurface);
        _snap.ClearGeometrySnap();
        return snapped;
    }

    /// <summary>The highlight marker at the last snapped geometry target (a small camera-scaled cross).</summary>
    private IEnumerable<LineSegment> BuildSnapMarker()
    {
        if (_snap.LastGeometrySnap is not SnapResult hit)
        {
            yield break;
        }

        var c = new Vector3(hit.Position.X, hit.Position.Y, hit.Position.Z);
        uint color = hit.Kind switch
        {
            SnapKinds.Vertices => Ged.Rendering.Scene.Palette.Rgba(80, 220, 255),
            SnapKinds.Midpoints => Ged.Rendering.Scene.Palette.Rgba(120, 255, 160),
            _ => Ged.Rendering.Scene.Palette.Rgba(255, 200, 80),
        };
        float r = MathF.Max(0.12f, SnapWorldRadius(_viewportGrid.ActiveSurface, hit.Position));
        yield return new LineSegment(c - new Vector3(r, 0, 0), c + new Vector3(r, 0, 0), color);
        yield return new LineSegment(c - new Vector3(0, r, 0), c + new Vector3(0, r, 0), color);
        yield return new LineSegment(c - new Vector3(0, 0, r), c + new Vector3(0, 0, r), color);
    }
}
