using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ged.App.Controls;
using Ged.Core.Lighting;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Owner UX gate for the stay-open Lightmap Method picker: an author must be able to set the base
/// method and several modifiers in ONE open of the popover, with the checkmarks updating live and
/// the flyout never dismissing on a toggle (the reason it is a <see cref="LightmapMethodPanel"/>
/// flyout, not an Avalonia submenu — which closes on any leaf click). These headless tests drive
/// the real panel controls and assert the flyout stays open across multiple toggles.
/// </summary>
public sealed class LightmapMethodPanelTests
{
    [AvaloniaFact]
    public void Toggling_The_Base_And_Several_Modifiers_Keeps_The_Flyout_Open()
    {
        var committed = new List<LightingMethod>();
        var panel = new LightmapMethodPanel(new LightingMethod(), m => committed.Add(m));

        var button = new Button { Content = "Lightmap Method" };
        var window = new Window { Content = button };
        window.Show();
        window.UpdateLayout();

        var flyout = new Flyout { Content = panel };
        flyout.ShowAt(button);
        Assert.True(flyout.IsOpen);

        // Set the base to Bounced ×2, then toggle three modifiers — all without reopening.
        panel.Toggle("bounce2", true);
        panel.Toggle("leak", true);
        panel.Toggle("gutters", true);
        panel.Toggle("ao", true);

        // The flyout must STILL be open: a toggle never dismisses the picker.
        Assert.True(flyout.IsOpen, "the picker must stay open across multiple toggles");

        // Live checkmarks reflect every selection.
        Assert.True(panel.IsChecked("bounce2"));
        Assert.True(panel.IsChecked("leak"));
        Assert.True(panel.IsChecked("gutters"));
        Assert.True(panel.IsChecked("ao"));
        Assert.False(panel.IsChecked("red"));

        // The last committed method composes every selection (base + all three modifiers).
        LightingMethod final = committed[^1];
        Assert.Equal(LightingBase.Bounced, final.Base);
        Assert.Equal(2, final.EffectiveBounces);
        Assert.True(final.CornerLeakFix);
        Assert.True(final.SmoothGutters);
        Assert.True(final.AmbientOcclusion);

        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void Base_Radio_Is_Mutually_Exclusive_And_Commits_Each_Choice()
    {
        var committed = new List<LightingMethod>();
        var panel = new LightmapMethodPanel(new LightingMethod(), m => committed.Add(m));
        var window = new Window { Content = panel };
        window.Show();
        window.UpdateLayout();

        panel.Toggle("bounce1", true);
        Assert.True(panel.IsChecked("bounce1"));
        Assert.False(panel.IsChecked("red"));
        Assert.Equal(LightingBase.Bounced, committed[^1].Base);
        Assert.Equal(1, committed[^1].EffectiveBounces);

        panel.Toggle("red", true);
        Assert.True(panel.IsChecked("red"));
        Assert.False(panel.IsChecked("bounce1"));
        Assert.Equal(LightingBase.RedClassic, committed[^1].Base);

        window.Close();
    }

    [AvaloniaFact]
    public void SetMethod_Refreshes_The_Controls_Without_Committing()
    {
        var committed = new List<LightingMethod>();
        var panel = new LightmapMethodPanel(new LightingMethod(), m => committed.Add(m));
        var window = new Window { Content = panel };
        window.Show();
        window.UpdateLayout();

        panel.SetMethod(new LightingMethod
        {
            Base = LightingBase.Bounced, Bounces = 2, CornerLeakFix = true, SmoothGutters = true, SeamBlend = true,
        });

        Assert.True(panel.IsChecked("bounce2"));
        Assert.True(panel.IsChecked("leak"));
        Assert.True(panel.IsChecked("gutters"));
        Assert.True(panel.IsChecked("seam"));
        Assert.Empty(committed); // a silent external refresh, not a user edit

        window.Close();
    }
}
