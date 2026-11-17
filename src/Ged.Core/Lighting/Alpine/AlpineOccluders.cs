using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>One compiled face offered to the Alpine occluder tree.</summary>
/// <param name="Polygon">World-space corners, in winding order.</param>
/// <param name="Uvs">Texture coordinates per corner (null: none).</param>
/// <param name="Flags">The RFL face flags.</param>
/// <param name="Texture">The face's texture file name.</param>
/// <param name="Surface">The lightmap surface the face belongs to (−1 none): a receiver skips its own surface's triangles.</param>
/// <param name="Normal">The face's plane normal ("front").</param>
/// <param name="IsPortal">A portal face (never an occluder).</param>
/// <param name="NoShadowCastUid">The "No shadow cast" brush the face belongs to, or −1.</param>
public readonly record struct AlpineOccluderFace(
    IReadOnlyList<Vec3> Polygon, IReadOnlyList<Uv>? Uvs, ushort Flags, string Texture, int Surface, Vec3 Normal, bool IsPortal,
    int NoShadowCastUid = -1);

/// <summary>One world-space mesh triangle offered to the tree (an Alpine mesh object's LOD0).</summary>
public readonly record struct AlpineMeshOccluder(Vec3 A, Vec3 B, Vec3 C, Uv UvA, Uv UvB, Uv UvC, string Texture, bool Additive, int Uid);

/// <summary>What the tree needs to know about a texture: whether it has an alpha channel, and its alpha mask.</summary>
public interface IAlpineTextureAlpha
{
    /// <summary>True when the texture carries alpha (Alpine's <c>bm_has_alpha</c>).</summary>
    bool HasAlpha(string texture);

    /// <summary>The texture's alpha mask, or null when it has none or cannot be read.</summary>
    AlpineAlphaMask? Mask(string texture);
}

/// <summary>
/// Alpine RED's fixed-pipeline occluder tree, reimplemented from editor_patch/lightmap.cpp:813-1470: every compiled face
/// but portals and see-through faces (a see-through face with holes blocks by its texture's alpha under
/// <c>alpha_tested_occlusion</c>), less the faces of "No shadow cast" brushes, plus — when the level says
/// <c>meshes_occlude</c> — the Alpine mesh objects' LOD0 triangles; built as Alpine builds it (median split on the
/// longest centroid axis, 8 triangles a leaf, depth ≤ 48). Each triangle keeps its face flags, so each light skips
/// the classes its rules exclude (liquid, sky, invisible — one-sided when invisible faces occlude — and alpha-textured
/// faces); the receiver's own surface and coplanar geometry at the receiver are skipped; alpha-tested triangles
/// attenuate by their coverage over the receiving texel's footprint, up to 8 merged layers per ray.
/// </summary>
public sealed class AlpineOccluders
{
    /// <summary>Alpine's synthetic flag for a face whose texture has alpha (lightmap.cpp:819-821).</summary>
    public const uint AlphaTexture = 0x80000000u;

    public const uint FaceShowSky = 0x1;
    public const uint FaceLiquid = 0x4;
    public const uint FaceSeeThrough = 0x40;
    public const uint FaceHasHoles = 0x80;
    public const uint FaceInvisible = 0x2000;

    public const float RayLift = 0.02f;
    public const float RayEps = 0.01f;
    private const float RayBand = 0.05f;
    private const float OneSideEps = 1e-4f;
    private const float AlphaBlocked = 1f / 512f;
    private const float AlphaFilterWidth = 2f;
    private const float AlphaMinCosine = 0.25f;
    private const float AlphaLayerGap = 0.25f;
    private const float AlphaLayerParallel = 0.9f;
    private const float AlphaLayerCoincident = 1e-3f;
    private const int AlphaMaxLayers = 8;
    private const int LeafSize = 8;
    private const int MaxDepth = 48;

