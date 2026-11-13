using System;
using System.Collections.Generic;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>Per-face fitting of a whole brush (<see cref="UvAlignOps.FitLocal"/>).</summary>
public sealed partial class ScriptBrushHandle
{
    [ScriptDoc("Fits each textured face to whole tiles in the brush's own frame (a crate: one texture per face); axes \"u\"/\"v\" fit one axis and keep square texels. Returns the brush.",
        Example = "crate:fit_faces() band:fit_faces{axes = \"v\"}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.FitFaces))]
    public ScriptBrushHandle FitFaces(ScriptArgs? options = null)
    {
        const string Api = "brush:fit_faces";
        options ??= new ScriptArgs();
        options.RejectUnknown(Api, ScriptOptionSets.FitFaces);
        UvFitAxes axes = (options.String(Api, "axes") ?? "uv").Trim().ToLowerInvariant() switch
        {
            "uv" or "both" => UvFitAxes.Both,
            "u" => UvFitAxes.U,
            "v" => UvFitAxes.V,
            string other => throw new ScriptApiException($"{Api}: 'axes' must be \"uv\", \"u\" or \"v\" (got \"{other}\")."),
        };
        (double tilesU, double tilesV) = Tiles(Api, options);
        string facing = (options.String(Api, "facing") ?? "all").Trim().ToLowerInvariant();
        if (facing is not ("all" or "up" or "down" or "side"))
        {
            throw new ScriptApiException($"{Api}: 'facing' must be \"all\", \"up\", \"down\" or \"side\" (got \"{facing}\").");
        }

        Brush model = Model;
        var sizes = new Dictionary<int, (int W, int H)>();
        foreach (Face f in model.Geometry.Faces)
        {
            if (IsMappable(model.Geometry, f) && !sizes.ContainsKey(f.Texture))
            {
                sizes[f.Texture] = _ctx.TextureSize(model.Geometry.Textures[f.Texture]);
            }
        }

        EditExisting("Fit brush faces", b =>
        {
            float sign = BrushOrientation.OutwardSign(b);
            foreach (Face f in b.Geometry.Faces)
            {
                if (!IsMappable(b.Geometry, f) || !IsFacing(facing, WorldNormal(b, f, sign).Y))
                {
                    continue;
                }

                (int w, int h) = sizes.TryGetValue(f.Texture, out (int W, int H) s) ? s : _ctx.TextureSize(b.Geometry.Textures[f.Texture]);
                UvAlignOps.FitLocal(b.Geometry, f, axes, tilesU, tilesV, w, h);
            }
        });
        _ctx.Changes.Record("uv");
        return this;
    }

    private static bool IsFacing(string facing, float ny) => facing switch
    {
        "up" => ny >= HorizontalDot,
        "down" => ny <= -HorizontalDot,
        "side" => ny > -HorizontalDot && ny < HorizontalDot,
        _ => true,
    };

    /// <summary>Parses <c>tiles</c>: a number (both axes) or {u, v}; each between 1/64 and 64 repeats.</summary>
    private static (double U, double V) Tiles(string api, ScriptArgs options)
    {
        if (!options.Has("tiles"))
        {
            return (1, 1);
        }

        object? raw = options.Named["tiles"];
        (double u, double v) = raw switch
        {
            double d => (d, d),
            ScriptArgs t when t.Items.Count == 2 && t.Named.Count == 0 && t.Items[0] is double a && t.Items[1] is double b => (a, b),
            _ => throw new ScriptApiException($"{api}: 'tiles' must be a number or {{u, v}} (repeats across each face)."),
        };
        foreach (double x in new[] { u, v })
        {
            if (!double.IsFinite(x) || x < 1.0 / 64 || x > 64)
            {
                throw new ScriptApiException($"{api}: 'tiles' must be between 1/64 and 64 repeats (got {ScriptArgs.Format(x)}).");
            }
        }

        return (u, v);
    }
}

/// <summary>The option sets of the UV fitting and alignment operations.</summary>
public static partial class ScriptOptionSets
{
    public static readonly ScriptOptionSet FitFaces = new("brush:fit_faces",
        new("axes", "string", "\"uv\"", "\"uv\" fits both texture axes to the face; \"u\" or \"v\" fits that one and keeps square texels on the other."),
        new("tiles", "number | {u, v}", "1", "Texture repeats across each face along the fitted axes (1/64 to 64)."),
        new("facing", "string", "\"all\"", "Which faces: \"all\", or those whose world normal points \"up\", \"down\" or to the \"side\" (as brush:faces_facing)."));
}
