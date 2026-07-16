using Ged.Core.Model;

namespace Ged.Core.IO.Mesh;

/// <summary>Which of the two mesh containers a file is: static (.v3m) or character (.v3c).</summary>
public enum V3dSignature
{
    /// <summary>'RF3D' — static mesh, no bones or collision spheres.</summary>
    V3m,

    /// <summary>'RFCM' — character mesh, may carry bones and collision spheres.</summary>
    V3c,
}

/// <summary>Quaternion (x, y, z, w) as stored by V3D bone/prop-point rotations.</summary>
public record struct V3dQuat(float X, float Y, float Z, float W);

/// <summary>
/// A parsed V3M/V3C mesh file. Retains every field required to render the mesh
/// (per-batch positions/normals/UVs/triangles + resolved material names) and to
/// re-export it later (LOD tables, prop points, collision spheres, bones).
/// </summary>
public sealed class V3dFile
{
    public V3dSignature Signature { get; set; } = V3dSignature.V3m;

    /// <summary>File-header reserved words (0 in every shipping file); kept for faithful re-export.</summary>
    public int Unknown0 { get; set; }

    public int Unknown1 { get; set; }

    public int Unknown2 { get; set; }

    public List<V3dSubmesh> Submeshes { get; } = new();

    public List<V3dColSphere> ColSpheres { get; } = new();

    public List<V3dBone> Bones { get; } = new();

    public bool IsCharacter => Signature == V3dSignature.V3c;
}

/// <summary>One 3ds-max object; the engine renders all submeshes of a file.</summary>
public sealed class V3dSubmesh
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The <c>unknown0</c> field: "None" or the 3ds-max group name.</summary>
    public string ParentName { get; set; } = string.Empty;

    /// <summary>Submesh version (7 in shipping files).</summary>
    public int Version { get; set; } = 7;

    public List<float> LodDistances { get; } = new();

    public Vec3 Offset { get; set; }

    public float Radius { get; set; }

    public Aabb BoundingBox { get; set; }

    public List<V3dLod> Lods { get; } = new();

    public List<V3dMaterial> Materials { get; } = new();

    /// <summary>The <c>unknown1</c> tail array (always one entry: a name + a float).</summary>
    public List<V3dSubmeshTail> Tail { get; } = new();

    /// <summary>Resolves the diffuse texture file name for a batch via the LOD texture table.</summary>
    public string ResolveBatchTexture(V3dLod lod, V3dBatch batch)
    {
        // batch.TextureIndex indexes the LOD texture refs; each ref's Id indexes this submesh's materials.
        if (batch.TextureIndex >= 0 && batch.TextureIndex < lod.Textures.Count)
        {
            V3dLodTexture texRef = lod.Textures[batch.TextureIndex];
            if (texRef.Id >= 0 && texRef.Id < Materials.Count)
            {
                return Materials[texRef.Id].DiffuseMapName;
            }

            if (!string.IsNullOrEmpty(texRef.Filename))
            {
                return texRef.Filename;
            }
        }

        if (batch.TextureIndex >= 0 && batch.TextureIndex < Materials.Count)
        {
            return Materials[batch.TextureIndex].DiffuseMapName;
        }

        return Materials.Count > 0 ? Materials[0].DiffuseMapName : string.Empty;
    }
}

/// <summary>The <c>unknown1</c> submesh tail entry (name + trailing float).</summary>
public sealed class V3dSubmeshTail
{
    public string Name { get; set; } = string.Empty;

    public float Value { get; set; }
}

/// <summary>Submesh material definition (only the diffuse name is used by the engine).</summary>
public sealed class V3dMaterial
{
    public string DiffuseMapName { get; set; } = string.Empty;

    public float EmissiveFactor { get; set; }

    public float Unknown0 { get; set; }

    public float Unknown1 { get; set; }

    public float RefCof { get; set; }

    public string RefMapName { get; set; } = string.Empty;

    public uint Flags { get; set; } = 1;
}

/// <summary>A single level-of-detail mesh: a set of render batches plus its texture and prop-point tables.</summary>
public sealed class V3dLod
{
    public uint Flags { get; set; }

