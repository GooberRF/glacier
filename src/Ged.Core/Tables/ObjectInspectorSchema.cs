using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Ged.Core.Model;

namespace Ged.Core.Tables;

/// <summary>Editor control kind for an object-inspector field.</summary>
public enum InspectorEditor
{
    Text,
    Int,
    Float,
    Bool,
    Enum,
    Color,
    Vector,
    Uid,
}

/// <summary>
/// One inspector field of an object type. Value access is reflection-based over
/// a dotted <see cref="Path"/> (e.g. "Header.ClassName"); a non-zero
/// <see cref="Mask"/> (+ <see cref="Shift"/>) reads/writes a bitfield slice of an
/// integer property, which backs the packed light flags. <see cref="EditorOnly"/>
/// marks fields RED shows but the game ignores.
/// </summary>
public sealed class InspectorField
{
    public InspectorField(string label, string path, InspectorEditor editor)
    {
        Label = label;
        Path = path;
        Editor = editor;
    }

    public string Label { get; }

    public string Path { get; }

    public InspectorEditor Editor { get; }

    public uint Mask { get; init; }

    public int Shift { get; init; }

    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>
    /// For an <see cref="InspectorEditor.Enum"/> whose stored values are not the option indices: the stored value of
    /// each option (e.g. a vehicle factory's team, stored 0xFF / 0 / 1 for None / Red / Blue). The editor value stays
    /// the option index; a stored value not in the list reads as -1.
    /// </summary>
    public IReadOnlyList<int>? OptionValues { get; init; }

    public bool EditorOnly { get; init; }

    /// <summary>A field whose value isn't a plain model property (section membership,
    /// script-name flag encoding); the UI supplies its own accessor.</summary>
    public bool Virtual { get; init; }

    public string? Note { get; init; }

    /// <summary>
    /// An explicit field key for scripts (snake_case). When null the key is derived from the label
    /// (<see cref="InspectorKeys.FromLabel"/>); see <see cref="EffectiveKey"/>.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>The script-facing key: <see cref="Key"/> when set, else derived from <see cref="Label"/>.</summary>
    public string EffectiveKey => Key ?? InspectorKeys.FromLabel(Label);

    /// <summary>Inclusive lower bound for numeric fields (null = only the storage type's range).</summary>
    public double? Min { get; init; }

    /// <summary>Inclusive upper bound for numeric fields (null = only the storage type's range).</summary>
    public double? Max { get; init; }

    /// <summary>A display-only field: shown, never written through an inspector or a script.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// The raw stored value of <see cref="Path"/> (the whole property — the full bitfield for a
    /// masked field), or null when the path does not resolve. Pairs with <see cref="SetRaw"/> so an
    /// undo can restore the exact prior bytes regardless of the field's editor conversion.
    /// </summary>
    public object? GetRaw(object model) =>
        TryResolve(model, Path, out object owner, out PropertyInfo prop) ? prop.GetValue(owner) : null;

    /// <summary>Writes a raw value captured by <see cref="GetRaw"/> back verbatim.</summary>
    public void SetRaw(object model, object? raw)
    {
        if (TryResolve(model, Path, out object owner, out PropertyInfo prop) && prop.CanWrite)
        {
            prop.SetValue(owner, raw);
        }
    }

    /// <summary>The (non-nullable) CLR storage type behind <see cref="Path"/> on <paramref name="model"/>, or null.</summary>
    public Type? StorageType(object model) =>
        TryResolve(model, Path, out _, out PropertyInfo prop)
            ? Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType
            : null;

    public object? Get(object model)
    {
        if (Virtual)
        {
            return null;
        }

        (object owner, PropertyInfo prop)? r = Resolve(model);
        if (r is null)
        {
            return null;
        }

        object? raw = r.Value.prop.GetValue(r.Value.owner);
        if (Mask != 0 && raw is not null)
        {
            uint bits = Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
            uint slice = (bits & Mask) >> Shift;
            return Editor == InspectorEditor.Bool ? slice != 0 : (int)slice;
        }

        if (Editor == InspectorEditor.Bool && raw is byte b)
        {
            return b != 0;
        }

        if (Editor == InspectorEditor.Enum && OptionValues is { } values && raw is not null)
        {
            long stored = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == stored)
                {
                    return i;
                }
            }

