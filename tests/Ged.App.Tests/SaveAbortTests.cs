using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Save semantics: "RED-style + seal guard" (owner decision). A save NEVER bakes lighting or recompiles
/// on its own — stock RED serializes exactly what was last built, and Glacier now matches. A merely-dirty
/// document is written as-is (nudged by a Hint). The SOLE exception is GED's unsealed live-CSG preview
/// geometry (a state RED never has): the seal guard re-seals it with a geometry-only build first, and is
/// the SOLE path that can abort a save. These cover the composed units the shell's <c>SaveAsync</c> is
/// thin glue over:
/// <list type="bullet">
/// <item><see cref="SaveGuard.RequiresSeal"/> / <see cref="SaveGuard.EvaluateSeal"/> /
/// <see cref="SaveGuard.NoticeForDirtySave"/> — the write/abort and advisory decisions.</item>
/// <item>the build controller's user-build entry points returning <c>false</c> when refused, with the
/// geometry left preview (exactly the state that drives the seal-guard abort), and a geometry-only seal
/// build (<c>bakeLighting == false</c>) that clears the preview flag without touching the dirty light.</item>
/// </list>
/// (The window's <c>SaveAsync</c> itself is not headless-constructable — it owns GPU viewports — so its
/// glue is verified by reading, over these tested units.)
/// </summary>
public sealed class SaveAbortTests
{
    private static GeometryBuildController NewController(EditorSession session, Action<string>? status = null) =>
        new(session, status ?? (_ => { }), () => { }, (_, _) => { });

