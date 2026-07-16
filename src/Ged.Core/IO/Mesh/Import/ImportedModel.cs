using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Import;

/// <summary>
/// A format-neutral triangle mesh, one <see cref="ImportedGroup"/> per source
/// material. The OBJ / Assimp importers all produce this shape; the import
/// pipeline (axis/scale conversion, brush or V3M generation) consumes it. Vertex
/// arrays are index-parallel; <see cref="ImportedGroup.Indices"/> is a flat
/// triangle list into them.
/// </summary>
public sealed class ImportedModel
{
    /// <summary>Per-material triangle groups.</summary>
    public List<ImportedGroup> Groups { get; } = new();

    /// <summary>The source format (drives the default axis-conversion suggestion).</summary>
    public ImportedFormat Format { get; set; } = ImportedFormat.Unknown;

    /// <summary>Diffuse texture file names referenced by any group (deduped, for VFS matching / reports).</summary>
    public IEnumerable<string> ReferencedTextures
    {
        get
        {
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (ImportedGroup group in Groups)
            {
                if (!string.IsNullOrWhiteSpace(group.Texture) && seen.Add(group.Texture))
                {
                    yield return group.Texture;
                }
            }
        }
    }

    public int TotalVertices
    {
        get
        {
            int n = 0;
            foreach (ImportedGroup g in Groups)
            {
                n += g.Positions.Count;
            }

            return n;
        }
    }

    public int TotalTriangles
    {
        get
        {
            int n = 0;
            foreach (ImportedGroup g in Groups)
            {
                n += g.Indices.Count / 3;
            }

            return n;
        }
    }
}

/// <summary>One material's worth of geometry: index-parallel vertex arrays + a flat triangle list.</summary>
public sealed class ImportedGroup
{
    /// <summary>The diffuse texture file name (from the material), or empty when the source had none.</summary>
    public string Texture { get; set; } = string.Empty;

    /// <summary>The source material/object name (for reporting).</summary>
    public string Name { get; set; } = string.Empty;

    public List<Vec3> Positions { get; } = new();

    /// <summary>Per-vertex normals, or empty when the source provided none.</summary>
    public List<Vec3> Normals { get; } = new();

    /// <summary>Per-vertex UVs, or empty when the source provided none (planar fallback then applies).</summary>
    public List<Uv> TexCoords { get; } = new();

    /// <summary>Flat triangle index list (multiple of 3) into the vertex arrays.</summary>
    public List<int> Indices { get; } = new();

    public bool HasNormals => Normals.Count == Positions.Count && Positions.Count > 0;

    public bool HasTexCoords => TexCoords.Count == Positions.Count && Positions.Count > 0;
}

/// <summary>Recognised import source formats.</summary>
public enum ImportedFormat
{
    Unknown,
    Obj,
    Gltf,
    Fbx,
    Collada,
}
