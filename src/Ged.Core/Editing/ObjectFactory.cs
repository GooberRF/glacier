using System;
using System.Collections;
using System.Collections.Generic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>A placeable object type shown in the object-mode palette.</summary>
public sealed record PlaceableObjectType(
    LevelObjectKind Kind, string DisplayName, string Category, bool NeedsClassName = false, bool Alpine = false);

/// <summary>Everything needed to add or remove one new object in a level: its
/// model, the target section type, and how to create the section and
/// append/remove the model (with any parallel data, e.g. nav-point connections).</summary>
public sealed class ObjectBlueprint
{
    public required LevelObjectKind Kind { get; init; }

    public required SectionType Section { get; init; }

    public required object Model { get; init; }

    public required int Uid { get; init; }

    public required Func<IRflSectionContent> CreateSection { get; init; }

    public required Action<IRflSectionContent> Append { get; init; }

    public required Action<IRflSectionContent> Remove { get; init; }
}

/// <summary>
/// The single source for constructing level objects with valid, internally
/// consistent defaults (discriminated optional fields set to match the chosen
/// shape so a place→save→reload round-trips exactly). Drives both the object
/// palette placement and the acceptance round-trip.
/// </summary>
public static class ObjectFactory
{
    /// <summary>Object types the palette can place (Player Start is unique; movers/keyframes are created via the Tools panel in Group mode).</summary>
    public static IReadOnlyList<PlaceableObjectType> Palette { get; } = new List<PlaceableObjectType>
    {
        new(LevelObjectKind.Entity, "Entity", "Entities", NeedsClassName: true),
        new(LevelObjectKind.Item, "Item", "Items", NeedsClassName: true),
        new(LevelObjectKind.Clutter, "Clutter", "Clutter", NeedsClassName: true),
        new(LevelObjectKind.Light, "Light", "Lights"),
        new(LevelObjectKind.Trigger, "Trigger", "Triggers"),
        new(LevelObjectKind.AmbientSound, "Ambient Sound", "Ambient Sounds"),
        new(LevelObjectKind.MpRespawnPoint, "MP Respawn Point", "Multiplayer"),
        new(LevelObjectKind.ParticleEmitter, "Particle Emitter", "Emitters"),
        new(LevelObjectKind.BoltEmitter, "Bolt Emitter", "Emitters"),
        new(LevelObjectKind.NavPoint, "Nav Point", "AI"),
        new(LevelObjectKind.Target, "Target", "Targets"),
        new(LevelObjectKind.CutsceneCamera, "Cutscene Camera", "Cutscenes"),
        new(LevelObjectKind.Decal, "Decal", "Decals"),
        new(LevelObjectKind.GeoRegion, "Geo Region", "Regions"),
        new(LevelObjectKind.GasRegion, "Gas Region", "Regions"),
        new(LevelObjectKind.ClimbRegion, "Climb Region", "Regions"),
        new(LevelObjectKind.PushRegion, "Push Region", "Regions"),
        new(LevelObjectKind.RoomEffect, "Room Effect", "Room Effects"),
        new(LevelObjectKind.MeshObject, "Mesh Object", "Alpine", Alpine: true),
        new(LevelObjectKind.NoteObject, "Note Object", "Alpine", Alpine: true),
        new(LevelObjectKind.CoronaObject, "Corona Object", "Alpine", Alpine: true),
        new(LevelObjectKind.BagObject, "Bag Object", "Alpine", Alpine: true),
    };

    /// <summary>The full set of kinds exercised by the acceptance round-trip.</summary>
    public static IReadOnlyList<LevelObjectKind> RoundTripKinds { get; } = new[]
    {
        LevelObjectKind.Entity, LevelObjectKind.Item, LevelObjectKind.Clutter, LevelObjectKind.Light,
        LevelObjectKind.Trigger, LevelObjectKind.AmbientSound, LevelObjectKind.MpRespawnPoint,
        LevelObjectKind.ParticleEmitter, LevelObjectKind.BoltEmitter, LevelObjectKind.NavPoint,
        LevelObjectKind.Target, LevelObjectKind.CutsceneCamera, LevelObjectKind.Decal,
        LevelObjectKind.GeoRegion, LevelObjectKind.GasRegion, LevelObjectKind.ClimbRegion, LevelObjectKind.PushRegion,
        LevelObjectKind.RoomEffect,
        LevelObjectKind.MeshObject, LevelObjectKind.NoteObject, LevelObjectKind.CoronaObject, LevelObjectKind.BagObject,
    };

    private static Mat3 Ident => Mat3.Identity;

