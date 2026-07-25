using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Honest inspector-parity check: enumerates the <see cref="ObjectInspectorCatalog"/>
/// metadata registry against a checklist derived from red-stock-inventory §8 and
/// asserts every §8 field is exposed. Also smoke-tests that reflection get/set
/// actually round-trips on a placed object (incl. packed-nibble emitter fields
/// and packed light flags).
/// </summary>
public class InspectorMetadataTests
{
    /// <summary>The required §8 dialog fields per object type (movers/keyframes/cutscene paths are handled separately).</summary>
    private static readonly Dictionary<LevelObjectKind, string[]> Checklist = new()
    {
        [LevelObjectKind.Entity] = new[]
        {
            "Class", "Script Name", "AI Mode", "Attack Style", "FOV", "Life", "Armor", "Cooperation", "Friendliness",
            "Primary", "Secondary", "Custom Attack Range", "Item Drop", "State Anim", "Corpse Pose", "Death Anim",
            "Skin", "Team ID", "Waypoint List", "Waypoint Method", "Run", "Sweep Min", "Sweep Max", "Turret UID",
            "Alert Camera UID", "Alarm Event UID", "Left Hand Holding", "Right Hand Holding",
            "Only Attack Player", "Weapon Holstered", "Ready To Fire", "Ignore Terrain When Firing", "Perfect Aim",
            "Never Collide With Player", "Cower From Weapon", "Question Unarmed Player", "No Persona Messages",
            "Don't Hum", "Never Flee", "Never Leave", "Always Simulate", "Permanent Corpse", "Fade Corpse Immediately",
            "No Shadow", "Wear Helmet", "Start Hidden", "Start Crouched", "End Game If Killed", "Deaf", "Boarded",
        },
        [LevelObjectKind.Item] = new[] { "Class", "Script", "Count", "Respawn Time", "Team ID" },
        [LevelObjectKind.Clutter] = new[] { "Class", "Script", "Skin" },
        [LevelObjectKind.Light] = new[]
        {
            "Script", "Type", "Initial State", "Color", "Size / Range", "Spot FOV", "Spot Dropoff", "Intensity At Max",
            "Tube Width", "Dynamic", "Shadow Casting", "Fade", "Enabled", "Runtime Shadows", "Always Show Range",
            "Editor Only", "Dropoff", "On Intensity", "On Time", "Off Intensity", "Off Time",
        },
        [LevelObjectKind.Trigger] = new[]
        {
            "Script", "Shape", "One Way", "Sphere Radius", "Box Width", "Box Depth", "Box Height", "Resets After",
            "Resets Times", "Activated By", "Key Name", "Airlock Room UID", "Attached To UID", "Use Clutter UID",
            "Button Active Time", "Inside Time", "Is NPC", "Is Auto", "Use Key Required", "Weapon Activates",
            "Player In Vehicle", "Disabled", "Team", "MP Solo", "MP Clientside", "MP Solo Ignore Resets",
        },
        [LevelObjectKind.AmbientSound] = new[] { "Sound File", "Min Dist", "Volume Scale", "Rolloff", "Start Delay ms" },
        [LevelObjectKind.MpRespawnPoint] = new[] { "Script", "Team ID", "Red Team", "Blue Team", "Bot" },
        [LevelObjectKind.ParticleEmitter] = new[]
        {
            "Bitmap", "Spawn Delay", "Velocity", "Particle Radius", "Growth Rate", "Acceleration", "Gravity Multiplier",
            "Particle Color", "Fade To Color", "Stickiness", "Bounciness", "Push", "Swirliness", "Initially On",
            "Time On", "Time Off", "Active Distance",
        },
        [LevelObjectKind.BoltEmitter] = new[]
        {
            "Target UID", "Thickness", "Jitter", "Num Segments", "Color", "Texture", "Initially On",
        },
        [LevelObjectKind.NavPoint] = new[] { "Type", "Radius", "Height", "Pause Time", "Directional", "Cover", "Hide", "Crouch" },
        [LevelObjectKind.Decal] = new[] { "Texture", "Extents", "Alpha", "Self Illuminated", "Tiling", "Scale" },
        [LevelObjectKind.GeoRegion] = new[] { "Shape", "Hardness", "Radius", "Is Ice", "Use Shallow Geomods", "Shallow Depth" },
        [LevelObjectKind.GasRegion] = new[] { "Shape", "Radius", "Gas Color", "Gas Density" },
        [LevelObjectKind.ClimbRegion] = new[] { "Region Type", "Extents" },
        [LevelObjectKind.PushRegion] = new[] { "Shape", "Strength", "Turbulence" },
        [LevelObjectKind.MeshObject] = new[] { "Script", "Mesh Filename", "Collision Mode", "Material", "Is Clutter" },
        [LevelObjectKind.CoronaObject] = new[]
        {
            "Bitmap", "Cone Angle", "Intensity", "Radius Distance", "Radius Scale", "Diminish Distance",
            "Volumetric Bitmap", "Volumetric Height", "Volumetric Length",
        },
    };

