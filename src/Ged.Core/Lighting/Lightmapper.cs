using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

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
        MoverTransform? moverTransform = null)
    {
        Surface = surface;
        FullBright = fullBright;
        SmoothFaces = smoothFaces;
        LightMultiplier = lightMultiplier;
        MoverTransform = moverTransform;
    }

    public Surface Surface { get; }

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
        LightingOptions options)
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

                buffers[si] = ComputeDirect(s, sb, mappers[si], active, affecting, occluders, ambient, options);
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
        if (options.LightBounces > 0)
        {
            BounceGather(surfaces, buffers, mappers, widths, heights, po, options);
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
        return stats;
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
        Surface s, SurfaceBake sb, SurfaceTexelMapper mapper,
        List<EngineLight> lights, List<int> affecting,
        OccluderBvh occluders, AmbientField ambient, LightingOptions options)
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

        int w = s.W, h = s.H;
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
