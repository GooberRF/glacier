using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editing;
using Ged.Core.Model;
using Brush = Ged.Core.Model.Brush;
using Geometry = Ged.Core.Model.Geometry;

namespace Ged.App.Panels;

/// <summary>
/// The shared per-face property editor: texture name (+ picker button), the genuinely-authored
/// face flags (full-bright / show-sky / mirrored), scroll U/V, the 4-level lightmap resolution,
/// and the 32-bit smoothing-group mask. The five build-derived flags (has-alpha / has-holes /
/// invisible / liquid-surface / detail) are shown as READ-ONLY indicators: RED generates them
/// at build time from the texture and brush (RED.exe <c>FlagFaceTextureTraits</c> derives
/// alpha/holes/invisible from the texture; detail is a brush property; the liquid surface is
/// generated from the liquid room), never as user-set face attributes, so GED does the same.
/// Multi-select mixed-value aware, undo-safe (routes every edit through
/// <see cref="BrushEditor.EditSelectedFaces"/> so only the brushes section is dirtied). The
/// SAME control backs both the Properties panel (when face(s) are selected) and Face mode's
/// Texture/UV tab, so there is one face-editing surface everywhere.
/// </summary>
internal sealed class FacePropsControl : UserControl
{
    private readonly StackPanel _root = new() { Spacing = 3 };
    private BrushEditor? _be;
    private Action? _afterEdit;
    private Action<string>? _report;
    private Action? _openPicker;
    private Action<Action<string>>? _armEyedropper;

    public FacePropsControl() => Content = _root;

    /// <summary>
    /// Binds the editor to a brush editor and its callbacks: <paramref name="afterEdit"/> runs
    /// after every committed edit (scene/overlay refresh), <paramref name="report"/> surfaces an
    /// op message, <paramref name="openTexturePicker"/> (optional) opens the full texture browser
    /// (Face mode's Texture/UV tab) for the "Browse…" button, and <paramref name="armEyedropper"/>
    /// (optional) arms a next-viewport-click texture sample for the "Pick" eyedropper (item 6).
    /// </summary>
    public void Bind(
        BrushEditor be,
        Action afterEdit,
        Action<string>? report = null,
        Action? openTexturePicker = null,
        Action<Action<string>>? armEyedropper = null)
    {
        _be = be;
        _afterEdit = afterEdit;
        _report = report;
        _openPicker = openTexturePicker;
        _armEyedropper = armEyedropper;
        Refresh();
    }

    private List<(Geometry G, Face F)> SelectedFaces()
    {
        var list = new List<(Geometry, Face)>();
        if (_be is null)
        {
            return list;
        }

        foreach ((int uid, int fi) in _be.SelectedFaces)
        {
            if (_be.FindBrush(uid) is Brush b && fi >= 0 && fi < b.Geometry.Faces.Count)
            {
                list.Add((b.Geometry, b.Geometry.Faces[fi]));
            }
        }

        return list;
    }

