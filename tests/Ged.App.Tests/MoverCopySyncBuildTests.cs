using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The editor's builds refresh stale mover copies before compiling: editing a mover's member brush in
/// the viewport and then building (the live-CSG preview or a user Build) leaves the movers-section copy
/// matching the edited brush, folded into the edit's undo step — so the preview never adds an undo
/// entry of its own, and one Undo restores the member and the copy together.
/// </summary>
public sealed class MoverCopySyncBuildTests
{
    private static (EditorSession Session, GeometryBuildController Controller, MoverService Movers, int Box) MoverLevel()
    {
        var session = new EditorSession();
        session.NewLevel();
        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
        controller.LivePreviewEnabled = false; // the test drives the builds itself
        controller.Attach();
        int box = session.BrushEditor!.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(0, 1, 0), Mat3.Identity);
        var movers = new MoverService(session.Document!);
        movers.CreateMover(new[] { box }, Array.Empty<int>(), "Lift");
        return (session, controller, movers, box);
    }

    private static Brush CopyOf(MoverService movers, int uid) => movers.MoverBrushes.Single(b => b.Uid == uid);

    [AvaloniaTheory]
    [InlineData(false)] // the debounced live-CSG preview
    [InlineData(true)]  // Build (sealed)
    public async Task A_Build_After_A_Viewport_Edit_Refreshes_The_Mover_Copy_In_The_Same_Undo_Step(bool interactive)
    {
        (EditorSession session, GeometryBuildController controller, MoverService movers, int box) = MoverLevel();
        EditorDocument doc = session.Document!;
        Vec3 restPos = CopyOf(movers, box).Position;

        session.BrushEditor!.EditBrushesCoalesced(new[] { box }, "Move (gizmo)",
            b => { BrushTransform.Move(b, new Vec3(0, 2, 0)); return OpResult.Ok(); }, null);
        int nodes = doc.Undo.NodeCount;
        Assert.Single(movers.StaleMovers());

        await controller.BuildAsync(interactive);

        Brush member = session.BrushEditor.FindBrush(box)!;
        Assert.Equal(member.Position, CopyOf(movers, box).Position);
        Assert.Empty(movers.StaleMovers());
        Assert.Equal(nodes, doc.Undo.NodeCount);

        controller.ApplyUndoRedo(redo: false, coalesce: true);
        Assert.Equal(restPos, CopyOf(movers, box).Position);
        Assert.Equal(restPos, session.BrushEditor.FindBrush(box)!.Position);
        Assert.Empty(movers.StaleMovers());
    }

    [AvaloniaFact]
    public async Task A_Build_With_No_Mover_Edits_Leaves_The_Copies_Alone()
    {
        (EditorSession session, GeometryBuildController controller, MoverService movers, int box) = MoverLevel();
        Geometry geometry = CopyOf(movers, box).Geometry;
        int nodes = session.Document!.Undo.NodeCount;
        await controller.BuildAsync(interactive: false);
        Assert.Same(geometry, CopyOf(movers, box).Geometry); // build-owned ids are renumbered in place, never replaced
        Assert.Equal(nodes, session.Document.Undo.NodeCount);
    }
}
