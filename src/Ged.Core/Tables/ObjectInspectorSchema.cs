using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

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

    public bool EditorOnly { get; init; }

    /// <summary>A field whose value isn't a plain model property (section membership,
    /// script-name flag encoding); the UI supplies its own accessor.</summary>
    public bool Virtual { get; init; }

    public string? Note { get; init; }

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

    private (object, PropertyInfo)? Resolve(object model)
    {
        object owner = model;
        string[] parts = Path.Split('.');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            PropertyInfo? p = owner.GetType().GetProperty(parts[i]);
            if (p?.GetValue(owner) is not { } next)
            {
                return null;
            }

            owner = next;
        }

        PropertyInfo? final = owner.GetType().GetProperty(parts[^1]);
        return final is null ? null : (owner, final);
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
            new InspectorField("Editor Only", "ClassName", InspectorEditor.Bool) { EditorOnly = true, Virtual = true, Note = "Section membership (editor_only_lights)." },
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

        return m;
    }
}
