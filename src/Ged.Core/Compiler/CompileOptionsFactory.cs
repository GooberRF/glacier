using System;
using System.Collections.Generic;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Lighting;

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
            // High-Resolution Lightmaps raises the surface texel density so cookies resolve. It is a
            // surface-stage concern (sealed builds only; the preview skips surfaces).
            HighResLightmaps = settings.LightingMethod?.HighResLightmaps ?? false,
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