            return -1;
        }

        return raw;
    }

    public void Set(object model, object? value)
    {
        if (Virtual)
        {
            return;
        }

        (object owner, PropertyInfo prop)? r = Resolve(model);
        if (r is null)
        {
            return;
        }

        (object owner, PropertyInfo prop) = r.Value;

        // A property may be Nullable<T> — trigger box/sphere dimensions (float?), Team (int?),
        // One Way (byte?). Convert.ChangeType CANNOT target Nullable<T> (it throws
        // InvalidCastException: "Invalid cast from 'System.Single' to 'System.Nullable`1[...]'"),
        // so always convert to the UNDERLYING type and let reflection box the result back into the
        // (possibly nullable) property. This is what let editing a trigger's numeric fields crash
        // the editor: the throw escaped the commit handler to the dispatcher.
        Type target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

        if (Mask != 0)
        {
            uint bits = Convert.ToUInt32(prop.GetValue(owner), CultureInfo.InvariantCulture);
            if (Editor == InspectorEditor.Bool)
            {
                // Single-flag toggle: set or clear the mask bits directly.
                bits = ToBool(value) ? bits | Mask : bits & ~Mask;
            }
            else
            {
                uint slice = (uint)Convert.ToInt32(value, CultureInfo.InvariantCulture);
                bits = (bits & ~Mask) | ((slice << Shift) & Mask);
            }

            if (TryConvert(bits, target, out object? masked))
            {
                prop.SetValue(owner, masked);
            }

            return;
        }

        if (Editor == InspectorEditor.Bool && target == typeof(byte))
        {
            prop.SetValue(owner, (byte)(ToBool(value) ? 1 : 0));
            return;
        }

        // An option index maps to its stored value; an index outside the options leaves the field unchanged.
        if (Editor == InspectorEditor.Enum && OptionValues is { } values && value is not null)
        {
            int index = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (index >= 0 && index < values.Count && TryConvert(values[index], target, out object? mapped))
            {
                prop.SetValue(owner, mapped);
            }

            return;
        }

        // A null value clears a nullable/reference property (used by undo when the field started
        // empty); a non-null value converts to the underlying type. A hostile/overflowing value
        // that the target type can't hold reverts to the prior value rather than throwing.
        if (value is null)
        {
            if (Nullable.GetUnderlyingType(prop.PropertyType) is not null || !prop.PropertyType.IsValueType)
            {
                prop.SetValue(owner, null);
            }

            return;
        }

        if (TryConvert(value, target, out object? converted))
        {
            prop.SetValue(owner, converted);
        }
    }

    /// <summary>
    /// Converts <paramref name="value"/> to <paramref name="target"/> (the non-nullable underlying
    /// type). Returns false — a silent revert to the prior value — instead of throwing when the
    /// value can't be represented (a mid-typing / pasted number outside the type's range, or a
    /// non-convertible token), so an inspector commit can never crash the editor.
    /// </summary>
    private static bool TryConvert(object value, Type target, out object? result)
    {
        try
        {
            result = Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            result = null;
            return false;
        }
    }

    private (object, PropertyInfo)? Resolve(object model) =>
        TryResolve(model, Path, out object owner, out PropertyInfo prop) ? (owner, prop) : null;

    /// <summary>Resolves a dotted property path (e.g. "Header.ClassName") to its owner + property.</summary>
    internal static bool TryResolve(object model, string path, out object owner, out PropertyInfo prop)
    {
        owner = model;
        prop = null!;
        string[] parts = path.Split('.');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            PropertyInfo? p = owner.GetType().GetProperty(parts[i]);
            if (p?.GetValue(owner) is not { } next)
            {
                return false;
            }

            owner = next;
        }

        PropertyInfo? final = owner.GetType().GetProperty(parts[^1]);
        if (final is null)
        {
            return false;
        }

        prop = final;
        return true;
    }

    private static bool ToBool(object? v) => v switch
    {
        bool b => b,
        int i => i != 0,
        _ => false,
    };
}

/// <summary>
/// The per-object-type inspector field registry: what each object kind exposes,
/// for RED §8 dialog parity. The object inspector renders from this, and a
/// completeness test asserts it against the §8 checklist. Movers/keyframes/
/// cutscene paths are intentionally absent.
/// </summary>
public static class ObjectInspectorCatalog
{
    private static readonly Dictionary<Editor.LevelObjectKind, IReadOnlyList<InspectorField>> Map = Build();

    public static IReadOnlyList<InspectorField> For(Editor.LevelObjectKind kind) =>
        Map.TryGetValue(kind, out IReadOnlyList<InspectorField>? f) ? f : Array.Empty<InspectorField>();

    public static IEnumerable<Editor.LevelObjectKind> Kinds => Map.Keys;

    private static InspectorField T(string l, string p) => new(l, p, InspectorEditor.Text);

    private static InspectorField I(string l, string p) => new(l, p, InspectorEditor.Int);

    private static InspectorField U(string l, string p) => new(l, p, InspectorEditor.Uid);

    private static InspectorField F(string l, string p) => new(l, p, InspectorEditor.Float);

    private static InspectorField B(string l, string p) => new(l, p, InspectorEditor.Bool);

    private static InspectorField C(string l, string p) => new(l, p, InspectorEditor.Color);

    private static InspectorField V(string l, string p) => new(l, p, InspectorEditor.Vector);

    private static InspectorField E(string l, string p, params string[] opts) =>
        new(l, p, InspectorEditor.Enum) { Options = opts };

    private static InspectorField Flag(string l, string p, uint mask, bool editorOnly = false) =>
        new(l, p, InspectorEditor.Bool) { Mask = mask, EditorOnly = editorOnly };

    private static InspectorField Bits(string l, string p, uint mask, int shift, params string[] opts) =>
        new(l, p, InspectorEditor.Enum) { Mask = mask, Shift = shift, Options = opts };

    private static IReadOnlyList<InspectorField> L(params InspectorField[] fields) => fields;

    /// <summary>A float field with an inclusive range (null = unbounded on that side) and an optional note.</summary>
    private static InspectorField FR(string l, string p, double? min, double? max, string? note) =>
        new(l, p, InspectorEditor.Float) { Min = min, Max = max, Note = note };

    /// <summary>An int field with an inclusive range.</summary>
    private static InspectorField IR(string l, string p, double min, double max) =>
        new(l, p, InspectorEditor.Int) { Min = min, Max = max };

