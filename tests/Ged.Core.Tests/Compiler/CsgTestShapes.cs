using System;
using System.Collections.Generic;
using Ged.Core.Compiler;
using Ged.Core.Model;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Shared builders for CSG tests: axis-aligned boxes whose six faces point
/// OUTWARD (the solid convention BspSolid expects), with simple planar UVs.
/// </summary>
public static class CsgTestShapes
{
    /// <summary>An outward-faced box spanning [min,max] with the given attributes.</summary>
    public static List<CsgFace> Box(Vec3 min, Vec3 max, string texture = "wall", bool fromAir = true)
    {
        var faces = new List<CsgFace>();

        void Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
        {
            var verts = new List<CsgVertex>
            {
                new(a, default), new(b, default), new(c, default), new(d, default),
            };
            var f = new CsgFace
            {
                Vertices = verts,
                Plane = CsgPlane.FromPolygon(verts),
                Texture = texture,
                FromAir = fromAir,
            };
            faces.Add(f);
        }

        // Corners
        var v000 = new Vec3(min.X, min.Y, min.Z);
        var v100 = new Vec3(max.X, min.Y, min.Z);
        var v110 = new Vec3(max.X, max.Y, min.Z);
        var v010 = new Vec3(min.X, max.Y, min.Z);
        var v001 = new Vec3(min.X, min.Y, max.Z);
        var v101 = new Vec3(max.X, min.Y, max.Z);
        var v111 = new Vec3(max.X, max.Y, max.Z);
        var v011 = new Vec3(min.X, max.Y, max.Z);

        Quad(v100, v110, v111, v101); // +X
        Quad(v000, v001, v011, v010); // -X
        Quad(v010, v011, v111, v110); // +Y
        Quad(v000, v100, v101, v001); // -Y
        Quad(v001, v101, v111, v011); // +Z
        Quad(v000, v010, v110, v100); // -Z

        return faces;
    }

    /// <summary>Sum of face areas — used to compare boolean results structurally.</summary>
    public static float TotalArea(IEnumerable<CsgFace> faces)
    {
        float sum = 0f;
        foreach (CsgFace f in faces)
        {
            sum += f.Area();
        }

        return sum;
    }
}
