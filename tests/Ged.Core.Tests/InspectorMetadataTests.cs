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
