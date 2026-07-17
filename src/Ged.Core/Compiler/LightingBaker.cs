using System;
using System.Collections.Generic;
using Ged.Core.Lighting;
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

            bakeInput.Add(new SurfaceBake(surf, surfaces.FullBright[i], sf));
        }

        BakeStats stats = Lightmapper.Bake(bakeInput, result.Lightmaps, lights, occluders, ambient, options.Lighting);

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
