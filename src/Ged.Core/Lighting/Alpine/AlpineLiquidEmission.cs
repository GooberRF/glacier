using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>
/// A liquid room's surface as the bake sees it: the room's horizontal liquid faces at one height (the upward ones; a
/// two-sided surface's downward copies only when there are no upward ones), with their outline — reimplemented from
/// editor_patch/liquid_emission.cpp:488-754.
/// </summary>
public sealed class AlpineLiquidSurface
{
    private readonly List<(float X, float Z)[]> _polygons = new();
    private readonly List<((float X, float Z) A, (float X, float Z) B)> _outline = new();
    private float _windingSign = 1f;
    private int _grid = 1;
    private float _cellX = 1f, _cellZ = 1f;
    private List<int>[] _cells = Array.Empty<List<int>>();

    public float Height { get; private set; }

    public float MinX { get; private set; }

    public float MaxX { get; private set; }

    public float MinZ { get; private set; }

    public float MaxZ { get; private set; }

    public int PolygonCount => _polygons.Count;

    /// <summary>The surface of a room from its liquid faces (world polygons with their plane normals); null when the room has none.</summary>
    public static AlpineLiquidSurface? Build(IEnumerable<(IReadOnlyList<Vec3> Polygon, Vec3 Normal)> liquidFaces)
    {
        var sides = new[] { new List<(float X, float Z)[]>(), new List<(float X, float Z)[]>() };
        var heightSum = new double[2];
        var heightCount = new int[2];
        foreach ((IReadOnlyList<Vec3> poly, Vec3 n) in liquidFaces)
        {
            if (MathF.Abs(n.Y) < 0.999f || poly.Count < 3)
            {
                continue;
            }

            int side = n.Y > 0f ? 0 : 1;
            var pts = new (float, float)[poly.Count];
            double ySum = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                pts[i] = (poly[i].X, poly[i].Z);
                ySum += poly[i].Y;
            }

            heightSum[side] += ySum;
            heightCount[side] += poly.Count;
            sides[side].Add(pts);
        }

        int use = sides[0].Count == 0 ? 1 : 0;
        if (sides[use].Count == 0)
        {
            return null;
        }

        var s = new AlpineLiquidSurface { Height = (float)(heightSum[use] / heightCount[use]) };
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach ((float X, float Z)[] poly in sides[use])
        {
            double area = 0;
            for (int i = 0; i < poly.Length; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Length];
                area += ((double)a.X * b.Z) - ((double)b.X * a.Z);
            }

            if (Math.Abs(area) < 1e-8)
            {
                continue;
            }

            if (area < 0)
            {
                Array.Reverse(poly);
            }

            foreach (var p in poly)
            {
                minX = MathF.Min(minX, p.X);
                maxX = MathF.Max(maxX, p.X);
                minZ = MathF.Min(minZ, p.Z);
                maxZ = MathF.Max(maxZ, p.Z);
            }

