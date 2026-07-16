using System;
using System.Collections.Generic;
using Ged.Core.IO.Mesh.Import;
using Ged.Core.Model;

namespace Ged.Core.IO.Mesh;

/// <summary>
/// Builds a renderable, round-trippable <see cref="V3dFile"/> (single submesh,
/// LOD0) from a set of per-texture triangle groups. Batches are keyed by texture
/// (deduped into the submesh material table), split at the V3M's 16-bit vertex
/// index limit, and carry positions + normals + UVs + per-triangle planes. Shared
/// by generic mesh import (→ mesh object) and brush "To Mesh" export.
/// </summary>
public static class V3dMeshBuilder
{
    /// <summary>Conservative per-batch vertex cap (well under the 65535 u16-index ceiling).</summary>
    private const int MaxBatchVertices = 6000;

    /// <summary>Builds a static V3M from <paramref name="model"/>'s groups.</summary>
    public static V3dFile Build(string meshName, ImportedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Build(meshName, model.Groups);
    }

    /// <summary>Builds a static V3M from the given texture-keyed groups.</summary>
    public static V3dFile Build(string meshName, IReadOnlyList<ImportedGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var file = new V3dFile { Signature = V3dSignature.V3m };
        var submesh = new V3dSubmesh
        {
            Name = Truncate(SanitizeName(meshName), 23),
            ParentName = "None",
            Version = 7,
            Offset = Vec3.Zero,
        };
        submesh.LodDistances.Add(0f);

        var lod = new V3dLod { Flags = 0x20 }; // 0x20 = per-triangle planes present

        // Material table: one entry per distinct texture; the LOD texture table
        // parallels it (Id -> material index), so a batch's TextureIndex is the
        // material index directly (see V3dSubmesh.ResolveBatchTexture).
        var materialIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int MaterialFor(string texture)
        {
            string tex = string.IsNullOrWhiteSpace(texture) ? "default.tga" : texture;
            if (materialIndex.TryGetValue(tex, out int idx))
            {
                return idx;
            }

            idx = submesh.Materials.Count;
            submesh.Materials.Add(new V3dMaterial { DiffuseMapName = Truncate(tex, 31), Flags = 1 });
            lod.Textures.Add(new V3dLodTexture { Id = (byte)idx, Filename = Truncate(tex, 31) });
            materialIndex[tex] = idx;
            return idx;
        }

        var min = new Vec3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vec3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        bool anyVertex = false;
        int totalVertices = 0;

        foreach (ImportedGroup group in groups)
        {
            if (group.Indices.Count < 3)
            {
                continue;
            }

            int texIndex = MaterialFor(group.Texture);
            foreach (V3dBatch batch in SplitIntoBatches(group, texIndex))
            {
                lod.Batches.Add(batch);
                totalVertices += batch.NumVertices;
                foreach (Vec3 p in batch.Positions)
                {
                    min = Min(min, p);
                    max = Max(max, p);
                    anyVertex = true;
                }
            }
        }

        if (!anyVertex)
        {
            min = max = Vec3.Zero;
        }

        lod.NumVertices = totalVertices;
        submesh.Lods.Add(lod);

        Vec3 center = min.Add(max).Scale(0.5f);
        submesh.Offset = center;
        submesh.BoundingBox = new Aabb(min, max);
        submesh.Radius = anyVertex ? max.Sub(center).Length() : 1f;

        file.Submeshes.Add(submesh);
        return file;
    }

    private static IEnumerable<V3dBatch> SplitIntoBatches(ImportedGroup group, int texIndex)
    {
        bool hasNormals = group.HasNormals;
        bool hasUvs = group.HasTexCoords;

        var remap = new Dictionary<int, int>();
        var positions = new List<Vec3>();
        var normals = new List<Vec3>();
        var uvs = new List<Uv>();
        var tris = new List<V3dTriangle>();

        V3dBatch Flush()
        {
            var batch = new V3dBatch
            {
                TextureIndex = texIndex,
                NumVertices = positions.Count,
                NumTriangles = tris.Count,
                Positions = positions.ToArray(),
                Normals = normals.ToArray(),
                TexCoords = uvs.ToArray(),
                Triangles = tris.ToArray(),
            };
            batch.Planes = BuildPlanes(batch);
            return batch;
        }

        var batches = new List<V3dBatch>();

        void Reset()
        {
            remap = new Dictionary<int, int>();
            positions = new List<Vec3>();
            normals = new List<Vec3>();
            uvs = new List<Uv>();
            tris = new List<V3dTriangle>();
        }

        int Local(int globalIndex)
        {
            if (remap.TryGetValue(globalIndex, out int local))
            {
                return local;
            }

            local = positions.Count;
            positions.Add(group.Positions[globalIndex]);
            normals.Add(hasNormals ? group.Normals[globalIndex] : default);
            uvs.Add(hasUvs ? group.TexCoords[globalIndex] : default);
            remap[globalIndex] = local;
            return local;
        }

        for (int i = 0; i + 2 < group.Indices.Count; i += 3)
        {
            int a = group.Indices[i];
            int b = group.Indices[i + 1];
            int c = group.Indices[i + 2];

            // Flush before a triangle that would overflow the batch vertex cap.
            if (positions.Count + 3 > MaxBatchVertices && positions.Count > 0)
            {
                batches.Add(Flush());
                Reset();
            }

            tris.Add(new V3dTriangle((ushort)Local(a), (ushort)Local(b), (ushort)Local(c), 0));
        }

        if (positions.Count > 0)
        {
            batches.Add(Flush());
        }

        return batches;
    }

    private static RfPlane[] BuildPlanes(V3dBatch batch)
    {
        var planes = new RfPlane[batch.Triangles.Length];
        for (int t = 0; t < planes.Length; t++)
        {
            V3dTriangle tri = batch.Triangles[t];
            Vec3 p0 = batch.Positions[tri.I0];
            Vec3 p1 = batch.Positions[tri.I1];
            Vec3 p2 = batch.Positions[tri.I2];
            Vec3 n = p1.Sub(p0).Cross(p2.Sub(p0));
            n = n.LengthSquared() > 1e-12f ? n.Normalized() : new Vec3(0, 1, 0);
            planes[t] = new RfPlane(n, n.Dot(p0));
        }

        return planes;
    }

    private static string SanitizeName(string name)
    {
        string baseName = System.IO.Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(baseName) ? "mesh" : baseName;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static Vec3 Min(Vec3 a, Vec3 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));

    private static Vec3 Max(Vec3 a, Vec3 b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));
}
