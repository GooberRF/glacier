using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Analysis.Recreation;
using Ged.Core.Assets;

namespace Ged.Core.Scripting;

// The ref global's theme readers: what a reference shot's surfaces are made of (match_textures) and how it is lit
// (shot_lighting), for carrying a shot's look into a traced build with kit.dress_trace.
public sealed partial class ScriptRef
{
    [ScriptDoc("Ranks library textures against a crop of a shot (a wall, floor, trim) by Lab colour and pattern; returns {matches = {{name, score}}, best, path} and writes a contact sheet.",
        Example = "local m = ref.match_textures{shot = \"shot1.png\", crop = {400, 300, 160, 120}, n = 8, out = \"wall_matches.png\"} print(m.best)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefMatchTextures))]
    public Dictionary<string, object?> MatchTextures(ScriptArgs args)
    {
        const string Api = "ref.match_textures";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefMatchTextures);
        RefImage img = Shot(Api, args);
        (int[] rect, Func<int, int, bool>? inside) = Region(Api, args, img);
        int n = args.Int(Api, "n", 8);
        if (n is < 1 or > 40)
        {
            throw new ScriptApiException($"{Api}: n must be 1 to 40 (got {n}).");
        }

        AssetVfs vfs = _ctx.Assets ?? throw new ScriptApiException($"{Api}: no texture library is mounted.", "Run with an install (glacier-cli --rf-dir, or the editor's game folder) so the stock textures can be compared.");
        bool stockOnly = args.Bool(Api, "stock", true);
        string? category = args.String(Api, "category");
        string? filter = args.String(Api, "filter");
        HashSet<string>? inCategory = null;
        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();
        if (stockOnly && category is null)
        {
            // RED's own texture browser categories (the maps*.txt lists): the level textures, not model skins.
            inCategory = new HashSet<string>(cats.Where(c => c.Name != TextureCategoryCatalog.AllName && c.Name != TextureCategoryCatalog.UncategorizedName && !c.Name.StartsWith("Custom", StringComparison.OrdinalIgnoreCase))
                .SelectMany(c => c.Files).Select(f => System.IO.Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase);
            if (inCategory.Count == 0)
            {
                inCategory = null;
            }
        }

        if (category is not null)
        {
            AssetCategory[] hit = cats.Where(c => c.Name.Contains(category, StringComparison.OrdinalIgnoreCase) && c.Name != TextureCategoryCatalog.AllName).ToArray();
            if (hit.Length == 0)
            {
                throw new ScriptApiException($"{Api}: no texture category matches '{category}'.", "Categories: " + string.Join(", ", cats.Select(c => c.Name).Take(40)));
            }

            inCategory = new HashSet<string>(hit.SelectMany(c => c.Files).Select(f => System.IO.Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase);
        }

        ShotMaterials.Library lib;
        float[] shot;
        List<ShotMaterials.Match> matches;
        using (_ctx.EnterHostOperation())
        {
            lib = ShotMaterials.LoadLibrary(vfs, AppPaths.CacheDirectory, _ctx.Cancellation);
            shot = ShotMaterials.Describe(img.Rgba, img.Width, img.Height, rect[0], rect[1], rect[2], rect[3], inside);
            matches = ShotMaterials.Rank(lib, shot, n, i =>
                (!stockOnly || lib.Stock[i])
                && (inCategory is null || inCategory.Contains(System.IO.Path.GetFileNameWithoutExtension(lib.Names[i])))
                && (filter is null || lib.Names[i].Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["matches"] = matches.Select(m => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = m.Name,
                ["score"] = m.Score,
                ["repeats"] = m.Repeats,
                ["mean_rgb"] = m.MeanRgb,
            }).ToArray(),
            ["best"] = matches.Count > 0 ? matches[0].Name : null,
            ["names"] = matches.Select(m => m.Name).ToArray(),
            ["crop"] = rect,
            ["library"] = lib.Names.Length,
            ["cached"] = lib.FromCache,
            ["scale_hint"] = "repeats = how many times the texture best fits across the crop: a crop spanning S metres wants S / repeats metres per repeat (f:planar_map(ppm) with ppm = texture px * repeats / S; RED's stock is 256 / 4 m).",
            ["read"] = "Higher score = closer in colour and pattern. Look at the contact sheet: pick by eye among the top few (lighting shifts colour; a trim or grate may rank under a plain panel).",
        };
        if (matches.Count == 0)
        {
            result["note"] = "No texture passed the filters (stock / category / filter).";
        }

        if (Out(Api, args, required: false) is { } o)
        {
            byte[] png;
            using (_ctx.EnterHostOperation())
            {
                png = ShotMaterials.ContactSheet(img, rect, inside, matches, name =>
                {
                    try
                    {
                        return vfs.LoadTexture(name)?.Primary;
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
                    {
                        return null;
                    }
                });
            }

            Write(Api, o, png);
            result["path"] = o;
        }

        return result;
    }

    [ScriptDoc("Estimates a shot's lighting: light colours from its highlights, ambient from its darkest quarter, fog from its top; returns light_color, intensity, ambient_color, fog_color, summary.",
        Example = "local L = ref.shot_lighting{shot = \"shot1.png\"} print(L.summary) kit.light_room{room = r, color = L.light_color}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefShotLighting))]
    public Dictionary<string, object?> ShotLighting(ScriptArgs args)
    {
        const string Api = "ref.shot_lighting";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefShotLighting);
        RefImage img = Shot(Api, args);
        int[]? rect = null;
        Func<int, int, bool>? inside = null;
        if (args.Has("crop") || args.Has("polygon"))
        {
            (rect, inside) = Region(Api, args, img);
        }

        Analysis.Recreation.ShotLighting.Result r;
        using (_ctx.EnterHostOperation())
        {
            r = Analysis.Recreation.ShotLighting.Estimate(img, rect, inside);
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["summary"] = r.Summary,
            ["brightness"] = r.Brightness,
            ["contrast"] = r.Contrast,
            ["tint"] = r.Tint,
            ["lights"] = r.Lights.Select(l => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = l.Name,
                ["color"] = l.Color,
                ["brightness"] = l.Brightness,
                ["share"] = l.Share,
            }).ToArray(),
            ["light_color"] = r.LightColor,
            ["accent_color"] = r.Lights.Count > 1 ? r.Lights[1].Color : null,
            ["intensity"] = r.Intensity,
            ["ambient"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["seen"] = r.AmbientSeen, ["level"] = r.AmbientLevel, ["name"] = r.AmbientName },
            ["ambient_color"] = r.AmbientColor,
            ["fog_color"] = r.FogColor,
            ["use"] = "light_color / intensity: kit.light_room color and the lights' on_intensity (kit.dress_trace lighting = this table); ambient_color: level.props ambient_color; fog_color: level.props fog_color (outdoor shots).",
        };
    }

    [ScriptDoc("Writes the reference shot and a render of the build side by side at the same height, labelled, plus each one's lighting summary; returns {path, shot, render}.",
        Example = "ref.side_by_side{shot = \"shot1.png\", render = \"mine1.png\", out = \"pair1.png\"}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefSideBySide))]
    public Dictionary<string, object?> SideBySide(ScriptArgs args)
    {
        const string Api = "ref.side_by_side";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefSideBySide);
        RefImage a = Load(Api, args.String(Api, "shot") ?? throw new ScriptApiException($"{Api}: shot (the reference picture) is required."));
        RefImage b = Load(Api, args.String(Api, "render") ?? throw new ScriptApiException($"{Api}: render (a render of the build, e.g. glacier-cli render --camera --look) is required."));
        string outPath = Out(Api, args, required: true)!;
        Analysis.Recreation.ShotLighting.Result la, lb;
        byte[] png;
        using (_ctx.EnterHostOperation())
        {
            la = Analysis.Recreation.ShotLighting.Estimate(a, null, null);
            lb = Analysis.Recreation.ShotLighting.Estimate(b, null, null);
            int h = Math.Clamp(args.Int(Api, "height", 360), 64, 1440);
            int wa = Math.Max(1, a.Width * h / a.Height), wb = Math.Max(1, b.Width * h / b.Height);
            const int Pad = 6, Label = 16;
            var c = new RefCanvas(wa + wb + (3 * Pad), h + (2 * Pad) + Label);
            Blit(c, a, Pad, Pad + Label, wa, h);
            Blit(c, b, (2 * Pad) + wa, Pad + Label, wb, h);
            c.Text(Pad, 4, "REFERENCE", 1, 255, 220, 60);
            c.Text((2 * Pad) + wa, 4, "BUILD", 1, 120, 220, 255);
            png = c.Png();
        }

        Write(Api, outPath, png);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = outPath,
            ["shot"] = la.Summary,
            ["render"] = lb.Summary,
            ["brightness"] = new[] { la.Brightness, lb.Brightness },
        };
    }

    private static void Blit(RefCanvas c, RefImage img, int ox, int oy, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (byte r, byte g, byte b) = img.At(x * img.Width / w, y * img.Height / h);
                c.Set(ox + x, oy + y, r, g, b);
            }
        }
    }

    // ---- plumbing -----------------------------------------------------------------------------------------------

    /// <summary>The shot to read: shot (or path), else the current reference picture. Never changes the current picture.</summary>
    private RefImage Shot(string api, ScriptArgs args)
    {
        if ((args.String(api, "shot") ?? args.String(api, "path")) is { } p)
        {
            return Load(api, p);
        }

        return S.Image ?? throw new ScriptApiException($"{api}: give shot = \"<picture>\" (a screenshot, mockup tile or render).");
    }

    /// <summary>The rectangle to read and an optional inside test: crop = {x, y, w, h} or polygon = {{x, y}, ...} pixels.</summary>
    private static (int[] Rect, Func<int, int, bool>? Inside) Region(string api, ScriptArgs args, RefImage img)
    {
        if (args.Has("crop") && args.Has("polygon"))
        {
            throw new ScriptApiException($"{api}: give crop or polygon, not both.");
        }

        if (args.Table(api, "polygon") is { } poly)
        {
            if (poly.Items.Count < 3)
            {
                throw new ScriptApiException($"{api}: polygon lists at least 3 picture pixels {{x, y}}.");
            }

            double[][] pts = poly.Items.Select((p, i) => p is ScriptArgs t ? Nums(api, $"polygon[{i + 1}]", t, 2) : throw new ScriptApiException($"{api}: polygon[{i + 1}] must be {{x, y}}.")).ToArray();
            int x0 = Math.Clamp((int)Math.Floor(pts.Min(p => p[0])), 0, img.Width - 1), y0 = Math.Clamp((int)Math.Floor(pts.Min(p => p[1])), 0, img.Height - 1);
            int x1 = Math.Clamp((int)Math.Ceiling(pts.Max(p => p[0])), 0, img.Width), y1 = Math.Clamp((int)Math.Ceiling(pts.Max(p => p[1])), 0, img.Height);
            if (x1 - x0 < 4 || y1 - y0 < 4)
            {
                throw new ScriptApiException($"{api}: the polygon must cover at least 4 x 4 pixels inside the {img.Width}x{img.Height} picture.");
            }

            return (new[] { x0, y0, x1 - x0, y1 - y0 }, (x, y) => InsidePoly(pts, x + 0.5, y + 0.5));
        }

        return (Rect(api, args, "crop", img) ?? new[] { 0, 0, img.Width, img.Height }, null);
    }

    private static bool InsidePoly(double[][] p, double x, double y)
    {
        bool c = false;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
        {
            if ((p[i][1] > y) != (p[j][1] > y) && x < ((p[j][0] - p[i][0]) * (y - p[i][1]) / (p[j][1] - p[i][1])) + p[i][0])
            {
                c = !c;
            }
        }

        return c;
    }
}

/// <summary>The option tables of ref.match_textures and ref.shot_lighting.</summary>
public static partial class ScriptOptionSets
{
    public static readonly ScriptOptionSet RefMatchTextures = new("ref.match_textures",
        new("shot", "string", "the current picture", "The screenshot, render or mockup to read (does not change ref's current picture)."),
        new("path", "string", "the current picture", "Same as shot."),
        new("crop", "{x, y, w, h}", "the whole picture", "The patch to match in source pixels: one surface (a wall panel, floor, ceiling, trim), as square-on as the shot allows."),
        new("polygon", "{{x, y}, ...}", "none", "Instead of crop: only the pixels inside this outline (a floor seen in perspective)."),
        new("n", "number", "8", "How many textures to return (1-40)."),
        new("stock", "boolean", "true", "Only textures every player has (a retail packfile carries them); false ranks the whole mounted library."),
        new("category", "string", "all", "Only textures in stock texture categories whose name contains this (the editor's texture browser categories)."),
        new("filter", "string", "none", "Only texture names containing this (e.g. \"mtl\", \"rck\", \"flr\")."),
        new("out", "string", "none", "Write the contact sheet .png: the crop beside the top n textures, ranked and scored."));

    public static readonly ScriptOptionSet RefSideBySide = new("ref.side_by_side",
        new("shot", "string", null, "Required: the reference picture."),
        new("render", "string", null, "Required: a render of the build from the matching camera (glacier-cli render --camera= --look= --hfov)."),
        new("out", "string", null, "Required: the .png to write."),
        new("height", "number", "360", "The height both pictures are scaled to (64-1440 px)."));

    public static readonly ScriptOptionSet RefShotLighting = new("ref.shot_lighting",
        new("shot", "string", "the current picture", "The screenshot or render to read (does not change ref's current picture)."),
        new("path", "string", "the current picture", "Same as shot."),
        new("crop", "{x, y, w, h}", "the whole picture", "Only this part of the shot (one area of a multi-area mockup)."),
        new("polygon", "{{x, y}, ...}", "none", "Instead of crop: only the pixels inside this outline."));
}
