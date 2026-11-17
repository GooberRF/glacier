using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Ged.Core.Analysis.Recreation;
using Ged.Core.Editor;
using Ged.Core.Spatial;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>ref</c> global: recreating an area from an overhead reference picture (map, mockup, drawing). open a picture,
/// read exact pixel coordinates off a labelled grid (grid), map pixels to world metres (calibrate, to_world, to_px,
/// world_points), trace its playable area by colour (mask), and compare the level's walkable floor with that mask
/// (compare: numbers plus an overlay) while building with kit.trace. The picture, calibration and last mask are kept
/// per document for the session, so later runs reuse them; setting them again in each script is the reliable form.
/// Eye-level screenshots are matched with the MCP reference_* tools (a rendered camera beside the picture).
/// </summary>
[ScriptDoc("Recreate from an overhead reference picture: open, grid (labelled pixel grid), calibrate (pixels to metres), to_world / to_px / world_points, mask (trace by colour), compare (floor vs mask: IoU, wall error, worst regions, overlay), heights (decode a heights picture into floors, ramps, stairs and drops).", Since = 2)]
public sealed partial class ScriptRef
{
    private static readonly ConditionalWeakTable<EditorDocument, State> Sessions = new();

    private readonly ScriptContext _ctx;

    internal ScriptRef(ScriptContext ctx) => _ctx = ctx;

    private State S => Sessions.GetValue(_ctx.Document, _ => new State());

    [ScriptDoc("Registers a reference picture (png, jpg, bmp, tga, gif) as the current one and returns {path, width, height, calibrated}. A new picture clears the calibration and mask.",
        Example = "local r = ref.open(\"map.png\") print(r.width, r.height)", Since = 2)]
    public Dictionary<string, object?> Open(string path)
    {
        const string Api = "ref.open";
        RefImage img = Load(Api, path);
        State s = S;
        if (!string.Equals(s.Image?.Path, img.Path, StringComparison.OrdinalIgnoreCase))
        {
            s.Calibration = null;
            s.Mask = null;
        }

        s.Image = img;
        return Info(s);
    }

    [ScriptDoc("Writes the picture (or a zoomed crop) with a labelled pixel grid; overlay draws your trace (a kit.trace table or polygons) or \"level\" (built floor per height) on it. Returns {path, step, crop, zoom}.",
        Example = "ref.grid{crop = {600, 200, 300, 300}, zoom = 4, overlay = plan, out = \"grid_a.png\"}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefGrid))]
    public Dictionary<string, object?> Grid(ScriptArgs args)
    {
        const string Api = "ref.grid";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefGrid);
        RefImage img = Image(Api, args);
        int[]? crop = Rect(Api, args, "crop", img);
        double zoom = args.Number(Api, "zoom", 0);
        if (zoom is < 0 or > 32)
        {
            throw new ScriptApiException($"{Api}: zoom must be 0 (fit) to 32 (got {ScriptArgs.Format(zoom)}).");
        }

        int step = args.Int(Api, "step", 0);
        if (step is < 0 or > 5000)
        {
            throw new ScriptApiException($"{Api}: step must be 1 to 5000 pixels, or 0 for automatic.");
        }

        string outPath = Out(Api, args, required: true)!;
        List<RefShape>? overlay = null;
        if (args.Named.TryGetValue("overlay", out object? ov) && ov is not null)
        {
            if (ov is string level)
            {
                if (level != "level")
                {
                    throw new ScriptApiException($"{Api}: overlay must be \"level\" or a table of polygons / a kit.trace table (got \"{level}\").");
                }

                RefCalibration cal = Cal(Api);
                Model.Geometry geo = SpatialLevel.StoredGeometry(_ctx.Document.Rfl)
                    ?? throw new ScriptApiException($"{Api}: overlay = \"level\" needs compiled geometry.", "Run ops.build() first.");
                using (_ctx.EnterHostOperation())
                {
                    RecreationFloor floor = RecreationFloor.Measure(geo, TerrainGround.Of(_ctx.Document.Rfl), _ctx.Cancellation);
                    overlay = ReferenceShapes.LevelOutline(floor, cal, img.Width, img.Height, crop);
                }
            }
            else
            {
                overlay = Shapes(Api, "overlay", ov, args.String(Api, "overlay_units") ?? "px");
            }
        }

        ReferenceGrid.Result r = ReferenceGrid.Render(img, crop, zoom, step, S.Calibration, null, overlay);
        Write(Api, outPath, r.Png);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["overlay_shapes"] = overlay?.Count,
            ["path"] = outPath,
            ["width"] = r.Width,
            ["height"] = r.Height,
            ["step"] = r.Step,
            ["crop"] = r.Crop,
            ["zoom"] = Math.Round(r.Zoom, 4),
            ["read"] = "Labels are the SOURCE picture's pixels (not the zoomed image's). Read corners off the lines; zoom into a crop for detail.",
        };
    }

