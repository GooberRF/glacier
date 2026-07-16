using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Convert clutter/entity → Mesh object (alpine-gap-inventory item 3): the inherited-field mapping
/// (destructibility, corpse, collision, material) and the child-object spawn (coronas from
/// corona_N tags, thruster meshes from thruster_N tags), plus the document-level undo transaction.
/// Mirrors editor_patch/alpine_obj.cpp:1483-1637. Uses inline .tbl fixtures + a fake tag source so
/// the rules are pinned deterministically without a live VFS.
/// </summary>
public sealed class ObjectToMeshConversionTests
{
    private const string ClutterTbl = @"
#Clutter
$Class Name: ""crate""
$V3D Filename: ""crate.v3d""
$Life: 50
$Material: ""Metal""
$Flags: (""collide_object"")
$Debris Filename: ""crate_deb.v3d""
$Debris Velocity: 12
$Explode Anim: ""explode_med""
$Explode Anim Radius: 2.5
$Corpse Class Name: ""crate_corpse""
$Glare: ""lanternglow""
$Damage Type Factor: ""explosive"" 1.5
$Class Name: ""crate_corpse""
$V3D Filename: ""crate_c.v3d""
$Material: ""Flesh""
#End
";

    private const string EntityTbl = @"
#Entity Classes
$Name: ""cutter""
$V3D Filename: ""cutter.v3d""
$Life: 200
$Material: ""Metal""
$Flags: (""fly"")
$Flags2: (""collide_player"")
$Explode Anim: ""explode_lg""
$Explode Anim Radius: 3
$Corpse V3D Filename: ""cutter_corpse.vcm""
$Corona (Glare) 1: ""EDFEngine01"" """"
$Thruster VFX 1: ""cutter_thrust.vfx""
+State: ""stand"" ""cutter_stand.mvf""
$Damage Type Factor: ""energy"" 2.0
#End
";

    private const string EffectsTbl = @"
#Glares
$Name: ""lanternglow""
$Light Color: {255, 255, 255}
$Corona Bitmap: ""LightCorona05.tga""
$Cone Angle: 105.0
$Intensity: 0.04
$Name: ""EDFEngine01""
$Light Color: {0, 182, 255}
$Corona Bitmap: ""engineglow.tga""
$Cone Angle: 180
$Intensity: 0.2
#End
";

    private sealed class FakeTags : IMeshTagSource
    {
        private readonly MeshTag[] _tags;

        public FakeTags(params MeshTag[] tags) => _tags = tags;

        public IReadOnlyList<MeshTag> ReadTags(string meshFilename) => _tags;
    }

    private static EditorDocument NewDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void Clutter_Conversion_Inherits_Destructibility_Corpse_Collision_And_Material()
    {
        var doc = NewDoc();
        ClutterCatalog clutter = ClutterCatalog.Load(ClutterTbl);
        LevelObject src = doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(5, 0, 0), "crate")!;

        MeshConversionPlan plan = ObjectToMeshConverter.BuildPlan(src, clutter, null)!;
        AlpineMeshObject m = plan.Mesh;

        Assert.Equal("crate.v3m", m.MeshFilename);   // v3d -> v3m
        Assert.Equal((byte)2, m.CollisionMode);      // collide_object -> All
        Assert.Equal(2, m.Material);                 // Metal
        Assert.Equal(new Vec3(5, 0, 0), m.Position);
        Assert.Equal(1, m.IsClutter);
        Assert.True(plan.InheritedClutter);

        AlpineMeshClutterInfo c = m.Clutter!;
        Assert.Equal(50f, c.Life);
        Assert.Equal("crate_deb.v3m", c.DebrisFilename);
        Assert.Equal(12f, c.DebrisVelocity);
        Assert.Equal("explode_med", c.ExplosionVclip);
        Assert.Equal(2.5f, c.ExplosionRadius);
        Assert.Equal(1.5f, c.DamageTypeFactors[3]);  // explosive slot
        Assert.Equal(1f, c.DamageTypeFactors[0]);    // untouched default
        Assert.Equal("crate_c.v3m", c.CorpseFilename);
        Assert.Equal((sbyte)3, c.CorpseMaterial);    // Flesh, from the corpse class
        Assert.Equal((byte)2, c.CorpseCollision);
    }

    [Fact]
    public void Entity_Conversion_Inherits_Fields_Stand_Anim_And_Flags2_Collision()
    {
        var doc = NewDoc();
        EntityCatalog entities = EntityCatalog.Load(EntityTbl);
        LevelObject src = doc.PlaceObject(LevelObjectKind.Entity, new Vec3(0, 0, 0), "cutter")!;

        MeshConversionPlan plan = ObjectToMeshConverter.BuildPlan(src, null, entities)!;
        AlpineMeshObject m = plan.Mesh;

        Assert.Equal("cutter.v3m", m.MeshFilename);
        Assert.Equal((byte)2, m.CollisionMode);          // collide_player (Flags2) -> All
        Assert.Equal(2, m.Material);
        Assert.Equal("cutter_stand.rfa", m.StateAnim);   // mvf -> rfa idle pose
        Assert.Equal(1, m.IsClutter);

        AlpineMeshClutterInfo c = m.Clutter!;
        Assert.Equal(200f, c.Life);
        Assert.Equal("explode_lg", c.ExplosionVclip);
        Assert.Equal(3f, c.ExplosionRadius);
        Assert.Equal("cutter_corpse.v3c", c.CorpseFilename); // vcm -> v3c
        Assert.Equal((sbyte)-1, c.CorpseMaterial);           // entity corpse inherits base material
        Assert.Equal(2f, c.DamageTypeFactors[5]);            // energy slot
    }

    [Fact]
    public void An_Indestructible_Clutter_Does_Not_Flag_As_Clutter()
    {
        var doc = NewDoc();
        // Life -1 (default when $Life omitted) -> not clutter.
        ClutterCatalog clutter = ClutterCatalog.Load("#Clutter\n$Class Name: \"pillar\"\n$V3D Filename: \"pillar.v3m\"\n#End\n");
        LevelObject src = doc.PlaceObject(LevelObjectKind.Clutter, Vec3.Zero, "pillar")!;

        MeshConversionPlan plan = ObjectToMeshConverter.BuildPlan(src, clutter, null)!;
        Assert.Equal(0, plan.Mesh.IsClutter);
        Assert.False(plan.InheritedClutter);
    }

    [Fact]
    public void Coronas_Spawn_From_Corona_Tags_At_The_Composed_World_Transform()
    {
        var doc = NewDoc();
        ClutterCatalog clutter = ClutterCatalog.Load(ClutterTbl);
        GlareCatalog glares = GlareCatalog.Load(EffectsTbl);
        LevelObject src = doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(5, 0, 0), "crate")!;

        var tags = new FakeTags(new MeshTag("corona_1", new Vec3(0, 1, 0), Mat3.Identity));
        MeshConversionPlan plan = ObjectToMeshConverter.BuildPlan(src, clutter, null, tags, glares.Find)!;

        Assert.Single(plan.Coronas);
        AlpineCoronaObject corona = plan.Coronas[0];
        Assert.Equal(new Vec3(5, 1, 0), corona.Position);   // obj_pos + orient*tag_pos (identity)
        Assert.Equal("LightCorona05.tga", corona.CoronaBitmap);
        Assert.Equal((byte)255, corona.ColorR);
        Assert.Equal((byte)255, corona.ColorA);
        Assert.Equal("Corona", corona.ScriptName);
    }

    [Fact]
    public void Thruster_Meshes_Spawn_From_Thruster_Tags_On_Entities()
    {
        var doc = NewDoc();
        EntityCatalog entities = EntityCatalog.Load(EntityTbl);
        GlareCatalog glares = GlareCatalog.Load(EffectsTbl);
        LevelObject src = doc.PlaceObject(LevelObjectKind.Entity, new Vec3(0, 0, 0), "cutter")!;

        var tags = new FakeTags(
            new MeshTag("corona_1", new Vec3(1, 0, 0), Mat3.Identity),
            new MeshTag("thruster_1", new Vec3(0, 0, 2), Mat3.Identity));
        MeshConversionPlan plan = ObjectToMeshConverter.BuildPlan(src, null, entities, tags, glares.Find)!;

        Assert.Single(plan.Coronas);                          // EDFEngine01 corona
        Assert.Single(plan.ThrusterMeshes);
        AlpineMeshObject thruster = plan.ThrusterMeshes[0];
        Assert.Equal("cutter_thrust.vfx", thruster.MeshFilename);
        Assert.Equal("Thruster", thruster.ScriptName);
        Assert.Equal(new Vec3(0, 0, 2), thruster.Position);   // identity orient
        Assert.Equal((byte)0, thruster.CollisionMode);        // VFX never collides
    }

    [Fact]
    public void Document_Conversion_Replaces_Source_With_Mesh_And_Is_One_Undo_Step()
    {
        var doc = NewDoc();
        ClutterCatalog clutter = ClutterCatalog.Load(ClutterTbl);
        GlareCatalog glares = GlareCatalog.Load(EffectsTbl);
        LevelObject src = doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(5, 0, 0), "crate")!;
        int srcUid = src.Uid;
        var tags = new FakeTags(new MeshTag("corona_1", new Vec3(0, 1, 0), Mat3.Identity));

        EditorDocument.MeshConversionReport report =
            doc.ConvertObjectsToMesh(new[] { src }, clutter, null, tags, glares.Find);

        Assert.Equal(1, report.ConvertedCount);
        Assert.Equal(1, report.ClutterCount);
        Assert.Equal(1, report.CoronaCount);
        Assert.Single(report.NewMeshUids);

        // Source clutter is gone; a mesh object and a corona now exist.
        Assert.Null(doc.FindByUid(srcUid));
        LevelObject meshObj = doc.FindByUid(report.NewMeshUids[0])!;
        Assert.Equal(LevelObjectKind.MeshObject, meshObj.Kind);
        Assert.Equal("crate.v3m", ((AlpineMeshObject)meshObj.Model).MeshFilename);
        Assert.Contains(doc.Objects, o => o.Kind == LevelObjectKind.CoronaObject);
        Assert.Contains(meshObj, doc.Selection);

        // One undo restores the clutter and removes every new object.
        doc.Undo.Undo();
        Assert.NotNull(doc.FindByUid(srcUid));
        Assert.Null(doc.FindByUid(report.NewMeshUids[0]));
        Assert.DoesNotContain(doc.Objects, o => o.Kind == LevelObjectKind.MeshObject);
        Assert.DoesNotContain(doc.Objects, o => o.Kind == LevelObjectKind.CoronaObject);
    }

    [Fact]
    public void Sole_Moving_Group_Member_Is_Converted_But_Not_Deleted()
    {
        var doc = NewDoc();
        ClutterCatalog clutter = ClutterCatalog.Load(ClutterTbl);
        LevelObject src = doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(5, 0, 0), "crate")!;
        int srcUid = src.Uid;

        // A moving group whose only member is the source clutter.
        RflSection groups = doc.Rfl.GetOrCreateSection(
            SectionType.MovingGroups, () => new GroupsSection(SectionType.MovingGroups));
        ((GroupsSection)groups.Content!).Groups.Add(new Group { Name = "mg", IsMoving = 1, Objects = { srcUid } });
        doc.RefreshObjects();
        src = doc.FindByUid(srcUid)!;

        EditorDocument.MeshConversionReport report = doc.ConvertObjectsToMesh(new[] { src }, clutter, null);

        Assert.Equal(1, report.ConvertedCount);
        Assert.Contains(srcUid, report.SkippedSoleGroupUids);
        Assert.NotNull(doc.FindByUid(srcUid));                       // source kept (would empty the group)
        Assert.Single(report.NewMeshUids);
        Assert.NotNull(doc.FindByUid(report.NewMeshUids[0]));        // mesh still created
    }
}
