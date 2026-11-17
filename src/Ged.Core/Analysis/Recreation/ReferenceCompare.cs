using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Analysis.Recreation;

/// <summary>
/// The closed loop of recreation from an overhead reference (Lua <c>ref.compare</c>): the level's walkable floor
/// (the recreation scorer's <see cref="RecreationFloor"/>: compiled geometry, normal.Y ≥ 0.7, ≥ 1 m headroom, not
/// inside a solid) is drawn through the calibration onto the reference picture's pixels and compared with a playable
/// mask of that same picture. Only the picture the agent was given is used: no hidden truth. Reports plan IoU (plain
/// and within a tolerance), the boundary distance in metres, and the largest missing / extra regions.
/// </summary>
public static class ReferenceCompare
{
    /// <summary>The boundary tolerance (m) of the tolerant IoU and of the region search (the scorer's lattice pitch).</summary>
    public const double ToleranceMetres = 0.5;

    /// <summary>The comparison: numbers as a Lua-shaped table, and the overlay PNG.</summary>
    public sealed record Result(Dictionary<string, object?> Table, byte[] Png);

    /// <summary>Compares <paramref name="floor"/> with <paramref name="mask"/> inside <paramref name="crop"/> (x, y, w, h; null = the mask's bounds plus a margin).</summary>
    internal static Result Run(RecreationFloor floor, RefCalibration cal, RefImage img, RefMask mask, int[]? crop, int worst, double? yMin, double? yMax)
    {
        int W = mask.Width, H = mask.Height;
        int x0, y0, x1, y1;
        if (crop is not null)
        {
            x0 = Math.Clamp(crop[0], 0, W);
            y0 = Math.Clamp(crop[1], 0, H);
            x1 = Math.Clamp(crop[0] + crop[2], x0 + 1, W);
            y1 = Math.Clamp(crop[1] + crop[3], y0 + 1, H);
        }
        else
        {
            (x0, y0, x1, y1) = (W, H, 0, 0);
            for (int p = 0; p < mask.M.Length; p++)
            {
                if (mask.M[p])
                {
                    int x = p % W, y = p / W;
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x + 1);
                    y1 = Math.Max(y1, y + 1);
                }
            }

            if (x1 <= x0)
            {
                (x0, y0, x1, y1) = (0, 0, W, H);
            }

            int margin = (int)Math.Ceiling(Math.Max(8 * cal.Ppm, 0.08 * Math.Max(x1 - x0, y1 - y0)));
            (x0, y0, x1, y1) = (Math.Max(0, x0 - margin), Math.Max(0, y0 - margin), Math.Min(W, x1 + margin), Math.Min(H, y1 + margin));
        }

        int w = x1 - x0, h = y1 - y0;
        var t = new bool[w * h];
        var c = new bool[w * h];
        for (int v = 0; v < h; v++)
        {
            for (int u = 0; u < w; u++)
            {
                int p = (v * w) + u;
                t[p] = mask.M[((y0 + v) * W) + x0 + u];
                (double wx, double wz) = cal.ToWorld(x0 + u + 0.5, y0 + v + 0.5);
                int cell = floor.Cell(wx, wz);
                if (cell >= 0)
                {
                    if (yMin is null && yMax is null)
                    {
                        c[p] = true;
                    }
                    else
                    {
                        foreach (FloorPoint fp in floor.In(cell))
                        {
                            if (fp.Y >= (yMin ?? double.NegativeInfinity) && fp.Y <= (yMax ?? double.PositiveInfinity))
                            {
                                c[p] = true;
                                break;
                            }
                        }
                    }
                }
            }
        }

        // Floor drawn outside the compared window (beyond the picture or the crop).
        double mpp = 1 / cal.Ppm, px2 = mpp * mpp;
        long inside = c.LongCount(b => b);
        double total = floor.CellCount * floor.Pitch * floor.Pitch;
        double outside = Math.Max(0, total - (inside * px2));

        // Overlap, plain and within the tolerance.
        double tolPx = Math.Max(1, ToleranceMetres * cal.Ppm);
        double[] dT = RecreationScorer.Distance(t, w, h), dC = RecreationScorer.Distance(c, w, h);
        long nt = 0, nc = 0, both = 0, cNear = 0, tNear = 0;
        for (int p = 0; p < t.Length; p++)
        {
            if (t[p]) nt++;
            if (c[p]) nc++;
            if (t[p] && c[p]) both++;
            if (c[p] && dT[p] <= tolPx) cNear++;
            if (t[p] && dC[p] <= tolPx) tNear++;
        }