    [ScriptDoc("Sets the picture-to-world mapping: scale from bar = {x1, y1, x2, y2, metres} (scale bar or known length), ppm or mpp; origin px lands at world at = {x, z}; up = \"+z\" or rotation. Returns it.",
        Example = "ref.calibrate{bar = {100, 950, 300, 950, 20}, origin = {512, 512}, at = {0, 0}}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefCalibrate))]
    public Dictionary<string, object?> Calibrate(ScriptArgs args)
    {
        const string Api = "ref.calibrate";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefCalibrate);
        RefImage img = Image(Api, args);
        int given = (args.Has("bar") ? 1 : 0) + (args.Has("ppm") ? 1 : 0) + (args.Has("mpp") ? 1 : 0);
        if (given != 1)
        {
            throw new ScriptApiException($"{Api}: give exactly one scale: bar = {{x1, y1, x2, y2, metres}}, ppm (pixels per metre) or mpp (metres per pixel).",
                "A scale bar or any length you know (a 2.5 m door, a 4 m corridor) gives bar; pixels are the source picture's.");
        }

        double ppm;
        if (args.Table(Api, "bar") is { } bar)
        {
            double[] b = Nums(Api, "bar", bar, 5);
            double d = Math.Sqrt(((b[2] - b[0]) * (b[2] - b[0])) + ((b[3] - b[1]) * (b[3] - b[1])));
            if (d < 2 || b[4] <= 0)
            {
                throw new ScriptApiException($"{Api}: bar needs two points at least 2 px apart and a positive length in metres.");
            }

            ppm = d / b[4];
        }
        else
        {
            ppm = args.Has("ppm") ? args.Number(Api, "ppm", 0) : 1 / args.Number(Api, "mpp", 0);
        }

        if (!double.IsFinite(ppm) || ppm < 0.01 || ppm > 10000)
        {
            throw new ScriptApiException($"{Api}: the scale must be 0.01 to 10000 pixels per metre (got {ScriptArgs.Format(ppm)}).");
        }

        double[] origin = args.Table(Api, "origin") is { } o ? Nums(Api, "origin", o, 2) : new[] { img.Width / 2.0, img.Height / 2.0 };
        double[] at = args.Table(Api, "at") is { } a ? Nums(Api, "at", a, 2) : new[] { 0.0, 0.0 };
        if (args.Has("up") && args.Has("rotation"))
        {
            throw new ScriptApiException($"{Api}: give either up or rotation, not both.");
        }

        double heading = 0;
        if (args.String(Api, "up") is { } up)
        {
            heading = RefCalibration.HeadingOf(up) ?? throw new ScriptApiException($"{Api}: up must be \"+z\", \"+x\", \"-z\" or \"-x\" (got \"{up}\").", "The world direction the top of the picture points to; use rotation for any angle.");
        }
        else if (args.Has("rotation"))
        {
            heading = args.Number(Api, "rotation", 0);
        }

        S.Calibration = new RefCalibration(ppm, origin[0], origin[1], at[0], at[1], heading);
        Dictionary<string, object?> t = S.Calibration.ToTable();
        (double x0, double z0) = S.Calibration.ToWorld(0, 0);
        (double x1, double z1) = S.Calibration.ToWorld(img.Width, img.Height);
        t["picture_world"] = new[] { Math.Round(Math.Min(x0, x1), 2), Math.Round(Math.Min(z0, z1), 2), Math.Round(Math.Max(x0, x1), 2), Math.Round(Math.Max(z0, z1), 2) };
        return t;
    }

    [ScriptDoc("The world point {x, z} under picture pixel (x, y) through the calibration (pixel centres are at +0.5).", Example = "local p = ref.to_world(640, 380) print(p[1], p[2])", Since = 2)]
    public double[] ToWorld(double x, double y)
    {
        (double wx, double wz) = Cal("ref.to_world").ToWorld(x, y);
        return new[] { Math.Round(wx, 4), Math.Round(wz, 4) };
    }

    [ScriptDoc("The picture pixel {x, y} over world point (x, z).", Example = "local q = ref.to_px(12, -8)", Since = 2)]
    public double[] ToPx(double x, double z)
    {
        (double px, double py) = Cal("ref.to_px").ToPx(x, z);
        return new[] { Math.Round(px, 3), Math.Round(py, 3) };
    }

    [ScriptDoc("Converts a list of picture pixels {{x, y}, ...} to world points {{x, z}, ...} (for kit.trace polygons in metres).", Example = "local poly = ref.world_points({{600, 200}, {760, 200}, {760, 330}})", Since = 2)]
    public double[][] WorldPoints(ScriptArgs points)
    {
        const string Api = "ref.world_points";
        RefCalibration cal = Cal(Api);
        if (points is null || points.Items.Count == 0)
        {
            throw new ScriptApiException($"{Api}: a list of {{x, y}} pixels is required.");
        }

        return points.Items.Select((p, i) =>
        {
            double[] q = p is ScriptArgs t ? Nums(Api, $"point {i + 1}", t, 2) : throw new ScriptApiException($"{Api}: point {i + 1} must be {{x, y}}.");
            (double x, double z) = cal.ToWorld(q[0], q[1]);
            return new[] { Math.Round(x, 4), Math.Round(z, 4) };
        }).ToArray();
    }

    [ScriptDoc("Traces the playable area by colour (samples or colors; mode rgb or hsv) or from polygons; reports each colour's share; returns polygons with holes (px, world), or zone parts. Becomes the compare mask.",
        Example = "local m = ref.mask{samples = {{700, 400}}, mode = \"hsv\", connected = true, out = \"mask_check.png\"}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefMask))]
    public Dictionary<string, object?> Mask(ScriptArgs args)
    {
        const string Api = "ref.mask";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefMask);
        RefImage img = Image(Api, args);
        RefCalibration? cal = S.Calibration;
        var colors = new List<(byte, byte, byte)>();
        var sources = new List<double[]?>();
        if (args.Table(Api, "colors") is { } cs)
        {
            foreach (object? item in cs.Items)
            {
                double[] c = item is ScriptArgs t ? Nums(Api, "colors", t, 3) : throw new ScriptApiException($"{Api}: colors lists {{r, g, b}} (0-255).");
                colors.Add(((byte)Math.Clamp(c[0], 0, 255), (byte)Math.Clamp(c[1], 0, 255), (byte)Math.Clamp(c[2], 0, 255)));
                sources.Add(null);
            }
        }

        var seeds = new List<(int X, int Y)>();
        if (args.Table(Api, "samples") is { } ss)
        {
            foreach (object? item in ss.Items)
            {
                double[] p = item is ScriptArgs t ? Nums(Api, "samples", t, 2) : throw new ScriptApiException($"{Api}: samples lists picture pixels {{x, y}}.");
                int x = (int)p[0], y = (int)p[1];
                seeds.Add((x, y));
                int r = 0, g = 0, b = 0, n = 0;
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        (byte pr, byte pg, byte pb) = img.At(x + dx, y + dy);
                        r += pr;
                        g += pg;
                        b += pb;
                        n++;
                    }
                }

                colors.Add(((byte)(r / n), (byte)(g / n), (byte)(b / n)));
                sources.Add(new double[] { x, y });
            }
        }

        string mode = args.String(Api, "mode") ?? "rgb";
        if (mode is not ("rgb" or "hsv"))
        {
            throw new ScriptApiException($"{Api}: mode must be \"rgb\" or \"hsv\" (got \"{mode}\").");
        }

        var ranges = new List<HsvRange>();
        if (args.Table(Api, "ranges") is { } rs)
        {
            if (mode != "hsv")
            {
                throw new ScriptApiException($"{Api}: ranges are HSV boxes: add mode = \"hsv\".");
            }

            foreach (object? item in rs.Items)
            {
                ScriptArgs rt = item as ScriptArgs ?? throw new ScriptApiException($"{Api}: ranges lists {{hue = {{min, max}}, sat = {{min, max}}, val = {{min, max}}}}.");
                rt.RejectUnknown(Api + " ranges", "hue", "sat", "val");
                double[]? hue = rt.Table(Api, "hue") is { } ht ? Nums(Api, "ranges.hue", ht, 2) : null;
                double[] sat = rt.Table(Api, "sat") is { } st ? Nums(Api, "ranges.sat", st, 2) : new[] { 0.0, 1.0 };
                double[] val = rt.Table(Api, "val") is { } vt ? Nums(Api, "ranges.val", vt, 2) : new[] { 0.0, 1.0 };
                ranges.Add(new HsvRange(hue?[0], hue?[1], sat[0], sat[1], val[0], val[1]));
            }
        }

        bool fromPolys = args.Has("polygons");
        if (fromPolys && (colors.Count > 0 || ranges.Count > 0))
        {
            throw new ScriptApiException($"{Api}: give polygons (rasterised as they are) or colours (samples / colors / ranges), not both.");
        }

        if (!fromPolys && colors.Count == 0 && ranges.Count == 0)
        {
            throw new ScriptApiException($"{Api}: give colors = {{{{r, g, b}}, ...}}, samples = {{{{x, y}}, ...}} (pixels whose colour is playable), ranges (mode = \"hsv\") or polygons.",
                "Sample several spots of each floor colour; with invert = true list the rock / background colours instead.");
        }

        bool connected = args.Bool(Api, "connected", false);
        double simplify = args.Number(Api, "simplify", 0.5);
        double simplifyPx = cal is null ? Math.Max(0.5, args.Number(Api, "simplify_px", 2)) : simplify * cal.Ppm;
        bool invert = args.Bool(Api, "invert", false);
        if (connected && seeds.Count == 0)
        {
            throw new ScriptApiException($"{Api}: connected = true keeps the regions under the samples: give samples.");
        }

        string holesMode = args.String(Api, "holes") ?? "keep";
        if (holesMode is not ("keep" or "cut"))
        {
            throw new ScriptApiException($"{Api}: holes must be \"keep\" (outlines with holes) or \"cut\" (pieces without holes, for kit.trace) (got \"{holesMode}\").");
        }

        RefMask mask;
        var warnings = new List<string>();
        using (_ctx.EnterHostOperation())
        {
            if (fromPolys)
            {
                var m = new bool[img.Width * img.Height];
                foreach (RefShape s in Shapes(Api, "polygons", args.Named["polygons"], args.String(Api, "units") ?? "px"))
                {
                    ReferenceShapes.Fill(m, img.Width, img.Height, s, true);
                }

                mask = new RefMask(img.Width, img.Height, m);
            }
            else
            {
                var options = new RefMaskOptions
                {
                    Crop = Rect(Api, args, "crop", img),
                    Colors = colors,
                    Tolerance = args.Number(Api, "tolerance", 40),
                    Invert = invert,
                    Seeds = connected ? seeds : null,
                    Close = args.Int(Api, "close", 2),
                    Open = args.Int(Api, "open", 1),
                    MinArea = args.Int(Api, "min_area", 64),
                    FillHoles = args.Int(Api, "fill_holes", 64),
                    Hsv = mode == "hsv",
                    HueTol = args.Number(Api, "hue_tol", 15),
                    SatTol = args.Number(Api, "sat_tol", 0.2),
                    ValTol = args.Number(Api, "val_tol", 0.3),
                    Ranges = ranges,
                };
                mask = RefMask.Segment(img, options);
            }
        }

        // What each listed colour picked: the share of the window, and of its border (a background colour).
        var report = new List<object?>();
        for (int i = 0; i < mask.Matches.Length; i++)
        {
            double share = mask.WindowPixels > 0 ? (double)mask.Matches[i] / mask.WindowPixels : 0;
            double border = mask.BorderPixels > 0 ? (double)mask.BorderMatches[i] / mask.BorderPixels : 0;
            var e = new Dictionary<string, object?>(StringComparer.Ordinal) { ["fraction"] = Math.Round(share, 4), ["border_fraction"] = Math.Round(border, 4) };
            string what;
            if (i < colors.Count)
            {
                (byte r, byte g, byte b) = colors[i];
                (double h, double s, double v) = RefMask.ToHsv(r, g, b);
                e["color"] = new double[] { r, g, b };
                e["hsv"] = new[] { Math.Round(h), Math.Round(s, 2), Math.Round(v, 2) };
                e["at"] = sources[i];
                what = sources[i] is { } at ? $"sample {i + 1 - colors.Count + seeds.Count} at ({at[0]}, {at[1]}) = rgb {r},{g},{b}" : $"colour {i + 1} rgb {r},{g},{b}";
            }
            else
            {
                e["range"] = i - colors.Count + 1;
                what = $"range {i - colors.Count + 1}";
            }

            report.Add(e);
            if (!invert && share > 0.5)
            {
                warnings.Add($"{what} matches {Math.Round(share * 100)} % of the {(args.Has("crop") ? "crop" : "picture")}: it is not specific to the floor. Lower tolerance, use mode = \"hsv\", or sample a cleaner spot.");
            }

            if (!invert && border > 0.5)
            {
                warnings.Add($"{what} matches {Math.Round(border * 100)} % of the window's border: it looks like the background. Try invert = true with the background colour listed instead.");
            }
        }

        if (!fromPolys && !invert && mask.WindowPixels > 0 && mask.Count > 0.85 * mask.WindowPixels)
        {
            warnings.Add($"The mask covers {Math.Round(100.0 * mask.Count / mask.WindowPixels)} % of the window: almost everything was taken as floor.");
        }

        S.Mask = mask;
        double px2 = cal is null ? 0 : 1 / (cal.Ppm * cal.Ppm);
        int maxPolys = args.Int(Api, "max_polygons", 32);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pixels"] = mask.Count,
            ["area_m2"] = cal is null ? null : Math.Round(mask.Count * px2, 1),
            ["colors"] = colors.Select(c => new double[] { c.Item1, c.Item2, c.Item3 }).ToArray(),
            ["samples_report"] = report.ToArray(),
        };

