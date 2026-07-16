using Ged.Core.Model;

namespace Ged.Core.IO.Mesh;

/// <summary>
/// Reads V3M (static, 'RF3D') and V3C (character, 'RFCM') meshes, format version
/// 0x40000, into a <see cref="V3dFile"/>. Parses SUBM/CSPH/BONE sections, all LODs
/// and their geometry batches (positions, normals, UVs, triangles, optional
/// per-triangle planes, bone links and morph map), materials, prop points and
/// collision spheres. The LOD data-block unpacking (with its 16-byte internal
/// alignment) is adapted from REDUX's V3mParser (MIT).
/// </summary>
public static class V3dReader
{
    private const int V3mSignature = 0x52463344; // 'RF3D'
    private const int V3cSignature = 0x5246434D; // 'RFCM'
    private const int Version = 0x40000;
    private const int Alignment = 0x10;

    private const int SectionEnd = 0x00000000;
    private const int SectionSubmesh = 0x5355424D; // 'SUBM'
    private const int SectionCsphere = 0x43535048; // 'CSPH'
    private const int SectionBones = 0x424F4E45;    // 'BONE'

    public static V3dFile Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return ReadCore(data);
        }
        catch (EndOfStreamException ex)
        {
            throw new V3dFormatException("Unexpected end of V3M/V3C data.", ex);
        }
    }

    private static V3dFile ReadCore(byte[] data)
    {
        var r = new RfReader(data);
        int signature = r.ReadI32();
        var file = new V3dFile
        {
            Signature = signature switch
            {
                V3mSignature => V3dSignature.V3m,
                V3cSignature => V3dSignature.V3c,
                _ => throw new V3dFormatException($"Not a V3M/V3C file (signature 0x{signature:X8})."),
            },
        };

        int version = r.ReadI32();
        if (version != Version)
        {
            throw new V3dFormatException($"Unsupported V3D version 0x{version:X8} (expected 0x{Version:X8}).");
        }

        r.ReadI32(); // num_submeshes (recomputed on write)
        r.ReadI32(); // num_all_vertices (0 after ccrunch)
        r.ReadI32(); // num_all_triangles (0 after ccrunch)
        file.Unknown0 = r.ReadI32();
        r.ReadI32(); // num_all_materials (recomputed on write)
        file.Unknown1 = r.ReadI32();
        file.Unknown2 = r.ReadI32();
        r.ReadI32(); // num_colspheres (recomputed on write)

        while (!r.Eof)
        {
            int type = r.ReadI32();
            int size = r.ReadI32();
            if (type == SectionEnd)
            {
                break;
            }

            switch (type)
            {
                case SectionSubmesh:
                    file.Submeshes.Add(ReadSubmesh(r));
                    break;
                case SectionCsphere:
                    file.ColSpheres.Add(ReadColSphere(r, size));
                    break;
                case SectionBones:
                    ReadBones(r, file, size);
                    break;
                default:
                    // Unknown section (e.g. DUMB): skip by its declared size.
                    r.Position += size;
                    break;
            }
        }

        return file;
    }

    private static V3dSubmesh ReadSubmesh(RfReader r)
    {
        var sm = new V3dSubmesh
        {
            Name = r.ReadFixedString(24),
            ParentName = r.ReadFixedString(24),
            Version = r.ReadI32(),
        };

        int numLods = r.ReadI32();
        if (numLods is < 0 or > 64)
        {
            throw new V3dFormatException($"Implausible LOD count {numLods}.");
        }

        for (int i = 0; i < numLods; i++)
        {
            sm.LodDistances.Add(r.ReadF32());
        }

        sm.Offset = r.ReadVec3();
        sm.Radius = r.ReadF32();
        sm.BoundingBox = r.ReadAabb();

        for (int i = 0; i < numLods; i++)
        {
            sm.Lods.Add(ReadLod(r));
        }

        int numMaterials = r.ReadI32();
        for (int i = 0; i < numMaterials; i++)
        {
            sm.Materials.Add(new V3dMaterial
            {
                DiffuseMapName = r.ReadFixedString(32),
                EmissiveFactor = r.ReadF32(),
                Unknown0 = r.ReadF32(),
                Unknown1 = r.ReadF32(),
                RefCof = r.ReadF32(),
                RefMapName = r.ReadFixedString(32),
                Flags = r.ReadU32(),
            });
        }

        int numTail = r.ReadI32();
        for (int i = 0; i < numTail; i++)
        {
            sm.Tail.Add(new V3dSubmeshTail
            {
                Name = r.ReadFixedString(24),
                Value = r.ReadF32(),
            });
        }

        return sm;
    }

    private static V3dLod ReadLod(RfReader r)
    {
        var lod = new V3dLod
        {
            Flags = r.ReadU32(),
            NumVertices = r.ReadI32(),
        };

        int numBatches = r.ReadU16();
        int dataSize = r.ReadI32();
        byte[] dataBlock = r.ReadBytes(dataSize);
        lod.DataUnknown = r.ReadI32();

        var infos = new BatchInfo[numBatches];
        for (int i = 0; i < numBatches; i++)
        {
            infos[i] = new BatchInfo
            {
                NumVertices = r.ReadU16(),
                NumTriangles = r.ReadU16(),
                PositionsSize = r.ReadU16(),
                IndicesSize = r.ReadU16(),
                SamePosSize = r.ReadU16(),
                BoneLinksSize = r.ReadU16(),
                TexCoordsSize = r.ReadU16(),
                RenderFlags = r.ReadU32(),
            };
        }

        int numPropPoints = r.ReadI32();
        int numTextures = r.ReadI32();
        for (int i = 0; i < numTextures; i++)
        {
            lod.Textures.Add(new V3dLodTexture
            {
                Id = r.ReadU8(),
                Filename = r.ReadZString(),
            });
        }

        UnpackDataBlock(lod, dataBlock, infos, numPropPoints);
        return lod;
    }

    private static void UnpackDataBlock(V3dLod lod, byte[] dataBlock, BatchInfo[] infos, int numPropPoints)
    {
        var d = new RfReader(dataBlock);

        // Batch headers: 0x20 reserved + i32 texture_idx + 0x14 reserved each.
        var textureIndices = new int[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            d.Position += 0x20;
            textureIndices[i] = d.ReadI32();
            d.Position += 0x14;
        }

        Align(d);

        for (int i = 0; i < infos.Length; i++)
        {
            BatchInfo info = infos[i];
            var batch = new V3dBatch
            {
                TextureIndex = textureIndices[i],
                RenderFlags = info.RenderFlags,
                NumVertices = info.NumVertices,
                NumTriangles = info.NumTriangles,
            };

            // Each batch_data array occupies its full allocated size on disk
            // (batch_info.*_size), which can over-allocate whole vertices beyond
            // num_vertices; triangle indices legitimately reference into that
            // allocation. Read the allocation-sized arrays (matching the engine and
            // REDUX) so the reader stays aligned across multi-batch meshes; the
            // *valid* counts remain num_vertices / num_triangles for consumers.
            int posCount = info.PositionsSize / 12;
            batch.Positions = ReadVec3Array(d, posCount);
            Align(d);

            batch.Normals = ReadVec3Array(d, posCount);
            Align(d);

            int uvCount = info.TexCoordsSize / 8;
            var uvs = new Uv[uvCount];
            for (int u = 0; u < uvCount; u++)
            {
                uvs[u] = d.ReadUv();
            }

            batch.TexCoords = uvs;
            Align(d);

            int triCount = info.IndicesSize / 8;
            var tris = new V3dTriangle[triCount];
            for (int t = 0; t < triCount; t++)
            {
                tris[t] = new V3dTriangle(d.ReadU16(), d.ReadU16(), d.ReadU16(), d.ReadU16());
            }

            batch.Triangles = tris;
            Align(d);

            if (lod.HasTrianglePlanes)
            {
                var planes = new RfPlane[info.NumTriangles];
                for (int p = 0; p < planes.Length; p++)
                {
                    planes[p] = d.ReadPlane();
                }

                batch.Planes = planes;
                Align(d);
            }

            int sameCount = info.SamePosSize / 2;
            var same = new short[sameCount];
            for (int s = 0; s < sameCount; s++)
            {
                same[s] = d.ReadI16();
            }

            batch.SamePosVertexOffsets = same;
            Align(d);

            if (info.BoneLinksSize > 0)
            {
                int blCount = info.BoneLinksSize / 8;
                var links = new V3dBoneLink[blCount];
                for (int b = 0; b < links.Length; b++)
                {
                    var link = new V3dBoneLink();
                    for (int w = 0; w < 4; w++)
                    {
                        link.Weights[w] = d.ReadU8();
                    }

                    for (int w = 0; w < 4; w++)
                    {
                        link.Bones[w] = d.ReadU8();
                    }

                    links[b] = link;
                }

                batch.BoneLinks = links;
                Align(d);
            }

            if (lod.HasMorphMap)
            {
                var morph = new short[lod.NumVertices];
                for (int m = 0; m < morph.Length; m++)
                {
                    morph[m] = d.ReadI16();
                }

                batch.MorphMap = morph;
                Align(d);
            }

            lod.Batches.Add(batch);
        }

        for (int p = 0; p < numPropPoints; p++)
        {
            lod.PropPoints.Add(new V3dPropPoint
            {
                Name = d.ReadFixedString(0x44),
                Orientation = new V3dQuat(d.ReadF32(), d.ReadF32(), d.ReadF32(), d.ReadF32()),
                Position = d.ReadVec3(),
                ParentIndex = d.ReadI32(),
            });
        }

        // A correct unpack consumes the whole data block: every batch's arrays
        // (positions/normals/uvs/tris/planes/same-pos/bone-links/morph-map, each
        // 0x10-aligned) plus the prop points land exactly on the block end. Record
        // the residual so the integrity tests can assert 0 for every LOD — this is
        // the invariant that catches the multi-batch LOD1+ morph-map drift.
        lod.DataBlockTrailingBytes = dataBlock.Length - d.Position;
    }

    private static V3dColSphere ReadColSphere(RfReader r, int size)
    {
        int start = r.Position;
        var cs = new V3dColSphere
        {
            Name = r.ReadFixedString(24),
            BoneIndex = r.ReadI32(),
            Position = r.ReadVec3(),
            Radius = r.ReadF32(),
        };

        int consumed = r.Position - start;
        if (size > consumed)
        {
            r.Position += size - consumed;
        }

        return cs;
    }

    private static void ReadBones(RfReader r, V3dFile file, int size)
    {
        int start = r.Position;
        int numBones = r.ReadI32();
        for (int i = 0; i < numBones; i++)
        {
            file.Bones.Add(new V3dBone
            {
                Name = r.ReadFixedString(24),
                Rotation = new V3dQuat(r.ReadF32(), r.ReadF32(), r.ReadF32(), r.ReadF32()),
                Position = r.ReadVec3(),
                ParentIndex = r.ReadI32(),
            });
        }

        int consumed = r.Position - start;
        if (size > consumed)
        {
            r.Position += size - consumed;
        }
    }

    private static Vec3[] ReadVec3Array(RfReader r, int count)
    {
        var arr = new Vec3[count];
        for (int i = 0; i < count; i++)
        {
            arr[i] = r.ReadVec3();
        }

        return arr;
    }

    private static void Align(RfReader r)
    {
        int pad = (Alignment - (r.Position % Alignment)) % Alignment;
        r.Position += pad;
    }

    private struct BatchInfo
    {
        public int NumVertices;
        public int NumTriangles;
        public int PositionsSize;
        public int IndicesSize;
        public int SamePosSize;
        public int BoneLinksSize;
        public int TexCoordsSize;
        public uint RenderFlags;
    }
}
