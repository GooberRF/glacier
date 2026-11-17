using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// The occluders only Alpine directional lights consult (the stock occluder set never holds them): liquid surfaces,
/// which block a light whose "liquid occludes" is set, and show-sky faces, which block one whose "sky passes" is off
/// (editor_patch/lightmap.cpp:1876-1903, 2685-2703).
/// </summary>
internal sealed class DirectionalOccluders
{
    public DirectionalOccluders(OccluderBvh liquid, OccluderBvh sky)
    {
        Liquid = liquid;
        Sky = sky;
    }

    public OccluderBvh Liquid { get; }

    public OccluderBvh Sky { get; }

    /// <summary>The liquid-surface and show-sky triangles of <paramref name="faces"/> (polygons as vertex lists).</summary>
    public static DirectionalOccluders Build(IEnumerable<(ushort Flags, IReadOnlyList<Vec3> Polygon)> faces)
    {
        var liquid = new List<(Vec3, Vec3, Vec3)>();
        var sky = new List<(Vec3, Vec3, Vec3)>();
        foreach ((ushort flags, IReadOnlyList<Vec3> poly) in faces)
        {
            var f = (FaceFlags)flags;
            List<(Vec3, Vec3, Vec3)>? into = (f & FaceFlags.LiquidSurface) != 0 ? liquid : (f & FaceFlags.ShowSky) != 0 ? sky : null;
            if (into is null || poly.Count < 3)
            {
                continue;
            }

            for (int i = 1; i < poly.Count - 1; i++)
            {
                into.Add((poly[0], poly[i], poly[i + 1]));
            }
        }

        return new DirectionalOccluders(OccluderBvh.Build(liquid), OccluderBvh.Build(sky));
    }
}

/// <summary>
/// A rigid local→world transform for a MOVER surface. RED stores mover lightmap surfaces in the mover's
/// LOCAL (brush) space (verified: dmabrupt mover 265 surface plane == its local face plane) but bakes them
/// against the WORLD lights/occluders at the mover's rest position. When set, the baker maps each texel's
/// local position and normal to world via <c>Position + Rotation·local</c> before doing the light math.
/// </summary>
public readonly record struct MoverTransform(Vec3 Position, Mat3 Rotation);

/// <summary>A surface plus the per-face metadata the bake needs (full-bright flag, smooth normals, gather multiplier).</summary>
public sealed class SurfaceBake
{
    public SurfaceBake(
        Surface surface, bool fullBright, IReadOnlyList<SmoothFace>? smoothFaces = null, float lightMultiplier = 1f,
        MoverTransform? moverTransform = null, int surfaceIndex = -1)
    {
        SurfaceIndex = surfaceIndex;
        Surface = surface;
        FullBright = fullBright;
        SmoothFaces = smoothFaces;
        LightMultiplier = lightMultiplier;
        MoverTransform = moverTransform;
    }

    public Surface Surface { get; }

    /// <summary>
    /// The surface's index in its solid (the occluder tree's surface ids), or −1: with the Alpine pipeline a receiver's
    /// shadow rays skip its own surface's triangles (editor_patch/lightmap.cpp:1374-1376).
    /// </summary>
    public int SurfaceIndex { get; }

    /// <summary>Non-null for a mover surface baked in local space against the world at its rest position.</summary>
    public MoverTransform? MoverTransform { get; }

    /// <summary>Full-bright faces are filled with neutral 128 grey (no lighting).</summary>
    public bool FullBright { get; }

    /// <summary>The smoothed faces (world polygons + interpolated vertex normals), or null for a flat surface.</summary>
    public IReadOnlyList<SmoothFace>? SmoothFaces { get; }

    /// <summary>
    /// Multiplier on light (not ambient) contributions. RED's per-face light gather
    /// collects a light once per room it is registered in, without dedup — a
    /// surface in a detail SUBROOM sees each light twice (subroom + parent room),
    /// verified texel-exact against the corpus (glass_house beams, dm01 platform:
    /// RED == ambient + 2 × GED's single-count contribution). Pass 2 here.
    /// </summary>
    public float LightMultiplier { get; }
}

/// <summary>
/// The multithreaded lightmap baker: reproduces RED's per-surface texel loop
/// (memset ambient×0.5, add each in-range light's premultiplied colour weighted by
/// the kernel factor and a raycast shadow mask, encode float→byte with the
/// proportional overbright clamp) and its quality post-passes (per-texel room
/// ambient, 1-px border replication into the atlas gutter). Parallel over surfaces;
/// the occluder BVH and page byte arrays are the only shared state and both are
/// written at disjoint indices, so no locking is needed on the hot path.
/// </summary>
public static class Lightmapper
{
    private const byte Neutral = 128;

    /// <summary>
    /// Bakes lighting into <paramref name="pages"/> in place. <paramref name="lights"/>
    /// are the lights that should contribute (caller excludes editor-only lights from
    /// the game bake); disabled and black lights are skipped internally.
    /// </summary>
    public static BakeStats Bake(
        IReadOnlyList<SurfaceBake> surfaces,
        IReadOnlyList<Lightmap> pages,
        IReadOnlyList<EngineLight> lights,
        OccluderBvh occluders,
        AmbientField ambient,
        LightingOptions options) =>
        Bake(surfaces, pages, lights, occluders, ambient, options, extra: null, charts: null, alpine: null);

