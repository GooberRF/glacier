using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// A read-only bounding-volume hierarchy over occluder triangles, supporting
/// thread-safe any-hit (shadow) queries. Built once per bake from the level's
/// solid world + detail faces (portals / invisible / sky / liquid excluded, per
/// docs/research/red-lighting-model.md §(c)); queried per texel per shadow light.
/// Median-split over the centroid of the largest axis; leaves hold a few tris.
/// </summary>
public sealed class OccluderBvh
{
    private const int LeafSize = 4;

    private readonly float[] _tri; // 9 floats per triangle: a.xyz b.xyz c.xyz
    private readonly Node[] _nodes;
    private readonly int _rootCount;

    private OccluderBvh(float[] tri, Node[] nodes, int rootCount)
    {
        _tri = tri;
        _nodes = nodes;
        _rootCount = rootCount;
    }

    /// <summary>True when the tree has no occluders (every query is unshadowed).</summary>
    public bool IsEmpty => _rootCount == 0;

    /// <summary>Number of occluder triangles.</summary>
    public int TriangleCount => _tri.Length / 9;

    private readonly struct Node
    {
        public readonly Vec3 Min;
        public readonly Vec3 Max;
        public readonly int Left;   // child node index, or -1 for a leaf
        public readonly int Right;  // right child node index (only valid when Left >= 0)
        public readonly int Start;  // leaf: first triangle index
        public readonly int Count;  // leaf: triangle count

        public Node(Vec3 min, Vec3 max, int left, int right, int start, int count)
        {
            Min = min;
            Max = max;
            Left = left;
            Right = right;
            Start = start;
            Count = count;
        }
    }

