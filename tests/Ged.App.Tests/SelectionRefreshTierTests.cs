using System.Linq;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 8 (performance): a selection change must be a lightweight OVERLAY update, never a
/// full scene re-emission. These lock in the tier split: (1) EditorSession.BuildScene no
/// longer depends on the selection (the selected object's range/region moved out of the
/// compiled scene), and (2) the selected object's range/region is drawn by the lightweight
/// BuildSelectionRangeLines overlay instead. On ctf07 this took a selection change from
/// ~13-18 ms (full BuildScene + GPU re-upload) down to ~0.12 ms.
/// </summary>
public sealed class SelectionRefreshTierTests
{
    private static (EditorSession Session, EditorDocument Doc) NewSessionWithLight(out LevelObject light)
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        light = doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 0, 0))!;
        ((Light)light.Model).Range = 5f;
        return (session, doc);
    }

    [AvaloniaFact]
    public void BuildScene_Does_Not_Depend_On_The_Selection()
    {
        (EditorSession session, _) = NewSessionWithLight(out LevelObject light);

        int unselected = session.BuildScene().Lines.Count;
        session.Selection.SelectObject(light); // Object mode default chips → permitted
        int selected = session.BuildScene().Lines.Count;

        // Selecting no longer bakes the light's range sphere into the compiled scene, so the
        // scene is byte-identical — a selection change needs no BuildScene at all.
        Assert.Equal(unselected, selected);
    }

    [AvaloniaFact]
    public void Selected_Light_Range_Is_Drawn_By_The_Lightweight_Overlay()
    {
        (EditorSession session, EditorDocument doc) = NewSessionWithLight(out LevelObject light);

        // The selected light contributes a range sphere to the selection overlay...
        Assert.NotEmpty(session.BuildSelectionRangeLines(new[] { light }));

        // ...while a non-range object (an item) contributes nothing.
        LevelObject item = doc.PlaceObject(LevelObjectKind.Item, new Vec3(10, 0, 0))!;
        Assert.Empty(session.BuildSelectionRangeLines(new[] { item }));
    }

    [AvaloniaFact]
    public void Show_All_Ranges_Keeps_Ranges_In_The_Scene_Not_The_Overlay()
    {
        (EditorSession session, _) = NewSessionWithLight(out LevelObject light);
        session.ShowAllRanges = true;

        // With "Show all ranges" on, the scene draws every range (selection-independent), so
        // the selection overlay must NOT also draw it (no double-draw).
        Assert.Empty(session.BuildSelectionRangeLines(new[] { light }));
    }
}
