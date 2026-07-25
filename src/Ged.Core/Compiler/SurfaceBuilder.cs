using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>The surfaces produced for a build plus, per surface, whether it is full-bright (filled 128).</summary>
public sealed class SurfaceBuildResult
{
    public List<Surface> Surfaces { get; } = new();

    /// <summary>Parallel to <see cref="Surfaces"/>: true when the surface's face(s) are full-bright.</summary>
    public List<bool> FullBright { get; } = new();

    /// <summary>Parallel to <see cref="Surfaces"/>: the source faces merged into each surface (for smooth normals).</summary>
    public List<List<CsgFace>> SurfaceFaces { get; } = new();
}

/// <summary>
/// Builds lightmap surfaces + atlas pages per red-geometry-compiler.md §B.6.
/// For a full build (grouping on) it merges coplanar, co-room, edge-adjacent faces
/// into one surface — undoing CSG over-splitting so a wall split into fragments
/// shares a single atlas rect (fewer pages, closer to RED's final counts). For a
/// live preview (grouping off) each lit face is its own surface (RED's preview
/// behaviour). Each surface gets a dominant-axis U/V basis, pixels-per-meter from
/// the max per-face 2-bit lightmap resolution (2.0 × {0.5,1,2,4}), a fragment rect
/// clamped to [4..64] texels (8 with holes), placement into 128×128 24bpp pages
/// with a 1-texel gutter, and the verbatim uv_scale / uv_add transform plus clamped
/// per-vertex lightmap UVs. High-Resolution Lightmaps (item 6 amendment) widens the
/// pages to 256×256, the fragment cap to 255, and the ppm ×4 (format-safe — see the
/// ctor) so projection cookies resolve; stock keeps the exact byte-parity numbers.
/// Fragment texels are seeded (128 grey for full-bright, else the room ambient, falling
/// back to the level ambient) so a no-bake build is still visible before the lighting pass
/// replaces them.
/// </summary>
public sealed class SurfaceBuilder
{
    private const int StockPageSize = 128;
    private const int StockMaxFragment = 64;
    private const int HighResPageSize = 256;   // format-safe: RF reads page w/h from the file (RF:FUN_004ed1c0)
    private const int HighResMaxFragment = 255; // surface x/y/w/h are u8 (≤255)
    private const float HighResPpmScale = 4f;   // stock max 8 px/m → 32 px/m so a gobo can resolve
    private const int Border = 1;
    private const float BasePpm = 2.0f;
    private const float CoplanarDot = 0.99899f;
    private const float OffsetWeld = 0.001f;
    private const float Quantum = 1e-3f;
    private static readonly float[] ResMultiplier = { 0.5f, 1f, 2f, 4f };

    // Item 6 (amendment): High-Resolution Lightmaps widens pages + fragment cap + ppm (format-safe).
    private readonly int _pageSize;
    private readonly int _maxFragment;
    private readonly float _ppmScale;

    private readonly List<Lightmap> _pages = new();
    private RfColor? _levelAmbient;
    private int _shelfX;
    private int _shelfY;
    private int _shelfHeight;

    /// <summary>Stock (128 pages, 64-texel fragments, ×1 ppm) unless <paramref name="highRes"/>.</summary>
    public SurfaceBuilder(bool highRes = false)
    {
        _pageSize = highRes ? HighResPageSize : StockPageSize;
        _maxFragment = highRes ? HighResMaxFragment : StockMaxFragment;
        _ppmScale = highRes ? HighResPpmScale : 1f;
    }