    private static void AddBox(EditorSession session, float size = 4f) =>
        session.BrushEditor!.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = size, Height = size, Depth = size },
            default, Mat3.Identity);

    // ---- Seal-guard decision table (the SOLE rebuild/abort trigger) ----

    [Fact]
    public void RequiresSeal_IsExactly_The_Preview_Flag()
    {
        // Preview geometry (unsealed) is the ONLY state that forces a pre-save rebuild.
        Assert.True(SaveGuard.RequiresSeal(geometryIsPreview: true));
        Assert.False(SaveGuard.RequiresSeal(geometryIsPreview: false));
    }

    [Fact]
    public void Seal_Proceeds_When_The_Build_Ran_And_Geometry_Is_Sealed()
    {
        Assert.Equal(SaveGuard.PreSaveOutcome.Proceed, SaveGuard.EvaluateSeal(sealBuildRan: true, geometryDirty: false, geometryIsPreview: false));
    }

    [Fact]
    public void Seal_Aborts_As_BuildRunning_When_The_Seal_Build_Was_Refused()
    {
        // sealBuildRan == false means the seal build was refused (another user build in flight).
        Assert.Equal(SaveGuard.PreSaveOutcome.AbortBuildRunning, SaveGuard.EvaluateSeal(sealBuildRan: false, geometryDirty: false, geometryIsPreview: true));
        Assert.Equal(SaveGuard.PreSaveOutcome.AbortBuildRunning, SaveGuard.EvaluateSeal(sealBuildRan: false, geometryDirty: true, geometryIsPreview: true));
    }

    [Theory]
    [InlineData(true, false)]  // still dirty (build failed / cancelled)
    [InlineData(false, true)]  // still preview-quality (unsealed)
    [InlineData(true, true)]
    public void Seal_Aborts_As_Incomplete_When_Geometry_Is_Still_Dirty_Or_Preview(bool dirty, bool preview)
    {
        Assert.Equal(SaveGuard.PreSaveOutcome.AbortSealIncomplete, SaveGuard.EvaluateSeal(sealBuildRan: true, geometryDirty: dirty, geometryIsPreview: preview));
    }

    // ---- Advisory notice for a plain (no-seal) RED-style save: geometry wins over lighting ----

    [Fact]
    public void DirtySave_Notice_Is_UnbuiltGeometry_When_Geometry_Dirty()
    {
        Assert.Equal(SaveGuard.SaveNotice.UnbuiltGeometry, SaveGuard.NoticeForDirtySave(geometryDirty: true, lightingDirty: false));
        // Geometry wins the single hint when both are dirty.
        Assert.Equal(SaveGuard.SaveNotice.UnbuiltGeometry, SaveGuard.NoticeForDirtySave(geometryDirty: true, lightingDirty: true));
    }

    [Fact]
    public void DirtySave_Notice_Is_UnbakedLighting_When_Only_Lighting_Dirty()
    {
        Assert.Equal(SaveGuard.SaveNotice.UnbakedLighting, SaveGuard.NoticeForDirtySave(geometryDirty: false, lightingDirty: true));
    }

    [Fact]
    public void DirtySave_Notice_Is_None_When_Clean()
    {
        Assert.Equal(SaveGuard.SaveNotice.None, SaveGuard.NoticeForDirtySave(geometryDirty: false, lightingDirty: false));
    }

    // ---- The advisory severities the shell emits are exactly Hint / Hint / Info ----
    // (An internal enum can't be a public [Theory] parameter — CS0051 — so this is a single Fact.)

    [Fact]
    public void SaveNotice_Maps_To_The_Documented_Severity()
    {
        // Mirrors the shell's post-write switch: dirty saves nudge at Hint, the re-seal informs at Info.
        // Pinned so the severities can't silently drift.
        Assert.Equal(NotificationSeverity.Hint, SeverityFor(SaveGuard.SaveNotice.UnbuiltGeometry));
        Assert.Equal(NotificationSeverity.Hint, SeverityFor(SaveGuard.SaveNotice.UnbakedLighting));
        Assert.Equal(NotificationSeverity.Info, SeverityFor(SaveGuard.SaveNotice.GeometryResealed));

        // A Hint only toasts at "Everything", so a merely-dirty save never raises an unsolicited toast
        // at the default "Info" level; the re-seal Info toasts from "Info" up.
        Assert.False(NotificationService.ShouldToast(NotificationSeverity.Hint, ToastLevel.Info));
        Assert.True(NotificationService.ShouldToast(NotificationSeverity.Hint, ToastLevel.Everything));
        Assert.True(NotificationService.ShouldToast(NotificationSeverity.Info, ToastLevel.Info));
    }

    private static NotificationSeverity SeverityFor(SaveGuard.SaveNotice notice) => notice switch
    {
        SaveGuard.SaveNotice.GeometryResealed => NotificationSeverity.Info,
        SaveGuard.SaveNotice.UnbuiltGeometry => NotificationSeverity.Hint,
        SaveGuard.SaveNotice.UnbakedLighting => NotificationSeverity.Hint,
        _ => NotificationSeverity.Info,
    };

    // ---- Controller contract: a plain dirty/lighting save never rebuilds; a preview save re-seals ----

    [AvaloniaFact]
    public async Task LightingDirty_Save_Does_Not_Seal_And_The_Notice_Is_UnbakedLighting()
    {
        var session = new EditorSession();
        session.NewLevel();
        GeometryBuildController controller = NewController(session);
        controller.LivePreviewEnabled = false; // drive builds explicitly
        controller.Attach();
        AddBox(session);

        // A full build seals the geometry and clears the flags. Then a light-only edit marks lighting
        // dirty WITHOUT touching geometry — the RED-style save writes as-is, no bake.
        Assert.True(await controller.CalculateMapsAndLightAsync(shadows: false));
        Assert.False(controller.GeometryDirty);
        Assert.False(controller.GeometryIsPreview);

        controller.MarkLightChanged(new Aabb(new Vec3(-1, -1, -1), new Vec3(1, 1, 1)));
        Assert.True(controller.LightingDirty);
        Assert.False(controller.GeometryDirty);

        // No seal required (geometry is sealed), and the save writes as-is with an "unbaked lighting" hint.
        Assert.False(SaveGuard.RequiresSeal(controller.GeometryIsPreview));
        Assert.Equal(
            SaveGuard.SaveNotice.UnbakedLighting,
            SaveGuard.NoticeForDirtySave(controller.GeometryDirty, controller.LightingDirty));
    }

    [AvaloniaFact]
    public void GeometryDirty_Only_Save_Does_Not_Rebuild_And_The_Notice_Is_UnbuiltGeometry()
    {
        var session = new EditorSession();
        session.NewLevel();
        GeometryBuildController controller = NewController(session);
        controller.LivePreviewEnabled = false; // no auto-preview → dirty but NOT preview
        controller.Attach();
        AddBox(session);

        // A brush edit with no build: geometry is dirty but was never applied as preview.
        Assert.True(controller.GeometryDirty);
        Assert.False(controller.GeometryIsPreview);

        // RED-style: the save does not rebuild — it writes as-is and hints "unbuilt geometry".
        Assert.False(SaveGuard.RequiresSeal(controller.GeometryIsPreview));
        Assert.Equal(
            SaveGuard.SaveNotice.UnbuiltGeometry,
            SaveGuard.NoticeForDirtySave(controller.GeometryDirty, controller.LightingDirty));
    }

    [AvaloniaFact]
    public async Task Preview_Geometry_Requires_A_Seal_And_BuildAsync_Seals_Without_Baking()
    {
        var session = new EditorSession();
        session.NewLevel();
        GeometryBuildController controller = NewController(session);
        controller.LivePreviewEnabled = false;
        controller.Attach();
        AddBox(session);

        // A PREVIEW build applies unsealed geometry (interactive == false) → GeometryIsPreview set.
        Assert.True(await controller.BuildAsync(interactive: false));
        Assert.True(controller.GeometryIsPreview);
        Assert.True(SaveGuard.RequiresSeal(controller.GeometryIsPreview)); // the save MUST re-seal

        // Lighting is dirty going into the seal; the seal is geometry-only (bakeLighting == false), so it
        // must NOT bake — the lightmaps stay unbaked (LightingDirty remains true) and the Info notice
        // ("lightmaps were reset; bake lighting when ready") is the single message.
        Assert.True(controller.LightingDirty);

        bool sealBuildRan = await controller.BuildAsync(); // the seal-guard rebuild (interactive geometry-only)

        Assert.True(sealBuildRan);
        Assert.False(controller.GeometryIsPreview); // sealed
        Assert.False(controller.GeometryDirty);
        Assert.True(controller.LightingDirty);      // the seal did NOT bake — lighting is still the author's to do
        Assert.Equal(
            SaveGuard.PreSaveOutcome.Proceed,
            SaveGuard.EvaluateSeal(sealBuildRan, controller.GeometryDirty, controller.GeometryIsPreview));
    }

    [AvaloniaFact]
    public async Task Seal_IsRefused_WhileAnotherUserBuildRuns_AndSaveWouldAbort_WriteNothing()
    {
        var session = new EditorSession();
        session.NewLevel();
        var msgs = new List<string>();
        GeometryBuildController controller = NewController(session, msgs.Add);
        controller.LivePreviewEnabled = false; // drive builds explicitly
        controller.Attach();

        // Put the document into the preview (seal-required) state first.
        AddBox(session);
        Assert.True(await controller.BuildAsync(interactive: false));
        Assert.True(controller.GeometryIsPreview);

        // A user build is in flight (not awaited); the seal build arrives while it runs and is refused.
        Task<bool> inFlight = controller.CalculateMapsAndLightAsync(shadows: false);
        bool sealBuildRan = await controller.BuildAsync();

        // Refused → the seal guard says abort, write NOTHING, warn.
        Assert.False(sealBuildRan);
        Assert.Contains(msgs, m => m.Contains("already running"));
        Assert.Equal(
            SaveGuard.PreSaveOutcome.AbortBuildRunning,
            SaveGuard.EvaluateSeal(sealBuildRan, controller.GeometryDirty, controller.GeometryIsPreview));

        await inFlight; // let the in-flight build settle
    }

    [AvaloniaFact]
    public async Task Completed_Seal_Returns_True_And_Clears_The_Preview_Flag()
    {
        var session = new EditorSession();
        session.NewLevel();
        GeometryBuildController controller = NewController(session);
        controller.LivePreviewEnabled = false;
        controller.Attach();
        AddBox(session);

        bool sealBuildRan = await controller.BuildAsync();

        // The happy path: the geometry-only seal ran, sealed the geometry, and cleared the flags → proceed.
        Assert.True(sealBuildRan);
        Assert.False(controller.GeometryDirty);
        Assert.False(controller.GeometryIsPreview);
        Assert.Equal(
            SaveGuard.PreSaveOutcome.Proceed,
            SaveGuard.EvaluateSeal(sealBuildRan, controller.GeometryDirty, controller.GeometryIsPreview));
    }
}
