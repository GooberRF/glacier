using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Tables;

/// <summary>Where a level property is stored.</summary>
public enum LevelPropertyTarget
{
    /// <summary>The RFL file header.</summary>
    Header,

    /// <summary>The <c>level_info</c> section.</summary>
    LevelInfo,

    /// <summary>The stock <c>level_properties</c> section.</summary>
    LevelProperties,

    /// <summary>The <c>alpine_level_properties</c> section (created on first edit).</summary>
    AlpineLevelProperties,
}

/// <summary>
/// One level property: its inspector field (label, path into the target section, editor kind,
/// range, key), where it lives, and the value reported while its section is absent (Alpine only).
/// <see cref="UndoDescription"/> is the undo label the Level Properties dialog has always used.
/// </summary>
public sealed record LevelPropertyField(InspectorField Field, LevelPropertyTarget Target, object? DefaultWhenAbsent = null)
{
    /// <summary>The undo-step label for an edit of this property.</summary>
    public string UndoDescription { get; init; } = Field.Label;
}

/// <summary>
/// The stock level properties (Level Properties dialog "Info" and "Stock" groups, plus the
/// read-only RFL version), in display order. The dialog and <c>level.props</c> render from this list.
/// </summary>
public static class LevelPropertiesSchema
{
    public static IReadOnlyList<LevelPropertyField> Fields { get; } = new[]
    {
        new LevelPropertyField(new InspectorField("Level Name", "LevelName", InspectorEditor.Text), LevelPropertyTarget.LevelInfo) { UndoDescription = "Level name" },
        new LevelPropertyField(new InspectorField("Author", "Author", InspectorEditor.Text), LevelPropertyTarget.LevelInfo) { UndoDescription = "Author" },
        new LevelPropertyField(new InspectorField("Date", "Date", InspectorEditor.Text) { ReadOnly = true, Note = "Set automatically on save." }, LevelPropertyTarget.LevelInfo),
        new LevelPropertyField(new InspectorField("Multiplayer Level", "MultiplayerLevel", InspectorEditor.Bool) { Key = "multiplayer" }, LevelPropertyTarget.LevelInfo) { UndoDescription = "Multiplayer" },
        new LevelPropertyField(new InspectorField("Default Geomod Texture", "GeomodTexture", InspectorEditor.Text) { Key = "geomod_texture" }, LevelPropertyTarget.LevelProperties) { UndoDescription = "Geomod texture" },
        new LevelPropertyField(new InspectorField("Hardness (0–100, 0 allowed [Alpine])", "Hardness", InspectorEditor.Int) { Key = "hardness", Min = 0, Max = 100 }, LevelPropertyTarget.LevelProperties) { UndoDescription = "Hardness" },
        new LevelPropertyField(new InspectorField("Ambient Light", "AmbientColor", InspectorEditor.Color) { Key = "ambient_color" }, LevelPropertyTarget.LevelProperties) { UndoDescription = "Ambient color" },
        new LevelPropertyField(new InspectorField("Fog Color", "FogColor", InspectorEditor.Color), LevelPropertyTarget.LevelProperties) { UndoDescription = "Fog color" },
        new LevelPropertyField(new InspectorField("Fog Near Plane (PS2 only)", "FogNearPlane", InspectorEditor.Float) { Key = "fog_near" }, LevelPropertyTarget.LevelProperties) { UndoDescription = "Fog near" },
        new LevelPropertyField(new InspectorField("Fog Far Plane", "FogFarPlane", InspectorEditor.Float) { Key = "fog_far" }, LevelPropertyTarget.LevelProperties) { UndoDescription = "Fog far" },
        new LevelPropertyField(new InspectorField("RFL Version", "Version", InspectorEditor.Int) { Key = "rfl_version", ReadOnly = true, Note = "Saves always write the Alpine version." }, LevelPropertyTarget.Header),
    };
}

