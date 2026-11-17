using System;
using System.Collections.Generic;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Lighting;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// The single place a <see cref="CompileOptions"/> is assembled for a level build. The editor's
/// build controller, script runs (<c>ops.build</c> / <c>ops.light</c> / <c>ops.check_holes</c>), and
/// any headless host all call it, so a build is identical no matter who starts it. The user's
/// choices come in as <see cref="BuildSettings"/>; everything document-dependent is derived here
/// from the level itself:
/// <list type="bullet">
/// <item><see cref="CompileOptions.Alpine"/> from the level version — drives geoable/breakable room
/// isolation and the recompute of the <c>alpine_level_properties</c> room-UID tables (without it
/// geomod silently stops working in-game).</item>
/// <item>The stock 64-lights-per-face warning, only for non-Alpine levels.</item>
/// <item>Texture-derived face traits (invisible / alpha / holes) from the mounted asset library.</item>
/// <item>Light cookies + cookie sharpness from the object-metadata chunk (<see cref="ResolveLightCookies"/>).</item>
/// </list>
/// </summary>
public static class CompileOptionsFactory
{
    /// <summary>
    /// Builds the compile options for <paramref name="rfl"/> from the user's
    /// <paramref name="settings"/> and the build <paramref name="request"/>. Light cookies are NOT
    /// resolved here (decoding cookie images is slow — callers resolve them on their worker thread
    /// via <see cref="ResolveLightCookies"/>); <see cref="CreateForDocument"/> does both.
    /// </summary>
    public static CompileOptions Create(
        RflFile rfl,
        BuildSettings settings,
        CompileRequest request,
        Func<string, TextureTraits?>? textureTraits = null)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);

        bool alpine = rfl.Context.IsAlpine;

        // High-resolution stock lightmaps come from the level's Alpine highres_lightmaps property, as in Alpine RED
        // (independent of Legacy lighting: editor_patch/lightmap.cpp:126-131, 3945-3993 — 256² pages, 254-texel
        // fragments, 4× density). Glacier's own High-Resolution Lightmaps method option applies only to a level with
        // Legacy lighting that does not set the property, so an Alpine-lighting level always bakes what it records.
        AlpineLightmapSettings lightmapSettings = AlpineLightmapSettings.FromLevel(rfl);
        bool levelHighRes = lightmapSettings.HighRes;
        bool methodHighRes = lightmapSettings.Pipeline == LightmapPipeline.Legacy && (settings.LightingMethod?.HighResLightmaps ?? false);
        var options = new CompileOptions
        {
            Alpine = alpine,
            // Geometry ▸ Build method: RED-authentic Shared BSP vs GED's Incremental construction.
            SharedBsp = settings.UseSharedBsp,
            // Geometry ▸ Repair holes when building: Alpine RED's build repair (on by default).
            AlpineRepair = settings.RepairBuildHoles,
            // A preview build skips the surface/atlas stage and the t-joint seal for speed.
            BuildSurfaces = request.Sealed,
            FixTJoints = request.Sealed,
            BakeLighting = request.BakeLighting,
            // A surface-stage concern (sealed builds only; the preview skips surfaces).
            HighResLightmaps = levelHighRes || methodHighRes,
            HighResFragmentMax = levelHighRes ? 254 : 255,
            Cancellation = request.Cancellation,
            TextureTraits = textureTraits,
            Progress = request.Progress,
        };

        options.Lighting.CastShadows = request.CastShadows;
        options.Lighting.WarnStockLightLimit = !alpine;
        options.Lighting.Cancellation = request.Cancellation;
        options.Lighting.Progress = request.BakeProgress;
        if (request.BakeLighting)
        {
            options.Lighting.WithMethod(request.ApplyLightingMethod ? settings.LightingMethod : null);

            // Alpine Faction's lighting, read from the level as Alpine RED reads it: its sun and Directional Light
            // objects are baked with either pipeline, and a sealed bake with the Alpine pipeline (the default — Legacy
            // lighting off) also produces the surface charts the level's Alpine lightmap chunk carries.
            options.Lighting.DirectionalLights = AlpineDirectionalLights.FromLevel(rfl);

            // The liquid emission and "No shadow cast" inputs need no assets; meshes and texture alpha come with
            // ResolveLightCookies / CreateForDocument, which run where the asset library is available.
            ResolveAlpineLighting(options.Lighting, rfl, vfs: null, warnings: null);
            if (request.Sealed)
            {
                options.Lighting.AlpineCharts = AlpineLightmapSettings.FromLevel(rfl);
            }
        }

        return options;
    }

    /// <summary>
    /// The one-call form for hosts that own a document + asset library (script runs, headless):
    /// <see cref="Create"/> with texture traits from <paramref name="traits"/> (or a fresh cache over
    /// <paramref name="vfs"/>), plus the light cookies resolved when the request bakes lighting.
    /// Unloadable cookies are reported into <paramref name="warnings"/> and baked without.
    /// </summary>
    public static CompileOptions CreateForDocument(
        EditorDocument doc,
        AssetVfs? vfs,
        BuildSettings settings,
        CompileRequest request,
        TextureTraitsCache? traits = null,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        traits ??= vfs is null ? null : new TextureTraitsCache(vfs);
        CompileOptions options = Create(doc.Rfl, settings, request, traits is null ? null : traits.Get);
        if (request.BakeLighting)
        {
            ResolveLightCookies(options.Lighting, doc, vfs, warnings);
        }

        return options;
    }

    /// <summary>
    /// Resolves each light's projection cookie (gobo) and its sharpness from the object-metadata
    /// chunk into <paramref name="lighting"/>, decoding cookie images through <paramref name="vfs"/>.
    /// A level with no cookies leaves both resolvers null (the bake does no cookie work). Missing /
    /// undecodable cookies are reported into <paramref name="warnings"/> and baked without.
    /// </summary>
    public static void ResolveLightCookies(
        LightingOptions lighting, EditorDocument doc, AssetVfs? vfs, ICollection<string>? warnings)
    {
        ArgumentNullException.ThrowIfNull(lighting);
        ArgumentNullException.ThrowIfNull(doc);
        lighting.CookieResolver = CookieResolverFor(doc, vfs, warnings);
        lighting.CookieSharpnessResolver = SharpnessResolverFor(doc);
        ResolveAlpineLighting(lighting, doc.Rfl, vfs, warnings);
    }

    /// <summary>
    /// The level's Alpine fixed-pipeline inputs a bake reads beyond the lights (every bake path calls this through
    /// <see cref="ResolveLightCookies"/>): the liquid emission records, the "No shadow cast" brush filter for a relight,
    /// texture alpha lookups for the occluder rules, and — when the level sets "meshes block light" — the Alpine mesh
    /// objects' LOD0 triangles in world space (editor_patch/lightmap_mesh_occluders.cpp:816-899). Meshes that cannot be
    /// loaded are reported into <paramref name="warnings"/> and left out.
    /// </summary>
    public static void ResolveAlpineLighting(LightingOptions lighting, RflFile rfl, AssetVfs? vfs, ICollection<string>? warnings)
    {
        ArgumentNullException.ThrowIfNull(lighting);
        ArgumentNullException.ThrowIfNull(rfl);
        AlpineLightmapSettings settings = AlpineLightmapSettings.FromLevel(rfl);
        if (!settings.FixedPipeline)
        {
            return;
        }

        rfl.ParseAllKnownSections();
        var records = new List<Model.AlpineLiquidEmission>();
        var brushes = new List<Model.Brush>();
        var meshes = new List<Model.AlpineMeshObject>();
        foreach (IO.Rfl.RflSection s in rfl.Sections)
        {
            switch (s.Content)
            {
                case IO.Rfl.Sections.AlpineLiquidEmissionSection le:
                    records.AddRange(le.Records);
                    break;
                case IO.Rfl.Sections.BrushesSection bs:
                    brushes.AddRange(bs.Brushes);
                    break;
                case IO.Rfl.Sections.AlpineMeshObjectsSection ms:
                    meshes.AddRange(ms.Meshes);
                    break;
            }
        }

        lighting.LiquidEmission = records;
        lighting.NoShadowCast = settings.NoShadowCastBrushUids.Count > 0 ? AlpineNoShadowCastFilter.Build(settings.NoShadowCastBrushUids, brushes) : null;
        if (vfs is not null)
        {
            var textures = new VfsTextureAlpha(vfs);
            lighting.AlpineTextures = textures;
            if (settings.MeshesOcclude)
            {
                lighting.MeshOccluders = MeshOccluders(meshes, vfs, warnings);
            }
        }
    }

    /// <summary>World-space LOD0 triangles of every Alpine mesh object not flagged "No shadow cast".</summary>
    internal static List<AlpineMeshOccluder> MeshOccluders(IEnumerable<Model.AlpineMeshObject> meshes, AssetVfs vfs, ICollection<string>? warnings)
    {
        var tris = new List<AlpineMeshOccluder>();
        foreach (Model.AlpineMeshObject m in meshes)
        {
            if (m.NoShadowCast != 0 || string.IsNullOrEmpty(m.MeshFilename))
            {
                continue;
            }

            IO.Mesh.V3dFile? file;
            try
            {
                file = vfs.LoadMesh(m.MeshFilename);
            }
            catch (Exception)
            {
                file = null;
            }

            if (file is null)
            {
                warnings?.Add($"Mesh '{m.MeshFilename}' (object {m.Uid}) could not be loaded: it casts no baked shadow.");
                continue;
            }

            float scale = m.DrawScale > 0f && float.IsFinite(m.DrawScale) ? m.DrawScale : 1f;
            Model.Vec3 World(Model.Vec3 local) => m.Position.Add(Model.Mat3Math.Transform(m.Orientation, local.Scale(scale)));
            foreach (IO.Mesh.V3dSubmesh sub in file.Submeshes)
            {
                if (sub.Lods.Count == 0)
                {
                    continue;
                }

                Model.Vec3 offset = file.ModelOffset(sub);
                IO.Mesh.V3dLod lod = sub.Lods[0];
                foreach (IO.Mesh.V3dBatch batch in lod.Batches)
                {
                    string texture = sub.ResolveBatchTexture(lod, batch);
                    if (batch.TextureIndex >= 0 && batch.TextureIndex < lod.Textures.Count)
                    {
                        byte slot = lod.Textures[batch.TextureIndex].Id;
                        foreach (Model.AlpineMeshTextureOverride o in m.TextureOverrides)
                        {
                            if (o.SlotId == slot && !string.IsNullOrEmpty(o.Filename))
                            {
                                texture = o.Filename;
                            }
                        }
                    }

                    int vc = batch.Positions.Length;
                    int n = Math.Min(batch.NumTriangles, batch.Triangles.Length);
                    for (int t = 0; t < n; t++)
                    {
                        IO.Mesh.V3dTriangle tri = batch.Triangles[t];
                        if (tri.I0 >= vc || tri.I1 >= vc || tri.I2 >= vc)
                        {
                            continue;
                        }

                        Model.Uv Uv(int i) => i < batch.TexCoords.Length ? batch.TexCoords[i] : default;
                        tris.Add(new AlpineMeshOccluder(
                            World(batch.Positions[tri.I0].Add(offset)), World(batch.Positions[tri.I1].Add(offset)), World(batch.Positions[tri.I2].Add(offset)),
                            Uv(tri.I0), Uv(tri.I1), Uv(tri.I2), texture, false, m.Uid));
                    }
                }
            }
        }

        return tris;
    }

    /// <summary>Texture alpha lookups over the asset library, decoded once per texture.</summary>
    private sealed class VfsTextureAlpha : IAlpineTextureAlpha
    {
        private readonly AssetVfs _vfs;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AlpineAlphaMask?> _masks = new(StringComparer.OrdinalIgnoreCase);

        public VfsTextureAlpha(AssetVfs vfs) => _vfs = vfs;

        public bool HasAlpha(string texture) => Mask(texture) is not null;

        public AlpineAlphaMask? Mask(string texture) => string.IsNullOrEmpty(texture) ? null : _masks.GetOrAdd(texture, Load);

        private AlpineAlphaMask? Load(string texture)
        {
            try
            {
                IO.Tex.DecodedTexture? decoded = _vfs.LoadTexture(texture);
                if (decoded is null)
                {
                    return null;
                }

                IO.Tex.TextureImage img = decoded.Primary;
                return AlpineAlphaMask.FromRgba(img.Width, img.Height, img.Pixels);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The light-UID → decoded cookie resolver for a bake, or null when the level uses no cookies.
    /// Missing / undecodable cookies are reported into <paramref name="warnings"/> and skipped.
    /// </summary>
    public static Func<int, LightCookie?>? CookieResolverFor(
        EditorDocument doc, AssetVfs? vfs, ICollection<string>? warnings)
    {
        var meta = new GedObjectMetadataService(doc);
        IReadOnlyDictionary<int, string> cookies = meta.AllCookies();
        if (cookies.Count == 0)
        {
            return null;
        }

        return LightCookies.BuildResolver(
            cookies,
            file => LoadCookieImage(vfs, file),
            file => warnings?.Add($"Light cookie '{file}' could not be loaded — baked without it."));
    }

    /// <summary>The light-UID → cookie sharpness resolver (1 when unset), or null when the level has none.</summary>
    public static Func<int, float>? SharpnessResolverFor(EditorDocument doc)
    {
        var meta = new GedObjectMetadataService(doc);
        IReadOnlyDictionary<int, float> map = meta.AllCookieSharpness();
        if (map.Count == 0)
        {
            return null;
        }

        return uid => map.TryGetValue(uid, out float s) ? s : 1f;
    }

    private static (int Width, int Height, byte[] Rgba)? LoadCookieImage(AssetVfs? vfs, string file)
    {
        if (vfs is null)
        {
            return null;
        }

        try
        {
            IO.Tex.DecodedTexture? decoded = vfs.LoadTexture(file);
            if (decoded is null)
            {
                return null;
            }

            IO.Tex.TextureImage img = decoded.Primary;
            return (img.Width, img.Height, img.Pixels);
        }
        catch
        {
            return null;
        }
    }
}
