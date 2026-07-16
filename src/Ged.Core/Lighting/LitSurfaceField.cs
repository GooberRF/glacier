using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// The queryable direct-lit scene for the bounce gather (feature 1 / Bounced): each
/// surface's texel grid is approximated by its planar quad (two triangles, from the
/// texel mapper) tagged with the surface index, in a closest-hit BVH. A gather ray
/// resolves to the nearest surface and its direct-lit texel colour, so an indirect
/// bounce fetches incoming radiance the way RED's gather does. Thread-safe queries.
/// </summary>
public sealed class LitSurfaceField
{
    private const int LeafSize = 4;

    private readonly float[] _tri;      // 9 floats per triangle
    private readonly int[] _surfaceOf;  // surface index per triangle
    private readonly Node[] _nodes;
    private readonly int _rootCount;
    private readonly SurfaceTexelMapper[] _mappers;
    private readonly float[]?[] _buffers;
    private readonly int[] _w;
    private readonly int[] _h;

    private LitSurfaceField(
        float[] tri, int[] surfaceOf, Node[] nodes, int rootCount,
        SurfaceTexelMapper[] mappers, float[]?[] buffers, int[] w, int[] h)
    {
        _tri = tri;
        _surfaceOf = surfaceOf;
        _nodes = nodes;
        _rootCount = rootCount;
        _mappers = mappers;
        _buffers = buffers;
        _w = w;
        _h = h;
    }

    public bool IsEmpty => _rootCount == 0;

    private readonly struct Node
    {
        public readonly Vec3 Min;
        public readonly Vec3 Max;
        public readonly int Left;
        public readonly int Start;
        public readonly int Count;

        public Node(Vec3 min, Vec3 max, int left, int start, int count)
        {
            Min = min; Max = max; Left = left; Start = start; Count = count;
        }
    }

    /// <summary>
    /// Builds the field from each surface's texel mapper, direct-lit float buffer and
    /// grid size. Surfaces with a null buffer (full-bright) are skipped.
    /// </summary>
    public static LitSurfaceField Build(
        IReadOnlyList<SurfaceTexelMapper> mappers,
        IReadOnlyList<float[]?> buffers,
        IReadOnlyList<int> widths,
        IReadOnlyList<int> heights)
    {
        var triList = new List<float>();
        var surfaceList = new List<int>();
        var boxMin = new List<Vec3>();
        var boxMax = new List<Vec3>();
        var centroid = new List<Vec3>();

        void AddTri(Vec3 a, Vec3 b, Vec3 c, int si)
        {
            triList.Add(a.X); triList.Add(a.Y); triList.Add(a.Z);
            triList.Add(b.X); triList.Add(b.Y); triList.Add(b.Z);
            triList.Add(c.X); triList.Add(c.Y); triList.Add(c.Z);
            surfaceList.Add(si);
            boxMin.Add(Vec3Math.Min(Vec3Math.Min(a, b), c));
            boxMax.Add(Vec3Math.Max(Vec3Math.Max(a, b), c));
            centroid.Add(a.Add(b).Add(c).Scale(1f / 3f));
        }

        int n = mappers.Count;
        for (int si = 0; si < n; si++)
        {
            if (buffers[si] is null || widths[si] < 1 || heights[si] < 1)
            {
                continue;
            }

            SurfaceTexelMapper m = mappers[si];
            int w = widths[si], h = heights[si];
            Vec3 c00 = m.World(0, 0), c10 = m.World(w - 1, 0), c11 = m.World(w - 1, h - 1), c01 = m.World(0, h - 1);
            AddTri(c00, c10, c11, si);
            AddTri(c00, c11, c01, si);
        }

        int triCount = surfaceList.Count;
        var index = new int[triCount];
        for (int i = 0; i < triCount; i++)
        {
            index[i] = i;
        }

        var nodes = new List<Node>(Math.Max(1, triCount / 2));
        if (triCount > 0)
        {
            BuildRange(nodes, index, centroid, boxMin, boxMax, 0, triCount);
        }

        // Reorder triangle payload + surface tags to the leaf-sorted index.
        var tri = new float[triCount * 9];
        var surfaceOf = new int[triCount];
        float[] src = triList.ToArray();
        for (int i = 0; i < triCount; i++)
        {
            Array.Copy(src, index[i] * 9, tri, i * 9, 9);
            surfaceOf[i] = surfaceList[index[i]];
        }

        return new LitSurfaceField(
            tri, surfaceOf, nodes.ToArray(), triCount,
            new List<SurfaceTexelMapper>(mappers).ToArray(),
            new List<float[]?>(buffers).ToArray(),
            new List<int>(widths).ToArray(),
            new List<int>(heights).ToArray());
    }