/// <summary>The Alpine level properties (Level Properties dialog "Alpine" group), in display order.</summary>
public static class AlpineLevelPropertiesSchema
{
    public static IReadOnlyList<LevelPropertyField> Fields { get; } = new[]
    {
        // Absent-chunk values: what the game and Alpine RED assume for a level without the chunk (LoadDefaults,
        // editor_patch/level.h:1133-1140; game_patch/misc/level.h:59-65) — the stock behaviour, legacy movers and
        // timers included. A new Glacier level carries the chunk with both legacy options off.
        Alpine(new InspectorField("Legacy Cyclic Timers", "LegacyCyclicTimers", InspectorEditor.Bool), true, "Legacy cyclic timers"),
        Alpine(new InspectorField("Legacy Movers", "LegacyMovers", InspectorEditor.Bool) { Note = "Must be off for a Payload cart (Payload_Handler)." }, true, "Legacy movers"),
        Alpine(new InspectorField("Player Starts With Headlamp", "StartsWithHeadlamp", InspectorEditor.Bool) { Key = "starts_with_headlamp" }, true, "Headlamp"),
        Alpine(new InspectorField("Override Static-Mesh Ambient", "OverrideStaticMeshAmbientLightModifier", InspectorEditor.Bool) { Key = "override_static_mesh_ambient" }, false, "SM ambient override"),
        Alpine(new InspectorField("Static-Mesh Ambient Value", "StaticMeshAmbientLightModifier", InspectorEditor.Float) { Key = "static_mesh_ambient" }, 2.0f, "SM ambient value"),
        Alpine(new InspectorField("RF2-Style (brush) Geomod", "Rf2StyleGeomod", InspectorEditor.Bool) { Key = "rf2_style_geomod" }, false, "RF2 geomod"),

        // Payload rules (props v6; Alpine RED's Level Properties "Payload" group, editor_patch/resources.rc:278-299;
        // ranges from the dialog's read_dlg_float calls, level.cpp:2360-2385, and common/include/common/payload.h:13-39).
        Payload("Payload Push Speed (m/s)", nameof(AlpineLevelPropertiesSection.PayloadPushSpeed), "payload_push_speed", 0.1, 20, 1.5f, "Cart speed with one attacker pushing."),
        Payload("Payload Rollback (x push speed)", nameof(AlpineLevelPropertiesSection.PayloadRollbackSpeed), "payload_rollback_speed", 0, 4, 0.5f, "Rollback speed as a fraction of the push speed."),
        Payload("Payload Acceleration (m/s/s)", nameof(AlpineLevelPropertiesSection.PayloadCartAccel), "payload_cart_accel", 0.1, 100, 2.0f, null),
        Payload("Payload Checkpoint Bonus (s)", nameof(AlpineLevelPropertiesSection.PayloadCheckpointTimeBonus), "payload_checkpoint_time_bonus", 0, 3600, 180f, "Round time added when the cart reaches a checkpoint (a checkpoint's own bonus wins)."),
        Payload("Payload Heal + Ammo (HP/s)", nameof(AlpineLevelPropertiesSection.PayloadHealPerSecond), "payload_heal_per_second", 0, 500, 10f, "Given to attackers near the cart."),
        Payload("Payload Overtime Grace (s)", nameof(AlpineLevelPropertiesSection.PayloadOvertimeGrace), "payload_overtime_grace", 0, 60, 5f, null),
        Payload("Payload Attacker Respawn: Base (s)", nameof(AlpineLevelPropertiesSection.PayloadAttackerRespawnBase), "payload_attacker_respawn_base", -60, 60, 0f, "Extra respawn delay for the attackers."),
        Payload("Payload Attacker Respawn: Per Checkpoint (s)", nameof(AlpineLevelPropertiesSection.PayloadAttackerRespawnPerCheckpoint), "payload_attacker_respawn_per_checkpoint", -60, 60, 0f, "Added for each checkpoint the cart has passed."),
        Payload("Payload Attacker Respawn: Final Stretch (s)", nameof(AlpineLevelPropertiesSection.PayloadAttackerRespawnFinalStretch), "payload_attacker_respawn_final_stretch", -60, 60, 0f, "Added once the cart is on the final stretch."),
        Payload("Payload Defender Respawn: Base (s)", nameof(AlpineLevelPropertiesSection.PayloadDefenderRespawnBase), "payload_defender_respawn_base", -60, 60, 1.0f, "Extra respawn delay for the defenders."),
        Payload("Payload Defender Respawn: Per Checkpoint (s)", nameof(AlpineLevelPropertiesSection.PayloadDefenderRespawnPerCheckpoint), "payload_defender_respawn_per_checkpoint", -60, 60, 0.5f, "Added for each checkpoint the cart has passed."),
        Payload("Payload Defender Respawn: Final Stretch (s)", nameof(AlpineLevelPropertiesSection.PayloadDefenderRespawnFinalStretch), "payload_defender_respawn_final_stretch", -60, 60, 1.0f, "Added once the cart is on the final stretch."),
    };