    /// <summary>The LOD's declared vertex count (header field, used for the optional morph map).</summary>
    public int NumVertices { get; set; }

    /// <summary>The <c>unknown1</c> word that follows the data block (usually -1).</summary>
    public int DataUnknown { get; set; } = -1;

    /// <summary>
    /// Bytes of the LOD data block left unconsumed after unpacking every batch and
    /// the prop points. A correct parse consumes the block exactly (0); a non-zero
    /// value means a batch/morph-map/prop-point size drifted the reader — the classic
    /// symptom of the multi-batch LOD1+ mis-parse. Asserted per-LOD by
    /// MeshBatchIntegrityTests across the whole meshes.vpp corpus.
    /// </summary>
    public int DataBlockTrailingBytes { get; set; }

    public List<V3dBatch> Batches { get; } = new();

    public List<V3dPropPoint> PropPoints { get; } = new();

    public List<V3dLodTexture> Textures { get; } = new();

    public bool HasTrianglePlanes => (Flags & 0x20) != 0;

    public bool HasMorphMap => (Flags & 0x01) != 0;

    public bool IsCharacter => (Flags & 0x02) != 0;
}

/// <summary>A texture reference within a LOD: a material-slot id and the (copied) filename.</summary>
public sealed class V3dLodTexture
{
    public byte Id { get; set; }

    public string Filename { get; set; } = string.Empty;
}

/// <summary>
/// A single render batch (one draw call / one material) with fully-unpacked
/// geometry. Array lengths follow the on-disk allocation sizes; <see cref="NumVertices"/>
/// and <see cref="NumTriangles"/> give the valid counts.
/// </summary>
public sealed class V3dBatch
{
    public int TextureIndex { get; set; }

    public uint RenderFlags { get; set; }

    /// <summary>Valid vertex count (batch_info.num_vertices).</summary>
    public int NumVertices { get; set; }

    /// <summary>Valid triangle count (batch_info.num_triangles).</summary>
    public int NumTriangles { get; set; }

    public Vec3[] Positions { get; set; } = Array.Empty<Vec3>();

    public Vec3[] Normals { get; set; } = Array.Empty<Vec3>();

    public Uv[] TexCoords { get; set; } = Array.Empty<Uv>();

    public V3dTriangle[] Triangles { get; set; } = Array.Empty<V3dTriangle>();

    public RfPlane[] Planes { get; set; } = Array.Empty<RfPlane>();

    public short[] SamePosVertexOffsets { get; set; } = Array.Empty<short>();

    public V3dBoneLink[] BoneLinks { get; set; } = Array.Empty<V3dBoneLink>();

    public short[] MorphMap { get; set; } = Array.Empty<short>();
}

/// <summary>A triangle: three vertex indices plus 16-bit flags (0x20 = double-sided).</summary>
public record struct V3dTriangle(ushort I0, ushort I1, ushort I2, ushort Flags)
{
    public const ushort DoubleSided = 0x20;
}

/// <summary>Per-vertex bone binding: up to four weights (0-255) and bone indices (0xFF = unused).</summary>
public sealed class V3dBoneLink
{
    public byte[] Weights { get; set; } = new byte[4];

    public byte[] Bones { get; set; } = new byte[4] { 0xFF, 0xFF, 0xFF, 0xFF };
}

/// <summary>A named point in the mesh referenced by game code (e.g. a thruster mount).</summary>
public sealed class V3dPropPoint
{
    public string Name { get; set; } = string.Empty;

    public V3dQuat Orientation { get; set; }

    public Vec3 Position { get; set; }

    public int ParentIndex { get; set; } = -1;
}

/// <summary>A character-mesh collision sphere (V3C only).</summary>
public sealed class V3dColSphere
{
    public string Name { get; set; } = string.Empty;

    public int BoneIndex { get; set; } = -1;

    public Vec3 Position { get; set; }

    public float Radius { get; set; }
}

/// <summary>A single skeleton bone (V3C only).</summary>
public sealed class V3dBone
{
    public string Name { get; set; } = string.Empty;

    public V3dQuat Rotation { get; set; }

    public Vec3 Position { get; set; }

    public int ParentIndex { get; set; } = -1;
}
