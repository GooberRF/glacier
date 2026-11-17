using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Lighting;
using Ged.Core.Lighting.Alpine;

namespace Ged.App.Controls;

/// <summary>
/// Stay-open Lightmap Method picker (owner UX ask: the method menu must not close when you
/// toggle a modifier — set the base + several modifiers in one open). Hosted in a
/// <see cref="Flyout"/> from the Level menu: a base radio group (RED Classic / Bounced ×1 / ×2)
/// plus the modifier check boxes. Toggling any control updates the method and invokes
/// <c>onChange</c> WITHOUT dismissing the flyout (Avalonia flyouts light-dismiss only on
/// click-away / Esc, and a toggle inside never closes them) — so checkmarks update live while the
/// popover stays open. Avalonia 11 menus close on a leaf-item click by design (no stay-open API),
/// so the flyout is the clean home for this multi-toggle flow.
/// </summary>
public sealed class LightmapMethodPanel : UserControl
{
    private readonly Action<LightingMethod> _onChange;
    private readonly Dictionary<string, ToggleButton> _byId = new();
    private readonly List<Action<LightingMethod>> _sync = new();
    private LightingMethod _method;
    private bool _suppress;

    public LightmapMethodPanel(LightingMethod initial, Action<LightingMethod> onChange)
        : this(initial, onChange, null, null)
    {
    }

