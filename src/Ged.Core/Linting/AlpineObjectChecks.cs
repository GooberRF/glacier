using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Linting;

/// <summary>
/// Checks on the Alpine Faction 1.5 objects, each grounded in what Alpine Faction does with them: a value its reader or
/// game code refuses, ignores or clamps. Every finding cites the Alpine behaviour it mirrors; nothing here is a style
/// rule. Warnings mean the object will not work as placed; Info means Alpine loads it with an adjusted value.
/// </summary>
internal static class AlpineObjectChecks
{
    public static void Run(RflFile rfl, List<LevelObject> objects, LintOptions options, List<LintFinding> findings)
    {
        var byUid = new Dictionary<int, LevelObject>();
        foreach (LevelObject o in objects)
        {
            byUid.TryAdd(o.Uid, o);
        }

        foreach (LevelObject o in objects)
        {
            switch (o.Model)
            {
                case AlpineVehicleFactory vf:
                    VehicleFactory(o, vf, options, findings);
                    break;
                case AlpineRopeEmitter rope:
                    Rope(o, rope, byUid, findings);
                    break;
                case AlpineDirectionalLight light:
                    DirectionalLight(o, light, findings);
                    break;
                case AlpineWaypointHelper helper:
                    WaypointHelper(o, helper, byUid, findings);
                    break;
                case RflEvent { ClassName: "Display_Projection" } ev:
                    DisplayProjection(o, ev, byUid, findings);
                    break;
            }
        }

        MoverPaths(rfl, byUid, findings);
        LiquidEmission(rfl, byUid, findings);
        NoShadowCast(rfl, findings);
        TerrainChecks.Run(rfl, objects, findings);
    }

    /// <summary>
    /// The props no-shadow-cast list: Alpine resolves it only for solid detail or moving-group brushes and ignores the
    /// rest (editor_patch/level.h:2256-2292).
    /// </summary>
    private static void NoShadowCast(RflFile rfl, List<LintFinding> findings)
    {
        if (rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().FirstOrDefault() is not { NoShadowCastBrushUids.Count: > 0 } props)
        {
            return;
        }

        var brushes = rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().SelectMany(b => b.Brushes)
            .GroupBy(b => b.Uid).ToDictionary(g => g.Key, g => g.First());
        foreach (int uid in props.NoShadowCastBrushUids.Distinct())
        {
            if (!brushes.TryGetValue(uid, out Brush? brush))
            {
                findings.Add(new LintFinding(LintSeverity.Info, LintCategory.AlpineObject,
                    $"The no-shadow-cast list names brush {uid}, which does not exist; Alpine Faction ignores it.", uid));
            }
            else if (!BrushShadowProps.IsEligible(rfl, brush))
            {
                findings.Add(new LintFinding(LintSeverity.Info, LintCategory.AlpineObject,
                    $"Brush {uid} is set to No Shadow Cast, but only solid detail or mover brushes can be left out of the baked shadows; Alpine Faction ignores it.",
                    uid, Location: brush.Position));
            }
        }
    }

