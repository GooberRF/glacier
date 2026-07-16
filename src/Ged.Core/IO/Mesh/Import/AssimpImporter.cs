using System;
using System.Numerics;
using Ged.Core.Model;
using Silk.NET.Assimp;
using AiFace = Silk.NET.Assimp.Face;
using AiMesh = Silk.NET.Assimp.Mesh;
using AiScene = Silk.NET.Assimp.Scene;

namespace Ged.Core.IO.Mesh.Import;

/// <summary>
/// Imports FBX / glTF / GLB / DAE (and any other Assimp-supported) mesh into an
/// <see cref="ImportedModel"/> via the native Assimp library (Silk.NET binding).
/// The scene graph is flattened (PreTransformVertices) so every mesh arrives in a
/// single space; meshes are grouped by their material's diffuse texture. The
/// native library is loaded lazily; <see cref="IsAvailable"/> reports whether it
/// could be initialised so callers can fall back gracefully.
/// </summary>
public static unsafe class AssimpImporter
{
    private static readonly Lazy<Assimp?> Api = new(() =>
    {
        try
        {
            return Assimp.GetApi();
        }
        catch (Exception)
        {
            return null;
        }
    });

    /// <summary>True when the native Assimp library loaded successfully.</summary>
    public static bool IsAvailable => Api.Value is not null;

    /// <summary>Imports a mesh file at <paramref name="path"/>. Throws when Assimp is unavailable or the file cannot be read.</summary>
    public static ImportedModel Import(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Assimp assimp = Api.Value ?? throw new InvalidOperationException("Native Assimp library is not available.");

        const uint flags = (uint)(
            PostProcessSteps.Triangulate |
            PostProcessSteps.GenerateSmoothNormals |
            PostProcessSteps.JoinIdenticalVertices |
            PostProcessSteps.PreTransformVertices |
            PostProcessSteps.FindDegenerates |
            PostProcessSteps.ImproveCacheLocality);

        AiScene* scene = assimp.ImportFile(path, flags);
        if (scene is null || scene->MRootNode is null)
        {
            string err = assimp.GetErrorStringS();
            throw new V3dFormatException($"Assimp failed to import '{path}': {err}");
        }

        try
        {
            var model = new ImportedModel { Format = DetectFormat(path) };
            for (int m = 0; m < scene->MNumMeshes; m++)
            {
                AiMesh* mesh = scene->MMeshes[m];
                if (mesh is null || mesh->MNumVertices == 0 || mesh->MNumFaces == 0)
                {
                    continue;
                }

                model.Groups.Add(BuildGroup(assimp, scene, mesh));
            }

            return model;
        }
        finally
        {
            assimp.ReleaseImport(scene);
        }
    }

    private static ImportedGroup BuildGroup(Assimp assimp, AiScene* scene, AiMesh* mesh)
    {
        var group = new ImportedGroup
        {
            Name = mesh->MName.AsString,
            Texture = ResolveTexture(assimp, scene, mesh->MMaterialIndex),
        };

        bool hasNormals = mesh->MNormals is not null;
        Vector3* uv0 = mesh->MTextureCoords[0];
        bool hasUvs = uv0 is not null;

        for (int v = 0; v < mesh->MNumVertices; v++)
        {
            Vector3 p = mesh->MVertices[v];
            group.Positions.Add(new Vec3(p.X, p.Y, p.Z));
            if (hasNormals)
            {
                Vector3 n = mesh->MNormals[v];
                group.Normals.Add(new Vec3(n.X, n.Y, n.Z));
            }

            if (hasUvs)
            {
                Vector3 uv = uv0[v];
                group.TexCoords.Add(new Uv(uv.X, uv.Y));
            }
        }

        for (int f = 0; f < mesh->MNumFaces; f++)
        {
            AiFace face = mesh->MFaces[f];
            if (face.MNumIndices != 3)
            {
                continue; // Triangulate guarantees 3, but skip any stray line/point primitive
            }

            group.Indices.Add((int)face.MIndices[0]);
            group.Indices.Add((int)face.MIndices[1]);
            group.Indices.Add((int)face.MIndices[2]);
        }

        return group;
    }

    private static string ResolveTexture(Assimp assimp, AiScene* scene, uint materialIndex)
    {
        if (materialIndex >= scene->MNumMaterials)
        {
            return string.Empty;
        }

        Material* mat = scene->MMaterials[materialIndex];
        foreach (TextureType type in new[] { TextureType.Diffuse, TextureType.BaseColor })
        {
            if (assimp.GetMaterialTextureCount(mat, type) == 0)
            {
                continue;
            }

            AssimpString path = default;
            assimp.GetMaterialTexture(mat, type, 0, &path, null, null, null, null, null, null);
            string name = path.AsString;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return System.IO.Path.GetFileName(name.Replace('\\', '/'));
            }
        }

        return string.Empty;
    }

    private static ImportedFormat DetectFormat(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".gltf" or ".glb" => ImportedFormat.Gltf,
            ".fbx" => ImportedFormat.Fbx,
            ".dae" => ImportedFormat.Collada,
            ".obj" => ImportedFormat.Obj,
            _ => ImportedFormat.Unknown,
        };
    }
}
