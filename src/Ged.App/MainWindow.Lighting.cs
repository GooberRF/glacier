using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Ged.App.Controls;
using Ged.Core.Lighting;

namespace Ged.App;

/// <summary>
/// Feature 1: the Level ▸ Lightmap Method picker. Owner UX ask — the method chooser must stay
/// open while you toggle several modifiers in one go, so it is a <see cref="LightmapMethodPanel"/>
/// hosted in a <see cref="Flyout"/> off the Level menu rather than a submenu of leaf items (an
/// Avalonia menu closes on any leaf click, which would force a reopen per toggle). The base radio
/// (RED Classic / Bounced ×1 / ×2) and the modifier check boxes update live while the popover stays
/// open; the picked method persists per-level in the .gedlayout.json sidecar and as a global
/// default in settings, and real bakes use it while the Preview Lighting path stays on RED Classic.
/// </summary>
public sealed partial class MainWindow
{
    private LightmapMethodPanel? _lightingMethodPanel;

    /// <summary>The global-default method from settings (used when a level has no per-level override).</summary>
    private LightingMethod GlobalDefaultMethod() => new()
    {
        Base = _settings.LightingMethodBase == (int)LightingBase.Bounced ? LightingBase.Bounced : LightingBase.RedClassic,
        Bounces = _settings.LightingMethodBounces >= 2 ? 2 : 1,
        AmbientOcclusion = _settings.LightingAmbientOcclusion,
        SoftShadows = _settings.LightingSoftShadows,
        HighResLightmaps = _settings.LightingHighRes,
        SeamBlend = _settings.LightingSeamBlend,
        CornerLeakFix = _settings.LightingCornerLeakFix,
        SmoothGutters = _settings.LightingSmoothGutters,
    };

    /// <summary>The effective method for the open level: its sidecar override, else the global default.</summary>
    private LightingMethod EffectiveLightingMethod() => (_levelLightingMethod ?? GlobalDefaultMethod()).Clone();

    /// <summary>
    /// The Level-menu entry that opens the stay-open method picker. Clicking it dismisses the menu
    /// and shows the <see cref="LightmapMethodPanel"/> flyout anchored to the Level menu header
    /// (persistent on the bar), so the popover stays put while the author sets base + modifiers.
    /// </summary>
    private MenuItem BuildLightmapMethodMenuItem(Control anchor)
    {
        var item = new MenuItem { Header = "Lightmap Method…" };
        item.Click += (_, _) => ShowLightmapMethodFlyout(anchor);
        return item;
    }

    /// <summary>Builds (or rebuilds) the picker panel and shows it in a flyout anchored to <paramref name="anchor"/>.</summary>
    private void ShowLightmapMethodFlyout(Control anchor)
    {
        _lightingMethodPanel = new LightmapMethodPanel(EffectiveLightingMethod(), CommitLightingMethod);
        var flyout = new Flyout
        {
            Content = _lightingMethodPanel,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };
        flyout.Closed += (_, _) => _lightingMethodPanel = null;
        flyout.ShowAt(anchor);
    }

    /// <summary>Applies a chosen method as the per-level override AND the new global default, persisting both.</summary>
    private void CommitLightingMethod(LightingMethod m)
    {
        _levelLightingMethod = m;
        _settings.LightingMethodBase = (int)m.Base;
        _settings.LightingMethodBounces = m.Bounces;
        _settings.LightingAmbientOcclusion = m.AmbientOcclusion;
        _settings.LightingSoftShadows = m.SoftShadows;
        _settings.LightingHighRes = m.HighResLightmaps;
        _settings.LightingSeamBlend = m.SeamBlend;
        _settings.LightingCornerLeakFix = m.CornerLeakFix;
        _settings.LightingSmoothGutters = m.SmoothGutters;
        Persist();
        SyncLightingMethodToController();
        SaveAnnotationSidecarAndLighting();
        _dispatcher.ShowMessage($"Lightmap method: {m.DisplayName()} (applies on the next full bake).");
    }

    /// <summary>Pushes the effective method into the build controller so real bakes use it.</summary>
    private void SyncLightingMethodToController()
    {
        if (_buildController is { } bc)
        {
            bc.Method = EffectiveLightingMethod();
        }
    }

    /// <summary>Saves both editor-only sidecar blocks (annotations + lighting method) for the open level.</summary>
    private void SaveAnnotationSidecarAndLighting()
    {
        if (Document?.Path is string p)
        {
            SaveSidecarFor(p);
        }
    }

    // Called from ApplyLoadedLightingMethod (MainWindow.Annotations.cs) after a sidecar load.
    partial void OnLightingMethodLoaded()
    {
        SyncLightingMethodToController();
        _lightingMethodPanel?.SetMethod(EffectiveLightingMethod()); // refresh an open picker
    }
}
