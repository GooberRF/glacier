using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Viewport;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 2 (fourth-round repeat): "Draw unmerged brushwork" must take effect the moment it
/// is toggled, not only after an edit nudges a build.
///
/// Root-cause layer: SCENE/MODEL STASH LIFECYCLE — not the emitter, and not the present.
/// <see cref="EditorSession.BuildScene"/> already re-emits the brush overlay every call and
/// reads <see cref="EditorSession.DrawUnmergedBrushwork"/> fresh, and round 2 already made
/// the present synchronous. But the OFF (merged) view clips against the survival/fragment
/// stash, and that stash is null until a geometry build populates it. On a freshly opened
/// level the toggle rebuilt + presented an overlay that still drew every authored face,
/// because there was no stash to clip against — so a synchronous present of unchanged
/// geometry looked identical. Only an edit's live-CSG preview built the stash, which is why
/// it "took effect" only after moving a face/brush. The fix makes toggling OFF ensure that
/// stash exists (build if missing).
///
/// These tests drive the REAL path: the RenderOptionsModel toggle command and
/// EditorSession.BuildScene (no hand-built SceneBuildOptions).
/// </summary>
public sealed class DrawUnmergedBrushworkTests
{
    private static EditorSession SessionWithBox(out int uid)
    {
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        uid = be.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 2f, Height = 2f, Depth = 2f },
            default, Mat3.Identity);
        be.SetMode(EditMode.Brush); // an edit mode: the brush overlay draws solid + wire
        return session;
    }

    private static RenderOptionsModel Model(AppSettings settings, EditorSession session, Action ensure) =>
        RenderOptionsModel.BuildGlobal(
            settings, session,
            rebuildScene: () => { },
            persist: () => { },
            applyBackfaceCulling: () => { },
            setEmitterAnimation: _ => { },
            ensureMergedBrushStash: ensure,
            gizmoVisible: () => true,
            toggleGizmo: () => { },
            applyFog: () => { },
            getRoomMode: () => Ged.App.RoomVisibility.All,
            setRoomMode: _ => { },
            getPortalFaces: () => Ged.Rendering.Scene.PortalFaceDrawMode.None,
            setPortalFaces: _ => { });

    private static RenderOptionToggle UnmergedToggle(RenderOptionsModel m) =>
        m.Toggles.First(t => t.Label == "Draw unmerged brushwork");

    // ---- The regression the round-1/2 model tests missed: the toggle must change the
    //      ACTUAL emitted overlay, not merely trigger a rebuild. ----
    [AvaloniaFact]
    public void Toggling_The_Option_Changes_The_Emitted_Overlay_Through_BuildScene()
    {
        var settings = new AppSettings();
        EditorSession session = SessionWithBox(out int uid);
        // Simulate the state right after a build: only ONE of the box's six faces survived
        // the merge (this is exactly what BrushFaceSurvival holds).
        session.BrushFaceSurvival = new Dictionary<int, bool[]>
        {
            [uid] = new[] { true, false, false, false, false, false },
        };

        RenderOptionsModel model = Model(settings, session, ensure: () => { });
        RenderOptionToggle toggle = UnmergedToggle(model);

        model.SetValue(toggle, true);              // ON: draw every authored (unmerged) face
        RenderScene authored = session.BuildScene();

        model.SetValue(toggle, false);             // OFF: clip to the surviving face(s)
        RenderScene merged = session.BuildScene();

        // ON draws all six box faces; OFF draws only the one surviving face — strictly more
        // overlay lines and triangles. (Grid + object content is identical across both.)
        Assert.True(authored.Lines.Count > merged.Lines.Count,
            $"unmerged overlay ({authored.Lines.Count} lines) should exceed merged ({merged.Lines.Count})");
        Assert.True(authored.TotalTriangleCount > merged.TotalTriangleCount,
            $"unmerged fill ({authored.TotalTriangleCount} tris) should exceed merged ({merged.TotalTriangleCount})");
    }

    // ---- The fix: turning the option OFF ensures the merged stash gets built. ----
    [Fact]
    public void Toggling_Off_Requests_A_Merged_Build_While_On_Requests_None()
    {
        var settings = new AppSettings();
        var session = new EditorSession();
        int ensures = 0;
        RenderOptionsModel model = Model(settings, session, ensure: () => ensures++);
        RenderOptionToggle toggle = UnmergedToggle(model);

        model.SetValue(toggle, true);  // ON: authored view needs no merged stash
        Assert.Equal(0, ensures);
        model.SetValue(toggle, false); // OFF: merged view needs the stash → ensure it
        Assert.Equal(1, ensures);
    }

    [AvaloniaFact]
    public void EnsureMergedBrushStash_Builds_Only_When_A_Stash_Is_Missing()
    {
        var session = new EditorSession();
        session.NewLevel();
        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
        controller.Attach();

        // No brushes → nothing to merge.
        Assert.False(controller.EnsureMergedBrushStash());

        BrushEditor be = session.BrushEditor!;
        be.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 2f, Height = 2f, Depth = 2f },
            default, Mat3.Identity);

        // Brushes present + no stash → kicks a build.
        Assert.True(controller.EnsureMergedBrushStash());

        // A stash already exists → no-op (no redundant build).
        session.BrushFaceSurvival = new Dictionary<int, bool[]>();
        Assert.False(controller.EnsureMergedBrushStash());
    }

    [AvaloniaFact]
    public async Task A_Build_Populates_The_Merged_Stash_So_Off_Can_Clip()
    {
        var session = new EditorSession();
        session.NewLevel();
        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
        controller.Attach();
        BrushEditor be = session.BrushEditor!;
        be.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 4f, Height = 4f, Depth = 4f },
            default, Mat3.Identity);

        Assert.Null(session.BrushFaceSurvival); // no build yet → OFF has nothing to clip against

        await controller.BuildAsync(interactive: false);

        Assert.NotNull(session.BrushFaceSurvival); // the stash the OFF (merged) view needs now exists
    }

    // ---- Offscreen pixel test through the same entry point (BuildScene) in an edit mode. ----
    [AvaloniaFact]
    public void Toggling_The_Option_Changes_The_Rendered_Pixels_Through_BuildScene()
    {
        GraphicsDevice gd;
        try
        {
            gd = new GraphicsDevice();
        }
        catch
        {
            return; // no D3D11 device in this environment → skip gracefully
        }

        using (gd)
        {
            var settings = new AppSettings();
            EditorSession session = SessionWithBox(out int uid);
            session.BrushFaceSurvival = new Dictionary<int, bool[]>
            {
                [uid] = new[] { true, false, false, false, false, false },
            };

            RenderOptionsModel model = Model(settings, session, ensure: () => { });
            RenderOptionToggle toggle = UnmergedToggle(model);

            model.SetValue(toggle, true);
            RenderScene authored = session.BuildScene();
            model.SetValue(toggle, false);
            RenderScene merged = session.BuildScene();

            var cam = new Ged.Rendering.Camera { Position = new Vector3(3f, 3f, -6f), AspectRatio = 320f / 240f };
            cam.LookAt(cam.Position, Vector3.Zero);

            byte[] on = OffscreenRenderer.Render(gd, authored, null, cam, RenderMode.JustTextures, 320, 240);
            byte[] off = OffscreenRenderer.Render(gd, merged, null, cam, RenderMode.JustTextures, 320, 240);

            Assert.True(PixelsDiffer(on, off),
                "toggling Draw unmerged brushwork must change the rendered overlay through BuildScene");
        }
    }

    // ---- FIFTH round: entering an edit mode with the option OFF and no stash must build it,
    //      via ONE guard at the consumption site (BuildScene), not just the option toggle. ----

    [AvaloniaFact]
    public void BuildScene_Requests_The_Merged_Stash_Only_When_The_Off_View_Needs_It()
    {
        EditorSession session = SessionWithBox(out _);
        int requests = 0;
        session.RequestMergedBrushStash = () => { requests++; return true; };

        // OFF (default) + no stash + brushes present → the merged view needs a build.
        session.DrawUnmergedBrushwork = false;
        session.BrushFaceSurvival = null;
        session.BuildScene();
        Assert.Equal(1, requests);

        // A stash already exists → nothing to request.
        session.BrushFaceSurvival = new Dictionary<int, bool[]>();
        session.BuildScene();
        Assert.Equal(1, requests);

        // ON (draw everything) never needs the merged stash.
        session.BrushFaceSurvival = null;
        session.DrawUnmergedBrushwork = true;
        session.BuildScene();
        Assert.Equal(1, requests);
    }

    [AvaloniaFact]
    public async Task Entering_Brush_Mode_On_A_Fresh_Level_Auto_Builds_The_Merged_Overlay()
    {
        var session = new EditorSession();
        session.NewLevel();
        // LivePreview off so the ONLY thing that can build the stash is the consumption-site
        // guard — reproducing an opened level whose brushes never went through a create/edit.
        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { })
        {
            LivePreviewEnabled = false,
        };
        controller.Attach();
        session.RequestMergedBrushStash = () => controller.EnsureMergedBrushStash();

        BrushEditor be = session.BrushEditor!;
        be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 4f, Height = 4f, Depth = 4f },
            new Vec3(0f, 0f, 0f), Mat3.Identity);
        be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 4f, Height = 4f, Depth = 4f },
            new Vec3(2f, 0f, 0f), Mat3.Identity); // overlaps the first → interior faces get clipped

        Assert.Null(session.BrushFaceSurvival);          // opened state: no build has run
        Assert.False(session.DrawUnmergedBrushwork);     // OFF by default

        be.SetMode(EditMode.Brush);                      // enter an edit mode — no toggle, no edit
        session.BuildScene();                            // consumption site fires the guard

        await WaitFor(() => session.BrushFaceSurvival is not null, timeoutMs: 5000);
        Assert.NotNull(session.BrushFaceSurvival);       // the merged stash now exists

        // The merged (OFF) overlay draws strictly fewer faces than the unmerged (ON) one.
        session.DrawUnmergedBrushwork = false;
        RenderScene merged = session.BuildScene();
        session.DrawUnmergedBrushwork = true;
        RenderScene authored = session.BuildScene();
        Assert.True(authored.Lines.Count > merged.Lines.Count,
            $"merged overlay ({merged.Lines.Count}) should draw fewer lines than authored ({authored.Lines.Count})");
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
    }

    private static bool PixelsDiffer(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return true;
        }

        int changed = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                changed++;
            }
        }

        return changed > a.Length / 500; // > ~0.2% of channel bytes differ
    }
}
