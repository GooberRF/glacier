using System.Reflection;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Regression for the latent clutter NRE (alpine-gap-inventory item #6 note): toggling
/// <see cref="AlpineMeshObject.IsClutter"/> true through the generic reflection inspector grid used
/// to leave <c>Clutter == null</c>, so <see cref="AlpineMeshObjectsSection.Write"/> dereferenced a
/// null (<c>mesh.Clutter!</c>) and threw. The setter now allocates a default clutter block on the
/// toggle, and the writer no longer relies on the null-forgiving operator. These tests prove the
/// toggle path allocates + round-trips and that the writer never throws even given a null block.
/// </summary>
public sealed class AlpineMeshClutterTests
{
    private static readonly PropertyInfo IsClutterProp =
        typeof(AlpineMeshObject).GetProperty(nameof(AlpineMeshObject.IsClutter))!;

    private static byte[] Write(AlpineMeshObjectsSection section, RflContext ctx)
    {
        var w = new RfWriter();
        section.Write(w, ctx);
        return w.ToArray();
    }

    [Fact]
    public void Toggling_IsClutter_Through_The_Grid_Allocates_Clutter_And_Round_Trips()
    {
        var ctx = new RflContext(RflFile.AlpineSaveVersion);
        var mesh = new AlpineMeshObject { Uid = 7, MeshFilename = "widget.v3m" };

        // Exactly what ObjectInspectorSchema's Bool setter does for "Is Clutter": prop.SetValue.
        IsClutterProp.SetValue(mesh, (byte)1);

        // Null-init fix: the flag never travels without a behaviour block.
        Assert.NotNull(mesh.Clutter);

        var section = new AlpineMeshObjectsSection { Meshes = { mesh } };
        byte[] bytes = Write(section, ctx); // must not throw (previously an NRE)

        var parsed = (AlpineMeshObjectsSection)AlpineMeshObjectsSection.Parse(new RfReader(bytes), ctx);
        Assert.Single(parsed.Meshes);
        Assert.Equal(1, parsed.Meshes[0].IsClutter);
        Assert.NotNull(parsed.Meshes[0].Clutter);
        Assert.Equal(7, parsed.Meshes[0].Uid);
    }

    [Fact]
    public void A_Fresh_Clutter_Block_Carries_The_Alpine_MeshClutterProps_Defaults()
    {
        // Toggling Is Clutter on a mesh must produce the same block Alpine's dialog would
        // (mfc_types.h MeshClutterProps): invulnerable, unit explosion, 10 m/s debris, unit
        // damage factors, automatic-material All-collision corpse.
        var mesh = new AlpineMeshObject();
        IsClutterProp.SetValue(mesh, (byte)1);

        AlpineMeshClutterInfo c = mesh.Clutter!;
        Assert.Equal(-1f, c.Life);
        Assert.Equal(1f, c.ExplosionRadius);
        Assert.Equal(10f, c.DebrisVelocity);
        Assert.Equal((byte)2, c.CorpseCollision);
        Assert.Equal((sbyte)-1, c.CorpseMaterial);
        Assert.All(c.DamageTypeFactors, f => Assert.Equal(1f, f));
        Assert.Equal(11, c.DamageTypeFactors.Length);
    }

    [Fact]
    public void A_Fully_Populated_Clutter_Mesh_Round_Trips_Every_Field()
    {
        var ctx = new RflContext(RflFile.AlpineSaveVersion);
        var mesh = new AlpineMeshObject
        {
            Uid = 11, Position = new Vec3(1, 2, 3), Orientation = Mat3.Identity,
            ScriptName = "crate", MeshFilename = "crate.v3m", StateAnim = "idle.rfa",
            CollisionMode = 1, Material = 5, IsClutter = 1,
        };
        mesh.TextureOverrides.Add(new AlpineMeshTextureOverride { SlotId = 0, Filename = "a.tga" });
        mesh.TextureOverrides.Add(new AlpineMeshTextureOverride { SlotId = 3, Filename = "b.tga" });
        AlpineMeshClutterInfo c = mesh.Clutter!;
        c.Life = 75f;
        c.DebrisFilename = "crate_debris.v3m";
        c.ExplosionVclip = "explode_med";
        c.ExplosionRadius = 2.5f;
        c.DebrisVelocity = 12f;
        c.CorpseFilename = "crate_corpse.v3c";
        c.CorpseStateAnim = "dead.rfa";
        c.CorpseCollision = 1;
        c.CorpseMaterial = -1;
        for (int i = 0; i < 11; i++)
        {
            c.DamageTypeFactors[i] = 0.1f * (i + 1);
        }

        var section = new AlpineMeshObjectsSection { Meshes = { mesh } };
        byte[] bytes = Write(section, ctx);
        var parsed = ((AlpineMeshObjectsSection)AlpineMeshObjectsSection.Parse(new RfReader(bytes), ctx)).Meshes[0];

        Assert.Equal("crate.v3m", parsed.MeshFilename);
        Assert.Equal("idle.rfa", parsed.StateAnim);
        Assert.Equal((byte)1, parsed.CollisionMode);
        Assert.Equal(5, parsed.Material);
        Assert.Equal(2, parsed.TextureOverrides.Count);
        Assert.Equal((byte)3, parsed.TextureOverrides[1].SlotId);
        Assert.Equal("b.tga", parsed.TextureOverrides[1].Filename);
        AlpineMeshClutterInfo pc = parsed.Clutter!;
        Assert.Equal(75f, pc.Life);
        Assert.Equal("crate_debris.v3m", pc.DebrisFilename);
        Assert.Equal("explode_med", pc.ExplosionVclip);
        Assert.Equal(2.5f, pc.ExplosionRadius);
        Assert.Equal(12f, pc.DebrisVelocity);
        Assert.Equal("crate_corpse.v3c", pc.CorpseFilename);
        Assert.Equal("dead.rfa", pc.CorpseStateAnim);
        Assert.Equal((byte)1, pc.CorpseCollision);
        Assert.Equal((sbyte)-1, pc.CorpseMaterial);
        for (int i = 0; i < 11; i++)
        {
            Assert.Equal(0.1f * (i + 1), pc.DamageTypeFactors[i]);
        }
    }

    [Fact]
    public void Writing_A_Clutter_Flagged_Mesh_With_A_Null_Block_Does_Not_Throw()
    {
        var ctx = new RflContext(RflFile.AlpineSaveVersion);
        var mesh = new AlpineMeshObject { Uid = 3, IsClutter = 1 };
        mesh.Clutter = null; // force the exact IsClutter != 0 && Clutter == null state

        var section = new AlpineMeshObjectsSection { Meshes = { mesh } };
        byte[] bytes = Write(section, ctx); // writer defaults the block instead of NRE-ing

        var parsed = (AlpineMeshObjectsSection)AlpineMeshObjectsSection.Parse(new RfReader(bytes), ctx);
        Assert.Equal(1, parsed.Meshes[0].IsClutter);
        Assert.NotNull(parsed.Meshes[0].Clutter);
    }
}