    private static void Warn(List<LintFinding> findings, LevelObject o, string message, int? target = null) =>
        findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.AlpineObject, message, o.Uid, target, Location: o.Position));

    private static void Info(List<LintFinding> findings, LevelObject o, string message) =>
        findings.Add(new LintFinding(LintSeverity.Info, LintCategory.AlpineObject, message, o.Uid, Location: o.Position));

    // ---- Vehicle factory ----------------------------------------------------------

    private static void VehicleFactory(LevelObject o, AlpineVehicleFactory vf, LintOptions options, List<LintFinding> findings)
    {
        string name = Label(o, "Vehicle factory");
        string cls = vf.VehicleClass.Trim();
        EntityCatalog? entities = options.ScanOptions?.EntityCatalog;
        if (cls.Length == 0)
        {
            Warn(findings, o, $"{name} has no vehicle class, so Alpine Faction spawns nothing from it.");
        }
        else if (AlpineVehicleFactoryLimits.IsUnsupported(cls))
        {
            // common/include/common/vehicle_meshes.h:10-25: classes factories do not support.
            Warn(findings, o, $"{name} uses '{cls}', a class Alpine Faction's vehicle factories do not support.");
        }
        else if (entities is not null)
        {
            // game_patch/multi/vehicles/vehicle_spawn.cpp:496-505: a class that is not a vehicle or turret spawns nothing.
            EntityDef? def = entities.Find(cls);
            if (def is null)
            {
                Warn(findings, o, $"{name} uses '{cls}', which is not an entity.tbl class, so Alpine Faction spawns nothing from it.");
            }
            else if (!def.IsVehicleOrTurret)
            {
                Warn(findings, o, $"{name} uses '{cls}', which is not a vehicle or turret class, so Alpine Faction spawns nothing from it.");
            }
        }
        else if (!AlpineVehicleFactoryLimits.StockClasses.Contains(cls, StringComparer.OrdinalIgnoreCase))
        {
            Info(findings, o, $"{name} uses '{cls}', which is not a stock vehicle or turret class; with no entity.tbl mounted Glacier cannot check it.");
        }

        // game_patch/multi/vehicles/vehicle_spawn.cpp:383-398 + common/include/common/vehicle_orient.h:12-30: a pose that
        // is not a proper rotation is rejected at load.
        if (!IsRotation(vf.Orientation) || !IsFinite(vf.Position))
        {
            Warn(findings, o, $"{name}'s orientation is not a proper rotation (unit, orthogonal, right-handed axes), so Alpine Faction rejects it.");
        }

        // vehicle_spawn.cpp:417-422: the delay is clamped to 0..3600 s.
        if (!float.IsFinite(vf.RespawnDelaySeconds) || vf.RespawnDelaySeconds < 0f || vf.RespawnDelaySeconds > AlpineVehicleFactoryLimits.MaxRespawnDelaySeconds)
        {
            Info(findings, o, $"{name}'s respawn delay {vf.RespawnDelaySeconds} s is outside 0–3600 s; Alpine Faction clamps it.");
        }

        // vehicle_spawn.cpp:427-428: a yaw limit over 179 degrees reads as no limit.
        if (vf.TurretYawLimitDegrees > AlpineVehicleFactoryLimits.MaxTurretYawLimitDegrees)
        {
            Warn(findings, o, $"{name}'s turret yaw limit {vf.TurretYawLimitDegrees}° is over 179°, so Alpine Faction reads it as no limit.");
        }
    }

    // ---- Rope emitter -------------------------------------------------------------

    private static void Rope(LevelObject o, AlpineRopeEmitter rope, Dictionary<int, LevelObject> byUid, List<LintFinding> findings)
    {
        string name = Label(o, rope.IsZipline ? "Zip line" : "Rope emitter");
        if (rope.TargetUid == o.Uid)
        {
            // editor_patch/rope_emitter.cpp:741-746: a rope targeting itself.
            Warn(findings, o, $"{name} targets itself, so it hangs as a free dangle.", rope.TargetUid);
        }
        else if (rope.TargetUid != -1 && !byUid.ContainsKey(rope.TargetUid))
        {
            // rope_emitter.cpp:1725-1736: a target UID that names no object leaves the rope dangling.
            Warn(findings, o, $"{name} targets UID {rope.TargetUid}, which is not an object, so it hangs as a free dangle.", rope.TargetUid);
        }

        if (rope.IsZipline && rope.TargetUid == -1)
        {
            // A zip line runs between the emitter and its target (common/include/common/rope_curve.h:98-116).
            Warn(findings, o, $"{name} is a zip line with no target, so there is nothing to ride along.");
        }

        // rope_emitter.cpp:125-160: values Alpine clamps on load.
        var clamped = new List<string>();
        Check(clamped, "segments", rope.Segments, AlpineRopeLimits.MinSegments, AlpineRopeLimits.MaxSegments);
        Check(clamped, "thickness", rope.Thickness, AlpineRopeLimits.MinThickness, AlpineRopeLimits.MaxThickness);
        Check(clamped, "slack", rope.Slack, 0f, AlpineRopeLimits.MaxSlack);
        Check(clamped, "dangle length", rope.DangleLength, AlpineRopeLimits.MinDangle, AlpineRopeLimits.MaxDangle);
        Check(clamped, "mass", rope.Weight, AlpineRopeLimits.MinWeight, AlpineRopeLimits.MaxWeight);
        Check(clamped, "UV tiles per meter", rope.UvTilesPerMeter, 0f, AlpineRopeLimits.MaxUvTiles);
        Check(clamped, "sway amplitude", rope.SwayAmplitude, 0f, AlpineRopeLimits.MaxSwayAmplitude);
        Check(clamped, "sway speed", rope.SwaySpeed, 0f, AlpineRopeLimits.MaxSwaySpeed);
        Check(clamped, "zip down speed", rope.ZipDownSpeed, AlpineRopeLimits.MinZipSpeed, AlpineRopeLimits.MaxZipSpeed);
        Check(clamped, "zip up speed", rope.ZipUpSpeed, AlpineRopeLimits.MinZipSpeed, AlpineRopeLimits.MaxZipSpeed);
        if (rope.ZipDirection > 2)
        {
            clamped.Add("zip direction");
        }

        if (rope.Decorations is { Meshes.Count: > 0 } d)
        {
            Check(clamped, "decoration count", d.Count, AlpineRopeLimits.MinDecorationCount, AlpineRopeLimits.MaxDecorationCount);
            Check(clamped, "decoration spacing", d.Spacing, AlpineRopeLimits.MinDecorationSpacing, AlpineRopeLimits.MaxDecorationSpacing);
            if (d.SpacingMode > 4)
            {
                clamped.Add("decoration spacing mode");
            }
        }

        Clamped(findings, o, name, clamped);
    }

    // ---- Directional light --------------------------------------------------------

    private static void DirectionalLight(LevelObject o, AlpineDirectionalLight light, List<LintFinding> findings)
    {
        string name = Label(o, "Directional light");

        // common/include/common/alpine_dir_light.h:145-165 (sanitize_basis): no usable travel direction → straight down.
        Vec3 f = light.Orientation.Forward;
        if (!IsFinite(f) || f.Length() < 1e-6f)
        {
            Warn(findings, o, $"{name} has no travel direction, so Alpine Faction points it straight down.");
        }

        // alpine_dir_light.h:166-200 (sanitize_record): values Alpine clamps on load.
        var clamped = new List<string>();
        Check(clamped, "intensity", light.Intensity, 0f, AlpineDirectionalLightLimits.MaxIntensity);
        Check(clamped, "width / radius", light.ExtentX, 0f, AlpineDirectionalLightLimits.MaxExtent);
        Check(clamped, "height / length", light.ExtentY, 0f, AlpineDirectionalLightLimits.MaxExtent);
        Check(clamped, "depth", light.ExtentZ, 0f, AlpineDirectionalLightLimits.MaxExtent);
        Check(clamped, "feather", light.Feather, 0f, AlpineDirectionalLightLimits.MaxFeather);
        Check(clamped, "spread", light.Spread, 0f, AlpineDirectionalLightLimits.MaxSpread);
        if (light.Shape > 3)
        {
            clamped.Add("shape");
        }

        if (light.MeshMode > 1)
        {
            clamped.Add("mesh mode");
        }

        Clamped(findings, o, name, clamped);
    }

    // ---- Waypoint helper ----------------------------------------------------------

    private static void WaypointHelper(LevelObject o, AlpineWaypointHelper helper, Dictionary<int, LevelObject> byUid, List<LintFinding> findings)
    {
        string name = $"Waypoint helper {o.Uid}";
        if (helper.Links.Count > AlpineWaypointHelperLimits.MaxLinks)
        {
            // editor_patch/waypoint_helper.cpp:270-292 + alpine_waypoint_helper.h:18: links past 64 are dropped.
            Warn(findings, o, $"{name} has {helper.Links.Count} links; Alpine Faction keeps the first {AlpineWaypointHelperLimits.MaxLinks}.");
        }

        foreach (int link in helper.Links.Distinct())
        {
            // waypoint_helper.cpp:384-388: helpers link only to other helpers.
            if (byUid.TryGetValue(link, out LevelObject? target) && target.Model is not AlpineWaypointHelper)
            {
                Warn(findings, o, $"{name} links to {target.Kind} {link}; a waypoint helper's links are routes to other waypoint helpers.", link);
            }
        }

        // alpine_waypoint_helper.h:100-103 (sanitize_radius).
        if (!float.IsFinite(helper.Radius) || helper.Radius < AlpineWaypointHelperLimits.MinRadius || helper.Radius > AlpineWaypointHelperLimits.MaxRadius)
        {
            Info(findings, o, $"{name}'s radius {helper.Radius} is outside 0.5–64; Alpine Faction clamps it.");
        }
    }

    // ---- Projection camera / Display_Projection ------------------------------------

    private static void DisplayProjection(LevelObject o, RflEvent ev, Dictionary<int, LevelObject> byUid, List<LintFinding> findings)
    {
        // game_patch/object/event_alpine.cpp:1130-1160: an empty ATX handle, or no linked projection camera, does nothing.
        if (string.IsNullOrWhiteSpace(ev.Str1))
        {
            Warn(findings, o, $"Display_Projection event {o.Uid} has no ATX handle (str1), so it renders nothing.");
        }

        if (!ev.Links.Any(u => byUid.TryGetValue(u, out LevelObject? t) && t.Kind == LevelObjectKind.ProjectionCamera))
        {
            Warn(findings, o, $"Display_Projection event {o.Uid} is not linked to a Projection Camera, so it renders nothing.");
        }
    }

    // ---- Mover paths --------------------------------------------------------------

    private static void MoverPaths(RflFile rfl, Dictionary<int, LevelObject> byUid, List<LintFinding> findings)
    {
        if (AlpineAttachedData.MoverPaths(rfl) is not { } paths || (paths.Keyframes.Count == 0 && paths.Movers.Count == 0))
        {
            return;
        }

        var groupOfKeyframe = new Dictionary<int, Group>();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GroupsSection { Type: SectionType.MovingGroups } gs)
            {
                foreach (Group g in gs.Groups)
                {
                    foreach (Keyframe k in g.MovingData?.Keyframes ?? new List<Keyframe>())
                    {
                        groupOfKeyframe.TryAdd(k.Uid, g);
                    }
                }
            }
        }

        bool legacy = rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().FirstOrDefault() is not { LegacyMovers: 0 };
        var reported = new HashSet<Group>();
        void GroupFinding(Group g, int uid, string message)
        {
            if (reported.Add(g))
            {
                Vec3? at = byUid.TryGetValue(uid, out LevelObject? o) ? o.Position : null;
                findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.AlpineObject, message, uid, Location: at));
            }
        }

        IEnumerable<(int Uid, bool Curved)> keyed = paths.Keyframes.Select(k => (k.KeyframeUid, k.SegmentMode == 1))
            .Concat(paths.Movers.Select(m => (m.FirstKeyframeUid, false)));
        foreach ((int uid, bool curved) in keyed)
        {
            if (!groupOfKeyframe.TryGetValue(uid, out Group? g))
            {
                // mover_path.cpp:1413-1440 saves records only for existing keyframes: this one is dropped.
                findings.Add(new LintFinding(LintSeverity.Info, LintCategory.AlpineObject,
                    $"The mover path settings for UID {uid} name no mover keyframe; Alpine Faction ignores them.", uid));
                continue;
            }

            string mover = g.Name.Length > 0 ? $"Mover '{g.Name}'" : $"The mover with start keyframe {g.MovingData!.Keyframes[0].Uid}";
            if (legacy)
            {
                // game_patch/object/mover.cpp:174-180 and editor_patch/mover_path.cpp:62-65: no paths with Legacy Movers on.
                GroupFinding(g, uid, $"{mover} has Alpine path settings, but Legacy Movers is on (Level Properties, Alpine), so Alpine Faction ignores them.");
            }
            else if (g.MovingData!.RotateInPlace != 0)
            {
                GroupFinding(g, uid, $"{mover} has Alpine path settings, but it rotates in place, so it has no path and they are ignored.");
            }
            else if (g.MovingData.Keyframes.Count < 2)
            {
                // mover_path.cpp:398-399: a path needs at least two keyframes.
                GroupFinding(g, uid, $"{mover} has Alpine path settings but fewer than 2 keyframes, so it has no path.");
            }
            else if (curved && !MoverPathGeometry.HasClosingSegment(g.MovingData.MovementType, g.MovingData.Keyframes.Count)
                && g.MovingData.Keyframes[^1].Uid == uid)
            {
                // mover_path.h:254-275: the last keyframe owns a segment only as a loop's closing segment.
                GroupFinding(g, uid, $"{mover}'s last keyframe {uid} is set to Curved, but it starts no segment (only a loop of 3+ keyframes closes back to the first).");
            }
        }
    }

    // ---- Liquid emission ----------------------------------------------------------

    private static void LiquidEmission(RflFile rfl, Dictionary<int, LevelObject> byUid, List<LintFinding> findings)
    {
        if (AlpineAttachedData.LiquidEmission(rfl) is not { } glow)
        {
            return;
        }

        foreach (AlpineLiquidEmission e in glow.Records)
        {
            if (e.Mode == 0)
            {
                continue;
            }

            if (!byUid.TryGetValue(e.RoomEffectUid, out LevelObject? o) || o.Model is not RoomEffect fx)
            {
                findings.Add(new LintFinding(LintSeverity.Info, LintCategory.AlpineObject,
                    $"The liquid emission settings for UID {e.RoomEffectUid} name no room effect; Alpine Faction ignores them.", e.RoomEffectUid));
            }
            else if (fx.EffectType != RoomEffectsSection.EffectLiquidRoom)
            {
                // editor_patch/liquid_emission.cpp:180-212: emission applies to a Liquid Room room effect only.
                Warn(findings, o, $"Room effect {o.Uid} has liquid emission on, but it is not a Liquid Room, so nothing glows.");
            }
            else if (e.Mode > 2)
            {
                Info(findings, o, $"Room effect {o.Uid}'s liquid emission mode {e.Mode} is unknown; Alpine Faction reads it as off.");
            }
        }
    }

    // ---- Helpers ------------------------------------------------------------------

    private static string Label(LevelObject o, string kind) =>
        o.ScriptName.Length > 0 ? $"{kind} {o.Uid} ('{o.ScriptName}')" : $"{kind} {o.Uid}";

    private static void Check(List<string> clamped, string what, float value, float min, float max)
    {
        if (!float.IsFinite(value) || value < min || value > max)
        {
            clamped.Add(what);
        }
    }

    private static void Check(List<string> clamped, string what, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            clamped.Add(what);
        }
    }

    private static void Clamped(List<LintFinding> findings, LevelObject o, string name, List<string> clamped)
    {
        if (clamped.Count > 0)
        {
            Info(findings, o, $"{name}: {string.Join(", ", clamped)} outside Alpine Faction's range; it loads clamped.");
        }
    }

    private static bool IsFinite(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>common/include/common/vehicle_orient.h:12-30: unit, pairwise orthogonal within 0.01, right-handed.</summary>
    private static bool IsRotation(Mat3 m)
    {
        static bool Unit(Vec3 a) => IsFinite(a) && MathF.Abs(a.Length() - 1f) <= 0.01f;
        Vec3 r = m.Right, u = m.Up, f = m.Forward;
        float handed = r.Cross(u).Dot(f);
        return Unit(r) && Unit(u) && Unit(f)
            && MathF.Abs(r.Dot(u)) <= 0.01f && MathF.Abs(r.Dot(f)) <= 0.01f && MathF.Abs(u.Dot(f)) <= 0.01f
            && handed > 0f;
    }
}