    /// <summary>
    /// The picker with the open level's lightmap pipeline (its Alpine level properties "Legacy lighting" and
    /// "Lightmap density", as Alpine RED's Level Properties dialog stores them): <paramref name="onPipeline"/> receives
    /// the chosen pipeline and stored density (0 = default, 1–128, 255 = Alpine charts off).
    /// </summary>
    public LightmapMethodPanel(
        LightingMethod initial, Action<LightingMethod> onChange, AlpineLightmapSettings? pipeline,
        Action<LightmapPipeline, int>? onPipeline)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(onChange);
        _method = initial.Clone();
        _onChange = onChange;
        _pipeline = pipeline?.Pipeline ?? LightmapPipeline.Alpine;
        _density = pipeline?.Density ?? 0;
        _density0 = _density;
        _onPipeline = onPipeline;
        Content = Build();
    }

    /// <summary>
    /// The density choices offered: stored value and label — the same list as Level Properties ▸ Lightmaps, since both
    /// edit the level's one stored density (plus the level's own value when it is a custom one).
    /// </summary>
    public IReadOnlyList<(int Value, string Label)> DensityChoices => LightmapDensityChoices.For(_density0);

    private readonly int _density0;

    private readonly Action<LightmapPipeline, int>? _onPipeline;
    private LightmapPipeline _pipeline;
    private int _density;
    private ComboBox? _densityBox;

    /// <summary>The panel's current pipeline choice.</summary>
    public LightmapPipeline Pipeline => _pipeline;

    /// <summary>The panel's current stored density choice.</summary>
    public int Density => _density;

    /// <summary>Picks a density by stored value as if the user chose it (test hook).</summary>
    public void ChooseDensity(int value)
    {
        int i = DensityChoices.ToList().FindIndex(c => c.Value == value);
        if (_densityBox is not null && i >= 0)
        {
            _densityBox.SelectedIndex = i;
        }
    }

    /// <summary>A copy of the panel's current method state.</summary>
    public LightingMethod Method => _method.Clone();

    /// <summary>True when the control with this id is checked (test / introspection hook).</summary>
    public bool IsChecked(string id) => _byId.TryGetValue(id, out ToggleButton? c) && c.IsChecked == true;

    /// <summary>Toggles a control by id as if the user clicked it (drives the same handler).</summary>
    public void Toggle(string id, bool on)
    {
        if (_byId.TryGetValue(id, out ToggleButton? c))
        {
            c.IsChecked = on;
        }
    }

    /// <summary>Reflects an externally-changed method onto the controls without firing onChange.</summary>
    public void SetMethod(LightingMethod m)
    {
        _method = m.Clone();
        foreach (Action<LightingMethod> s in _sync)
        {
            s(_method);
        }
    }

    private Control Build()
    {
        var root = new StackPanel { Spacing = 3, MinWidth = 250, Margin = new Thickness(10) };
        root.Children.Add(new TextBlock { Text = "Lightmap Method", FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "Applies on the next full bake.",
            FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        root.Children.Add(Radio("red", "RED Classic (stock)",
            m => m.Base == LightingBase.RedClassic,
            () => { _method.Base = LightingBase.RedClassic; _method.Bounces = 1; }));
        root.Children.Add(Radio("bounce1", "Bounced — 1 Bounce",
            m => m.Base == LightingBase.Bounced && m.EffectiveBounces == 1,
            () => { _method.Base = LightingBase.Bounced; _method.Bounces = 1; }));
        root.Children.Add(Radio("bounce2", "Bounced — 2 Bounces",
            m => m.Base == LightingBase.Bounced && m.EffectiveBounces == 2,
            () => { _method.Base = LightingBase.Bounced; _method.Bounces = 2; }));

        root.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        root.Children.Add(new TextBlock { Text = "Modifiers", FontSize = 11, Opacity = 0.7 });

        root.Children.Add(Check("ao", "Ambient Occlusion", m => m.AmbientOcclusion, (m, v) => m.AmbientOcclusion = v));
        root.Children.Add(Check("soft", "Soft Shadows", m => m.SoftShadows, (m, v) => m.SoftShadows = v));
        root.Children.Add(Check("hires", "High-Resolution Lightmaps (Legacy lighting)", m => m.HighResLightmaps, (m, v) => m.HighResLightmaps = v));
        root.Children.Add(Check("seam", "Seam Blend (cross-room)", m => m.SeamBlend, (m, v) => m.SeamBlend = v));
        root.Children.Add(Check("leak", "Corner Leak Fix", m => m.CornerLeakFix, (m, v) => m.CornerLeakFix = v));
        root.Children.Add(Check("gutters", "Smooth Gutter Normals", m => m.SmoothGutters, (m, v) => m.SmoothGutters = v));
        root.Children.Add(Check("movershadows", "Movers cast shadows", m => m.MoverShadows, (m, v) => m.MoverShadows = v));

        if (_onPipeline is not null)
        {
            AddPipeline(root);
        }

        return root;
    }

    // The level's lightmap pipeline: Alpine RED's "Legacy lighting" and "Lightmap density" level properties
    // (editor_patch/level.cpp:2340, 2421). Stored on the level, so every bake of it — editor, script, command line —
    // uses the same choice.
    private void AddPipeline(StackPanel root)
    {
        root.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        root.Children.Add(new TextBlock { Text = "Lightmap Pipeline (this level)", FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "Alpine also bakes Alpine Faction's sharper Direct3D 11 lightmaps; Legacy bakes the stock lightmaps only.",
            FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 250, Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(PipelineRadio("alpine", "Alpine", LightmapPipeline.Alpine));
        root.Children.Add(PipelineRadio("legacy", "Legacy (stock lightmaps only)", LightmapPipeline.Legacy));
        root.Children.Add(new TextBlock
        {
            Text = "The same settings as Level Properties ▸ Compatibility (Legacy lighting) and ▸ Lightmaps (Lightmap density).",
            FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 250,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Lightmap density", VerticalAlignment = VerticalAlignment.Center });
        _densityBox = new ComboBox { ItemsSource = DensityChoices.Select(c => c.Label).ToList(), MinWidth = 150 };
        int at = DensityChoices.ToList().FindIndex(c => c.Value == _density);
        _densityBox.SelectedIndex = at >= 0 ? at : 0;
        _densityBox.SelectionChanged += (_, _) =>
        {
            if (_suppress || _densityBox.SelectedIndex < 0)
            {
                return;
            }

            _density = DensityChoices[_densityBox.SelectedIndex].Value;
            _onPipeline!(_pipeline, _density);
        };
        row.Children.Add(_densityBox);
        root.Children.Add(row);
    }

    private RadioButton PipelineRadio(string id, string text, LightmapPipeline value)
    {
        var rb = new RadioButton { Content = text, GroupName = "lm_pipeline", IsChecked = _pipeline == value };
        rb.IsCheckedChanged += (_, _) =>
        {
            if (_suppress || rb.IsChecked != true || _pipeline == value)
            {
                return;
            }

            _pipeline = value;
            _onPipeline!(_pipeline, _density);
        };
        _byId[id] = rb;
        return rb;
    }

    private RadioButton Radio(string id, string text, Func<LightingMethod, bool> get, Action apply)
    {
        var rb = new RadioButton { Content = text, GroupName = "lm_base", IsChecked = get(_method) };
        rb.IsCheckedChanged += (_, _) =>
        {
            if (_suppress || rb.IsChecked != true)
            {
                return;
            }

            apply();
            _onChange(_method.Clone());
        };

        _byId[id] = rb;
        _sync.Add(m => { _suppress = true; rb.IsChecked = get(m); _suppress = false; });
        return rb;
    }

    private CheckBox Check(string id, string text, Func<LightingMethod, bool> get, Action<LightingMethod, bool> set)
    {
        var cb = new CheckBox { Content = text, IsChecked = get(_method) };
        cb.IsCheckedChanged += (_, _) =>
        {
            if (_suppress)
            {
                return;
            }

            set(_method, cb.IsChecked == true);
            _onChange(_method.Clone());
        };

        _byId[id] = cb;
        _sync.Add(m => { _suppress = true; cb.IsChecked = get(m); _suppress = false; });
        return cb;
    }
}