    /// <summary>
    /// The Alpine Faction 1.5 properties (alpine_level_properties chunk versions 5 and 6: lighting, sun, minimap,
    /// vehicles, far clip), editable as Alpine RED's Level Properties tabs edit them (editor_patch/level.cpp:1540-2470;
    /// ranges from SanitizeSunProperties, level.h:1094-1119, the dialog's read_dlg_float calls and
    /// common/include/common/alpine_camera_far_clip.h). Shown in the Level Properties dialog's Alpine 1.5 groups and
    /// reported by <c>level.alpine</c>. The stored bit fields stay read-only raw values; their mapper settings are the
    /// separate <c>*_setting</c> fields (bit0 of each is recomputed on every save). Absent-chunk values are Alpine's
    /// defaults (editor_patch/level.h:1133-1180). How the lighting fields change a bake is the lightmap pipeline's
    /// concern: Glacier's bake reads every one of them (Lighting/Alpine/AlpineLightmapSettings.cs,
    /// AlpineDirectionalLights.cs), as Alpine RED's does.
    /// </summary>
    public static IReadOnlyList<LevelPropertyField> Alpine15Fields { get; } = new[]
    {
        ReadOnly(new InspectorField("Props Chunk Version", "Version", InspectorEditor.Int) { Key = "props_version" }, 0),

        // Lighting (level.h:982-1004; dialog "Lightmaps" tab, level.cpp:2125-2185).
        Alpine15(new InspectorField("Legacy Lighting", "LegacyLighting", InspectorEditor.Bool) { Key = "legacy_lighting", Note = "Off = Alpine's lighting pipeline (the default); on = the stock pipeline." }, false),
        Alpine15(new InspectorField("High-Res Lightmaps", "HighresLightmaps", InspectorEditor.Bool) { Key = "highres_lightmaps" }, false),
        Alpine15(new InspectorField("Lightmap Density", "LightmapDensity", InspectorEditor.Int) { Key = "lightmap_density", Min = 0, Max = 255, Note = "Texels per unit: 0 = default (8), 1–128, 255 = off." }, 0),
        ReadOnly(new InspectorField("D3D11-Only Lightmaps", "D3D11OnlyLightmaps", InspectorEditor.Int) { Key = "d3d11_only_lightmaps", Note = "Bit 0: no stock lightmaps stored; bit 1: the setting." }, 0),
        Alpine15(new InspectorField("D3D11-Only Lightmaps Setting", "D3D11OnlyLightmaps", InspectorEditor.Bool) { Key = "d3d11_only_lightmaps_setting", Mask = AlpineLevelPropertiesSection.D3D11OnlySetting, Note = "Store only Alpine (Direct3D 11) lightmaps when baking in Alpine RED." }, false),
        Alpine15(new InspectorField("Lightmap Compression", "LightmapCompression", InspectorEditor.Enum) { Key = "lightmap_compression", Options = new[] { "Quality", "Balanced", "Compact" } }, 0),
        Alpine15(new InspectorField("Invisible Faces Occlude", "InvisibleFacesOcclude", InspectorEditor.Bool) { Key = "invisible_faces_occlude" }, false),
        Alpine15(new InspectorField("Alpha Faces Occlude", "AlphaFacesOcclude", InspectorEditor.Bool) { Key = "alpha_faces_occlude" }, false),
        Alpine15(new InspectorField("Alpha-Tested Occlusion", "AlphaTestedOcclusion", InspectorEditor.Bool) { Key = "alpha_tested_occlusion" }, false),
        Alpine15(new InspectorField("Meshes Occlude", "MeshesOcclude", InspectorEditor.Bool) { Key = "meshes_occlude" }, false),

        // Sun (level.h:982-994, SanitizeSunProperties :1094-1119).
        Alpine15(new InspectorField("Sun Enabled", "EnableSun", InspectorEditor.Bool) { Key = "sun_enabled" }, false),
        Alpine15(new InspectorField("Sun Yaw", "SunYaw", InspectorEditor.Float) { Key = "sun_yaw", Min = 0, Max = 360, Note = "Degrees around world up." }, 0f),
        Alpine15(new InspectorField("Sun Pitch", "SunPitch", InspectorEditor.Float) { Key = "sun_pitch", Min = 0, Max = 90, Note = "Degrees above the horizon; 90 = overhead." }, 90f),
        Alpine15(new InspectorField("Sun Red", "SunColorR", InspectorEditor.Int) { Key = "sun_red", Min = 0, Max = 255 }, 255),
        Alpine15(new InspectorField("Sun Green", "SunColorG", InspectorEditor.Int) { Key = "sun_green", Min = 0, Max = 255 }, 255),
        Alpine15(new InspectorField("Sun Blue", "SunColorB", InspectorEditor.Int) { Key = "sun_blue", Min = 0, Max = 255 }, 255),
        Alpine15(new InspectorField("Sun Intensity", "SunIntensity", InspectorEditor.Float) { Key = "sun_intensity", Min = 0, Max = 10 }, 1f),
        Alpine15(new InspectorField("Sun Spread Angle", "SunSpreadAngle", InspectorEditor.Float) { Key = "sun_spread_angle", Min = 0, Max = 45, Note = "Penumbra half-angle in degrees for soft baked shadows." }, 0f),
        Alpine15(new InspectorField("Sun Casts Baked Shadows", "SunCastBakedShadows", InspectorEditor.Bool) { Key = "sun_cast_baked_shadows" }, true),
        Alpine15(new InspectorField("Sun Affects Meshes", "SunAffectsMeshes", InspectorEditor.Bool) { Key = "sun_affects_meshes" }, true),
        Alpine15(new InspectorField("Sun Mesh Mode", "SunMeshMode", InspectorEditor.Enum) { Key = "sun_mesh_mode", Options = new[] { "Scaled By Lightmap", "Everywhere" } }, 0),
        Alpine15(new InspectorField("Sun Drives Shadow-Map Direction", "SunDrivesShadowmapDir", InspectorEditor.Bool) { Key = "sun_drives_shadowmap_dir" }, true),
        Alpine15(new InspectorField("Liquid Occludes Sun", "SunLiquidOccludes", InspectorEditor.Bool) { Key = "sun_liquid_occludes" }, true),

        // Level settings (v6; level.h:1040-1063, dialog level.cpp:1640-1725, 2400-2470).
        Alpine15(new InspectorField("Vehicle Flight Ceiling Enabled", "VehicleFlightCeilingEnabled", InspectorEditor.Bool) { Key = "vehicle_flight_ceiling_enabled" }, false),
        Alpine15(new InspectorField("Vehicle Flight Ceiling", "VehicleFlightCeiling", InspectorEditor.Float) { Key = "vehicle_flight_ceiling", Note = "World height aircraft cannot climb above." }, 0f),
        Alpine15(new InspectorField("Minimap Enabled", "MinimapEnabled", InspectorEditor.Bool) { Key = "minimap_enabled" }, false),
        Alpine15(new InspectorField("Minimap Bitmap", "MinimapBitmap", InspectorEditor.Text) { Key = "minimap_bitmap", Note = "A bitmap file name (no path)." }, string.Empty),
        Alpine15(new InspectorField("Minimap World Min", "MinimapWorldMin", InspectorEditor.Vector) { Key = "minimap_world_min", Note = "The world corner the minimap's first pixel covers (X and Z are used)." }, default(Vec3)),
        Alpine15(new InspectorField("Minimap World Max", "MinimapWorldMax", InspectorEditor.Vector) { Key = "minimap_world_max" }, default(Vec3)),
        Alpine15(new InspectorField("Minimap Cut Height", "MinimapCutHeight", InspectorEditor.Float) { Key = "minimap_cut_height" }, 0f),
        Alpine15(new InspectorField("Camera Far Clip", "CameraFarClip", InspectorEditor.Float) { Key = "camera_far_clip", Min = 0, Max = 100000, Note = "0 = the stock far clip." }, 0f),
        ReadOnly(new InspectorField("Require Direct3D 11", "RequireD3D11", InspectorEditor.Int) { Key = "require_d3d11", Note = "Bit 0: needed (recomputed on save); bit 1: the setting." }, 0),
        Alpine15(new InspectorField("Require Direct3D 11 Setting", "RequireD3D11", InspectorEditor.Bool) { Key = "require_d3d11_setting", Mask = AlpineLevelPropertiesSection.RequireD3D11Setting, Note = "Tell players the level needs the Direct3D 11 renderer." }, false),
    };

