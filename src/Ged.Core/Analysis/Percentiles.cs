using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Ged.Core.Analysis;

/// <summary>
/// Percentile math shared by the per-level fingerprint (distributions) and the corpus aggregate (norms):
/// linear interpolation between closest ranks (Hyndman–Fan type 7, the R / NumPy default).
/// </summary>
public static class Percentiles
{
    /// <summary>The percentiles every per-level distribution records.</summary>
    public static readonly IReadOnlyList<(string Name, double P)> DistributionPoints = new[]
    {
        ("p5", 0.05), ("p25", 0.25), ("p50", 0.50), ("p75", 0.75), ("p95", 0.95),
    };

    /// <summary>The type-7 percentile <paramref name="p"/> (0..1) of an ascending-sorted list. NaN when empty.</summary>
    public static double Type7(IReadOnlyList<double> sorted, double p)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        int n = sorted.Count;
        if (n == 0)
        {
            return double.NaN;
        }

        if (n == 1)
        {
            return sorted[0];
        }

        double h = (n - 1) * Math.Clamp(p, 0, 1);
        int lo = (int)Math.Floor(h);
        if (lo >= n - 1)
        {
            return sorted[n - 1];
        }

        double frac = h - lo;
        return sorted[lo] + (frac * (sorted[lo + 1] - sorted[lo]));
    }

    /// <summary>Rounds to <paramref name="decimals"/> places (away from zero at the midpoint); −0 and non-finite become 0.</summary>
    public static double Round(double v, int decimals = 3)
    {
        if (!double.IsFinite(v))
        {
            return 0;
        }

        double r = Math.Round(v, decimals, MidpointRounding.AwayFromZero);
        return r == 0 ? 0 : r;
    }

    /// <summary>
    /// A per-level distribution <c>{n, min, p5, p25, p50, p75, p95, max}</c> (values rounded to 3 decimals), or
    /// null when <paramref name="values"/> is empty. Every lattice sample carries the same weight (pitch²), so the
    /// area-weighted distribution of a sampled quantity is its plain distribution.
    /// </summary>
    public static JsonObject? Distribution(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        double[] sorted = values.Where(double.IsFinite).ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        Array.Sort(sorted);
        var o = new JsonObject
        {
            ["n"] = sorted.Length,
            ["min"] = Round(sorted[0]),
        };
        foreach ((string name, double p) in DistributionPoints)
        {
            o[name] = Round(Type7(sorted, p));
        }

        o["max"] = Round(sorted[^1]);
        return o;
    }

    /// <summary>
    /// An area-weighted distribution: each value counts in proportion to its weight. Percentiles are taken on the
    /// weighted empirical CDF (value at which the cumulative weight first reaches p of the total); n = item count.
    /// </summary>
    public static JsonObject? WeightedDistribution(IEnumerable<(double Value, double Weight)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        (double Value, double Weight)[] list = items.Where(i => double.IsFinite(i.Value) && double.IsFinite(i.Weight) && i.Weight > 0).ToArray();
        if (list.Length == 0)
        {
            return null;
        }

        Array.Sort(list, (a, b) => a.Value.CompareTo(b.Value));
        double total = list.Sum(i => i.Weight);
        var o = new JsonObject
        {
            ["n"] = list.Length,
            ["min"] = Round(list[0].Value),
        };
        foreach ((string name, double p) in DistributionPoints)
        {
            double target = p * total;
            double acc = 0;
            double v = list[^1].Value;
            foreach ((double value, double weight) in list)
            {
                acc += weight;
                if (acc >= target - 1e-12)
                {
                    v = value;
                    break;
                }
            }

            o[name] = Round(v);
        }

        o["max"] = Round(list[^1].Value);
        return o;
    }
}