        List<RefPolygon> drawn;
        if (args.Has("split") || holesMode == "cut")
        {
            (int[] zone, List<(string Name, Dictionary<string, object?> Extra)> zones, Dictionary<int, IReadOnlyList<(double X, double Y)>> snap) = Zones(Api, args, mask, colors.Count);
            List<(int Zone, RefPolygon Poly)> pieces;
            using (_ctx.EnterHostOperation())
            {
                pieces = mask.ZonePolygons(zone, simplifyPx, maxPolys, holesMode == "cut", snap);
            }

            var parts = new List<object?>();
            var regions = new List<object?>();
            var all = new List<object?>();
            for (int z = 0; z < zones.Count; z++)
            {
                var mine = pieces.Where(q => q.Zone == z).Select(q => q.Poly).ToList();
                if (mine.Count == 0)
                {
                    continue;
                }

                var polys = new List<object?>();
                for (int k = 0; k < mine.Count; k++)
                {
                    string name = mine.Count == 1 ? zones[z].Name : $"{zones[z].Name}_{k + 1}";
                    Dictionary<string, object?> d = PolyTable(mine[k], cal, px2);
                    d["name"] = name;
                    polys.Add(d);
                    all.Add(d);
                    var region = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = name, ["poly"] = d["px"] };
                    foreach ((string key, object? v) in zones[z].Extra)
                    {
                        region[key] = v;
                    }

                    regions.Add(region);
                }

                var part = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = zones[z].Name, ["polygons"] = polys.ToArray() };
                foreach ((string key, object? v) in zones[z].Extra)
                {
                    part[key] = v;
                }