    /// <summary>Builds a placement blueprint with representative, round-trip-safe field values.</summary>
    public static ObjectBlueprint Build(LevelObjectKind kind, int uid, Vec3 pos, string? className = null)
    {
        ObjectBlueprint bp = kind switch
        {
            LevelObjectKind.Entity => Bp(kind, SectionType.Entities, uid,
                new Entity
                {
                    Uid = uid, ClassName = ClassScript(className, "Guard"), Position = pos, Rotation = Ident,
                    Life = 100f, Armor = 50f, Fov = 90, TeamId = 0,
                    DefaultPrimaryWeapon = "rail_gun", AiMode = 0, AiAttackStyle = 0,
                },
                () => new EntitiesSection(), c => ((EntitiesSection)c).Entities),

            LevelObjectKind.Item => Bp(kind, SectionType.Items, uid,
                new Item { Header = Head(uid, ClassScript(className, "First_Aid"), pos), Count = 1, RespawnTime = 20, TeamId = 0 },
                () => new ItemsSection(), c => ((ItemsSection)c).Items),

            LevelObjectKind.Clutter => Bp(kind, SectionType.Clutters, uid,
                new Clutter { Header = Head(uid, ClassScript(className, "officebookcase"), pos), Skin = string.Empty },
                () => new CluttersSection(), c => ((CluttersSection)c).Clutters),

            // RED's new-Light defaults, mapped from the light_flags bitfield (rfl.ksy light_flags;
            // mirrored by ObjectInspectorSchema's Light rows): shape = Sphere → light_type Point /
            // omnidirectional (bits 0x30 == 1 → 0x010), initial_state = On (bits 0xF00 == 2 → 0x200),
            // is_enabled (0x008), and shadow_casting (0x004). Sum = 0x21C — RED's most common authored
            // value (3718 of 7228 example-corpus lights). The old value 0x1 was the "dynamic" bit —
            // which the whole example corpus never sets (0 of 7228 authored lights are dynamic) and
            // which left the light DISABLED with an invalid type 0; every stock light is enabled, Point,
            // initial-state On, and mostly shadow-casting (bits set on 7228/7228, 6524/7228, 7227/7228,
            // 5497/7228 respectively). All fields stay editable afterward.
            LevelObjectKind.Light => Bp(kind, SectionType.Lights, uid,
                new Light
                {
                    Uid = uid, ClassName = "Light", Position = pos, Rotation = Ident,
                    Flags = 0x21C, Color = new RfColor(255, 240, 200, 255), Range = 10f, IntensityAtMaxRange = 1f,
                    OnIntensity = 1f, OnTime = 1f, OffTime = 1f,
                },
                () => new LightsSection(SectionType.Lights), c => ((LightsSection)c).Lights),

            LevelObjectKind.Trigger => Bp(kind, SectionType.Triggers, uid,
                new Trigger
                {
                    Uid = uid, Position = pos, Shape = Trigger.ShapeSphere, SphereRadius = 3f,
                    ResetsAfter = 0f, ResetsTimes = -1, ActivatedBy = 0, Team = -1,
                },
                () => new TriggersSection(), c => ((TriggersSection)c).Triggers),

            LevelObjectKind.AmbientSound => Bp(kind, SectionType.AmbientSounds, uid,
                new AmbientSound
                {
                    Uid = uid, Position = pos, SoundFileName = "amb_hum.wav", MinDistance = 5f,
                    VolumeScale = 1f, Rolloff = 1f, StartDelayMs = 0,
                },
                () => new AmbientSoundsSection(), c => ((AmbientSoundsSection)c).Sounds),

            LevelObjectKind.MpRespawnPoint => Bp(kind, SectionType.MpRespawnPoints, uid,
                new MpRespawnPoint
                {
                    Uid = uid, Position = pos, Rotation = Ident, Team = 0,
                    RedTeam = 1, BlueTeam = 1, Bot = 0,
                },
                () => new MpRespawnPointsSection(), c => ((MpRespawnPointsSection)c).Points),

            LevelObjectKind.ParticleEmitter => Bp(kind, SectionType.ParticleEmitters, uid,
                new ParticleEmitter
                {
                    Header = Head(uid, string.Empty, pos), Shape = 1, PlaneWidth = 1f, PlaneDepth = 1f,
                    Texture = "glass1.tga", SpawnDelay = 0.1f, Velocity = 5f, ParticleRadius = 0.2f,
                    ParticleColor = new RfColor(200, 200, 255, 255), FadeToColor = new RfColor(0, 0, 0, 0),
                    StickinessBounciness = 0x34, PushSwirliness = 0x12, InitiallyOn = 1, ActiveDistance = 100f,
                },
                () => new ParticleEmittersSection(), c => ((ParticleEmittersSection)c).Emitters),

            LevelObjectKind.BoltEmitter => Bp(kind, SectionType.BoltEmitters, uid,
                new BoltEmitter
                {
                    Header = Head(uid, string.Empty, pos), TargetUid = -1, Thickness = 0.1f, Jitter = 0.5f,
                    NumSegments = 8, Color = new RfColor(120, 180, 255, 255), Texture = "bolt.tga", InitiallyOn = 1,
                },
                () => new BoltEmittersSection(), c => ((BoltEmittersSection)c).Emitters),

            LevelObjectKind.NavPoint => Bp(kind, SectionType.NavPoints, uid,
                new NavPoint
                {
                    Uid = uid, Height = 2f, Position = pos, Radius = 1.5f, NavType = 0, Directional = 0,
                    Cover = 1, Hide = 1, Crunch = 1, PauseTime = 0f,
                },
                () => new NavPointsSection(), c => ((NavPointsSection)c).NavPoints,
                onAppend: c => ((NavPointsSection)c).Connections.Add(new List<int>()),
                onRemove: c =>
                {
                    var s = (NavPointsSection)c;
                    if (s.Connections.Count > 0)
                    {
                        s.Connections.RemoveAt(s.Connections.Count - 1);
                    }
                }),

            LevelObjectKind.Target => Bp(kind, SectionType.Targets, uid,
                Head(uid, className ?? "Target", pos),
                () => new TargetsSection(), c => ((TargetsSection)c).Targets),

            LevelObjectKind.CutsceneCamera => Bp(kind, SectionType.CutsceneCameras, uid,
                Head(uid, "Camera", pos),
                () => new CutsceneCamerasSection(), c => ((CutsceneCamerasSection)c).Cameras),

            LevelObjectKind.Decal => Bp(kind, SectionType.Decals, uid,
                new Decal
                {
                    Header = Head(uid, string.Empty, pos), Extents = new Vec3(1f, 1f, 0.1f), Texture = "decal1.tga",
                    Alpha = 255, SelfIlluminated = 0, Tiling = 0, Scale = 1f,
                },
                () => new DecalsSection(), c => ((DecalsSection)c).Decals),

            LevelObjectKind.GeoRegion => Bp(kind, SectionType.GeoRegions, uid,
                new GeoRegion { Uid = uid, Flags = GeoRegion.FlagIsSphere, Hardness = 50, Position = pos, Radius = 4f },
                () => new GeoRegionsSection(), c => ((GeoRegionsSection)c).Regions),

            LevelObjectKind.GasRegion => Bp(kind, SectionType.GasRegions, uid,
                new GasRegion
                {
                    Header = Head(uid, string.Empty, pos), Shape = GasRegionsSection.ShapeSphere, Radius = 4f,
                    GasColor = new RfColor(80, 120, 80, 128), GasDensity = 0.5f,
                },
                () => new GasRegionsSection(), c => ((GasRegionsSection)c).Regions),

            LevelObjectKind.ClimbRegion => Bp(kind, SectionType.ClimbingRegions, uid,
                new ClimbingRegion { Header = Head(uid, string.Empty, pos), RegionType = 1, Extents = new Vec3(1f, 3f, 1f) },
                () => new ClimbingRegionsSection(), c => ((ClimbingRegionsSection)c).Regions),

            LevelObjectKind.PushRegion => Bp(kind, SectionType.PushRegions, uid,
                new PushRegion
                {
                    Header = Head(uid, string.Empty, pos), Shape = PushRegionsSection.ShapeSphere, Radius = 4f,
                    Strength = 10f, Flags = 0, Turbulence = 0,
                },
                () => new PushRegionsSection(), c => ((PushRegionsSection)c).Regions),

            // RED's own new-Room-Effect defaults (RED.exe ctor @ 0x4548b0): effect type 4
            // (None) with the three room flags clear; the ambient/liquid blocks are only
            // provisioned when the user switches the type (the section serializes them only
            // for types 2/3). Class and script name are both "Room Effect" — every stock
            // level writes exactly that pair (rfl.ksy: always "Room Effect").
            LevelObjectKind.RoomEffect => Bp(kind, SectionType.RoomEffects, uid,
                new RoomEffect
                {
                    EffectType = RoomEffectsSection.EffectNone,
                    Header = new ObjectHeader
                    {
                        Uid = uid, ClassName = "Room Effect", Position = pos, Rotation = Ident,
                    },
                },
                () => new RoomEffectsSection(), c => ((RoomEffectsSection)c).Effects),

            LevelObjectKind.MeshObject => Bp(kind, SectionType.AlpineMeshObjects, uid,
                new AlpineMeshObject
                {
                    Uid = uid, Position = pos, Orientation = Ident,
                    MeshFilename = className ?? "mymesh.v3m", StateAnim = string.Empty, CollisionMode = 2,
                    Material = 0, IsClutter = 0,
                },
                () => new AlpineMeshObjectsSection(), c => ((AlpineMeshObjectsSection)c).Meshes),

            LevelObjectKind.NoteObject => Bp(kind, SectionType.AlpineNoteObjects, uid,
                new AlpineNoteObject
                {
                    Uid = uid, Position = pos, Orientation = Ident,
                    Notes = new List<string> { "A designer note." },
                },
                () => new AlpineNoteObjectsSection(), c => ((AlpineNoteObjectsSection)c).Notes),

            LevelObjectKind.CoronaObject => Bp(kind, SectionType.AlpineCoronaObjects, uid,
                new AlpineCoronaObject
                {
                    Uid = uid, Position = pos, Orientation = Ident,
                    ColorR = 255, ColorG = 220, ColorB = 160, ColorA = 255, CoronaBitmap = "glow1.tga",
                    ConeAngle = 45f, Intensity = 1f, RadiusDistance = 20f, RadiusScale = 1f, DiminishDistance = 40f,
                    VolumetricBitmap = string.Empty,
                },
                () => new AlpineCoronaObjectsSection(), c => ((AlpineCoronaObjectsSection)c).Coronas),

            LevelObjectKind.BagObject => Bp(kind, SectionType.AlpineBagObjects, uid,
                new AlpineBagObject { Uid = uid, Position = pos, Orientation = Ident },
                () => new AlpineBagObjectsSection(), c => ((AlpineBagObjectsSection)c).Bags),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a placeable object kind (movers/keyframes/player start are handled elsewhere)."),
        };

        ApplyDefaultScriptName(bp.Model, kind);
        return bp;
    }

