using System.Collections.Generic;
using System.Linq;
using Ged.App;
using Ged.App.Viewport;
using Ged.Core.Editing;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The per-pane render-option model (items 3 + 4). All render options are GLOBAL — one shared
/// model, toggled from any pane. Item 4 relocated the remaining View-menu render toggles into the
/// dropdown: Show Links / Show Path Node Connections / Show Gizmo / Show Annotations (all view
/// types) and Draw Sky / Show Fog / Room Rendering / Portal Faces (perspective panes only). Item 4
/// also carries the increment settings through the same shared model.
/// </summary>
public sealed class PaneOptionModelTests
{
    private sealed class Fakes
    {
        public readonly AppSettings Settings = new();
        public readonly EditorSession Session = new();
        public int Rebuilds;
        public int Persists;
        public int CullApplies;
        public int EnsureStash;
        public int FogApplies;
        public bool? EmitterAnim;
        public bool GizmoVisible;
        public int GizmoToggles;
        public RoomVisibility RoomMode = RoomVisibility.All;
        public Ged.Rendering.Scene.PortalFaceDrawMode PortalFaces = Ged.Rendering.Scene.PortalFaceDrawMode.None;

        public RenderOptionsModel Build() => RenderOptionsModel.BuildGlobal(
            Settings, Session,
            rebuildScene: () => Rebuilds++,
            persist: () => Persists++,
            applyBackfaceCulling: () => CullApplies++,
            setEmitterAnimation: v => EmitterAnim = v,
            ensureMergedBrushStash: () => EnsureStash++,
            gizmoVisible: () => GizmoVisible,
            toggleGizmo: () => { GizmoVisible = !GizmoVisible; GizmoToggles++; },
            applyFog: () => FogApplies++,
            getRoomMode: () => RoomMode,
            setRoomMode: m => RoomMode = m,
            getPortalFaces: () => PortalFaces,
            setPortalFaces: m => PortalFaces = m);
    }

    // ---- Item 4: relocated toggles + perspective-only scoping ------------------

    [Fact]
    public void Dropdown_Contains_The_Relocated_View_Menu_Toggles()
    {
        RenderOptionsModel model = new Fakes().Build();
        List<string> labels = model.Toggles.Select(t => t.Label).ToList();

        foreach (string relocated in new[]
                 {
                     "Show objects as Bounding Boxes", "Disable Backface Culling",
                     "Show Links", "Show Path Node Connections", "Show Gizmo", "Show Annotations",
                     "Draw Sky (Like in-game)", "Show Fog",
                 })
        {
            Assert.Contains(labels, l => l == relocated);
        }
    }

    [Fact]
    public void Perspective_Only_Toggles_Are_Hidden_In_Ortho_Panes()
    {
        RenderOptionsModel model = new Fakes().Build();

        var persp = model.VisibleToggles(ViewType.Perspective).Select(t => t.Label).ToList();
        var top = model.VisibleToggles(ViewType.Top).Select(t => t.Label).ToList();

        // All-view toggles show in both.
        Assert.Contains("Show Links", persp);
        Assert.Contains("Show Links", top);
        Assert.Contains("Show Gizmo", top);

        // Fog / Sky are perspective-only.
        Assert.Contains("Draw Sky (Like in-game)", persp);
        Assert.Contains("Show Fog", persp);
        Assert.DoesNotContain("Draw Sky (Like in-game)", top);
        Assert.DoesNotContain("Show Fog", top);
    }

    [Fact]
    public void Room_Rendering_And_Portal_Faces_Are_Perspective_Only_Radio_Groups()
    {
        RenderOptionsModel model = new Fakes().Build();

        var perspGroups = model.VisibleRadioGroups(ViewType.Perspective).Select(g => g.Label).ToList();
        Assert.Equal(new[] { "Room Rendering", "Portal Faces" }, perspGroups);

        // Ortho panes show no radio groups (all are perspective-only).
        Assert.Empty(model.VisibleRadioGroups(ViewType.Top));
        Assert.Empty(model.VisibleRadioGroups(ViewType.Front));
    }

    [Fact]
    public void Toggling_Show_Links_Flips_The_Shared_Global_State()
    {
        var f = new Fakes();
        RenderOptionsModel model = f.Build();
        RenderOptionToggle links = model.Toggles.Single(t => t.Label == "Show Links");

        int changed = 0;
        model.Changed += () => changed++;

        model.SetValue(links, false);
        Assert.False(f.Settings.ShowLinks);
        Assert.False(f.Session.ShowLinks);
        Assert.Equal(1, changed);
        Assert.Equal(1, f.Rebuilds);
    }

