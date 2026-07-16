using System.Collections.Generic;
using Ged.Core.Compiler;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Carve (stock permanent boolean): subtracts a cutter brush's shape from a
/// target brush's geometry using the CSG kernel, producing a new brush-local
/// <see cref="Geometry"/> for the target (outer shell + cavity walls). Pure and
/// undo-friendly — the caller swaps the returned geometry in through the undo
/// system. Returns null when the brushes do not intersect.
/// </summary>
public static class CarveOps
{
    /// <summary>Returns the target's geometry with the cutter's volume removed, or null if disjoint.</summary>
    public static Geometry? Carve(Brush target, Brush cutter)
    {
        List<CsgFace> targetWorld = BrushWorld.ToWorldFaces(target, 0, out _);
        List<CsgFace> cutterWorld = BrushWorld.ToWorldFaces(cutter, 0, out _);
        if (targetWorld.Count == 0 || cutterWorld.Count == 0 || !Overlaps(targetWorld, cutterWorld))
        {
            return null;
        }

        List<CsgFace> result = BspSolid.Subtract(targetWorld, cutterWorld);
        if (result.Count == 0)
        {
            return null;
        }

        // Rebuild brush-local geometry (world → target local) with a welded pool.
        var g = new Geometry { Name = target.Geometry.Name };
        var pool = new List<Vec3>();
        Mat3 rot = target.Rotation;
        Vec3 pos = target.Position;

        int LocalIndex(Vec3 world)
        {
            Vec3 local = rot.InverseTransform(world.Sub(pos));
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i].ApproxEquals(local, 1e-4f))
                {
                    return i;
                }
            }

            pool.Add(local);
            return pool.Count - 1;
        }

        int faceId = 0;
        foreach (CsgFace cf in result)
        {
            if (cf.Vertices.Count < 3 || cf.Area() < 1e-6f)
            {
                continue;
            }

            int tex = GeometryUtil.EnsureTexture(g, string.IsNullOrEmpty(cf.Texture) ? BrushCreateParams.DefaultTexture : cf.Texture);
            var face = new Face { Texture = tex, SurfaceIndex = -1, RoomIndex = -1, FaceId = faceId++ };
            foreach (CsgVertex v in cf.Vertices)
            {
                face.Vertices.Add(new FaceVertex { Index = LocalIndex(v.Position), TextureCoords = v.Uv });
            }

            g.Faces.Add(face);
        }

        g.Vertices = pool;
        GeometryUtil.RecomputeAllPlanes(g);
        return g.Faces.Count >= 4 ? g : null;
    }

    private static bool Overlaps(List<CsgFace> a, List<CsgFace> b)
    {
        (Vec3 amin, Vec3 amax) = Bounds(a);
        (Vec3 bmin, Vec3 bmax) = Bounds(b);
        return amin.X <= bmax.X && amax.X >= bmin.X &&
               amin.Y <= bmax.Y && amax.Y >= bmin.Y &&
               amin.Z <= bmax.Z && amax.Z >= bmin.Z;
    }

    private static (Vec3 Min, Vec3 Max) Bounds(List<CsgFace> faces)
    {
        var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (CsgFace f in faces)
        {
            f.GrowAabb(ref min, ref max);
        }

        return (min, max);
    }
}