    /// <summary>
    /// The full bake: <paramref name="extra"/> carries the occluders only Alpine directional lights consult (liquid
    /// and show-sky faces); <paramref name="charts"/>, when given, runs Alpine's chart bake after the stock pass,
    /// against the same lights, occluders and ambient (<see cref="Alpine.AlpineChartBaker"/>).
    /// </summary>
    internal static BakeStats Bake(
        IReadOnlyList<SurfaceBake> surfaces,
        IReadOnlyList<Lightmap> pages,
        IReadOnlyList<EngineLight> lights,
        OccluderBvh occluders,
        AmbientField ambient,
        LightingOptions options,
        DirectionalOccluders? extra,
        Alpine.IAlpineChartCapture? charts,
        Alpine.AlpineShading? alpine)
    {
        var stats = new BakeStats { Surfaces = surfaces.Count };

        // Active lights only (enabled, non-black); keep AABBs for surface culling.
        var active = new List<EngineLight>(lights.Count);
        foreach (EngineLight l in lights)
        {
            if (l.Enabled && !l.IsBlack)
            {
                active.Add(l);
            }
        }

        stats.Lights = active.Count;
        var bounds = new Aabb[active.Count];
        for (int i = 0; i < active.Count; i++)
        {
            bounds[i] = active[i].Bounds;
        }

        int maxThreads = options.MaxThreads > 0 ? options.MaxThreads : Environment.ProcessorCount;
        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, maxThreads),
            CancellationToken = options.Cancellation,
        };

        int texelTotal = 0;
        int maxLights = 0;
        int overLimit = 0;
        int done = 0;
        object gate = new();

        // Feature 1: keep each surface's DIRECT-lit float buffer so a gather bounce can
        // read it, and the AO/soft-shadow modifiers can be applied per texel. When the
        // method is stock RED Classic (no bounce/AO/soft) the buffers are computed exactly
        // as before and the encode is byte-identical — the parity gates stay green.
        int count = surfaces.Count;
        var buffers = new float[count][];
        var mappers = new SurfaceTexelMapper[count];
        var widths = new int[count];
        var heights = new int[count];

        // Item 6 (amendment 2, lever 1): a surface reached by a high-sharpness projection cookie is
        // EXCLUDED from the interior smoothing pass — that edge-aware blur softens lighting seams but
        // also smears a crisp gobo boundary. Keeping the raw (unsmoothed) texels there is what makes a
        // >80%-sharpness cookie actually read as sharp.
        var cookieCrisp = new bool[count];

        Parallel.For(0, count, po, () => (Texels: 0, MaxLights: 0, Over: 0), (si, _, local) =>
        {
            SurfaceBake sb = surfaces[si];
            Surface s = sb.Surface;
            Lightmap page = pages[s.LightmapIndex];
            mappers[si] = new SurfaceTexelMapper(s, page.Width, page.Height);
            widths[si] = s.W;
            heights[si] = s.H;

            if (sb.FullBright)
            {
                FillFragment(page, s, Neutral, Neutral, Neutral);
                buffers[si] = null!;
            }
            else
            {
                var affecting = new List<int>();
                // A mover surface's bbox is in LOCAL space; the lights are in world space, so the light
                // culling must use the surface's WORLD bbox (else no world light ever overlaps it and the
                // mover bakes ambient-only — the door stayed dark).
                Aabb sbox = sb.MoverTransform is MoverTransform mtb ? WorldAabb(s.BoundingBox, mtb) : s.BoundingBox;
                for (int i = 0; i < active.Count; i++)
                {
                    if (Overlaps(bounds[i], sbox))
                    {
                        affecting.Add(i);
                    }
                }

                local.MaxLights = Math.Max(local.MaxLights, affecting.Count);
                if (affecting.Count > 64)
                {
                    local.Over++;
                }

                foreach (int li in affecting)
                {
                    EngineLight al = active[li];
                    if (al.Cookie is not null && al.CookieSharpness >= CookieCrispSmoothingThreshold)
                    {
                        cookieCrisp[si] = true;
                        break;
                    }
                }

                buffers[si] = ComputeDirect(s, sb, mappers[si], s.W, s.H, active, affecting, occluders, ambient, options, extra, alpine);
            }

            local.Texels += s.W * s.H;
            int d = Interlocked.Increment(ref done);
            if (options.Progress is not null && (d % 64 == 0 || d == count))
            {
                options.Progress(new BakeProgress("Calculating lighting", d, count));
            }

            return local;
        },
        local =>
        {
            lock (gate)
            {
                texelTotal += local.Texels;
                maxLights = Math.Max(maxLights, local.MaxLights);
                overLimit += local.Over;
            }
        });

        // Gather bounces (Bounced method): fetch the direct-lit colour from each texel's
        // hemisphere against the surface field, scale by the albedo approximation, add.
        // The Alpine chart bake shades the charts with the direct kernel only; the stock surfaces' indirect term is
        // carried onto them afterwards, so keep it apart (charts only — the stock path is untouched).
        float[][]? indirect = null;
        if (options.LightBounces > 0)
        {
            float[][]? direct = charts is null ? null : CopyBuffers(buffers);
            BounceGather(surfaces, buffers, mappers, widths, heights, po, options);
            if (direct is not null)
            {
                indirect = new float[count][];
                for (int si = 0; si < count; si++)
                {
                    if (buffers[si] is { } fin && direct[si] is { } dir)
                    {
                        var d = new float[fin.Length];
                        for (int i = 0; i < d.Length; i++)
                        {
                            d[i] = fin[i] - dir[i];
                        }

                        indirect[si] = d;
                    }
                }
            }
        }

        // Cross-surface lightmap seam blend across coplanar surfaces a portal split into
        // different rooms (Alpine -smoothlights, lightmap.cpp:81-110). Runs on the pre-encode
        // float buffers so the encoded atlas is continuous over the doorway seam. OFF unless the
        // author enables the Seam Blend method option (which sets CrossRoomBlend via WithMethod) —
        // like Alpine's opt-in -smoothlights. The RED-Classic default bake leaves it off, so the
        // stock-RED parity gates (which use default options) stay byte-identical.
        if (options.CrossRoomBlend)
        {
            stats.SeamTexelsBlended = CrossSurfaceBlend.Apply(surfaces, buffers, mappers, widths, heights);
        }

        // Encode + optional interior smoothing + gutter border replication.
        Parallel.For(0, count, po, si =>
        {
            if (buffers[si] is { } buf)
            {
                Surface s = surfaces[si].Surface;
                EncodeSurface(pages[s.LightmapIndex], s, buf, options, cookieCrisp[si]);
            }
        });

        if (options.Quality)
        {
            foreach (SurfaceBake sb in surfaces)
            {
                ReplicateBorder(pages[sb.Surface.LightmapIndex], sb.Surface);
            }
        }

        stats.Texels = texelTotal;
        stats.MaxLightsOnAnyFace = maxLights;
        stats.OverLimitFaces = overLimit;

        if (charts is not null)
        {
            var context = new ShadeContext(active, bounds, occluders, ambient, options, extra, po) { Alpine = alpine };
            charts.Capture(context, surfaces, pages, cookieCrisp, indirect);
            if (charts is Alpine.AlpineChartBakeJob job)
            {
                stats.Pipeline = job.Settings.Pipeline;
                if (job.Finished)
                {
                    stats.AlpineLightmaps = job.Result;
                    stats.AlpineSkipped = job.Skipped;
                }
            }
        }

        foreach (EngineLight l in active)
        {
            if (l.Type == EngineLightType.Directional)
            {
                stats.DirectionalLights++;
            }
        }

        stats.LiquidEmitters = alpine?.Liquid.Count ?? 0;

        return stats;
    }

    private static float[][] CopyBuffers(float[][] buffers)
    {
        var copy = new float[buffers.Length][];
        for (int i = 0; i < buffers.Length; i++)
        {
            copy[i] = buffers[i] is { } b ? (float[])b.Clone() : null!;
        }

        return copy;
    }

    /// <summary>The shared state of one bake that a chart pass shades against (the stock pass's own lights and occluders).</summary>
    internal sealed class ShadeContext
    {
        public ShadeContext(
            List<EngineLight> active, Aabb[] bounds, OccluderBvh occluders, AmbientField ambient, LightingOptions options,
            DirectionalOccluders? extra, ParallelOptions parallel)
        {
            Active = active;
            Bounds = bounds;
            Occluders = occluders;
            Ambient = ambient;
            Options = options;
            Extra = extra;
            Parallel = parallel;
        }

        public List<EngineLight> Active { get; }

        public Aabb[] Bounds { get; }

        public OccluderBvh Occluders { get; }

        public AmbientField Ambient { get; }

        public LightingOptions Options { get; }

        public DirectionalOccluders? Extra { get; }

        public ParallelOptions Parallel { get; }

        /// <summary>The Alpine fixed pipeline's occluders, rules and liquid emitters (null: the stock shadow model).</summary>
        public Alpine.AlpineShading? Alpine { get; init; }

        /// <summary>The lights whose range reaches <paramref name="sb"/> (the stock pass's culling).</summary>
        public List<int> Affecting(SurfaceBake sb)
        {
            Surface s = sb.Surface;
            Aabb sbox = sb.MoverTransform is MoverTransform mtb ? WorldAabb(s.BoundingBox, mtb) : s.BoundingBox;
            var affecting = new List<int>();
            for (int i = 0; i < Active.Count; i++)
            {
                if (Overlaps(Bounds[i], sbox))
                {
                    affecting.Add(i);
                }
            }

            return affecting;
        }

        /// <summary>
        /// Shades a <paramref name="w"/> × <paramref name="h"/> texel view of a surface placed by
        /// <paramref name="mapper"/> with exactly the stock pass's per-texel kernel; returns the float buffer.
        /// </summary>
        public float[] Shade(SurfaceBake sb, SurfaceTexelMapper mapper, int w, int h, List<int> affecting) =>
            ComputeDirect(sb.Surface, sb, mapper, w, h, Active, affecting, Occluders, Ambient, Options, Extra, Alpine);

        /// <summary>
        /// Lights arbitrary points with the same light model as a surface texel — Alpine RED's
        /// <c>lightmap_light_terrain_points</c> (editor_patch/lightmap.cpp:4721-4830): the ambient seed (the smallest
        /// custom-ambient room containing the point, else the level's) halved, every light whose range reaches the
        /// points' padded bounds, each shadow-casting light masked by the occluders from the point lifted
        /// <paramref name="lift"/> along its own normal, the kernel at the point's own normal as a smooth receiver
        /// (no face of the receiver is skipped), clamped at zero. Returns RGB floats per point.
        /// </summary>
        public float[] ShadePoints(IReadOnlyList<Vec3> pos, IReadOnlyList<Vec3> nrm, float lift, float footprint)
        {
            int count = pos.Count;
            var buf = new float[count * 3];
            if (count == 0)
            {
                return buf;
            }

            Vec3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = new(float.MinValue, float.MinValue, float.MinValue);
            foreach (Vec3 p in pos)
            {
                lo = Vec3Math.Min(lo, p);
                hi = Vec3Math.Max(hi, p);
            }

            float pad = MathF.Max(lift, 0.02f);
            var box = new Aabb(new Vec3(lo.X - pad, lo.Y - pad, lo.Z - pad), new Vec3(hi.X + pad, hi.Y + pad, hi.Z + pad));
            var affecting = new List<int>();
            for (int i = 0; i < Active.Count; i++)
            {
                if (Active[i].Type == EngineLightType.Directional || Overlaps(Bounds[i], box))
                {
                    affecting.Add(i);
                }
            }

            Alpine.AlpineOccluders? tree = Alpine?.Tree;
            bool doShadows = Alpine is not null ? Options.CastShadows && tree is { IsEmpty: false } : Options.CastShadows && !Occluders.IsEmpty;
            var skip = new uint[affecting.Count];
            var oneSide = new uint[affecting.Count];
            if (Alpine is not null)
            {
                for (int a = 0; a < affecting.Count; a++)
                {
                    (skip[a], oneSide[a]) = Ged.Core.Lighting.Alpine.AlpineOccluders.RayFlags(Active[affecting[a]], Alpine.Settings);
                }
            }

            (uint Skip, uint OneSide) liquidRays = Alpine is { Liquid.Count: > 0 }
                ? Ged.Core.Lighting.Alpine.AlpineOccluders.RayFlags(new EngineLight { Type = EngineLightType.Point }, Alpine.Settings)
                : default;
            const int noSurface = int.MinValue;
            System.Threading.Tasks.Parallel.For(0, count, Parallel, i =>
            {
                Vec3 p = pos[i], n = nrm[i];
                Vec3 amb = Ambient.At(p, -1);
                float br = amb.X * 0.5f, bg = amb.Y * 0.5f, bb = amb.Z * 0.5f;
                Vec3 origin = p.Add(n.Scale(lift));
                var plane = new RfPlane(n, -n.Dot(p));
                for (int a = 0; a < affecting.Count; a++)
                {
                    EngineLight light = Active[affecting[a]];
                    if (light.Type == EngineLightType.Directional)
                    {
                        float dw = Alpine is not null
                            ? AlpineDirectionalContribution(light, p, n, origin, n, tree, noSurface, footprint, skip[a], oneSide[a], Options.CastShadows)
                            : DirectionalContribution(light, p, n, origin, plane, Occluders, Extra, Options.CastShadows);
                        br += dw * light.Color.X;
                        bg += dw * light.Color.Y;
                        bb += dw * light.Color.Z;
                        continue;
                    }

                    float f = LightKernel.Factor(light, p, n, true);
                    if (f <= 0f)
                    {
                        continue;
                    }

                    if (light.Cookie is not null)
                    {
                        f *= CookieProjection.Mask(light, p);
                        if (f <= 0f)
                        {
                            continue;
                        }
                    }

                    float shadow = 1f;
                    if (doShadows && light.CastsShadows)
                    {
                        shadow = Alpine is not null
                            ? tree!.PointMask(light, origin, n, noSurface, footprint, skip[a], oneSide[a]) / 255f
                            : ShadowSample(Occluders, origin, light, plane);
                        if (shadow <= 0f)
                        {
                            continue;
                        }
                    }

                    float wt = f * shadow;
                    br += wt * light.Color.X;
                    bg += wt * light.Color.Y;
                    bb += wt * light.Color.Z;
                }

                if (Alpine is { Liquid.Count: > 0 })
                {
                    foreach (Alpine.AlpineLiquidEmitter em in Alpine.Liquid)
                    {
                        float fall = em.Falloff(p);
                        if (!(fall > 0f))
                        {
                            continue;
                        }

                        byte vis = Options.CastShadows
                            ? em.Visibility(doShadows ? tree : null, origin, n, noSurface, footprint, liquidRays.Skip, liquidRays.OneSide)
                            : (byte)255;
                        if (vis == 0)
                        {
                            continue;
                        }

                        float k = vis / 255f * fall * em.Surface.FormFactor(p, n);
                        br += em.Color.X * k;
                        bg += em.Color.Y * k;
                        bb += em.Color.Z * k;
                    }
                }

                buf[i * 3] = MathF.Max(br, 0f);
                buf[(i * 3) + 1] = MathF.Max(bg, 0f);
                buf[(i * 3) + 2] = MathF.Max(bb, 0f);
            });

            return buf;
        }
    }

    /// <summary>
    /// The stock pass's float→byte conversion of a <paramref name="w"/> × <paramref name="h"/> buffer (interior
    /// smoothing when the fragment is at least 9×9, then the overbright clamp), into a packed RGB8 array.
    /// </summary>
    internal static byte[] EncodeBuffer(float[] buf, int w, int h, LightingOptions options, bool cookieCrisp)
    {
        if (options.Quality && options.SmoothIterations > 0 && !cookieCrisp && w >= 9 && h >= 9)
        {
            SmoothBufferInterior(buf, w, h, options.SmoothIterations);
        }

        var px = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            LightEncoder.Encode(buf[i * 3], buf[(i * 3) + 1], buf[(i * 3) + 2], options.ProportionalClamp, px, i * 3);
        }

        return px;
    }

    /// <summary>
    /// One or two cosine-weighted gather bounces over the direct-lit surface field: each
    /// texel accumulates the mean incoming direct-lit colour from its hemisphere, scaled
    /// by the albedo approximation, added in place. Bounce 2 reads bounce 1's result.
    /// </summary>
    private static void BounceGather(
        IReadOnlyList<SurfaceBake> surfaces, float[][] buffers, SurfaceTexelMapper[] mappers,
        int[] widths, int[] heights, ParallelOptions po, LightingOptions options)
    {
        LitSurfaceField field = LitSurfaceField.Build(mappers, buffers, widths, heights);
        if (field.IsEmpty)
        {
            return;
        }

        int count = surfaces.Count;
        int n = Math.Max(1, options.BounceSamples);
        float albedo = options.BounceAlbedo;
        const float golden = 0.61803398875f;
        const float maxDist = 200f;

        for (int bounce = 0; bounce < options.LightBounces; bounce++)
        {
            options.Progress?.Invoke(new BakeProgress($"Bounce {bounce + 1}", 0, count));
            var add = new float[count][];
            Parallel.For(0, count, po, si =>
            {
                if (buffers[si] is not { } buf)
                {
                    return;
                }

                Surface s = surfaces[si].Surface;
                Vec3 nrm = s.Plane.Normal.Normalized();
                (Vec3 t, Vec3 b) = Sampling.Basis(nrm);
                SurfaceTexelMapper m = mappers[si];
                int w = widths[si], h = heights[si];
                var acc = new float[buf.Length];
                for (int row = 0; row < h; row++)
                {
                    for (int col = 0; col < w; col++)
                    {
                        Vec3 p = m.World(col, row);
                        Vec3 origin = p.Add(nrm.Scale(0.05f));
                        float ir = 0f, ig = 0f, ib = 0f;
                        for (int k = 0; k < n; k++)
                        {
                            float u1 = (k + 0.5f) / n;
                            float u2 = (k * golden) % 1f;
                            float rad = MathF.Sqrt(u1);
                            float phi = u2 * MathF.PI * 2f;
                            Vec3 dir = t.Scale(rad * MathF.Cos(phi))
                                .Add(b.Scale(rad * MathF.Sin(phi)))
                                .Add(nrm.Scale(MathF.Sqrt(MathF.Max(0f, 1f - u1))));
                            if (field.SampleColor(origin, dir.Normalized(), maxDist) is Vec3 c)
                            {
                                ir += c.X; ig += c.Y; ib += c.Z;
                            }
                        }

                        int o = ((row * w) + col) * 3;
                        acc[o] = ir / n * albedo;
                        acc[o + 1] = ig / n * albedo;
                        acc[o + 2] = ib / n * albedo;
                    }
                }

                add[si] = acc;
            });

            // Commit the bounce (so within one bounce every surface reads the same state).
            for (int si = 0; si < count; si++)
            {
                if (add[si] is { } a && buffers[si] is { } buf)
                {
                    for (int i = 0; i < buf.Length; i++)
                    {
                        buf[i] += a[i];
                    }
                }
            }
        }
    }

    /// <summary>Item 6 lever 1: a cookie ≥ this sharpness excludes its lit surfaces from the smoothing pass.</summary>
    private const float CookieCrispSmoothingThreshold = 0.8f;

    private static void EncodeSurface(Lightmap page, Surface s, float[] buf, LightingOptions options, bool cookieCrisp = false)
    {
        int w = s.W, h = s.H;
        if (options.Quality && options.SmoothIterations > 0 && !cookieCrisp && w >= 9 && h >= 9)
        {
            SmoothBufferInterior(buf, w, h, options.SmoothIterations);
        }

        byte[] px = page.Pixels;
        int stride = page.Width * 3;
        for (int row = 0; row < h; row++)
        {
            int py = s.Y + row;
            if (py >= page.Height)
            {
                break;
            }

            for (int col = 0; col < w; col++)
            {
                int pxx = s.X + col;
                if (pxx >= page.Width)
                {
                    break;
                }

                int bo = ((row * w) + col) * 3;
                int o = (py * stride) + (pxx * 3);
                LightEncoder.Encode(buf[bo], buf[bo + 1], buf[bo + 2], options.ProportionalClamp, px, o);
            }
        }
    }

    private static float[] ComputeDirect(
        Surface s, SurfaceBake sb, SurfaceTexelMapper mapper, int w, int h,
        List<EngineLight> lights, List<int> affecting,
        OccluderBvh occluders, AmbientField ambient, LightingOptions options, DirectionalOccluders? extra,
        Alpine.AlpineShading? alpine)
    {
        Vec3 flatNormal = s.Plane.Normal;
        bool smooth = s.ShouldSmooth != 0;

        // A MOVER surface is stored in LOCAL space; the texel loop maps local→world with this transform
        // before the light math (RED bakes movers against the world at their rest position). The plane
        // used for shadow self-rejection / AO is likewise the world-space plane of the local surface.
        MoverTransform? mover = sb.MoverTransform;
        RfPlane shadowPlane = s.Plane;
        if (mover is MoverTransform mt0)
        {
            Vec3 wn = mt0.Rotation.Transform(s.Plane.Normal).Normalized();
            shadowPlane = new RfPlane(wn, s.Plane.Offset - wn.Dot(mt0.Position));
        }

        // RED's should_smooth path (red-lighting-model.md §c): per-texel BARYCENTRIC
        // interpolation of smoothing-group-averaged vertex normals with raw N·L.
        // Always on for smooth surfaces (matte surfaces keep flat normal + wrap).
        // Movers bake flat (their smooth-face polygons are world-space; skip the local-space interp).
        bool interp = smooth && sb.SmoothFaces is { Count: > 0 } && mover is null;
        bool doShadows = options.CastShadows && !occluders.IsEmpty;

        // The Alpine fixed pipeline: shadow rays against its own occluder tree with each light's face-class rules, the
        // receiver's own surface skipped, mask bytes rounded as Alpine rounds them; liquid rooms glow
        // (editor_patch/lightmap.cpp:1876-2072, 2861-2960).
        Alpine.AlpineOccluders? alpineTree = alpine?.Tree;
        Vec3 alpineNs = shadowPlane.Normal;
        float footprint = 0f;
        uint[]? alpineSkip = null, alpineOneSide = null;
        List<Alpine.AlpineLiquidEmitter>? glows = null;
        (uint Skip, uint OneSide) liquidRays = default;
        if (alpine is not null)
        {
            doShadows = options.CastShadows && alpineTree is { IsEmpty: false };
            Vec3 t00 = mapper.World(0, 0);
            float ec = mapper.World(1, 0).Sub(t00).Length(), er = mapper.World(0, 1).Sub(t00).Length();
            footprint = MathF.Sqrt(ec * er);
            alpineSkip = new uint[affecting.Count];
            alpineOneSide = new uint[affecting.Count];
            for (int a = 0; a < affecting.Count; a++)
            {
                (alpineSkip[a], alpineOneSide[a]) = Alpine.AlpineOccluders.RayFlags(lights[affecting[a]], alpine.Settings);
            }

            if (alpine.Liquid.Count > 0)
            {
                Aabb wbox = mover is MoverTransform mtl ? WorldAabb(s.BoundingBox, mtl) : s.BoundingBox;
                foreach (Alpine.AlpineLiquidEmitter em in alpine.Liquid)
                {
                    if (Overlaps(em.Bounds, wbox))
                    {
                        (glows ??= new List<Alpine.AlpineLiquidEmitter>()).Add(em);
                    }
                }

                liquidRays = Alpine.AlpineOccluders.RayFlags(new EngineLight { Type = EngineLightType.Point }, alpine.Settings);
            }
        }

        var buf = new float[w * h * 3]; // per-surface float buffer (blended before clamp)

        Vec3 bbMin = s.BoundingBox.P1;
        Vec3 bbMax = s.BoundingBox.P2;

        // Corner Leak Fix (shadow): half-texel inset on the two kept (in-plane) axes. A fragment
        // min-clamp overhang texel is clamped onto the surface's bbox edge, which at a room-boundary
        // corner is the plane of the adjacent wall occluder — so its shadow-ray origin sits exactly
        // on that wall and the ray to a neighbouring room's light crosses the wall at t≈0 (missed),
        // leaking light through it. Nudging the shadow origin back into the surface interior on the
        // clamped axis moves it genuinely onto this surface's side, where the wall correctly occludes.
        int uAxis = s.UCoefficient, vAxis = s.VCoefficient;
        float insetU = 0f, insetV = 0f;
        if (options.CornerLeakFix)
        {
            Vec3 o00 = mapper.World(0, 0);
            insetU = w > 1 ? 0.5f * MathF.Abs(mapper.World(1, 0).Component(uAxis) - o00.Component(uAxis)) : 0f;
            insetV = h > 1 ? 0.5f * MathF.Abs(mapper.World(0, 1).Component(vAxis) - o00.Component(vAxis)) : 0f;
        }

        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                Vec3 rawP = mapper.World(col, row);
                Vec3 p = rawP;

                // Clamp into the surface bounds: fragments are min-clamped to 4
                // texels, so a sub-texel face's grid extends past the polygon; an
                // off-face position can start shadow rays inside adjacent solids
                // (blotchy dark rows). RED's fills never sample off the face.
                p = new Vec3(
                    Math.Clamp(p.X, bbMin.X, bbMax.X),
                    Math.Clamp(p.Y, bbMin.Y, bbMax.Y),
                    Math.Clamp(p.Z, bbMin.Z, bbMax.Z));

                // Smooth surfaces (RED's should_smooth path): barycentric per-texel
                // interpolation of the vertex NORMALS and the world POSITION over the
                // face polygons — a curved surface's planar texel mapping lands off the
                // actual polygons, so RED reconstructs both from the face the texel
                // falls in. Gutter texels (outside every face) fall back to the flat
                // normal at the clamped planar position.
                Vec3 n = flatNormal;
                Vec3 lift = flatNormal;
                if (interp && Interpolate(sb.SmoothFaces!, p, s.UCoefficient, s.VCoefficient, options.SmoothGutterNormals) is (Vec3 tn, Vec3 tp))
                {
                    n = tn;
                    lift = tn;
                    p = tp;
                }

                // Mover surface: lift the local texel position + normal into world for the light math.
                if (mover is MoverTransform mt)
                {
                    p = mt.Position.Add(mt.Rotation.Transform(p));
                    n = mt.Rotation.Transform(n).Normalized();
                    lift = mt.Rotation.Transform(lift).Normalized();
                }

                Vec3 amb = options.Quality ? ambient.At(p, s.RoomIndex, options.CornerLeakFix) : ambient.ForRoom(s.RoomIndex);

                // Feature 1 modifier: AO multiplies the AMBIENT term only (standard AO-on-
                // ambient). 1.0 (no darkening) when the modifier is off.
                float ao = options.AmbientOcclusion
                    ? AmbientOcclusion.Factor(occluders, p, n, options.AoSamples, options.AoRadius, shadowPlane)
                    : 1f;
                float br = amb.X * 0.5f * ao, bg = amb.Y * 0.5f * ao, bb = amb.Z * 0.5f * ao;

                Vec3 origin = p.Add(lift.Scale(0.02f)); // lift off the surface for shadow rays

                // Corner Leak Fix (shadow): a texel whose planar position overhung the bbox was
                // clamped onto a bbox face (== a room-boundary wall plane); push its shadow origin
                // back into the surface interior on that axis so the wall occludes correctly.
                if (options.CornerLeakFix)
                {
                    if (insetU > 0f)
                    {
                        float ru = rawP.Component(uAxis), lo = bbMin.Component(uAxis), hi = bbMax.Component(uAxis);
                        if (ru < lo)
                        {
                            origin = origin.WithComponent(uAxis, origin.Component(uAxis) + insetU);
                        }
                        else if (ru > hi)
                        {
                            origin = origin.WithComponent(uAxis, origin.Component(uAxis) - insetU);
                        }
                    }

                    if (insetV > 0f)
                    {
                        float rv = rawP.Component(vAxis), lo = bbMin.Component(vAxis), hi = bbMax.Component(vAxis);
                        if (rv < lo)
                        {
                            origin = origin.WithComponent(vAxis, origin.Component(vAxis) + insetV);
                        }
                        else if (rv > hi)
                        {
                            origin = origin.WithComponent(vAxis, origin.Component(vAxis) - insetV);
                        }
                    }
                }

                for (int a = 0; a < affecting.Count; a++)
                {
                    EngineLight light = lights[affecting[a]];
                    if (light.Type == EngineLightType.Directional)
                    {
                        // Alpine's sun / Directional Light objects: a type-1 light, deduplicated per face, so the
                        // detail-subroom double gather (LightMultiplier) does not apply (lightmap.cpp:2654-2683).
                        float dw = alpine is not null
                            ? AlpineDirectionalContribution(light, p, n, origin, alpineNs, alpineTree, sb.SurfaceIndex, footprint, alpineSkip![a], alpineOneSide![a], options.CastShadows)
                            : DirectionalContribution(light, p, n, origin, shadowPlane, occluders, extra, options.CastShadows);
                        br += dw * light.Color.X;
                        bg += dw * light.Color.Y;
                        bb += dw * light.Color.Z;
                        continue;
                    }

                    float f = LightKernel.Factor(light, p, n, smooth);
                    if (f <= 0f)
                    {
                        continue;
                    }

                    // Item 4 — light cookie (greyscale gobo): multiply the contribution by the
                    // projected mask at this texel (1 when the light has no cookie / is a tube).
                    if (light.Cookie is not null)
                    {
                        f *= CookieProjection.Mask(light, p);
                        if (f <= 0f)
                        {
                            continue;
                        }
                    }

                    float shadow = 1f;
                    if (doShadows && light.CastsShadows)
                    {
                        // Feature 1 modifier: N-sample area soft shadows replace the stock
                        // 2-sample penumbra when enabled.
                        shadow = options.SoftShadows
                            ? AreaShadow.Mask(occluders, origin, light.Position, options.SoftShadowRadius, options.SoftShadowSamples, shadowPlane)
                            : alpine is not null
                                ? alpineTree!.PointMask(light, origin, alpineNs, sb.SurfaceIndex, footprint, alpineSkip![a], alpineOneSide![a]) / 255f
                                : ShadowSample(occluders, origin, light, shadowPlane);
                        if (shadow <= 0f)
                        {
                            continue;
                        }
                    }

                    float wt = f * shadow * sb.LightMultiplier;
                    br += wt * light.Color.X;
                    bg += wt * light.Color.Y;
                    bb += wt * light.Color.Z;
                }

                // Liquid emission: visibility × falloff × the surface's form factor, once per liquid room
                // (lightmap.cpp:2941-2959); the stand-in light is deduplicated, so no subroom double gather.
                if (glows is not null)
                {
                    foreach (Alpine.AlpineLiquidEmitter em in glows)
                    {
                        float fall = em.Falloff(p);
                        if (!(fall > 0f))
                        {
                            continue;
                        }

                        byte vis = options.CastShadows
                            ? em.Visibility(doShadows ? alpineTree : null, origin, alpineNs, sb.SurfaceIndex, footprint, liquidRays.Skip, liquidRays.OneSide)
                            : (byte)255;
                        if (vis == 0)
                        {
                            continue;
                        }

                        float k = vis / 255f * fall * em.Surface.FormFactor(p, n);
                        br += em.Color.X * k;
                        bg += em.Color.Y * k;
                        bb += em.Color.Z * k;
                    }
                }

                int bo = ((row * w) + col) * 3;
                buf[bo] = br; buf[bo + 1] = bg; buf[bo + 2] = bb;
            }
        }

        return buf;
    }

    /// <summary>
    /// Separable 3-tap [0.25,0.5,0.25] blur of the fragment's INTERIOR texels only
    /// (rows/cols 2..dim-3, matching RED's interior-filter region); the outer 2-px
    /// ring keeps its plain per-texel values.
    /// </summary>
    private static void SmoothBufferInterior(float[] buf, int w, int h, int iterations)
    {
        var tmp = new float[buf.Length];
        for (int it = 0; it < iterations; it++)
        {
            Array.Copy(buf, tmp, buf.Length);

            // Horizontal over interior columns.
            for (int row = 2; row < h - 2; row++)
            {
                int baseIdx = row * w * 3;
                for (int col = 2; col < w - 2; col++)
                {
                    int oc = baseIdx + (col * 3), om = oc - 3, op = oc + 3;
                    for (int k = 0; k < 3; k++)
                    {
                        tmp[oc + k] = (0.25f * buf[om + k]) + (0.5f * buf[oc + k]) + (0.25f * buf[op + k]);
                    }
                }
            }

            // Vertical over interior rows.
            for (int col = 2; col < w - 2; col++)
            {
                for (int row = 2; row < h - 2; row++)
                {
                    int oc = ((row * w) + col) * 3, om = oc - (w * 3), op = oc + (w * 3);
                    for (int k = 0; k < 3; k++)
                    {
                        buf[oc + k] = (0.25f * tmp[om + k]) + (0.5f * tmp[oc + k]) + (0.25f * tmp[op + k]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Barycentric-interpolated vertex normal AND world position for a texel at
    /// planar-mapped position <paramref name="p"/>, found by projecting onto the
    /// surface's kept axes (<paramref name="uAxis"/>/<paramref name="vAxis"/>) and
    /// locating the fan triangle it lands in. The position is reconstructed from the
    /// triangle's 3D vertices — on a curved smooth surface the planar mapping is off
    /// the actual polygon, and RED reconstructs the on-face point the same way.
    /// Returns null when the texel is outside every face (a gutter texel) so the
    /// caller falls back to the flat normal at the planar position — UNLESS
    /// <paramref name="weldGutter"/> is set (Smooth Gutter Normals): then a gutter
    /// texel welds to the NEAREST face's interpolated normal (closest-point on the
    /// triangle in projected UV space), giving a continuous normal across the polygon
    /// boundary instead of the flat-normal discontinuity.
    /// </summary>
    private static (Vec3 Normal, Vec3 Position)? Interpolate(
        IReadOnlyList<SmoothFace> faces, Vec3 p, int uAxis, int vAxis, bool weldGutter = false)
    {
        float pu = p.Component(uAxis), pv = p.Component(vAxis);
        float bestDistSq = float.MaxValue;
        Vec3 bestN = default, bestPos = default;
        bool haveNearest = false;

        for (int fi = 0; fi < faces.Count; fi++)
        {
            SmoothFace f = faces[fi];
            Vec3[] pos = f.Positions;
            for (int i = 1; i < pos.Length - 1; i++)
            {
                float ax = pos[0].Component(uAxis), ay = pos[0].Component(vAxis);
                float bx = pos[i].Component(uAxis), by = pos[i].Component(vAxis);
                float cx = pos[i + 1].Component(uAxis), cy = pos[i + 1].Component(vAxis);

                float d = ((by - cy) * (ax - cx)) + ((cx - bx) * (ay - cy));
                if (MathF.Abs(d) < 1e-12f)
                {
                    continue;
                }

                float l1 = (((by - cy) * (pu - cx)) + ((cx - bx) * (pv - cy))) / d;
                float l2 = (((cy - ay) * (pu - cx)) + ((ax - cx) * (pv - cy))) / d;
                float l3 = 1f - l1 - l2;
                const float e = -0.02f;
                if (l1 >= e && l2 >= e && l3 >= e)
                {
                    Vec3 n = f.Normals[0].Scale(l1).Add(f.Normals[i].Scale(l2)).Add(f.Normals[i + 1].Scale(l3));
                    Vec3 nn = n.Normalized();
                    if (nn.LengthSquared() <= 1e-8f)
                    {
                        return null;
                    }

                    Vec3 world = pos[0].Scale(l1).Add(pos[i].Scale(l2)).Add(pos[i + 1].Scale(l3));
                    return (nn, world);
                }

                // Gutter weld: track the nearest triangle by the closest-point barycentric.
                if (weldGutter)
                {
                    ClosestBary(ax, ay, bx, by, cx, cy, pu, pv, out float c1, out float c2, out float c3);
                    float qu = (ax * c1) + (bx * c2) + (cx * c3);
                    float qv = (ay * c1) + (by * c2) + (cy * c3);
                    float du = qu - pu, dv = qv - pv;
                    float distSq = (du * du) + (dv * dv);
                    if (distSq < bestDistSq)
                    {
                        Vec3 n = f.Normals[0].Scale(c1).Add(f.Normals[i].Scale(c2)).Add(f.Normals[i + 1].Scale(c3));
                        Vec3 nn = n.Normalized();
                        if (nn.LengthSquared() > 1e-8f)
                        {
                            bestDistSq = distSq;
                            bestN = nn;
                            bestPos = pos[0].Scale(c1).Add(pos[i].Scale(c2)).Add(pos[i + 1].Scale(c3));
                            haveNearest = true;
                        }
                    }
                }
            }
        }

        return weldGutter && haveNearest ? (bestN, bestPos) : null;
    }

    /// <summary>
    /// Barycentric coords of the point in triangle (A,B,C) closest to (px,py) in 2D
    /// (Ericson, Real-Time Collision Detection §5.1.5) — used to weld a gutter texel to the
    /// nearest face. Returns coords that sum to 1 and are all in [0,1].
    /// </summary>
    private static void ClosestBary(
        float ax, float ay, float bx, float by, float cx, float cy,
        float px, float py, out float u, out float v, out float w)
    {
        float abx = bx - ax, aby = by - ay;
        float acx = cx - ax, acy = cy - ay;
        float apx = px - ax, apy = py - ay;
        float d1 = (abx * apx) + (aby * apy);
        float d2 = (acx * apx) + (acy * apy);
        if (d1 <= 0f && d2 <= 0f) { u = 1f; v = 0f; w = 0f; return; } // vertex A

        float bpx = px - bx, bpy = py - by;
        float d3 = (abx * bpx) + (aby * bpy);
        float d4 = (acx * bpx) + (acy * bpy);
        if (d3 >= 0f && d4 <= d3) { u = 0f; v = 1f; w = 0f; return; } // vertex B

        float vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float t = d1 / (d1 - d3);
            u = 1f - t; v = t; w = 0f; return; // edge AB
        }

        float cpx = px - cx, cpy = py - cy;
        float d5 = (abx * cpx) + (aby * cpy);
        float d6 = (acx * cpx) + (acy * cpy);
        if (d6 >= 0f && d5 <= d6) { u = 0f; v = 0f; w = 1f; return; } // vertex C

        float vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float t = d2 / (d2 - d6);
            u = 1f - t; v = 0f; w = t; return; // edge AC
        }

        float va = (d3 * d6) - (d5 * d4);
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            u = 0f; v = 1f - t; w = t; return; // edge BC
        }

        float denom = 1f / (va + vb + vc); // interior
        v = vb * denom; w = vc * denom; u = 1f - v - w;
    }

    /// <summary>
    /// One directional light's weight at a texel, before its premultiplied colour: RED's type-1 kernel
    /// <c>0.25 · N·L</c> with no attenuation and no wrap (RED.exe FUN_004894c0 case 1), times the bounded volume's
    /// weight (editor_patch/lightmap.cpp:2861-2932), times the shadow mask — Alpine RED's per-texel ray trace along
    /// the light's 1 or 9 fixed directions to 1e6 units or the volume exit (lightmap.cpp:1920-1983), the mask byte
    /// rounded as Alpine rounds it.
    /// </summary>
    private static float DirectionalContribution(
        in EngineLight light, Vec3 p, Vec3 n, Vec3 origin, RfPlane plane, OccluderBvh occluders,
        DirectionalOccluders? extra, bool castShadows)
    {
        Vec3[] dirs = light.ShadowDirections ?? new[] { light.Position };
        float f = 0.25f * n.Dot(dirs[0]);
        if (!(f > 0f))
        {
            return 0f;
        }

        if (light.Volume is { } volume)
        {
            float wv = volume.Weight(p);
            if (!(wv > 0f))
            {
                return 0f;
            }

            f *= wv;
        }

        if (!castShadows || !light.CastsShadows)
        {
            return f;
        }

        AlpineLightVolume? clip = light.OutsideCasts ? null : light.Volume;
        int lit = 0;
        for (int k = 0; k < dirs.Length; k++)
        {
            // Alpine clips at the volume exit along the world direction toward the light (lightmap.cpp:1946-1950).
            float tMax = clip is not null ? MathF.Min(1e6f, clip.RayExitDistance(origin, dirs[k])) : 1e6f;
            bool blocked = occluders.OccludedAlong(origin, dirs[k], tMax, plane)
                || (extra is not null && light.LiquidOccludes && extra.Liquid.OccludedAlong(origin, dirs[k], tMax, plane))
                || (extra is not null && !light.SkyPasses && extra.Sky.OccludedAlong(origin, dirs[k], tMax, plane));
            if (!blocked)
            {
                lit++;
            }
        }

        int taken = dirs.Length;
        int mask = ((lit * 255) + (taken / 2)) / taken;
        return mask == 0 ? 0f : f * (mask / 255f);
    }

    /// <summary>
    /// A directional light's weight with the Alpine fixed pipeline: the same kernel and volume weight as
    /// <see cref="DirectionalContribution"/>, the shadow from Alpine's occluder tree with the light's face-class rules
    /// (editor_patch/lightmap.cpp:1876-1954); without occluders the light is unshadowed (Alpine leaves the stock projector
    /// in charge of an empty tree, which shadows nothing).
    /// </summary>
    private static float AlpineDirectionalContribution(
        in EngineLight light, Vec3 p, Vec3 n, Vec3 origin, Vec3 ns, Alpine.AlpineOccluders? tree, int skipSurf, float footprint,
        uint skip, uint oneSide, bool castShadows)
    {
        Vec3[] dirs = light.ShadowDirections ?? new[] { light.Position };
        float f = 0.25f * n.Dot(dirs[0]);
        if (!(f > 0f))
        {
            return 0f;
        }

        if (light.Volume is { } volume)
        {
            float wv = volume.Weight(p);
            if (!(wv > 0f))
            {
                return 0f;
            }

            f *= wv;
        }

        if (!castShadows || !light.CastsShadows || tree is null || tree.IsEmpty)
        {
            return f;
        }

        int mask = tree.DirectionalMask(light, origin, ns, skipSurf, footprint, skip, oneSide);
        return mask == 0 ? 0f : f * (mask / 255f);
    }

    /// <summary>Shadow mask sample: 1 sample for point/spot, 2-sample penumbra (0/0.5/1) for tube/area lights.</summary>
    private static float ShadowSample(OccluderBvh occluders, Vec3 origin, in EngineLight light, RfPlane surfacePlane)
    {
        if (light.IsArea)
        {
            int lit = 0;
            if (!occluders.Occluded(origin, light.Position, surfacePlane))
            {
                lit++;
            }

            if (!occluders.Occluded(origin, light.Position2, surfacePlane))
            {
                lit++;
            }

            return lit * 0.5f;
        }

        return occluders.Occluded(origin, light.Position, surfacePlane) ? 0f : 1f;
    }

    private static void FillFragment(Lightmap page, Surface s, byte r, byte g, byte b)
    {
        byte[] px = page.Pixels;
        int stride = page.Width * 3;
        for (int row = 0; row < s.H; row++)
        {
            int py = s.Y + row;
            if (py >= page.Height)
            {
                break;
            }

            for (int col = 0; col < s.W; col++)
            {
                int pxx = s.X + col;
                if (pxx >= page.Width)
                {
                    break;
                }

                int o = (py * stride) + (pxx * 3);
                px[o] = r; px[o + 1] = g; px[o + 2] = b;
            }
        }
    }

    /// <summary>Copies the fragment's outer 1-px ring outward into the atlas gutter (bilinear-safe).</summary>
    private static void ReplicateBorder(Lightmap page, Surface s)
    {
        byte[] px = page.Pixels;
        int w = page.Width, h = page.Height, stride = w * 3;
        int x0 = s.X, y0 = s.Y, x1 = s.X + s.W - 1, y1 = s.Y + s.H - 1;
        if (s.W <= 0 || s.H <= 0)
        {
            return;
        }

        void Copy(int sx, int sy, int dx, int dy)
        {
            if (dx < 0 || dy < 0 || dx >= w || dy >= h || sx < 0 || sy < 0 || sx >= w || sy >= h)
            {
                return;
            }

            int so = (sy * stride) + (sx * 3);
            int doff = (dy * stride) + (dx * 3);
            px[doff] = px[so]; px[doff + 1] = px[so + 1]; px[doff + 2] = px[so + 2];
        }

        for (int x = x0; x <= x1; x++)
        {
            Copy(x, y0, x, y0 - 1); // top
            Copy(x, y1, x, y1 + 1); // bottom
        }

        for (int y = y0; y <= y1; y++)
        {
            Copy(x0, y, x0 - 1, y); // left
            Copy(x1, y, x1 + 1, y); // right
        }

        Copy(x0, y0, x0 - 1, y0 - 1);
        Copy(x1, y0, x1 + 1, y0 - 1);
        Copy(x0, y1, x0 - 1, y1 + 1);
        Copy(x1, y1, x1 + 1, y1 + 1);
    }

    private static bool Overlaps(Aabb a, Aabb b) =>
        a.P1.X <= b.P2.X && a.P2.X >= b.P1.X &&
        a.P1.Y <= b.P2.Y && a.P2.Y >= b.P1.Y &&
        a.P1.Z <= b.P2.Z && a.P2.Z >= b.P1.Z;

    /// <summary>World-space AABB of a local AABB under a mover's rigid transform (all 8 corners mapped).</summary>
    private static Aabb WorldAabb(Aabb local, MoverTransform mt)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vec3(
                (i & 1) == 0 ? local.P1.X : local.P2.X,
                (i & 2) == 0 ? local.P1.Y : local.P2.Y,
                (i & 4) == 0 ? local.P1.Z : local.P2.Z);
            Vec3 w = mt.Position.Add(mt.Rotation.Transform(corner));
            mn = Vec3Math.Min(mn, w);
            mx = Vec3Math.Max(mx, w);
        }

        return new Aabb(mn, mx);
    }
}
