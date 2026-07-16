using System;
using System.Collections.Generic;
using Ged.Core.Lighting;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Bakes lightmap surfaces for the level's MOVER brushes (elevators, doors, lifts) — exactly what RED does,
/// and the fix for Goober's "movers appear much darker than RED" report.
/// <para>
/// Movers are excluded from the static world fold (flagship 23A — they animate from the movers section), but
/// RED still bakes each mover's own surfaces into the shared lightmap atlas at the mover's REST position,
/// lit by the static world lights + ambient (verified on dmabrupt: every mover surface lands on page 27 of
/// RED's 28-page atlas, luminance 87–116 comparable to the static mean 92.7). GED regenerated the atlas from
/// the static geometry only and left the movers section untouched, so the mover surfaces kept RED's page
/// indices/coords into a completely different atlas → they sampled stale/dark texels.
/// </para>
/// <para>
/// This pass rebuilds each mover's surfaces (RED stores them in the mover's LOCAL space — verified), packs
/// them into fresh atlas pages appended after the static pages, and bakes them against the static world by
/// mapping each local texel to world at the mover's rest position (<see cref="MoverTransform"/>). Movers do
/// NOT occlude the static bake and are not occluders of each other (they are not in the static solid); they
/// only RECEIVE the world's direct + ambient lighting and its shadows. The mover brush geometry is mutated in
/// place (surfaces + per-vertex lightmap UVs); the caller re-serialises the movers section.
/// </para>
/// </summary>
internal static class MoverLighting
{
    /// <summary>
    /// Rebuilds + bakes the mover surfaces into <paramref name="result"/>'s atlas, mutating each mover
    /// brush's geometry. Returns the UIDs whose geometry changed (so the caller marks the section dirty).
    /// Requires <see cref="CompileOptions.BuildSurfaces"/>; bakes real light only when
    /// <see cref="CompileOptions.BakeLighting"/> is set (else the surfaces are ambient-seeded, like a
    /// no-bake static build).
    /// </summary>
    public static HashSet<int> Bake(IReadOnlyList<Brush> movers, CompiledLevel result, CompileOptions options)
    {
        var baked = new HashSet<int>();
        if (movers.Count == 0 || !options.BuildSurfaces)
        {
            return baked;
        }

        bool doBake = options.BakeLighting;

        // World lighting context (same as the static bake): level lights only, static-world occluders,
        // per-room ambient. The static occluder BVH is built ONCE and shared across movers — movers receive
        // the static world's shadows but are not themselves occluders (RED keeps the static and mover bakes
        // separate, so a mover never casts a baked ghost onto the world). Self-occlusion measured as a no-op
        // on dmabrupt's movers, so the shared static BVH keeps the parity while staying within the perf ceiling.
        List<EngineLight>? lights = null;
        AmbientField? ambient = null;
        OccluderBvh? occluders = null;
        if (doBake)
        {
            Func<int, LightCookie?>? cookies = options.Lighting.CookieResolver;
            Func<int, float>? sharpness = options.Lighting.CookieSharpnessResolver;
            lights = new List<EngineLight>(options.Lights.Count);
            foreach (Light l in options.Lights)
            {
                lights.Add(EngineLight.FromModel(l, editorOnly: false, cookies?.Invoke(l.Uid), sharpness?.Invoke(l.Uid) ?? 1f));
            }

            occluders = options.Lighting.CastShadows
                ? OccluderBvh.Build(BuildStaticTris(result.Geometry))
                : OccluderBvh.Build(System.Array.Empty<(Vec3, Vec3, Vec3)>());

            Vec3 levelAmbient = options.LevelAmbient is RfColor a
                ? new Vec3(a.R / 255f, a.G / 255f, a.B / 255f)
                : new Vec3(1f, 1f, 1f);
            ambient = new AmbientField(levelAmbient, result.Geometry.Rooms);
        }

        var emptyRooms = new RoomBuildResult();
        foreach (Brush mover in movers)
        {
            if (BakeOne(mover, result, options, doBake, lights, occluders, ambient, emptyRooms))
            {
                baked.Add(mover.Uid);
            }
        }

        return baked;
    }