    [Fact]
    public void Show_Gizmo_Toggle_Routes_Through_The_Gizmo_Command()
    {
        var f = new Fakes();
        RenderOptionsModel model = f.Build();
        RenderOptionToggle gizmo = model.Toggles.Single(t => t.Label == "Show Gizmo");

        Assert.False(gizmo.Value);
        model.SetValue(gizmo, true);
        Assert.True(f.GizmoVisible);
        Assert.Equal(1, f.GizmoToggles);
        Assert.True(gizmo.Value);
    }

    [Fact]
    public void Show_Fog_Toggle_Applies_Fog_Without_A_Scene_Rebuild()
    {
        var f = new Fakes();
        RenderOptionsModel model = f.Build();
        RenderOptionToggle fog = model.Toggles.Single(t => t.Label == "Show Fog");

        model.SetValue(fog, true);
        Assert.True(f.Settings.ShowFog);
        Assert.Equal(1, f.FogApplies);
        Assert.Equal(0, f.Rebuilds);
    }

    [Fact]
    public void Selecting_A_Radio_Option_Sets_The_Shared_Value_And_Notifies()
    {
        var f = new Fakes();
        RenderOptionsModel model = f.Build();

        RenderOptionRadioGroup rooms = model.RadioGroups.Single(g => g.Label == "Room Rendering");
        RenderOptionRadioOption portals = rooms.Options[1]; // "Render Using Portals"

        int changed = 0;
        model.Changed += () => changed++;

        model.SelectRadio(portals);
        Assert.Equal(RoomVisibility.Portals, f.RoomMode);
        Assert.True(portals.IsChecked);
        Assert.Equal(1, changed);

        // Idempotent: selecting the already-checked option is a no-op (no notify storm).
        model.SelectRadio(portals);
        Assert.Equal(1, changed);

        RenderOptionRadioGroup portalFaces = model.RadioGroups.Single(g => g.Label == "Portal Faces");
        model.SelectRadio(portalFaces.Options[1]); // See-thru
        Assert.Equal(Ged.Rendering.Scene.PortalFaceDrawMode.SeeThru, f.PortalFaces);
    }

    // ---- Show Event Arrows toggle (directional-event facing arrows) -------------

    [Fact]
    public void Show_Event_Arrows_Toggle_Is_Present_On_All_Views_And_Defaults_On()
    {
        RenderOptionsModel model = new Fakes().Build();
        RenderOptionToggle arrows = model.Toggles.Single(t => t.Label == "Show Event Arrows");

        Assert.Equal(ViewScope.AllViews, arrows.Scope);
        Assert.True(arrows.Value); // default on, like its Show Links / Show Annotations siblings

        var top = model.VisibleToggles(ViewType.Top).Select(t => t.Label).ToList();
        var persp = model.VisibleToggles(ViewType.Perspective).Select(t => t.Label).ToList();
        Assert.Contains("Show Event Arrows", top);
        Assert.Contains("Show Event Arrows", persp);
    }

    [Fact]
    public void Toggling_Show_Event_Arrows_Flips_Shared_State_Persists_And_Invalidates_The_Scene()
    {
        var f = new Fakes();
        RenderOptionsModel model = f.Build();
        RenderOptionToggle arrows = model.Toggles.Single(t => t.Label == "Show Event Arrows");

        int changed = 0;
        model.Changed += () => changed++;

        model.SetValue(arrows, false);
        Assert.False(f.Settings.ShowEventArrows); // persisted setting flipped
        Assert.False(f.Session.ShowEventArrows);  // live scene state flipped
        Assert.Equal(1, f.Rebuilds);              // scene invalidated, like Show Links
        Assert.Equal(1, f.Persists);              // written back to settings
        Assert.Equal(1, changed);
    }

    // ---- Draw Decals toggle (perspective-only, default off) --------------------

    [Fact]
    public void Draw_Decals_Toggle_Is_Perspective_Only_And_Defaults_Off()
    {
        RenderOptionsModel model = new Fakes().Build();
        RenderOptionToggle decals = model.Toggles.Single(t => t.Label == "Draw Decals");

        Assert.Equal(ViewScope.PerspectiveOnly, decals.Scope);
        Assert.False(decals.Value); // default OFF, like its perspective-only Draw Sky / Show Fog siblings

        var persp = model.VisibleToggles(ViewType.Perspective).Select(t => t.Label).ToList();
        var top = model.VisibleToggles(ViewType.Top).Select(t => t.Label).ToList();
        Assert.Contains("Draw Decals", persp);
        Assert.DoesNotContain("Draw Decals", top);
    }

