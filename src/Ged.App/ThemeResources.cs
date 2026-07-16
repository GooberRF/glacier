using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Dock.Avalonia.Controls;

namespace Ged.App;

/// <summary>
/// App-level theme corrections layered over Fluent + Dock.Avalonia (items 1 &amp; 2).
///
/// <para>Root cause both items share: Dock.Avalonia's Fluent accent theme declares its
/// chrome brushes — <c>DockThemeForegroundBrush</c>, <c>DockThemeControlBackgroundBrush</c>,
/// <c>DockThemeBorderLowBrush</c> — as single shared <see cref="SolidColorBrush"/> instances
/// whose <c>Color</c> is itself a nested <c>{DynamicResource …}</c> (SystemBaseHighColor,
/// SystemAltHighColor, SystemBaseMediumLowColor). That inner indirection is resolved ONCE, the
/// first time the brush is used, and never re-resolves when <see cref="StyledElement.ActualThemeVariant"/>
/// changes. So after a runtime Light↔Dark switch the whole dock chrome keeps the previous
/// variant's colours until the app is restarted — and because the unselected tab label uses
/// <c>DockThemeForegroundBrush</c>, switching from Dock's dark default into light leaves the
/// labels white-on-light (invisible) until they are hovered.</para>
///
/// <para>Fix: re-declare those keys here as plain, per-variant brushes inside
/// <see cref="ResourceDictionary.ThemeDictionaries"/> on <see cref="Application.Resources"/>.
/// Application resources shadow the theme's, and a per-variant dictionary entry is swapped
/// wholesale on a variant change (verified live in both directions) — unlike Dock's stuck
/// nested-DynamicResource brush. The values match each theme's correct colour, so steady-state
/// appearance is unchanged in BOTH themes; only the live switch is repaired.</para>
///
/// <para>Item 1 additionally routes the unselected tab foreground to a dedicated
/// <c>GedUnselectedTabForeground</c>: the theme's secondary-text tone in light (legible on the
/// light strip) and the unchanged bright foreground in dark (dark was already perfect).</para>
/// </summary>
internal static class ThemeResources
{
    internal const string DockForegroundKey = "DockThemeForegroundBrush";
    internal const string DockControlBackgroundKey = "DockThemeControlBackgroundBrush";
    internal const string DockBorderLowKey = "DockThemeBorderLowBrush";

    /// <summary>The unselected tab-label foreground (item 1). Secondary tone in light, bright in dark.</summary>
    internal const string UnselectedTabForegroundKey = "GedUnselectedTabForeground";

    /// <summary>Installs the theme corrections onto an application (also called from tests).</summary>
    public static void Install(Application app)
    {
        var overrides = new ResourceDictionary();
        overrides.ThemeDictionaries[ThemeVariant.Light] = Variant(
            foreground: Color.FromRgb(0x00, 0x00, 0x00),          // SystemBaseHighColor (light)
            controlBackground: Color.FromRgb(0xFF, 0xFF, 0xFF),   // SystemAltHighColor (light)
            borderLow: Color.FromArgb(0x66, 0x00, 0x00, 0x00),    // SystemBaseMediumLowColor (light)
            unselectedTab: Color.FromArgb(0x9E, 0x00, 0x00, 0x00)); // Fluent secondary text (light) — item 1
        overrides.ThemeDictionaries[ThemeVariant.Dark] = Variant(
            foreground: Color.FromRgb(0xFF, 0xFF, 0xFF),          // SystemBaseHighColor (dark)
            controlBackground: Color.FromRgb(0x00, 0x00, 0x00),   // SystemAltHighColor (dark)
            borderLow: Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),    // SystemBaseMediumLowColor (dark)
            unselectedTab: Color.FromRgb(0xFF, 0xFF, 0xFF));      // unchanged from today's dark look
        app.Resources.MergedDictionaries.Add(overrides);

        // Item 1: point the unselected (and un-hovered, so Dock's hover/selected accent still wins)
        // tab labels at the theme-aware secondary foreground above.
        app.Styles.Add(UnselectedTabStyle<ToolTabStripItem>());
        app.Styles.Add(UnselectedTabStyle<DocumentTabStripItem>());
    }

    private static ResourceDictionary Variant(Color foreground, Color controlBackground, Color borderLow, Color unselectedTab) => new()
    {
        { DockForegroundKey, new SolidColorBrush(foreground) },
        { DockControlBackgroundKey, new SolidColorBrush(controlBackground) },
        { DockBorderLowKey, new SolidColorBrush(borderLow) },
        { UnselectedTabForegroundKey, new SolidColorBrush(unselectedTab) },
    };

    private static Style UnselectedTabStyle<T>()
        where T : TemplatedControl
    {
        var style = new Style(x => x.OfType<T>().Not(y => y.Class(":selected")).Not(y => y.Class(":pointerover")));
        style.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension(UnselectedTabForegroundKey)));
        return style;
    }
}
