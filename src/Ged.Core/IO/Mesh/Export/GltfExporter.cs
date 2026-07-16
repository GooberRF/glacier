using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ged.Core.IO.Mesh.Import;
using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Export;

/// <summary>The two files a glTF export produces: the JSON document and its binary buffer.</summary>
public sealed record GltfOutput(string Json, byte[] Bin);

/// <summary>
/// Exports an <see cref="ImportedModel"/> to glTF 2.0 (JSON + external .bin), one
/// primitive per texture group with shared POSITION/NORMAL/TEXCOORD_0 + a material
/// referencing the texture image. RF (+X right, +Y up, +Z forward, left-handed) is
/// converted to glTF (+Y up, −Z forward, right-handed) by negating Z — the pinned
/// GED convention — and the negation's winding flip is compensated by reversing
/// each triangle. Adapted from REDUX's GltfExporter (MIT). Structure is the inverse
/// of <see cref="AssimpImporter"/> + <see cref="MeshAxisConversion.GltfYUp"/> so an
/// export round-trips back through the importer.
/// </summary>
public static class GltfExporter
{
    private const int Float = 5126;
    private const int UShort = 5123;
    private const int UInt = 5125;
    private const int ArrayBuffer = 34962;
    private const int ElementArrayBuffer = 34963;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static GltfOutput Export(ImportedModel model, string binFileName)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(binFileName);

        var bin = new List<byte>();
        var bufferViews = new List<object>();
        var accessors = new List<object>();
        var materials = new List<object>();
        var images = new List<object>();
        var textures = new List<object>();
        var primitives = new List<object>();

        foreach (ImportedGroup group in model.Groups)
        {
            int vcount = group.Positions.Count;
            if (vcount == 0 || group.Indices.Count < 3)
            {
                continue;
            }

            bool hasNormals = group.HasNormals;
            bool hasUvs = group.HasTexCoords;

            // POSITION (with min/max), converting RF -> glTF (negate Z).
            var (posBytes, min, max) = PositionBytes(group);
            int posAccessor = AddAccessor(bin, bufferViews, accessors, posBytes, Float, vcount, "VEC3", ArrayBuffer, min, max);

            var attributes = new Dictionary<string, int> { ["POSITION"] = posAccessor };
            if (hasNormals)
            {
                attributes["NORMAL"] = AddAccessor(bin, bufferViews, accessors, NormalBytes(group), Float, vcount, "VEC3", ArrayBuffer);
            }

            if (hasUvs)
            {
                attributes["TEXCOORD_0"] = AddAccessor(bin, bufferViews, accessors, UvBytes(group), Float, vcount, "VEC2", ArrayBuffer);
            }

            // Indices, reversed winding to compensate the Z negation.
            (byte[] idxBytes, int componentType) = IndexBytes(group, vcount);
            int idxAccessor = AddAccessor(bin, bufferViews, accessors, idxBytes, componentType, group.Indices.Count, "SCALAR", ElementArrayBuffer);

            int? material = null;
            if (!string.IsNullOrWhiteSpace(group.Texture))
            {
                material = AddMaterial(materials, images, textures, group.Texture);
            }

            primitives.Add(new Dictionary<string, object?>
            {
                ["attributes"] = attributes,
                ["indices"] = idxAccessor,
                ["material"] = material,
                ["mode"] = 4, // TRIANGLES
            });
        }

