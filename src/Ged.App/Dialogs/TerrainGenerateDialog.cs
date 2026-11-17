using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.App.Dialogs;

/// <summary>
/// Generate Terrain, over Glacier's <see cref="TerrainGenerator"/>: Alpine RED's dialog (editor_patch/resources.rc:1864-
/// 1977) — Grid (vertices, Height Min, Height Range), Noise (type, seed with Randomize, feature size, octaves, roughness,
/// lacunarity, domain warp), Shaping (exponent, terraces, offset, edge falloff and its width), the Erosion and Splat Map
/// groups (shown, not available: Glacier's generator has neither — FEATURES approximation 103) and a live Height /
/// Shaded preview with statistics. Apply replaces the terrain's heights (and grid size) as one undo step.
/// </summary>
public sealed class TerrainGenerateDialog : Window
{
    private const int PreviewSize = 256;

    private readonly EditorDocument _doc;
    private readonly LevelObject _obj;
    private readonly AlpineTerrain _t;
    private readonly Image _preview = new() { Name = "tgen_preview", Width = PreviewSize, Height = PreviewSize, Stretch = Stretch.Uniform };
    private readonly TextBlock _stats = new() { Name = "tgen_stats", FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 260 };
    private readonly RadioButton _shaded = new() { Name = "tgen_preview_shaded", Content = "Shaded", GroupName = "tgen_preview" };
    private readonly RadioButton _height = new() { Name = "tgen_preview_height", Content = "Height", GroupName = "tgen_preview", IsChecked = true };
    private bool _loading;

