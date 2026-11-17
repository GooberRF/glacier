using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.App.Panels;

/// <summary>What the Terrain Tools panel uses from the shell.</summary>
public sealed class TerrainToolServices
{
    /// <summary>The tools were armed or disarmed in the panel (left drags on the terrain then paint).</summary>
    public Action<bool>? SetArmed { get; init; }

    /// <summary>Opens Generate Terrain on the panel's terrain.</summary>
    public Action? OpenGenerator { get; init; }

    /// <summary>After an edit the panel made (stitch, decorations): rebuild the scene.</summary>
    public Action? AfterEdit { get; init; }

    /// <summary>Show decorations was toggled.</summary>
    public Action<bool>? ShowDecorationsChanged { get; init; }

    /// <summary>A setting that changes the cursor changed (radius, falloff, mirror): redraw the overlay.</summary>
    public Action? SettingsChanged { get; init; }
}

/// <summary>
/// The Terrain Tools panel, docked in the Properties panel above a selected terrain's fields. It is Alpine RED's modeless
/// Terrain Tools window (editor_patch/resources.rc:1766-1834; behaviour terrain_paint.cpp): the target, the 14 tools in
/// RED's two columns with the selected tool's hint, the Layer list (base layers, then overlays, then decorations) with
/// Show decorations, the brush (radius, strength %, falloff, spacing %), the mirror lines, the sculpt fields (Set Height,
/// Ramp angle) with Stitch Edges, and a status line. Glacier adds the arm toggle (Alpine RED arms the tools by opening its
/// window), Generate Terrain, and the decoration list Alpine RED keeps in its Terrain Properties dialog. Every edit is one
/// undo step; strokes are the viewport's (<see cref="Services.TerrainToolController"/>).
/// </summary>
public sealed class TerrainToolsPanel : UserControl
{
    // terrain_paint.cpp:83-108, the hint per tool
    private static readonly Dictionary<TerrainTool, string> Hints = new()
    {
        [TerrainTool.PaintLayer] = "Blends toward the selected layer, or raises the selected overlay's or decoration's coverage. Strength: how far per dab.",
        [TerrainTool.EraseToBase] = "Blends toward layer 1, the base, or lowers the selected overlay's or decoration's coverage. Strength: how far per dab.",
        [TerrainTool.SmoothWeights] = "Blends layer weights, or the selected overlay's or decoration's coverage, toward their neighbours'. Strength: how far per dab.",
        [TerrainTool.PaintHoles] = "Cuts holes in the cells under the brush. Strength and falloff do not apply.",
        [TerrainTool.ClearHoles] = "Fills holes in the cells under the brush. Strength and falloff do not apply.",
        [TerrainTool.Raise] = "Raises by up to strength x radius / 20 per dab at the centre; hold still to keep raising. The height range grows as needed.",
        [TerrainTool.Lower] = "Lowers by up to strength x radius / 20 per dab at the centre; hold still to keep lowering. The height range grows as needed; a geoable terrain's bottom only moves if the surface is lowered through it.",
        [TerrainTool.SmoothHeights] = "Blends heights toward their local average. Strength: the blend per dab; hold still to keep smoothing.",
        [TerrainTool.Flatten] = "Blends toward the surface height where the stroke started. Strength: the blend per dab.",
        [TerrainTool.SetHeight] = "Blends toward the Height field (world Y). Strength: the blend per dab. Ctrl+click samples the height under the cursor.",
        [TerrainTool.Noise] = "Adds noise of up to +/- strength x radius / 4, a new pattern each stroke; going over a spot again in the same stroke does not add more.",
        [TerrainTool.GeoableChunks] = "Click or drag over chunks to set whether craters carve them (orange = geoable); the first chunk decides on or off. No rebuild needed, just save.",
        [TerrainTool.Ramp] = "Slopes at the Angle field along the drag (negative goes down) from the height where the stroke began; the direction locks after a short drag. Strength: the blend per dab.",
        [TerrainTool.BridgePoints] = "Press at one end and release at the other: blends toward a straight ramp between the heights there, as wide as the brush. Strength: the blend, applied once on release.",
    };

    // resources.rc:1777-1790: two columns
    private static readonly TerrainTool[] LeftColumn =
        { TerrainTool.PaintLayer, TerrainTool.EraseToBase, TerrainTool.SmoothWeights, TerrainTool.PaintHoles, TerrainTool.ClearHoles, TerrainTool.GeoableChunks, TerrainTool.BridgePoints };

