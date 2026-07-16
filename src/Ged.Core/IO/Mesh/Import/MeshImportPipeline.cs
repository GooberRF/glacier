using System;
using System.Collections.Generic;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Import;

/// <summary>
/// Turns a format-neutral <see cref="ImportedModel"/> into RF geometry: applies the
/// scale + axis conversion (and winding fix), then produces either one brush per
/// material group (triangulated faces, UVs preserved with a planar fallback) or a
/// single batched <see cref="V3dFile"/> for a mesh object. Pure Ged.Core.
/// </summary>
public static class MeshImportPipeline
{
    /// <summary>
    /// Applies <paramref name="options"/> (uniform scale + axis conversion + winding
    /// flip) to <paramref name="model"/> in place. Positions are scaled then axis-
    /// converted; normals are axis-converted and renormalised; triangle winding is
    /// reversed when the effective flip is set.
    /// </summary>
    public static void ApplyTransform(ImportedModel model, MeshImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        bool flip = options.EffectiveFlipWinding;
        foreach (ImportedGroup group in model.Groups)
        {
            for (int i = 0; i < group.Positions.Count; i++)
            {
                group.Positions[i] = MeshAxis.Convert(group.Positions[i].Scale(options.Scale), options.Axis);
            }

            for (int i = 0; i < group.Normals.Count; i++)
            {
                group.Normals[i] = MeshAxis.Convert(group.Normals[i], options.Axis).Normalized();
            }

            if (flip)
            {
                ReverseWinding(group.Indices);
            }
        }
    }

    /// <summary>
    /// Builds one <see cref="Brush"/> per material group. Faces are the group's
    /// triangles (degenerate ones dropped); UVs are preserved per corner, or planar-
    /// mapped when the source had none.
    /// </summary>
    public static IReadOnlyList<Brush> ToBrushes(ImportedModel model, MeshImportOptions options, Func<int>? allocateUid = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        int next = 1;
        Func<int> alloc = allocateUid ?? (() => next++);

        var brushes = new List<Brush>();
        foreach (ImportedGroup group in model.Groups)
        {
            if (group.Indices.Count < 3)
            {
                continue;
            }

            Geometry g = BuildGroupGeometry(group, options);
            if (g.Faces.Count == 0)
            {
                continue;
            }

            brushes.Add(new Brush
            {
                Uid = alloc(),
                Position = Vec3.Zero,
                Rotation = Mat3.Identity,
                Geometry = g,
                Flags = 0, // solid, non-portal, non-detail
                Life = -1,
                State = BrushState.Normal,
            });
        }

        return brushes;
    }

    /// <summary>Builds a single batched <see cref="V3dFile"/> from every group (mesh-object target).</summary>
    public static V3dFile ToV3dFile(ImportedModel model, string meshName)
    {
        ArgumentNullException.ThrowIfNull(model);
        return V3dMeshBuilder.Build(meshName, model);
    }

    private static Geometry BuildGroupGeometry(ImportedGroup group, MeshImportOptions options)
    {
        var g = new Geometry { Name = "imported" };
        bool hasTexture = !string.IsNullOrWhiteSpace(group.Texture);
        g.Textures.Add(hasTexture ? group.Texture : BrushCreateParams.StockWallTexture);
        bool hasUvs = group.HasTexCoords;

        for (int i = 0; i + 2 < group.Indices.Count; i += 3)
        {
            int a = group.Indices[i];
            int b = group.Indices[i + 1];
            int c = group.Indices[i + 2];

            int v0 = GeometryUtil.AddVertex(g, group.Positions[a]);
            int v1 = GeometryUtil.AddVertex(g, group.Positions[b]);
            int v2 = GeometryUtil.AddVertex(g, group.Positions[c]);
            if (v0 == v1 || v1 == v2 || v0 == v2)
            {
                continue; // degenerate triangle (collapsed after position weld) — rejected
            }

            var face = new Face { Texture = 0, SurfaceIndex = -1, RoomIndex = -1, FaceId = g.Faces.Count };
            face.Vertices.Add(new FaceVertex { Index = v0, TextureCoords = hasUvs ? group.TexCoords[a] : default });
            face.Vertices.Add(new FaceVertex { Index = v1, TextureCoords = hasUvs ? group.TexCoords[b] : default });
            face.Vertices.Add(new FaceVertex { Index = v2, TextureCoords = hasUvs ? group.TexCoords[c] : default });
            g.Faces.Add(face);
        }

        GeometryUtil.RecomputeAllPlanes(g);

        // Imported groups with no material texture would otherwise reference a
        // nonexistent "default.tga" and render untextured. Give them the same per-face
        // orientation defaults (floor/wall/ceiling) every other creation path applies.
        if (!hasTexture)
        {
            BrushFactory.ApplyOrientationTextures(
                g,
                BrushCreateParams.StockFloorTexture,
                BrushCreateParams.StockWallTexture,
                BrushCreateParams.StockCeilingTexture);
        }

        if (!hasUvs)
        {
            GeometryUtil.AssignAllPlanarUv(g, options.PlanarTileMeters); // planar fallback
        }

        return g;
    }

    private static void ReverseWinding(List<int> indices)
    {
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        }
    }
}
