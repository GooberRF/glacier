using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;

namespace Ged.Core.Spatial;

// The fingerprint-scale measurements (corridors, openings, vertical layers) exactly as `glacier-cli analyze` makes them
// (LevelMetrics: a 20,000-sample lattice over all floor, no focus), so the grader reads the same numbers the corpus
// norms and the exemplar study were made from — measured on this analysis's geometry (which may be a fresh build).
public sealed partial class SpatialAnalysis
{
    private JsonObject? _fingerprintScale;

    private JsonObject FingerprintScale()
    {
        if (_fingerprintScale is not null)
        {
            return _fingerprintScale;
        }

        var defaults = new MetricOptions();
        SpaceSampler sp = SpaceSampler.Build(_level.Geometry, _ct, ground: Ground);
        sp.Measure(defaults.LatticePitch, defaults.MaxFloorSamples, focus: null, _o.Parallelism);
        List<SpaceSampler.Sample> valid = sp.Samples.Where(s => s.Valid).ToList();
        List<SpaceSampler.Sample> corr = valid.Where(s => s.Corridor).ToList();
        int layers = 0;
        double[] ys = valid.Select(s => s.P.Y).OrderBy(y => y).ToArray();
        if (ys.Length > 0)
        {
            var bins = ys.GroupBy(y => (long)Math.Floor(y)).ToDictionary(g => g.Key, g => g.Count());
            List<long> qualifying = bins.Where(kv => kv.Value >= 0.05 * ys.Length).Select(kv => kv.Key).OrderBy(k => k).ToList();
            for (int i = 0; i < qualifying.Count; i++)
            {
                if (i == 0 || qualifying[i] != qualifying[i - 1] + 1)
                {
                    layers++;
                }
            }
        }

        List<SpaceSampler.Opening> openings = sp.FindOpenings();
        _fingerprintScale = new JsonObject
        {
            ["pitch"] = sp.Pitch,
            ["corridor"] = new JsonObject
            {
                ["width"] = Percentiles.Distribution(corr.Select(s => s.Width)),
                ["height"] = Percentiles.Distribution(corr.Where(s => !s.OpenSky).Select(s => s.Headroom)),
            },
            ["opening"] = new JsonObject
            {
                ["count"] = openings.Count,
                ["width"] = Percentiles.Distribution(openings.Select(o => o.Width)),
                ["height"] = Percentiles.Distribution(openings.Select(o => o.Height)),
            },
            ["vertical"] = new JsonObject { ["layers"] = layers },
        };
        return _fingerprintScale;
    }
}
