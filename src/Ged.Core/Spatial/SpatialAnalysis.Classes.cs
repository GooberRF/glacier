using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

// The features the level-class detector reads (DESIGN-QUALITY-SPEC §(h)): how much walkable floor is under sky or a
// far ceiling, and how much of it sits at an open edge that falls away into the void.
public sealed partial class SpatialAnalysis
{
    /// <summary>A ceiling at least this far above the floor reads as open air (a skybox or the void above a platform).</summary>
    public const double OpenAirHeadroom = 20;

    /// <summary>How far from a floor sample the edge probes look for a fall-off (m).</summary>
    public const double EdgeProbe = 1.5;

    private JsonObject? _classFeatures;

    /// <summary>Floor samples on an open edge with a drop of more than 8 m: (point, true when the fall lands off the playable floor).</summary>
    internal List<(Vec3d P, bool Void)> FallOffEdges { get; } = new();

    /// <summary>The class detector's two inputs: the open-air share of the floor and the share at fall-off edges.</summary>
    public (double OpenAir, double FallOff) ClassFeatureValues()
    {
        JsonObject f = ClassFeatures();
        return (f["open_air_fraction"]!.GetValue<double>(), f["falloff_edge_fraction"]!.GetValue<double>());
    }

    private JsonObject ClassFeatures()
    {
        if (_classFeatures is not null)
        {
            return _classFeatures;
        }

        ShapeReport(); // sky ceilings
        List<int> even = _w.PlayIdx.Where(i => (_s[i].I + _s[i].K) % 2 == 0).ToList();
        var edge = new int[even.Count]; // 0 none, 1 fall (> 8 m) onto playable floor, 2 fall out of play
        For(even.Count, j =>
        {
            Vec3d p = _s[even[j]].P;
            var o = new Vec3d(p.X, p.Y + 0.5, p.Z);
            int kind = 0;
            for (int k = 0; k < 16 && kind < 2; k += 2)
            {
                Vec3d dir = SpaceSampler.Directions[k];
                if (_sp.Blocked(o, dir, EdgeProbe, SpaceSampler.HitFilter.Any))
                {
                    continue;
                }

                Vec3d q = o.Add(dir.Scale(EdgeProbe));
                double? down = _sp.Cast(q, new Vec3d(0, -1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.UpFacing);
                if (down is null)
                {
                    kind = 2;
                }
                else if (down.Value - 0.5 > WalkGraph.MaxDrop)
                {
                    // A long fall: out of play (void-bounded) unless it lands on playable floor.
                    int landing = _sp.NearestValid(q.Add(new Vec3d(0, -down.Value, 0)), 1.0, 0.5, 0.5);
                    kind = landing >= 0 && _w.Play[landing] ? Math.Max(kind, 1) : 2;
                }
            }

            edge[j] = kind;
        });
        int sky = 0, open = 0, falloff = 0, bottomless = 0;
        for (int j = 0; j < even.Count; j++)
        {
            int i = even[j];
            bool skyAbove = _skyCeiling![i];
            if (skyAbove)
            {
                sky++;
            }

            if (skyAbove || _s[i].Headroom >= OpenAirHeadroom)
            {
                open++;
            }

            if (edge[j] > 0)
            {
                falloff++;
                FallOffEdges.Add((_s[i].P, edge[j] == 2));
            }

            if (edge[j] == 2)
            {
                bottomless++;
            }
        }

        double n = Math.Max(1, even.Count);
        _classFeatures = new JsonObject
        {
            ["sky_fraction"] = R(sky / n),
            ["open_air_fraction"] = R(open / n),
            ["falloff_edge_fraction"] = R(falloff / n),
            ["void_edge_fraction"] = R(bottomless / n),
        };
        return _classFeatures;
    }
}