    private readonly struct Tri
    {
        public Tri(Vec3 a, Vec3 e1, Vec3 e2, Vec3 n, Vec3 mn, Vec3 mx, int surf, uint flags, int alpha)
        {
            V0 = a;
            E1 = e1;
            E2 = e2;
            Normal = n;
            Min = mn;
            Max = mx;
            Surf = surf;
            Flags = flags;
            Alpha = alpha;
        }

        public Vec3 V0 { get; }

        public Vec3 E1 { get; }

        public Vec3 E2 { get; }

        public Vec3 Normal { get; }

        public Vec3 Min { get; }

        public Vec3 Max { get; }

        public int Surf { get; }

        public uint Flags { get; }

        public int Alpha { get; }
    }

    private sealed class AlphaTri
    {
        public required AlpineAlphaMask Mask { get; init; }

        public float U0, V0, Du1, Dv1, Du2, Dv2, Density;

        public AlpineAlphaMask.Rect? Rect;
    }

    private readonly struct Node
    {
        public Node(Vec3 mn, Vec3 mx, int start, int count, int left, int right)
        {
            Min = mn;
            Max = mx;
            Start = start;
            Count = count;
            Left = left;
            Right = right;
        }

        public Vec3 Min { get; }

        public Vec3 Max { get; }

        public int Start { get; }

        public int Count { get; }

        public int Left { get; }

        public int Right { get; }
    }

    private readonly Tri[] _tris;
    private readonly AlphaTri[] _alpha;
    private readonly Node[] _nodes;

    private AlpineOccluders(Tri[] tris, AlphaTri[] alpha, Node[] nodes, Dictionary<int, int> dropped)
    {
        _tris = tris;
        _alpha = alpha;
        _nodes = nodes;
        NoShadowCastDropped = dropped;
    }

    /// <summary>True when the tree holds no triangle.</summary>
    public bool IsEmpty => _tris.Length == 0;

    public int TriangleCount => _tris.Length;

    /// <summary>Triangles that block by their texture's alpha (alpha-tested occlusion).</summary>
    public int AlphaTestedTriangles => _alpha.Length;

    /// <summary>Occluder faces left out per "No shadow cast" brush uid.</summary>
    public IReadOnlyDictionary<int, int> NoShadowCastDropped { get; }

