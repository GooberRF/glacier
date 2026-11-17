using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// Bakes lighting directly into an already-compiled <see cref="Geometry"/> and its
/// lightmap pages — reused by the incremental-relight path (light-only edits) and
/// by the acceptance gates. Builds the occluder BVH, engine lights and ambient
/// field from the geometry, then runs the <see cref="Lightmapper"/> over its
/// surfaces. Full-bright surfaces are detected from the bound faces' flags.
/// </summary>
public static class LevelLighting
{
    /// <summary>
    /// Bakes surfaces of <paramref name="g"/> into <paramref name="pages"/> in place.
    /// When <paramref name="onlyRegion"/> is given, only surfaces whose bounds
    /// overlap it are re-baked (incremental relight for a light-only edit — pass
    /// the union of the changed light's old and new influence bounds).
    /// </summary>
    public static BakeStats BakeInto(
        Geometry g,
        IReadOnlyList<Lightmap> pages,
        IReadOnlyList<Light> lights,
        RfColor? levelAmbient,
        LightingOptions options,
        Aabb? onlyRegion = null)
    {
        Func<int, LightCookie?>? cookies = options.CookieResolver;
        Func<int, float>? sharpness = options.CookieSharpnessResolver;
        var el = new List<EngineLight>(lights.Count + options.DirectionalLights.Count);
        foreach (Light l in lights)
        {
            el.Add(EngineLight.FromModel(l, editorOnly: false, cookies?.Invoke(l.Uid), sharpness?.Invoke(l.Uid) ?? 1f));
        }

        el.AddRange(options.DirectionalLights);

        // Alpine's charts refine every surface of the solid, so a chart bake relights the whole level.
        if (options.AlpineCharts is not null)
        {
            onlyRegion = null;
        }

        // The occluder BVH is needed for shadows AND the AO modifier (feature 1).
        OccluderBvh occluders = options.CastShadows || options.AmbientOcclusion
            ? BuildOccluders(g)
            : OccluderBvh.Build(Array.Empty<(Vec3, Vec3, Vec3)>());

        Vec3 amb = levelAmbient is RfColor c
            ? new Vec3(c.R / 255f, c.G / 255f, c.B / 255f)
            : new Vec3(1f, 1f, 1f);
        var field = new AmbientField(amb, g.Rooms);

        bool[] fullBright = FullBrightSurfaces(g);
        List<SmoothFace>?[] smooth = BuildSmoothFaces(g, options.AngleWeightedNormals);

        var input = new List<SurfaceBake>(g.Surfaces.Count);
        for (int i = 0; i < g.Surfaces.Count; i++)
        {
            Surface s = g.Surfaces[i];
            if ((uint)s.LightmapIndex >= (uint)pages.Count)
            {
                // No stock lightmap page: an Alpine D3D11-only level stores every surface's index as -1
                // and omits the 0x1200 section (ALPINE-COMPAT-SPEC §1.5); nothing to bake into.
                continue;
            }

            if (onlyRegion is Aabb region && !Overlaps(s.BoundingBox, region))
            {
                continue; // incremental: skip surfaces the changed light can't reach
            }

            input.Add(new SurfaceBake(s, i < fullBright.Length && fullBright[i], smooth[i], surfaceIndex: i));
        }

        DirectionalOccluders? extra = options.DirectionalLights.Count > 0 ? DirectionalOccludersOf(g) : null;
        Alpine.AlpineChartBakeJob? job = options.AlpineCharts is { } settings ? new Alpine.AlpineChartBakeJob(g.Surfaces, settings, g) : null;
        Alpine.AlpineShading? alpine = options.AlpineCharts is { FixedPipeline: true } fixedSettings ? AlpineShadingOf(g, options, fixedSettings) : null;
        BakeStats stats = Lightmapper.Bake(input, pages, el, occluders, field, options, extra, job, alpine);
        stats.AlpineLog.AddRange(alpine?.Log ?? new List<string>());
        return stats;
    }

    /// <summary>
    /// The Alpine fixed pipeline's shading state over an already-compiled level (a relight): its faces as occluders —
    /// "No shadow cast" brushes resolved by face id and bounds — the mesh occluders, and the liquid emitters of its
    /// liquid rooms (editor_patch/lightmap.cpp:151-236, 1100-1257, 2152-2225).
    /// </summary>
    internal static Alpine.AlpineShading AlpineShadingOf(Geometry g, LightingOptions options, Alpine.AlpineLightmapSettings settings)
    {
        Alpine.AlpineOccluders? tree = null;
        if (options.CastShadows)
        {
            var faces = new List<Alpine.AlpineOccluderFace>();
            foreach (Alpine.AlpineOccluderFace f in Alpine.AlpineShading.FacesOf(g, options.NoShadowCast))
            {
                if (!string.IsNullOrEmpty(f.Texture))
                {
                    faces.Add(f);
                }
            }

            tree = Alpine.AlpineOccluders.Build(faces, settings.MeshesOcclude ? options.MeshOccluders : null, settings.AlphaTestedOcclusion, options.AlpineTextures);
        }

        var liquidFaces = new List<(int, IReadOnlyList<Vec3>, Vec3)>();
        foreach (Face f in g.Faces)
        {
            if (((FaceFlags)f.Flags & FaceFlags.LiquidSurface) != 0 && f.RoomIndex >= 0 && f.Vertices.Count >= 3)
            {
                liquidFaces.Add((f.RoomIndex, f.Vertices.ConvertAll(v => g.Vertices[v.Index]), f.Plane.Normal));
            }
        }

        var log = new List<string>();
        List<Alpine.AlpineLiquidEmitter> liquid = Alpine.AlpineShading.LiquidEmitters(options.LiquidEmission, g.Rooms, liquidFaces, log);
        var shading = new Alpine.AlpineShading(tree, settings, liquid);
        shading.Log.AddRange(log);
        return shading;
    }