    public TerrainGenerateDialog(EditorDocument doc, LevelObject terrain)
    {
        _doc = doc;
        _obj = terrain;
        _t = (AlpineTerrain)terrain.Model;
        Title = "Generate Terrain";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Nx = Int("tgen_nx", _t.Nx, AlpineTerrainRules.MinVerts, AlpineTerrainRules.MaxVerts);
        Nz = Int("tgen_nz", _t.Nz, AlpineTerrainRules.MinVerts, AlpineTerrainRules.MaxVerts);
        HeightMin = Dec("tgen_height_min", _t.HeightMin, -AlpineTerrainRules.MaxCoord, AlpineTerrainRules.MaxCoord, 1, 3);
        HeightRange = Dec("tgen_height_range", _t.HeightRange, AlpineTerrainRules.MinHeightRange, AlpineTerrainRules.MaxHeightRange, 1, 3);
        _loading = true;
        NoiseType = new ComboBox { Name = "tgen_type", ItemsSource = new[] { "fBm", "Ridged", "Billow", "Hybrid" }, MinWidth = 110 };
        Seed = Int("tgen_seed", 1, 0, uint.MaxValue);
        Feature = Dec("tgen_feature", 128, 1, 65536, 8, 2);
        Octaves = Int("tgen_octaves", 6, 1, 12);
        Roughness = Dec("tgen_roughness", 0.45, 0, 1, 0.05, 3);
        Lacunarity = Dec("tgen_lacunarity", 2, 1, 4, 0.1, 2);
        Warp = Dec("tgen_warp", 0.25, 0, 2, 0.05, 2);
        Exponent = Dec("tgen_exponent", 1, 0.1, 8, 0.1, 2);
        Terraces = Int("tgen_terraces", 0, 0, 64);
        Offset = Dec("tgen_offset", 0, -100, 100, 5, 1);
        Falloff = new ComboBox { Name = "tgen_falloff", ItemsSource = new[] { "None", "Island", "Basin" }, MinWidth = 110 };
        FalloffWidth = Dec("tgen_falloff_width", 30, 1, 100, 5, 1);
        Smooth = Int("tgen_smooth", 1, 0, 16);
        NoiseType.SelectionChanged += (_, _) => Regenerate();
        Falloff.SelectionChanged += (_, _) => Regenerate();
        Defaults(new TerrainGenSettings());

        var randomize = new Button { Name = "tgen_randomize", Content = "Randomize" };
        randomize.Click += (_, _) => Seed.Value = (decimal)(uint)Random.Shared.Next(1, int.MaxValue);
        var defaults = new Button { Name = "tgen_defaults", Content = "Defaults" };
        defaults.Click += (_, _) => Defaults(new TerrainGenSettings());
        var apply = new Button { Name = "tgen_apply", Content = "Apply", IsDefault = true, MinWidth = 80 };
        apply.Click += (_, _) =>
        {
            Apply();
            Applied = true;
            Close();
        };
        var cancel = new Button { Name = "tgen_cancel", Content = "Cancel", IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();

        Control Unavailable(params string[] labels) => new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = string.Join(", ", labels), FontSize = 11, Opacity = 0.55, TextWrapping = TextWrapping.Wrap, Width = 260 },
                new TextBlock { Text = "Not available in Glacier's generator.", FontSize = 11, Opacity = 0.55 },
            },
        };

        var left = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Group("Grid", Row("Vertices", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Nx, new TextBlock { Text = "x", VerticalAlignment = VerticalAlignment.Center }, Nz } }),
                    Row("Height Min", HeightMin), Row("Height Range", HeightRange)),
                Group("Noise", Row("Type", NoiseType), Row("Seed", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Seed, randomize } }),
                    Row("Feature (m)", Feature), Row("Octaves", Octaves), Row("Roughness", Roughness), Row("Lacunarity", Lacunarity), Row("Domain Warp", Warp)),
            },
        };
        var middle = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Group("Shaping", Row("Exponent", Exponent), Row("Terraces", Terraces), Row("Offset (%)", Offset), Row("Edge Falloff", Falloff), Row("Width (%)", FalloffWidth)),
                Group("Erosion", Row("Smoothing", Smooth), Unavailable("Thermal Passes", "Talus", "Droplets", "Strength")),
                Group("Splat Map", Unavailable("Generate splat map", "Rock Slope", "High Above", "Variation", "Ridges")),
            },
        };
        _height.IsCheckedChanged += (_, _) => Regenerate();
        var right = Group("Preview",
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _height, _shaded } },
            new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _preview },
            _stats);

        Content = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 8,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { left, middle, right } },
                new DockPanel
                {
                    Children =
                    {
                        defaults,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { apply, cancel } },
                    },
                },
            },
        };
        Regenerate();
    }

    public NumericUpDown Nx { get; }

    public NumericUpDown Nz { get; }

    public NumericUpDown HeightMin { get; }

    public NumericUpDown HeightRange { get; }

    public ComboBox NoiseType { get; }

    public NumericUpDown Seed { get; }

    public NumericUpDown Feature { get; }

    public NumericUpDown Octaves { get; }

    public NumericUpDown Roughness { get; }

    public NumericUpDown Lacunarity { get; }

    public NumericUpDown Warp { get; }

    public NumericUpDown Exponent { get; }

    public NumericUpDown Terraces { get; }

    public NumericUpDown Offset { get; }

    public ComboBox Falloff { get; }

    public NumericUpDown FalloffWidth { get; }

    public NumericUpDown Smooth { get; }

    /// <summary>True once Apply ran.</summary>
    public bool Applied { get; private set; }

    /// <summary>The heights (0..1) the preview shows, for the dialog's current settings.</summary>
    public double[] PreviewHeights { get; private set; } = Array.Empty<double>();

    /// <summary>The generator settings the fields describe.</summary>
    public TerrainGenSettings Settings => new()
    {
        Seed = (uint)(Seed.Value ?? 1),
        Type = (TerrainNoiseType)Math.Max(NoiseType.SelectedIndex, 0),
        FeatureSize = (float)(Feature.Value ?? 128),
        Octaves = (int)(Octaves.Value ?? 6),
        Roughness = (float)(Roughness.Value ?? 0.45m),
        Lacunarity = (float)(Lacunarity.Value ?? 2),
        Warp = (float)(Warp.Value ?? 0.25m),
        Exponent = (float)(Exponent.Value ?? 1),
        Terraces = (int)(Terraces.Value ?? 0),
        Offset = (float)(Offset.Value ?? 0) / 100f,
        Falloff = (TerrainEdgeFalloff)Math.Max(Falloff.SelectedIndex, 0),
        FalloffWidth = (float)(FalloffWidth.Value ?? 30) / 100f,
        SmoothPasses = (int)(Smooth.Value ?? 1),
    };

    /// <summary>Applies the generated heights as one undo step (resizing the grid first when the vertex counts changed).</summary>
    public void Apply()
    {
        TerrainGenSettings s = Settings;
        int nx = (int)(Nx.Value ?? _t.Nx), nz = (int)(Nz.Value ?? _t.Nz);
        float min = (float)(HeightMin.Value ?? 0), range = (float)(HeightRange.Value ?? 64);
        TerrainOps.Apply(_doc, _obj, "Generate terrain", t =>
        {
            if (nx != t.Nx || nz != t.Nz)
            {
                TerrainOps.Resize(t, nx, nz);
            }

            t.HeightMin = min;
            t.HeightRange = range;
            double[] h = TerrainGenerator.Generate(s, t.Nx, t.Nz, t.CellSize);
            int w = t.Nx;
            TerrainOps.SetNormalizedHeights(t, (x, z) => h[(z * w) + x]);
        });
    }

    public static async Task<bool> ShowAsync(Window owner, EditorDocument doc, LevelObject terrain)
    {
        var dlg = new TerrainGenerateDialog(doc, terrain);
        await dlg.ShowDialog(owner);
        return dlg.Applied;
    }

    private void Defaults(TerrainGenSettings d)
    {
        _loading = true;
        NoiseType.SelectedIndex = (int)d.Type;
        Seed.Value = d.Seed;
        Feature.Value = (decimal)d.FeatureSize;
        Octaves.Value = d.Octaves;
        Roughness.Value = (decimal)d.Roughness;
        Lacunarity.Value = (decimal)d.Lacunarity;
        Warp.Value = (decimal)d.Warp;
        Exponent.Value = (decimal)d.Exponent;
        Terraces.Value = d.Terraces;
        Offset.Value = (decimal)(d.Offset * 100);
        Falloff.SelectedIndex = (int)d.Falloff;
        FalloffWidth.Value = (decimal)(d.FalloffWidth * 100);
        Smooth.Value = d.SmoothPasses;
        _loading = false;
        Regenerate();
    }

    /// <summary>Recomputes the preview: the heights over the requested grid, as greyscale or hill-shaded, and statistics.</summary>
    private void Regenerate()
    {
        if (_loading)
        {
            return;
        }

        int nx = (int)(Nx.Value ?? _t.Nx), nz = (int)(Nz.Value ?? _t.Nz);
        double[] h = TerrainGenerator.Generate(Settings, nx, nz, _t.CellSize);
        PreviewHeights = h;
        bool shaded = _shaded.IsChecked == true;
        float range = (float)(HeightRange.Value ?? 64);
        var bmp = new WriteableBitmap(new PixelSize(PreviewSize, PreviewSize), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            var row = new byte[PreviewSize * 4];
            for (int py = 0; py < PreviewSize; py++)
            {
                // image row 0 is the terrain's +Z edge, as heightmap images lay it (terrain.cpp:990-1002)
                int z = Math.Clamp((int)((PreviewSize - 1 - py) * (nz - 1) / (double)(PreviewSize - 1)), 0, nz - 1);
                for (int px = 0; px < PreviewSize; px++)
                {
                    int x = Math.Clamp((int)(px * (nx - 1) / (double)(PreviewSize - 1)), 0, nx - 1);
                    double v = h[(z * nx) + x];
                    if (shaded)
                    {
                        double dx = (h[(z * nx) + Math.Min(x + 1, nx - 1)] - h[(z * nx) + Math.Max(x - 1, 0)]) * range / (2 * _t.CellSize);
                        double dz = (h[(Math.Min(z + 1, nz - 1) * nx) + x] - h[(Math.Max(z - 1, 0) * nx) + x]) * range / (2 * _t.CellSize);
                        double len = Math.Sqrt((dx * dx) + 1 + (dz * dz));
                        v = Math.Clamp(((-dx * 0.4) + 0.8 + (dz * 0.4)) / len, 0, 1);
                    }

                    byte b = (byte)Math.Round(v * 255);
                    row[(px * 4) + 0] = b;
                    row[(px * 4) + 1] = b;
                    row[(px * 4) + 2] = b;
                    row[(px * 4) + 3] = 255;
                }

                System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + (py * fb.RowBytes), row.Length);
            }
        }

        _preview.Source = bmp;
        double mean = h.Average();
        int flat = h.Count(v => v < 0.02);
        float min = (float)(HeightMin.Value ?? 0);
        _stats.Text = string.Create(CultureInfo.InvariantCulture,
            $"{nx} x {nz} vertices, {(nx - 1) * _t.CellSize:0.#} x {(nz - 1) * _t.CellSize:0.#} m\n" +
            $"Heights {min:0.##} to {min + range:0.##} m above the origin, mean {min + (mean * range):0.##} m\n" +
            $"{100.0 * flat / h.Length:0.#}% of the vertices within 2% of the lowest height");
    }

    private NumericUpDown Dec(string name, double value, double min, double max, double step, int decimals)
    {
        var n = new NumericUpDown
        {
            Name = name,
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = (decimal)step,
            Value = (decimal)Math.Clamp(value, min, max),
            FormatString = "0." + new string('#', decimals),
            MinWidth = 110,
        };
        n.ValueChanged += (_, _) => Regenerate();
        return n;
    }

    private NumericUpDown Int(string name, double value, double min, double max)
    {
        var n = new NumericUpDown { Name = name, Minimum = (decimal)min, Maximum = (decimal)max, Increment = 1, Value = (decimal)Math.Clamp(value, min, max), FormatString = "0", MinWidth = 110 };
        n.ValueChanged += (_, _) => Regenerate();
        return n;
    }

    private static Control Row(string label, Control editor)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private static Control Group(string title, params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.SemiBold });
        foreach (Control r in rows)
        {
            panel.Children.Add(r);
        }

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 160, 160, 160)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 4),
            Child = panel,
        };
    }
}