    private static readonly TerrainTool[] RightColumn =
        { TerrainTool.Raise, TerrainTool.Lower, TerrainTool.SmoothHeights, TerrainTool.Flatten, TerrainTool.SetHeight, TerrainTool.Noise, TerrainTool.Ramp };

    private readonly EditorDocument _doc;
    private readonly LevelObject _obj;
    private readonly AlpineTerrain _t;
    private readonly TerrainBrushSettings _s;
    private readonly TerrainToolServices _services;
    private readonly TextBlock _hint;
    private readonly TextBlock _status;
    private readonly ListBox _layers;
    private readonly ListBox _decorations;
    private readonly Dictionary<TerrainTool, RadioButton> _tools = new();
    private bool _updating;

    public TerrainToolsPanel(EditorDocument doc, LevelObject terrain, TerrainBrushSettings settings, bool armed, TerrainToolServices? services = null, bool showDecorations = true)
    {
        _doc = doc;
        _obj = terrain;
        _t = (AlpineTerrain)terrain.Model;
        _s = settings;
        _services = services ?? new TerrainToolServices();
        _hint = new TextBlock { Name = "ttools_hint", FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MinHeight = 44 };
        _status = new TextBlock { Name = "ttools_status", FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        _layers = new ListBox { Name = "ttools_layers", MaxHeight = 120, SelectionMode = SelectionMode.Single };
        _decorations = new ListBox { Name = "ttools_deco_list", MaxHeight = 100, SelectionMode = SelectionMode.Single };

        var root = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
        root.Children.Add(new TextBlock { Name = "ttools_target", Text = $"Terrain Tools - {Ged.Core.Compiler.TerrainChunkEmitter.Label(_t)}", FontWeight = FontWeight.SemiBold, FontSize = 13 });

        var arm = new ToggleButton
        {
            Name = "ttools_active",
            Content = "Paint in the viewport",
            IsChecked = armed,
            [ToolTip.TipProperty] = "While on, a left drag on this terrain paints with the selected tool (Esc cancels a stroke; [ and ] scale the radius). Off, clicks select as usual.",
        };
        arm.IsCheckedChanged += (_, _) => _services.SetArmed?.Invoke(arm.IsChecked == true);
        var generate = new Button { Name = "ttools_generate", Content = "Generate...", [ToolTip.TipProperty] = "Generate Terrain: procedural heights with a preview." };
        generate.Click += (_, _) => _services.OpenGenerator?.Invoke();
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { arm, generate } });

        // Tool
        var toolGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", 7))) };
        for (int i = 0; i < 7; i++)
        {
            AddTool(toolGrid, LeftColumn[i], 0, i);
            AddTool(toolGrid, RightColumn[i], 1, i);
        }

        root.Children.Add(Group("Tool", new StackPanel { Spacing = 4, Children = { toolGrid, _hint } }));

        // Layer
        _layers.SelectionChanged += (_, _) =>
        {
            if (!_updating && _layers.SelectedIndex >= 0)
            {
                _s.Layer = _layers.SelectedIndex;
                Status();
            }
        };
        ShowDecorations = showDecorations;
        var showDeco = new CheckBox { Name = "ttools_show_decorations", Content = "Show decorations", IsChecked = showDecorations };
        showDeco.IsCheckedChanged += (_, _) =>
        {
            ShowDecorations = showDeco.IsChecked == true;
            _services.ShowDecorationsChanged?.Invoke(ShowDecorations);
        };
        root.Children.Add(Group("Layer", new StackPanel { Spacing = 4, Children = { _layers, showDeco } }));

        // Brush
        var falloff = new ComboBox { Name = "ttools_falloff", ItemsSource = new[] { "Smooth", "Linear", "Constant" }, SelectedIndex = (int)_s.Falloff, MinWidth = 110 };
        falloff.SelectionChanged += (_, _) =>
        {
            if (falloff.SelectedIndex >= 0)
            {
                _s.Falloff = (TerrainFalloff)falloff.SelectedIndex;
                _services.SettingsChanged?.Invoke();
            }
        };
        root.Children.Add(Group("Brush", new StackPanel
        {
            Spacing = 3,
            Children =
            {
                Row("Radius (m) [ ]", Num("ttools_radius", _s.Radius, (decimal)TerrainBrush.MinRadius, (decimal)TerrainBrush.MaxRadius, 0.5m, 2, v => _s.Radius = (float)v)),
                Row("Strength (%)", Num("ttools_strength", Math.Round(_s.Strength * 100), 1, 100, 5, 0, v => _s.Strength = (float)v / 100f)),
                Row("Falloff", falloff),
                Row("Spacing (%)", Num("ttools_spacing", Math.Round(_s.Spacing * 100), 5, 200, 5, 0, v => _s.Spacing = (float)v / 100f)),
            },
        }));

