using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Lighting;

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
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(onChange);
        _method = initial.Clone();
        _onChange = onChange;
        Content = Build();
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
        root.Children.Add(Check("hires", "High-Resolution Lightmaps", m => m.HighResLightmaps, (m, v) => m.HighResLightmaps = v));
        root.Children.Add(Check("seam", "Seam Blend (cross-room)", m => m.SeamBlend, (m, v) => m.SeamBlend = v));
        root.Children.Add(Check("leak", "Corner Leak Fix", m => m.CornerLeakFix, (m, v) => m.CornerLeakFix = v));
        root.Children.Add(Check("gutters", "Smooth Gutter Normals", m => m.SmoothGutters, (m, v) => m.SmoothGutters = v));
        root.Children.Add(Check("movershadows", "Movers cast shadows", m => m.MoverShadows, (m, v) => m.MoverShadows = v));

        return root;
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
