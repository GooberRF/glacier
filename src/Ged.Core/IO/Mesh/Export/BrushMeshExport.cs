using System;
using System.Collections.Generic;
using Ged.Core.IO.Mesh.Import;
using Ged.Core.Model;

namespace Ged.Core.IO.Mesh.Export;

/// <summary>
/// Alpine's "To Mesh": converts a brush selection into a single texture-batched
/// <see cref="V3dFile"/> (normals + UVs), optionally recentring the geometry on the
/// origin (returning the offset so a Mesh object can be placed where the brushes
/// were). Uses <see cref="V3dMeshBuilder"/> / <see cref="V3dWriter"/>.
/// </summary>
public static class BrushMeshExport
{
    /// <summary>
    /// Builds a V3M from <paramref name="brushes"/>. When <paramref name="resetOrigin"/>
    /// is set the mesh is recentred on (0,0,0) and the world-space centre is returned
    /// as <paramref name="origin"/> (the point to place the replacement Mesh object);
    /// otherwise the mesh keeps world coordinates and <paramref name="origin"/> is zero.
    /// </summary>
    public static V3dFile ToV3d(string meshName, IReadOnlyList<Brush> brushes, bool resetOrigin, out Vec3 origin)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        ImportedModel model = GeometryExtract.FromBrushes(brushes);
        origin = Vec3.Zero;

        if (resetOrigin && model.Groups.Count > 0)
        {
            origin = Center(model);
            foreach (ImportedGroup g in model.Groups)
            {
                for (int i = 0; i < g.Positions.Count; i++)
                {
                    g.Positions[i] = g.Positions[i].Sub(origin);
                }
            }
        }

        return V3dMeshBuilder.Build(meshName, model);
    }

    private static Vec3 Center(ImportedModel model)
    {
        var min = new Vec3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vec3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (ImportedGroup g in model.Groups)
        {
            foreach (Vec3 p in g.Positions)
            {
                min = new Vec3(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z));
                max = new Vec3(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z));
            }
        }

        return min.Add(max).Scale(0.5f);
    }
}