        // Mirror X (U) / Z (V), offset in cells
        var mx = new CheckBox { Name = "ttools_mirror_x", Content = "X", IsChecked = _s.MirrorX };
        var mz = new CheckBox { Name = "ttools_mirror_z", Content = "Z", IsChecked = _s.MirrorZ };
        NumericUpDown mxo = Num("ttools_mirror_x_offset", _s.MirrorXOffset2 / 2.0, -AlpineTerrainRules.Cells(_t.Nx), AlpineTerrainRules.Cells(_t.Nx), 0.5m, 1, v => _s.MirrorXOffset2 = (int)Math.Round(v * 2));
        NumericUpDown mzo = Num("ttools_mirror_z_offset", _s.MirrorZOffset2 / 2.0, -AlpineTerrainRules.Cells(_t.Nz), AlpineTerrainRules.Cells(_t.Nz), 0.5m, 1, v => _s.MirrorZOffset2 = (int)Math.Round(v * 2));
        mx.IsCheckedChanged += (_, _) => { _s.MirrorX = mx.IsChecked == true; mxo.IsEnabled = _s.MirrorX; _services.SettingsChanged?.Invoke(); Status(); };
        mz.IsCheckedChanged += (_, _) => { _s.MirrorZ = mz.IsChecked == true; mzo.IsEnabled = _s.MirrorZ; _services.SettingsChanged?.Invoke(); Status(); };
        mxo.IsEnabled = _s.MirrorX;
        mzo.IsEnabled = _s.MirrorZ;
        var centre = new Button { Name = "ttools_mirror_centre", Content = "Centre" };
        centre.Click += (_, _) =>
        {
            _s.MirrorXOffset2 = 0;
            _s.MirrorZOffset2 = 0;
            mxo.Value = 0;
            mzo.Value = 0;
        };
        mx.MinWidth = 40;
        mz.MinWidth = 40;
        root.Children.Add(Group("Mirror X (U) / Z (V), offset in cells", new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { mx, mxo } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { mz, mzo, centre } },
            },
        }));

        // Sculpt
        float maxY = AlpineTerrainRules.MaxCoord + AlpineTerrainRules.MaxHeightRange;
        NumericUpDown height = Num("ttools_height", _s.SetHeight, (decimal)-maxY, (decimal)maxY, 1m, 3, v => _s.SetHeight = (float)v);
        HeightField = height;
        var stitch = new Button { Name = "ttools_stitch", Content = "Stitch Edges", [ToolTip.TipProperty] = "Snaps this terrain's border to any grid-aligned terrain of the same cell size that shares it." };
        stitch.Click += (_, _) => Stitch();
        root.Children.Add(Group("Sculpt", new StackPanel
        {
            Spacing = 3,
            Children =
            {
                Row("Height (Y) Ctrl+click", height),
                Row("Angle (deg)", Num("ttools_angle", _s.RampAngle, (decimal)-TerrainBrush.MaxRampAngle, (decimal)TerrainBrush.MaxRampAngle, 1m, 1, v => _s.RampAngle = (float)v)),
                stitch,
            },
        }));

        root.Children.Add(Group("Decorations", BuildDecorations()));
        root.Children.Add(_status);

        Content = root;
        RefreshLists();
        LoadDecoration();
        SelectTool(_s.Tool);
    }

    /// <summary>Whether decorations draw in the viewport (Alpine RED's Show decorations).</summary>
    public bool ShowDecorations { get; private set; } = true;

    /// <summary>The Set Height field (Ctrl+click in the viewport samples it).</summary>
    public NumericUpDown HeightField { get; }

    /// <summary>Shows a status note (the last stroke's).</summary>
    public void SetNote(string? note) => Status(note);

    /// <summary>Re-reads the settings a viewport click changed (sampled height).</summary>
    public void RefreshSettings()
    {
        _updating = true;
        HeightField.Value = (decimal)_s.SetHeight;
        _updating = false;
    }

    private void AddTool(Grid grid, TerrainTool tool, int column, int row)
    {
        var rb = new RadioButton
        {
            Name = "ttools_tool_" + Snake(tool),
            Content = Title(tool),
            GroupName = "ttools_tool",
            FontSize = 12,
            IsEnabled = tool != TerrainTool.GeoableChunks || _t.Geoable,
        };
        rb.IsCheckedChanged += (_, _) =>
        {
            if (rb.IsChecked == true && !_updating)
            {
                _s.Tool = tool;
                _hint.Text = Hints[tool];
                _services.SettingsChanged?.Invoke();
                Status();
            }
        };
        Grid.SetColumn(rb, column);
        Grid.SetRow(rb, row);
        grid.Children.Add(rb);
        _tools[tool] = rb;
    }

    private void SelectTool(TerrainTool tool)
    {
        if (tool == TerrainTool.GeoableChunks && !_t.Geoable)
        {
            tool = TerrainTool.PaintLayer;
            _s.Tool = tool;
        }

        _updating = true;
        _tools[tool].IsChecked = true;
        _updating = false;
        _hint.Text = Hints[tool];
        Status();
    }

    /// <summary>"Paint Layer", as the panel's radio buttons read (resources.rc:1777-1790).</summary>
    public static string Title(TerrainTool t) => t switch
    {
        TerrainTool.PaintLayer => "Paint Layer",
        TerrainTool.EraseToBase => "Erase to Base",
        TerrainTool.SmoothWeights => "Smooth Weights",
        TerrainTool.PaintHoles => "Paint Holes",
        TerrainTool.ClearHoles => "Clear Holes",
        TerrainTool.GeoableChunks => "Geoable Chunks",
        TerrainTool.BridgePoints => "Bridge Points",
        TerrainTool.SmoothHeights => "Smooth Heights",
        TerrainTool.SetHeight => "Set Height",
        _ => t.ToString(),
    };

    private static string Snake(TerrainTool t) =>
        string.Concat(t.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    /// <summary>The Layer list: layers, then overlays, then decorations, each with its kind (terrain_paint_math.h:134-174).</summary>
    private void RefreshLists()
    {
        _updating = true;
        var items = new List<string>();
        for (int i = 0; i < _t.Layers.Count; i++)
        {
            items.Add($"{i + 1}. {_t.Layers[i].Texture}");
        }

        foreach (AlpineTerrainLayer o in _t.Overlays)
        {
            items.Add($"Overlay: {o.Texture}");
        }

        foreach (AlpineTerrainDecoration d in _t.Decorations)
        {
            items.Add($"Decoration: {d.Mesh}");
        }

        _layers.ItemsSource = items;
        _layers.SelectedIndex = Math.Clamp(_s.Layer, 0, items.Count - 1);
        _s.Layer = _layers.SelectedIndex;
        _decorations.ItemsSource = _t.Decorations.Select((d, i) => $"{i + 1}. {d.Mesh} ({d.Density:0.##}/m²)").ToList();
        _updating = false;
    }

    private void Status(string? note = null)
    {
        string text = $"Tool {Title(_s.Tool)}";
        if (_s.Tool == TerrainTool.GeoableChunks && _t.Geoable)
        {
            int n = Ged.Core.Compiler.TerrainChunkEmitter.ChunkCount(_t);
            int on = Enumerable.Range(0, n).Count(k => TerrainStroke.ChunkGeoable(_t.Grid, k));
            text += $". Geoable chunks {on}/{n}";
        }

        if (_s.MirrorX || _s.MirrorZ)
        {
            text += ". Mirror " + (_s.MirrorX && _s.MirrorZ ? "X+Z" : _s.MirrorX ? "X" : "Z");
        }

        if (!string.IsNullOrEmpty(note))
        {
            text += ". " + note;
        }

        _status.Text = text;
    }

    private void Stitch()
    {
        var others = _doc.Objects.Select(o => o.Model).OfType<AlpineTerrain>().Where(o => !ReferenceEquals(o, _t)).ToList();
        int matched = 0, used = 0;
        TerrainOps.Apply(_doc, _obj, "Stitch terrain edges", t => (matched, used) = TerrainOps.StitchEdges(t, others));
        Status(matched == 0
            ? "Stitch: no grid-aligned terrain with the same cell size touches this one's border"
            : $"Stitched {matched} border vertices to {used} terrain(s)");
        _services.AfterEdit?.Invoke();
    }

    // ---- decorations (Alpine RED keeps them in Terrain Properties; editor_patch/terrain.cpp:1193-1260) ----------------

    private TextBox? _decoMesh;
    private NumericUpDown? _decoDensity;
    private NumericUpDown? _decoScaleMin;
    private NumericUpDown? _decoScaleMax;
    private NumericUpDown? _decoSlope;
    private NumericUpDown? _decoDraw;
    private NumericUpDown? _decoOffset;
    private ComboBox? _decoLink;
    private CheckBox? _decoAlign;
    private CheckBox? _decoYaw;
    private CheckBox? _decoShadows;

    private Control BuildDecorations()
    {
        _decoMesh = new TextBox { Name = "ttools_deco_mesh", MinWidth = 160, Watermark = "mesh.v3m" };
        _decoDensity = Num("ttools_deco_density", 0.5, 0, (decimal)AlpineTerrainRules.MaxDecorationDensity, 0.05m, 3, _ => { });
        _decoScaleMin = Num("ttools_deco_scale_min", 1, (decimal)AlpineTerrainRules.MinDecorationScale, (decimal)AlpineTerrainRules.MaxDecorationScale, 0.05m, 2, _ => { });
        _decoScaleMax = Num("ttools_deco_scale_max", 1, (decimal)AlpineTerrainRules.MinDecorationScale, (decimal)AlpineTerrainRules.MaxDecorationScale, 0.05m, 2, _ => { });
        _decoSlope = Num("ttools_deco_max_slope", 35, 0, (decimal)AlpineTerrainRules.MaxDecorationSlope, 1m, 1, _ => { });
        _decoDraw = Num("ttools_deco_draw_distance", 80, (decimal)AlpineTerrainRules.MinDecorationDrawDistance, (decimal)AlpineTerrainRules.MaxDecorationDrawDistance, 5m, 1, _ => { });
        _decoOffset = Num("ttools_deco_vertical_offset", 0, (decimal)-AlpineTerrainRules.MaxDecorationOffset, (decimal)AlpineTerrainRules.MaxDecorationOffset, 0.1m, 2, _ => { });
        _decoLink = new ComboBox { Name = "ttools_deco_link", MinWidth = 140 };
        _decoAlign = new CheckBox { Name = "ttools_deco_align", Content = "Align to slope" };
        _decoYaw = new CheckBox { Name = "ttools_deco_random_yaw", Content = "Random yaw", IsChecked = true };
        _decoShadows = new CheckBox { Name = "ttools_deco_casts_shadows", Content = "Casts shadows" };
        _decorations.SelectionChanged += (_, _) => LoadDecoration();

        var add = new Button { Name = "ttools_deco_add", Content = "Add" };
        add.Click += (_, _) => ApplyDecoration(add: true);
        var apply = new Button { Name = "ttools_deco_apply", Content = "Apply" };
        apply.Click += (_, _) => ApplyDecoration(add: false);
        var remove = new Button { Name = "ttools_deco_remove", Content = "Remove" };
        remove.Click += (_, _) => RemoveDecoration();
        return new StackPanel
        {
            Spacing = 3,
            Children =
            {
                _decorations,
                Row("Mesh", _decoMesh),
                Row("Density (/m²)", _decoDensity),
                Row("Scale min", _decoScaleMin),
                Row("Scale max", _decoScaleMax),
                Row("Max slope (deg)", _decoSlope),
                Row("Draw distance (m)", _decoDraw),
                Row("Vertical offset", _decoOffset),
                Row("Link layer", _decoLink),
                new WrapPanel { Children = { _decoAlign, _decoYaw, _decoShadows } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, apply, remove } },
                new TextBlock { Text = "Paint where they grow: select the decoration in the Layer list, then Paint Layer / Erase.", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
            },
        };
    }

    private void LoadDecoration()
    {
        _decoLink!.ItemsSource = new[] { "None" }.Concat(_t.Layers.Select((l, i) => $"{i + 1}. {l.Texture}")).ToList();
        int i = _decorations.SelectedIndex;
        if (i < 0 || i >= _t.Decorations.Count)
        {
            _decoLink.SelectedIndex = 0;
            return;
        }

        AlpineTerrainDecoration d = _t.Decorations[i];
        _decoMesh!.Text = d.Mesh;
        _decoDensity!.Value = (decimal)d.Density;
        _decoScaleMin!.Value = (decimal)d.ScaleMin;
        _decoScaleMax!.Value = (decimal)d.ScaleMax;
        _decoSlope!.Value = (decimal)d.MaxSlopeDegrees;
        _decoDraw!.Value = (decimal)d.DrawDistance;
        _decoOffset!.Value = (decimal)d.VerticalOffset;
        _decoLink.SelectedIndex = d.LinkLayer == AlpineTerrainRules.DecorationLinkNone ? 0 : d.LinkLayer + 1;
        _decoAlign!.IsChecked = (d.Flags & AlpineTerrainSurface.DecorationFlagAlignToSlope) != 0;
        _decoYaw!.IsChecked = (d.Flags & AlpineTerrainSurface.DecorationFlagRandomYaw) != 0;
        _decoShadows!.IsChecked = (d.Flags & AlpineTerrainSurface.DecorationFlagCastsShadows) != 0;
    }

    private void ApplyDecoration(bool add)
    {
        int index = add ? _t.Decorations.Count : _decorations.SelectedIndex;
        if (index < 0)
        {
            Status("Select a decoration to change, or Add one");
            return;
        }

        string mesh = _decoMesh!.Text?.Trim() ?? string.Empty;
        if (!AlpineTerrainRules.DecorationMeshValid(mesh) || mesh.Length == 0)
        {
            Status("A decoration needs a .v3m or .v3c mesh name of at most 31 characters");
            return;
        }

        byte keep = index < _t.Decorations.Count ? (byte)(_t.Decorations[index].Flags & ~0x07) : (byte)0;
        byte flags = (byte)(keep
            | (_decoAlign!.IsChecked == true ? AlpineTerrainSurface.DecorationFlagAlignToSlope : 0)
            | (_decoYaw!.IsChecked == true ? AlpineTerrainSurface.DecorationFlagRandomYaw : 0)
            | (_decoShadows!.IsChecked == true ? AlpineTerrainSurface.DecorationFlagCastsShadows : 0));
        int link = _decoLink!.SelectedIndex <= 0 ? AlpineTerrainRules.DecorationLinkNone : _decoLink.SelectedIndex - 1;
        var d = new AlpineTerrainDecoration(mesh, (float)(_decoDensity!.Value ?? 0.5m), (float)(_decoScaleMin!.Value ?? 1m), (float)(_decoScaleMax!.Value ?? 1m),
            (float)(_decoSlope!.Value ?? 35m), (float)(_decoDraw!.Value ?? 80m), (float)(_decoOffset!.Value ?? 0m), (byte)link, flags);
        TerrainOps.Apply(_doc, _obj, add ? "Add terrain decoration" : "Change terrain decoration", t => TerrainOps.SetDecoration(t, index, d));
        RefreshLists();
        _decorations.SelectedIndex = index;
        _services.AfterEdit?.Invoke();
    }

    private void RemoveDecoration()
    {
        int index = _decorations.SelectedIndex;
        if (index < 0 || index >= _t.Decorations.Count)
        {
            return;
        }

        TerrainOps.Apply(_doc, _obj, "Remove terrain decoration", t => TerrainOps.RemoveDecoration(t, index));
        RefreshLists();
        _services.AfterEdit?.Invoke();
    }

    // ---- small builders --------------------------------------------------------------------------------------------

    private NumericUpDown Num(string name, double value, decimal min, decimal max, decimal step, int decimals, Action<decimal> set)
    {
        var n = new NumericUpDown
        {
            Name = name,
            Minimum = min,
            Maximum = max,
            Increment = step,
            Value = Math.Clamp((decimal)value, min, max),
            FormatString = decimals == 0 ? "0" : "0." + new string('#', decimals),
            MinWidth = 110,
        };
        n.ValueChanged += (_, e) =>
        {
            if (!_updating && e.NewValue is decimal v)
            {
                set(v);
                _services.SettingsChanged?.Invoke();
            }
        };
        return n;
    }

    private static Control Row(string label, Control editor)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("128,*") };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(editor, 1);
        editor.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(editor);
        return grid;
    }

    private static Control Group(string title, Control content) => new Border
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(70, 160, 160, 160)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 4),
        Child = new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.85 }, content },
        },
    };
}