    /// <summary>A virtual field (its value lives outside the model's own properties; <see cref="InspectorAccess"/> maps it).</summary>
    private static InspectorField Virt(string l, InspectorEditor editor, string key, string? note) =>
        new(l, "Virtual." + key, editor) { Virtual = true, Key = key, Note = note };

    /// <summary>A copy of <paramref name="f"/> with an explicit script key and / or note.</summary>
    private static InspectorField KN(InspectorField f, string? key, string? note) => new(f.Label, f.Path, f.Editor)
    {
        Mask = f.Mask,
        Shift = f.Shift,
        Options = f.Options,
        OptionValues = f.OptionValues,
        EditorOnly = f.EditorOnly,
        Virtual = f.Virtual,
        Note = note ?? f.Note,
        Key = key ?? f.Key,
        Min = f.Min,
        Max = f.Max,
        ReadOnly = f.ReadOnly,
    };

    /// <summary>A display-only schema: every field read-only (objects the editor cannot author yet).</summary>
    private static IReadOnlyList<InspectorField> RO(params InspectorField[] fields) =>
        fields.Select(f => new InspectorField(f.Label, f.Path, f.Editor)
        {
            Mask = f.Mask,
            Shift = f.Shift,
            Options = f.Options,
            Key = f.Key,
            Note = f.Note,
            ReadOnly = true,
        }).ToList();