    /// <summary>Builds a BVH from flat triangles (each entry = 3 world-space corners).</summary>
    public static OccluderBvh Build(IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)> triangles)
    {
        int n = triangles.Count;
        var tri = new float[n * 9];
        var index = new int[n];
        var centroid = new Vec3[n];
        var boxMin = new Vec3[n];
        var boxMax = new Vec3[n];
        for (int i = 0; i < n; i++)
        {
            (Vec3 a, Vec3 b, Vec3 c) = triangles[i];
            int o = i * 9;
            tri[o] = a.X; tri[o + 1] = a.Y; tri[o + 2] = a.Z;
            tri[o + 3] = b.X; tri[o + 4] = b.Y; tri[o + 5] = b.Z;
            tri[o + 6] = c.X; tri[o + 7] = c.Y; tri[o + 8] = c.Z;
            index[i] = i;
            boxMin[i] = Vec3Math.Min(Vec3Math.Min(a, b), c);
            boxMax[i] = Vec3Math.Max(Vec3Math.Max(a, b), c);
            centroid[i] = a.Add(b).Add(c).Scale(1f / 3f);
        }

        var nodes = new List<Node>(Math.Max(1, n / 2));
        if (n > 0)
        {
            BuildRange(nodes, index, centroid, boxMin, boxMax, 0, n);
        }

        // Reorder triangle payload to match the leaf-sorted index for cache locality.
        var ordered = new float[n * 9];
        for (int i = 0; i < n; i++)
        {
            Array.Copy(tri, index[i] * 9, ordered, i * 9, 9);
        }

        return new OccluderBvh(ordered, nodes.ToArray(), n);
    }

    private static int BuildRange(
        List<Node> nodes, int[] index, Vec3[] centroid, Vec3[] boxMin, Vec3[] boxMax, int start, int end)
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
            nodes[nodeIndex] = new Node(mn, mx, -1, -1, start, count);
            return nodeIndex;
        }

        // Split on the largest centroid extent axis at the median.
        Vec3 ext = mx.Sub(mn);
        int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : (ext.Y >= ext.Z ? 1 : 2);
        int mid = (start + end) / 2;
        Array.Sort(index, start, count, Comparer<int>.Create(
            (a, b) => centroid[a].Component(axis).CompareTo(centroid[b].Component(axis))));

        // Nodes are appended pre-order, so the right subtree's root is NOT at left+1 (the
        // left subtree occupies a variable span of nodes between them). Capture BOTH child
        // indices explicitly — assuming right == left+1 only holds when the left child is a
        // single leaf, which silently orphaned every deeper right subtree (missed shadows).
        int left = BuildRange(nodes, index, centroid, boxMin, boxMax, start, mid);
        int right = BuildRange(nodes, index, centroid, boxMin, boxMax, mid, end);
        nodes[nodeIndex] = new Node(mn, mx, left, right, 0, 0);
        return nodeIndex;
    }

    /// <summary>
    /// True when the open segment (<paramref name="origin"/>, <paramref name="target"/>)
    /// is blocked by any occluder — the shadow test. Endpoints are trimmed by a
    /// small epsilon so the surface at either end never self-shadows. When
    /// <paramref name="surfacePlane"/> is given, occluders lying in that plane are
    /// ignored (RED's mask rasterisation never shadows a surface with faces in its
    /// own plane; coplanar neighbours would otherwise blotch grazing-lit texels).
    /// </summary>
    public bool Occluded(Vec3 origin, Vec3 target, RfPlane? surfacePlane = null)
    {
        if (_rootCount == 0)
        {
            return false;
        }

        Vec3 dir = target.Sub(origin);
        float dist = dir.Length();
        if (dist < 1e-5f)
        {
            return false;
        }

        Vec3 d = dir.Scale(1f / dist);
        Vec3 inv = new(SafeInv(d.X), SafeInv(d.Y), SafeInv(d.Z));
        const float eps = 0.01f;
        float tMax = dist - eps;
        if (tMax <= eps)
        {
            return false;
        }

        // Iterative stack traversal (thread-safe: no shared mutable state).
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            Node node = _nodes[stack[--sp]];
            if (!SlabHit(node.Min, node.Max, origin, inv, eps, tMax))
            {
                continue;
            }

            if (node.Left < 0)
            {
                for (int i = 0; i < node.Count; i++)
                {
                    int t = (node.Start + i) * 9;
                    if (TriHit(t, origin, d, eps, tMax) &&
                        (surfacePlane is not RfPlane pl || !TriInPlane(t, pl)))
                    {
                        return true;
                    }
                }
            }
            else if (sp + 2 <= stack.Length)
            {
                stack[sp++] = node.Left;
                stack[sp++] = node.Right;
            }
        }

        return false;
    }

    /// <summary>True when all three triangle corners lie within a small band of the plane.</summary>
    private bool TriInPlane(int o, RfPlane pl)
    {
        const float band = 0.05f;
        Vec3 n = pl.Normal;
        for (int k = 0; k < 3; k++)
        {
            float dd = (n.X * _tri[o + (k * 3)]) + (n.Y * _tri[o + (k * 3) + 1]) +
                       (n.Z * _tri[o + (k * 3) + 2]) + pl.Offset;
            if (MathF.Abs(dd) > band)
            {
                return false;
            }
        }

        return true;
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

    private bool TriHit(int o, Vec3 orig, Vec3 dir, float tMin, float tMax)
    {
        // Möller–Trumbore.
        float ax = _tri[o], ay = _tri[o + 1], az = _tri[o + 2];
        float e1x = _tri[o + 3] - ax, e1y = _tri[o + 4] - ay, e1z = _tri[o + 5] - az;
        float e2x = _tri[o + 6] - ax, e2y = _tri[o + 7] - ay, e2z = _tri[o + 8] - az;

        float px = (dir.Y * e2z) - (dir.Z * e2y);
        float py = (dir.Z * e2x) - (dir.X * e2z);
        float pz = (dir.X * e2y) - (dir.Y * e2x);
        float det = (e1x * px) + (e1y * py) + (e1z * pz);
        if (det > -1e-8f && det < 1e-8f)
        {
            return false; // parallel
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
        return t > tMin && t < tMax;
    }
}