    /// <summary>Builds the tree (lightmap.cpp:1100-1257).</summary>
    public static AlpineOccluders Build(
        IEnumerable<AlpineOccluderFace> faces, IEnumerable<AlpineMeshOccluder>? meshes, bool alphaTested, IAlpineTextureAlpha? textures)
    {
        var tris = new List<Tri>();
        var alpha = new List<AlphaTri>();
        var dropped = new Dictionary<int, int>();
        var idx = new List<int>();
        foreach (AlpineOccluderFace f in faces)
        {
            if (f.IsPortal || f.Polygon.Count < 3)
            {
                continue;
            }

            uint flags = f.Flags;
            AlpineAlphaMask? mask = null;
            if ((flags & FaceSeeThrough) != 0)
            {
                // A see-through face blocks only by its texels' alpha, and only with holes (glass lets light through).
                mask = alphaTested && (flags & FaceHasHoles) != 0 && f.Uvs is not null ? textures?.Mask(f.Texture) : null;
                if (mask is null)
                {
                    continue;
                }
            }

            if (f.NoShadowCastUid >= 0)
            {
                dropped[f.NoShadowCastUid] = dropped.GetValueOrDefault(f.NoShadowCastUid) + 1;
                continue;
            }

            if (mask is null && (flags & 0x2004u) == 0 && !string.IsNullOrEmpty(f.Texture) && textures?.HasAlpha(f.Texture) == true)
            {
                flags |= AlphaTexture;
            }

            Triangulate(f.Polygon, idx);
            for (int k = 0; k + 2 < idx.Count; k += 3)
            {
                if (!Make(f.Polygon[idx[k]], f.Polygon[idx[k + 1]], f.Polygon[idx[k + 2]], f.Surface, flags, f.Normal, out Tri t))
                {
                    continue;
                }

                if (mask is not null)
                {
                    if (AddAlpha(t, mask, f.Uvs![idx[k]], f.Uvs[idx[k + 1]], f.Uvs[idx[k + 2]], alpha) is not { } ai)
                    {
                        continue;
                    }

                    t = new Tri(t.V0, t.E1, t.E2, t.Normal, t.Min, t.Max, t.Surf, t.Flags, ai);
                }

                tris.Add(t);
            }
        }

        foreach (AlpineMeshOccluder m in meshes ?? Array.Empty<AlpineMeshOccluder>())
        {
            // additive glows and beams add light, they never stop it (lightmap.cpp:1212-1216)
            if (alphaTested && m.Additive)
            {
                continue;
            }

            uint flags = textures?.HasAlpha(m.Texture) == true ? AlphaTexture : 0u;
            if (!Make(m.A, m.B, m.C, -1, flags, default, out Tri t))
            {
                continue;
            }

            if (alphaTested && !string.IsNullOrEmpty(m.Texture))
            {
                if (textures?.Mask(m.Texture) is { } mask && AddAlpha(t, mask, m.UvA, m.UvB, m.UvC, alpha) is { } ai)
                {
                    t = new Tri(t.V0, t.E1, t.E2, t.Normal, t.Min, t.Max, -1, 0u, ai);
                }
            }

            tris.Add(t);
        }

        Tri[] arr = tris.ToArray();
        var order = new int[arr.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        var nodes = new List<Node>(Math.Max(1, arr.Length / 2));
        if (arr.Length > 0)
        {
            BuildRange(arr, order, nodes, 0, arr.Length, 0);
        }

        var ordered = new Tri[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            ordered[i] = arr[order[i]];
        }

        return new AlpineOccluders(ordered, alpha.ToArray(), nodes.ToArray(), dropped);
    }

    private static bool Make(Vec3 a, Vec3 b, Vec3 c, int surf, uint flags, Vec3 faceNormal, out Tri t)
    {
        t = default;
        if (!Finite(a) || !Finite(b) || !Finite(c))
        {
            return false;
        }

        Vec3 e1 = b.Sub(a), e2 = c.Sub(a);
        Vec3 n = e1.Cross(e2);
        float len = n.Length();
        if (!(len >= 1e-12f) || !float.IsFinite(len))
        {
            return false;
        }

        n = n.Scale(1f / len);
        if (faceNormal.Dot(n) < 0f)
        {
            n = n.Negate(); // the face's own plane normal is what "front" means
        }

        t = new Tri(a, e1, e2, n, Vec3Math.Min(Vec3Math.Min(a, b), c), Vec3Math.Max(Vec3Math.Max(a, b), c), surf, flags, -1);
        return true;
    }

    private static bool Finite(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static int? AddAlpha(in Tri t, AlpineAlphaMask mask, Uv uv0, Uv uv1, Uv uv2, List<AlphaTri> list)
    {
        if (!float.IsFinite(uv0.U) || !float.IsFinite(uv0.V) || !float.IsFinite(uv1.U) || !float.IsFinite(uv1.V)
            || !float.IsFinite(uv2.U) || !float.IsFinite(uv2.V))
        {
            return null;
        }

        var a = new AlphaTri
        {
            Mask = mask,
            U0 = uv0.U,
            V0 = uv0.V,
            Du1 = uv1.U - uv0.U,
            Dv1 = uv1.V - uv0.V,
            Du2 = uv2.U - uv0.U,
            Dv2 = uv2.V - uv0.V,
        };

        // an atlas part or a card: filtering must not reach the texture around it (lightmap.cpp:1074-1087)
        float loU = MathF.Min(MathF.Min(uv0.U, uv1.U), uv2.U), loV = MathF.Min(MathF.Min(uv0.V, uv1.V), uv2.V);
        float tileU = MathF.Floor(loU + 1e-4f), tileV = MathF.Floor(loV + 1e-4f);
        float hiU = MathF.Max(MathF.Max(uv0.U, uv1.U), uv2.U) - tileU, hiV = MathF.Max(MathF.Max(uv0.V, uv1.V), uv2.V) - tileV;
        if (hiU <= 1f + 1e-4f && hiV <= 1f + 1e-4f)
        {
            a.U0 -= tileU;
            a.V0 -= tileV;
            a.Rect = new AlpineAlphaMask.Rect(MathF.Max(loU - tileU, 0f), MathF.Max(loV - tileV, 0f), MathF.Min(hiU, 1f), MathF.Min(hiV, 1f));
        }

        float world = t.E1.Cross(t.E2).Length();
        (int fw, int fh, _) = mask.Levels[0];
        float texels = MathF.Abs((a.Du1 * a.Dv2) - (a.Du2 * a.Dv1)) * fw * fh;
        float density = MathF.Sqrt(texels / world);
        a.Density = float.IsFinite(density) ? density : 0f;
        list.Add(a);
        return list.Count - 1;
    }

    /// <summary>
    /// Ear-clipped triangles of a polygon in its dominant projection (lightmap.cpp:944-1037), falling back to a fan for
    /// a self-intersecting or degenerate loop. Writes corner indices, three per triangle.
    /// </summary>
    internal static void Triangulate(IReadOnlyList<Vec3> v, List<int> outIdx)
    {
        outIdx.Clear();
        int n = v.Count;
        if (n < 3)
        {
            return;
        }

        if (n == 3)
        {
            outIdx.Add(0);
            outIdx.Add(1);
            outIdx.Add(2);
            return;
        }

        float nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < n; i++)
        {
            Vec3 a = v[i], b = v[(i + 1) % n];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }

        float ax = MathF.Abs(nx), ay = MathF.Abs(ny), az = MathF.Abs(nz);
        int drop = ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);
        var px = new float[n];
        var py = new float[n];
        var idx = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            px[i] = v[i].Component(drop == 0 ? 1 : 0);
            py[i] = v[i].Component(drop == 2 ? 1 : 2);
            idx.Add(i);
        }

