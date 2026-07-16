using System;
using System.IO;
using System.Linq;

namespace Ged.Core.IO.Mesh.Import;

/// <summary>
/// Front door for generic mesh import: dispatches by file extension to the native
/// OBJ parser (which also resolves the companion .mtl) or to Assimp for everything
/// else (FBX / glTF / GLB / DAE). Returns the format-neutral <see cref="ImportedModel"/>
/// that <see cref="MeshImportPipeline"/> converts into brushes or a V3M.
/// </summary>
public static class MeshImporter
{
    /// <summary>Extensions the importer accepts (for the file-picker filter).</summary>
    public static readonly string[] SupportedExtensions = { ".obj", ".gltf", ".glb", ".fbx", ".dae" };

    /// <summary>True when <paramref name="path"/> has an importable extension.</summary>
    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Loads <paramref name="path"/> into an <see cref="ImportedModel"/>.</summary>
    public static ImportedModel Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".obj")
        {
            string objText = File.ReadAllText(path);
            string? mtlText = FindCompanionMtl(path, objText);
            return ObjImporter.Import(objText, mtlText);
        }

        return AssimpImporter.Import(path);
    }

    /// <summary>Reads the OBJ's <c>mtllib</c> companion from the same directory, if present.</summary>
    private static string? FindCompanionMtl(string objPath, string objText)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? string.Empty;
        foreach (string raw in objText.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("mtllib ", StringComparison.OrdinalIgnoreCase))
            {
                string name = line[7..].Trim();
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }
            }
        }

        return null;
    }
}