                parts.Add(part);
            }

            result["parts"] = parts.ToArray();
            result["polygons"] = all.ToArray();
            result["regions"] = regions.ToArray();
            result["regions_note"] = "regions: ready for kit.trace{units = \"px\", regions = m.regions} (pieces share their seams exactly; add floor / kind per zone).";
            drawn = pieces.Select(q => q.Poly).ToList();
        }
        else
        {
            drawn = mask.Polygons(simplifyPx, maxPolys);
            result["polygons"] = drawn.Select(q => (object?)PolyTable(q, cal, px2)).ToArray();
            int withHoles = drawn.Count(q => q.Holes.Count > 0);
            if (withHoles > 0)
            {
                warnings.Add($"{withHoles} outline(s) have holes (polygons[i].holes_px / holes). kit.trace regions take no holes: use holes = \"cut\" for pieces without holes that share their cut edges.");
            }
        }

        if (warnings.Count > 0)
        {
            result["warnings"] = warnings.ToArray();
        }

        if (Out(Api, args, required: false) is { } o)
        {
            Write(Api, o, mask.Overlay(img, drawn));
            result["path"] = o;
        }

        if (args.String(Api, "mask_out") is { } mo)
        {
            string full = WritePath(Api, mo);
            Write(Api, full, mask.Png());
            result["mask_path"] = full;
        }

        if (cal is null)
        {
            result["note"] = "Not calibrated: polygons are in pixels only (ref.calibrate adds world points and areas).";
        }

        return result;
    }

    [ScriptDoc("Edits a mask: fill and erase polygons (px or world) on the last ref.mask or a mask .png; the result becomes the compare mask. Returns {pixels, area_m2, filled, erased, path}.",
        Example = "ref.mask_edit{fill = {{{10, 10}, {90, 10}, {90, 40}}}, erase = {pit}, out = \"mask_fixed.png\"}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefMaskEdit))]
    public Dictionary<string, object?> MaskEdit(ScriptArgs args)
    {
        const string Api = "ref.mask_edit";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefMaskEdit);
        RefImage img = Image(Api, args);
        RefMask mask = args.String(Api, "mask") is { } mp
            ? LoadMask(Api, mp, img)
            : (S.Mask ?? throw new ScriptApiException($"{Api}: no mask: run ref.mask{{...}} first or pass mask = \"<png>\"."));
        mask = mask.Copy();
        if (!args.Has("fill") && !args.Has("erase"))
        {
            throw new ScriptApiException($"{Api}: give fill and / or erase (lists of polygons).");
        }

        string units = args.String(Api, "units") ?? "px";
        int filled = 0, erased = 0;
        if (args.Has("fill"))
        {
            foreach (RefShape s in Shapes(Api, "fill", args.Named["fill"], units))
            {
                filled += ReferenceShapes.Fill(mask.M, mask.Width, mask.Height, s, true);
            }
        }

        if (args.Has("erase"))
        {
            foreach (RefShape s in Shapes(Api, "erase", args.Named["erase"], units))
            {
                erased += ReferenceShapes.Fill(mask.M, mask.Width, mask.Height, s, false);
            }
        }

        S.Mask = mask;
        RefCalibration? cal = S.Calibration;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pixels"] = mask.Count,
            ["area_m2"] = cal is null ? null : Math.Round(mask.Count / (cal.Ppm * cal.Ppm), 1),
            ["filled"] = filled,
            ["erased"] = erased,
        };
        if (Out(Api, args, required: false) is { } o)
        {
            Write(Api, o, mask.Png());
            result["path"] = o;
        }

        if (args.String(Api, "overlay") is { } ov)
        {
            string full = WritePath(Api, ov);
            Write(Api, full, mask.Overlay(img, mask.Polygons(cal is null ? 2 : 0.5 * cal.Ppm, 32)));
            result["overlay_path"] = full;
        }

        return result;
    }

    private RefMask LoadMask(string api, string maskPath, RefImage img)
    {
        string full = ReadPath(api, maskPath);
        try
        {
            return RefMask.Load(full, img.Width, img.Height);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new ScriptApiException($"{api}: could not read the mask '{Path.GetFileName(full)}': {ex.Message}", "A mask is a PNG the size of the picture: white = playable.");
        }
    }

    private static Dictionary<string, object?> PolyTable(RefPolygon p, RefCalibration? cal, double px2)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["px"] = p.Outer.Select(q => new[] { q.X, q.Y }).ToArray(),
            ["area_px"] = Math.Round(p.AreaPx, 1),
            ["holes_px"] = p.Holes.Select(hh => hh.Select(q => new[] { q.X, q.Y }).ToArray()).ToArray(),
            ["hole_count"] = p.Holes.Count,
        };
        if (cal is not null)
        {
            d["points"] = p.Outer.Select(q => W(cal, q)).ToArray();
            d["holes"] = p.Holes.Select(hh => hh.Select(q => W(cal, q)).ToArray()).ToArray();
            d["area_m2"] = Math.Round(p.AreaPx * px2, 2);
        }

        return d;
    }

    /// <summary>The zone of every pixel for ref.mask's split / holes = "cut": colour classes, listed zones, or one zone.</summary>
    private (int[] Zone, List<(string Name, Dictionary<string, object?> Extra)> Zones, Dictionary<int, IReadOnlyList<(double X, double Y)>> Snap) Zones(string api, ScriptArgs args, RefMask mask, int colorCount)
    {
        int n = mask.M.Length, w = mask.Width, h = mask.Height;
        var zones = new List<(string Name, Dictionary<string, object?> Extra)>();
        var snap = new Dictionary<int, IReadOnlyList<(double X, double Y)>>();
        var zone = new int[n];
        object? split = args.Named.TryGetValue("split", out object? sv) ? sv : null;
        if (split is null)
        {
            zones.Add(("floor", new Dictionary<string, object?>(StringComparer.Ordinal)));
            return (zone, zones, snap);
        }

        Array.Fill(zone, -1);
        if (split is string s)
        {
            if (s != "colors")
            {
                throw new ScriptApiException($"{api}: split must be \"colors\" or a list of zones {{name =, rect = {{x, y, w, h}} | poly = {{...}}, ...}} (got \"{s}\").");
            }

            int[] lab = mask.ClassLabels() ?? throw new ScriptApiException($"{api}: split = \"colors\" needs a colour mask (not invert = true, not polygons).");
            int classes = mask.Matches.Length;
            for (int c = 0; c < classes; c++)
            {
                zones.Add((c < colorCount ? $"color{c + 1}" : $"range{c - colorCount + 1}", new Dictionary<string, object?>(StringComparer.Ordinal)));
            }

            Array.Copy(lab, zone, n);
            return (zone, zones, snap);
        }

        ScriptArgs list = split as ScriptArgs ?? throw new ScriptApiException($"{api}: split must be \"colors\" or a list of zones.");
        foreach (object? item in list.Items)
        {
            ScriptArgs zt = item as ScriptArgs ?? throw new ScriptApiException($"{api}: each zone is {{name =, rect = {{x, y, w, h}} | poly = {{{{x, y}}, ...}}, ...any fields for the region}}.");
            int id = zones.Count;
            string name = zt.String(api, "name") ?? $"zone{id + 1}";
            var extra = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach ((string key, object? v) in zt.Named)
            {
                if (key is not ("name" or "rect" or "poly") && v is double or string or bool)
                {
                    extra[key] = v;
                }
                else if (key is not ("name" or "rect" or "poly") && v is ScriptArgs vt && vt.Items.All(x => x is double))
                {
                    extra[key] = vt.Items.Select(x => (double)x!).ToArray();
                }
            }

            zones.Add((name, extra));
            var m = new bool[n];
            if (zt.Table(api, "rect") is { } rt)
            {
                double[] r = Nums(api, "split.rect", rt, 4);
                ReferenceShapes.Fill(m, w, h, new RefShape(name, new[] { (r[0], r[1]), (r[0] + r[2], r[1]), (r[0] + r[2], r[1] + r[3]), (r[0], r[1] + r[3]) }, Array.Empty<IReadOnlyList<(double X, double Y)>>()), true);
            }
            else if (zt.Has("poly"))
            {
                RefShape shape = Shapes(api, "split.poly", new ScriptArgs(items: new object?[] { zt.Named["poly"] }), args.String(api, "units") ?? "px")[0];
                ReferenceShapes.Fill(m, w, h, shape, true);
                snap[id] = shape.Outer;
            }
            else
            {
                throw new ScriptApiException($"{api}: zone '{name}' needs rect = {{x, y, w, h}} or poly = {{{{x, y}}, ...}}.");
            }

            for (int p = 0; p < n; p++)
            {
                if (m[p] && zone[p] < 0)
                {
                    zone[p] = id;
                }
            }
        }

        int rest = 0;
        for (int p = 0; p < n; p++)
        {
            if (mask.M[p] && zone[p] < 0)
            {
                zone[p] = zones.Count;
                rest++;
            }
        }

        if (rest > 0)
        {
            zones.Add(("unzoned", new Dictionary<string, object?>(StringComparer.Ordinal)));
        }

        return (zone, zones, snap);
    }

    /// <summary>
    /// Polygons from a Lua value: a kit.trace table (regions [+ solids], its own units, default m) or a kit.trace result
    /// (regions by name with world points); or a list of regions {name, poly}, ref.mask polygons {px, holes_px}, world
    /// {points}, circles {circle = {x, y, r}} or plain point lists (in <paramref name="units"/>).
    /// </summary>
    private List<RefShape> Shapes(string api, string key, object? value, string units)
    {
        if (units is not ("px" or "m"))
        {
            throw new ScriptApiException($"{api}: units must be \"px\" or \"m\" (got \"{units}\").");
        }

        var shapes = new List<RefShape>();
        if (value is not ScriptArgs t)
        {
            throw new ScriptApiException($"{api}: {key} must be a table of polygons (or a kit.trace table).");
        }

        if (t.Named.TryGetValue("regions", out object? regs) && regs is ScriptArgs rt)
        {
            string u = t.String(api, "units") ?? "m";
            foreach (object? item in rt.Items)
            {
                shapes.Add(ShapeOf(api, key, item, u, "region"));
            }

            foreach ((string name, object? v) in rt.Named.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (v is ScriptArgs one)
                {
                    RefShape s = ShapeOf(api, key, one, "m", "region");
                    shapes.Add(s with { Name = name });
                }
            }

            if (t.Named.TryGetValue("solids", out object? sol) && sol is ScriptArgs st)
            {
                foreach (object? item in st.Items)
                {
                    shapes.Add(ShapeOf(api, key, item, u, "solid") with { Name = "solid" });
                }
            }

            return shapes;
        }

        if (t.Items.Count > 0 && t.Items[0] is ScriptArgs first && first.Items.Count == 2 && first.Items.All(x => x is double))
        {
            // A single point list.
            shapes.Add(ShapeOf(api, key, t, units, "polygon"));
            return shapes;
        }

        foreach (object? item in t.Items)
        {
            shapes.Add(ShapeOf(api, key, item, units, "polygon"));
        }

        if (shapes.Count == 0)
        {
            throw new ScriptApiException($"{api}: {key} holds no polygons.");
        }

        return shapes;
    }

    private RefShape ShapeOf(string api, string key, object? item, string units, string kind)
    {
        ScriptArgs t = item as ScriptArgs ?? throw new ScriptApiException($"{api}: {key}: each polygon is a table ({{name, poly}}, {{px}}, {{points}} or a list of points).");
        string? name = t.Named.TryGetValue("name", out object? nv) ? nv as string : null;
        List<(double X, double Y)> Ring(object? v, string u, string what)
        {
            ScriptArgs pts = v as ScriptArgs ?? throw new ScriptApiException($"{api}: {key}: {what} must be a list of points.");
            var ring = new List<(double X, double Y)>();
            foreach (object? q in pts.Items)
            {
                double[] xy = q is ScriptArgs qt && qt.Items.Count >= 2 && qt.Items[0] is double && qt.Items[1] is double
                    ? new[] { (double)qt.Items[0]!, (double)qt.Items[1]! }
                    : throw new ScriptApiException($"{api}: {key}: {what} must list points {{x, y}}.");
                if (u == "m")
                {
                    (double px, double py) = Cal(api).ToPx(xy[0], xy[1]);
                    ring.Add((px, py));
                }
                else
                {
                    ring.Add((xy[0], xy[1]));
                }
            }

            return ring.Count >= 3 ? ring : throw new ScriptApiException($"{api}: {key}: a polygon needs at least 3 points.");
        }

        List<IReadOnlyList<(double X, double Y)>> Holes(object? v, string u) =>
            v is ScriptArgs hs ? hs.Items.Select(hh => (IReadOnlyList<(double X, double Y)>)Ring(hh, u, "a hole")).ToList() : new List<IReadOnlyList<(double X, double Y)>>();

        if (t.Named.TryGetValue("px", out object? px) && px is not null)
        {
            return new RefShape(name, Ring(px, "px", "px"), Holes(t.Named.GetValueOrDefault("holes_px"), "px"), kind);
        }

        if (t.Named.TryGetValue("poly", out object? poly) && poly is not null)
        {
            return new RefShape(name, Ring(poly, units, "poly"), Holes(t.Named.GetValueOrDefault("holes"), units), kind);
        }

        if (t.Named.TryGetValue("points", out object? wp) && wp is not null)
        {
            return new RefShape(name, Ring(wp, "m", "points"), Holes(t.Named.GetValueOrDefault("holes"), "m"), kind);
        }

        if (t.Table(api, "circle") is { } ct)
        {
            double[] c = Nums(api, $"{key}.circle", ct, 3);
            if (units == "m")
            {
                RefCalibration cal = Cal(api);
                (double cx, double cy) = cal.ToPx(c[0], c[1]);
                return new RefShape(name, ReferenceShapes.Circle(cx, cy, c[2] * cal.Ppm), Array.Empty<IReadOnlyList<(double X, double Y)>>(), kind);
            }

            return new RefShape(name, ReferenceShapes.Circle(c[0], c[1], c[2]), Array.Empty<IReadOnlyList<(double X, double Y)>>(), kind);
        }

        return new RefShape(name, Ring(t, units, "the polygon"), Array.Empty<IReadOnlyList<(double X, double Y)>>(), kind);
    }

    [ScriptDoc("The level's walkable floor vs the mask: {score, iou, iou_tol, walls_mean_m, missing_m2, extra_m2, worst = {{kind, area_m2, box_world, center}}}; out writes an overlay.",
        Example = "local c = ref.compare{out = \"cmp.png\", build = \"sealed\"} print(c.score, c.worst[1].kind, c.worst[1].center[1])", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefCompare))]
    public Dictionary<string, object?> Compare(ScriptArgs? args = null)
    {
        const string Api = "ref.compare";
        args ??= new ScriptArgs();
        args.RejectUnknown(ScriptOptionSets.RefCompare);
        RefImage img = Image(Api, args);
        RefCalibration cal = Cal(Api);
        RefMask mask;
        if (args.String(Api, "mask") is { } maskPath)
        {
            string full = ReadPath(Api, maskPath);
            try
            {
                mask = RefMask.Load(full, img.Width, img.Height);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new ScriptApiException($"{Api}: could not read the mask '{Path.GetFileName(full)}': {ex.Message}", "A mask is a PNG the size of the picture: white = playable.");
            }
        }
        else
        {
            mask = S.Mask ?? throw new ScriptApiException($"{Api}: no mask: run ref.mask{{...}} first or pass mask = \"<png>\" (white = playable, the picture's size).");
        }

        // The level's floor, as the spatial queries see it (build = "sealed" builds first).
        GeometryFreshness fresh = FreshGeometry(Api, args);
        Model.Geometry geo = SpatialLevel.StoredGeometry(_ctx.Document.Rfl)
            ?? throw new ScriptApiException($"{Api}: the level has no compiled geometry to compare.", "Run ops.build() first, or pass build = \"sealed\".");
        double? yMin = null, yMax = null;
        if (args.Table(Api, "y") is { } yr)
        {
            double[] y = Nums(Api, "y", yr, 2);
            (yMin, yMax) = (Math.Min(y[0], y[1]), Math.Max(y[0], y[1]));
        }

        ReferenceCompare.Result r;
        using (_ctx.EnterHostOperation())
        {
            RecreationFloor floor = RecreationFloor.Measure(geo, TerrainGround.Of(_ctx.Document.Rfl), _ctx.Cancellation);
            r = ReferenceCompare.Run(floor, cal, img, mask, Rect(Api, args, "crop", img), args.Int(Api, "worst", 5), yMin, yMax);
        }

        Dictionary<string, object?> t = r.Table;
        t["geometry"] = fresh.State;
        if (fresh.Note is not null)
        {
            t["geometry_note"] = fresh.Note;
        }

        if (Out(Api, args, required: false) is { } o)
        {
            Write(Api, o, r.Png);
            t["path"] = o;
        }

        return t;
    }

    [ScriptDoc("The current picture, its size and calibration: {path, width, height, calibrated, calibration?, mask_pixels?}.", Example = "print(ref.info().calibrated)", Since = 2)]
    public Dictionary<string, object?> Info() => Info(S);

    // ---- plumbing ---------------------------------------------------------------------------------------------

    private static Dictionary<string, object?> Info(State s) => new(StringComparer.Ordinal)
    {
        ["path"] = s.Image?.Path,
        ["width"] = s.Image?.Width,
        ["height"] = s.Image?.Height,
        ["calibrated"] = s.Calibration is not null,
        ["calibration"] = s.Calibration?.ToTable(),
        ["mask_pixels"] = s.Mask?.Count,
    };

    private GeometryFreshness FreshGeometry(string api, ScriptArgs args)
    {
        if (args.String(api, "build") is { } build)
        {
            if (build == "sealed")
            {
                _ctx.Ops.Build();
            }
            else if (build != "none")
            {
                throw new ScriptApiException($"{api}: build must be \"none\" or \"sealed\" (got \"{build}\").");
            }
        }

        return _ctx.Spatial.CurrentFreshness();
    }

    private static ScriptArgs Need(string api, ScriptArgs? args) => args ?? throw new ScriptApiException($"{api}: a table argument is required.");

    private RefCalibration Cal(string api) => S.Calibration
        ?? throw new ScriptApiException($"{api}: the reference is not calibrated.", "Run ref.calibrate{bar = {x1, y1, x2, y2, metres}} (or ppm / mpp) after ref.open.");

    private RefImage Image(string api, ScriptArgs args)
    {
        if (args.String(api, "path") is { } p)
        {
            RefImage img = Load(api, p);
            State s = S;
            if (!string.Equals(s.Image?.Path, img.Path, StringComparison.OrdinalIgnoreCase))
            {
                s.Calibration = null;
                s.Mask = null;
            }

            s.Image = img;
            return img;
        }

        return S.Image ?? throw new ScriptApiException($"{api}: no reference picture: run ref.open(\"<picture>\") first or pass path.");
    }

    private RefImage Load(string api, string path)
    {
        string full = ReadPath(api, path);
        if (!File.Exists(full))
        {
            throw new ScriptApiException($"{api}: '{full}' was not found.", "Relative paths resolve against the level's folder.");
        }

        try
        {
            return RefImage.Load(full);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            throw new ScriptApiException($"{api}: could not read '{Path.GetFileName(full)}' as an image: {ex.Message}", "Use a PNG, JPEG, BMP, TGA or GIF picture.");
        }
    }

    private string ReadPath(string api, string path)
    {
        Func<string, string, string> access = _ctx.Services.FileReadAccess
            ?? throw new ScriptApiException($"{api}: reading files is not available in this host.");
        return access(Resolve(path), api);
    }

    private string WritePath(string api, string path)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ScriptApiException($"{api}: images are written as .png (got '{Path.GetFileName(path)}').");
        }

        Func<string, string, string> access = _ctx.Services.FileWriteAccess
            ?? throw new ScriptApiException($"{api}: writing files is not available in this host.");
        return access(Resolve(path), api);
    }

    private string? Out(string api, ScriptArgs args, bool required)
    {
        string? o = args.String(api, "out");
        if (o is null)
        {
            return required ? throw new ScriptApiException($"{api}: out (the .png to write) is required.") : null;
        }

        return WritePath(api, o);
    }

    private static void Write(string api, string full, byte[] png)
    {
        try
        {
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(full, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ScriptApiException($"{api}: could not write '{Path.GetFileName(full)}': {ex.Message}");
        }
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ScriptApiException("A file path is required.");
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        // A new level saved with --out has no file yet: its save folder is the level's folder.
        string? dir = _ctx.Document.Path is { Length: > 0 } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : _ctx.Services.PendingLevelFolder;
        return Path.GetFullPath(Path.Combine(dir ?? Environment.CurrentDirectory, path));
    }

    private static int[]? Rect(string api, ScriptArgs args, string key, RefImage img)
    {
        if (args.Table(api, key) is not { } t)
        {
            return null;
        }

        double[] r = Nums(api, key, t, 4);
        int x = (int)Math.Round(r[0]), y = (int)Math.Round(r[1]), w = (int)Math.Round(r[2]), h = (int)Math.Round(r[3]);
        if (w < 4 || h < 4 || x < 0 || y < 0 || x + w > img.Width || y + h > img.Height)
        {
            throw new ScriptApiException($"{api}: {key} = {{x, y, w, h}} = {{{x}, {y}, {w}, {h}}} must lie inside the {img.Width}x{img.Height} picture and be at least 4x4.");
        }

        return new[] { x, y, w, h };
    }

    private static double[] Nums(string api, string key, ScriptArgs t, int n)
    {
        if (t.Items.Count != n || t.Items.Any(v => v is not double))
        {
            throw new ScriptApiException($"{api}: {key} must be a list of {n} numbers.");
        }

        return t.Items.Select(v => (double)v!).ToArray();
    }

    private static double[] W(RefCalibration cal, (double X, double Y) q)
    {
        (double x, double z) = cal.ToWorld(q.X, q.Y);
        return new[] { Math.Round(x, 3), Math.Round(z, 3) };
    }

    private sealed class State
    {
        public RefImage? Image { get; set; }

        public RefCalibration? Calibration { get; set; }

        public RefMask? Mask { get; set; }
    }
}

