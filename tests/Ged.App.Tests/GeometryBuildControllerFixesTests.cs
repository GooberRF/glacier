using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Regression coverage for the build-controller defect fixes:
/// <list type="bullet">
/// <item>Fix A — the merged-brush STASH build (<see cref="GeometryBuildController.EnsureMergedBrushStash"/>)
/// populates the brush overlay without replacing the document's loaded static_geometry + lightmaps
/// (opening a level no longer wipes its baked lighting). The live-CSG preview build still applies.</item>
/// <item>Fix B — a user build preempts a seamless background build; a user build during another user
/// build is refused with a status message rather than silently dropped.</item>
/// <item>Fix C — Remove Lightmaps re-arms the relight debounce when Preview Lighting is active.</item>
/// <item>Fix D — the relight tick reschedules (instead of dropping) when a build is in flight.</item>
/// </list>
/// </summary>
public sealed class GeometryBuildControllerFixesTests
{
    private static GeometryBuildController NewController(EditorSession session, Action<string>? status = null) =>
        new(session, status ?? (_ => { }), () => { }, (_, _) => { });

    private static int AddBox(EditorSession session, float size = 4f) =>
        session.BrushEditor!.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = size, Height = size, Depth = size },
            default, Mat3.Identity);

    /// <summary>Adds an air box — a hollow room whose walls compile to real surfaces + lightmap pages.</summary>
    private static void AddAirRoom(EditorSession session, float w = 24f, float h = 8f, float d = 24f)
    {
        int uid = session.Document!.AllocateUid();
        session.BrushEditor!.AddBrush(
            new Brush
            {
                Uid = uid,
                Position = default,
                Rotation = Mat3.Identity,
                Geometry = BrushFactory.Box(w, h, d, 0, 0, 0, BrushCreateParams.DefaultTexture),
                Flags = (uint)BrushFlags.Air,
                Life = -1,
                State = BrushState.Normal,
            },
            "Add air room");
    }

    private static Geometry? FindGeometry(RflFile rfl)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                return g.Geometry;
            }
        }

        return null;
    }

    private static LightmapsSection? FindLightmaps(RflFile rfl)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is LightmapsSection lm)
            {
                return lm;
            }
        }

        return null;
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
    }

    // ---- Fix A: stash-only build must not mutate the document ----

    [AvaloniaFact]
    public async Task StashOnlyBuild_PopulatesTheStash_WithoutTouchingLoadedGeometryOrLightmaps()
    {
        var session = new EditorSession();
        session.NewLevel();
        AddAirRoom(session);

        // Establish loaded compiled geometry + baked lightmap pages BEFORE the controller attaches —
        // exactly the state of a freshly opened RED level (no editor build has run, so the brush edit
        // that seeds the room never marks anything dirty and the preview timer never arms).
        EditorDocument doc = session.Document!;
        GeometryBuildService.BuildAndApply(doc.Rfl, new CompileOptions { BuildSurfaces = true });

        GeometryBuildController controller = NewController(session);
        controller.LivePreviewEnabled = false;
        controller.Attach();
        doc.MarkSaved(); // a freshly opened file is clean, and no build has populated the overlay stash

        Geometry loadedGeo = FindGeometry(doc.Rfl)!;
        LightmapsSection loadedLm = FindLightmaps(doc.Rfl)!;
        int pageCount = loadedLm.Lightmaps.Count;
        Assert.True(pageCount > 0, "setup must produce lightmap pages to detect a wipe");
        Assert.False(doc.IsDirty);
        Assert.False(controller.GeometryDirty);
        Assert.Null(session.BrushFaceSurvival);

        // The merged-brush stash build (the entry BuildScene fires on open) must be stash-only.
        Assert.True(controller.EnsureMergedBrushStash());
        await WaitFor(() => session.BrushFaceSurvival is not null, timeoutMs: 5000);

        // Sections untouched — same objects, pages intact.
        Assert.Same(loadedGeo, FindGeometry(doc.Rfl));
        Assert.Same(loadedLm, FindLightmaps(doc.Rfl));
        Assert.Equal(pageCount, loadedLm.Lightmaps.Count);

        // Document + dirty tracking left exactly as it was; no preview-quality flag.
        Assert.False(doc.IsDirty, "a stash-only build must not dirty the document");
        Assert.False(controller.GeometryDirty, "a stash-only build must not alter geometry-dirty");
        Assert.False(controller.GeometryIsPreview, "stash-only never marks the geometry preview-quality");

        // But the brush overlay stash IS populated (the whole point of the build).
        Assert.NotNull(session.BrushFaceSurvival);
        Assert.NotNull(session.BrushFragments);
    }

    // ---- Live-CSG preview regression: the debounced preview STILL applies to the document ----

    [AvaloniaFact]
    public async Task LiveCsgPreviewBuild_StillApplies_GeometryToTheDocument()
    {
        var session = new EditorSession();
        session.NewLevel();
        GeometryBuildController controller = NewController(session);
        controller.Attach();
        EditorDocument doc = session.Document!;
        AddBox(session);
        doc.MarkSaved();

        Assert.True(controller.GeometryDirty, "a brush edit marks geometry dirty");

        // Exactly what the live-CSG preview timer fires after a brush edit.
        await controller.BuildAsync(interactive: false);

        Assert.True(controller.GeometryIsPreview, "the live-CSG preview applies UNSEALED geometry");
        Assert.False(controller.GeometryDirty, "applying the preview clears the geometry-dirty flag");
        Assert.True(doc.IsDirty, "the live-CSG preview mutates (and dirties) the document");
        Assert.NotNull(FindGeometry(doc.Rfl));
    }

    // ---- Fix C: Remove Lightmaps + Preview Lighting ----

    [AvaloniaFact]
    public void RemoveLightmaps_ArmsRelight_OnlyWithPreviewOn_AndAlwaysGreysThePages()
    {
        // Preview ON: the greyed pages must re-bake automatically → the debounce is armed.
        (EditorSession s1, GeometryBuildController c1) = SessionWithBakedBox();
        c1.PreviewLightingEnabled = true;
        c1.RemoveLightmaps();
        Assert.True(c1.AutoRelightPending, "Remove Lightmaps with the preview active must re-arm the relight");
        AssertAllPagesGrey(s1);

        // Preview OFF: fully manual, no relight scheduled — but pages are still greyed.
        (EditorSession s2, GeometryBuildController c2) = SessionWithBakedBox();
        Assert.False(c2.PreviewLightingEnabled);
        c2.RemoveLightmaps();
        Assert.False(c2.AutoRelightPending, "Remove Lightmaps with the preview off stays fully manual");
        AssertAllPagesGrey(s2);
    }

    private static (EditorSession Session, GeometryBuildController Controller) SessionWithBakedBox()
    {
        var session = new EditorSession();
        session.NewLevel();
        AddAirRoom(session);
        GeometryBuildService.BuildAndApply(session.Document!.Rfl, new CompileOptions { BuildSurfaces = true });
        GeometryBuildController controller = NewController(session);
        controller.Attach(); // attach to an already-built level (no brush edit fires after attach)
        return (session, controller);
    }

    private static void AssertAllPagesGrey(EditorSession session)
    {
        LightmapsSection lm = FindLightmaps(session.Document!.Rfl)!;
        Assert.NotEmpty(lm.Lightmaps);
        foreach (Lightmap page in lm.Lightmaps)
        {
            Assert.All(page.Pixels, b => Assert.Equal((byte)128, b));
        }
    }

    // ---- Fix B: no dead clicks ----

    [AvaloniaFact]
    public async Task UserBuild_Preempts_AnInFlightBackgroundBuild()
    {
        var session = new EditorSession();
        session.NewLevel();
        var msgs = new List<string>();
        GeometryBuildController controller = NewController(session, msgs.Add);
        controller.LivePreviewEnabled = false; // drive builds explicitly
        controller.Attach();
        AddBox(session);

        // A seamless background build (live-CSG preview) is started but NOT awaited.
        Task background = controller.BuildAsync(interactive: false);

        // A user build arriving now must preempt it and run to completion — never a silent drop.
        await controller.BuildAsync(interactive: true);
        await background; // the preempted background build has unwound

        Assert.False(controller.GeometryIsPreview, "the user (interactive) build sealed the geometry");
        Assert.False(controller.GeometryDirty);
        Assert.DoesNotContain(msgs, m => m.Contains("already running"));
    }

    [AvaloniaFact]
    public async Task SecondUserBuild_DuringAUserBuild_IsRefusedWithAMessage()
    {
        var session = new EditorSession();
        session.NewLevel();
        var msgs = new List<string>();
        GeometryBuildController controller = NewController(session, msgs.Add);
        controller.LivePreviewEnabled = false;
        controller.Attach();
        AddBox(session);

        Task first = controller.BuildAsync(interactive: true);   // user build in flight (not awaited)
        Task second = controller.BuildAsync(interactive: true);  // arrives while the first is running

        await Task.WhenAll(first, second);

        // The second click is refused with a status message rather than dropped in silence.
        Assert.Contains(msgs, m => m.Contains("already running"));
    }

    // ---- Fix D: relight debounce resilience (decision logic, headless-safe) ----

    [AvaloniaFact]
    public async Task RelightTick_Reschedules_WhileABuildIsInFlight_InsteadOfDropping()
    {
        var session = new EditorSession();
        session.NewLevel();
        GeometryBuildController controller = NewController(session);
        controller.LivePreviewEnabled = false;
        controller.PreviewLightingEnabled = true;
        controller.Attach();
        AddBox(session); // marks LightingDirty
        Assert.True(controller.LightingDirty);

        // With a build in flight, the tick must reschedule (retry) — not drop the pending relight.
        Task bg = controller.BuildAsync(interactive: false);
        Assert.Equal(GeometryBuildController.RelightTickAction.Reschedule, controller.DecideRelightTick());
        await bg;
    }

    [AvaloniaFact]
    public void RelightTick_DoesNothing_AndDoesNotSpin_WhenPreviewOff_Or_NotDirty()
    {
        var session = new EditorSession();
        session.NewLevel();
        GeometryBuildController controller = NewController(session);
        controller.Attach();

        // Preview off → terminal None (never Reschedule, so the timer can't spin).
        Assert.False(controller.PreviewLightingEnabled);
        Assert.Equal(GeometryBuildController.RelightTickAction.None, controller.DecideRelightTick());

        // Preview on but nothing dirty → still terminal None.
        controller.PreviewLightingEnabled = true;
        Assert.False(controller.LightingDirty);
        Assert.Equal(GeometryBuildController.RelightTickAction.None, controller.DecideRelightTick());
    }

    // ---- Save must not spuriously re-dirty lighting (owner: every save after a bake logged
    //       "Saved with unbaked lighting changes — bake when ready.") ----

    [AvaloniaFact]
    public async Task SaveAfterBake_DoesNotSpuriouslyMarkLightingDirty_OnSubsequentSaves()
    {
        var session = new EditorSession();
        session.NewLevel();
        GeometryBuildController controller = NewController(session);
        controller.LivePreviewEnabled = false; // drive builds explicitly, no background preview
        controller.Attach();
        EditorDocument doc = session.Document!;
        AddAirRoom(session);
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(0f, 2f, 0f));

        // Calculate Maps and Light: a real geometry build + lighting bake, which clears LightingDirty.
        Assert.True(await controller.CalculateMapsAndLightAsync(shadows: false));
        Assert.False(controller.LightingDirty, "a completed bake leaves lighting clean");

        // First save: EditorDocument.MarkSaved raises DirtyChanged to refresh the clean indicator. It
        // must NOT re-dirty lighting — otherwise the NEXT save reports the spurious "unbaked lighting"
        // nudge even though nothing lighting-relevant changed.
        doc.MarkSaved();
        Assert.False(controller.LightingDirty, "a save must not spuriously mark lighting dirty");
        Assert.Equal(
            SaveGuard.SaveNotice.None,
            SaveGuard.NoticeForDirtySave(controller.GeometryDirty, controller.LightingDirty));

        // Second save — the owner's exact repro: still clean, still no unbaked-lighting nudge.
        doc.MarkSaved();
        Assert.False(controller.LightingDirty);
        Assert.Equal(
            SaveGuard.SaveNotice.None,
            SaveGuard.NoticeForDirtySave(controller.GeometryDirty, controller.LightingDirty));
    }

    [AvaloniaFact]
    public async Task RealLightEditAfterSave_StillMarksLightingDirty_AndNudges()
    {
        var session = new EditorSession();
        session.NewLevel();
        GeometryBuildController controller = NewController(session);
        controller.LivePreviewEnabled = false;
        controller.Attach();
        EditorDocument doc = session.Document!;
        AddAirRoom(session);
        LevelObject light = doc.PlaceObject(LevelObjectKind.Light, new Vec3(0f, 2f, 0f))!;

        Assert.True(await controller.CalculateMapsAndLightAsync(shadows: false));
        doc.MarkSaved();
        Assert.False(controller.LightingDirty);

        // A genuine light move through the undo stack is a real content change — the nudge is CORRECT here
        // and must survive the save-suppression fix (only clean/just-saved transitions are suppressed).
        doc.EditValue(light.Section, "Move light", light.Position, new Vec3(3f, 2f, 0f), v => light.Position = v);
        Assert.True(controller.LightingDirty, "a genuine light edit after a save must still mark lighting stale");
        Assert.Equal(
            SaveGuard.SaveNotice.UnbakedLighting,
            SaveGuard.NoticeForDirtySave(controller.GeometryDirty, controller.LightingDirty));
    }
}
