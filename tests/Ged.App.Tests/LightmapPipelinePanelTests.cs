using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ged.App.Controls;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Lighting.Alpine;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The level's lightmap pipeline in the Lightmap Method picker (ALPINE-COMPAT-SPEC C6): Alpine RED's "Legacy lighting"
/// and "Lightmap density" level properties, chosen in the same stay-open flyout and stored as one undoable edit.
/// </summary>
public sealed class LightmapPipelinePanelTests
{
    [AvaloniaFact]
    public void The_Pipeline_And_Density_Choices_Reach_The_Callback_Without_Closing_The_Flyout()
    {
        var chosen = new List<(LightmapPipeline, int)>();
        var panel = new LightmapMethodPanel(new LightingMethod(), _ => { }, new AlpineLightmapSettings(), (p, d) => chosen.Add((p, d)));
        var button = new Button { Content = "Lightmap Method" };
        var window = new Window { Content = button };
        window.Show();
        var flyout = new Flyout { Content = panel };
        flyout.ShowAt(button);

        Assert.True(panel.IsChecked("alpine"));
        panel.Toggle("legacy", true);
        panel.ChooseDensity(16);
        panel.Toggle("alpine", true);
        Assert.True(flyout.IsOpen);
        Assert.Equal(new[] { (LightmapPipeline.Legacy, 0), (LightmapPipeline.Legacy, 16), (LightmapPipeline.Alpine, 16) }, chosen);
    }

    [Fact]
    public void Storing_The_Pipeline_Is_One_Undoable_Edit()
    {
        EditorDocument doc = LevelTemplates.CreateNew(new System.DateTime(2026, 1, 1));
        Assert.Equal(LightmapPipeline.Alpine, AlpineLightmapSettings.FromLevel(doc.Rfl).Pipeline);
        int nodes = doc.Undo.NodeCount;

        Assert.True(AlpineLightmapSettings.Apply(doc, LightmapPipeline.Legacy, 32));
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
        AlpineLightmapSettings s = AlpineLightmapSettings.FromLevel(doc.Rfl);
        Assert.Equal(LightmapPipeline.Legacy, s.Pipeline);
        Assert.Equal(32, s.Density);
        Assert.False(s.ChartsEnabled);
        Assert.False(AlpineLightmapSettings.Apply(doc, LightmapPipeline.Legacy, 32)); // no change, no undo node

        doc.Undo.Undo();
        Assert.Equal(LightmapPipeline.Alpine, AlpineLightmapSettings.FromLevel(doc.Rfl).Pipeline);
        Assert.Equal(0, AlpineLightmapSettings.FromLevel(doc.Rfl).Density);
        Assert.Single(doc.Rfl.Sections, x => x.Content is AlpineLevelPropertiesSection);
    }
}
