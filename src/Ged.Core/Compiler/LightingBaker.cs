using System;
using System.Collections.Generic;
using Ged.Core.Lighting;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Bridges the geometry compiler to the pure <see cref="Lightmapper"/>: assembles
/// the occluder BVH (solid world + detail faces; portal / invisible / sky / liquid
/// excluded), the engine lights (level lights only — editor-only lights are left
/// out of the game bake), and the ambient field, then runs the multithreaded bake
/// over the built surfaces and records the stats + the &gt;64-lights warning.
/// </summary>
internal static class LightingBaker
{
    public static BakeStats Bake(
        List<CsgFace> faces,
        SurfaceBuildResult surfaces,
        RoomBuildResult rooms,
        CompiledLevel result,
        CompileOptions options,
        BuildReport report)
    {
        // Engine lights: level lights only for the game bake (editor-only excluded). A light's
        // projection cookie (item 4), if any, is resolved by UID through the options resolver.
        Func<int, LightCookie?>? cookies = options.Lighting.CookieResolver;
        Func<int, float>? sharpness = options.Lighting.CookieSharpnessResolver;
        var lights = new List<EngineLight>(options.Lights.Count);
        foreach (Light l in options.Lights)
        {
            lights.Add(EngineLight.FromModel(l, editorOnly: false, cookies?.Invoke(l.Uid), sharpness?.Invoke(l.Uid) ?? 1f));
        }

        OccluderBvh occluders = options.Lighting.CastShadows
            ? BuildOccluders(faces, options)
            : OccluderBvh.Build(Array.Empty<(Vec3, Vec3, Vec3)>());

        // Hand the built occluder set to the mover pass so it shadows movers against the exact same
        // convex CsgFace geometry the static world was shadowed against — RED uses one world face list
        // for both bakes (docs/research/red-lighting-model.md; RED.exe FUN_004ae360).
        result.StaticOccluders = occluders;

        Vec3 levelAmbient = options.LevelAmbient is RfColor a
            ? new Vec3(a.R / 255f, a.G / 255f, a.B / 255f)
            : new Vec3(1f, 1f, 1f);
        var ambient = new AmbientField(levelAmbient, rooms.Rooms);

        // Smoothing-group-averaged vertex normals for smooth surfaces.
        Dictionary<CsgFaceKey, SmoothFace> smoothNormals = SmoothNormals.Build(faces, options.Lighting.AngleWeightedNormals);

        var bakeInput = new List<SurfaceBake>(surfaces.Surfaces.Count);
        for (int i = 0; i < surfaces.Surfaces.Count; i++)
        {
            Surface surf = surfaces.Surfaces[i];
            IReadOnlyList<SmoothFace>? sf = null;
            if (surf.ShouldSmooth != 0 && i < surfaces.SurfaceFaces.Count)
            {
                var list = new List<SmoothFace>();
                foreach (CsgFace cf in surfaces.SurfaceFaces[i])
                {
                    if (smoothNormals.TryGetValue(new CsgFaceKey(cf), out SmoothFace? face))
                    {
                        list.Add(face);
                    }
                }

                if (list.Count > 0)
                {
                    sf = list;
                }
            }

            bakeInput.Add(new SurfaceBake(surf, surfaces.FullBright[i], sf, surfaceIndex: i));
        }

        // Alpine's directional lights (the sun, Directional Light objects) and its surface charts, when the level asks
        // for them (both empty / null on a stock bake, which then runs exactly as before).
        lights.AddRange(options.Lighting.DirectionalLights);
        DirectionalOccluders? extra = options.Lighting.DirectionalLights.Count > 0
            ? DirectionalOccludersOf(faces)
            : null;
        result.DirectionalOccluders = extra;
        // The chart layout waits for the movers when the build lights any (their charts pack with the surfaces'), and for
        // the terrain build mapping when it compiles terrain (only a built terrain gets a chart).
        AlpineChartBakeJob? job = options.Lighting.AlpineCharts is { } chartSettings
            ? new AlpineChartBakeJob(surfaces.Surfaces, chartSettings) { DeferFinish = options.Movers.Count > 0 || options.TerrainBrushUids.Count > 0 }
            : null;
        result.AlpineChartJob = job;

        // The Alpine fixed pipeline (Legacy lighting off) shades against its own occluder tree and adds liquid emission.
        AlpineShading? alpine = options.Lighting.AlpineCharts is { FixedPipeline: true } fixedSettings
            ? AlpineShadingOf(faces, rooms, options, fixedSettings, report)
            : null;
        result.AlpineShading = alpine;

        BakeStats stats = Lightmapper.Bake(bakeInput, result.Lightmaps, lights, occluders, ambient, options.Lighting, extra, job, alpine);
        result.BakeStats = stats;
        if (job is { Finished: true })
        {
            FinishAlpineCharts(result, report);
        }

        if (options.Lighting.WarnStockLightLimit && stats.OverLimitFaces > 0)
        {
            report.Add(
                BuildSeverity.Warning,
                $"{stats.OverLimitFaces} surface(s) lit by more than 64 lights " +
                $"(max {stats.MaxLightsOnAnyFace}); exceeds the stock per-face limit " +
                "(computed anyway — Alpine raises the cap).");
        }

        return stats;
    }

