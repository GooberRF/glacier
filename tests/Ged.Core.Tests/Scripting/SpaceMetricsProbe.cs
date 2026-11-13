using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Simple local measurements of a compiled space for the design-kit archetype gates (DESIGN-QUALITY-SPEC §a.1-a.2):
/// headroom p50 and IQR/median over covered floor samples, the share of neighbouring 8 m cells whose headroom
/// steps by at least 1.5 m, the sky share, the off-axis wall share by area, the sloped floor share and wall
/// planes per 100 m². Floor samples come from the corpus <see cref="SpaceSampler"/> lattice; everything else
/// is a direct pass over the compiled faces (portal, sky-room, invisible and show-sky faces left out).
/// </summary>
internal sealed record SpaceMetrics(
    int Samples, double SkyShare, double HeadroomP25, double HeadroomP50, double HeadroomP75, double HeadroomP90, double HeadroomIqrRatio,
    double CellStepShare, double OffAxisShare, double SlopedShare, double WallPlanesPer100, double FloorArea)
{
    /// <summary>The spec's ceiling-variety rule (c.1-2 / G07): IQR/median at least <paramref name="iqr"/>, or at least <paramref name="cells"/> of neighbouring cells step.</summary>
    public bool Varied(double iqr = 0.5, double cells = 0.25) => HeadroomIqrRatio >= iqr || CellStepShare >= cells;

    public override string ToString() =>
        $"samples {Samples}, sky {SkyShare:0.00}, headroom p25/p50/p75/p90 {HeadroomP25:0.0}/{HeadroomP50:0.0}/{HeadroomP75:0.0}/{HeadroomP90:0.0} m, IQR/median {HeadroomIqrRatio:0.00}, cell steps {CellStepShare:0.00}, " +
        $"off-axis {OffAxisShare:0.00}, sloped {SlopedShare:0.00}, wall planes/100m² {WallPlanesPer100:0.0}, floor {FloorArea:0} m²";
}

internal static class SpaceMetricsProbe
{
    /// <summary>Measures floor samples and faces between <paramref name="minY"/> and <paramref name="maxY"/>.</summary>
    public static SpaceMetrics Measure(Geometry geo, double minY = -1e9, double maxY = 1e9)
    {
        SpaceSampler sampler = SpaceSampler.Build(geo, CancellationToken.None);
        sampler.Measure(0.75, 60000);
        List<SpaceSampler.Sample> samples = sampler.Samples.Where(s => s.Valid && s.P.Y >= minY - 1e-3 && s.P.Y <= maxY + 1e-3).ToList();
        // Open sky: nothing overhead, or the first ceiling overhead shows the sky.
        bool Sky(SpaceSampler.Sample s)
        {
            if (s.OpenSky)
            {
                return true;
            }

            sampler.Cast(new Vec3d(s.P.X, s.P.Y + 0.005, s.P.Z), new Vec3d(0, 1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.DownFacing, out int hit);
            return hit >= 0 && (geo.Faces[sampler.Faces[hit].Source].Flags & (ushort)FaceFlags.ShowSky) != 0;
        }

        List<SpaceSampler.Sample> covered = samples.Where(s => !Sky(s)).ToList();
        double[] heads = covered.Select(s => s.Headroom).OrderBy(h => h).ToArray();
        double p50 = Pct(heads, 0.5);
        double iqr = heads.Length == 0 || p50 <= 0 ? 0 : (Pct(heads, 0.75) - Pct(heads, 0.25)) / p50;

        // 8 m cells (per storey band of 4 m, so stacked floors are separate cells): median headroom per cell.
        var cells = covered
            .GroupBy(s => (X: (int)Math.Floor(s.P.X / 8), Z: (int)Math.Floor(s.P.Z / 8), Y: (int)Math.Floor(s.P.Y / 4)))
            .ToDictionary(g => g.Key, g => Pct(g.Select(s => s.Headroom).OrderBy(h => h).ToArray(), 0.5));
        int pairs = 0, steps = 0;
        foreach (((int x, int z, int y), double h) in cells)
        {
            foreach ((int dx, int dz) in new[] { (1, 0), (0, 1) })
            {
                if (cells.TryGetValue((x + dx, z + dz, y), out double n))
                {
                    pairs++;
                    if (Math.Abs(n - h) >= 1.5)
                    {
                        steps++;
                    }
                }
            }
        }

        double wall = 0, offAxis = 0, floor = 0, sloped = 0;
        var planes = new HashSet<(long, long, long, long)>();
        double sinLimit = Math.Sin(5 * Math.PI / 180);
        for (int i = 0; i < geo.Faces.Count; i++)
        {
            Face f = geo.Faces[i];
            if (f.IsPortalFace || (f.Flags & (ushort)FaceFlags.ShowSky) != 0 || SpaceSampler.IsInvisibleTexture(geo, f))
            {
                continue;
            }

            if (f.RoomIndex >= 0 && f.RoomIndex < geo.Rooms.Count && geo.Rooms[f.RoomIndex].IsSkyroom != 0)
            {
                continue;
            }

            if (SpaceSampler.Polygon(geo, f) is not { } p)
            {
                continue;
            }

            double cy = p.Poly.Average(v => v.Y);
            if (cy < minY - 0.5 || cy > maxY + 40)
            {
                continue;
            }

            Vec3d n = p.Normal;
            if (Math.Abs(n.Y) < 0.3)
            {
                wall += p.Area;
                if (Math.Abs(n.X) > sinLimit && Math.Abs(n.Z) > sinLimit)
                {
                    offAxis += p.Area;
                }

                double d = n.X * p.Poly[0].X + n.Y * p.Poly[0].Y + n.Z * p.Poly[0].Z;
                planes.Add(((long)Math.Round(n.X * 200), (long)Math.Round(n.Y * 200), (long)Math.Round(n.Z * 200), (long)Math.Round(d * 4)));
            }
            else if (n.Y >= SpaceSampler.FloorNormalY && cy <= maxY + 1e-3)
            {
                floor += p.Area * n.Y;
                if (n.Y < 0.985)
                {
                    sloped += p.Area * n.Y;
                }
            }
        }

        return new SpaceMetrics(
            samples.Count,
            samples.Count == 0 ? 0 : (double)(samples.Count - covered.Count) / samples.Count,
            Pct(heads, 0.25), p50, Pct(heads, 0.75), Pct(heads, 0.9), iqr,
            pairs == 0 ? 0 : (double)steps / pairs,
            wall <= 0 ? 0 : offAxis / wall,
            floor <= 0 ? 0 : sloped / floor,
            floor <= 0 ? 0 : planes.Count / floor * 100,
            floor);
    }

    private static double Pct(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        double at = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(at);
        int hi = Math.Min(sorted.Length - 1, lo + 1);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (at - lo));
    }
}
