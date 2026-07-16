using Ged.Core.Model;

namespace Ged.Core.IO.Mesh;

/// <summary>
/// Serializes a <see cref="V3dFile"/> back to V3M/V3C bytes. The LOD data block is
/// rebuilt from the structured batch geometry with the format's 16-byte internal
/// alignment, so a parse → write → parse cycle reproduces the model. Adapted from
/// REDUX's V3MExporter (MIT); GED writes from its own faithful model rather than
/// REDUX's brush representation, which also makes it reusable for brush→mesh.
/// </summary>
public static class V3dWriter
{
    private const int V3mSignature = 0x52463344; // 'RF3D'
    private const int V3cSignature = 0x5246434D; // 'RFCM'
    private const int Version = 0x40000;
    private const int Alignment = 0x10;

    private const int SectionEnd = 0x00000000;
    private const int SectionSubmesh = 0x5355424D; // 'SUBM'
    private const int SectionCsphere = 0x43535048; // 'CSPH'
    private const int SectionBones = 0x424F4E45;    // 'BONE'

    public static byte[] Write(V3dFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var w = new RfWriter(4096);

        bool character = file.IsCharacter;
        w.WriteI32(character ? V3cSignature : V3mSignature);
        w.WriteI32(Version);
        w.WriteI32(file.Submeshes.Count);
        w.WriteI32(0); // num_all_vertices (ccrunch-zeroed)
        w.WriteI32(0); // num_all_triangles
        w.WriteI32(file.Unknown0);
        w.WriteI32(file.Submeshes.Sum(s => s.Materials.Count));
        w.WriteI32(file.Unknown1);
        w.WriteI32(file.Unknown2);
        w.WriteI32(character ? file.ColSpheres.Count : 0);

        foreach (V3dSubmesh sm in file.Submeshes)
        {
            WriteSubmesh(w, sm);
        }

        if (character)
        {
            foreach (V3dColSphere cs in file.ColSpheres)
            {
                WriteColSphere(w, cs);
            }

            if (file.Bones.Count > 0)
            {
                WriteBones(w, file.Bones);
            }
        }

        w.WriteI32(SectionEnd);
        w.WriteI32(0);
        return w.ToArray();
    }

    private static void WriteSubmesh(RfWriter w, V3dSubmesh sm)
    {
        w.WriteI32(SectionSubmesh);
        w.WriteI32(0); // SUBM size field is 0; the section must be fully parsed.

        w.WriteFixedString(sm.Name, 24);
        w.WriteFixedString(sm.ParentName, 24);
        w.WriteI32(sm.Version);

        w.WriteI32(sm.Lods.Count);
        foreach (float dist in sm.LodDistances)
        {
            w.WriteF32(dist);
        }

        w.WriteVec3(sm.Offset);
        w.WriteF32(sm.Radius);
        w.WriteAabb(sm.BoundingBox);

        foreach (V3dLod lod in sm.Lods)
        {
            WriteLod(w, lod);
        }

        w.WriteI32(sm.Materials.Count);
        foreach (V3dMaterial m in sm.Materials)
        {
            w.WriteFixedString(m.DiffuseMapName, 32);
            w.WriteF32(m.EmissiveFactor);
            w.WriteF32(m.Unknown0);
            w.WriteF32(m.Unknown1);
            w.WriteF32(m.RefCof);
            w.WriteFixedString(m.RefMapName, 32);
            w.WriteU32(m.Flags);
        }

        w.WriteI32(sm.Tail.Count);
        foreach (V3dSubmeshTail t in sm.Tail)
        {
            w.WriteFixedString(t.Name, 24);
            w.WriteF32(t.Value);
        }
    }

