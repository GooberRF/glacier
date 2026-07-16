using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Viewport;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 5: the clipped-face fragment overlay lifecycle. (a) The "Show Clipped Brush
/// Faces" toggle rebuilds the scene immediately. (b) A move of ONE brush marks only that
/// brush's fragments stale (drawing it authored) while the build-overlay stash — and
/// every other brush's fragment overlay — is preserved until the next build.
/// </summary>
public sealed class FragmentLifecycleTests
{
    private static (EditorSession Session, GeometryBuildController Controller) NewSession()
    {
        var session = new EditorSession();
        session.NewLevel();
        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
        controller.Attach();
        return (session, controller);
    }

    private static int AddBox(BrushEditor be, Vec3 pos) =>
        be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2f, Height = 2f, Depth = 2f }, pos, Mat3.Identity);

    private static void StashOverlays(EditorSession s)
    {
        s.BrushFaceSurvival = new Dictionary<int, bool[]>();
        s.BrushFragments = BrushFragmentIndex.Build(new Geometry(), new Dictionary<int, int>(), new Dictionary<int, bool[]>());
        s.StaleFragmentBrushUids.Clear();
    }

    [AvaloniaFact]
    public void Moving_One_Brush_Marks_Only_It_Stale_And_Keeps_The_Stash()
    {
        (EditorSession session, _) = NewSession();
        BrushEditor be = session.BrushEditor!;
        int a = AddBox(be, new Vec3(0, 0, 0));
        int b = AddBox(be, new Vec3(20, 0, 0));
        StashOverlays(session); // simulate the state right after a build

        // A gizmo / M-N drag commits exactly through EditBrushesCoalesced on brush A.
        be.EditBrushesCoalesced(new[] { a }, "Move (gizmo)",
            br => { BrushTransform.Move(br, new Vec3(1, 0, 0)); return OpResult.Ok(); }, null);

        // The stash survives (untouched brushes keep their fragment overlay)...
        Assert.NotNull(session.BrushFragments);
        Assert.NotNull(session.BrushFaceSurvival);
        // ...and only the moved brush is stale.
        Assert.Contains(a, session.StaleFragmentBrushUids);
        Assert.DoesNotContain(b, session.StaleFragmentBrushUids);
    }

    [AvaloniaFact]
    public void A_Structural_Change_Invalidates_The_Whole_Stash()
    {
        (EditorSession session, _) = NewSession();
        BrushEditor be = session.BrushEditor!;
        AddBox(be, new Vec3(0, 0, 0));
        StashOverlays(session);

        // Creating a brush (structural) reports no specific UIDs → the stash is dropped.
        AddBox(be, new Vec3(5, 0, 0));

        Assert.Null(session.BrushFragments);
        Assert.Null(session.BrushFaceSurvival);
        Assert.Empty(session.StaleFragmentBrushUids);
    }

    [AvaloniaFact]
    public void A_Fresh_Build_Stash_Clears_Staleness()
    {
        (EditorSession session, _) = NewSession();
        session.StaleFragmentBrushUids.Add(99);
        // NewLevel clears build overlays (incl. the stale set).
        session.NewLevel();
        Assert.Empty(session.StaleFragmentBrushUids);
    }

    [Fact]
    public void Show_Clipped_Brush_Faces_Toggle_Rebuilds_The_Scene_Immediately()
    {
        var settings = new AppSettings();
        var session = new EditorSession();
        int rebuilds = 0;

        RenderOptionsModel model = RenderOptionsModel.BuildGlobal(
            settings, session,
            rebuildScene: () => rebuilds++,
            persist: () => { },
            applyBackfaceCulling: () => { },
            setEmitterAnimation: _ => { },
            ensureMergedBrushStash: () => { },
            gizmoVisible: () => true,
            toggleGizmo: () => { },
            applyFog: () => { },
            getRoomMode: () => Ged.App.RoomVisibility.All,
            setRoomMode: _ => { },
            getPortalFaces: () => Ged.Rendering.Scene.PortalFaceDrawMode.None,
            setPortalFaces: _ => { });

        RenderOptionToggle toggle = System.Linq.Enumerable.First(
            model.Toggles, t => t.Label == "Draw unmerged brushwork");

        model.SetValue(toggle, true);
        Assert.True(settings.DrawUnmergedBrushwork);
        Assert.True(session.DrawUnmergedBrushwork);
        Assert.Equal(1, rebuilds); // the toggle change forces an immediate rebuild
    }
}
