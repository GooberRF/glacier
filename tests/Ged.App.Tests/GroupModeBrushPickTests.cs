using System.Linq;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;
using CoreVec3 = Ged.Core.Model.Vec3;

namespace Ged.App.Tests;

/// <summary>
/// P4 — brushes must be selectable in Group mode. The prior fix only widened
/// <see cref="PickGate.AllowsBrushEditor"/>, but in Group mode the scene emits NO solid brush faces
/// (solidFill is off outside brush-edit modes), so a whole brush contributed only wireframe lines —
/// zero pickable geometry in the id-buffer — and no <see cref="PickKind.Brush"/> hit ever reached
/// the gate. The real chokepoint is scene emission: whole-brush faces are now emitted PICK-ONLY when
/// whole-brush selection is enabled (Groups/Brushes chip) but nothing is solid-filled. This test
/// exercises the FULL path — emission, then the gate, then the router — not just the gate.
/// </summary>
public sealed class GroupModeBrushPickTests
{
    [AvaloniaFact]
    public void Whole_Brush_Is_Emitted_Pickable_Gated_And_Routable_In_Group_Mode()
    {
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        int uid = be.CreateBrush(new BrushCreateParams { Width = 4, Height = 4, Depth = 4 }, new CoreVec3(0, 0, 0), Ged.Core.Model.Mat3.Identity);

        // Group mode: Groups chip active, brushes NOT solid-filled.
        be.SetMode(EditMode.Group);
        session.ActiveSelectKinds = SelectKinds.Groups;

        // 1) EMISSION: the brush faces are in the scene as a pick-only batch carrying the whole-brush id.
        RenderScene scene = session.BuildScene();
        GeometryBatch pickBatch = Assert.Single(scene.Batches, b => b.PickOnly);
        Assert.NotEmpty(pickBatch.Vertices);
        PickId decoded = PickId.Decode(pickBatch.Vertices[0].PickId);
        Assert.Equal(PickKind.Brush, decoded.Kind);
        Assert.Equal(uid, decoded.Index);

        // The pick-only batch must NOT be a portal batch and NOT be visibly filled (colour pass skips it).
        Assert.False(pickBatch.IsPortal);

        // 2) GATE: the PickGate admits a whole-brush hit under the Groups chip.
        Assert.True(PickGate.AllowsBrushEditor(session.ActiveSelectKinds, PickKind.Brush));

        // 3) ROUTING: the router actually selects the brush in Group mode.
        Assert.True(session.Selection.SelectBrush(decoded.Index));
        Assert.Contains(uid, be.SelectedBrushes);
    }

    [AvaloniaFact]
    public void Object_Mode_Does_Not_Emit_Brushes_Into_The_Pick_Pass()
    {
        // Regression guard for the narrow scope of the fix: in Object mode brushes are NOT group
        // members and must stay unpickable (objects/movers are picked by their own path), so no
        // pick-only brush faces are emitted.
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        be.CreateBrush(new BrushCreateParams { Width = 4, Height = 4, Depth = 4 }, new CoreVec3(0, 0, 0), Ged.Core.Model.Mat3.Identity);

        be.SetMode(EditMode.Object);
        session.ActiveSelectKinds = SelectKinds.Objects;

        RenderScene scene = session.BuildScene();
        Assert.DoesNotContain(scene.Batches, b => b.PickOnly);
    }
}