    /// <summary>Everything <c>level.alpine</c> reports: the editable fields, then the read-only 1.5 fields.</summary>
    public static IReadOnlyList<LevelPropertyField> ScriptFields { get; } = Fields.Concat(Alpine15Fields).ToList();

    private static LevelPropertyField Alpine(InspectorField f, object defaultWhenAbsent, string undo) =>
        new(f, LevelPropertyTarget.AlpineLevelProperties, defaultWhenAbsent) { UndoDescription = undo };

    private static LevelPropertyField Payload(string label, string path, string key, double min, double max, float dflt, string? note) =>
        Alpine(new InspectorField(label, path, InspectorEditor.Float) { Key = key, Min = min, Max = max, Note = note }, dflt, label);

    private static LevelPropertyField Alpine15(InspectorField f, object defaultWhenAbsent) =>
        new(f, LevelPropertyTarget.AlpineLevelProperties, defaultWhenAbsent) { UndoDescription = f.Label };

    private static LevelPropertyField ReadOnly(InspectorField f, object defaultWhenAbsent)
    {
        var ro = new InspectorField(f.Label, f.Path, f.Editor) { Key = f.Key, Note = f.Note, ReadOnly = true };
        return new LevelPropertyField(ro, LevelPropertyTarget.AlpineLevelProperties, defaultWhenAbsent);
    }
}

