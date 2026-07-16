using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ged.App;
using Ged.App.Dialogs;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 3: the level_info DATE string is set automatically on save and must not be
/// user-editable in Level Properties. The dialog shows it in a read-only, disabled
/// field (never wired to an editable setter), so the panel cannot change it.
/// </summary>
public sealed class LevelPropertiesDateTests
{
    [AvaloniaFact]
    public void Date_Field_Is_Read_Only_In_Level_Properties()
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        doc.Rfl.ParseAllKnownSections();
        LevelInfoSection info = doc.Rfl.Sections.Select(s => s.Content).OfType<LevelInfoSection>().Single();
        string date = info.Date;
        Assert.False(string.IsNullOrEmpty(date), "New Level must stamp a date");

        var dlg = new LevelPropertiesDialog(doc);
        dlg.Show();
        dlg.UpdateLayout();

        TextBox[] boxes = dlg.GetVisualDescendants().OfType<TextBox>().ToArray();

        // The date is shown in a read-only, disabled field ...
        TextBox? dateBox = boxes.FirstOrDefault(b => b.Text == date);
        Assert.NotNull(dateBox);
        Assert.True(dateBox!.IsReadOnly, "the Date field must be read-only");
        Assert.False(dateBox.IsEnabled, "the Date field must be disabled (not editable)");

        // ... and no EDITABLE text box carries the date (it isn't wired to a setter).
        Assert.DoesNotContain(boxes, b => b.Text == date && !b.IsReadOnly && b.IsEnabled);

        dlg.Close();
    }
}
