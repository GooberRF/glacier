using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Spatial;

namespace Ged.Core.Analysis.Recreation;

/// <summary>One walkable floor sample: its height and the clear air above it (+∞ = open sky).</summary>
internal readonly record struct FloorPoint(double Y, double Headroom);

/// <summary>
/// A level's walkable floor for the recreation scorer (recreate/SCORING.md §1): the <see cref="SpaceSampler"/> floor
/// lattice of the compiled geometry (up-facing faces with normal.Y ≥ 0.7, invisible collision helpers, portals, sky
/// rooms and liquid surfaces left out) at 0.5 m, keeping samples with at least 1 m of headroom that are not inside a
/// solid (a detail brush's top seen from inside it). Samples are grouped by lattice cell: cell (i, k) covers
/// x ∈ [i·p, (i+1)·p), z ∈ [k·p, (k+1)·p) and holds every floor height in that column.
/// </summary>
internal sealed class RecreationFloor
{
    /// <summary>The lattice pitch the scorer measures at (metres).</summary>
    public const double DefaultPitch = 0.5;

    private readonly int[] _start;
    private readonly FloorPoint[] _points;

    private RecreationFloor(double pitch, int i0, int k0, int w, int h, int[] start, FloorPoint[] points)
    {
        Pitch = pitch;
        I0 = i0;
        K0 = k0;
        W = w;
        H = h;
        _start = start;
        _points = points;
        if (points.Length > 0)
        {
            MinY = points.Min(p => p.Y);
            MaxY = points.Max(p => p.Y);
        }
    }

    public double Pitch { get; }

    public int I0 { get; }

    public int K0 { get; }

    public int W { get; }

    public int H { get; }

    public double MinY { get; }

    public double MaxY { get; }

    /// <summary>The number of floor samples.</summary>
    public int Count => _points.Length;

    /// <summary>The number of lattice cells with floor (the plan area is this × pitch²).</summary>
    public int CellCount { get; private init; }

    public bool IsEmpty => _points.Length == 0;

    /// <summary>Every floor sample.</summary>
    public ReadOnlySpan<FloorPoint> Points => _points;

    /// <summary>The floor samples of cell <paramref name="cell"/> (from <see cref="Cell"/>).</summary>
    public ReadOnlySpan<FloorPoint> In(int cell) => cell < 0 ? ReadOnlySpan<FloorPoint>.Empty : _points.AsSpan(_start[cell], _start[cell + 1] - _start[cell]);

    /// <summary>The cell holding world point (x, z), or −1 when it has no floor.</summary>
    public int Cell(double x, double z)
    {
        if (_points.Length == 0)
        {
            return -1;
        }

        int i = (int)Math.Floor(x / Pitch) - I0, k = (int)Math.Floor(z / Pitch) - K0;
        if ((uint)i >= (uint)W || (uint)k >= (uint)H)
        {
            return -1;
        }

        int c = (k * W) + i;
        return _start[c + 1] > _start[c] ? c : -1;
    }

    /// <summary>The XZ extent of the floor cells (min x, min z, max x, max z), metres.</summary>
    public (double MinX, double MinZ, double MaxX, double MaxZ) Bounds =>
        (I0 * Pitch, K0 * Pitch, (I0 + W) * Pitch, (K0 + H) * Pitch);