    private static ObjectHeader Head(int uid, string className, Vec3 pos) => new()
    {
        Uid = uid, ClassName = className, Position = pos, Rotation = Ident, ScriptName = string.Empty,
    };

    /// <summary>The resolved class name for a class-based object (the given name, or the fallback).</summary>
    private static string ClassScript(string? className, string fallback) =>
        string.IsNullOrEmpty(className) ? fallback : className;

    /// <summary>
    /// The shared script-name default (RED convention; always editable afterward), applied to EVERY
    /// newly-built object: a class-based kind (entity / item / clutter) defaults to its class name; any
    /// other kind defaults to the kind's canonical palette DISPLAY NAME (e.g. "Bolt Emitter", "Light").
    /// No placeholders. A model with no script-name field is left untouched.
    /// </summary>
    private static void ApplyDefaultScriptName(object model, LevelObjectKind kind)
    {
        bool classBased = kind is LevelObjectKind.Entity or LevelObjectKind.Item or LevelObjectKind.Clutter;
        SetScriptName(model, classBased ? ClassNameOf(model) : DisplayName(kind));
    }

    /// <summary>The kind's canonical human-readable name — exactly the palette / Outliner display name.</summary>
    private static string DisplayName(LevelObjectKind kind)
    {
        foreach (PlaceableObjectType t in Palette)
        {
            if (t.Kind == kind)
            {
                return t.DisplayName;
            }
        }

        return kind.ToString();
    }