/// <summary>
/// Reads and writes level properties (canonical values: <c>string</c>, <c>int</c>, <c>float</c>,
/// <c>bool</c>, <see cref="RfColor"/>), shared by the Level Properties dialog and the script API.
/// <list type="bullet">
/// <item>Reading an absent section returns <see cref="LevelPropertyField.DefaultWhenAbsent"/> (Alpine) or null; it never creates a section.</item>
/// <item>Writing to an absent <c>level_info</c> / <c>level_properties</c> throws "This level has no … section.".</item>
/// <item>Writing to an absent Alpine section creates it (version 6; a lower version is raised to 6) inside the same undo command, so undo removes it again.</item>
/// <item>Writing the current value is a no-op (no command, no section).</item>
/// </list>
/// </summary>
public static class LevelPropertyAccess
{
    /// <summary>The section name a target is stored in, as the dialog and errors spell it.</summary>
    public static string SectionName(LevelPropertyTarget target) => target switch
    {
        LevelPropertyTarget.Header => "header",
        LevelPropertyTarget.LevelInfo => "level_info",
        LevelPropertyTarget.LevelProperties => "level_properties",
        _ => "alpine_level_properties",
    };

    /// <summary>True when the level has the section <paramref name="target"/> is stored in (the header always exists).</summary>
    public static bool HasSection(EditorDocument doc, LevelPropertyTarget target)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return target == LevelPropertyTarget.Header || Model(doc, target) is not null;
    }

    /// <summary>The canonical value of <paramref name="f"/>, or its absent-section default.</summary>
    public static object? Get(EditorDocument doc, LevelPropertyField f)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(f);
        object? model = Model(doc, f.Target);
        if (model is null)
        {
            return f.Target == LevelPropertyTarget.AlpineLevelProperties ? f.DefaultWhenAbsent : null;
        }

        return InspectorAccess.Canonical(f.Field, f.Field.Get(model));
    }

    /// <summary>Writes a canonical value (one undoable command; see the class remarks).</summary>
    public static void Set(EditorDocument doc, LevelPropertyField f, object? value)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(f);
        if (f.Field.ReadOnly)
        {
            throw new InvalidOperationException($"Field '{f.Field.EffectiveKey}' is read-only.");
        }

        if (Equals(Get(doc, f), value))
        {
            return;
        }

        InspectorField field = f.Field;
        if (f.Target == LevelPropertyTarget.AlpineLevelProperties)
        {
            AlpinePropertiesEdit edit = AlpinePropertiesEdit.Begin(doc);
            AlpineLevelPropertiesSection alp = edit.Content;
            object? oldRaw = field.GetRaw(alp);
            doc.Undo.Execute(new RelayCommand(f.UndoDescription,
                () =>
                {
                    edit.Attach();
                    field.Set(alp, value);
                },
                () =>
                {
                    field.SetRaw(alp, oldRaw);
                    edit.Detach();
                }));
            return;
        }

        if (Host(doc, f.Target) is not { Content: { } model } host)
        {
            throw new InvalidOperationException($"This level has no {SectionName(f.Target)} section.");
        }

        object? old = field.GetRaw(model);
        doc.Undo.Execute(new RelayCommand(f.UndoDescription,
            () =>
            {
                field.Set(model, value);
                host.Dirty = true;
            },
            () =>
            {
                field.SetRaw(model, old);
                host.Dirty = true;
            }));
    }

    private static object? Model(EditorDocument doc, LevelPropertyTarget target) =>
        target == LevelPropertyTarget.Header ? doc.Rfl.Header : Host(doc, target)?.Content;

    private static RflSection? Host(EditorDocument doc, LevelPropertyTarget target)
    {
        doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in doc.Rfl.Sections)
        {
            bool match = target switch
            {
                LevelPropertyTarget.LevelInfo => s.Content is LevelInfoSection,
                LevelPropertyTarget.LevelProperties => s.Content is LevelPropertiesSection,
                LevelPropertyTarget.AlpineLevelProperties => s.Content is AlpineLevelPropertiesSection,
                _ => false,
            };
            if (match)
            {
                return s;
            }
        }

        return null;
    }
}
