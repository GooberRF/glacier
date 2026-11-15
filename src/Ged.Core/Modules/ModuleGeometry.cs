using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ged.Core.Model;

namespace Ged.Core.Modules;

/// <summary>A double-precision 3D vector for the module analysis (world and module-local frames).</summary>
public readonly record struct D3(double X, double Y, double Z)
{
    public static D3 Zero => default;

    public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static D3 operator -(D3 a) => new(-a.X, -a.Y, -a.Z);

    public static D3 operator *(D3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public double Dot(D3 b) => (X * b.X) + (Y * b.Y) + (Z * b.Z);

    public D3 Cross(D3 b) => new((Y * b.Z) - (Z * b.Y), (Z * b.X) - (X * b.Z), (X * b.Y) - (Y * b.X));

    public double Length => Math.Sqrt(Dot(this));

    public D3 Normalized()
    {
        double l = Length;
        return l > 1e-15 ? this * (1.0 / l) : this;
    }

    /// <summary>Component by axis index (0 = X, 1 = Y, 2 = Z).</summary>
    public double this[int axis] => axis switch { 0 => X, 1 => Y, _ => Z };

    public D3 With(int axis, double value) => axis switch
    {
        0 => this with { X = value },
        1 => this with { Y = value },
        _ => this with { Z = value },
    };

    public static D3 Min(D3 a, D3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    public static D3 Max(D3 a, D3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    public static D3 From(Vec3 v) => new(v.X, v.Y, v.Z);

    public Vec3 ToVec3() => new((float)X, (float)Y, (float)Z);
}

/// <summary>
/// Yaw-only rigid frames: <c>world = origin + RotY(yaw)·local</c>, the same convention as a Lua
/// <c>{yaw = deg}</c> rotation (yaw 0 keeps local +Z forward on world +Z; yaw 90 turns local +Z onto world +X).
/// </summary>
public static class YawFrame
{
    /// <summary>Rotates a local vector by <paramref name="yawDegrees"/> about +Y.</summary>
    public static D3 Rotate(D3 v, double yawDegrees)
    {
        double r = yawDegrees * Math.PI / 180.0;
        double c = Math.Cos(r), s = Math.Sin(r);
        if (Math.Abs(c) < 1e-15)
        {
            c = 0;
        }

        if (Math.Abs(s) < 1e-15)
        {
            s = 0;
        }

        return new D3((v.X * c) + (v.Z * s), v.Y, (-v.X * s) + (v.Z * c));
    }

    /// <summary>local → world.</summary>
    public static D3 ToWorld(D3 local, D3 origin, double yawDegrees) => origin + Rotate(local, yawDegrees);

    /// <summary>world → local.</summary>
    public static D3 ToLocal(D3 world, D3 origin, double yawDegrees) => Rotate(world - origin, -yawDegrees);

    /// <summary>The engine orientation matrix of a yaw (rows Right, Up, Forward as <see cref="Mat3"/> stores them).</summary>
    public static Mat3 Matrix(double yawDegrees)
    {
        D3 right = Rotate(new D3(1, 0, 0), yawDegrees);
        D3 forward = Rotate(new D3(0, 0, 1), yawDegrees);
        return new Mat3(forward.ToVec3(), right.ToVec3(), new Vec3(0, 1, 0));
    }

    /// <summary>Normalises an angle to [0, 360).</summary>
    public static double Normalize(double degrees)
    {
        double d = degrees % 360.0;
        if (d < 0)
        {
            d += 360.0;
        }

        return d >= 360.0 - 1e-9 ? 0 : d;
    }
}

/// <summary>
/// A brush as a world-space polyhedron: vertices, polygon faces (vertex indices), outward face planes,
/// bounds, and whether it is convex. Built once per brush for the solidity index and the harvester.
/// </summary>
public sealed class WorldPolyhedron
{
    private WorldPolyhedron(D3[] vertices, int[][] faces, D3[] normals, double[] offsets, D3 min, D3 max, bool closed, bool convex, double volume)
    {
        Vertices = vertices;
        Faces = faces;
        Normals = normals;
        Offsets = offsets;
        Min = min;
        Max = max;
        Closed = closed;
        Convex = convex;
        Volume = volume;
    }

    /// <summary>World-space vertices.</summary>
    public D3[] Vertices { get; }

    /// <summary>Face polygons as indices into <see cref="Vertices"/> (the brush's own face order).</summary>
    public int[][] Faces { get; }

    /// <summary>Outward unit normals per face (zero for a degenerate face).</summary>
    public D3[] Normals { get; }

    /// <summary>Plane offsets per face: a point p is on the face plane when <c>Normals[i]·p == Offsets[i]</c>.</summary>
    public double[] Offsets { get; }

    public D3 Min { get; }

    public D3 Max { get; }

    /// <summary>True when the brush has at least four faces and encloses a volume (a "plane" brush encloses nothing).</summary>
    public bool Closed { get; }

    public bool Convex { get; }

    /// <summary>The enclosed volume (m³; 0 for an open brush).</summary>
    public double Volume { get; }

    public D3 Size => Max - Min;

    /// <summary>Builds the polyhedron of <paramref name="b"/> (its geometry posed by Position + Rotation).</summary>
    public static WorldPolyhedron FromBrush(Brush b)
    {
        ArgumentNullException.ThrowIfNull(b);
        var verts = new D3[b.Geometry.Vertices.Count];
        D3 pos = D3.From(b.Position);
        D3 r = D3.From(b.Rotation.Right), u = D3.From(b.Rotation.Up), f = D3.From(b.Rotation.Forward);
        for (int i = 0; i < verts.Length; i++)
        {
            Vec3 v = b.Geometry.Vertices[i];
            verts[i] = pos + (r * v.X) + (u * v.Y) + (f * v.Z);
        }

        var faces = new List<int[]>(b.Geometry.Faces.Count);
        foreach (Face face in b.Geometry.Faces)
        {
            var idx = new List<int>(face.Vertices.Count);
            foreach (FaceVertex fv in face.Vertices)
            {
                if (fv.Index >= 0 && fv.Index < verts.Length && (idx.Count == 0 || idx[^1] != fv.Index))
                {
                    idx.Add(fv.Index);
                }
            }

            while (idx.Count > 1 && idx[0] == idx[^1])
            {
                idx.RemoveAt(idx.Count - 1);
            }

            faces.Add(idx.ToArray());
        }

        return Create(verts, faces.ToArray());
    }

    /// <summary>Builds a polyhedron from explicit vertices and polygons (any winding; orientation is measured).</summary>
    public static WorldPolyhedron Create(D3[] verts, int[][] faces)
    {
        D3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
        bool any = false;
        foreach (int[] face in faces)
        {
            foreach (int i in face)
            {
                min = D3.Min(min, verts[i]);
                max = D3.Max(max, verts[i]);
                any = true;
            }
        }

        if (!any)
        {
            min = max = D3.Zero;
        }

        // Signed volume about the bounds centre (divergence theorem over fan triangles).
        D3 c = (min + max) * 0.5;
        double vol6 = 0;
        var normals = new D3[faces.Length];
        for (int fi = 0; fi < faces.Length; fi++)
        {
            int[] face = faces[fi];
            normals[fi] = Newell(verts, face);
            for (int k = 1; k + 1 < face.Length; k++)
            {
                D3 a = verts[face[0]] - c, b2 = verts[face[k]] - c, d = verts[face[k + 1]] - c;
                vol6 += a.Dot(b2.Cross(d));
            }
        }

        double volume = vol6 / 6.0;
        D3 size = max - min;
        double diag = size.Length;
        bool closed = faces.Length >= 4 && Math.Abs(volume) > 1e-9 * Math.Max(1e-6, diag * diag * diag);
        if (volume < 0)
        {
            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = -normals[i];
            }
        }

        var offsets = new double[faces.Length];
        for (int fi = 0; fi < faces.Length; fi++)
        {
            offsets[fi] = faces[fi].Length == 0 ? 0 : Centroid(verts, faces[fi]).Dot(normals[fi]);
        }

        bool convex = closed;
        if (closed)
        {
            double tol = 1e-4 * Math.Max(1.0, diag);
            for (int fi = 0; fi < faces.Length && convex; fi++)
            {
                if (normals[fi].Dot(normals[fi]) < 0.5)
                {
                    continue;
                }

                foreach (D3 v in verts)
                {
                    if (normals[fi].Dot(v) - offsets[fi] > tol)
                    {
                        convex = false;
                        break;
                    }
                }
            }
        }

        return new WorldPolyhedron(verts, faces, normals, offsets, min, max, closed, convex, closed ? Math.Abs(volume) : 0);
    }

    /// <summary>The area-weighted (Newell) unit normal of a polygon in its stored winding.</summary>
    public static D3 Newell(D3[] verts, int[] face)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < face.Length; i++)
        {
            D3 a = verts[face[i]], b = verts[face[(i + 1) % face.Length]];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }

        return new D3(nx, ny, nz).Normalized();
    }

    public static D3 Centroid(D3[] verts, int[] face)
    {
        D3 s = D3.Zero;
        foreach (int i in face)
        {
            s += verts[i];
        }

        return face.Length == 0 ? s : s * (1.0 / face.Length);
    }

    /// <summary>The polygon area of face <paramref name="fi"/>.</summary>
    public double FaceArea(int fi)
    {
        int[] face = Faces[fi];
        if (face.Length < 3)
        {
            return 0;
        }

        D3 sum = D3.Zero;
        D3 o = Vertices[face[0]];
        for (int k = 1; k + 1 < face.Length; k++)
        {
            sum += (Vertices[face[k]] - o).Cross(Vertices[face[k + 1]] - o);
        }

        return sum.Length * 0.5;
    }

    /// <summary>True when <paramref name="p"/> lies inside the bounds grown by <paramref name="pad"/>.</summary>
    public bool BoundsContain(D3 p, double pad = 0) =>
        p.X >= Min.X - pad && p.X <= Max.X + pad && p.Y >= Min.Y - pad && p.Y <= Max.Y + pad && p.Z >= Min.Z - pad && p.Z <= Max.Z + pad;

    private TriangleGrid? _grid;
    private int _triangles = -1;

    /// <summary>The number of fan triangles over all faces.</summary>
    public int TriangleCount => _triangles >= 0 ? _triangles : (_triangles = Faces.Sum(f => Math.Max(0, f.Length - 2)));

    /// <summary>
    /// The fan triangles of a large non-convex brush binned on a 2D grid over its XZ bounds, for parity tests with an
    /// almost-vertical ray (+Y, skewed by about a milliradian): a query only tests the triangles under the ray's tiny
    /// XZ footprint. A degenerate hit answers null and the caller falls back to the full test.
    /// </summary>
    private sealed class TriangleGrid
    {
        private static readonly D3 Ray = new D3(0.000731, 1.0, 0.000377).Normalized();

        private readonly D3[] _a, _e1, _e2;
        private readonly int[][] _cells;
        private readonly double _minX, _minZ, _cell;
        private readonly int _nx, _nz;

        public TriangleGrid(WorldPolyhedron p)
        {
            var a = new List<D3>();
            var e1 = new List<D3>();
            var e2 = new List<D3>();
            foreach (int[] f in p.Faces)
            {
                for (int k = 1; k + 1 < f.Length; k++)
                {
                    D3 v0 = p.Vertices[f[0]];
                    a.Add(v0);
                    e1.Add(p.Vertices[f[k]] - v0);
                    e2.Add(p.Vertices[f[k + 1]] - v0);
                }
            }

            _a = a.ToArray();
            _e1 = e1.ToArray();
            _e2 = e2.ToArray();
            int n = Math.Clamp((int)Math.Sqrt(_a.Length / 2.0), 4, 64);
            _minX = p.Min.X;
            _minZ = p.Min.Z;
            _cell = Math.Max(1e-3, Math.Max(p.Max.X - p.Min.X, p.Max.Z - p.Min.Z) / n);
            _nx = Math.Max(1, (int)Math.Ceiling((p.Max.X - p.Min.X) / _cell) + 1);
            _nz = Math.Max(1, (int)Math.Ceiling((p.Max.Z - p.Min.Z) / _cell) + 1);
            var lists = new List<int>[_nx * _nz];
            for (int t = 0; t < _a.Length; t++)
            {
                D3 v0 = _a[t], v1 = v0 + _e1[t], v2 = v0 + _e2[t];
                (int x0, int z0) = CellOf(Math.Min(v0.X, Math.Min(v1.X, v2.X)), Math.Min(v0.Z, Math.Min(v1.Z, v2.Z)));
                (int x1, int z1) = CellOf(Math.Max(v0.X, Math.Max(v1.X, v2.X)), Math.Max(v0.Z, Math.Max(v1.Z, v2.Z)));
                for (int x = x0; x <= x1; x++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        (lists[(x * _nz) + z] ??= new List<int>()).Add(t);
                    }
                }
            }

            _cells = lists.Select(l => l?.ToArray() ?? Array.Empty<int>()).ToArray();
        }

        private (int, int) CellOf(double x, double z) => (
            Math.Clamp((int)((x - _minX) / _cell), 0, _nx - 1),
            Math.Clamp((int)((z - _minZ) / _cell), 0, _nz - 1));

        public int? CountHits(D3 p, double topY)
        {
            double tEnd = Math.Max(0, (topY - p.Y) / Ray.Y) + 1e-6;
            double ex = p.X + (Ray.X * tEnd), ez = p.Z + (Ray.Z * tEnd);
            (int x0, int z0) = CellOf(Math.Min(p.X, ex), Math.Min(p.Z, ez));
            (int x1, int z1) = CellOf(Math.Max(p.X, ex), Math.Max(p.Z, ez));
            HashSet<int>? seen = x0 == x1 && z0 == z1 ? null : new HashSet<int>();
            int hits = 0;
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    foreach (int t in _cells[(x * _nz) + z])
                    {
                        if (seen is not null && !seen.Add(t))
                        {
                            continue;
                        }

                        D3 e1 = _e1[t], e2 = _e2[t];
                        D3 pv = Ray.Cross(e2);
                        double det = e1.Dot(pv);
                        if (Math.Abs(det) < 1e-12 * Math.Max(1e-9, e1.Length * e2.Length))
                        {
                            continue;
                        }

                        double inv = 1.0 / det;
                        D3 tv = p - _a[t];
                        double u = tv.Dot(pv) * inv;
                        if (u < -1e-9 || u > 1 + 1e-9)
                        {
                            continue;
                        }

                        D3 qv = tv.Cross(e1);
                        double v = Ray.Dot(qv) * inv;
                        if (v < -1e-9 || u + v > 1 + 1e-9)
                        {
                            continue;
                        }

                        double tt = e2.Dot(qv) * inv;
                        if (tt < -1e-9)
                        {
                            continue;
                        }

                        if (u < 1e-7 || v < 1e-7 || u + v > 1 - 1e-7 || tt < 1e-9)
                        {
                            return null;
                        }

                        hits++;
                    }
                }
            }

            return hits;
        }
    }

    private static readonly D3[] RayDirections =
    {
        new D3(0.5773, 0.6131, 0.5395).Normalized(),
        new D3(-0.4129, 0.7311, -0.5431).Normalized(),
        new D3(0.6871, -0.3313, -0.6467).Normalized(),
        new D3(-0.2797, -0.8221, 0.4959).Normalized(),
    };

    /// <summary>
    /// True when <paramref name="p"/> is inside the closed polyhedron: a half-space test for a convex brush, ray
    /// parity against the fan-triangulated faces otherwise (a slightly skewed ray, retried on a degenerate hit).
    /// An open ("plane") brush contains nothing.
    /// </summary>
    public bool Contains(D3 p)
    {
        if (!Closed || !BoundsContain(p))
        {
            return false;
        }

        if (Convex)
        {
            for (int fi = 0; fi < Faces.Length; fi++)
            {
                if (Normals[fi].Dot(Normals[fi]) > 0.5 && Normals[fi].Dot(p) - Offsets[fi] > 1e-7)
                {
                    return false;
                }
            }

            return true;
        }

        // A large brush: an almost-vertical ray against the triangles binned by their XZ footprint.
        if (TriangleCount > 48)
        {
            TriangleGrid grid = LazyInitializer.EnsureInitialized(ref _grid, () => new TriangleGrid(this));
            if (grid.CountHits(p, Max.Y) is int gh)
            {
                return (gh & 1) == 1;
            }
        }

        int votes = 0, cast = 0;
        foreach (D3 dir in RayDirections)
        {
            int? hits = CountHits(p, dir);
            if (hits is int h)
            {
                return (h & 1) == 1;
            }

            cast++;
        }

        // Every direction grazed an edge: fall back to the majority of the raw counts.
        foreach (D3 dir in RayDirections)
        {
            if ((CountHits(p, dir, strict: false) ?? 0) % 2 == 1)
            {
                votes++;
            }
        }

        return votes * 2 > cast;
    }

    private int? CountHits(D3 p, D3 dir, bool strict = true)
    {
        int hits = 0;
        foreach (int[] face in Faces)
        {
            for (int k = 1; k + 1 < face.Length; k++)
            {
                D3 v0 = Vertices[face[0]], v1 = Vertices[face[k]], v2 = Vertices[face[k + 1]];
                D3 e1 = v1 - v0, e2 = v2 - v0;
                D3 pv = dir.Cross(e2);
                double det = e1.Dot(pv);
                double scale = e1.Length * e2.Length;
                if (Math.Abs(det) < 1e-12 * Math.Max(1e-9, scale))
                {
                    continue; // parallel to this triangle: no crossing
                }

                double inv = 1.0 / det;
                D3 tv = p - v0;
                double u = tv.Dot(pv) * inv;
                if (u < -1e-9 || u > 1 + 1e-9)
                {
                    continue;
                }

                D3 qv = tv.Cross(e1);
                double v = dir.Dot(qv) * inv;
                if (v < -1e-9 || u + v > 1 + 1e-9)
                {
                    continue;
                }

                double t = e2.Dot(qv) * inv;
                if (t < -1e-9)
                {
                    continue;
                }

                if (strict && (u < 1e-7 || v < 1e-7 || u + v > 1 - 1e-7 || t < 1e-9))
                {
                    // Grazing an edge (including a fan diagonal, which two triangles share) or starting on the
                    // surface: the parity is ambiguous, so the caller retries with another direction.
                    return null;
                }

                hits++;
            }
        }

        return hits;
    }
}
