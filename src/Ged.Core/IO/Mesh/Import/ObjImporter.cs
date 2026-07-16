using System;
using System.Collections.Generic;
using System.Globalization;
using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Import;

/// <summary>
/// A self-contained Wavefront OBJ (+ companion MTL) importer. Parses v / vt / vn /
/// f statements into an <see cref="ImportedModel"/>, one group per <c>usemtl</c>
/// material, fan-triangulating polygons and de-duplicating each group's
/// position/uv/normal corner tuples. The diffuse texture name comes from the MTL
/// <c>map_Kd</c> when a <c>mtllib</c> is supplied, else the material name. Native
/// (no Assimp dependency) so the OBJ path always works. Adapted in spirit from
/// REDUX's ObjParser (MIT).
/// </summary>
public static class ObjImporter
{
    /// <summary>Parses OBJ <paramref name="objText"/>, resolving materials via <paramref name="mtlText"/> when present.</summary>
    public static ImportedModel Import(string objText, string? mtlText = null)
    {
        ArgumentNullException.ThrowIfNull(objText);

        Dictionary<string, string> materialTextures = mtlText is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ParseMtl(mtlText);

        var positions = new List<Vec3>();
        var texcoords = new List<Uv>();
        var normals = new List<Vec3>();

        var model = new ImportedModel { Format = ImportedFormat.Obj };
        var groups = new Dictionary<string, GroupBuilder>(StringComparer.Ordinal);
        var order = new List<GroupBuilder>();

        GroupBuilder Current(string material)
        {
            if (!groups.TryGetValue(material, out GroupBuilder? gb))
            {
                gb = new GroupBuilder
                {
                    Name = material,
                    Texture = ResolveTexture(material, materialTextures),
                };
                groups[material] = gb;
                order.Add(gb);
            }

            return gb;
        }

        string currentMaterial = string.Empty;

        foreach (string raw in objText.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "v" when t.Length >= 4:
                    positions.Add(new Vec3(F(t[1]), F(t[2]), F(t[3])));
                    break;
                case "vt" when t.Length >= 3:
                    texcoords.Add(new Uv(F(t[1]), F(t[2])));
                    break;
                case "vn" when t.Length >= 4:
                    normals.Add(new Vec3(F(t[1]), F(t[2]), F(t[3])));
                    break;
                case "usemtl":
                    currentMaterial = t.Length >= 2 ? t[1] : string.Empty;
                    break;
                case "f" when t.Length >= 4:
                    AddFace(Current(currentMaterial), t, positions, texcoords, normals);
                    break;
                default:
                    break; // o / g / s / mtllib / unknown: ignored (materials drive grouping)
            }
        }

        foreach (GroupBuilder gb in order)
        {
            gb.Finish();
            if (gb.Group.Positions.Count > 0)
            {
                model.Groups.Add(gb.Group);
            }
        }

        return model;
    }

    private static void AddFace(
        GroupBuilder gb, string[] t, List<Vec3> positions, List<Uv> texcoords, List<Vec3> normals)
    {
        // Resolve each corner "p/t/n" (1-based, negative = from end, t/n optional).
        int corners = t.Length - 1;
        Span<int> local = corners <= 32 ? stackalloc int[corners] : new int[corners];
        for (int i = 0; i < corners; i++)
        {
            local[i] = gb.Corner(t[i + 1], positions, texcoords, normals);
        }

        // Fan triangulation: (0, i, i+1).
        for (int i = 1; i + 1 < corners; i++)
        {
            gb.Group.Indices.Add(local[0]);
            gb.Group.Indices.Add(local[i]);
            gb.Group.Indices.Add(local[i + 1]);
        }
    }

    private static Dictionary<string, string> ParseMtl(string mtlText)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string current = string.Empty;
        foreach (string raw in mtlText.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t[0].Equals("newmtl", StringComparison.OrdinalIgnoreCase) && t.Length >= 2)
            {
                current = t[1];
            }
            else if (t[0].Equals("map_Kd", StringComparison.OrdinalIgnoreCase) && t.Length >= 2 && current.Length > 0)
            {
                // The texture path may contain spaces / options; take the final token.
                map[current] = System.IO.Path.GetFileName(t[^1]);
            }
        }

        return map;
    }

    private static string ResolveTexture(string material, IReadOnlyDictionary<string, string> materialTextures)
    {
        if (materialTextures.TryGetValue(material, out string? tex))
        {
            return tex;
        }

        // No MTL entry: use the material name as the texture name if it looks like one.
        return material;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private sealed class GroupBuilder
    {
        private readonly Dictionary<(int P, int T, int N), int> _dedup = new();
        private bool _anyUv;
        private bool _anyNormal;

        public string Name
        {
            get => Group.Name;
            set => Group.Name = value;
        }

        public string Texture
        {
            get => Group.Texture;
            set => Group.Texture = value;
        }

        public ImportedGroup Group { get; } = new();

        public int Corner(string spec, List<Vec3> positions, List<Uv> texcoords, List<Vec3> normals)
        {
            string[] parts = spec.Split('/');
            int pi = Index(parts, 0, positions.Count);
            int ti = parts.Length > 1 ? Index(parts, 1, texcoords.Count) : -1;
            int ni = parts.Length > 2 ? Index(parts, 2, normals.Count) : -1;

            var key = (pi, ti, ni);
            if (_dedup.TryGetValue(key, out int existing))
            {
                return existing;
            }

            int idx = Group.Positions.Count;
            Group.Positions.Add(pi >= 0 && pi < positions.Count ? positions[pi] : default);

            // Keep the UV/normal arrays index-parallel with positions (default when a
            // corner lacks one); Finish() drops an array the source never populated.
            if (ti >= 0 && ti < texcoords.Count)
            {
                Group.TexCoords.Add(texcoords[ti]);
                _anyUv = true;
            }
            else
            {
                Group.TexCoords.Add(default);
            }

            if (ni >= 0 && ni < normals.Count)
            {
                Group.Normals.Add(normals[ni]);
                _anyNormal = true;
            }
            else
            {
                Group.Normals.Add(default);
            }

            _dedup[key] = idx;
            return idx;
        }

        /// <summary>Drops the UV / normal arrays entirely if the source never provided any.</summary>
        public void Finish()
        {
            if (!_anyUv)
            {
                Group.TexCoords.Clear();
            }

            if (!_anyNormal)
            {
                Group.Normals.Clear();
            }
        }

        private static int Index(string[] parts, int slot, int count)
        {
            if (slot >= parts.Length || parts[slot].Length == 0)
            {
                return -1;
            }

            int v = int.Parse(parts[slot], CultureInfo.InvariantCulture);
            return v > 0 ? v - 1 : count + v; // 1-based, or negative from the end
        }
    }
}
