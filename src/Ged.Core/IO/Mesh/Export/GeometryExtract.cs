using System;
using System.Collections.Generic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Mesh.Import;
using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Export;

/// <summary>
/// Flattens RF geometry (compiled static geometry or a brush selection) into the
/// format-neutral <see cref="ImportedModel"/> that the mesh exporters (V3M / glTF /
/// OBJ / VRML) consume: triangles grouped by texture, in world space, carrying the
/// per-face plane normal and per-corner UVs. Portal / textureless faces are
/// dropped. Pure Ged.Core.
/// </summary>
public static class GeometryExtract
{
    /// <summary>Builds a model from the level's compiled static geometry (world space).</summary>
    public static ImportedModel FromLevelStatic(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        rfl.ParseAllKnownSections();

        var groups = new Dictionary<string, ImportedGroup>(StringComparer.OrdinalIgnoreCase);
        var order = new List<ImportedGroup>();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection geo)
            {
                Append(groups, order, geo.Geometry, Vec3.Zero, Mat3.Identity);
            }
        }

        return Finish(order);
    }

    /// <summary>Builds a model from a set of brushes (each transformed to world space).</summary>
    public static ImportedModel FromBrushes(IEnumerable<Brush> brushes)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        var groups = new Dictionary<string, ImportedGroup>(StringComparer.OrdinalIgnoreCase);
        var order = new List<ImportedGroup>();
        foreach (Brush b in brushes)
        {
            Append(groups, order, b.Geometry, b.Position, b.Rotation);
        }

        return Finish(order);
    }

    private static void Append(
        Dictionary<string, ImportedGroup> groups, List<ImportedGroup> order,
        Geometry g, Vec3 pos, Mat3 rot)
    {
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3 || f.Texture < 0 || f.Texture >= g.Textures.Count)
            {
                continue; // portal / textureless face
            }

            string texture = g.Textures[f.Texture];
            ImportedGroup group = GroupFor(groups, order, texture);

            Vec3 worldNormal = rot.Transform(f.Plane.Normal).Normalized();

            // Fan-triangulate the (already convex) face polygon: (0, i, i+1).
            for (int i = 1; i + 1 < f.Vertices.Count; i++)
            {
                AppendCorner(group, g, f.Vertices[0], pos, rot, worldNormal);
                AppendCorner(group, g, f.Vertices[i], pos, rot, worldNormal);
                AppendCorner(group, g, f.Vertices[i + 1], pos, rot, worldNormal);
            }
        }
    }

    private static void AppendCorner(ImportedGroup group, Geometry g, FaceVertex fv, Vec3 pos, Mat3 rot, Vec3 normal)
    {
        Vec3 local = fv.Index >= 0 && fv.Index < g.Vertices.Count ? g.Vertices[fv.Index] : Vec3.Zero;
        Vec3 world = pos.Add(rot.Transform(local));
        int idx = group.Positions.Count;
        group.Positions.Add(world);
        group.Normals.Add(normal);
        group.TexCoords.Add(fv.TextureCoords);
        group.Indices.Add(idx);
    }

    private static ImportedGroup GroupFor(
        Dictionary<string, ImportedGroup> groups, List<ImportedGroup> order, string texture)
    {
        if (!groups.TryGetValue(texture, out ImportedGroup? group))
        {
            group = new ImportedGroup { Texture = texture, Name = texture };
            groups[texture] = group;
            order.Add(group);
        }

        return group;
    }

    private static ImportedModel Finish(List<ImportedGroup> order)
    {
        var model = new ImportedModel { Format = ImportedFormat.Unknown };
        foreach (ImportedGroup g in order)
        {
            if (g.Indices.Count >= 3)
            {
                model.Groups.Add(g);
            }
        }

        return model;
    }
}
