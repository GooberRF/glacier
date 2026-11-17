using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Analysis.Recreation;

namespace Ged.Core.Scripting;

// ref.heights{}: reads a heights picture (glacier-cli render --mode heights, or a plan with the heights overlay) back
// into floor heights, flat regions, ramps, stairs and drop edges, in the shape kit.trace{units = "px"} takes.
public sealed partial class ScriptRef
{
    /// <summary>The grid value of a cell without floor.</summary>
    private const double NoData = -9999;

    [ScriptDoc("Decodes a heights picture (render --mode heights, or a heights overlay) by its legend: {grid, flats, ramps, stairs, drops, regions (for kit.trace{units = \"px\"}), counts}; out writes a check overlay.",
        Example = "local h = ref.heights{image = \"overhead_heights.png\", legend = {-38, -31}, out = \"heights_check.png\"} local tr = kit.trace{units = \"px\", regions = h.regions}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RefHeights))]
    public Dictionary<string, object?> Heights(ScriptArgs args)
    {
        const string Api = "ref.heights";
        args = Need(Api, args);
        args.RejectUnknown(ScriptOptionSets.RefHeights);
        if (args.Has("image") && args.Has("path"))
        {
            throw new ScriptApiException($"{Api}: give image or path (the same thing), not both.");
        }

        RefImage img;
        if ((args.String(Api, "image") ?? args.String(Api, "path")) is { } picture)
        {
            img = Load(Api, picture);
            State s = S;
            if (!string.Equals(s.Image?.Path, img.Path, StringComparison.OrdinalIgnoreCase))
            {
                s.Calibration = null;
                s.Mask = null;
            }

            s.Image = img;
        }
        else
        {
            img = S.Image ?? throw new ScriptApiException($"{Api}: no picture: pass image = \"<png>\" or run ref.open first.");
        }

        if (args.Table(Api, "legend") is not { } lt)
        {
            throw new ScriptApiException($"{Api}: legend = {{min, max}} is required: the heights (m) the colour bar's two ends stand for.",
                "Read them off the picture's legend (\"walkable floor height, m (min to max)\") or the render's --json heights.range.");
        }

        double[] legend = Nums(Api, "legend", lt, 2);
        if (!(legend[1] > legend[0]))
        {
            throw new ScriptApiException($"{Api}: legend = {{min, max}} needs max above min (got {ScriptArgs.Format(legend[0])}, {ScriptArgs.Format(legend[1])}).");
        }

        double ppm;
        if (args.Has("ppm"))
        {
            ppm = args.Number(Api, "ppm", 0);
        }
        else if (S.Calibration is { } cal)
        {
            ppm = cal.Ppm;
        }
        else
        {
            throw new ScriptApiException($"{Api}: the picture has no scale: run ref.calibrate (a scale bar, or the render's worldPerPixel as mpp) or pass ppm.",
                "Slopes, areas and stairs are measured in metres.");
        }

        if (!double.IsFinite(ppm) || ppm < 0.5 || ppm > 1000)
        {
            throw new ScriptApiException($"{Api}: ppm must be 0.5 to 1000 pixels per metre (got {ScriptArgs.Format(ppm)}).");
        }

        var ignore = new List<int[]>();
        if (args.Table(Api, "ignore") is { } it)
        {
            foreach (object? item in it.Items)
            {
                double[] r = item is ScriptArgs t ? Nums(Api, "ignore", t, 4) : throw new ScriptApiException($"{Api}: ignore lists rectangles {{x, y, w, h}}.");
                ignore.Add(new[] { (int)r[0], (int)r[1], (int)r[2], (int)r[3] });
            }
        }

        int cell = args.Int(Api, "cell", 0);
        if (cell is < 0 or > 64)
        {
            throw new ScriptApiException($"{Api}: cell must be 1 to 64 pixels, or 0 for about a quarter metre.");
        }

        double minArea = args.Number(Api, "min_area", 2);
        if (minArea is < 0.25 or > 1000)
        {
            throw new ScriptApiException($"{Api}: min_area must be 0.25 to 1000 m².");
        }

        int[]? crop = Rect(Api, args, "crop", img);
        var options = new HeightsDecoderOptions
        {
            Min = legend[0],
            Max = legend[1],
            Ppm = ppm,
            Crop = crop,
            Ignore = ignore,
            Cell = cell,
            MinArea = minArea,
            SimplifyPx = Math.Max(1, args.Number(Api, "simplify", 0.1) * ppm),
        };

        HeightsDecoded d;
        using (_ctx.EnterHostOperation())
        {
            d = HeightsDecoder.Decode(img, options, _ctx.Cancellation);
        }

        static double[][] Px(IReadOnlyList<(double X, double Y)> pts) => pts.Select(q => new[] { Math.Round(q.X, 2), Math.Round(q.Y, 2) }).ToArray();
        var flats = new List<object?>();
        var ramps = new List<object?>();
        var stairs = new List<object?>();
        var regions = new List<object?>();
        foreach (DecodedRegion r in d.Regions)
        {
            var region = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = r.Name, ["poly"] = Px(r.Poly) };
            var info = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = r.Name, ["poly"] = Px(r.Poly), ["area_m2"] = Math.Round(r.AreaM2, 2) };
            if (r.Kind == "flat")
            {
                region["floor"] = r.Low;
                info["floor"] = r.Low;
                flats.Add(info);
            }
            else
            {
                double[] climb = r.Climb is { } c ? new[] { Math.Round(c.X, 4), Math.Round(c.Y, 4) } : new[] { 0.0, -1.0 };
                region["kind"] = r.Kind;
                region["floor"] = new[] { r.Low, r.High };
                region["climb"] = climb;
                info["kind"] = r.Kind;
                info["floor"] = new[] { r.Low, r.High };
                info["climb"] = climb;
                info["rise"] = Math.Round(r.High - r.Low, 2);
                info["slope"] = Math.Round(r.Slope, 3);
                (r.Kind == "stairs" ? stairs : ramps).Add(info);
            }

            regions.Add(region);
        }

        var drops = d.Drops.Select(x => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["points"] = Px(x.Points),
            ["high"] = x.High,
            ["low"] = double.IsNaN(x.Low) ? null : x.Low,
            ["drop"] = double.IsNaN(x.Low) ? null : Math.Round(x.High - x.Low, 2),
        }).ToArray();