    public SurfaceBuildResult Build(
        List<CsgFace> faces, RoomBuildResult rooms, CompiledLevel result, bool group, RfColor? levelAmbient = null)
    {
        _levelAmbient = levelAmbient;
        var eligible = new List<CsgFace>();
        foreach (CsgFace f in faces)
        {
            if (f.IsPortal || string.IsNullOrEmpty(f.Texture) || f.Vertices.Count < 3)
            {
                continue; // portals + textureless faces carry no lightmap
            }

            // RED binds NO surface (surface_index = -1) to sky, invisible, liquid or
            // full-bright faces — verified across 12 corpus levels (0 bound of 185
            // sky / 1084 invisible / 2 liquid / 128 full-bright faces). Unbound
            // renders neutral (base × 1.0), which IS full-bright; binding a baked
            // surface to a sky face modulates the sky texture near-black (the dm01
            // black-slab regression this rule fixes).
            var flags = (FaceFlags)f.Flags;
            if ((flags & (FaceFlags.ShowSky | FaceFlags.IsInvisible | FaceFlags.LiquidSurface | FaceFlags.FullBright)) != 0)
            {
                continue;
            }

            eligible.Add(f);
        }

        List<List<CsgFace>> clusters = group ? Cluster(eligible) : SingletonClusters(eligible);

        var outp = new SurfaceBuildResult();
        foreach (List<CsgFace> cluster in clusters)
        {
            if (BuildSurface(cluster, rooms, outp.Surfaces.Count) is Surface s)
            {
                bool fullBright = ((FaceFlags)cluster[0].Flags & FaceFlags.FullBright) != 0;
                outp.Surfaces.Add(s);
                outp.FullBright.Add(fullBright);
                outp.SurfaceFaces.Add(cluster);
            }
        }

        result.Lightmaps = _pages;
        return outp;
    }

    private static List<List<CsgFace>> SingletonClusters(List<CsgFace> faces)
    {
        var list = new List<List<CsgFace>>(faces.Count);
        foreach (CsgFace f in faces)
        {
            list.Add(new List<CsgFace> { f });
        }

        return list;
    }

    /// <summary>
    /// Union-find over eligible faces: two faces merge iff same room, coplanar
    /// (normal dot ≥ 0.99899, |offset diff| &lt; 0.001) and edge-adjacent (share a
    /// welded edge).
    /// </summary>
    private static List<List<CsgFace>> Cluster(List<CsgFace> faces)
    {
        int n = faces.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++)
        {
            parent[i] = i;
        }