    /// <summary>Measures the walkable floor of compiled geometry (see the class summary).</summary>
    public static RecreationFloor Measure(Geometry geo, TerrainGround? ground, CancellationToken ct, double pitch = DefaultPitch)
    {
        ArgumentNullException.ThrowIfNull(geo);
        SpaceSampler sampler = SpaceSampler.Build(geo, ct, probe: null, ground);
        sampler.MeasureHeadroom(pitch, 4_000_000, Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)));
        var kept = new List<(int I, int K, FloorPoint P)>();
        foreach (SpaceSampler.Sample s in sampler.Samples)
        {
            if (s.Valid && !InsideSolid(sampler, s.P))
            {
                kept.Add((s.I, s.K, new FloorPoint(s.P.Y, s.Headroom)));
            }
        }

        return FromSamples(sampler.Pitch, kept);
    }

    /// <summary>Groups lattice samples (cell indices + floor point) into a floor model.</summary>
    public static RecreationFloor FromSamples(double pitch, IReadOnlyList<(int I, int K, FloorPoint P)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return new RecreationFloor(pitch, 0, 0, 0, 0, new int[1], Array.Empty<FloorPoint>());
        }

        int i0 = samples.Min(s => s.I), i1 = samples.Max(s => s.I), k0 = samples.Min(s => s.K), k1 = samples.Max(s => s.K);
        int w = i1 - i0 + 1, h = k1 - k0 + 1;
        var counts = new int[(w * h) + 1];
        foreach ((int i, int k, _) in samples)
        {
            counts[((k - k0) * w) + (i - i0) + 1]++;
        }

        int cells = 0;
        for (int c = 1; c < counts.Length; c++)
        {
            if (counts[c] > 0)
            {
                cells++;
            }

            counts[c] += counts[c - 1];
        }

        var fill = (int[])counts.Clone();
        var points = new FloorPoint[samples.Count];
        foreach ((int i, int k, FloorPoint p) in samples.OrderBy(s => s.P.Y))
        {
            points[fill[((k - k0) * w) + (i - i0)]++] = p;
        }

        return new RecreationFloor(pitch, i0, k0, w, h, counts, points) { CellCount = cells };
    }

    /// <summary>The highest climb from one floor sample to a neighbour's (m): a step or a jump onto a ledge.</summary>
    public const double JumpUp = 1.5;

    /// <summary>How far above or below a stand point (a spawn, the Player Start, an item) its floor may lie (m).</summary>
    public const double StandAbove = 1.5, StandBelow = 4.0;

    /// <summary>
    /// The stand points of a level for <see cref="Reachable"/>: multiplayer respawns, the Player Start, items and
    /// navigation points (each is placed where a player or bot stands).
    /// </summary>
    public static List<Vec3d> StandPoints(RflFile? rfl)
    {
        var pts = new List<Vec3d>();
        if (rfl is null)
        {
            return pts;
        }

        rfl.ParseAllKnownSections();
        foreach (IO.Rfl.Sections.MpRespawnPointsSection s in rfl.Sections.Select(s => s.Content).OfType<IO.Rfl.Sections.MpRespawnPointsSection>())
        {
            pts.AddRange(s.Points.Select(p => new Vec3d(p.Position.X, p.Position.Y, p.Position.Z)));
        }

        foreach (IO.Rfl.Sections.NavPointsSection s in rfl.Sections.Select(s => s.Content).OfType<IO.Rfl.Sections.NavPointsSection>())
        {
            pts.AddRange(s.NavPoints.Select(p => new Vec3d(p.Position.X, p.Position.Y, p.Position.Z)));
        }

        foreach (LevelObject o in new EditorDocument(rfl).Objects)
        {
            if (o.Kind is LevelObjectKind.PlayerStart or LevelObjectKind.Item)
            {
                pts.Add(new Vec3d(o.Position.X, o.Position.Y, o.Position.Z));
            }
        }

        return pts;
    }

    /// <summary>
    /// Splits the floor into the parts connected to <paramref name="standPoints"/> and the rest: hidden rooms, sky boxes and
    /// other floor no player stands on (recreate/SCORING.md §1). Each stand point seeds the highest floor sample within 1 m
    /// in plan and between <see cref="StandBelow"/> below and <see cref="StandAbove"/> above it. Two samples in
    /// neighbouring lattice cells (8-neighbourhood) are joined when moving from one at height y into the other's cell lands
    /// on it: that cell's highest floor no higher than y + <see cref="JumpUp"/> (walking, a step, a jump onto a ledge, or a
    /// fall of any height), or a floor within one jump below that landing. Joins count both ways, so a floor reached only
    /// by a drop stays (ladders, lifts and jump pads are not modelled; climbing back up is given the benefit of the
    /// doubt). What is dropped is floor with no walkable link at all to any stand point. Returns null when no stand point
    /// lands on floor (nothing is excluded then).
    /// </summary>
    public (RecreationFloor Kept, RecreationFloor Dropped, int Seeds)? Reachable(IReadOnlyList<Vec3d> standPoints) =>
        Reachable(standPoints, oneWay: false, maxDrop: double.PositiveInfinity, killDepth: double.PositiveInfinity);

    /// <summary>A floor this far (m) below the lowest stand point's floor is a kill zone (a chasm bottom) in mockup scoring.</summary>
    public const double KillDepth = 8.0;

    /// <summary>The longest fall (m) the mockup reachability follows: deeper drops are not a way to go.</summary>
    public const double MaxFall = 6.0;

    /// <summary>
    /// <see cref="Reachable(IReadOnlyList{Vec3d})"/> with the mockup scorer's stricter rule (recreate/SCORING.md §1, mockup):
    /// with <paramref name="oneWay"/> a join is followed only in the direction a player moves (from a reached sample into a
    /// neighbour cell: a step or jump up of at most <see cref="JumpUp"/>, or a fall of at most <paramref name="maxDrop"/>),
    /// so a rock ledge or mesa top only reachable by climbing is left out; floor more than <paramref name="killDepth"/> below
    /// the lowest seed's floor is a kill zone and is left out too.
    /// </summary>
    public (RecreationFloor Kept, RecreationFloor Dropped, int Seeds)? Reachable(IReadOnlyList<Vec3d> standPoints, bool oneWay, double maxDrop, double killDepth)
    {
        ArgumentNullException.ThrowIfNull(standPoints);
        if (IsEmpty || standPoints.Count == 0)
        {
            return null;
        }

        var cellOf = new int[_points.Length];
        for (int c = 0; c < W * H; c++)
        {
            for (int j = _start[c]; j < _start[c + 1]; j++)
            {
                cellOf[j] = c;
            }
        }

        var reached = new bool[_points.Length];
        var queue = new Queue<int>();
        int seeds = 0;
        foreach (Vec3d s in standPoints)
        {
            int best = -1;
            for (double dx = -1; dx <= 1.001; dx += Pitch)
            {
                for (double dz = -1; dz <= 1.001; dz += Pitch)
                {
                    int c = Cell(s.X + dx, s.Z + dz);
                    for (int j = c < 0 ? 0 : _start[c]; c >= 0 && j < _start[c + 1]; j++)
                    {
                        double y = _points[j].Y;
                        if (y <= s.Y + StandAbove && y >= s.Y - StandBelow && (best < 0 || y > _points[best].Y))
                        {
                            best = j;
                        }
                    }
                }
            }

            if (best >= 0)
            {
                seeds++;
                if (!reached[best])
                {
                    reached[best] = true;
                    queue.Enqueue(best);
                }
            }
        }

        if (seeds == 0)
        {
            return null;
        }

        double floorCut = double.NegativeInfinity;
        if (double.IsFinite(killDepth))
        {
            double lowest = double.PositiveInfinity;
            for (int j = 0; j < reached.Length; j++)
            {
                if (reached[j])
                {
                    lowest = Math.Min(lowest, _points[j].Y);
                }
            }

            floorCut = lowest - killDepth;
        }

        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            int c = cellOf[p], i = c % W, k = c / W;
            double y = _points[p].Y;
            for (int dk = -1; dk <= 1; dk++)
            {
                for (int di = -1; di <= 1; di++)
                {
                    int ni = i + di, nk = k + dk;
                    if ((di == 0 && dk == 0) || (uint)ni >= (uint)W || (uint)nk >= (uint)H)
                    {
                        continue;
                    }

                    int n = (nk * W) + ni;
                    for (int j = _start[n]; j < _start[n + 1]; j++)
                    {
                        if (!reached[j] && _points[j].Y >= floorCut && (oneWay ? Step(y, n, _points[j].Y) && y - _points[j].Y <= maxDrop : Step(y, n, _points[j].Y) || Step(_points[j].Y, c, y)))
                        {
                            reached[j] = true;
                            queue.Enqueue(j);
                        }
                    }
                }
            }
        }

        // True when a player at height fromY, moving into cell `cell`, lands on (or can stand at) height toY there: the
        // cell's highest floor no higher than fromY + JumpUp, or a floor within one jump below that landing.
        bool Step(double fromY, int cell, double toY)
        {
            double top = double.NegativeInfinity;
            for (int j = _start[cell]; j < _start[cell + 1]; j++)
            {
                if (_points[j].Y <= fromY + JumpUp)
                {
                    top = Math.Max(top, _points[j].Y);
                }
            }

            return toY <= top && toY >= top - JumpUp;
        }

        var kept = new List<(int I, int K, FloorPoint P)>();
        var dropped = new List<(int I, int K, FloorPoint P)>();
        for (int j = 0; j < _points.Length; j++)
        {
            (reached[j] ? kept : dropped).Add(((cellOf[j] % W) + I0, (cellOf[j] / W) + K0, _points[j]));
        }

        return (FromSamples(Pitch, kept), FromSamples(Pitch, dropped), seeds);
    }

    /// <summary>True when the first face straight up from the sample faces up: the sample lies inside a solid.</summary>
    private static bool InsideSolid(SpaceSampler sampler, Vec3d p) =>
        sampler.Cast(new Vec3d(p.X, p.Y + 0.02, p.Z), new Vec3d(0, 1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.Any, out int face) is not null
        && face >= 0 && sampler.Faces[face].N.Y > 0.1;
}