    public void Refresh()
    {
        _root.Children.Clear();
        var faces = SelectedFaces();
        if (faces.Count == 0)
        {
            _root.Children.Add(new TextBlock { Text = "Select face(s) to edit properties.", FontSize = 11, Foreground = Brushes.Gray });
            return;
        }

        _root.Children.Add(new TextBlock { Text = $"{faces.Count} face(s)", FontSize = 11, Foreground = Brushes.Gray });

        // Texture name (mixed-value aware) + inline set + optional picker.
        _root.Children.Add(BuildTextureRow(faces));

        // Genuinely-authored flags (user-selectable).
        _root.Children.Add(FlagCheck("Full-bright", faces, FaceFlags.FullBright));
        _root.Children.Add(FlagCheck("Show Sky", faces, FaceFlags.ShowSky));
        _root.Children.Add(FlagCheck("Mirrored", faces, FaceFlags.Mirrored));

        // Build-derived flags: the compiler generates these from the texture and brush the way
        // RED does, so they are shown read-only rather than offered as editable face attributes.
        _root.Children.Add(Head("Build-derived (read-only)"));
        _root.Children.Add(FlagIndicator("Has Alpha", faces, FaceFlags.HasAlpha, DerivedTip));
        _root.Children.Add(FlagIndicator("Has Holes", faces, FaceFlags.HasHoles, DerivedTip));
        _root.Children.Add(FlagIndicator("Invisible", faces, FaceFlags.IsInvisible, DerivedTip));
        _root.Children.Add(FlagIndicator("Liquid Surface", faces, FaceFlags.LiquidSurface, DerivedTip));
        _root.Children.Add(FlagIndicator("Detail", faces, FaceFlags.IsDetail, DerivedTip));

        // Scroll velocities.
        Uv scroll0 = FaceProps.GetScroll(faces[0].G, faces[0].F);
        bool scrollMixed = faces.Any(t => !FaceProps.GetScroll(t.G, t.F).Equals(scroll0));
        _root.Children.Add(NumRow("Scroll U (px/s)", scrollMixed ? 0f : scroll0.U, u => SetScroll(u, null)));
        _root.Children.Add(NumRow("Scroll V (px/s)", scrollMixed ? 0f : scroll0.V, v => SetScroll(null, v)));

        // Lightmap resolution (4-level).
        int res0 = FaceProps.GetLightmapResolution(faces[0].F);
        bool resMixed = faces.Any(t => FaceProps.GetLightmapResolution(t.F) != res0);
        var resCombo = new ComboBox
        {
            ItemsSource = new[] { "Lowest", "Low", "High", "Highest" },
            SelectedIndex = resMixed ? -1 : res0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        resCombo.SelectionChanged += (_, _) =>
        {
            if (resCombo.SelectedIndex >= 0)
            {
                int r = resCombo.SelectedIndex;
                Commit("Lightmap resolution", (g, fi) => FaceProps.SetLightmapResolution(g.Faces[fi], r));
            }
        };
        _root.Children.Add(Labeled("Lightmap Resolution", resCombo));

        // Smoothing groups (32-bit mask).
        _root.Children.Add(Head("Smoothing Groups"));
        _root.Children.Add(BuildSmoothingGroupGrid(faces));
    }

    private Control BuildTextureRow(List<(Geometry G, Face F)> faces)
    {
        string TexOf((Geometry G, Face F) t) =>
            t.F.Texture >= 0 && t.F.Texture < t.G.Textures.Count ? t.G.Textures[t.F.Texture] : "(none)";
        string name0 = TexOf(faces[0]);
        bool mixed = faces.Any(t => !string.Equals(TexOf(t), name0, StringComparison.OrdinalIgnoreCase));

        var box = new TextBox { Text = mixed ? string.Empty : name0, Watermark = mixed ? "— (mixed)" : "texture name", FontSize = 12, Width = 150 };
        var set = new Button { Content = "Set", FontSize = 11, Padding = new Avalonia.Thickness(6, 2) };
        set.Click += (_, _) =>
        {
            string tex = box.Text?.Trim() ?? string.Empty;
            if (tex.Length == 0)
            {
                return;
            }

            Commit($"Apply {tex}", (g, fi) => g.Faces[fi].Texture = GeometryUtil.EnsureTexture(g, tex));
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(box);
        row.Children.Add(set);

        // Pick = eyedropper: the next viewport click samples a face's texture into the field.
        if (_armEyedropper is not null)
        {
            var pick = new Button
            {
                Content = "Pick",
                FontSize = 11,
                Padding = new Avalonia.Thickness(6, 2),
                [ToolTip.TipProperty] = "Eyedropper: click a face in the viewport to sample its texture into this field.",
            };
            pick.Click += (_, _) => _armEyedropper!(name => box.Text = name);
            row.Children.Add(pick);
        }

        // Browse = open the full texture browser (Face mode's Texture/UV tab / Asset Browser).
        if (_openPicker is not null)
        {
            var browse = new Button
            {
                Content = "Browse…",
                FontSize = 11,
                Padding = new Avalonia.Thickness(6, 2),
                [ToolTip.TipProperty] = "Open the texture browser.",
            };
            browse.Click += (_, _) => _openPicker!();
            row.Children.Add(browse);
        }

        return Labeled("Texture", row);
    }

    /// <summary>Tooltip shown on every build-derived (read-only) flag indicator.</summary>
    private const string DerivedTip = "Determined by the build from the texture and brush.";

    private Control FlagCheck(string label, List<(Geometry G, Face F)> faces, FaceFlags flag)
    {
        bool all = faces.All(t => FaceProps.Get(t.F, flag));
        bool none = faces.All(t => !FaceProps.Get(t.F, flag));
        var cb = new CheckBox { Content = label, FontSize = 12, IsThreeState = !all && !none, IsChecked = all ? true : (none ? false : (bool?)null) };
        cb.IsCheckedChanged += (_, _) =>
        {
            bool val = cb.IsChecked ?? false;
            cb.IsThreeState = false;
            Commit($"Set {label}", (g, fi) => FaceProps.Set(g.Faces[fi], flag, val));
        };
        return cb;
    }

    /// <summary>
    /// A read-only indicator for a build-derived flag: a disabled checkbox that reflects the
    /// current stored value (three-state across a mixed selection) with an explanatory tooltip.
    /// It carries NO change handler, so it never edits the face — the value is generated at
    /// build time, and the stored flags are left byte-untouched on load/save.
    /// </summary>
    private static Control FlagIndicator(string label, List<(Geometry G, Face F)> faces, FaceFlags flag, string tooltip)
    {
        bool all = faces.All(t => FaceProps.Get(t.F, flag));
        bool none = faces.All(t => !FaceProps.Get(t.F, flag));
        return new CheckBox
        {
            Content = label,
            FontSize = 12,
            IsEnabled = false,
            IsHitTestVisible = false,
            IsThreeState = !all && !none,
            IsChecked = all ? true : (none ? false : (bool?)null),
            [ToolTip.TipProperty] = tooltip,
        };
    }

    private void SetScroll(float? u, float? v)
    {
        if (_be is null || _be.SelectedFaces.Count == 0)
        {
            return;
        }

        Commit("Set scroll", (g, fi) =>
        {
            Uv cur = FaceProps.GetScroll(g, g.Faces[fi]);
            FaceProps.SetScroll(g, g.Faces[fi], u ?? cur.U, v ?? cur.V);
        });
    }

    private Control BuildSmoothingGroupGrid(List<(Geometry G, Face F)> faces)
    {
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 260 };
        for (int g = 0; g < 32; g++)
        {
            int group = g;
            bool all = faces.All(t => FaceProps.GetSmoothingGroup(t.F, group));
            bool none = faces.All(t => !FaceProps.GetSmoothingGroup(t.F, group));
            var tb = new ToggleButton
            {
                Content = group.ToString(CultureInfo.InvariantCulture),
                Width = 28,
                Height = 24,
                FontSize = 9,
                Margin = new Avalonia.Thickness(1),
                IsChecked = all ? true : (none ? false : (bool?)null),
                IsThreeState = !all && !none,
            };
            tb.IsCheckedChanged += (_, _) =>
            {
                bool val = tb.IsChecked ?? false;
                tb.IsThreeState = false;
                Commit($"Smoothing group {group}", (geo, fi) => FaceProps.SetSmoothingGroup(geo.Faces[fi], group, val));
            };
            grid.Children.Add(tb);
        }

        return grid;
    }

    private void Commit(string description, Action<Geometry, int> op)
    {
        if (_be is null)
        {
            return;
        }

        OpResult r = _be.EditSelectedFaces(description, op);
        if (!r.Success)
        {
            _report?.Invoke(r.Message);
        }

        _afterEdit?.Invoke();
    }

    // ---- tiny UI helpers ------------------------------------------------------

    private static Control Head(string t) => new TextBlock { Text = t, FontWeight = FontWeight.Bold, FontSize = 11, Margin = new Avalonia.Thickness(0, 4, 0, 2) };

    private static Control Labeled(string label, Control c)
    {
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(new TextBlock { Text = label, FontSize = 11 });
        p.Children.Add(c);
        return p;
    }

    private static Control NumRow(string label, float value, Action<float> set)
    {
        var box = new NumericUpDown { Value = (decimal)value, Increment = 1m, Minimum = -100000m, Maximum = 100000m, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.ValueChanged += (_, _) => set((float)(box.Value ?? 0));
        return Labeled(label, box);
    }
}