        var root = new Dictionary<string, object?>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "Glacier" },
            ["scene"] = 0,
            ["scenes"] = new object[] { new Dictionary<string, object> { ["nodes"] = new[] { 0 } } },
            ["nodes"] = new object[] { new Dictionary<string, object> { ["mesh"] = 0, ["name"] = "level" } },
            ["meshes"] = new object[] { new Dictionary<string, object> { ["primitives"] = primitives } },
            ["buffers"] = new object[] { new Dictionary<string, object> { ["uri"] = binFileName, ["byteLength"] = bin.Count } },
            ["bufferViews"] = bufferViews,
            ["accessors"] = accessors,
            ["materials"] = materials.Count > 0 ? materials : null,
            ["textures"] = textures.Count > 0 ? textures : null,
            ["images"] = images.Count > 0 ? images : null,
            ["samplers"] = images.Count > 0
                ? new object[] { new Dictionary<string, object> { ["magFilter"] = 9729, ["minFilter"] = 9729, ["wrapS"] = 10497, ["wrapT"] = 10497 } }
                : null,
        };

        return new GltfOutput(JsonSerializer.Serialize(root, JsonOptions), bin.ToArray());
    }

    private static (byte[] Bytes, float[] Min, float[] Max) PositionBytes(ImportedGroup group)
    {
        var bytes = new byte[group.Positions.Count * 12];
        var min = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var max = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        int o = 0;
        foreach (Vec3 p in group.Positions)
        {
            WriteVec3(bytes, ref o, p.X, p.Y, -p.Z);
            Track(min, max, 0, p.X);
            Track(min, max, 1, p.Y);
            Track(min, max, 2, -p.Z);
        }

        return (bytes, min, max);
    }

    private static byte[] NormalBytes(ImportedGroup group)
    {
        var bytes = new byte[group.Normals.Count * 12];
        int o = 0;
        foreach (Vec3 n in group.Normals)
        {
            WriteVec3(bytes, ref o, n.X, n.Y, -n.Z);
        }

        return bytes;
    }

    private static byte[] UvBytes(ImportedGroup group)
    {
        var bytes = new byte[group.TexCoords.Count * 8];
        int o = 0;
        foreach (Uv uv in group.TexCoords)
        {
            WriteFloat(bytes, ref o, uv.U);
            WriteFloat(bytes, ref o, uv.V);
        }

        return bytes;
    }

    private static (byte[] Bytes, int ComponentType) IndexBytes(ImportedGroup group, int vcount)
    {
        bool wide = vcount > ushort.MaxValue;
        var bytes = new byte[group.Indices.Count * (wide ? 4 : 2)];
        int o = 0;
        for (int i = 0; i + 2 < group.Indices.Count; i += 3)
        {
            // Reverse winding: a, c, b.
            WriteIndex(bytes, ref o, group.Indices[i], wide);
            WriteIndex(bytes, ref o, group.Indices[i + 2], wide);
            WriteIndex(bytes, ref o, group.Indices[i + 1], wide);
        }

        return (bytes, wide ? UInt : UShort);
    }

    private static int AddAccessor(
        List<byte> bin, List<object> bufferViews, List<object> accessors,
        byte[] data, int componentType, int count, string type, int target,
        float[]? min = null, float[]? max = null)
    {
        while (bin.Count % 4 != 0)
        {
            bin.Add(0);
        }

        int offset = bin.Count;
        bin.AddRange(data);
        int bufferView = bufferViews.Count;
        bufferViews.Add(new Dictionary<string, object>
        {
            ["buffer"] = 0,
            ["byteOffset"] = offset,
            ["byteLength"] = data.Length,
            ["target"] = target,
        });

        int accessor = accessors.Count;
        var acc = new Dictionary<string, object?>
        {
            ["bufferView"] = bufferView,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
            ["min"] = min,
            ["max"] = max,
        };
        accessors.Add(acc);
        return accessor;
    }

    private static int AddMaterial(List<object> materials, List<object> images, List<object> textures, string texture)
    {
        int image = images.Count;
        images.Add(new Dictionary<string, object> { ["uri"] = texture, ["name"] = System.IO.Path.GetFileNameWithoutExtension(texture) });
        int tex = textures.Count;
        textures.Add(new Dictionary<string, object> { ["sampler"] = 0, ["source"] = image });

        int material = materials.Count;
        materials.Add(new Dictionary<string, object>
        {
            ["name"] = texture,
            ["doubleSided"] = true,
            ["pbrMetallicRoughness"] = new Dictionary<string, object>
            {
                ["baseColorTexture"] = new Dictionary<string, object> { ["index"] = tex },
                ["metallicFactor"] = 0.0,
                ["roughnessFactor"] = 1.0,
            },
        });
        return material;
    }

    private static void Track(float[] min, float[] max, int i, float v)
    {
        if (v < min[i])
        {
            min[i] = v;
        }

        if (v > max[i])
        {
            max[i] = v;
        }
    }

    private static void WriteVec3(byte[] b, ref int o, float x, float y, float z)
    {
        WriteFloat(b, ref o, x);
        WriteFloat(b, ref o, y);
        WriteFloat(b, ref o, z);
    }

    private static void WriteFloat(byte[] b, ref int o, float v)
    {
        BitConverter.TryWriteBytes(b.AsSpan(o), v);
        o += 4;
    }

    private static void WriteIndex(byte[] b, ref int o, int index, bool wide)
    {
        if (wide)
        {
            BitConverter.TryWriteBytes(b.AsSpan(o), (uint)index);
            o += 4;
        }
        else
        {
            BitConverter.TryWriteBytes(b.AsSpan(o), (ushort)index);
            o += 2;
        }
    }
}
