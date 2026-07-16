using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ged.App.Dialogs;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The "About Glacier" box shows the app logo in the top-right corner with the
/// surrounding text word-wrapped so nothing clips, and its credits reference the
/// community reverse-engineering projects (including rf-reversed). It also must not
/// claim to ship game assets.
/// </summary>
public sealed class AboutDialogTests
{
    private static string AllText(AboutDialog dlg) =>
        string.Join("\n", dlg.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty));

    [AvaloniaFact]
    public void Credits_Reference_Rf_Reversed_And_Existing_Projects()
    {
        var dlg = new AboutDialog();
        dlg.Show();
        dlg.UpdateLayout();

        string text = AllText(dlg);
        // The credit must name RF Reversed (user's preferred casing) alongside the
        // projects it already listed.
        Assert.Contains("RF Reversed", text);
        Assert.Contains("REDUX", text);
        Assert.Contains("Alpine Faction", text);

        // The license credit line is neutral: copyright + a pointer to LICENSE, with
        // no license characterization and no MIT claim.
        Assert.Contains("See LICENSE and licensing-info.txt", text);
        Assert.DoesNotContain("MIT", text);

        dlg.Close();
    }

    [AvaloniaFact]
    public void Does_Not_Claim_To_Ship_Game_Assets()
    {
        var dlg = new AboutDialog();
        dlg.Show();
        dlg.UpdateLayout();

        // The "ships no game assets ..." sentence was removed; the Volition/THQ copyright
        // note stays so the surrounding credits remain coherent.
        string text = AllText(dlg);
        Assert.DoesNotContain("ships no game assets", text);
        Assert.Contains("Volition", text);

        dlg.Close();
    }

    [AvaloniaFact]
    public void Logo_Is_Shown_Top_Right_With_Wrapped_Text()
    {
        var dlg = new AboutDialog();
        dlg.Show();
        dlg.UpdateLayout();

        // The logo lives in the right (Auto) column of the header grid, pinned top-right.
        Image logo = dlg.GetVisualDescendants().OfType<Image>().Single();
        Assert.Equal(1, Grid.GetColumn(logo));
        Assert.Equal(HorizontalAlignment.Right, logo.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Top, logo.VerticalAlignment);
        Assert.True(logo.Bounds.Width > 0 && logo.Bounds.Height > 0, "logo should be laid out with a size");

        // The header text column (*) wraps so it fits beside the logo without clipping.
        Assert.Contains(dlg.GetVisualDescendants().OfType<TextBlock>(),
            t => t.TextWrapping == TextWrapping.Wrap);

        dlg.Close();
    }
}