/// <summary>The option tables of the <c>ref</c> global.</summary>
public static partial class ScriptOptionSets
{
    private const string Px2 = "{x, y}";
    private const string PathDoc = "Use this picture (and make it current) instead of the one ref.open registered.";
    private const string CropDoc = "Only this part of the picture: {x, y, w, h} source pixels.";

    public static readonly ScriptOptionSet RefGrid = new("ref.grid",
        new("out", "string", null, "Required: the .png to write (relative to the level's folder)."),
        new("path", "string", "current", PathDoc),
        new("crop", "{x, y, w, h}", "whole picture", CropDoc),
        new("zoom", "number", "fit about 1400 px", "Enlargement (crisp pixels above 1); 2-8 on a crop reads corners to a pixel."),
        new("step", "number", "auto (lines >= 70 px apart)", "Major grid step in source pixels; minor lines at a fifth of it."),
        new("overlay", "table | \"level\"", "none", "Draw outlines with names and corner dots: a kit.trace table (its units; default m), a kit.trace result, a list of regions {name, poly} / ref.mask polygons / point lists, or \"level\" (the built floor, one outline per height band)."),
        new("overlay_units", "string", "\"px\"", "Units of overlay lists that are not a kit.trace table: \"px\" or \"m\" (world x, z through the calibration)."));