    private static string ClassNameOf(object model)
    {
        if (model.GetType().GetProperty("ClassName")?.GetValue(model) is string s)
        {
            return s;
        }

        return model.GetType().GetProperty("Header")?.GetValue(model) is ObjectHeader h ? h.ClassName : string.Empty;
    }

    private static void SetScriptName(object model, string script)
    {
        System.Reflection.PropertyInfo? p = model.GetType().GetProperty("ScriptName");
        if (p is { CanWrite: true } && p.PropertyType == typeof(string))
        {
            p.SetValue(model, script);
            return;
        }

        if (model.GetType().GetProperty("Header")?.GetValue(model) is ObjectHeader h)
        {
            h.ScriptName = script;
        }
    }

    private static ObjectBlueprint Bp(
        LevelObjectKind kind, SectionType section, int uid, object model,
        Func<IRflSectionContent> create, Func<IRflSectionContent, IList> listOf,
        Action<IRflSectionContent>? onAppend = null, Action<IRflSectionContent>? onRemove = null) =>
        new()
        {
            Kind = kind,
            Section = section,
            Model = model,
            Uid = uid,
            CreateSection = create,
            Append = content =>
            {
                listOf(content).Add(model);
                onAppend?.Invoke(content);
            },
            Remove = content =>
            {
                onRemove?.Invoke(content);
                listOf(content).Remove(model);
            },
        };
}