        // Map each welded edge (unordered quantized endpoint pair) to the faces on it.
        var edgeFaces = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < n; i++)
        {
            CsgFace f = faces[i];
            int vc = f.Vertices.Count;
            for (int e = 0; e < vc; e++)
            {
                long a = Key(f.Vertices[e].Position);
                long b = Key(f.Vertices[(e + 1) % vc].Position);
                var key = a <= b ? (a, b) : (b, a);
                if (!edgeFaces.TryGetValue(key, out List<int>? list))
                {
                    list = new List<int>(2);
                    edgeFaces[key] = list;
                }

                list.Add(i);
            }
        }

        foreach (List<int> onEdge in edgeFaces.Values)
        {
            for (int x = 0; x < onEdge.Count; x++)
            {
                for (int y = x + 1; y < onEdge.Count; y++)
                {
                    int i = onEdge[x], j = onEdge[y];
                    if (Mergeable(faces[i], faces[j]))
                    {
                        Union(parent, i, j);
                    }
                }
            }
        }

        var groups = new Dictionary<int, List<CsgFace>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(parent, i);
            if (!groups.TryGetValue(r, out List<CsgFace>? list))
            {
                list = new List<CsgFace>();
                groups[r] = list;
            }

            list.Add(faces[i]);
        }

        return new List<List<CsgFace>>(groups.Values);
    }

    private static bool Mergeable(CsgFace a, CsgFace b)
    {
        if (a.RoomIndex != b.RoomIndex)
        {
            return false;
        }

        float dot = a.Plane.Normal.Dot(b.Plane.Normal);
        if (dot < CoplanarDot)
        {
            return false;
        }

        return MathF.Abs(a.Plane.Offset - b.Plane.Offset) < OffsetWeld;
    }

    private static long Key(Vec3 p)
    {
        // Pack a coarse 21-bit-per-axis quantized position into a single long.
        long qx = (long)MathF.Round(p.X / Quantum) & 0x1FFFFF;
        long qy = (long)MathF.Round(p.Y / Quantum) & 0x1FFFFF;
        long qz = (long)MathF.Round(p.Z / Quantum) & 0x1FFFFF;
        return (qx << 42) | (qy << 21) | qz;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb)
        {
            parent[ra] = rb;
        }
    }

    private Surface? BuildSurface(List<CsgFace> cluster, RoomBuildResult rooms, int surfaceIndex)
    {
        // Area-weighted average normal → dominant axis (all faces are within ~2.5°).
        var navg = new Vec3(0, 0, 0);
        foreach (CsgFace f in cluster)
        {
            navg = navg.Add(f.Plane.Normal.Scale(MathF.Max(1e-4f, f.Area())));
        }

        Vec3 n = navg.Normalized();
        (int uAxis, int vAxis) = DominantUv(n);

        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        int resMax = 0;
        bool holes = false;
        foreach (CsgFace f in cluster)
        {
            foreach (CsgVertex vtx in f.Vertices)
            {
                float u = vtx.Position.Component(uAxis);
                float v = vtx.Position.Component(vAxis);
                minU = MathF.Min(minU, u);
                maxU = MathF.Max(maxU, u);
                minV = MathF.Min(minV, v);
                maxV = MathF.Max(maxV, v);
            }

            resMax = Math.Max(resMax, (f.Flags & (ushort)FaceFlags.LightmapResolutionMask) >> 8);
            holes |= ((FaceFlags)f.Flags & FaceFlags.HasHoles) != 0;
        }

        float uExtent = maxU - minU;
        float vExtent = maxV - minV;
        if (uExtent < 1e-4f || vExtent < 1e-4f)
        {
            return null; // edge-on / degenerate: no lightmap
        }

        float ppm = BasePpm * ResMultiplier[resMax & 3] * _ppmScale;
        int minTexels = holes ? 8 : 4;
        int w = ClampFragment((int)MathF.Round(uExtent * ppm) + 2, minTexels);
        int h = ClampFragment((int)MathF.Round(vExtent * ppm) + 2, minTexels);

        (int page, int ax, int ay) = Pack(w, h);

        // Effective ppm from the final rect (== nominal when uncapped; lower when clamped to the cap).
        float effPpmU = (w - 2) / uExtent;
        float effPpmV = (h - 2) / vExtent;

        // Representative plane: the first face's (all are coplanar within tolerance).
        CsgPlane plane = cluster[0].Plane;

        var surface = new Surface
        {
            LightmapIndex = page,
            X = (byte)ax,
            Y = (byte)ay,
            W = (byte)w,
            H = (byte)h,
            XPixelsPerMeter = effPpmU,
            YPixelsPerMeter = effPpmV,
            BoundingBox = ClusterAabb(cluster),
            Plane = new RfPlane(plane.Normal, plane.Offset),
            ShouldSmooth = cluster[0].SmoothingGroups != 0 ? 1 : 0,
            UnknownZero = 0,
            DroppedCoefficient = Dominant(n),
            UCoefficient = uAxis,
            VCoefficient = vAxis,
            RoomIndex = cluster[0].RoomIndex,
        };

        float scaleU = ((w - 2) / (float)_pageSize) / uExtent;
        float scaleV = ((h - 2) / (float)_pageSize) / vExtent;
        float addU = (-minU * scaleU) + ((ax + 1f) / _pageSize);
        float addV = (-minV * scaleV) + ((ay + 1f) / _pageSize);
        surface.UvScale = new Uv(scaleU, scaleV);
        surface.UvAdd = new Uv(addU, addV);

        foreach (CsgFace f in cluster)
        {
            var lm = new Uv[f.Vertices.Count];
            for (int i = 0; i < f.Vertices.Count; i++)
            {
                float u = f.Vertices[i].Position.Component(uAxis);
                float v = f.Vertices[i].Position.Component(vAxis);
                lm[i] = new Uv(
                    Math.Clamp((u * scaleU) + addU, 0f, 1f),
                    Math.Clamp((v * scaleV) + addV, 0f, 1f));
            }

            f.SurfaceIndex = surfaceIndex;
            f.LightmapUvs = lm;
        }

        SeedTexels(page, ax, ay, w, h, RoomAmbient(cluster[0], rooms));
        return surface;
    }

    private int ClampFragment(int size, int minTexels)
    {
        if (size < minTexels)
        {
            return minTexels;
        }

        return size > _maxFragment ? _maxFragment : size;
    }

    /// <summary>Shelf/skyline atlas packing into square pages honouring a 1-texel gutter.</summary>
    private (int Page, int X, int Y) Pack(int w, int h)
    {
        if (_pages.Count == 0)
        {
            NewPage();
        }

        if (_shelfX + w + Border > _pageSize)
        {
            _shelfX = Border;
            _shelfY += _shelfHeight + Border;
            _shelfHeight = 0;
        }

        if (_shelfY + h + Border > _pageSize)
        {
            NewPage();
        }

        int x = _shelfX;
        int y = _shelfY;
        _shelfX += w + Border;
        _shelfHeight = Math.Max(_shelfHeight, h);
        return (_pages.Count - 1, x, y);
    }

    private void NewPage()
    {
        _pages.Add(new Lightmap { Width = _pageSize, Height = _pageSize, Pixels = new byte[_pageSize * _pageSize * 3] });
        _shelfX = Border;
        _shelfY = Border;
        _shelfHeight = 0;
    }

    private void SeedTexels(int page, int ax, int ay, int w, int h, RfColor c)
    {
        byte[] px = _pages[page].Pixels;
        for (int y = 0; y < h; y++)
        {
            int py = ay + y;
            if (py >= _pageSize)
            {
                break;
            }

            for (int x = 0; x < w; x++)
            {
                int pxx = ax + x;
                if (pxx >= _pageSize)
                {
                    break;
                }

                int o = ((py * _pageSize) + pxx) * 3;
                px[o] = c.R;
                px[o + 1] = c.G;
                px[o + 2] = c.B;
            }
        }
    }

    private RfColor RoomAmbient(CsgFace f, RoomBuildResult rooms)
    {
        if ((FaceFlags)f.Flags is var flags && (flags & FaceFlags.FullBright) != 0)
        {
            return new RfColor(128, 128, 128, 255);
        }

        if (f.RoomIndex >= 0 && f.RoomIndex < rooms.Rooms.Count)
        {
            Room r = rooms.Rooms[f.RoomIndex];
            if (r.HasAmbientLight != 0 && r.AmbientColor is RfColor a)
            {
                return new RfColor((byte)(a.R >> 1), (byte)(a.G >> 1), (byte)(a.B >> 1), 255);
            }
        }

        // No per-room ambient: seed the LEVEL ambient (halved to the bake's ambient floor —
        // matching AmbientField.ForRoom + the Lightmapper's amb×0.5), exactly as RED seeds the
        // room/level ambient into unbaked fragments (red-geometry-compiler.md §B.6). This keeps
        // an unbaked save consistent with a zero-light bake instead of the old flat 64 grey (a
        // magic value that ignored the level ambient). Baked builds overwrite the seed, so this
        // touches only unbaked/preview output — the stock-bake byte-identity gates are unaffected.
        if (_levelAmbient is RfColor la)
        {
            return new RfColor((byte)(la.R >> 1), (byte)(la.G >> 1), (byte)(la.B >> 1), 255);
        }

        return new RfColor(64, 64, 64, 255); // neutral pre-lighting grey (no level ambient supplied)
    }

    private static (int UAxis, int VAxis) DominantUv(Vec3 n)
    {
        int drop = Dominant(n);
        return drop switch
        {
            0 => (1, 2),
            1 => (0, 2),
            _ => (0, 1),
        };
    }

    private static int Dominant(Vec3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        return ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);
    }

    private static Aabb ClusterAabb(List<CsgFace> cluster)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (CsgFace f in cluster)
        {
            f.GrowAabb(ref mn, ref mx);
        }

        return new Aabb(mn, mx);
    }
}
