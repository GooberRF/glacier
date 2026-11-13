using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ged.App;
using Ged.App.Dialogs;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Tables;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The Level Properties dialog renders from the Core level-property schemas and writes through the
/// shared <see cref="LevelPropertyAccess"/> (the same path as <c>level.props</c> / <c>level.alpine</c>):
/// one row per schema field with the dialog's labels, edits land in the right section as one undo
/// step each, and undoing the first Alpine edit removes the section it created again.
/// </summary>
public sealed class LevelPropertiesSchemaDialogTests
{
    private static (EditorDocument Doc, LevelPropertiesDialog Dialog) Open(bool withoutAlpineProps = false)
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        if (withoutAlpineProps)
        {
            // A level without the chunk (a stock level; a new level carries Alpine RED's new-level chunk).
            doc.Rfl.Sections.RemoveAll(s => s.TypeId == (uint)Ged.Core.IO.Rfl.SectionType.AlpineLevelProperties);
        }

        var dlg = new LevelPropertiesDialog(doc);
        dlg.Show();
        dlg.UpdateLayout();
        return (doc, dlg);
    }

    private static bool HasRow(LevelPropertiesDialog dlg, string label) =>
        dlg.GetVisualDescendants().OfType<CheckBox>().Any(c => c.Content as string == label) ||
        dlg.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label || t.Text == label + " (R G B)");

    [AvaloniaFact]
    public void Dialog_Builds_One_Row_Per_Schema_Field()
    {
        (_, LevelPropertiesDialog dlg) = Open();
        var fields = LevelPropertiesSchema.Fields.Concat(AlpineLevelPropertiesSchema.Fields)
            .Where(f => f.Target != LevelPropertyTarget.Header)
            .ToList();
        foreach (LevelPropertyField f in fields)
        {
            Assert.True(HasRow(dlg, f.Field.Label), $"no row for '{f.Field.Label}'");
        }

        // Exactly one control per field: checkboxes for flags, labelled editors for the rest.
        int checks = dlg.GetVisualDescendants().OfType<CheckBox>().Count();
        Assert.Equal(fields.Count(f => f.Field.Editor == InspectorEditor.Bool), checks);
        int numbers = dlg.GetVisualDescendants().OfType<NumericUpDown>().Count();
        Assert.Equal(fields.Count(f => f.Field.Editor is InspectorEditor.Int or InspectorEditor.Float), numbers);

        // The RFL version stays a dialog note, not an editable row.
        Assert.Contains(dlg.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("RFL version:", System.StringComparison.Ordinal) == true);
        dlg.Close();
    }

    [AvaloniaFact]
    public void Dialog_Edits_Write_Through_The_Shared_Accessor()
    {
        (EditorDocument doc, LevelPropertiesDialog dlg) = Open(withoutAlpineProps: true);
        LevelInfoSection info = doc.Rfl.Sections.Select(s => s.Content).OfType<LevelInfoSection>().Single();
        Assert.DoesNotContain(doc.Rfl.Sections, s => s.Content is AlpineLevelPropertiesSection);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int nodes = doc.Undo.NodeCount;

        CheckBox mp = dlg.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Multiplayer Level");
        mp.IsChecked = !(mp.IsChecked ?? false);
        Assert.Equal(mp.IsChecked == true ? (byte)1 : (byte)0, info.MultiplayerLevel);
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);

        CheckBox headlamp = dlg.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Player Starts With Headlamp");
        Assert.True(headlamp.IsChecked); // without the chunk the game gives the player a headlamp
        headlamp.IsChecked = false;
        AlpineLevelPropertiesSection alp = doc.Rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
        Assert.Equal((byte)0, alp.StartsWithHeadlamp);
        Assert.Equal((byte)1, alp.LegacyMovers); // the created chunk keeps the stock (existing-level) values
        Assert.Equal(6u, alp.Version); // an edit writes the chunk version Alpine RED writes
        Assert.Equal("Headlamp", doc.Undo.Current.Description);

        // Undo the Alpine edit: the section it created goes away again; undo the flag: bytes identical.
        doc.Undo.Undo();
        Assert.DoesNotContain(doc.Rfl.Sections, s => s.Content is AlpineLevelPropertiesSection);
        doc.Undo.Undo();
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));
        dlg.Close();
    }

    [AvaloniaFact]
    public void A_New_Level_Shows_Alpine_Reds_New_Level_Settings_And_Edits_Its_Chunk()
    {
        (EditorDocument doc, LevelPropertiesDialog dlg) = Open();
        AlpineLevelPropertiesSection alp = doc.Rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);

        CheckBox movers = dlg.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Legacy Movers");
        Assert.False(movers.IsChecked);
        movers.IsChecked = true;
        Assert.Equal((byte)1, alp.LegacyMovers);
        doc.Undo.Undo();
        Assert.Equal((byte)0, alp.LegacyMovers);
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));
        dlg.Close();
    }
}