            s._polygons.Add(poly);
        }

        if (s._polygons.Count == 0)
        {
            return null;
        }

        (s.MinX, s.MaxX, s.MinZ, s.MaxZ) = (minX, maxX, minZ, maxZ);

        // Neighbouring polygons walk a shared edge in opposite directions, so it cancels (liquid_emission.cpp:586-606).
        var net = new SortedDictionary<(float, float, float, float), int>();
        foreach (var poly in s._polygons)
        {
            for (int i = 0; i < poly.Length; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Length];
                bool forward = a.X < b.X || (a.X == b.X && a.Z < b.Z);
                var key = forward ? (a.X, a.Z, b.X, b.Z) : (b.X, b.Z, a.X, a.Z);
                net[key] = net.GetValueOrDefault(key) + (forward ? 1 : -1);
            }
        }

        foreach (var (key, count) in net)
        {
            var lo = (key.Item1, key.Item2);
            var hi = (key.Item3, key.Item4);
            for (int k = 0; k < Math.Abs(count); k++)
            {
                s._outline.Add(count > 0 ? (lo, hi) : (hi, lo));
            }
        }

        // Seen from straight above a polygon the circulation points along the receiver's normal or against it.
        var first = s._polygons[0];
        float cx = 0, cz = 0;
        foreach (var p in first)
        {
            cx += p.X / first.Length;
            cz += p.Z / first.Length;
        }

        var centre = new Vec3(cx, s.Height + 1f, cz);
        Span<double> sum = stackalloc double[3];
        sum.Clear();
        for (int i = 0; i < first.Length; i++)
        {
            var a = first[i];
            var b = first[(i + 1) % first.Length];
            AddCirculation(sum, centre, new Vec3(a.X, s.Height, a.Z), new Vec3(b.X, s.Height, b.Z));
        }

        s._windingSign = sum[1] < 0.0 ? 1f : -1f;
        s.BuildGrid();
        return s;
    }

    private void BuildGrid()
    {
        _grid = Math.Clamp((int)(MathF.Sqrt(_polygons.Count) * 2f), 1, 64);
        _cellX = MathF.Max((MaxX - MinX) / _grid, 1e-6f);
        _cellZ = MathF.Max((MaxZ - MinZ) / _grid, 1e-6f);
        _cells = new List<int>[_grid * _grid];
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = new List<int>();
        }

        int Cell(float v, float lo, float size) => Math.Clamp((int)((v - lo) / size), 0, _grid - 1);
        for (int i = 0; i < _polygons.Count; i++)
        {
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            foreach (var p in _polygons[i])
            {
                x0 = MathF.Min(x0, p.X);
                x1 = MathF.Max(x1, p.X);
                z0 = MathF.Min(z0, p.Z);
                z1 = MathF.Max(z1, p.Z);
            }

            for (int cz = Cell(z0, MinZ, _cellZ); cz <= Cell(z1, MinZ, _cellZ); cz++)
            {
                for (int cx = Cell(x0, MinX, _cellX); cx <= Cell(x1, MinX, _cellX); cx++)
                {
                    _cells[(cz * _grid) + cx].Add(i);
                }
            }
        }
    }

    private static void AddCirculation(Span<double> sum, Vec3 p, Vec3 a, Vec3 b)
    {
        Vec3 va = a.Sub(p).Normalized(), vb = b.Sub(p).Normalized();
        Vec3 c = va.Cross(vb);
        double s = c.Length();
        if (!(s > 1e-9))
        {
            return;
        }

        double theta = Math.Atan2(s, va.Dot(vb));
        sum[0] += c.X * theta / s;
        sum[1] += c.Y * theta / s;
        sum[2] += c.Z * theta / s;
    }

    public bool Contains(float x, float z)
    {
        if (!(x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ) || _cells.Length == 0)
        {
            return false;
        }

        int cx = Math.Clamp((int)((x - MinX) / _cellX), 0, _grid - 1);
        int cz = Math.Clamp((int)((z - MinZ) / _cellZ), 0, _grid - 1);
        foreach (int index in _cells[(cz * _grid) + cx])
        {
            var poly = _polygons[index];
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                if ((a.Z > z) != (b.Z > z) && x < ((b.X - a.X) * (z - a.Z) / (b.Z - a.Z)) + a.X)
                {
                    inside = !inside;
                }
            }

            if (inside)
            {
                return true;
            }
        }

        return false;
    }

    private float NearestEdgeDistance(float x, float z, out float qx, out float qz)
    {
        float best = float.MaxValue;
        qx = x;
        qz = z;
        foreach (var (a, b) in _outline)
        {
            float dx = b.X - a.X, dz = b.Z - a.Z;
            float lenSq = (dx * dx) + (dz * dz);
            float t = lenSq > 0f ? Math.Clamp((((x - a.X) * dx) + ((z - a.Z) * dz)) / lenSq, 0f, 1f) : 0f;
            float px = a.X + (dx * t), pz = a.Z + (dz * t);
            float d = ((x - px) * (x - px)) + ((z - pz) * (z - pz));
            if (d < best)
            {
                best = d;
                qx = px;
                qz = pz;
            }
        }

        return MathF.Sqrt(best);
    }

    public Vec3 NearestPoint(Vec3 p)
    {
        if (Contains(p.X, p.Z))
        {
            return new Vec3(p.X, Height, p.Z);
        }

        NearestEdgeDistance(p.X, p.Z, out float qx, out float qz);
        return new Vec3(qx, Height, qz);
    }

    public float Distance(Vec3 p)
    {
        float dy = p.Y - Height;
        if (Contains(p.X, p.Z))
        {
            return MathF.Abs(dy);
        }

        float dxz = NearestEdgeDistance(p.X, p.Z, out _, out _);
        return MathF.Sqrt((dy * dy) + (dxz * dxz));
    }

    /// <summary>The cosine-weighted share of a receiver's hemisphere the unoccluded surface covers (liquid_emission.cpp:713-754).</summary>
    public float FormFactor(Vec3 p, Vec3 n)
    {
        float dy = p.Y - Height;
        if (MathF.Abs(dy) < 1e-4f || _polygons.Count == 0)
        {
            return 0f;
        }

        Span<double> sum = stackalloc double[3];
        sum.Clear();
        float InFront(Vec3 q) => q.Sub(p).Dot(n);
        bool whole = InFront(new Vec3(MinX, Height, MinZ)) >= 0f && InFront(new Vec3(MaxX, Height, MinZ)) >= 0f
            && InFront(new Vec3(MinX, Height, MaxZ)) >= 0f && InFront(new Vec3(MaxX, Height, MaxZ)) >= 0f;
        if (whole)
        {
            foreach (var (a, b) in _outline)
            {
                AddCirculation(sum, p, new Vec3(a.X, Height, a.Z), new Vec3(b.X, Height, b.Z));
            }
        }
        else
        {
            var clipped = new List<Vec3>(8);
            foreach (var poly in _polygons)
            {
                clipped.Clear();
                for (int i = 0; i < poly.Length; i++)
                {
                    var a = new Vec3(poly[i].X, Height, poly[i].Z);
                    var b = new Vec3(poly[(i + 1) % poly.Length].X, Height, poly[(i + 1) % poly.Length].Z);
                    float fa = InFront(a), fb = InFront(b);
                    if (fa >= 0f)
                    {
                        clipped.Add(a);
                    }

                    if ((fa >= 0f) != (fb >= 0f))
                    {
                        clipped.Add(a.Add(b.Sub(a).Scale(fa / (fa - fb))));
                    }
                }

                for (int i = 0; clipped.Count >= 3 && i < clipped.Count; i++)
                {
                    AddCirculation(sum, p, clipped[i], clipped[(i + 1) % clipped.Count]);
                }
            }
        }

        double side = dy > 0f ? 1.0 : -1.0;
        double ff = side * _windingSign * ((sum[0] * n.X) + (sum[1] * n.Y) + (sum[2] * n.Z)) / (2.0 * Math.PI);
        return (float)Math.Clamp(ff, 0.0, 1.0);
    }
}