    public static readonly ScriptOptionSet RefCalibrate = new("ref.calibrate",
        new("bar", "{x1, y1, x2, y2, metres}", null, "Scale from two pixels a known distance apart (a scale bar, a door, a corridor width)."),
        new("ppm", "number", null, "Scale as picture pixels per metre (instead of bar)."),
        new("mpp", "number", null, "Scale as metres per picture pixel (instead of bar)."),
        new("origin", Px2, "the picture's centre", "The pixel that lands on world point at."),
        new("at", "{x, z}", "{0, 0}", "World x, z of origin."),
        new("up", "string", "\"+z\"", "World direction the picture's top points to: \"+z\" (north up, like render_plan), \"+x\", \"-z\", \"-x\". Picture down is then -Z."),
        new("rotation", "number", "0", "Instead of up: the heading of the picture's top in degrees (0 = +Z, 90 = +X)."),
        new("path", "string", "current", PathDoc));

    public static readonly ScriptOptionSet RefMask = new("ref.mask",
        new("samples", "{{x, y}, ...}", null, "Pixels whose colour (5x5 average) is playable; give several per floor colour. samples_report gives each one's colour and share of the picture."),
        new("mode", "string", "\"rgb\"", "\"rgb\": RGB distance (tolerance). \"hsv\": hue within hue_tol, saturation within sat_tol, value within val_tol (separates saturated rock from yellow or grey tiles)."),
        new("hue_tol", "number", "15", "hsv: hue tolerance in degrees (ignored for grey references, saturation < 0.12)."),
        new("sat_tol", "number", "0.2", "hsv: saturation tolerance (0-1)."),
        new("val_tol", "number", "0.3", "hsv: value (brightness) tolerance (0-1)."),
        new("ranges", "{{hue = {min, max}, sat = {min, max}, val = {min, max}}, ...}", null, "hsv: explicit boxes (hue degrees, wrapping when min > max; sat / val 0-1; any part optional)."),
        new("polygons", "table", null, "Instead of colours: rasterise these polygons (a kit.trace table, regions, ref.mask polygons or point lists) into the mask."),
        new("units", "string", "\"px\"", "Units of polygons and split polys: \"px\" or \"m\"."),
        new("holes", "string", "\"keep\"", "\"keep\": outlines carry holes (holes_px / holes). \"cut\": pieces without holes, cut along pixel rows through each hole (they share the cut edges), for kit.trace."),
        new("split", "\"colors\" | {{name, rect | poly, ...}, ...}", "none", "Separate outlines per colour class, or per zone (rect = {x, y, w, h} or poly in px). Extra zone fields (floor, kind...) are copied into the result's ready-made regions."),
        new("colors", "{{r, g, b}, ...}", null, "Playable colours (0-255), with or instead of samples."),
        new("tolerance", "number", "40", "RGB distance a pixel may be from a listed colour."),
        new("invert", "boolean", "false", "Playable = pixels NOT matching (list rock / background colours instead)."),
        new("connected", "boolean", "false", "Keep only the regions that contain a sample."),
        new("crop", "{x, y, w, h}", "whole picture", "Only segment this rectangle (e.g. the map panel)."),
        new("close", "number", "2", "Closing radius px: bridges grout lines and gaps up to about twice it."),
        new("open", "number", "1", "Opening radius px: removes specks and threads."),
        new("min_area", "number", "64", "Drop regions smaller than this many pixels."),
        new("fill_holes", "number", "64", "Fill holes smaller than this many pixels."),
        new("simplify", "number", "0.5", "Outline tolerance in metres (calibrated)."),
        new("simplify_px", "number", "2", "Outline tolerance in pixels when not calibrated."),
        new("max_polygons", "number", "32", "Return at most this many outlines, largest first."),
        new("out", "string", "none", "Write an overlay .png (mask green, outlines yellow, corners red)."),
        new("mask_out", "string", "none", "Write the mask itself as a black / white .png (for compare's mask)."),
        new("path", "string", "current", PathDoc));