    private static Dictionary<Editor.LevelObjectKind, IReadOnlyList<InspectorField>> Build()
    {
        var m = new Dictionary<Editor.LevelObjectKind, IReadOnlyList<InspectorField>>();

        m[Editor.LevelObjectKind.Entity] = L(
            T("Class", "ClassName"), T("Script Name", "ScriptName"),
            E("AI Mode", "AiMode", "catatonic", "waiting", "patrolling"), E("Attack Style", "AiAttackStyle", "default", "evasive", "direct"),
            I("FOV", "Fov"), F("Life", "Life"), F("Armor", "Armor"),
            I("Cooperation", "Cooperation"), I("Friendliness", "Friendliness"),
            T("Primary", "DefaultPrimaryWeapon"), T("Secondary", "DefaultSecondaryWeapon"),
            B("Use Custom Attack Range", "UseCustomAttackRange"), F("Custom Attack Range", "CustomAttackRange"),
            T("Item Drop", "ItemDrop"), T("State Anim", "StateAnim"), T("Corpse Pose", "CorpsePose"),
            T("Death Anim", "DeathAnim"), T("Skin", "Skin"), I("Team ID", "TeamId"),
            T("Waypoint List", "WaypointList"), T("Waypoint Method", "WaypointMethod"), B("Run", "Run"),
            I("Sweep Min", "SweepMinAngle"), I("Sweep Max", "SweepMaxAngle"),
            U("Turret UID", "TurretUid"), U("Alert Camera UID", "AlertCameraUid"), U("Alarm Event UID", "AlarmEventUid"),
            T("Left Hand Holding", "LeftHandHolding"), T("Right Hand Holding", "RightHandHolding"),
            B("Only Attack Player", "OnlyAttackPlayer"), B("Weapon Holstered", "WeaponIsHolstered"),
            B("Ready To Fire", "ReadyToFireState"), B("Ignore Terrain When Firing", "IgnoreTerrainWhenFiring"),
            B("Perfect Aim", "PerfectAim"), B("Never Collide With Player", "NeverCollideWithPlayer"),
            B("Cower From Weapon", "CowerFromWeapon"), B("Question Unarmed Player", "QuestionUnarmedPlayer"),
            B("No Persona Messages", "NoPersonaMessages"), B("Don't Hum", "DontHum"), B("Never Flee", "NeverFly"),
            B("Never Leave", "NeverLeave"), B("Always Simulate", "AlwaysSimulate"), B("Permanent Corpse", "PermanentCorpse"),
            B("Fade Corpse Immediately", "FadeCorpseImmediately"), B("No Shadow", "NoShadow"), B("Wear Helmet", "WearHelmet"),
            B("Start Hidden", "StartHidden"), B("Start Crouched", "StartCrouched"), B("End Game If Killed", "EndGameIfKilled"),
            B("Deaf", "Deaf"), B("Boarded", "Boarded"));

        m[Editor.LevelObjectKind.Item] = L(
            T("Class", "Header.ClassName"), T("Script", "Header.ScriptName"),
            I("Count", "Count"), I("Respawn Time", "RespawnTime"), I("Team ID", "TeamId"));

        m[Editor.LevelObjectKind.Clutter] = L(
            T("Class", "Header.ClassName"), T("Script", "Header.ScriptName"), T("Skin", "Skin"));

        m[Editor.LevelObjectKind.Light] = L(
            T("Script", "ScriptName"),
            Bits("Type", "Flags", 0x30, 4, "(unused)", "Point", "Spot", "Tube"),
            Bits("Initial State", "Flags", 0xF00, 8, "(unused)", "Off", "On", "Alternating", "Alternating2"),
            C("Color", "Color"), F("Size / Range", "Range"),
            F("Spot FOV", "Fov"), F("Spot Dropoff", "FovDropoff"), F("Intensity At Max", "IntensityAtMaxRange"),
            F("Tube Width", "TubeLightWidth"),
            Flag("Dynamic", "Flags", 0x1), Flag("Fade", "Flags", 0x2), Flag("Shadow Casting", "Flags", 0x4),
            Flag("Enabled", "Flags", 0x8), Flag("Runtime Shadows", "Flags", 0x40),
            Flag("Always Show Range", "Flags", 0x80, editorOnly: true), Flag("Dropoff", "Flags", 0x1000),
            new InspectorField("Editor Only", "ClassName", InspectorEditor.Bool) { EditorOnly = true, Virtual = true, ReadOnly = true, Note = "Section membership (editor_only_lights)." },
            F("On Intensity", "OnIntensity"), F("On Time", "OnTime"), F("On Time Var", "OnTimeVariation"),
            F("Off Intensity", "OffIntensity"), F("Off Time", "OffTime"), F("Off Time Var", "OffTimeVariation"));

        m[Editor.LevelObjectKind.Trigger] = L(
            T("Script", "ScriptName"), E("Shape", "Shape", "Sphere", "Box"),
            F("Sphere Radius", "SphereRadius"), F("Box Width", "BoxWidth"), F("Box Depth", "BoxDepth"), F("Box Height", "BoxHeight"),
            B("One Way", "OneWay"), F("Resets After", "ResetsAfter"), I("Resets Times", "ResetsTimes"),
            E("Activated By", "ActivatedBy", "Players", "All Objects", "Linked Objects", "AI", "Player Vehicle", "Geomods"),
            B("Use Key Required", "IsUseKeyRequired"), T("Key Name", "KeyName"),
            U("Airlock Room UID", "AirlockRoomUid"), U("Attached To UID", "AttachedToUid"), U("Use Clutter UID", "UseClutterUid"),
            F("Button Active Time", "ButtonActiveTimeSeconds"), F("Inside Time", "InsideTimeSeconds"),
            B("Is NPC", "IsNpc"), B("Is Auto", "IsAuto"), B("Weapon Activates", "WeaponActivates"),
            B("Player In Vehicle", "InVehicle"), B("Disabled", "Disabled"), I("Team", "Team"),
            new InspectorField("MP Solo", "ScriptName", InspectorEditor.Bool) { Virtual = true, Note = "0xAB PF flag 0x4." },
            new InspectorField("MP Clientside", "ScriptName", InspectorEditor.Bool) { Virtual = true, Note = "0xAB PF flag 0x2." },
            new InspectorField("MP Solo Ignore Resets", "ScriptName", InspectorEditor.Bool) { Virtual = true, Note = "0xAB PF flag 0x8 (teleport)." });

        m[Editor.LevelObjectKind.AmbientSound] = L(
            T("Sound File", "SoundFileName"), F("Min Dist", "MinDistance"), F("Volume Scale", "VolumeScale"),
            F("Rolloff", "Rolloff"), I("Start Delay ms", "StartDelayMs"));

        m[Editor.LevelObjectKind.MpRespawnPoint] = L(
            T("Script", "ScriptName"), I("Team ID", "Team"), B("Red Team", "RedTeam"), B("Blue Team", "BlueTeam"), B("Bot", "Bot"));

        m[Editor.LevelObjectKind.ParticleEmitter] = L(
            T("Script", "Header.ScriptName"), E("Shape", "Shape", "(unused)", "Plane", "Sphere"),
            F("Sphere Radius", "SphereRadius"), F("Plane Width", "PlaneWidth"), F("Plane Depth", "PlaneDepth"),
            T("Bitmap", "Texture"), F("Spawn Delay", "SpawnDelay"), F("Spawn Randomize", "SpawnRandomize"),
            F("Velocity", "Velocity"), F("Velocity Randomize", "VelocityRandomize"), F("Acceleration", "Acceleration"),
            F("Decay", "Decay"), F("Decay Randomize", "DecayRandomize"), F("Particle Radius", "ParticleRadius"),
            F("Radius Randomize", "ParticleRadiusRandomize"), F("Growth Rate", "GrowthRate"),
            F("Gravity Multiplier", "GravityMultiplier"), F("Random Direction", "RandomDirection"),
            C("Particle Color", "ParticleColor"), C("Fade To Color", "FadeToColor"),
            I("Stickiness", "Stickiness"), I("Bounciness", "Bounciness"), I("Push", "Push"), I("Swirliness", "Swirliness"),
            B("Initially On", "InitiallyOn"), F("Time On", "TimeOn"), F("Time On Randomize", "TimeOnRandomize"),
            F("Time Off", "TimeOff"), F("Time Off Randomize", "TimeOffRandomize"), F("Active Distance", "ActiveDistance"));

        m[Editor.LevelObjectKind.BoltEmitter] = L(
            T("Script", "Header.ScriptName"), U("Target UID", "TargetUid"),
            F("Src Ctrl Dist", "SrcCtrlDist"), F("Trg Ctrl Dist", "TrgCtrlDist"), F("Thickness", "Thickness"),
            F("Jitter", "Jitter"), I("Num Segments", "NumSegments"), F("Spawn Delay", "SpawnDelay"),
            F("Spawn Delay Randomize", "SpawnDelayRandomize"), F("Decay", "Decay"), F("Decay Randomize", "DecayRandomize"),
            C("Color", "Color"), T("Texture", "Texture"), B("Initially On", "InitiallyOn"));

        m[Editor.LevelObjectKind.NavPoint] = L(
            E("Type", "NavType", "Walking", "Flying"), F("Radius", "Radius"), F("Height", "Height"),
            F("Pause Time", "PauseTime"), B("Directional", "Directional"), B("Cover", "Cover"), B("Hide", "Hide"), B("Crouch", "Crunch"));

        m[Editor.LevelObjectKind.Decal] = L(
            T("Texture", "Texture"), V("Extents", "Extents"), I("Alpha", "Alpha"),
            B("Self Illuminated", "SelfIlluminated"), E("Tiling", "Tiling", "None", "U", "V"), F("Scale", "Scale"));

        m[Editor.LevelObjectKind.GeoRegion] = L(
            Bits("Shape", "Flags", 0x06, 1, "(none)", "Sphere", "Box"), I("Hardness", "Hardness"),
            F("Radius", "Radius"), F("Width", "Width"), F("Depth", "Depth"), F("Height", "Height"),
            Flag("Is Ice", "Flags", 0x40), Flag("Use Shallow Geomods", "Flags", 0x20), F("Shallow Depth", "ShallowGeomodDepth"));

        m[Editor.LevelObjectKind.GasRegion] = L(
            T("Script", "Header.ScriptName"), E("Shape", "Shape", "(unused)", "Sphere", "Box"),
            F("Radius", "Radius"), F("Width", "Width"), F("Depth", "Depth"), F("Height", "Height"),
            C("Gas Color", "GasColor"), F("Gas Density", "GasDensity"));

        m[Editor.LevelObjectKind.ClimbRegion] = L(
            T("Script", "Header.ScriptName"), E("Region Type", "RegionType", "(unused)", "Ladder", "Chain Fence"), V("Extents", "Extents"));

        m[Editor.LevelObjectKind.PushRegion] = L(
            T("Script", "Header.ScriptName"), E("Shape", "Shape", "(unused)", "Sphere", "Axis-Aligned Box", "Oriented Box"),
            F("Radius", "Radius"), V("Extents", "Extents"), F("Strength", "Strength"), I("Turbulence", "Turbulence"));

        m[Editor.LevelObjectKind.Target] = L(T("Class", "ClassName"), T("Script", "ScriptName"));

        m[Editor.LevelObjectKind.CutsceneCamera] = L(T("Class", "ClassName"), T("Script", "ScriptName"));

        m[Editor.LevelObjectKind.MeshObject] = L(
            T("Script", "ScriptName"), T("Mesh Filename", "MeshFilename"), T("State Anim", "StateAnim"),
            E("Collision Mode", "CollisionMode", "None", "Only Weapons", "All"),
            E("Material", "Material", "Default", "Rock", "Metal", "Flesh", "Water", "Lava", "Solid", "Sand", "Ice", "Glass"),
            B("Is Clutter", "IsClutter"));

        m[Editor.LevelObjectKind.NoteObject] = L(T("Script", "ScriptName"));

        m[Editor.LevelObjectKind.CoronaObject] = L(
            T("Script", "ScriptName"), I("Color R", "ColorR"), I("Color G", "ColorG"), I("Color B", "ColorB"), I("Color A", "ColorA"),
            T("Bitmap", "CoronaBitmap"), F("Cone Angle", "ConeAngle"), F("Intensity", "Intensity"),
            F("Radius Distance", "RadiusDistance"), F("Radius Scale", "RadiusScale"), F("Diminish Distance", "DiminishDistance"),
            T("Volumetric Bitmap", "VolumetricBitmap"), F("Volumetric Height", "VolumetricHeight"), F("Volumetric Length", "VolumetricLength"));

        m[Editor.LevelObjectKind.BagObject] = L();

        // Alpine Faction 1.5 objects: Alpine RED's property dialogs, field for field, with its ranges and choices
        // (editor_patch/weather_region.cpp:284-292 + resources.rc:850-940, vehicle_factory.cpp:60-70 + :290-340,
        // rope_emitter.cpp:125-160 + common/include/common/rope_curve.h:83-116, dir_light.cpp + alpine_dir_light.h:17-50,
        // waypoint_helper.cpp:95-200 + alpine_waypoint_helper.h:17-45). Ranges are what Alpine's dialogs clamp to
        // and its readers sanitise to; values outside them load unchanged and are reported by the linter.
        m[Editor.LevelObjectKind.WeatherRegion] = L(
            T("Script", "ScriptName"),
            E("Weather", "WeatherType", "Rain", "Snow"),
            E("Shape", "Shape", "Box", "Sphere"),
            FR("Width", "Width", 0, AlpineWeatherRegionLimits.MaxDimension, "Box size along the region's right axis."),
            FR("Height", "Height", 0, AlpineWeatherRegionLimits.MaxDimension, "Box size along the region's up axis."),
            FR("Depth", "Depth", 0, AlpineWeatherRegionLimits.MaxDimension, "Box size along the region's forward axis."),
            FR("Radius", "Radius", 0, AlpineWeatherRegionLimits.MaxDimension, "Sphere radius."),
            FR("Density Scale", "DensityScale", AlpineWeatherRegionLimits.MinDensityScale, AlpineWeatherRegionLimits.MaxDensityScale, null),
            FR("Active Distance", "ActiveDistance", 0, null, "0 = automatic (the visible distance plus the game's activity margin)."),
            FR("Visible Distance", "VisibleDistance", 0, null, "0 = automatic (the weather type's own simulation box)."),
            B("Always Show Range", "AlwaysShowRange"), B("Initially Enabled", "InitiallyEnabled"),
            KN(B("Blocked By Geometry", "BlockByGeometry"), "block_by_geometry", null),
            FR("Column Width", "ColumnWidth", AlpineWeatherRegionLimits.MinColumnWidth, AlpineWeatherRegionLimits.MaxColumnWidth, "Occlusion column size when Blocked By Geometry is on."),
            C("Rain Color", "RainColor"), F("Rain Fall Speed", "RainFallSpeed"), F("Rain Wind X", "RainWindX"), F("Rain Wind Z", "RainWindZ"),
            F("Rain Streak Seconds", "RainStreakSeconds"),
            C("Snow Color", "SnowColor"), F("Snow Fall Speed", "SnowFallSpeed"), F("Snow Sway Amplitude", "SnowSwayAmplitude"),
            F("Snow Sway Speed", "SnowSwaySpeed"), F("Snow Sprite Radius", "SnowSpriteRadius"), T("Snow Bitmap", "SnowBitmap"));

        m[Editor.LevelObjectKind.VehicleFactory] = L(
            T("Script", "ScriptName"),
            KN(T("Vehicle Class", "VehicleClass"), null, "An entity.tbl vehicle or turret class (e.g. Jeep01, APC, Fighter01, sub, Driller01, Stationary Turret_Plain)."),
            KN(FR("Respawn Delay (s)", "RespawnDelaySeconds", 0, AlpineVehicleFactoryLimits.MaxRespawnDelaySeconds, null), "respawn_delay", null),
            new InspectorField("Team", "Team", InspectorEditor.Enum) { Options = new[] { "None", "Red", "Blue" }, OptionValues = new[] { 0xFF, 0, 1 } },
            B("Lock To Team", "LockToTeam"), KN(B("Spawn At Level Start", "ActiveByDefault"), "active_by_default", null),
            new InspectorField("Turret Yaw Limit (deg)", "TurretYawLimitDegrees", InspectorEditor.Int)
            {
                Key = "turret_yaw_limit", Min = 0, Max = AlpineVehicleFactoryLimits.MaxTurretYawLimitDegrees,
                Note = "Turret classes only: degrees the turret may yaw either side of its spawn facing; 0 = unrestricted.",
            });

        m[Editor.LevelObjectKind.ProjectionCamera] = L(
            KN(T("Script", "ScriptName"), null, "The view a Display_Projection event linked to this camera renders (its FOV and size live on the event)."));

        m[Editor.LevelObjectKind.RopeEmitter] = L(
            T("Script", "ScriptName"),
            KN(U("Target UID", "TargetUid"), "target", "The object the rope hangs to; -1 = a free-hanging dangle."),
            KN(FR("Slack (m)", "Slack", 0, AlpineRopeLimits.MaxSlack, "Extra length over the span to the target."), "slack", null),
            KN(FR("Dangle Length (m)", "DangleLength", AlpineRopeLimits.MinDangle, AlpineRopeLimits.MaxDangle, "Length of a rope with no target."), "dangle_length", null),
            IR("Segments", "Segments", AlpineRopeLimits.MinSegments, AlpineRopeLimits.MaxSegments),
            FR("Thickness", "Thickness", AlpineRopeLimits.MinThickness, AlpineRopeLimits.MaxThickness, null),
            C("Color", "Color"), T("Bitmap", "Bitmap"),
            FR("UV Tiles Per Meter", "UvTilesPerMeter", 0, AlpineRopeLimits.MaxUvTiles, "0 = stretch the texture once over the whole rope."),
            Flag("Glow", "Flags", AlpineRopeLimits.FlagGlow), Flag("Dynamic", "Flags", AlpineRopeLimits.FlagDynamic),
            Flag("Sway", "Flags", AlpineRopeLimits.FlagSway),
            KN(FR("Mass", "Weight", AlpineRopeLimits.MinWeight, AlpineRopeLimits.MaxWeight, null), "weight", null),
            KN(FR("Sway Amplitude (m)", "SwayAmplitude", 0, AlpineRopeLimits.MaxSwayAmplitude, null), "sway_amplitude", null),
            FR("Sway Speed", "SwaySpeed", 0, AlpineRopeLimits.MaxSwaySpeed, null),
            KN(Flag("Zip Line", "Flags", AlpineRopeLimits.FlagZipline), "zipline", null),
            KN(FR("Zip Down Speed (m/s)", "ZipDownSpeed", AlpineRopeLimits.MinZipSpeed, AlpineRopeLimits.MaxZipSpeed, "Cruise speed toward the lower anchor."), "zip_down_speed", null),
            KN(FR("Zip Up Speed (m/s)", "ZipUpSpeed", AlpineRopeLimits.MinZipSpeed, AlpineRopeLimits.MaxZipSpeed, null), "zip_up_speed", null),
            E("Zip Direction", "ZipDirection", "Two Way", "Emitter To Target", "Target To Emitter"),
            B("Initially On", "InitiallyOn"),
            Virt("Decorations", InspectorEditor.Bool, "decorations", "Duplicate the decoration meshes along the rope."),
            Virt("Decoration Meshes", InspectorEditor.Text, "decoration_meshes", "Up to 6 mesh file names separated by ';'. An empty list removes the decorations."),
            new InspectorField("Decoration Spacing Mode", "Rope.DecorationSpacingMode", InspectorEditor.Enum)
            {
                Virtual = true, Key = "decoration_spacing_mode",
                Options = new[] { "Fixed Count", "Every N Meters", "Both Ends", "Only Start", "Only Target" },
            },
            new InspectorField("Decoration Count", "Rope.DecorationCount", InspectorEditor.Int)
            {
                Virtual = true, Key = "decoration_count", Min = AlpineRopeLimits.MinDecorationCount, Max = AlpineRopeLimits.MaxDecorationCount,
            },
            new InspectorField("Decoration Spacing (m)", "Rope.DecorationSpacing", InspectorEditor.Float)
            {
                Virtual = true, Key = "decoration_spacing", Min = AlpineRopeLimits.MinDecorationSpacing, Max = AlpineRopeLimits.MaxDecorationSpacing,
            },
            Virt("Decoration Random Order", InspectorEditor.Bool, "decoration_random_order", "Randomly sort the meshes along the rope."),
            new InspectorField("Decoration Orientation", "Rope.DecorationOrient", InspectorEditor.Enum)
            {
                Virtual = true, Key = "decoration_orientation", Options = new[] { "Follow Curve", "Upright" },
            });

        m[Editor.LevelObjectKind.DirectionalLight] = L(
            T("Script", "ScriptName"), B("Initially On", "InitiallyOn"), C("Color", "Color"),
            FR("Intensity", "Intensity", 0, AlpineDirectionalLightLimits.MaxIntensity, null),
            KN(FR("Spread (deg)", "Spread", 0, AlpineDirectionalLightLimits.MaxSpread, "Baked penumbra angle."), "spread", null),
            KN(B("Casts Shadows", "CastBakedShadows"), "cast_baked_shadows", "Casts shadows in the baked lightmaps."),
            KN(B("Water Blocks Light", "LiquidOccludes"), "liquid_occludes", null),
            KN(B("Sky Faces Let Light Through", "SkyPasses"), "sky_passes", null),
            KN(E("Shape", "Shape", "None", "Box", "Sphere", "Cylinder"), null, "The volume the light is bounded to: box centred on the light (world-aligned after Box Yaw), sphere, or cylinder along the light's travel direction."),
            KN(FR("Width / Radius", "ExtentX", 0, AlpineDirectionalLightLimits.MaxExtent, "Box width, or sphere / cylinder radius."), "extent_x", null),
            KN(FR("Height / Length", "ExtentY", 0, AlpineDirectionalLightLimits.MaxExtent, "Box height, or cylinder length."), "extent_y", null),
            KN(FR("Depth", "ExtentZ", 0, AlpineDirectionalLightLimits.MaxExtent, "Box depth."), "extent_z", null),
            KN(FR("Box Yaw (deg)", "BoxYaw", 0, 360, "Box rotation about world up."), "box_yaw", null),
            KN(FR("Feather (m)", "Feather", 0, AlpineDirectionalLightLimits.MaxFeather, "Fade width at the volume's edge."), "feather", null),
            KN(B("Outside Geometry Casts Shadows", "OutsideCasts"), "outside_casts", null),
            B("Always Show Range", "AlwaysShowRange"),
            KN(B("Affects Mesh Lighting", "AffectsMeshes"), "affects_meshes", null),
            E("Mesh Mode", "MeshMode", "Scaled By Lightmap", "Everywhere"));

        m[Editor.LevelObjectKind.WaypointHelper] = L(
            KN(E("Type", "HelperType", "Air", "Water", "Ground"), "helper_type", null),
            KN(E("Largest Vehicle", "SizeClass", "Jeep", "APC", "Driller"), "size_class", "Ground helpers: the largest hull the road takes."),
            FR("Radius", "Radius", AlpineWaypointHelperLimits.MinRadius, AlpineWaypointHelperLimits.MaxRadius, null),
            KN(Flag("Landing / Exit Spot", "Flags", 0x1), "landing", null));

        // Alpine terrain (0x0AFBAE0B): the header fields Alpine RED's Terrain Properties dialog edits, with its ranges
        // (common/include/common/terrain/alpine_terrain.h:38-73, editor_patch/terrain.cpp:385-431). The grid size and
        // weight resolution resample the heightfield, so they change through the script / MCP terrain API.
        m[Editor.LevelObjectKind.Terrain] = L(
            T("Script", "ScriptName"),
            KN(FR("Cell Size (m)", "CellSize", AlpineTerrainRules.MinCellSize, AlpineTerrainRules.MaxCellSize, "Distance between height samples."), "cell_size", null),
            new InspectorField("Vertices X", "Nx", InspectorEditor.Int) { ReadOnly = true, Key = "nx", Note = "Change with terrain:resize{...}." },
            new InspectorField("Vertices Z", "Nz", InspectorEditor.Int) { ReadOnly = true, Key = "nz", Note = "Change with terrain:resize{...}." },
            KN(FR("Height Min (m)", "HeightMin", -AlpineTerrainRules.MaxCoord, AlpineTerrainRules.MaxCoord, "World height above the origin that the lowest sample maps to."), "height_min", null),
            KN(FR("Height Range (m)", "HeightRange", AlpineTerrainRules.MinHeightRange, AlpineTerrainRules.MaxHeightRange, "World height the 0..65535 samples span."), "height_range", null),
            new InspectorField("Chunk Size (cells)", "ChunkCells", InspectorEditor.Enum)
            {
                Key = "chunk_cells", Options = new[] { "16", "32", "64", "128" }, OptionValues = new[] { 16, 32, 64, 128 },
                Note = "Cells per chunk edge; each chunk compiles to its own detail room. Capped at 64 (32 with skirts or geoable) and at 256 chunks.",
            },
            new InspectorField("Weight Resolution", "WeightResolutionMultiplier", InspectorEditor.Int) { ReadOnly = true, Key = "weight_resolution", Note = "Paint texels per cell edge." },
            KN(IR("Lightmap Density", "LightmapDensity", AlpineTerrainRules.LightmapDensityMin, AlpineTerrainRules.LightmapDensityMax), "lightmap_density", "Baked light texels per cell edge."),
            KN(Flag("Geoable", "Flags", AlpineTerrainRules.FlagGeoable), "geoable", "Closed solid chunks that geomod can carve (needs RF2-style geomod)."),
            KN(Flag("Skirts", "Flags", AlpineTerrainRules.FlagSkirts), "skirts", "Walls hanging below the outer edges and hole rims (not geoable terrains)."),
            KN(Flag("Fullbright", "Flags", AlpineTerrainRules.FlagFullbright), "fullbright", "Drawn at full brightness, with no baked light."),
            KN(FR("Thickness (m)", "Thickness", AlpineTerrainRules.MinThickness, AlpineTerrainRules.MaxThickness, "Depth of a geoable terrain's solid below Height Min."), "thickness", null),
            KN(FR("Skirt Depth (m)", "SkirtDepth", 0, AlpineTerrainRules.MaxSkirtDepth, "How far skirts hang below the edges and holes."), "skirt_depth", null),
            KN(T("Underside Texture", "UndersideTexture"), "underside_texture", "Walls, skirts and the bottom of a geoable terrain."),
            KN(T("Crater Texture", "CraterTexture"), "crater_texture", "Empty = the level's geomod texture."),
            Virt("Layer Textures", InspectorEditor.Text, "layer_textures", "Up to 8 texture layers separated by ';' (the first is the base). Each cell's faces take its most painted layer."));

        // Room effects (room_effects): the type picks which block the section stores — the ambient colour
        // for Ambient Light, the liquid block for Liquid Room (InspectorPresence provisions it). The liquid
        // texture scroll rate stays a Properties-panel field (a nested UV pair).
        m[Editor.LevelObjectKind.RoomEffect] = L(
            T("Script", "Header.ScriptName"),
            new InspectorField("Effect Type", "EffectType", InspectorEditor.Enum)
            {
                Options = new[] { "(unused)", "Sky Room", "Liquid Room", "Ambient Light", "None" }, Key = "type",
                Note = "Sky Room renders this room's view as the sky seen through show-sky faces; Liquid Room fills the room with liquid; Ambient Light overrides the room's ambient light.",
            },
            B("Room Is Cold", "RoomIsCold"), B("Room Is Outside", "RoomIsOutside"), B("Room Is Air Lock", "RoomIsAirLock"),
            C("Ambient Color", "AmbientLightColor"),
            E("Waveform", "LiquidProperties.Waveform", "(unused)", "None", "Calm", "Choppy"),
            F("Liquid Depth", "LiquidProperties.Depth"),
            T("Liquid Surface Texture", "LiquidProperties.SurfaceTexture"),
            C("Liquid Color", "LiquidProperties.LiquidColor"),
            F("Liquid Visibility", "LiquidProperties.Visibility"),
            E("Liquid Type", "LiquidProperties.LiquidType", "(unused)", "Water", "Lava", "Acid"),
            B("Contains Plankton", "LiquidProperties.ContainsPlankton"),
            I("Liquid Pixels Per Meter U", "LiquidProperties.TexturePixelsPerMeterU"),
            I("Liquid Pixels Per Meter V", "LiquidProperties.TexturePixelsPerMeterV"),
            F("Liquid Texture Angle", "LiquidProperties.TextureAngleDegrees"),

            // Alpine liquid emission (chunk 0x0AFBAE0F, keyed by the room effect's UID): the light a Liquid Room's
            // surface bakes (editor_patch/liquid_emission.h:18-36; dialog liquid_emission.cpp:198-350).
            new InspectorField("Liquid Emission", "Virtual.liquid_emission", InspectorEditor.Enum)
            {
                Virtual = true, Key = "liquid_emission", Options = new[] { "Off", "Up Only", "Up And Down" },
                Note = "The light the liquid surface bakes: off, onto what is above it, or above and below.",
            },
            new InspectorField("Emission Color", "Virtual.emission_color", InspectorEditor.Color) { Virtual = true, Key = "emission_color" },
            new InspectorField("Emission Intensity", "Virtual.emission_intensity", InspectorEditor.Float)
            {
                Virtual = true, Key = "emission_intensity", Min = 0, Max = AlpineLiquidEmissionLimits.MaxIntensity,
            },
            new InspectorField("Emission Distance (m)", "Virtual.emission_distance", InspectorEditor.Float)
            {
                Virtual = true, Key = "emission_distance", Min = 0, Max = AlpineLiquidEmissionLimits.MaxDistance,
                Note = "How far from the surface the light reaches, falling off linearly to it.",
            });

        return m;
    }
}
