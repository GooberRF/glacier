using System;
using System.Collections.Generic;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Linting;

/// <summary>
/// The solid volumes of a level's closed detail brushes, for point-inside tests. Detail brushes do not carve the world,
/// so a structural face a detail brush covers (the flat wall behind a canyon's rock skin) survives into the compiled
/// geometry although no player can see it; the craft audit asks this whether a point in front of a face lies inside
/// such a brush. Each brush is tested on its own (ray parity on its world triangles), so overlapping rocks count
/// correctly; brushes that are not closed (single-sided sheets, grates) are left out. Read-only after construction.
/// </summary>
internal sealed class DetailVolumes
{
    private const double Cell = 4.0;

    /// <summary>The parity ray: skewed off every axis so it does not run along brush edges.</summary>
    private static readonly Vec3d Ray = new Vec3d(0.3127, 0.8913, 0.3283).Normalized();

    private readonly List<Volume> _volumes = new();
    private readonly Dictionary<(int, int, int), List<int>> _grid = new();

    private sealed class Volume
    {
        public required Vec3d Min { get; init; }

        public required Vec3d Max { get; init; }

        /// <summary>World triangles, 9 doubles each.</summary>
        public required double[] Tris { get; init; }
    }

    public DetailVolumes(IEnumerable<Brush> brushes)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        foreach (Brush b in brushes)
        {
            if ((b.Flags & (uint)BrushFlags.Detail) == 0 || (b.Flags & (uint)(BrushFlags.Air | BrushFlags.Portal)) != 0
                || b.Geometry.Vertices.Count < 4 || b.Geometry.Faces.Count < 4 || !Closed(b.Geometry))
            {
                continue;
            }

            if (Build(b) is { } v)
            {
                int index = _volumes.Count;
                _volumes.Add(v);
                (int x0, int y0, int z0) = Key(v.Min);
                (int x1, int y1, int z1) = Key(v.Max);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 4096)
                {
                    continue; // a huge detail brush: not a skin piece (and too costly to grid)
                }

                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int z = z0; z <= z1; z++)
                        {
                            if (!_grid.TryGetValue((x, y, z), out List<int>? l))
                            {
                                _grid[(x, y, z)] = l = new List<int>(4);
                            }

                            l.Add(index);
                        }
                    }
                }
            }
        }
    }

    /// <summary>The number of closed detail brushes indexed.</summary>
    public int Count => _volumes.Count;

    /// <summary>True when <paramref name="p"/> lies inside a closed detail brush.</summary>
    public bool Contains(Vec3d p)
    {
        if (!_grid.TryGetValue(Key(p), out List<int>? list))
        {
            return false;
        }

        foreach (int i in list)
        {
            Volume v = _volumes[i];
            if (p.X < v.Min.X || p.X > v.Max.X || p.Y < v.Min.Y || p.Y > v.Max.Y || p.Z < v.Min.Z || p.Z > v.Max.Z)
            {
                continue;
            }

            if (Inside(v.Tris, p))
            {
                return true;
            }
        }

        return false;
    }

    private static (int, int, int) Key(Vec3d p) => ((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell), (int)Math.Floor(p.Z / Cell));

    /// <summary>True when every edge of the brush is shared by exactly two faces (a watertight solid).</summary>
    private static bool Closed(Geometry g)
    {
        var edges = new Dictionary<(int, int), int>();
        foreach (Face f in g.Faces)
        {
            int n = f.Vertices.Count;
            if (n < 3)
            {
                return false;
            }

            for (int k = 0; k < n; k++)
            {
                int a = f.Vertices[k].Index, b = f.Vertices[(k + 1) % n].Index;
                if (a == b)
                {
                    continue;
                }

                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }

        foreach (int c in edges.Values)
        {
            if (c != 2)
            {
                return false;
            }
        }

        return edges.Count > 0;
    }

    private static Volume? Build(Brush b)
    {
        Geometry g = b.Geometry;
        Mat3 r = b.Rotation;
        var pos = new Vec3d(b.Position.X, b.Position.Y, b.Position.Z);
        var right = new Vec3d(r.Right.X, r.Right.Y, r.Right.Z);
        var up = new Vec3d(r.Up.X, r.Up.Y, r.Up.Z);
        var fwd = new Vec3d(r.Forward.X, r.Forward.Y, r.Forward.Z);
        var w = new Vec3d[g.Vertices.Count];
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        for (int i = 0; i < w.Length; i++)
        {
            Vec3 v = g.Vertices[i];
            Vec3d p = pos.Add(right.Scale(v.X)).Add(up.Scale(v.Y)).Add(fwd.Scale(v.Z));
            w[i] = p;
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            z0 = Math.Min(z0, p.Z);
            x1 = Math.Max(x1, p.X);
            y1 = Math.Max(y1, p.Y);
            z1 = Math.Max(z1, p.Z);
        }

        var tris = new List<double>();
        foreach (Face f in g.Faces)
        {
            for (int k = 1; k + 1 < f.Vertices.Count; k++)
            {
                int a = f.Vertices[0].Index, b1 = f.Vertices[k].Index, c = f.Vertices[k + 1].Index;
                if (a < 0 || b1 < 0 || c < 0 || a >= w.Length || b1 >= w.Length || c >= w.Length)
                {
                    return null;
                }

                foreach (Vec3d p in new[] { w[a], w[b1], w[c] })
                {
                    tris.Add(p.X);
                    tris.Add(p.Y);
                    tris.Add(p.Z);
                }
            }
        }

        return tris.Count == 0 || !(x1 > x0) || !(y1 > y0) || !(z1 > z0)
            ? null
            : new Volume { Min = new Vec3d(x0, y0, z0), Max = new Vec3d(x1, y1, z1), Tris = tris.ToArray() };
    }

    /// <summary>Ray parity against the triangles (Möller–Trumbore): an odd number of crossings is inside.</summary>
    private static bool Inside(double[] t, Vec3d o)
    {
        int crossings = 0;
        for (int i = 0; i + 8 < t.Length; i += 9)
        {
            double e1x = t[i + 3] - t[i], e1y = t[i + 4] - t[i + 1], e1z = t[i + 5] - t[i + 2];
            double e2x = t[i + 6] - t[i], e2y = t[i + 7] - t[i + 1], e2z = t[i + 8] - t[i + 2];
            double px = (Ray.Y * e2z) - (Ray.Z * e2y), py = (Ray.Z * e2x) - (Ray.X * e2z), pz = (Ray.X * e2y) - (Ray.Y * e2x);
            double det = (e1x * px) + (e1y * py) + (e1z * pz);
            if (Math.Abs(det) < 1e-12)
            {
                continue;
            }

            double inv = 1 / det;
            double sx = o.X - t[i], sy = o.Y - t[i + 1], sz = o.Z - t[i + 2];
            double u = ((sx * px) + (sy * py) + (sz * pz)) * inv;
            if (u < 0 || u > 1)
            {
                continue;
            }

            double qx = (sy * e1z) - (sz * e1y), qy = (sz * e1x) - (sx * e1z), qz = (sx * e1y) - (sy * e1x);
            double v = ((Ray.X * qx) + (Ray.Y * qy) + (Ray.Z * qz)) * inv;
            if (v < 0 || u + v > 1)
            {
                continue;
            }

            if (((e2x * qx) + (e2y * qy) + (e2z * qz)) * inv > 1e-9)
            {
                crossings++;
            }
        }

        return (crossings & 1) == 1;
    }
}