    [Fact]
    public void Toggling_Draw_Decals_Flips_Shared_State_Persists_And_Rebuilds()
    {
        var f = new Fakes();
        RenderOptionsModel model = f.Build();
        RenderOptionToggle decals = model.Toggles.Single(t => t.Label == "Draw Decals");

        model.SetValue(decals, true);
        Assert.True(f.Settings.DrawDecals);  // persisted setting flipped
        Assert.True(f.Session.DrawDecals);   // live scene state flipped
        Assert.Equal(1, f.Rebuilds);         // scene re-emitted (projection recomputed only on rebuild)
        Assert.Equal(1, f.Persists);
    }

    // ---- Item 5: "Render using portals" label drops the "(Like in-game)" suffix -

    [Fact]
    public void Room_Rendering_Portal_Option_Label_Drops_The_Like_In_Game_Suffix()
    {
        RenderOptionsModel model = new Fakes().Build();
        RenderOptionRadioGroup rooms = model.RadioGroups.Single(g => g.Label == "Room Rendering");
        var labels = rooms.Options.Select(o => o.Label).ToList();

        Assert.Contains("Render Using Portals", labels);
        Assert.DoesNotContain(labels, l => l.Contains("Like in-game"));
        Assert.DoesNotContain(labels, l => l.Contains("(Like in-game)"));
    }

    // ---- Carried-over toggles (item 3) -----------------------------------------

    [Fact]
    public void Backface_Culling_Toggle_Applies_Raster_State_Without_Scene_Rebuild()
    {
        var f = new Fakes();
        RenderOptionsModel model = f.Build();
        RenderOptionToggle cull = model.Toggles.Single(t => t.Label == "Disable Backface Culling");

        model.SetValue(cull, true);
        Assert.True(f.Settings.DisableBackfaceCulling);
        Assert.Equal(1, f.CullApplies);
        Assert.Equal(0, f.Rebuilds);
    }

    [Fact]
    public void Animate_Emitters_Toggle_Starts_And_Stops_The_Animation_Clock()
    {
        var f = new Fakes();
        RenderOptionsModel model = f.Build();
        RenderOptionToggle anim = model.Toggles.Single(t => t.Label == "Animate Emitters");

        model.SetValue(anim, true);
        Assert.True(f.Session.AnimateEmitters);
        Assert.True(f.EmitterAnim);

        model.SetValue(anim, false);
        Assert.False(f.Session.AnimateEmitters);
        Assert.False(f.EmitterAnim);
    }

    // ---- Item 4: increment setting propagation ---------------------------------

    [Fact]
    public void Increment_Setting_Propagates_To_All_Consumers_Through_One_Value()
    {
        var settings = new AppSettings();
        var snap = new SnapPolicy();
        var setting = new IncrementSetting(
            "Grid", " m", SnapIncrements.GridPresets,
            () => settings.GridSize,
            v => { settings.GridSize = v; snap.GridSize = v; },
            SnapIncrements.TryParseGrid,
            hotkeyLadder: SnapIncrements.GridLadder);

        int changed = 0;
        setting.Changed += () => changed++;

        setting.SetValue(0.25f);
        Assert.Equal(0.25f, settings.GridSize);
        Assert.Equal(0.25f, snap.GridSize);
        Assert.Equal(1, changed);

        setting.StepUp();
        Assert.Equal(0.5f, settings.GridSize);
        Assert.Equal(0.5f, snap.GridSize);

        setting.StepDown();
        setting.StepDown();
        Assert.Equal(0.125f, settings.GridSize, 4);

        setting.SetValue(8f);
        setting.StepUp();
        Assert.Equal(16f, settings.GridSize);
        Assert.DoesNotContain(16f, setting.Presets);
    }

    [Fact]
    public void Increment_Free_Entry_Validates_Before_Applying()
    {
        var settings = new AppSettings { RotationStep = 15f };
        var setting = new IncrementSetting(
            "Rot", "°", SnapIncrements.RotationPresets,
            () => settings.RotationStep,
            v => settings.RotationStep = v,
            SnapIncrements.TryParseRotation);

        int changed = 0;
        setting.Changed += () => changed++;

        Assert.False(setting.TrySetFromText("garbage"));
        Assert.False(setting.TrySetFromText("-5"));
        Assert.Equal(15f, settings.RotationStep);
        Assert.Equal(0, changed);

        Assert.True(setting.TrySetFromText("22.5"));
        Assert.Equal(22.5f, settings.RotationStep);
        Assert.Equal(1, changed);
    }
}