        var values = d.Grid.Select(v => double.IsNaN(v) ? NoData : Math.Round(v, 2)).ToArray();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["units"] = "px",
            ["legend"] = legend,
            ["ppm"] = Math.Round(ppm, 4),
            ["floor_pixels"] = d.FloorPixels,
            ["grid"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cols"] = d.Cols,
                ["rows"] = d.Rows,
                ["cell"] = d.Cell,
                ["x0"] = d.X0,
                ["y0"] = d.Y0,
                ["nodata"] = NoData,
                ["values"] = values,
                ["read"] = "values[row * cols + col + 1] is the floor height (m) of the cell whose top-left pixel is (x0 + col * cell, y0 + row * cell); nodata where no floor shows.",
            },
            ["flats"] = flats.ToArray(),
            ["ramps"] = ramps.ToArray(),
            ["stairs"] = stairs.ToArray(),
            ["drops"] = drops,
            ["regions"] = regions.ToArray(),
            ["regions_note"] = "regions: ready for kit.trace{units = \"px\", regions = h.regions} (flats with floor, ramps and stairs with floor = {low, high} and climb; neighbours share their seams; give ceilings and walls from the other references).",
            ["counts"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["flats"] = flats.Count,
                ["ramps"] = ramps.Count,
                ["stairs"] = stairs.Count,
                ["drops"] = drops.Length,
            },
        };

        if (d.FloorPixels == 0)
        {
            result["note"] = "No floor colours were found: is this a heights picture (render --mode heights, or --overlays heights)?";
        }

        if (Out(Api, args, required: false) is { } o)
        {
            var shapes = new List<RefShape>();
            static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
            foreach (DecodedRegion r in d.Regions)
            {
                string label = r.Kind == "flat" ? $"{r.Name} {(r.Low >= 0 ? "+" : string.Empty)}{N(r.Low)}" : $"{r.Name} {N(r.Low)}→{N(r.High)}";
                shapes.Add(new RefShape(label, r.Poly, Array.Empty<IReadOnlyList<(double X, double Y)>>(), "region"));
            }

            int k = 0;
            foreach (DecodedDrop x in d.Drops)
            {
                k++;
                var ring = x.Points.Concat(x.Points.Reverse().Skip(1)).ToList();
                string label = double.IsNaN(x.Low) ? $"drop{k} void" : $"drop{k} ↓{N(x.High - x.Low)}";
                if (ring.Count >= 3)
                {
                    shapes.Add(new RefShape(label, ring, Array.Empty<IReadOnlyList<(double X, double Y)>>(), "polygon"));
                }
            }

            ReferenceGrid.Result g = ReferenceGrid.Render(img, crop, 0, 0, S.Calibration, "ref.heights", shapes);
            Write(Api, o, g.Png);
            result["path"] = o;
        }

        return result;
    }
}

public static partial class ScriptOptionSets
{
    public static readonly ScriptOptionSet RefHeights = new("ref.heights",
        new("image", "string", "the current picture", "The heights picture (render --mode heights, or a plan rendered with the heights overlay); it becomes the current picture."),
        new("legend", "{min, max}", null, "Required: the heights (m) at the colour bar's two ends (the legend's \"walkable floor height, m (min to max)\", or the render's --json heights.range)."),
        new("ppm", "number", "the calibration's", "Picture pixels per metre (1 / worldPerPixel of the render) when the picture is not calibrated."),
        new("crop", "{x, y, w, h}", "whole picture", "Only decode this part of the picture."),
        new("ignore", "{{x, y, w, h}, ...}", "none", "Rectangles to leave out (a legend panel, a caption); the legend's striped colour bar is skipped on its own."),
        new("cell", "number", "about 0.25 m", "The height grid's cell in pixels."),
        new("min_area", "number", "2", "The smallest flat region, ramp or flight of stairs reported (m²)."),
        new("simplify", "number", "0.1", "Outline tolerance in metres (neighbouring regions keep their shared seams within it)."),
        new("out", "string", "none", "Write a check overlay .png: the picture with every region outlined and named with its height (flat +y, ramp / stairs low→high) and the drop edges."),
        new("path", "string", "current", "Same as image."));
}
