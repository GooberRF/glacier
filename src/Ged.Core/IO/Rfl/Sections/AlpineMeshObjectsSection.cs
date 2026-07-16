using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>alpine_mesh_objects (0x0AFBAE01): placed V3M/V3C mesh objects.</summary>
public sealed class AlpineMeshObjectsSection : IRflSectionContent
{
    public SectionType Type => SectionType.AlpineMeshObjects;

    public List<AlpineMeshObject> Meshes { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new AlpineMeshObjectsSection();
        int count = (int)r.ReadU32();
        for (int i = 0; i < count; i++)
        {
            var mesh = new AlpineMeshObject
            {
                Uid = r.ReadI32(),
                Position = r.ReadVec3(),
                Orientation = r.ReadMat3(),
                ScriptName = r.ReadVString(),
                MeshFilename = r.ReadVString(),
                StateAnim = r.ReadVString(),
                CollisionMode = r.ReadU8(),
            };

            int numOverrides = r.ReadU8();
            for (int j = 0; j < numOverrides; j++)
            {
                mesh.TextureOverrides.Add(new AlpineMeshTextureOverride
                {
                    SlotId = r.ReadU8(),
                    Filename = r.ReadVString(),
                });
            }

            mesh.Material = r.ReadI32();
            mesh.IsClutter = r.ReadU8();

            if (mesh.IsClutter != 0)
            {
                var clutter = new AlpineMeshClutterInfo
                {
                    Life = r.ReadF32(),
                    DebrisFilename = r.ReadVString(),
                    ExplosionVclip = r.ReadVString(),
                    ExplosionRadius = r.ReadF32(),
                    DebrisVelocity = r.ReadF32(),
                };
                for (int k = 0; k < 11; k++)
                {
                    clutter.DamageTypeFactors[k] = r.ReadF32();
                }

                clutter.CorpseFilename = r.ReadVString();
                clutter.CorpseStateAnim = r.ReadVString();
                clutter.CorpseCollision = r.ReadU8();
                clutter.CorpseMaterial = r.ReadI8();
                mesh.Clutter = clutter;
            }

            section.Meshes.Add(mesh);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteU32((uint)Meshes.Count);
        foreach (AlpineMeshObject mesh in Meshes)
        {
            w.WriteI32(mesh.Uid);
            w.WriteVec3(mesh.Position);
            w.WriteMat3(mesh.Orientation);
            w.WriteVString(mesh.ScriptName);
            w.WriteVString(mesh.MeshFilename);
            w.WriteVString(mesh.StateAnim);
            w.WriteU8(mesh.CollisionMode);

            w.WriteU8((byte)mesh.TextureOverrides.Count);
            foreach (AlpineMeshTextureOverride o in mesh.TextureOverrides)
            {
                w.WriteU8(o.SlotId);
                w.WriteVString(o.Filename);
            }

            w.WriteI32(mesh.Material);
            w.WriteU8(mesh.IsClutter);

            if (mesh.IsClutter != 0)
            {
                AlpineMeshClutterInfo c = mesh.Clutter ?? new AlpineMeshClutterInfo();
                w.WriteF32(c.Life);
                w.WriteVString(c.DebrisFilename);
                w.WriteVString(c.ExplosionVclip);
                w.WriteF32(c.ExplosionRadius);
                w.WriteF32(c.DebrisVelocity);
                for (int k = 0; k < 11; k++)
                {
                    w.WriteF32(c.DamageTypeFactors[k]);
                }

                w.WriteVString(c.CorpseFilename);
                w.WriteVString(c.CorpseStateAnim);
                w.WriteU8(c.CorpseCollision);
                w.WriteI8(c.CorpseMaterial);
            }
        }
    }
}