        double iou = nt + nc - both > 0 ? (double)both / (nt + nc - both) : 0;
        double P = nc > 0 ? (double)cNear / nc : 0, R = nt > 0 ? (double)tNear / nt : 0;
        double iouTol = P + R - (P * R) > 0 ? P * R / (P + R - (P * R)) : 0;

        // Walls: boundary-to-boundary distances in metres (capped like the scorer).
        bool[] bt = RecreationScorer.Boundary(t, w, h), bc = RecreationScorer.Boundary(c, w, h);
        double[] dbt = RecreationScorer.Distance(bt, w, h), dbc = RecreationScorer.Distance(bc, w, h);
        var ct = new List<double>();
        var tc = new List<double>();
        for (int p = 0; p < t.Length; p++)
        {
            if (bc[p]) ct.Add(Math.Min(RecreationScorer.ChamferCap, dbt[p] * mpp));
            if (bt[p]) tc.Add(Math.Min(RecreationScorer.ChamferCap, dbc[p] * mpp));
        }

        double ctMean = ct.Count > 0 ? ct.Average() : RecreationScorer.ChamferCap;
        double tcMean = tc.Count > 0 ? tc.Average() : RecreationScorer.ChamferCap;
        double sym = (ctMean + tcMean) / 2;
        var all = ct.Concat(tc).OrderBy(d => d).ToList();
        double p90 = all.Count > 0 ? all[(int)Math.Min(all.Count - 1, Math.Floor(0.9 * all.Count))] : RecreationScorer.ChamferCap;
        double walls = Math.Clamp(1 - (Math.Max(0, sym - RecreationScorer.FreeTolerance) / RecreationScorer.FalloffMetres), 0, 1);

        // Worst regions: missing (mask, no floor within the tolerance) and extra (floor, no mask within it).
        var miss = new bool[t.Length];
        var extra = new bool[t.Length];
        for (int p = 0; p < t.Length; p++)
        {
            miss[p] = t[p] && dC[p] > tolPx;
            extra[p] = c[p] && dT[p] > tolPx;
        }

        var regions = new List<(string Kind, int N, int Bx0, int By0, int Bx1, int By1, double Cx, double Cy)>();
        foreach ((bool[] m, string kind) in new[] { (miss, "missing"), (extra, "extra") })
        {
            (int[] label, List<int> sizes) = RefMask.Components(m, w, h, true);
            var box = sizes.Select(_ => (X0: int.MaxValue, Y0: int.MaxValue, X1: -1, Y1: -1, Sx: 0.0, Sy: 0.0)).ToArray();
            for (int p = 0; p < m.Length; p++)
            {
                if (label[p] >= 0)
                {
                    int u = p % w, v = p / w;
                    var b = box[label[p]];
                    box[label[p]] = (Math.Min(b.X0, u), Math.Min(b.Y0, v), Math.Max(b.X1, u + 1), Math.Max(b.Y1, v + 1), b.Sx + u + 0.5, b.Sy + v + 0.5);
                }
            }

            for (int i = 0; i < sizes.Count; i++)
            {
                if (sizes[i] * px2 >= 0.5)
                {
                    regions.Add((kind, sizes[i], box[i].X0 + x0, box[i].Y0 + y0, box[i].X1 + x0, box[i].Y1 + y0, (box[i].Sx / sizes[i]) + x0, (box[i].Sy / sizes[i]) + y0));
                }
            }
        }

