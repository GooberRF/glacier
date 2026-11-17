using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

// What the craft audit (Linting/CraftAudit) reads from the measurement: the intent layer's readings (flights, bridges,
// props, route ledges — the same rules spatial.purpose{} and the grader apply, read from the geometry of any level,
// tagged or not) and the playable floor lattice for the per-area density reading.
public sealed partial class SpatialAnalysis
{
    /// <summary>The intent layer's readings, computed once (<see cref="PurposeReport"/>).</summary>
    internal (IReadOnlyList<FlightRead> Flights, IReadOnlyList<BridgeRoute> Bridges, IReadOnlyList<PropRead> Props, IReadOnlyList<LedgeRead> Ledges) PurposeReadings()
    {
        PurposeReport();
        return (FlightReads, BridgeRoutes, Props, LedgeReads);
    }

    /// <summary>Every playable floor sample's point (the walk from the spawns; one lattice cell of <see cref="Pitch"/>² each).</summary>
    internal IEnumerable<Vec3d> PlayablePoints() => _w.PlayIdx.Select(i => _s[i].P);

    /// <summary>The headroom above a playable sample (+∞ under open sky).</summary>
    internal IEnumerable<(Vec3d P, double Headroom)> PlayableSamples() => _w.PlayIdx.Select(i => (_s[i].P, _s[i].Headroom));

    /// <summary>A brush's world bounds (the box the prop and set-piece readings use).</summary>
    internal static (Vec3d Min, Vec3d Max) BrushBounds(Model.Brush b)
    {
        (Vec3d mn, Vec3d mx, _) = BrushBox(b);
        return (mn, mx);
    }

    /// <summary>The share of a brush's face area under natural-rock textures (the prop reading's rule).</summary>
    internal static double BrushRockShare(Model.Brush b) => RockShare(b);

    /// <summary>True when playable floor lies within <paramref name="horizontal"/> m of <paramref name="p"/> in plan and between <paramref name="below"/> m below and <paramref name="above"/> m above it.</summary>
    internal bool PlayableNear(Vec3d p, double horizontal, double above, double below)
    {
        int s = _sp.NearestValid(p, horizontal, above, below);
        return s >= 0 && _w.Play[s];
    }
}
