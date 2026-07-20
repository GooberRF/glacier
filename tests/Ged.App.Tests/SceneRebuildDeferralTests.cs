using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Performance regression guard for the interactive-transform rework (symptom B — "moving/rotating/
/// scaling objects is jerky"). The jerkiness was O(level) work PER pointer-move frame: each applied
/// gizmo / M-N drag step ran a full <see cref="EditorSession.BuildScene"/> re-emit + a 4-pane GPU
/// re-upload TWICE (once from the <c>BrushesChanged</c> handler, once from the after-edit path), plus
/// panel refreshes and a re-armed live-CSG debounce. The fix defers all of that to the single drag
/// commit, updating only the cheap selection/gizmo overlay each frame.
///
/// <para><see cref="EditorSession.SceneBuildCount"/> is the headless-measurable proxy for "scene
/// rebuilds" (each rebuild = one BuildScene emit + the GPU re-upload it drives). These tests drive the
/// REAL <see cref="Ged.Core.Editing.BrushEditor"/> and BuildScene; the two heavy-refresh call sites are
/// mirrored exactly as <c>MainWindow.SubscribeDocument</c>'s <c>BrushesChanged</c> handler and
/// <c>MainWindow.AfterBrushEdit</c> route them (both guarded by
/// <see cref="EditorSession.InteractiveTransformActive"/>).</para>
/// </summary>
public sealed class SceneRebuildDeferralTests
{
    private const int DragSteps = 20;

    private static (EditorSession Session, BrushEditor Be, int Uid) SessionWithBox()
    {
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        int uid = be.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 4, Height = 4, Depth = 4 },
            default, Mat3.Identity);
        return (session, be, uid);
    }

    private static void MoveStep(BrushEditor be, int uid) =>
        be.EditBrushesCoalesced(new List<int> { uid }, "Move (gizmo)",
            b => { BrushTransform.Move(b, new Vec3(1, 0, 0)); return OpResult.Ok(); }, null);

    [AvaloniaFact]
    public void An_Interactive_Drag_Rebuilds_The_Scene_Exactly_Once_On_Commit()
    {
        (EditorSession session, BrushEditor be, int uid) = SessionWithBox();

        // MainWindow's two heavy-refresh sites, each guarded by the drag gate. During a drag the model
        // edit still applies (live feedback) but no BuildScene runs — only the cheap overlay would.
        be.BrushesChanged += () => { if (!session.InteractiveTransformActive) session.BuildScene(); };
        void AfterBrushEdit()
        {
            if (session.InteractiveTransformActive)
            {
                return; // cheap RefreshSelectionOverlay only — no BuildScene
            }

            session.BuildScene();
        }

        session.InteractiveTransformActive = true; // gizmo/brush drag begins (BeginInteractiveTransform)
        int before = session.SceneBuildCount;
        for (int i = 0; i < DragSteps; i++)
        {
            MoveStep(be, uid);
            AfterBrushEdit();
        }

        Assert.Equal(0, session.SceneBuildCount - before); // O(1): zero rebuilds across the whole drag body

        // CommitInteractiveTransform: the single deferred rebuild.
        session.InteractiveTransformActive = false;
        session.BuildScene();
        Assert.Equal(1, session.SceneBuildCount - before);
    }

    [AvaloniaFact]
    public void Without_The_Drag_Gate_The_Same_Edits_Rebuild_Twice_Per_Step()
    {
        // The pre-fix cost this rework removes: each applied brush step triggered TWO BuildScene emits
        // (BrushesChanged handler + AfterBrushEdit), i.e. O(N) per drag — the jerkiness.
        (EditorSession session, BrushEditor be, int uid) = SessionWithBox();
        be.BrushesChanged += () => { if (!session.InteractiveTransformActive) session.BuildScene(); };
        void AfterBrushEdit()
        {
            if (session.InteractiveTransformActive)
            {
                return;
            }

            session.BuildScene();
        }

        int before = session.SceneBuildCount; // gate stays OFF (no drag)
        for (int i = 0; i < DragSteps; i++)
        {
            MoveStep(be, uid);
            AfterBrushEdit();
        }

        Assert.Equal(DragSteps * 2, session.SceneBuildCount - before);
    }

    [AvaloniaFact]
    public void A_Suspended_Drag_Never_Arms_The_Live_CSG_Preview_Debounce()
    {
        // Fully real: the GeometryBuildController is the app code. SuspendLivePreview makes OnBrushesChanged
        // accumulate dirty state (needed for a correct commit) WITHOUT re-arming the debounced live-CSG
        // build each frame; ArmLivePreviewIfPending fires it once on commit.
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        int uid = be.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 4, Height = 4, Depth = 4 },
            default, Mat3.Identity); // created BEFORE Attach → no initial arm

        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
        controller.Attach();
        Assert.True(controller.LivePreviewEnabled);
        Assert.False(controller.LivePreviewPending);

        controller.SuspendLivePreview = true; // drag begins
        for (int i = 0; i < DragSteps; i++)
        {
            MoveStep(be, uid);
        }

        Assert.False(controller.LivePreviewPending); // never armed across the drag
        Assert.True(controller.GeometryDirty);       // but the geometry is dirty, ready for the commit

        controller.SuspendLivePreview = false; // commit
        controller.ArmLivePreviewIfPending();
        Assert.True(controller.LivePreviewPending); // armed exactly once, on commit
    }

    [AvaloniaFact]
    public void A_Normal_Brush_Edit_Still_Arms_The_Live_CSG_Preview()
    {
        // Contrast / non-regression: outside a drag the debounce arms per edit exactly as before.
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        int uid = be.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 4, Height = 4, Depth = 4 },
            default, Mat3.Identity);

        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
        controller.Attach();

        MoveStep(be, uid);
        Assert.True(controller.LivePreviewPending);
    }
}
