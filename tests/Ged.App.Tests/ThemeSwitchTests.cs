using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Dock.Avalonia.Themes;
using Ged.App;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Items 1 &amp; 2 regression coverage. Dock.Avalonia's Fluent accent theme declares its chrome
/// brushes as single shared <see cref="SolidColorBrush"/> instances whose Color is a nested
/// <c>{DynamicResource}</c>; that indirection never re-resolves on a variant change, so a runtime
/// Light↔Dark switch leaves the dock chrome (unselected tab labels especially) stuck on the
/// previous variant until restart. <see cref="ThemeResources"/> re-declares those keys as plain
/// per-variant brushes so the switch applies live, and routes unselected tab labels to a legible
/// secondary tone in light theme (dark left unchanged).
/// </summary>
public sealed class ThemeSwitchTests
{
    private static void EnsureThemeInstalled()
    {
        Application app = Application.Current!;
        if (app.TryGetResource(ThemeResources.UnselectedTabForegroundKey, ThemeVariant.Light, out _))
        {
            return; // already installed on the shared test application
        }

        app.Styles.Add(new DockFluentTheme());
        ThemeResources.Install(app);
    }

    private static Color Col(IBrush? b) => b is ISolidColorBrush s ? s.Color : Colors.Transparent;

    // WCAG relative luminance of a colour composited over an opaque background.
    private static double Luminance(Color c, Color bg)
    {
        double a = c.A / 255.0;
        double R = (c.R * a + bg.R * (1 - a)) / 255.0;
        double G = (c.G * a + bg.G * (1 - a)) / 255.0;
        double B = (c.B * a + bg.B * (1 - a)) / 255.0;
        static double Lin(double v) => v <= 0.03928 ? v / 12.92 : System.Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
    }

    private static double Contrast(Color fg, Color bg)
    {
        double l1 = Luminance(fg, bg);
        double l2 = Luminance(bg, bg);
        double hi = System.Math.Max(l1, l2);
        double lo = System.Math.Min(l1, l2);
        return (hi + 0.05) / (lo + 0.05);
    }

    [AvaloniaFact]
    public void Light_Unselected_Tab_Foreground_Is_Legible_And_Dark_Unchanged()
    {
        EnsureThemeInstalled();
        Application app = Application.Current!;

        Assert.True(app.TryGetResource(ThemeResources.UnselectedTabForegroundKey, ThemeVariant.Light, out object? lightObj));
        Assert.True(app.TryGetResource(ThemeResources.UnselectedTabForegroundKey, ThemeVariant.Dark, out object? darkObj));

        Color light = Col(lightObj as IBrush);
        Color dark = Col(darkObj as IBrush);

        // Item 1: clearly legible on the light tab strip (near-white). WCAG AA body text is ≥ 4.5.
        double contrast = Contrast(light, Colors.White);
        Assert.True(contrast >= 4.5, $"unselected tab foreground contrast too low in light theme: {contrast:0.0}");

        // Dark theme unchanged: unselected labels stay the bright base foreground (white).
        Assert.True(dark.R > 0xF0 && dark.G > 0xF0 && dark.B > 0xF0, $"dark unselected foreground changed: {dark}");
    }

    [AvaloniaFact]
    public void Dock_Foreground_Switches_Live_Both_Directions()
    {
        EnsureThemeInstalled();

        // A control painted with Dock's foreground brush, exactly as an unselected tab label is.
        var tb = new TextBlock { Text = "tab" };
        tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(ThemeResources.DockForegroundKey));
        var win = new Window { Content = tb, RequestedThemeVariant = ThemeVariant.Light };
        win.Show();
        win.UpdateLayout();
        Color light1 = Col(tb.Foreground);

        win.RequestedThemeVariant = ThemeVariant.Dark;
        win.UpdateLayout();
        Color dark = Col(tb.Foreground);

        win.RequestedThemeVariant = ThemeVariant.Light;
        win.UpdateLayout();
        Color light2 = Col(tb.Foreground);

        win.Close();

        // Without the fix all three read Black (the stuck brush). With it, the foreground tracks
        // the variant live in BOTH directions.
        Assert.Equal(Colors.Black, light1);
        Assert.Equal(Colors.White, dark);
        Assert.Equal(Colors.Black, light2);
    }
}