    private static int BuildRange(
        List<Node> nodes, int[] index, List<Vec3> centroid, List<Vec3> boxMin, List<Vec3> boxMax, int start, int end)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = start; i < end; i++)
        {
            mn = Vec3Math.Min(mn, boxMin[index[i]]);
            mx = Vec3Math.Max(mx, boxMax[index[i]]);
        }

        int count = end - start;
        int nodeIndex = nodes.Count;
        nodes.Add(default);
        if (count <= LeafSize)
        {
            nodes[nodeIndex] = new Node(mn, mx, -1, start, count);
            return nodeIndex;
        }

        Vec3 ext = mx.Sub(mn);
        int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : (ext.Y >= ext.Z ? 1 : 2);
        int mid = (start + end) / 2;
        Array.Sort(index, start, count, Comparer<int>.Create(
            (a, b) => centroid[a].Component(axis).CompareTo(centroid[b].Component(axis))));
        int left = BuildRange(nodes, index, centroid, boxMin, boxMax, start, mid);
        BuildRange(nodes, index, centroid, boxMin, boxMax, mid, end);
        nodes[nodeIndex] = new Node(mn, mx, left, 0, 0);
        return nodeIndex;
    }

    /// <summary>
    /// The direct-lit float RGB at the nearest surface hit along the ray (from
    /// <paramref name="origin"/> in unit <paramref name="dir"/>), or null when the ray
    /// escapes. This is the incoming radiance sample for a bounce.
    /// </summary>
    public Vec3? SampleColor(Vec3 origin, Vec3 dir, float maxDist)
    {
        if (_rootCount == 0)
        {
            return null;
        }

        Vec3 inv = new(SafeInv(dir.X), SafeInv(dir.Y), SafeInv(dir.Z));
        const float eps = 0.02f;
        float bestT = maxDist;
        int bestTri = -1;

        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            Node node = _nodes[stack[--sp]];
            if (!SlabHit(node.Min, node.Max, origin, inv, eps, bestT))
            {
                continue;
            }

            if (node.Left < 0)
            {
                for (int i = 0; i < node.Count; i++)
                {
                    int t = (node.Start + i) * 9;
                    if (TriHit(t, origin, dir, eps, bestT, out float hitT) && hitT < bestT)
                    {
                        bestT = hitT;
                        bestTri = node.Start + i;
                    }
                }
            }
            else if (sp + 2 <= stack.Length)
            {
                stack[sp++] = node.Left;
                stack[sp++] = node.Left + 1;
            }
        }

        if (bestTri < 0)
        {
            return null;
        }

        int si = _surfaceOf[bestTri];
        if (_buffers[si] is not { } buf)
        {
            return null;
        }

        Vec3 hit = origin.Add(dir.Scale(bestT));
        _mappers[si].TexelAt(hit, _w[si], _h[si], out int col, out int row);
        int o = ((row * _w[si]) + col) * 3;
        return o + 2 < buf.Length ? new Vec3(buf[o], buf[o + 1], buf[o + 2]) : null;
    }

    private static float SafeInv(float v) => MathF.Abs(v) < 1e-12f ? (v < 0 ? -1e12f : 1e12f) : 1f / v;

    private static bool SlabHit(Vec3 mn, Vec3 mx, Vec3 o, Vec3 inv, float tMin, float tMax)
    {
        float t1 = (mn.X - o.X) * inv.X, t2 = (mx.X - o.X) * inv.X;
        float lo = MathF.Min(t1, t2), hi = MathF.Max(t1, t2);
        t1 = (mn.Y - o.Y) * inv.Y; t2 = (mx.Y - o.Y) * inv.Y;
        lo = MathF.Max(lo, MathF.Min(t1, t2)); hi = MathF.Min(hi, MathF.Max(t1, t2));
        t1 = (mn.Z - o.Z) * inv.Z; t2 = (mx.Z - o.Z) * inv.Z;
        lo = MathF.Max(lo, MathF.Min(t1, t2)); hi = MathF.Min(hi, MathF.Max(t1, t2));
        return hi >= MathF.Max(lo, tMin) && lo <= tMax;
    }

    private bool TriHit(int o, Vec3 orig, Vec3 dir, float tMin, float tMax, out float tHit)
    {
        tHit = 0f;
        float ax = _tri[o], ay = _tri[o + 1], az = _tri[o + 2];
        float e1x = _tri[o + 3] - ax, e1y = _tri[o + 4] - ay, e1z = _tri[o + 5] - az;
        float e2x = _tri[o + 6] - ax, e2y = _tri[o + 7] - ay, e2z = _tri[o + 8] - az;
        float px = (dir.Y * e2z) - (dir.Z * e2y);
        float py = (dir.Z * e2x) - (dir.X * e2z);
        float pz = (dir.X * e2y) - (dir.Y * e2x);
        float det = (e1x * px) + (e1y * py) + (e1z * pz);
        if (det > -1e-8f && det < 1e-8f)
        {
            return false;
        }

        float invDet = 1f / det;
        float tx = orig.X - ax, ty = orig.Y - ay, tz = orig.Z - az;
        float u = ((tx * px) + (ty * py) + (tz * pz)) * invDet;
        if (u < -1e-4f || u > 1.0001f)
        {
            return false;
        }

        float qx = (ty * e1z) - (tz * e1y);
        float qy = (tz * e1x) - (tx * e1z);
        float qz = (tx * e1y) - (ty * e1x);
        float v = ((dir.X * qx) + (dir.Y * qy) + (dir.Z * qz)) * invDet;
        if (v < -1e-4f || u + v > 1.0001f)
        {
            return false;
        }

        float t = ((e2x * qx) + (e2y * qy) + (e2z * qz)) * invDet;
        if (t <= tMin || t >= tMax)
        {
            return false;
        }

        tHit = t;
        return true;
    }
}
