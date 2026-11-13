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

    /// <summary>
    /// Loads <paramref name="path"/> reading ONLY the model file itself and, for an OBJ, a <c>mtllib</c>
    /// companion named without any folder part (so it lies in the same folder). Assimp formats are parsed
    /// from memory, so a model that keeps data in separate files (a .gltf with external buffers) fails
    /// rather than reading them. Used where file access is confined (scripts).
    /// </summary>
    public static ImportedModel LoadConfined(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".obj")
        {
            string objText = File.ReadAllText(path);
            string? mtlText = FindCompanionMtl(path, objText, sameFolderOnly: true);
            return ObjImporter.Import(objText, mtlText);
        }

        return AssimpImporter.ImportFromMemory(File.ReadAllBytes(path), ext, Path.GetFileName(path));
    }

    /// <summary>Reads the OBJ's <c>mtllib</c> companion from the same directory, if present.</summary>
    private static string? FindCompanionMtl(string objPath, string objText, bool sameFolderOnly = false)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? string.Empty;
        foreach (string raw in objText.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("mtllib ", StringComparison.OrdinalIgnoreCase))
            {
                string name = line[7..].Trim();
                if (sameFolderOnly && (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name is "." or ".."))
                {
                    continue; // a companion outside the model's folder is never read
                }

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
