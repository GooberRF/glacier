using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 3 regression coverage: with Preview Lighting enabled, light-affecting document
/// changes (property edits on selected lights, light add/delete) must schedule the
/// debounced incremental relight, with the changed light's influence AABB recorded so
/// only overlapping surfaces re-bake. Non-light edits and disabled preview must not.
/// </summary>
public class PreviewLightingTests
{
    private static (EditorSession Session, GeometryBuildController Controller) NewSession()
    {
        var session = new EditorSession();
        session.NewLevel();
        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
        controller.Attach();
        return (session, controller);
    }

    [AvaloniaFact]
    public void Light_Property_Edit_Schedules_A_Relight_With_The_Lights_Region()
    {
        (EditorSession session, GeometryBuildController c) = NewSession();
        c.PreviewLightingEnabled = true;
        EditorDocument doc = session.Document!;

        LevelObject lo = doc.PlaceObject(LevelObjectKind.Light, new Vec3(10f, 2f, -4f))!;
        var light = (Light)lo.Model;
        light.Range = 6f;
        doc.Select(lo);

        // Inspector-style undo-safe property edit → DirtyChanged → debounce armed.
        doc.EditValue(lo.Section, "Edit Range", 6f, 8f, v => light.Range = v);

        Assert.True(c.AutoRelightPending, "a light edit with preview on must arm the relight debounce");
        Assert.True(c.LightingDirty);

        // The scheduled relight targets THIS light: its influence AABB (position ± range).
        Assert.True(c.PendingLightRegion.HasValue);
        Aabb region = c.PendingLightRegion.Value;
        Assert.True(region.P1.X <= 10f - 8f + 0.01f && region.P2.X >= 10f + 8f - 0.01f, $"region {region.P1}..{region.P2} misses X extent");
        Assert.True(region.P1.Y <= 2f - 8f + 0.01f && region.P2.Y >= 2f + 8f - 0.01f, $"region {region.P1}..{region.P2} misses Y extent");
        Assert.True(region.P1.Z <= -4f - 8f + 0.01f && region.P2.Z >= -4f + 8f - 0.01f, $"region {region.P1}..{region.P2} misses Z extent");
    }

    [AvaloniaFact]
    public void Light_Move_Through_The_Document_Schedules_A_Relight()
    {
        (EditorSession session, GeometryBuildController c) = NewSession();
        EditorDocument doc = session.Document!;
        LevelObject lo = doc.PlaceObject(LevelObjectKind.Light, new Vec3(0f, 0f, 0f))!;
        doc.Select(lo);
        c.PreviewLightingEnabled = true;

        // Gizmo/keyboard moves commit through the undo stack exactly like this.
        doc.EditValue(lo.Section, "Move light", new Vec3(0f, 0f, 0f), new Vec3(3f, 0f, 0f), v => lo.Position = v);

        Assert.True(c.AutoRelightPending);
        Assert.True(c.PendingLightRegion.HasValue);
    }

    [AvaloniaFact]
    public void Light_Add_And_Delete_Schedule_A_Relight()
    {
        (EditorSession session, GeometryBuildController c) = NewSession();
        EditorDocument doc = session.Document!;
        c.PreviewLightingEnabled = true;

        // Add (unselected, e.g. palette place): the light count change schedules.
        LevelObject lo = doc.PlaceObject(LevelObjectKind.Light, new Vec3(1f, 2f, 3f))!;
        Assert.True(c.AutoRelightPending, "adding a light must schedule a relight");

        // Delete: the count change schedules again (full relight covers the removed influence).
        (EditorSession s2, GeometryBuildController c2) = NewSession();
        EditorDocument doc2 = s2.Document!;
        LevelObject l2 = doc2.PlaceObject(LevelObjectKind.Light, new Vec3(0f, 0f, 0f))!;
        c2.PreviewLightingEnabled = true;
        doc2.Select(l2);
        doc2.DeleteSelection();
        Assert.True(c2.AutoRelightPending, "deleting a light must schedule a relight");
        Assert.True(c2.LightingDirty);
    }

    [AvaloniaFact]
    public void Non_Light_Edits_Do_Not_Schedule_A_Relight()
    {
        (EditorSession session, GeometryBuildController c) = NewSession();
        EditorDocument doc = session.Document!;
        c.PreviewLightingEnabled = true;

        LevelObject clutter = doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(5f, 0f, 5f))!;
        Assert.False(c.AutoRelightPending, "placing non-light objects must not schedule a relight");

        doc.Select(clutter);
        doc.EditValue(clutter.Section, "Move clutter", new Vec3(5f, 0f, 5f), new Vec3(6f, 0f, 5f), v => clutter.Position = v);
        Assert.False(c.AutoRelightPending, "editing non-light objects must not schedule a relight");
    }

    [AvaloniaFact]
    public void Disabled_Preview_Never_Schedules()
    {
        (EditorSession session, GeometryBuildController c) = NewSession();
        EditorDocument doc = session.Document!;
        Assert.False(c.PreviewLightingEnabled); // default off

        LevelObject lo = doc.PlaceObject(LevelObjectKind.Light, new Vec3(0f, 0f, 0f))!;
        doc.Select(lo);
        doc.EditValue(lo.Section, "Edit", 0f, 1f, _ => { });

        Assert.False(c.AutoRelightPending, "preview off must leave relights fully manual");
        Assert.True(c.LightingDirty); // dirty tracking itself still works
    }
}