    private static void WriteLod(RfWriter w, V3dLod lod)
    {
        w.WriteU32(lod.Flags);
        w.WriteI32(lod.NumVertices);
        w.WriteU16((ushort)lod.Batches.Count);

        byte[] dataBlock = BuildDataBlock(lod);
        w.WriteI32(dataBlock.Length);
        w.WriteBytes(dataBlock);
        w.WriteI32(lod.DataUnknown);

        foreach (V3dBatch b in lod.Batches)
        {
            w.WriteU16((ushort)b.NumVertices);
            w.WriteU16((ushort)b.NumTriangles);
            w.WriteU16((ushort)(b.Positions.Length * 12));
            w.WriteU16((ushort)(b.Triangles.Length * 8));
            w.WriteU16((ushort)(b.SamePosVertexOffsets.Length * 2));
            w.WriteU16((ushort)(b.BoneLinks.Length * 8));
            w.WriteU16((ushort)(b.TexCoords.Length * 8));
            w.WriteU32(b.RenderFlags);
        }

        w.WriteI32(lod.PropPoints.Count);
        w.WriteI32(lod.Textures.Count);
        foreach (V3dLodTexture t in lod.Textures)
        {
            w.WriteU8(t.Id);
            w.WriteZString(t.Filename);
        }
    }

    private static byte[] BuildDataBlock(V3dLod lod)
    {
        var d = new RfWriter(2048);

        foreach (V3dBatch b in lod.Batches)
        {
            WriteZeros(d, 0x20);
            d.WriteI32(b.TextureIndex);
            WriteZeros(d, 0x14);
        }

        Align(d);

        foreach (V3dBatch b in lod.Batches)
        {
            foreach (Vec3 p in b.Positions)
            {
                d.WriteVec3(p);
            }

            Align(d);

            foreach (Vec3 n in b.Normals)
            {
                d.WriteVec3(n);
            }

            Align(d);

            foreach (Uv uv in b.TexCoords)
            {
                d.WriteUv(uv);
            }

            Align(d);

            foreach (V3dTriangle t in b.Triangles)
            {
                d.WriteU16(t.I0);
                d.WriteU16(t.I1);
                d.WriteU16(t.I2);
                d.WriteU16(t.Flags);
            }

            Align(d);

            if (lod.HasTrianglePlanes)
            {
                foreach (RfPlane pl in b.Planes)
                {
                    d.WritePlane(pl);
                }

                Align(d);
            }

            foreach (short s in b.SamePosVertexOffsets)
            {
                d.WriteI16(s);
            }

            Align(d);

            if (b.BoneLinks.Length > 0)
            {
                foreach (V3dBoneLink link in b.BoneLinks)
                {
                    d.WriteBytes(link.Weights);
                    d.WriteBytes(link.Bones);
                }

                Align(d);
            }

            if (lod.HasMorphMap)
            {
                foreach (short m in b.MorphMap)
                {
                    d.WriteI16(m);
                }

                Align(d);
            }
        }

        foreach (V3dPropPoint pp in lod.PropPoints)
        {
            d.WriteFixedString(pp.Name, 0x44);
            d.WriteF32(pp.Orientation.X);
            d.WriteF32(pp.Orientation.Y);
            d.WriteF32(pp.Orientation.Z);
            d.WriteF32(pp.Orientation.W);
            d.WriteVec3(pp.Position);
            d.WriteI32(pp.ParentIndex);
        }

        return d.ToArray();
    }

    private static void WriteColSphere(RfWriter w, V3dColSphere cs)
    {
        w.WriteI32(SectionCsphere);
        w.WriteI32(44);
        w.WriteFixedString(cs.Name, 24);
        w.WriteI32(cs.BoneIndex);
        w.WriteVec3(cs.Position);
        w.WriteF32(cs.Radius);
    }

    private static void WriteBones(RfWriter w, List<V3dBone> bones)
    {
        w.WriteI32(SectionBones);
        w.WriteI32(4 + (bones.Count * 44));
        w.WriteI32(bones.Count);
        foreach (V3dBone b in bones)
        {
            w.WriteFixedString(b.Name, 24);
            w.WriteF32(b.Rotation.X);
            w.WriteF32(b.Rotation.Y);
            w.WriteF32(b.Rotation.Z);
            w.WriteF32(b.Rotation.W);
            w.WriteVec3(b.Position);
            w.WriteI32(b.ParentIndex);
        }
    }

    private static void Align(RfWriter w)
    {
        int pad = (Alignment - (w.Length % Alignment)) % Alignment;
        WriteZeros(w, pad);
    }

    private static void WriteZeros(RfWriter w, int count)
    {
        if (count > 0)
        {
            w.WriteBytes(new byte[count]);
        }
    }
}