        float area = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            area += (px[i] * py[j]) - (px[j] * py[i]);
        }

        if (area < 0f)
        {
            idx.Reverse();
        }

        float Side(int a, int b, float x, float y) => ((px[b] - px[a]) * (y - py[a])) - ((py[b] - py[a]) * (x - px[a]));

        while (idx.Count > 3)
        {
            int m = idx.Count;
            bool clipped = false;
            for (int i = 0; i < m; i++)
            {
                int i0 = idx[(i + m - 1) % m], i1 = idx[i], i2 = idx[(i + 1) % m];
                if (Side(i0, i1, px[i2], py[i2]) <= 0f)
                {
                    continue;
                }

                bool ok = true;
                for (int k = 0; k < m && ok; k++)
                {
                    int p = idx[k];
                    if (p == i0 || p == i1 || p == i2)
                    {
                        continue;
                    }

                    ok = !(Side(i0, i1, px[p], py[p]) > 0f && Side(i1, i2, px[p], py[p]) > 0f && Side(i2, i0, px[p], py[p]) > 0f);
                }

                if (!ok)
                {
                    continue;
                }

                outIdx.Add(i0);
                outIdx.Add(i1);
                outIdx.Add(i2);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                outIdx.Clear();
                for (int i = 2; i < n; i++)
                {
                    outIdx.Add(0);
                    outIdx.Add(i - 1);
                    outIdx.Add(i);
                }

                return;
            }
        }

        outIdx.Add(idx[0]);
        outIdx.Add(idx[1]);
        outIdx.Add(idx[2]);
    }

    private static int BuildRange(Tri[] tris, int[] order, List<Node> nodes, int begin, int end, int depth)
    {
        int self = nodes.Count;
        nodes.Add(default);
        var bmin = new Vec3(1e30f, 1e30f, 1e30f);
        var bmax = new Vec3(-1e30f, -1e30f, -1e30f);
        Vec3 cmin = bmin, cmax = bmax;
        for (int i = begin; i < end; i++)
        {
            Tri t = tris[order[i]];
            bmin = Vec3Math.Min(bmin, t.Min);
            bmax = Vec3Math.Max(bmax, t.Max);
            Vec3 c = t.Min.Add(t.Max).Scale(0.5f);
            cmin = Vec3Math.Min(cmin, c);
            cmax = Vec3Math.Max(cmax, c);
        }

        int count = end - begin;
        if (count <= LeafSize || depth >= MaxDepth)
        {
            nodes[self] = new Node(bmin, bmax, begin, count, -1, -1);
            return self;
        }

        Vec3 ext = cmax.Sub(cmin);
        int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : (ext.Y >= ext.Z ? 1 : 2);
        int mid = begin + (count / 2);
        Array.Sort(order, begin, count, Comparer<int>.Create((a, b) =>
        {
            float ca = (tris[a].Min.Component(axis) + tris[a].Max.Component(axis)) * 0.5f;
            float cb = (tris[b].Min.Component(axis) + tris[b].Max.Component(axis)) * 0.5f;
            return ca != cb ? ca.CompareTo(cb) : a.CompareTo(b);
        }));
        int left = BuildRange(tris, order, nodes, begin, mid, depth + 1);
        int right = BuildRange(tris, order, nodes, mid, end, depth + 1);
        nodes[self] = new Node(bmin, bmax, 0, 0, left, right);
        return self;
    }

    /// <summary>One ray's query (lightmap.cpp:901-913).</summary>
    public struct Query
    {
        public Vec3 Origin;
        public Vec3 Dir;
        public Vec3 SurfNormal;
        public float Nd;
        public float TMin;
        public float TMax;
        public int SkipSurf;
        public uint SkipFlags;
        public uint OneSideFlags;
        public float Footprint;
        public float Converge;
    }

    /// <summary>The fraction of the ray's light that reaches tmax (lightmap.cpp:1313-1470).</summary>
    public float Transmittance(in Query q)
    {
        if (_nodes.Length == 0)
        {
            return 1f;
        }

        static float SafeInv(float c) => c > -1e-8f && c < 1e-8f ? (c < 0f ? -1e8f : 1e8f) : 1f / c;
        Vec3 o = q.Origin, d = q.Dir;
        float ix = SafeInv(d.X), iy = SafeInv(d.Y), iz = SafeInv(d.Z);
        Span<int> stack = stackalloc int[(MaxDepth * 2) + 8];
        int sp = 0;
        stack[sp++] = 0;
        Span<float> layerDist = stackalloc float[AlphaMaxLayers];
        Span<float> layerCover = stackalloc float[AlphaMaxLayers];
        Span<Vec3> layerNormal = stackalloc Vec3[AlphaMaxLayers];
        var layerMask = new AlpineAlphaMask?[AlphaMaxLayers];
        int layers = 0;
        float folded = 1f;
        while (sp > 0)
        {
            Node nd = _nodes[stack[--sp]];
            float t0 = q.TMin, t1 = q.TMax;
            Slab(nd.Min.X, nd.Max.X, o.X, ix, ref t0, ref t1);
            Slab(nd.Min.Y, nd.Max.Y, o.Y, iy, ref t0, ref t1);
            Slab(nd.Min.Z, nd.Max.Z, o.Z, iz, ref t0, ref t1);
            if (!(t0 <= t1))
            {
                continue;
            }

            if (nd.Count == 0)
            {
                if (nd.Left >= 0 && sp < stack.Length)
                {
                    stack[sp++] = nd.Left;
                }

                if (nd.Right >= 0 && sp < stack.Length)
                {
                    stack[sp++] = nd.Right;
                }

                continue;
            }

            for (int i = nd.Start; i < nd.Start + nd.Count; i++)
            {
                ref readonly Tri t = ref _tris[i];
                if (t.Surf == q.SkipSurf && q.SkipSurf >= 0)
                {
                    continue;
                }

                if ((t.Flags & q.SkipFlags) != 0)
                {
                    continue;
                }

                // a one-sided face stops light arriving on its front: along this receiver→light ray, dot(d, n) > 0
                if ((t.Flags & q.OneSideFlags) != 0 && d.Dot(t.Normal) < OneSideEps)
                {
                    continue;
                }

                Vec3 p = d.Cross(t.E2);
                float det = t.E1.Dot(p);
                if (det > -1e-9f && det < 1e-9f)
                {
                    continue;
                }

                float invDet = 1f / det;
                Vec3 tv = o.Sub(t.V0);
                float u = tv.Dot(p) * invDet;
                if (u < 0f || u > 1f)
                {
                    continue;
                }

                Vec3 qq = tv.Cross(t.E1);
                float v = d.Dot(qq) * invDet;
                if (v < 0f || u + v > 1f)
                {
                    continue;
                }

                float dist = t.E2.Dot(qq) * invDet;
                if (dist <= q.TMin || dist >= q.TMax)
                {
                    continue;
                }

                // the receiving surface's own plane (both sides), not everything within a ray length of it
                if (MathF.Abs(RayLift + (dist * q.Nd)) < RayBand && MathF.Abs(t.Normal.Dot(q.SurfNormal)) > 0.999f)
                {
                    continue;
                }

                if (t.Alpha < 0)
                {
                    return 0f;
                }

                AlphaTri a = _alpha[t.Alpha];
                float facing = MathF.Abs(d.Dot(t.Normal));
                float width = q.Footprint * MathF.Max(0f, 1f - (dist * q.Converge));
                float lod = 0f;
                if (a.Density > 0f && width > 0f)
                {
                    float texels = AlphaFilterWidth * width / MathF.Max(facing, AlphaMinCosine) * a.Density;
                    lod = texels > 1f ? MathF.Log2(texels) : 0f;
                }

                float cover = a.Mask.Coverage(a.U0 + (a.Du1 * u) + (a.Du2 * v), a.V0 + (a.Dv1 * u) + (a.Dv2 * v), lod, a.Rect);
                bool merged = false;
                for (int k = 0; k < layers && !merged; k++)
                {
                    float gap = MathF.Abs(dist - layerDist[k]);
                    if (ReferenceEquals(layerMask[k], a.Mask)
                        && (gap <= AlphaLayerCoincident || (gap * facing <= AlphaLayerGap && MathF.Abs(t.Normal.Dot(layerNormal[k])) > AlphaLayerParallel)))
                    {
                        layerCover[k] = MathF.Max(layerCover[k], cover);
                        merged = true;
                    }
                }

                if (!merged)
                {
                    if (layers < AlphaMaxLayers)
                    {
                        layerMask[layers] = a.Mask;
                        layerNormal[layers] = t.Normal;
                        layerDist[layers] = dist;
                        layerCover[layers] = cover;
                        layers++;
                    }
                    else
                    {
                        folded *= 1f - cover;
                    }
                }

                float passed = folded;
                for (int k = 0; k < layers; k++)
                {
                    passed *= 1f - layerCover[k];
                }

                if (passed < AlphaBlocked)
                {
                    return 0f;
                }
            }
        }

        float result = folded;
        for (int k = 0; k < layers; k++)
        {
            result *= 1f - layerCover[k];
        }

        return result;
    }

    private static void Slab(float lo, float hi, float o, float inv, ref float t0, ref float t1)
    {
        float na = (lo - o) * inv, fa = (hi - o) * inv;
        if (na > fa)
        {
            (na, fa) = (fa, na);
        }

        t0 = na > t0 ? na : t0;
        t1 = fa < t1 ? fa : t1;
    }

    /// <summary>The face classes a light's rays pass through (light_rays_setup, lightmap.cpp:1876-1918).</summary>
    public static (uint Skip, uint OneSide) RayFlags(in EngineLight light, AlpineLightmapSettings s)
    {
        uint skip = FaceLiquid;
        if (light.Type == EngineLightType.Directional)
        {
            skip = light.SkyPasses ? FaceShowSky : 0u;
            if (!light.LiquidOccludes)
            {
                skip |= FaceLiquid;
            }
        }

        if (!s.InvisibleFacesOcclude)
        {
            skip |= FaceInvisible;
        }

        if (!s.AlphaFacesOcclude)
        {
            skip |= AlphaTexture;
        }

        return (skip, s.InvisibleFacesOcclude ? FaceInvisible : 0u);
    }

    /// <summary>A mask byte from summed transmittance over <paramref name="taken"/> rays (lightmap.cpp:1977-1982).</summary>
    public static byte MaskByte(float lit, int taken)
    {
        if (taken == 0)
        {
            return 255;
        }

        int lit255 = (int)MathF.Round(lit * 255f, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp((lit255 + (taken / 2)) / taken, 0, 255);
    }

    /// <summary>
    /// A point, spot or tube light's mask byte at a receiver: one ray to a point light, one to each end of a tube
    /// (lightmap.cpp:1955-1976). <paramref name="origin"/> is already lifted off the receiver.
    /// </summary>
    public byte PointMask(in EngineLight light, Vec3 origin, Vec3 ns, int skipSurf, float footprint, uint skip, uint oneSide)
    {
        var q = new Query { Origin = origin, SurfNormal = ns, TMin = RayEps, SkipSurf = skipSurf, SkipFlags = skip, OneSideFlags = oneSide, Footprint = footprint };
        float lit = 0f;
        int taken = 0;
        int samples = light.IsArea ? 2 : 1;
        for (int k = 0; k < samples; k++)
        {
            Vec3 target = k == 0 ? light.Position : light.Position2;
            Vec3 dv = target.Sub(origin);
            float len = dv.Length();
            if (!(len >= 1e-4f))
            {
                taken++;
                lit += 1f;
                continue;
            }

            if (light.Range > 0f && len > light.Range)
            {
                continue; // outside the light's range: the mask is never read
            }

            q.Dir = dv.Scale(1f / len);
            q.Nd = ns.Dot(q.Dir);
            q.TMax = len - RayEps;
            q.Converge = 1f / len;
            taken++;
            lit += Transmittance(q);
        }

        return MaskByte(lit, taken);
    }

    /// <summary>
    /// A directional light's mask byte: its 1 or 9 fixed directions, each to 1e6 units or the light volume's exit when
    /// only occluders inside it may shadow (lightmap.cpp:1939-1954).
    /// </summary>
    public byte DirectionalMask(in EngineLight light, Vec3 origin, Vec3 ns, int skipSurf, float footprint, uint skip, uint oneSide)
    {
        Vec3[] dirs = light.ShadowDirections ?? new[] { light.Position };
        AlpineLightVolume? clip = light.OutsideCasts ? null : light.Volume;
        var q = new Query { Origin = origin, SurfNormal = ns, TMin = RayEps, SkipSurf = skipSurf, SkipFlags = skip, OneSideFlags = oneSide, Footprint = footprint };
        float lit = 0f;
        foreach (Vec3 dir in dirs)
        {
            q.Dir = dir;
            q.Nd = ns.Dot(dir);
            q.TMax = clip is not null ? MathF.Min(1e6f, clip.RayExitDistance(origin, dir)) : 1e6f;
            lit += Transmittance(q);
        }

        return MaskByte(lit, dirs.Length);
    }
}
