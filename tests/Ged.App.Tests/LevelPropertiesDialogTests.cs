using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Ged.App.Dialogs;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Level Properties as Alpine RED's six tabs: the tabs and their controls, every field round-tripping through the
/// shared accessor on OK as ONE undo step (Cancel writes nothing), the read-only date and RFL version, the Level Flags
/// buttons, Set from camera, the lightmap enables, the sun's mesh-mode checkbox and the minimap checks on OK.
/// </summary>
public sealed class LevelPropertiesDialogTests
{
    private static (EditorDocument Doc, LevelPropertiesDialog Dialog) Open(LevelPropertiesServices? services = null, bool stock = false)
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        if (stock)
        {
            doc.Rfl.Sections.RemoveAll(s => s.TypeId == (uint)Ged.Core.IO.Rfl.SectionType.AlpineLevelProperties);
        }

        doc.MarkSaved();
        var dlg = new LevelPropertiesDialog(doc, services);
        dlg.Show();
        ShowAllTabs(dlg);
        return (doc, dlg);
    }

    /// <summary>Visits every tab so its controls are in the visual tree.</summary>
    private static void ShowAllTabs(LevelPropertiesDialog dlg)
    {
        for (int i = 0; i < dlg.Tabs.ItemCount; i++)
        {
            dlg.Tabs.SelectedIndex = i;
            dlg.UpdateLayout();
        }

        dlg.Tabs.SelectedIndex = 0;
        dlg.UpdateLayout();
    }

    private static T Named<T>(LevelPropertiesDialog dlg, string name)
        where T : Control
    {
        for (int i = 0; i < dlg.Tabs.ItemCount; i++)
        {
            var tab = (TabItem)dlg.Tabs.Items[i]!;
            if (((ScrollViewer)tab.Content!).Content is Control page &&
                new[] { page }.Concat(page.GetLogicalDescendants()).OfType<T>().FirstOrDefault(c => c.Name == name) is { } found)
            {
                return found;
            }
        }

        return dlg.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    }

    private static object? Get(EditorDocument doc, string key) =>
        LevelPropertyAccess.Get(doc, LevelPropertiesSchema.Fields.Concat(AlpineLevelPropertiesSchema.ScriptFields)
            .First(f => f.Field.EffectiveKey == key && !f.Field.ReadOnly));

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void The_Six_Tabs_Are_Alpine_REDs_In_Order()
    {
        (_, LevelPropertiesDialog dlg) = Open();
        string[] headers = dlg.Tabs.Items.Cast<TabItem>().Select(t => (string)t.Header!).ToArray();
        Assert.Equal(new[] { "General", "Lighting", "Lightmaps", "Gameplay", "Minimap", "Compatibility" }, headers);
        Assert.Equal("Level Properties", dlg.Title);
        dlg.Close();
    }

    [AvaloniaFact]
    public void The_Date_And_RFL_Version_Are_Shown_Not_Edited()
    {
        (EditorDocument doc, LevelPropertiesDialog dlg) = Open();
        LevelInfoSection info = doc.Rfl.Sections.Select(s => s.Content).OfType<LevelInfoSection>().Single();
        Assert.Equal(info.Date, Named<TextBlock>(dlg, "level_date").Text);
        Assert.Equal(doc.Rfl.Header.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), Named<TextBlock>(dlg, "rfl_version").Text);
        Assert.DoesNotContain(dlg.GetLogicalDescendants().OfType<TextBox>(), b => b.Text == info.Date);
        dlg.Close();
    }

    [AvaloniaFact]
    public void Every_Field_Round_Trips_On_OK_As_One_Undo_Step()
    {
        (EditorDocument doc, LevelPropertiesDialog dlg) = Open(new LevelPropertiesServices { PickColor = _ => Task.FromResult<RfColor?>(new RfColor(9, 8, 7, 255)) });
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int nodes = doc.Undo.NodeCount;

        // General
        Named<TextBox>(dlg, "level_name").Text = "Supply Line";
        Named<TextBox>(dlg, "author").Text = "Glacier test";
        Named<CheckBox>(dlg, "multiplayer").IsChecked = true;
        Named<TextBox>(dlg, "geomod_texture").Text = "rock05.tga";
        Named<NumericUpDown>(dlg, "hardness").Value = 0;
        Named<CheckBox>(dlg, "rf2_style_geomod").IsChecked = true;
        Named<CheckBox>(dlg, "require_d3d11").IsChecked = true;

        // Lighting
        TextBox ambient = Named<TextBox>(dlg, "ambient_color");
        ambient.Text = "<150, 130, 100>";
        ambient.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Click(Named<Button>(dlg, "fog_color_change")); // the injected picker gives <9, 8, 7>
        Named<NumericUpDown>(dlg, "fog_near").Value = 5;
        Named<NumericUpDown>(dlg, "fog_far").Value = 150;
        Named<NumericUpDown>(dlg, "camera_far_clip").Value = 400;
        Named<CheckBox>(dlg, "override_static_mesh_ambient").IsChecked = true;
        Named<NumericUpDown>(dlg, "static_mesh_ambient").Value = 1.5m;
        Named<CheckBox>(dlg, "sun_enabled").IsChecked = true;
        Named<NumericUpDown>(dlg, "sun_yaw").Value = 45;
        Named<NumericUpDown>(dlg, "sun_pitch").Value = 30;
        Named<NumericUpDown>(dlg, "sun_intensity").Value = 2;
        Named<NumericUpDown>(dlg, "sun_spread_angle").Value = 3;
        TextBox sunColor = Named<TextBox>(dlg, "sun_color");
        sunColor.Text = "200, 190, 180";
        sunColor.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Named<CheckBox>(dlg, "sun_cast_baked_shadows").IsChecked = false;
        Named<CheckBox>(dlg, "sun_affects_meshes").IsChecked = false;
        Named<CheckBox>(dlg, "sun_mesh_mode").IsChecked = false;
        Named<CheckBox>(dlg, "sun_drives_shadowmap_dir").IsChecked = false;
        Named<CheckBox>(dlg, "sun_liquid_occludes").IsChecked = false;

        // Lightmaps
        Named<CheckBox>(dlg, "highres_lightmaps").IsChecked = true;
        Named<CheckBox>(dlg, "invisible_faces_occlude").IsChecked = true;
        Named<CheckBox>(dlg, "alpha_faces_occlude").IsChecked = true;
        Named<CheckBox>(dlg, "meshes_occlude").IsChecked = true;
        Named<CheckBox>(dlg, "alpha_tested_occlusion").IsChecked = true;
        Named<CheckBox>(dlg, "d3d11_only_lightmaps").IsChecked = true;
        ComboBox density = Named<ComboBox>(dlg, "lightmap_density");
        density.SelectedIndex = ((IEnumerable<object>)density.ItemsSource!).Cast<string>().ToList().IndexOf("16");
        Named<ComboBox>(dlg, "lightmap_compression").SelectedIndex = 2;

        // Gameplay
        Named<CheckBox>(dlg, "starts_with_headlamp").IsChecked = false;
        Named<CheckBox>(dlg, "vehicle_flight_ceiling_enabled").IsChecked = true;
        Named<NumericUpDown>(dlg, "vehicle_flight_ceiling").Value = 64;
        Named<NumericUpDown>(dlg, "payload_push_speed").Value = 2.5m;
        Named<NumericUpDown>(dlg, "payload_checkpoint_time_bonus").Value = 120;
        Named<NumericUpDown>(dlg, "payload_cart_accel").Value = 3;
        Named<NumericUpDown>(dlg, "payload_overtime_grace").Value = 10;
        Named<NumericUpDown>(dlg, "payload_rollback_speed").Value = 1;
        Named<NumericUpDown>(dlg, "payload_heal_per_second").Value = 20;
        Named<NumericUpDown>(dlg, "payload_attacker_respawn_base").Value = 1;
        Named<NumericUpDown>(dlg, "payload_attacker_respawn_per_checkpoint").Value = 2;
        Named<NumericUpDown>(dlg, "payload_attacker_respawn_final_stretch").Value = 3;
        Named<NumericUpDown>(dlg, "payload_defender_respawn_base").Value = 4;
        Named<NumericUpDown>(dlg, "payload_defender_respawn_per_checkpoint").Value = 5;
        Named<NumericUpDown>(dlg, "payload_defender_respawn_final_stretch").Value = 6;

        // Minimap
        Named<CheckBox>(dlg, "minimap_enabled").IsChecked = true;
        Named<TextBox>(dlg, "minimap_bitmap").Text = "mm_supply.tga";
        Named<NumericUpDown>(dlg, "minimap_min_x").Value = -50;
        Named<NumericUpDown>(dlg, "minimap_min_z").Value = -40;
        Named<NumericUpDown>(dlg, "minimap_max_x").Value = 50;
        Named<NumericUpDown>(dlg, "minimap_max_z").Value = 60;
        Named<NumericUpDown>(dlg, "minimap_cut_height").Value = 12;

        // Compatibility
        Named<CheckBox>(dlg, "legacy_cyclic_timers").IsChecked = true;
        Named<CheckBox>(dlg, "legacy_movers").IsChecked = true;
        Named<CheckBox>(dlg, "legacy_lighting").IsChecked = true;

        Assert.Equal(nodes, doc.Undo.NodeCount); // nothing is written before OK
        Click(Named<Button>(dlg, "ok"));
        Assert.True(dlg.Accepted, dlg.ErrorText);
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);

        var expected = new (string Key, object Value)[]
        {
            ("level_name", "Supply Line"), ("author", "Glacier test"), ("multiplayer", true), ("geomod_texture", "rock05.tga"), ("hardness", 0),
            ("rf2_style_geomod", true), ("require_d3d11_setting", true), ("ambient_color", new RfColor(150, 130, 100, 255)),
            ("fog_color", new RfColor(9, 8, 7, 255)), ("fog_near", 5f), ("fog_far", 150f), ("camera_far_clip", 400f),
            ("override_static_mesh_ambient", true), ("static_mesh_ambient", 1.5f), ("sun_enabled", true), ("sun_yaw", 45f), ("sun_pitch", 30f),
            ("sun_intensity", 2f), ("sun_spread_angle", 3f), ("sun_red", 200), ("sun_green", 190), ("sun_blue", 180),
            ("sun_cast_baked_shadows", false), ("sun_affects_meshes", false), ("sun_mesh_mode", 1), ("sun_drives_shadowmap_dir", false),
            ("sun_liquid_occludes", false), ("highres_lightmaps", true), ("invisible_faces_occlude", true), ("alpha_faces_occlude", true),
            ("meshes_occlude", true), ("alpha_tested_occlusion", true), ("d3d11_only_lightmaps_setting", true), ("lightmap_density", 16),
            ("lightmap_compression", 2), ("starts_with_headlamp", false), ("vehicle_flight_ceiling_enabled", true), ("vehicle_flight_ceiling", 64f),
            ("payload_push_speed", 2.5f), ("payload_checkpoint_time_bonus", 120f), ("payload_cart_accel", 3f), ("payload_overtime_grace", 10f),
            ("payload_rollback_speed", 1f), ("payload_heal_per_second", 20f), ("payload_attacker_respawn_base", 1f),
            ("payload_attacker_respawn_per_checkpoint", 2f), ("payload_attacker_respawn_final_stretch", 3f), ("payload_defender_respawn_base", 4f),
            ("payload_defender_respawn_per_checkpoint", 5f), ("payload_defender_respawn_final_stretch", 6f), ("minimap_enabled", true),
            ("minimap_bitmap", "mm_supply.tga"), ("minimap_cut_height", 12f), ("legacy_cyclic_timers", true), ("legacy_movers", true),
            ("legacy_lighting", true),
        };
        foreach ((string key, object value) in expected)
        {
            Assert.True(Equals(value, Get(doc, key)), $"{key}: expected {value}, got {Get(doc, key)}");
        }

        var min = (Vec3)Get(doc, "minimap_world_min")!;
        var max = (Vec3)Get(doc, "minimap_world_max")!;
        Assert.Equal((-50f, -40f, 50f, 60f), (min.X, min.Z, max.X, max.Z));

        doc.Undo.Undo(); // one step takes it all back
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [AvaloniaFact]
    public void Cancel_Writes_Nothing()
    {
        (EditorDocument doc, LevelPropertiesDialog dlg) = Open();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        Named<TextBox>(dlg, "level_name").Text = "changed";
        Named<CheckBox>(dlg, "legacy_movers").IsChecked = true;
        Click(Named<Button>(dlg, "cancel"));
        Assert.False(dlg.Accepted);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [AvaloniaFact]
    public void OK_With_No_Change_Adds_No_Undo_Step_And_A_Stock_Level_Keeps_No_Alpine_Section()
    {
        (EditorDocument doc, LevelPropertiesDialog dlg) = Open(stock: true);
        int nodes = doc.Undo.NodeCount;
        Assert.True(Named<CheckBox>(dlg, "starts_with_headlamp").IsChecked); // the stock behaviour without the chunk
        Assert.True(dlg.Apply());
        Assert.Equal(nodes, doc.Undo.NodeCount);
        Assert.DoesNotContain(doc.Rfl.Sections, s => s.Content is AlpineLevelPropertiesSection);
        dlg.Close();
    }

    [AvaloniaFact]
    public void Ambient_Default_Restores_REDs_New_Level_Ambient()
    {
        (EditorDocument doc, LevelPropertiesDialog dlg) = Open();
        TextBox ambient = Named<TextBox>(dlg, "ambient_color");
        ambient.Text = "<1, 2, 3>";
        ambient.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Click(Named<Button>(dlg, "ambient_default"));
        Assert.Equal("<40, 40, 40>", ambient.Text);
        Assert.True(dlg.Apply());
        Assert.Equal(new RfColor(40, 40, 40, 255), Get(doc, "ambient_color"));
    }

    [AvaloniaFact]
    public void Outside_And_Inside_Flag_Every_Room_At_Once_After_The_Warning()
    {
        string? asked = null;
        (EditorDocument doc, LevelPropertiesDialog dlg) = Open(new LevelPropertiesServices
        {
            Confirm = (_, message) => { asked = message; return Task.FromResult(true); },
        });
        RflSectionHelper.AddRoomEffect(doc);
        RoomEffect fx = doc.Rfl.Sections.Select(s => s.Content).OfType<RoomEffectsSection>().Single().Effects[0];
        Click(Named<Button>(dlg, "level_outside"));
        Assert.Contains("outside, negating the effects of all Room Effects", asked, StringComparison.Ordinal);
        Assert.Equal((byte)1, fx.RoomIsOutside);
        Click(Named<Button>(dlg, "level_inside"));
        Assert.Equal((byte)0, fx.RoomIsOutside);
        dlg.Close();
    }

    [AvaloniaFact]
    public void Set_From_Camera_Aims_The_Sun_Along_The_View()
    {
        // Looking down at 30 degrees towards +X: the light travels that way, so the sun is up and behind (-X).
        var look = Vector3.Normalize(new Vector3(MathF.Cos(MathF.PI / 6), -MathF.Sin(MathF.PI / 6), 0));
        (_, LevelPropertiesDialog dlg) = Open(new LevelPropertiesServices { CameraForward = () => look });
        Click(Named<Button>(dlg, "sun_from_camera"));
        Assert.Equal(270m, Named<NumericUpDown>(dlg, "sun_yaw").Value);
        Assert.Equal(30m, Named<NumericUpDown>(dlg, "sun_pitch").Value);
        dlg.Close();
    }

    [AvaloniaFact]
    public void D3D11_Only_And_Alpha_Tested_Grey_Out_With_Legacy_Lighting_Or_Density_Off()
    {
        (_, LevelPropertiesDialog dlg) = Open();
        CheckBox d3d11 = Named<CheckBox>(dlg, "d3d11_only_lightmaps");
        CheckBox alphaTested = Named<CheckBox>(dlg, "alpha_tested_occlusion");
        Assert.True(d3d11.IsEnabled);
        Named<CheckBox>(dlg, "legacy_lighting").IsChecked = true;
        Assert.False(d3d11.IsEnabled);
        Assert.False(alphaTested.IsEnabled);
        Named<CheckBox>(dlg, "legacy_lighting").IsChecked = false;
        ComboBox density = Named<ComboBox>(dlg, "lightmap_density");
        density.SelectedIndex = ((IEnumerable<object>)density.ItemsSource!).Cast<string>().ToList().IndexOf("Off");
        Assert.False(d3d11.IsEnabled);
        Assert.True(alphaTested.IsEnabled);
        Assert.Equal(new[] { "Default (8)", "Off", "2", "4", "8", "16", "32", "64", "128" }, ((IEnumerable<object>)density.ItemsSource!).Cast<string>());
        dlg.Close();
    }

    [AvaloniaFact]
    public void OK_Refuses_An_Enabled_Minimap_Without_A_Bitmap_And_Shows_The_Minimap_Tab()
    {
        (EditorDocument doc, LevelPropertiesDialog dlg) = Open();
        int nodes = doc.Undo.NodeCount;
        Named<CheckBox>(dlg, "minimap_enabled").IsChecked = true;
        Assert.False(dlg.Apply());
        Assert.Equal(4, dlg.Tabs.SelectedIndex);
        Assert.Equal(nodes, doc.Undo.NodeCount);
        Assert.False(Named<Button>(dlg, "minimap_bake").IsEnabled); // the bake is Alpine RED's for now
        Named<TextBox>(dlg, "minimap_bitmap").Text = @"maps\mm.tga";
        Assert.False(dlg.Apply()); // a path, not a bare file name
        dlg.Close();
    }

    [Fact]
    public void Colour_Text_Parses_As_Alpine_RED_Parses_It()
    {
        Assert.Equal(((byte)1, (byte)2, (byte)3), LevelPropertiesDialog.ParseColor("<1, 2, 3>"));
        Assert.Equal(((byte)255, (byte)0, (byte)9), LevelPropertiesDialog.ParseColor("255 0 9"));
        Assert.Null(LevelPropertiesDialog.ParseColor("<256, 0, 0>"));
        Assert.Null(LevelPropertiesDialog.ParseColor("1, 2"));
    }
}

/// <summary>Test-level helpers for room effects.</summary>
internal static class RflSectionHelper
{
    public static void AddRoomEffect(EditorDocument doc)
    {
        var host = doc.Rfl.GetOrCreateSection(Ged.Core.IO.Rfl.SectionType.RoomEffects, () => new RoomEffectsSection());
        ((RoomEffectsSection)host.Content!).Effects.Add(new RoomEffect
        {
            EffectType = RoomEffectsSection.EffectNone,
            Header = new ObjectHeader { Uid = 9100, ClassName = "Room Effect", ScriptName = "Room Effect" },
        });
        host.Dirty = true;
        doc.RefreshObjects();
    }
}