    [Fact]
    public void Registry_Covers_Every_Section8_Field()
    {
        foreach ((LevelObjectKind kind, string[] required) in Checklist)
        {
            var have = ObjectInspectorCatalog.For(kind).Select(f => f.Label).ToHashSet();
            foreach (string field in required)
            {
                Assert.True(have.Contains(field),
                    $"{kind} inspector is missing the §8 field '{field}'.");
            }
        }
    }

    /// <summary>The RED Keyframe-Properties / mover dialog fields (red-stock-inventory §8), split
    /// keyframe (per-waypoint) vs mover (per-group). Every one must be exposed by the mover inspector.</summary>
    private static readonly string[] Section8KeyframeFields =
    {
        "Travel Time to Next", "Return Travel", "Pause Time", "Accel Time", "Decel Time",
        "Degrees About Axis", "Triggered Event UID", "Item UID 1", "Item UID 2", "Script Name",
    };

    private static readonly string[] Section8MoverFields =
    {
        "Movement Type", "Is Door", "Rotate In Place", "Starts Backwards", "Use Travel Time as Speed",
        "Force Orient", "No Player Collide", "Starting Keyframe",
        "Start Sound", "Start Volume", "Looping Sound", "Looping Volume",
        "Stop Sound", "Stop Volume", "Close Sound", "Close Volume", "Hold Open [Alpine]",
    };

    [Fact]
    public void Mover_Inspector_Schema_Covers_Every_Section8_Field()
    {
        var kf = MoverInspectorSchema.KeyframeFields.Select(f => f.Label).ToHashSet();
        foreach (string field in Section8KeyframeFields)
        {
            Assert.True(kf.Contains(field), $"Keyframe inspector is missing the §8 field '{field}'.");
        }

        var mv = MoverInspectorSchema.MoverFields.Select(f => f.Label).ToHashSet();
        foreach (string field in Section8MoverFields)
        {
            Assert.True(mv.Contains(field), $"Mover inspector is missing the §8 field '{field}'.");
        }
    }

    [Fact]
    public void Mover_Schema_Accessors_Round_Trip_On_MovingGroupData_And_Keyframe()
    {
        var data = new MovingGroupData();

        InspectorField collide = MoverInspectorSchema.MoverFields.First(f => f.Label == "No Player Collide");
        collide.Set(data, true);
        Assert.Equal((byte)1, data.NoPlayerCollide);       // Bool editor writes through the byte
        Assert.Equal(true, collide.Get(data));

        InspectorField vol = MoverInspectorSchema.MoverFields.First(f => f.Label == "Looping Volume");
        vol.Set(data, 0.5f);
        Assert.Equal(0.5f, data.LoopingVol);

        var k = new Keyframe();
        InspectorField ev = MoverInspectorSchema.KeyframeFields.First(f => f.Label == "Triggered Event UID");
        ev.Set(k, 42);
        Assert.Equal(42, k.EventUid);
    }

    [Fact]
    public void Reflection_Accessors_Round_Trip_On_A_Placed_Entity()
    {
        var doc = NewDoc();
        LevelObject e = doc.PlaceObject(LevelObjectKind.Entity, Vec3.Zero)!;
        InspectorField life = Field(LevelObjectKind.Entity, "Life");
        life.Set(e.Model, 55f);
        Assert.Equal(55f, (float)life.Get(e.Model)!);

        InspectorField deaf = Field(LevelObjectKind.Entity, "Deaf");
        deaf.Set(e.Model, true);
        Assert.Equal(true, deaf.Get(e.Model));
        Assert.Equal((byte)1, ((Entity)e.Model).Deaf);
    }