    /// <summary>The liquid-surface and show-sky faces of <paramref name="g"/>, for the Alpine directional lights.</summary>
    internal static DirectionalOccluders DirectionalOccludersOf(Geometry g)
    {
        var faces = new List<(ushort, IReadOnlyList<Vec3>)>();
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3 || f.IsPortalFace)
            {
                continue;
            }

            var poly = new Vec3[f.Vertices.Count];
            for (int i = 0; i < poly.Length; i++)
            {
                poly[i] = g.Vertices[f.Vertices[i].Index];
            }

            faces.Add((f.Flags, poly));
        }

        return DirectionalOccluders.Build(faces);
    }

    /// <summary>Fresh zeroed pages matching an existing atlas's dimensions.</summary>
    public static List<Lightmap> FreshPages(IReadOnlyList<Lightmap> template)
    {
        var pages = new List<Lightmap>(template.Count);
        foreach (Lightmap t in template)
        {
            pages.Add(new Lightmap { Width = t.Width, Height = t.Height, Pixels = new byte[t.Pixels.Length] });
        }

        return pages;
    }

    /// <summary>Surfaces whose bound faces are full-bright (filled 128 grey, not lit).</summary>
    private static bool[] FullBrightSurfaces(Geometry g)
    {
        var full = new bool[g.Surfaces.Count];
        foreach (Face f in g.Faces)
        {
            int si = f.SurfaceIndex;
            if (si >= 0 && (si & 0xFFFF) != 0xFFFF && si < full.Length && ((FaceFlags)f.Flags & FaceFlags.FullBright) != 0)
            {
                full[si] = true;
            }
        }

        return full;
    }

    /// <summary>
    /// Per-surface smoothed faces for interpolated-normal lighting, matching RED's
    /// baker (FUN_004aded0): a vertex normal is the unweighted mean of the face's own
    /// plane normal plus every vertex-sharing smooth face's plane normal within 90°
    /// of the current face (<see cref="SmoothNormals.AverageAt"/>). Exact pool-index
    /// adjacency — no positional quantization.
    /// </summary>
    private static List<SmoothFace>?[] BuildSmoothFaces(Geometry g, bool angleWeighted = false)
    {
        // Pool vertex -> plane normals of the smoothing-capable faces using it.
        var acc = new Dictionary<int, List<Vec3>>();
        foreach (Face f in g.Faces)
        {
            if (f.SmoothingGroups == 0 || f.Texture < 0 || f.Vertices.Count < 3)
            {
                continue;
            }

            foreach (FaceVertex fv in f.Vertices)
            {
                if (!acc.TryGetValue(fv.Index, out List<Vec3>? list))
                {
                    list = new List<Vec3>(4);
                    acc[fv.Index] = list;
                }

                list.Add(f.Plane.Normal);
            }
        }

        var result = new List<SmoothFace>?[g.Surfaces.Count];
        foreach (Face f in g.Faces)
        {
            int si = f.SurfaceIndex;
            if (f.SmoothingGroups == 0 || si < 0 || (si & 0xFFFF) == 0xFFFF || si >= result.Length ||
                f.Vertices.Count < 3 || g.Surfaces[si].ShouldSmooth == 0)
            {
                continue;
            }

            int vc = f.Vertices.Count;
            var pos = new Vec3[vc];
            var nrm = new Vec3[vc];
            for (int i = 0; i < vc; i++)
            {
                pos[i] = g.Vertices[f.Vertices[i].Index];
                nrm[i] = SmoothNormals.AverageAt(f.Plane.Normal, acc.GetValueOrDefault(f.Vertices[i].Index), angleWeighted);
            }

            (result[si] ??= new List<SmoothFace>()).Add(new SmoothFace(pos, nrm));
        }

        return result;
    }

    private static bool Overlaps(Aabb a, Aabb b) =>
        a.P1.X <= b.P2.X && a.P2.X >= b.P1.X &&
        a.P1.Y <= b.P2.Y && a.P2.Y >= b.P1.Y &&
        a.P1.Z <= b.P2.Z && a.P2.Z >= b.P1.Z;

    private static OccluderBvh BuildOccluders(Geometry g)
    {
        var tris = new List<(Vec3, Vec3, Vec3)>(g.Faces.Count * 2);
        foreach (Face f in g.Faces)
        {
            if (f.Texture < 0 || f.Vertices.Count < 3)
            {
                continue;
            }

            // RED's shadow rasteriser skips liquid + alpha faces (flags & 0x44),
            // invisible (0x2000) and portal faces — glass panes don't cast shadows.
            var fl = (FaceFlags)f.Flags;
            if ((fl & (FaceFlags.ShowSky | FaceFlags.IsInvisible | FaceFlags.LiquidSurface | FaceFlags.HasAlpha)) != 0)
            {
                continue;
            }

            List<FaceVertex> v = f.Vertices;
            Vec3 a = g.Vertices[v[0].Index];
            for (int i = 1; i < v.Count - 1; i++)
            {
                tris.Add((a, g.Vertices[v[i].Index], g.Vertices[v[i + 1].Index]));
            }
        }

        return OccluderBvh.Build(tris);
    }
}