/// <summary>
/// One liquid room's light in a bake (editor_patch/lightmap.cpp:1583-1708, 2152-2225, 2861-2960): its surface, the
/// premultiplied colour, how far it reaches and whether it also lights what is below. Its glow at a receiver is
/// visibility × linear falloff × the surface's form factor, visibility being the share of 32 cosine-weighted
/// Hammersley rays (turned per receiver by a hash of its position) that reach the surface unblocked.
/// </summary>
public sealed class AlpineLiquidEmitter
{
    public const int VisibilityRays = 32;

    private static readonly Vec3[] Pattern = BuildPattern();

    public required AlpineLiquidSurface Surface { get; init; }

    public required Vec3 Color { get; init; }

    public required float Reach { get; init; }

    public required bool Below { get; init; }

    /// <summary>The room effect it belongs to.</summary>
    public int EffectUid { get; init; }

    /// <summary>The stand-in light's sphere: every point within the reach of the surface (lightmap.cpp:2205-2209).</summary>
    public Aabb Bounds
    {
        get
        {
            float hx = (Surface.MaxX - Surface.MinX) * 0.5f, hz = (Surface.MaxZ - Surface.MinZ) * 0.5f;
            var c = new Vec3(Surface.MinX + hx, Surface.Height, Surface.MinZ + hz);
            float r = MathF.Sqrt((hx * hx) + (hz * hz)) + Reach;
            return new Aabb(c.Sub(new Vec3(r, r, r)), c.Add(new Vec3(r, r, r)));
        }
    }