    /// <summary>
    /// Completes the build's Alpine chart bake (after the movers were lit, when it waited for them), records the result
    /// on the bake stats and binds it to the assembled geometry; logs its lines into <paramref name="report"/>. A no-op
    /// without a chart bake.
    /// </summary>
    public static void FinishAlpineCharts(CompiledLevel result, BuildReport report)
    {
        if (result.AlpineChartJob is not { } job || result.AlpineChartsReported)
        {
            return;
        }

        // The terrains this build compiled and mapped get terrain charts (alpine_lightmaps.cpp:580-622: only a terrain
        // whose build is current).
        if (!job.Finished)
        {
            job.Terrains = result.Terrain is { } tb
                ? tb.Terrains.Where(e => e.Mapping is not null).Select(e => e.Source).ToList()
                : Array.Empty<AlpineTerrain>();
        }

        job.Finish();
        result.AlpineChartsReported = true;
        if (result.BakeStats is { } stats)
        {
            stats.AlpineLightmaps = job.Result;
            stats.AlpineSkipped = job.Skipped;
        }

        if (job.Result is { } bake)
        {
            bake.Geometry = result.Geometry.Surfaces.Count == job.Surfaces.Count ? result.Geometry : bake.Geometry;
        }

        foreach (string line in job.Result?.Log ?? (IEnumerable<string>)Array.Empty<string>())
        {
            report.Add(BuildSeverity.Info, "Alpine lightmaps: " + line);
        }

        if (job.Skipped is { } skipped)
        {
            report.Add(BuildSeverity.Info, "Alpine lightmaps: " + skipped);
        }
    }

    /// <summary>
    /// The Alpine fixed pipeline's shading state for the static solid: the occluder tree over the compile's faces (the
    /// faces of "No shadow cast" brushes left out; mover rest geometry folded in when "Movers cast shadows" is on), the
    /// level's mesh occluders, and its liquid emitters (editor_patch/lightmap.cpp:1100-1257, 2152-2225).
    /// </summary>
    private static AlpineShading AlpineShadingOf(
        List<CsgFace> faces, RoomBuildResult rooms, CompileOptions options, AlpineLightmapSettings settings, BuildReport report)
    {
        LightingOptions lo = options.Lighting;
        var nsc = new HashSet<int>(settings.NoShadowCastBrushUids);
        AlpineOccluders? tree = null;
        if (lo.CastShadows)
        {
            var input = new List<AlpineOccluderFace>(faces.Count);
            foreach (CsgFace f in faces)
            {
                if (string.IsNullOrEmpty(f.Texture) || f.Vertices.Count < 3)
                {
                    continue;
                }

                var poly = new Vec3[f.Vertices.Count];
                var uvs = new Uv[f.Vertices.Count];
                for (int i = 0; i < poly.Length; i++)
                {
                    poly[i] = f.Vertices[i].Position;
                    uvs[i] = f.Vertices[i].Uv;
                }

                input.Add(new AlpineOccluderFace(poly, uvs, f.Flags, f.Texture, f.SurfaceIndex, f.Plane.Normal, f.IsPortal,
                    nsc.Contains(f.SourceBrushUid) ? f.SourceBrushUid : -1));
            }

            if (lo.MoverShadows)
            {
                foreach (Brush m in options.Movers)
                {
                    input.AddRange(MoverLighting.MoverOccluderFaces(m, nsc, surfaceIds: false));
                }
            }

            tree = AlpineOccluders.Build(input, settings.MeshesOcclude ? lo.MeshOccluders : null, settings.AlphaTestedOcclusion, lo.AlpineTextures);
            ReportOccluders(tree, settings, options, report);
        }

        var log = new List<string>();
        var liquidFaces = new List<(int, IReadOnlyList<Vec3>, Vec3)>();
        foreach (CsgFace f in faces)
        {
            if (((FaceFlags)f.Flags & FaceFlags.LiquidSurface) != 0 && f.RoomIndex >= 0 && f.Vertices.Count >= 3)
            {
                liquidFaces.Add((f.RoomIndex, f.Vertices.ConvertAll(v => v.Position), f.Plane.Normal));
            }
        }

        List<AlpineLiquidEmitter> liquid = AlpineShading.LiquidEmitters(lo.LiquidEmission, rooms.Rooms, liquidFaces, log);
        foreach (string line in log)
        {
            report.Add(line.Contains(" but ", StringComparison.Ordinal) ? BuildSeverity.Warning : BuildSeverity.Info, "Lighting: " + line);
        }

        return new AlpineShading(tree, settings, liquid);
    }