    [Fact]
    public void Light_Flag_Bits_Read_And_Write_Through_The_Mask()
    {
        var doc = NewDoc();
        LevelObject l = doc.PlaceObject(LevelObjectKind.Light, Vec3.Zero)!;
        var model = (Light)l.Model;

        Field(LevelObjectKind.Light, "Type").Set(model, 2);        // spot
        Field(LevelObjectKind.Light, "Shadow Casting").Set(model, true);
        Field(LevelObjectKind.Light, "Initial State").Set(model, 2); // on

        Assert.Equal(2, (int)Field(LevelObjectKind.Light, "Type").Get(model)!);
        Assert.Equal(true, Field(LevelObjectKind.Light, "Shadow Casting").Get(model));
        Assert.Equal(2u << 4, model.Flags & 0x30u);
        Assert.Equal(0x4u, model.Flags & 0x4u);
        Assert.Equal(2u << 8, model.Flags & 0xF00u);
    }

    [Fact]
    public void Trigger_Nullable_Numeric_Fields_Round_Trip_Without_Throwing()
    {
        // Regression: a trigger's dimension/team fields are Nullable<T> (float?, int?, byte?).
        // InspectorField.Set used Convert.ChangeType(value, prop.PropertyType), which throws
        // InvalidCastException for a Nullable<T> target — so amending any of these numeric
        // parameters crashed the editor. Set must convert to the underlying type instead.
        var doc = NewDoc();
        LevelObject t = doc.PlaceObject(LevelObjectKind.Trigger, Vec3.Zero)!;
        var trig = (Trigger)t.Model;

        InspectorField radius = Field(LevelObjectKind.Trigger, "Sphere Radius"); // float?
        radius.Set(trig, 12.5f);
        Assert.Equal(12.5f, trig.SphereRadius);
        Assert.Equal(12.5f, (float)radius.Get(trig)!);

        InspectorField width = Field(LevelObjectKind.Trigger, "Box Width"); // float?
        width.Set(trig, 3f);
        Assert.Equal(3f, trig.BoxWidth);

        InspectorField team = Field(LevelObjectKind.Trigger, "Team"); // int?
        team.Set(trig, 2);
        Assert.Equal(2, trig.Team);

        InspectorField oneWay = Field(LevelObjectKind.Trigger, "One Way"); // Bool editor over byte?
        oneWay.Set(trig, true);
        Assert.Equal((byte)1, trig.OneWay);
        Assert.Equal(true, oneWay.Get(trig));

        // A null (the value undo replays when the field started empty) clears the nullable
        // property rather than throwing or sticking at the edited value.
        radius.Set(trig, null);
        Assert.Null(trig.SphereRadius);
    }

    [Fact]
    public void Set_Reverts_Rather_Than_Throws_On_An_Out_Of_Range_Value()
    {
        // A pasted / mid-typing value the target type can't hold must revert to the prior value,
        // never surface an OverflowException to the commit handler (which would reach the dispatcher).
        var doc = NewDoc();
        LevelObject e = doc.PlaceObject(LevelObjectKind.Entity, Vec3.Zero)!;
        InspectorField fov = Field(LevelObjectKind.Entity, "FOV");
        fov.Set(e.Model, 90);
        int before = (int)fov.Get(e.Model)!;

        fov.Set(e.Model, long.MaxValue); // does not fit an int → no-op, no throw
        Assert.Equal(before, (int)fov.Get(e.Model)!);
    }

    [Fact]
    public void Emitter_Nibble_Accessors_Pack_Correctly()
    {
        var e = new ParticleEmitter();
        Field(LevelObjectKind.ParticleEmitter, "Stickiness").Set(e, 5);
        Field(LevelObjectKind.ParticleEmitter, "Bounciness").Set(e, 3);
        Assert.Equal((byte)0x53, e.StickinessBounciness);
        Assert.Equal(5, (int)Field(LevelObjectKind.ParticleEmitter, "Stickiness").Get(e)!);
        Assert.Equal(3, (int)Field(LevelObjectKind.ParticleEmitter, "Bounciness").Get(e)!);
    }

    private static InspectorField Field(LevelObjectKind kind, string label) =>
        ObjectInspectorCatalog.For(kind).First(f => f.Label == label);

    private static EditorDocument NewDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xB4;
        return new EditorDocument(rfl);
    }
}