    private static bool BakeOne(
        Brush mover, CompiledLevel result, CompileOptions options, bool doBake,
        List<EngineLight>? lights, OccluderBvh? occluders, AmbientField? ambient, RoomBuildResult emptyRooms)
    {
        Geometry geom = mover.Geometry;
        int faceCount = geom.Faces.Count;
        if (faceCount == 0)
        {
            return false;
        }

        // One LOCAL CsgFace per geometry face (null for portal / degenerate), keeping the index alignment so
        // the surface bindings can be written back to the exact geometry face.
        var perFace = new CsgFace?[faceCount];
        var eligible = new List<CsgFace>();
        for (int i = 0; i < faceCount; i++)
        {
            Face gf = geom.Faces[i];
            if (gf.Vertices.Count < 3 || gf.Texture < 0)
            {
                continue;
            }

            var verts = new List<CsgVertex>(gf.Vertices.Count);
            foreach (FaceVertex fv in gf.Vertices)
            {
                Vec3 local = fv.Index >= 0 && fv.Index < geom.Vertices.Count ? geom.Vertices[fv.Index] : default;
                verts.Add(new CsgVertex(local, fv.TextureCoords));
            }

            var cf = new CsgFace
            {
                Vertices = verts,
                Plane = CsgPlane.FromPolygon(verts),
                Texture = gf.Texture < geom.Textures.Count ? geom.Textures[gf.Texture] : string.Empty,
                Flags = gf.Flags,
                SmoothingGroups = gf.SmoothingGroups,
                FaceId = i,
                SourceBrushUid = mover.Uid,
                RoomIndex = -1,
            };
            perFace[i] = cf;
            eligible.Add(cf);
        }

        if (eligible.Count == 0)
        {
            return false;
        }

        // Build the mover's LOCAL surfaces into its own atlas pages (seeded ambient/grey), and — since
        // RED reuses one surface per coplanar co-planar group — group like the static build does.
        var scratch = new CompiledLevel();
        SurfaceBuildResult msr = new SurfaceBuilder(options.HighResLightmaps)
            .Build(eligible, emptyRooms, scratch, options.GroupSurfaces);
        if (msr.Surfaces.Count == 0)
        {
            return false;
        }

        if (doBake)
        {
            var transform = new MoverTransform(mover.Position, mover.Rotation);
            var bakeInput = new List<SurfaceBake>(msr.Surfaces.Count);
            for (int i = 0; i < msr.Surfaces.Count; i++)
            {
                bakeInput.Add(new SurfaceBake(msr.Surfaces[i], msr.FullBright[i], smoothFaces: null, lightMultiplier: 1f, transform));
            }

            Lightmapper.Bake(bakeInput, scratch.Lightmaps, lights!, occluders!, ambient!, options.Lighting);
        }

        // Splice the mover pages onto the shared atlas and re-index the surfaces to them.
        int pageOffset = result.Lightmaps.Count;
        result.Lightmaps.AddRange(scratch.Lightmaps);
        foreach (Surface s in msr.Surfaces)
        {
            s.LightmapIndex += pageOffset;
        }

        geom.Surfaces = msr.Surfaces;

        // Write the surface bindings + per-vertex lightmap UVs back onto the exact geometry faces.
        for (int i = 0; i < faceCount; i++)
        {
            Face gf = geom.Faces[i];
            CsgFace? cf = perFace[i];
            if (cf is { SurfaceIndex: >= 0, LightmapUvs: { } lm } && lm.Length == gf.Vertices.Count)
            {
                gf.SurfaceIndex = cf.SurfaceIndex;
                for (int v = 0; v < gf.Vertices.Count; v++)
                {
                    gf.Vertices[v].LightmapCoords = lm[v];
                }
            }
            else
            {
                gf.SurfaceIndex = -1; // no GED surface for this face → renders neutral (no stale lightmap)
            }
        }

        return true;
    }

    /// <summary>Triangulated static-world occluders for the mover shadow rays (portals / invisible / sky /
    /// liquid / alpha excluded — RED's shadow rasteriser skips them).</summary>
    private static List<(Vec3, Vec3, Vec3)> BuildStaticTris(Geometry g)
    {
        var tris = new List<(Vec3, Vec3, Vec3)>(g.Faces.Count * 2);
        foreach (Face f in g.Faces)
        {
            if (f.IsPortalFace || f.Vertices.Count < 3)
            {
                continue;
            }

            var flags = (FaceFlags)f.Flags;
            if ((flags & (FaceFlags.ShowSky | FaceFlags.IsInvisible | FaceFlags.LiquidSurface | FaceFlags.HasAlpha)) != 0)
            {
                continue;
            }

            List<FaceVertex> v = f.Vertices;
            for (int i = 1; i < v.Count - 1; i++)
            {
                if (v[0].Index >= 0 && v[i].Index >= 0 && v[i + 1].Index >= 0 &&
                    v[0].Index < g.Vertices.Count && v[i].Index < g.Vertices.Count && v[i + 1].Index < g.Vertices.Count)
                {
                    tris.Add((g.Vertices[v[0].Index], g.Vertices[v[i].Index], g.Vertices[v[i + 1].Index]));
                }
            }
        }

        return tris;
    }
}
