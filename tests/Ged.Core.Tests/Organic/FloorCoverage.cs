using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Model;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// Test helpers over a compiled level (ORGANIC-GEOMETRY-SPEC §4.3 gates 3 and 4): duplicate horizontal coverage
/// (two non-detail horizontal faces over one spot within 1 mm of each other = z-fighting) and the walk floors
/// under a point.
/// </summary>
internal static class FloorCoverage
{
    private sealed record Flat(Vec3[] Corners, float Y, float MinX, float MaxX, float MinZ, float MaxZ);

    /// <summary>
    /// Samples a <paramref name="step"/> m XZ grid; at each sample, the heights of the compiled non-detail faces
    /// with |n.y| ≥ 0.999 whose outline contains it. Returns the samples where two such heights are within 1 mm.
    /// </summary>
    public static List<(float X, float Z, float Y)> Duplicates(Geometry g, float step = 0.25f)
    {
        var flats = new List<Flat>();
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3 || ((FaceFlags)f.Flags & FaceFlags.IsDetail) != 0 || MathF.Abs(f.Plane.Normal.Y) < 0.999f)
            {
                continue;
            }

            Vec3[] c = f.Vertices.Select(v => g.Vertices[v.Index]).ToArray();
            flats.Add(new Flat(c, c.Average(v => v.Y), c.Min(v => v.X), c.Max(v => v.X), c.Min(v => v.Z), c.Max(v => v.Z)));
        }

        var hits = new Dictionary<(int, int), List<float>>();
        foreach (Flat f in flats)
        {
            for (int i = (int)MathF.Ceiling(f.MinX / step); i * step <= f.MaxX; i++)
            {
                for (int k = (int)MathF.Ceiling(f.MinZ / step); k * step <= f.MaxZ; k++)
                {
                    if (Inside(f.Corners, i * step, k * step, 0.0005f, strict: true))
                    {
                        (hits.TryGetValue((i, k), out List<float>? l) ? l : hits[(i, k)] = new List<float>()).Add(f.Y);
                    }
                }
            }
        }

        var dup = new List<(float, float, float)>();
        foreach (((int i, int k), List<float> ys) in hits)
        {
            ys.Sort();
            for (int n = 1; n < ys.Count; n++)
            {
                if (ys[n] - ys[n - 1] < 0.001f)
                {
                    dup.Add((i * step, k * step, ys[n]));
                    break;
                }
            }
        }

        return dup;
    }

    /// <summary>The heights at (x, z) of the compiled non-detail upward faces (n.y ≥ 0.5) whose outline strictly contains the point (a point on an edge shared by two faces belongs to neither).</summary>
    public static List<double> FloorsAt(Geometry g, double x, double z, bool strict = true)
    {
        var ys = new List<double>();
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3 || ((FaceFlags)f.Flags & FaceFlags.IsDetail) != 0 || f.Plane.Normal.Y < 0.5f)
            {
                continue;
            }

            Vec3[] c = f.Vertices.Select(v => g.Vertices[v.Index]).ToArray();
            if (!Inside(c, (float)x, (float)z, 0.0005f, strict))
            {
                continue;
            }

            // The plane through the corners (Newell, double precision), evaluated at (x, z).
            double nx = 0, ny = 0, nz = 0, cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < c.Length; i++)
            {
                Vec3 a = c[i], b = c[(i + 1) % c.Length];
                nx += ((double)a.Y - b.Y) * ((double)a.Z + b.Z);
                ny += ((double)a.Z - b.Z) * ((double)a.X + b.X);
                nz += ((double)a.X - b.X) * ((double)a.Y + b.Y);
                cx += a.X;
                cy += a.Y;
                cz += a.Z;
            }

            cx /= c.Length;
            cy /= c.Length;
            cz /= c.Length;
            ys.Add(cy - (((nx * (x - cx)) + (nz * (z - cz))) / ny));
        }

        return ys;
    }

    /// <summary>
    /// Inside the XZ outline (even-odd rule, so merged output faces that are not convex work too);
    /// <paramref name="strict"/>: a point within the tolerance of an edge (shared by two faces) is outside.
    /// </summary>
    private static bool Inside(Vec3[] poly, float x, float z, float tolerance, bool strict = false)
    {
        bool inside = false;
        for (int i = 0, k = poly.Length - 1; i < poly.Length; k = i++)
        {
            Vec3 a = poly[i], b = poly[k];
            float ex = b.X - a.X, ez = b.Z - a.Z;
            float len2 = (ex * ex) + (ez * ez);
            if (len2 > 1e-12f)
            {
                float t = Math.Clamp((((x - a.X) * ex) + ((z - a.Z) * ez)) / len2, 0f, 1f);
                float px = a.X + (t * ex) - x, pz = a.Z + (t * ez) - z;
                if ((px * px) + (pz * pz) <= tolerance * tolerance)
                {
                    return !strict;
                }
            }

            if ((a.Z > z) != (b.Z > z) && x < a.X + ((b.X - a.X) * (z - a.Z) / (b.Z - a.Z)))
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
