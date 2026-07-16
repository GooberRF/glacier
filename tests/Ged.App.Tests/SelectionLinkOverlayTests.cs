using System.Linq;
using Ged.App;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Feature 3 (selected-object links always visible): EditorSession.BuildSelectionLinkLines
/// emits, into the lightweight selection overlay, every link whose source OR destination is
/// selected — independent of the global Show Links toggle — and nothing when the selection
/// is empty. Endpoints resolve for ordinary objects, movers and keyframes alike.
/// </summary>
public sealed class SelectionLinkOverlayTests
{
    private static EditorSession NewSession()
    {
        var session = new EditorSession();
        session.NewLevel();
        return session;
    }

    [Fact]
    public void No_Selection_Emits_No_Link_Overlay()
    {
        EditorSession session = NewSession();
        EditorDocument doc = session.Document!;
        LevelObject trigger = doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 0))!;
        LevelObject target = doc.PlaceObject(LevelObjectKind.Target, new Vec3(10, 0, 0))!;
        ((Trigger)trigger.Model).Links.Add(target.Uid);

        Assert.Empty(session.BuildSelectionLinkLines(doc.Selection));
    }

    [Fact]
    public void Selecting_Source_Or_Destination_Shows_The_Link_Even_When_ShowLinks_Off()
    {
        EditorSession session = NewSession();
        session.ShowLinks = false; // the global toggle is OFF
        EditorDocument doc = session.Document!;
        LevelObject trigger = doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 0))!;
        LevelObject target = doc.PlaceObject(LevelObjectKind.Target, new Vec3(10, 0, 0))!;
        ((Trigger)trigger.Model).Links.Add(target.Uid);

        // Selecting the SOURCE shows the link (base line + arrowhead = 3 segments).
        session.Selection.SelectObject(trigger);
        Assert.Equal(3, session.BuildSelectionLinkLines(doc.Selection).Count);

        // Selecting the DESTINATION shows the same incoming link.
        session.Selection.ClearObjects();
        session.Selection.SelectObject(target);
        Assert.Equal(3, session.BuildSelectionLinkLines(doc.Selection).Count);
    }

    [Fact]
    public void Selecting_A_Keyframe_Shows_Its_Mover_And_Sequence_Links()
    {
        EditorSession session = NewSession();
        EditorDocument doc = session.Document!;
        var editor = new BrushEditor(doc);
        int b1 = editor.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        var movers = new MoverService(doc);
        Group group = movers.CreateMover(new[] { b1 }, System.Array.Empty<int>(), "Lift");
        Keyframe start = group.MovingData!.Keyframes[0];
        movers.AddKeyframe(group, new Vec3(0, 8, 0), Mat3.Identity);
        doc.RefreshObjects();

        LevelObject startObj = doc.FindByUid(start.Uid)!;
        Assert.Equal(LevelObjectKind.Keyframe, startObj.Kind);

        session.Selection.SelectObject(startObj);
        // The start keyframe touches: member mover -> start, and start -> next keyframe.
        // Both draw a base line + arrowhead, so at least 4 overlay segments appear.
        Assert.True(session.BuildSelectionLinkLines(doc.Selection).Count >= 4);
    }
}