    /// <summary>The bake log's occluder lines: what "No shadow cast" left out, and flags that resolved nothing (lightmap.cpp:1806-1843).</summary>
    private static void ReportOccluders(AlpineOccluders tree, AlpineLightmapSettings settings, CompileOptions options, BuildReport report)
    {
        if (tree.AlphaTestedTriangles > 0)
        {
            report.Add(BuildSeverity.Info, $"Lighting: {tree.AlphaTestedTriangles} triangles block light by their texture's alpha.");
        }

        if (settings.MeshesOcclude && options.Lighting.MeshOccluders.Count > 0)
        {
            report.Add(BuildSeverity.Info, $"Lighting: {options.Lighting.MeshOccluders.Count} mesh triangles block light.");
        }

        if (settings.NoShadowCastBrushUids.Count == 0)
        {
            return;
        }

        int total = 0;
        foreach (int n in tree.NoShadowCastDropped.Values)
        {
            total += n;
        }

        report.Add(BuildSeverity.Info,
            $"Lighting: {tree.NoShadowCastDropped.Count} of {settings.NoShadowCastBrushUids.Count} \"No shadow cast\" brushes resolved, {total} occluder faces left out.");
    }

    /// <summary>The liquid-surface and show-sky faces of the compile, for the Alpine directional lights.</summary>
    private static DirectionalOccluders DirectionalOccludersOf(List<CsgFace> faces)
    {
        var list = new List<(ushort, IReadOnlyList<Vec3>)>(faces.Count);
        foreach (CsgFace f in faces)
        {
            if (f.IsPortal || f.Vertices.Count < 3)
            {
                continue;
            }

            var poly = new Vec3[f.Vertices.Count];
            for (int i = 0; i < poly.Length; i++)
            {
                poly[i] = f.Vertices[i].Position;
            }

            list.Add(((ushort)f.Flags, poly));
        }

        return DirectionalOccluders.Build(list);
    }

    /// <summary>Triangulates the level's solid occluder faces for the shadow BVH.</summary>
    private static OccluderBvh BuildOccluders(List<CsgFace> faces, CompileOptions options)
    {
        var tris = new List<(Vec3, Vec3, Vec3)>(faces.Count * 2);

        // "Movers cast shadows" (LightingOptions.MoverShadows) — fold each mover's rest geometry into the
        // shadow occluder set so movers shadow the static world, their peers, and themselves (the mover bake
        // reuses this same BVH via CompiledLevel.StaticOccluders). OFF is the RED-matching / byte-parity
        // default: RED excludes mover-group faces from occluders (RED.exe FUN_004ae360 → FUN_004bcc60, moving
        // group types 4/5/7) — a moving object cannot bake a fixed shadow — so a stock bake never adds them.
        if (options.Lighting.MoverShadows)
        {
            foreach (Brush m in options.Movers)
            {
                MoverLighting.AppendMoverWorldTris(m, tris);
            }
        }

        foreach (CsgFace f in faces)
        {
            if (f.IsPortal || string.IsNullOrEmpty(f.Texture) || f.Vertices.Count < 3)
            {
                continue;
            }

            // RED's shadow rasteriser skips liquid + alpha faces (flags & 0x44),
            // invisible (0x2000) and portal faces — glass panes don't cast shadows.
            var flags = (FaceFlags)f.Flags;
            if ((flags & (FaceFlags.ShowSky | FaceFlags.IsInvisible | FaceFlags.LiquidSurface | FaceFlags.HasAlpha)) != 0)
            {
                continue;
            }

            List<CsgVertex> v = f.Vertices;
            for (int i = 1; i < v.Count - 1; i++)
            {
                tris.Add((v[0].Position, v[i].Position, v[i + 1].Position));
            }
        }

        return OccluderBvh.Build(tris);
    }
}