        regions.Sort((a, b) => b.N.CompareTo(a.N));
        var worstList = new List<object?>();
        foreach (var r in regions.Take(Math.Max(0, worst)))
        {
            (double ax, double az) = cal.ToWorld(r.Bx0, r.By0);
            (double bx, double bz) = cal.ToWorld(r.Bx1, r.By1);
            (double mx, double mz) = cal.ToWorld(r.Cx, r.Cy);
            worstList.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = r.Kind,
                ["area_m2"] = Math.Round(r.N * px2, 2),
                ["box_px"] = new double[] { r.Bx0, r.By0, r.Bx1, r.By1 },
                ["box_world"] = new[] { Math.Round(Math.Min(ax, bx), 2), Math.Round(Math.Min(az, bz), 2), Math.Round(Math.Max(ax, bx), 2), Math.Round(Math.Max(az, bz), 2) },
                ["center"] = new[] { Math.Round(mx, 2), Math.Round(mz, 2) },
                ["center_px"] = new[] { Math.Round(r.Cx, 1), Math.Round(r.Cy, 1) },
                ["fix"] = r.Kind == "missing"
                    ? "the reference has playable floor here and the level has none: extend or add a region"
                    : "the level has floor here and the reference shows none: pull the region's edge back",
            });
        }

        double missM2 = miss.LongCount(b => b) * px2, extraM2 = extra.LongCount(b => b) * px2;
        var table = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["score"] = Math.Round(100 * ((0.5 * iouTol) + (0.5 * walls)), 1),
            ["iou"] = Math.Round(iou, 4),
            ["iou_tol"] = Math.Round(iouTol, 4),
            ["precision"] = Math.Round(P, 4),
            ["recall"] = Math.Round(R, 4),
            ["walls_mean_m"] = Math.Round(sym, 3),
            ["walls_build_to_ref_m"] = Math.Round(ctMean, 3),
            ["walls_ref_to_build_m"] = Math.Round(tcMean, 3),
            ["walls_p90_m"] = Math.Round(p90, 3),
            ["walls"] = Math.Round(walls, 4),
            ["mask_m2"] = Math.Round(nt * px2, 1),
            ["built_m2"] = Math.Round(nc * px2, 1),
            ["missing_m2"] = Math.Round(missM2, 1),
            ["extra_m2"] = Math.Round(extraM2, 1),
            ["outside_m2"] = Math.Round(outside, 1),
            ["window_px"] = new[] { x0, y0, w, h },
            ["worst"] = worstList.ToArray(),
            ["read"] = "score = 50 x iou_tol + 50 x walls (0-100, against this picture's mask only). Fix the first worst region, rebuild, compare again.",
        };

        // Overlay: the picture dimmed, floor cyan, missing red, extra orange, the mask's outline yellow, worst boxes numbered.
        var cv = new RefCanvas(w, h + 30);
        for (int v = 0; v < h; v++)
        {
            for (int u = 0; u < w; u++)
            {
                int p = (v * w) + u;
                (byte r, byte g, byte b) = img.At(x0 + u, y0 + v);
                cv.Set(u, v + 30, (byte)(r * 0.4), (byte)(g * 0.4), (byte)(b * 0.4));
                if (miss[p])
                {
                    cv.Blend(u, v + 30, 255, 40, 40, 0.65);
                }
                else if (extra[p])
                {
                    cv.Blend(u, v + 30, 255, 150, 0, 0.65);
                }
                else if (c[p])
                {
                    cv.Blend(u, v + 30, 0, 220, 255, 0.45);
                }

                if (bt[p])
                {
                    cv.Set(u, v + 30, 255, 230, 0);
                }
            }
        }

        int k = 0;
        foreach (var r in regions.Take(Math.Max(0, worst)))
        {
            k++;
            cv.Rect(r.Bx0 - x0, r.By0 - y0 + 30, r.Bx1 - x0, r.By1 - y0 + 30, 255, 255, 255, 1);
            cv.Text(r.Bx0 - x0 + 3, r.By0 - y0 + 33, k.ToString(System.Globalization.CultureInfo.InvariantCulture), 2, 255, 255, 255, box: true);
        }

        cv.Text(6, 4, $"score {RefCanvas.N(Math.Round(100 * ((0.5 * iouTol) + (0.5 * walls)), 1))}  iou {RefCanvas.N(Math.Round(iou, 3))} (tol {RefCanvas.N(Math.Round(iouTol, 3))})  walls {RefCanvas.N(Math.Round(sym, 2))} m", 1, 255, 255, 255);
        cv.Text(6, 16, $"missing {RefCanvas.N(Math.Round(missM2))} m2 (red)  extra {RefCanvas.N(Math.Round(extraM2))} m2 (orange)  floor cyan, mask edge yellow; window {x0},{y0} {w}x{h}", 1, 200, 200, 200);
        return new Result(table, cv.Png());
    }
}