    public static readonly ScriptOptionSet RefMaskEdit = new("ref.mask_edit",
        new("fill", "table", null, "Polygons to add (a list of point lists, regions {name, poly}, ref.mask polygons or a kit.trace table)."),
        new("erase", "table", null, "Polygons to remove (same forms as fill); applied after fill."),
        new("units", "string", "\"px\"", "Units of fill / erase lists: \"px\" or \"m\" (a kit.trace table uses its own units)."),
        new("mask", "string", "the last ref.mask", "Start from this mask .png (white = playable, the picture's size)."),
        new("out", "string", "none", "Write the edited mask as a black / white .png."),
        new("overlay", "string", "none", "Write an overlay .png (the picture, mask green, outlines)."),
        new("path", "string", "current", PathDoc));

    public static readonly ScriptOptionSet RefCompare = new("ref.compare",
        new("mask", "string", "the last ref.mask", "A mask .png the picture's size (white = playable) instead of the last ref.mask."),
        new("out", "string", "none", "Write the overlay .png: picture dimmed, floor cyan, missing red, extra orange, mask edge yellow, worst regions boxed."),
        new("crop", "{x, y, w, h}", "the mask's bounds + margin", "Compare only this window of the picture."),
        new("y", "{min, max}", "all heights", "Only floor between these heights (one storey)."),
        new("worst", "number", "5", "How many worst regions to list."),
        new("build", "string", "\"none\"", "\"sealed\" runs ops.build() first so the current brushes are measured."),
        new("path", "string", "current", PathDoc));
}