    /// <summary>1 on the surface, falling linearly to 0 at the reach; 0 on a side it does not light (lightmap.cpp:1602-1614).</summary>
    public float Falloff(Vec3 p)
    {
        AlpineLiquidSurface s = Surface;
        float dy = p.Y - s.Height;
        if ((dy <= 0f && !Below) || MathF.Abs(dy) >= Reach || p.X <= s.MinX - Reach || p.X >= s.MaxX + Reach
            || p.Z <= s.MinZ - Reach || p.Z >= s.MaxZ + Reach)
        {
            return 0f;
        }

        float d = s.Distance(p);
        return d < Reach ? 1f - (d / Reach) : 0f;
    }

    private static Vec3[] BuildPattern()
    {
        var dirs = new Vec3[VisibilityRays];
        for (int i = 0; i < VisibilityRays; i++)
        {
            uint bits = (uint)i;
            bits = (bits << 16) | (bits >> 16);
            bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
            bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
            bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
            bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
            float u = (i + 0.5f) / VisibilityRays;
            float phi = bits * 2.3283064e-10f * 2f * MathF.PI;
            float r = MathF.Sqrt(u);
            dirs[i] = new Vec3(r * MathF.Cos(phi), MathF.Sqrt(1f - u), r * MathF.Sin(phi));
        }

        return dirs;
    }

    private static float Turn(Vec3 p)
    {
        uint h = (BitConverter.SingleToUInt32Bits(p.X) * 0x9E3779B1u)
            ^ (BitConverter.SingleToUInt32Bits(p.Y) * 0x85EBCA77u)
            ^ (BitConverter.SingleToUInt32Bits(p.Z) * 0xC2B2AE3Du);
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return (h >> 8) * (2f * MathF.PI / 16777216f);
    }

    private static Vec3 AnyPerpendicular(Vec3 n)
    {
        Vec3 a = MathF.Abs(n.X) < 0.9f ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        return a.Sub(n.Scale(a.Dot(n))).Normalized();
    }

    /// <summary>
    /// The visibility byte at a receiver whose shadow-ray origin is <paramref name="origin"/> (already lifted) with
    /// normal <paramref name="ns"/> (lightmap.cpp:1654-1708); null occluders = nothing blocks.
    /// </summary>
    public byte Visibility(AlpineOccluders? tree, Vec3 origin, Vec3 ns, int skipSurf, float footprint, uint skip, uint oneSide)
    {
        if (Falloff(origin) <= 0f)
        {
            return 0;
        }

        Vec3 n = ns;
        Vec3 t = AnyPerpendicular(n);
        Vec3 b = n.Cross(t);
        float turn = Turn(origin);
        float cs = MathF.Cos(turn), sn = MathF.Sin(turn);
        float dy = origin.Y - Surface.Height;
        var q = new AlpineOccluders.Query
        {
            Origin = origin, SurfNormal = ns, TMin = AlpineOccluders.RayEps, SkipSurf = skipSurf, SkipFlags = skip, OneSideFlags = oneSide, Footprint = footprint,
        };
        float lit = 0f;
        int taken = 0;
        void Trace(Vec3 dir, float dist)
        {
            q.Dir = dir;
            q.Nd = ns.Dot(dir);
            q.TMax = dist - AlpineOccluders.RayEps;
            q.Converge = 1f / dist;
            taken++;
            lit += q.TMax > q.TMin && tree is not null ? tree.Transmittance(q) : 1f;
        }

        foreach (Vec3 p in Pattern)
        {
            float x = (p.X * cs) - (p.Z * sn);
            float z = (p.X * sn) + (p.Z * cs);
            Vec3 dir = t.Scale(x).Add(n.Scale(p.Y)).Add(b.Scale(z));
            if (!(dir.Y * dy < 0f))
            {
                continue;
            }

            float dist = -dy / dir.Y;
            Vec3 hit = origin.Add(dir.Scale(dist));
            if (Surface.Contains(hit.X, hit.Z))
            {
                Trace(dir, dist);
            }
        }

        if (taken == 0)
        {
            Vec3 to = Surface.NearestPoint(origin).Sub(origin);
            float dist = to.Length();
            if (!(dist > 1e-4f))
            {
                return 255;
            }

            Trace(to.Scale(1f / dist), dist);
        }

        return AlpineOccluders.MaskByte(lit, taken);
    }
}
