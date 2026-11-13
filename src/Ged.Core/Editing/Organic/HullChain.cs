using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing.Organic;

/// <summary>The cross-section a tube is built from.</summary>
public enum TubeProfile
{
    /// <summary>A flat floor under an arch of <see cref="TubeOptions.Sides"/> − 2 points.</summary>
    Cave,

    /// <summary>A flat floor, leaning walls and one common horizontal top (open to the sky).</summary>
    Canyon,
}

/// <summary>Options of <see cref="HullChain.Build"/> (ORGANIC-GEOMETRY-SPEC §3.1–§3.2).</summary>
public sealed class TubeOptions
{
    /// <summary>The floor path (2 to 256 points; x, z snap to 1/1024 m, y rounds to 1/16 m).</summary>
    public IReadOnlyList<Vec3d> Path { get; init; } = Array.Empty<Vec3d>();

    /// <summary>The cross-section.</summary>
    public TubeProfile Profile { get; init; }

    /// <summary>Half-width w at each path point (one value = everywhere). Cave: the arch half-width; canyon: half the floor width.</summary>
    public IReadOnlyList<double> HalfWidths { get; init; } = new[] { 3.0 };

    /// <summary>Cave: clear height at the crown per path point (one value = everywhere). Canyon: one value, the rim height above the highest floor.</summary>
    public IReadOnlyList<double> Heights { get; init; } = new[] { 4.5 };

    /// <summary>Jitter amplitude ρ as a fraction of the ring size (0.05 to 0.5).</summary>
    public double Roughness { get; init; } = 0.2;

    /// <summary>Cave floor half-width as a fraction of the half-width (0.3 to 1).</summary>
    public double FloorFlatness { get; init; } = 0.6;

    /// <summary>Canyon wall lean: extra half-width per metre of height (0 to 0.5).</summary>
    public double Lean { get; init; } = 0.15;

    /// <summary>Cave ring points (6 to 14).</summary>
    public int Sides { get; init; } = 8;

    /// <summary>Segments (hulls) per leg; null = from <see cref="Spacing"/>.</summary>
    public int? Segments { get; init; }

    /// <summary>The target station spacing, metres.</summary>
    public double Spacing { get; init; } = 3;

    /// <summary>The random seed.</summary>
    public long Seed { get; init; } = 1;

    /// <summary>The walkability limits.</summary>
    public WalkLimits Limits { get; init; } = new();

    /// <summary>Structural brushes already in the level that the audit also checks against.</summary>
    public IReadOnlyList<AuditShape> Existing { get; init; } = Array.Empty<AuditShape>();

    /// <summary>Textures, tile and flags of the generated brushes (Air is forced on).</summary>
    public HullBrushParams Brush { get; init; } = new();

    /// <summary>Checked between segments, so a long build stops when the script is cancelled.</summary>
    public System.Threading.CancellationToken Cancel { get; init; }
}

/// <summary>
/// The abutting hull chain behind <c>geo.tube</c> (<c>kit.cave</c>, <c>kit.canyon</c>; ORGANIC-GEOMETRY-SPEC
/// §3.2, with the deviations listed in its §10).
/// <para>
/// The path is cut into stations (§3.1). Each station carries a jittered ring of (right offset, height)
/// pairs that is EXACTLY PLANAR: its points are grid points on the vertical plane through the station
/// along the integer lattice direction (a, b) nearest the station's right vector (<see cref="Lattice"/>), so
/// every lateral offset is a whole number of lattice steps. Hull <c>i</c> is the convex hull of ring
/// <c>i</c> and ring <c>i+1</c>; neighbouring hulls therefore ABUT on one bit-identical cap (W4) — no
/// overlapping walls crossing at shallow angles, no vertex the compiler has to compute at a joint.
/// </para>
/// <para>
/// Floors: a ring's two floor corners sit at its station's floor height, which runs linearly along each
/// leg, so a level leg has one exact horizontal floor plane and a climbing leg a smooth slope (at most
/// <c>max_slope</c>) whose facets each belong to one brush and meet the next on exact shared edges (W6).
/// Every hull is verified before it is placed — convex and planar, every face fitted by the compiler's own
/// plane code within half its band (<see cref="CompilerFit"/>), rings that do not cross, a walkway of the
/// clear width inside its floor, the headroom over that walkway, and the near-coplanar audit
/// (<see cref="HullAudit"/>) against every earlier hull and the level's structural brushes — with a
/// deterministic retry: attempts 0–7 re-draw the later station's ring, then that ring falls back to no
/// roughness.
/// </para>
/// </summary>
public static class HullChain
{
    /// <summary>How many seeded re-draws a segment gets before the roughness fallback.</summary>
    public const int Attempts = 8;

    /// <summary>How many times one segment may push a forward-ring corner outward to clear near-flat folds.</summary>
    private const int FoldFixes = 12;

    /// <summary>The upward push for a ring corner near the crown that is nearly in line with its neighbours, grid steps (about 1.6 cm).</summary>
    private const int CrownStep = 16;



    /// <summary>How many times the legs are re-planned on the grid-aligned path before alignment gives up.</summary>
    private const int AlignPasses = 3;

    /// <summary>A turn at least this sharp (degrees) is a real bend: a leg between two of them may absorb a closed path's remainder.</summary>
    private const double BentTurn = 1;

    /// <summary>How far past 90° grid alignment may tip an authored corner, degrees.</summary>
    private const double AlignedTurnSlack = 0.5;

    /// <summary>The shortest station spacing, metres.</summary>
    public const double MinStationSpacing = 0.5;

    /// <summary>
    /// The toe of each wall: a ring point this far above the floor, set out by <see cref="Toe"/> lattice
    /// steps from the floor corner. It keeps the wall standing up from the floor edge (no overhang right
    /// at the floor), and because it is a whole number of lattice steps from its floor corner the floor-to-toe
    /// wall strip has the same shape on every ring of a straight leg (a planar quad, never a near-flat fold).
    /// </summary>
    internal const double ToeHeight = 0.5;

    /// <summary>The step floor corners move in, metres (see <see cref="RingPoints"/>).</summary>
    private const double FloorQuantum = 0.0625;

    /// <summary>How far every point of a ring must stay behind the neighbouring ring's plane, metres.</summary>
    private const double MinRingGap = 0.01;

    /// <summary>The largest lattice step (grid cells per axis) a ring direction may use: lateral positions quantise to at most 32·√2 / 1024 m.</summary>
    private const int MaxLattice = 32;

    /// <summary>Containment tolerance for walkway corners on a floor edge (lattice rounding moves edges by under 5 cm).</summary>
    private const double EdgeTolerance = 0.002;

    private enum RingKind
    {
        Floor,
        Toe,
        Arch,
        Rim,
    }

    private readonly record struct RingPoint(double A, double B, RingKind Kind);

    private sealed class Station
    {
        public Vec3d S;
        public Vec3d T;
        public Vec3d R;
        public Vec3d D;
        public double Turn;
        public double W;
        public double H;
        public int Leg;
        public bool PathPoint;
    }

    private enum Failure
    {
        Shape,
        Fit,
        Crossing,
        Width,
        Headroom,
        Audit,

        /// <summary>Its walls nearly line up with the walls of an existing organic piece (a tube started inside another on the same line).</summary>
        OrganicWalls,
    }

    /// <summary>Builds the chain; null with <paramref name="error"/> (a sentence + hint) on failure.</summary>
    public static TubeResult? Build(TubeOptions o, out GeoEditError? error)
    {
        ArgumentNullException.ThrowIfNull(o);
        error = Validate(o);
        if (error is not null)
        {
            return null;
        }

        OrganicPath? path = OrganicPath.Create(o.Path, 2, 90, out error);
        if (path is null)
        {
            return null;
        }

        int m = path.Points.Length;
        bool closed = m >= 4 && path.Points[0] == path.Points[^1];
        if (closed && path.ClosedTurn() > 90 + 1e-9)
        {
            error = new GeoEditError($"the closed path turns {OrganicPath.F(path.ClosedTurn())}° where it meets its start.",
                "Add a point to round the corner (at most 90° per point).");
            return null;
        }

        if (Count(o.HalfWidths, m, "widths", out error) || Count(o.Heights, o.Profile == TubeProfile.Canyon ? 1 : m, "heights", out error))
        {
            return null;
        }

        bool cave = o.Profile == TubeProfile.Cave;
        double rho = o.Roughness;
        double ff = cave ? o.FloorFlatness : 1;
        WalkLimits lim = o.Limits;
        double clear = lim.MinClearWidth;
        for (int i = 0; i < m; i++)
        {
            double w = o.HalfWidths.Count == 1 ? o.HalfWidths[0] : o.HalfWidths[i];
            double walk = 2 * w * ff * Math.Cos(Math.PI / 4) * (1 - (0.5 * rho));
            if (walk < clear - 1e-9)
            {
                error = new GeoEditError(
                    $"the floor at path point {i + 1} is {OrganicPath.F(2 * w * ff)} m wide; at a 90° bend with roughness {OrganicPath.F(rho)} that leaves {OrganicPath.F(walk)} m, under the {OrganicPath.F(clear)} m walkway.",
                    cave ? "Raise radius or floor_flatness, or lower roughness." : "Raise width, or lower roughness.");
                return null;
            }

            if (cave)
            {
                double h = o.Heights.Count == 1 ? o.Heights[0] : o.Heights[i];
                double cosTheta = Math.Min(1, clear / 2 / Math.Max(1e-9, w * (1 - rho)));
                double over = h * (1 - rho) * Math.Sqrt(1 - (cosTheta * cosTheta));
                if (over < lim.MinHeadroom - 1e-9)
                {
                    error = new GeoEditError(
                        $"the cave at path point {i + 1} is {OrganicPath.F(h)} m high; with roughness {OrganicPath.F(rho)} its arch can come down to {OrganicPath.F(over)} m over the walkway, under the {OrganicPath.F(lim.MinHeadroom)} m headroom.",
                        "Raise height, or lower roughness.");
                    return null;
                }
            }
        }

        if (!cave && o.Heights[0] < lim.MinHeadroom - 1e-9)
        {
            error = new GeoEditError($"'height' {OrganicPath.F(o.Heights[0])} m is under the {OrganicPath.F(lim.MinHeadroom)} m headroom.", "Raise height.");
            return null;
        }

        double ySpan = path.Points.Max(p => p.Y) - path.Points.Min(p => p.Y);

        // How far a ring reaches sideways at path point i: at a bend of β the next station must be further
        // than reach · tan(β/2) away, or the bend's (bisector) ring and its neighbour's ring would cross.
        double Reach(int i)
        {
            double w = o.HalfWidths.Count == 1 ? o.HalfWidths[0] : o.HalfWidths[i];
            return (cave ? w : w + (o.Lean * (o.Heights[0] + ySpan))) * (1 + rho);
        }

        double TurnAt(int i) => closed && (i == 0 || i == m - 1) ? path.ClosedTurn() : path.Turn(i);

        // ---- legs: segment counts -------------------------------------------------------------------
        // A sloped leg's floor is ONE exact plane: its stations sit at exact multiples of the leg's grid
        // delta (the end point moves by at most a few millimetres so the delta divides evenly), and the climb
        // between its landings splits into equal whole grid steps. Consecutive sloped floor faces are then
        // exactly coplanar, like the horizontal floors of a level leg (station-by-station snapping would leave
        // near-flat folds between them, which the compiler handles badly).
        int legs = path.Legs;
        var legN = new int[legs];
        var legLanding = new (bool Start, bool End)[legs];
        for (int pass = 0; ; pass++)
        {
            error = PlanLegs(path, o, lim, closed, TurnAt, Reach, legN, legLanding);
            if (error is not null)
            {
                return null;
            }

            Vec3d[] adjusted = AlignLegs(path, legN, closed);
            if (adjusted.SequenceEqual(path.Points))
            {
                break;
            }

            if (pass == AlignPasses)
            {
                error = new GeoEditError(
                    "the path's legs could not be aligned to the 1/1024 m grid: moving its points onto the grid keeps changing how many segments a leg needs.",
                    "Move a path point by a few centimetres, or set 'segments' (or 'spacing') explicitly.");
                return null;
            }

            // The authored path passed the 90° turn limit; the millimetres of alignment may tip an exactly square
            // corner over it by a hair, which the bend geometry tolerates (every hull is still verified).
            OrganicPath? aligned = OrganicPath.Create(adjusted, 2, 90 + AlignedTurnSlack, out error);
            if (aligned is null)
            {
                return null;
            }

            path = aligned;
        }

        int hulls = legN.Sum();
        if (hulls > OrganicLimits.MaxBulkHulls)
        {
            error = new GeoEditError($"the path needs {hulls} segments; the limit is {OrganicLimits.MaxBulkHulls}.", "Raise spacing, or split the path into several tubes.");
            return null;
        }

        // ---- stations ---------------------------------------------------------------------------------
        var stations = new List<Station>(hulls + 1);
        for (int j = 0; j < legs; j++)
        {
            Vec3d p = path.Points[j], q = path.Points[j + 1];
            int n = legN[j];
            for (int k = j == 0 ? 0 : 1; k <= n; k++)
            {
                bool pathPoint = k == 0 || k == n;
                int pointIndex = k == 0 ? j : j + 1;
                // The floor runs linearly between the landings (k = 1 .. n-1 when both ends turn); on a sloped
                // leg every station is an exact multiple of the leg's grid delta (see AlignLegs).
                int k0 = legLanding[j].Start ? 1 : 0, k1 = legLanding[j].End ? n - 1 : n;
                double fy = k <= k0 ? p.Y : k >= k1 ? q.Y : p.Y + ((q.Y - p.Y) * (k - k0) / (k1 - k0));
                Vec3d s = pathPoint ? (k == 0 ? p : q)
                    : new Vec3d(OrganicPath.SnapG(p.X + ((q.X - p.X) * k / n)), OrganicPath.SnapG(fy), OrganicPath.SnapG(p.Z + ((q.Z - p.Z) * k / n)));
                Vec3d t = pathPoint ? path.Heading(pointIndex) : path.Dir[j];
                double turn = pathPoint ? path.Turn(pointIndex) : 0;
                if (closed && pathPoint && (pointIndex == 0 || pointIndex == m - 1))
                {
                    // A closed loop: its start and end are one station on the bisector of the last and first legs.
                    t = path.Dir[^1].Add(path.Dir[0]).Normalized();
                    turn = path.ClosedTurn();
                }


                // The ring plane: the vertical plane through S along the lattice direction nearest the right
                // vector, so every ring point is an exact grid point ON it (an exactly planar ring).
                (int la, int lb) = Lattice(OrganicPath.Right(t));
                var d = new Vec3d(la * OrganicPath.G, 0, lb * OrganicPath.G);
                Vec3d r = d.Normalized();
                double along = path.Cumulative[j] + (path.Run[j] * k / n);
                stations.Add(new Station
                {
                    S = s,
                    T = new Vec3d(-r.Z, 0, r.X),
                    R = r,
                    D = d,
                    Turn = turn,
                    W = path.Interpolate(o.HalfWidths, along),
                    H = cave ? path.Interpolate(o.Heights, along) : o.Heights[0],
                    Leg = j,
                    PathPoint = pathPoint,
                });
            }
        }

        double? topY = cave ? null : OrganicPath.RoundQ(path.Points.Max(p => p.Y) + o.Heights[0]);
        double toe = Toe(o.HalfWidths.Min());

        // ---- rings and hulls --------------------------------------------------------------------------
        HullBrushParams bp = Clone(o.Brush);
        var result = new TubeResult { TopY = topY, Length = path.Length, ClearWidth = clear };
        var placed = new List<AuditShape>(o.Existing);
        var rings = new Dictionary<(int Station, int Attempt), List<Vec3d>>();

        List<Vec3d> RingAt(int s, int attempt)
        {
            if (!rings.TryGetValue((s, attempt), out List<Vec3d>? ring))
            {
                ring = RingPoints(o, stations[s], s, attempt, toe, topY);
                OpenRing(ring, stations[s]);
                rings[(s, attempt)] = ring;
            }

            return ring;
        }

        var accepted = new int[stations.Count];
        for (int g = 0; g < hulls; g++)
        {
            o.Cancel.ThrowIfCancellationRequested();
            Station a = stations[g], b = stations[g + 1];
            (Failure Kind, string Detail)? last = null;

            // The diagnosis a seed cannot fix wins over whatever the last attempt happened to run into.
            (Failure Kind, string Detail)? wallClash = null;
            bool done = false;
            for (int attempt = 0; attempt <= Attempts && !done; attempt++)
            {
                List<Vec3d> back = RingAt(g, g == 0 ? attempt : accepted[g]);

                // A closed loop's last segment ends on the first segment's own start ring (one identical cap).
                bool closing = closed && g == hulls - 1;
                List<Vec3d> fwd = closing ? RingAt(0, accepted[0]) : RingAt(g + 1, attempt);
                last = Verify(back, fwd, !closing, bp, a, b, clear, lim.MinHeadroom, placed, $"segment {g + 1}",
                    out Brush? brush, out HullResult? hull, out List<Vec3d[]>? floor, out List<Vec3d> fwdUsed);
                for (int fix = 0; last is null && closed && g == hulls - 2; fix++)
                {
                    // Look ahead: a closed loop's last segment has both rings fixed once this one is placed, so
                    // this ring must also let the closing segment pass. A near-flat fold there is opened on this
                    // ring (the closing segment's start), and this segment is checked again with the moved ring.
                    var withThis = new List<AuditShape>(placed) { AuditShape.FromHull(hull!, $"segment {g + 1}") };
                    (Failure, string)? ahead = VerifyOnce(fwdUsed, RingAt(0, accepted[0]), bp, b, stations[g + 2], clear, lim.MinHeadroom, withThis,
                        $"segment {g + 2}", out _, out _, out _, out AuditConflict? fold, out AuditShape? closingShape);
                    if (ahead is null)
                    {
                        break;
                    }

                    var moved = new List<Vec3d>(fwdUsed);
                    if (fold is null || fix == FoldFixes || !PushOut(moved, fold, closingShape!, withThis, b))
                    {
                        last = ahead;
                        break;
                    }

                    last = Verify(back, moved, true, bp, a, b, clear, lim.MinHeadroom, placed, $"segment {g + 1}", out brush, out hull, out floor, out fwdUsed);
                }

                if (last is { Kind: Failure.OrganicWalls })
                {
                    wallClash ??= last;
                }

                if (last is null)
                {
                    if (!closing)
                    {
                        // The ring may have had corners pushed out to clear a near-flat fold: the next segment starts on it.
                        rings[(g + 1, attempt)] = fwdUsed;
                    }

                    if (g == 0)
                    {
                        accepted[0] = attempt;
                    }

                    accepted[g + 1] = attempt;
                    result.Retries += attempt;
                    result.Brushes.Add(brush!);
                    result.FloorFaces.AddRange(floor!);
                    placed.Add(AuditShape.FromHull(hull!, $"segment {g + 1}"));
                    done = true;
                }
            }

            if (!done)
            {
                error = FailureError(g + 1, (wallClash ?? last)!.Value, clear, lim.MinHeadroom, cave);
                return null;
            }
        }

        // ---- record data ------------------------------------------------------------------------------
        foreach (Station st in stations)
        {
            double height = cave ? st.H : topY!.Value - st.S.Y;
            result.Stations.Add(new TubeStation(st.S, st.S.Y, 2 * st.W * ff, height, OrganicPath.Yaw(st.T)));
        }

        for (int j = 0; j < legs; j++)
        {
            Vec3d p = path.Points[j], q = path.Points[j + 1];
            if (q.Y != p.Y)
            {
                result.Climbs.Add(new TubeClimb(p, q, q.Y - p.Y, path.Run[j], OrganicPath.Yaw(path.Dir[j]), clear));
            }
        }

        result.AirCount = result.Brushes.Count;
        result.ComputeBounds();
        return result;
    }

    /// <summary>Segments and landings per leg (errors name the leg and what to change).</summary>
    private static GeoEditError? PlanLegs(OrganicPath path, TubeOptions o, WalkLimits lim, bool closed, Func<int, double> turnAt, Func<int, double> reach,
        int[] legN, (bool Start, bool End)[] legLanding)
    {
        for (int j = 0; j < path.Legs; j++)
        {
            double run = path.Run[j];
            double dy = path.Points[j + 1].Y - path.Points[j].Y;
            double slope = Math.Abs(dy) / run;
            string limit = OrganicPath.F(Math.Atan(lim.MaxSlope) * 180 / Math.PI);
            if (slope > lim.MaxSlope + 1e-9)
            {
                return new GeoEditError(
                    $"the path climbs {OrganicPath.F(Math.Atan(slope) * 180 / Math.PI)}° between points {j + 1} and {j + 2} ({OrganicPath.F(Math.Abs(dy))} m over {OrganicPath.F(run)} m); the limit is {limit}°.",
                    $"Lengthen the leg to at least {OrganicPath.F(Math.Abs(dy) / lim.MaxSlope)} m, or use kit.pit / kit.stairs for a drop.");
            }

            // Either ring next to a bend of β reaches across the other's plane by up to reach · tan(β/2).
            double bend = Math.Max(reach(j), reach(j + 1)) * Math.Tan(Math.Max(turnAt(j), turnAt(j + 1)) * Math.PI / 360);
            double endSpacing = Math.Max(MinStationSpacing, bend + 0.25);
            int most = Math.Max(1, (int)Math.Floor((run / endSpacing) + 1e-9));
            int sharp = turnAt(j) >= turnAt(j + 1) ? j : j + 1;
            if (o.Segments is int seg && seg > most)
            {
                return new GeoEditError(
                    $"'segments' = {seg} is too many for the leg between points {j + 1} and {j + 2} ({OrganicPath.F(run)} m): {(turnAt(sharp) > 0 ? $"the {OrganicPath.F(turnAt(sharp))}° turn at point {sharp + 1} keeps" : "it keeps")} its stations at least {OrganicPath.F(endSpacing)} m apart (at most {most}).",
                    $"Use segments = {most} or fewer, or use spacing instead.");
            }

            int desired = o.Segments ?? Math.Min(Math.Max(1, (int)Math.Ceiling((run / o.Spacing) - 1e-9)), most);
            if (dy == 0)
            {
                legN[j] = desired;
                legLanding[j] = (false, false);
                continue;
            }

            // A sloped leg keeps a level landing (one segment at the bend's height) at each end that turns, so
            // every segment touching a bend has a flat floor: a sloped floor between two differently oriented
            // rings would be a twisted, folded quad. The climb between the landings splits into equal whole
            // grid steps, so its floor is one exact plane.
            bool startLanding = turnAt(j) > 1e-6, endLanding = turnAt(j + 1) > 1e-6;
            int landings = (startLanding ? 1 : 0) + (endLanding ? 1 : 0);
            long units = (long)Math.Round(Math.Abs(dy) / OrganicPath.G);
            bool Fits(int n) => n > landings && n <= most && units % (n - landings) == 0
                && Math.Abs(dy) <= (run * (n - landings) / n * lim.MaxSlope) + 1e-9;

            int chosen = -1;
            if (o.Segments is int given)
            {
                chosen = Fits(given) ? given : -1;
                if (chosen < 0)
                {
                    int[] valid = Enumerable.Range(landings + 1, Math.Max(0, most - landings)).Where(Fits).ToArray();
                    return new GeoEditError(
                        $"'segments' = {given} does not suit the leg between points {j + 1} and {j + 2}: it climbs {OrganicPath.F(Math.Abs(dy))} m{(landings > 0 ? $" with {landings} level landing{(landings == 1 ? string.Empty : "s")} at its turn{(landings == 1 ? string.Empty : "s")}" : string.Empty)}, which must split into equal grid steps within the {limit}° limit.",
                        valid.Length > 0 ? $"Use segments = {string.Join(" or ", valid.OrderBy(v => Math.Abs(v - given)).Take(2))}, or use spacing instead." : "Lengthen the leg, or straighten its turns.");
                }
            }
            else
            {
                for (int n = Math.Max(desired, landings + 1); n <= most && chosen < 0; n++)
                {
                    chosen = Fits(n) ? n : -1;
                }

                for (int n = desired - 1; n > landings && chosen < 0; n--)
                {
                    chosen = Fits(n) ? n : -1;
                }
            }

            if (chosen < 0)
            {
                return new GeoEditError(
                    $"the leg between points {j + 1} and {j + 2} climbs {OrganicPath.F(Math.Abs(dy))} m over {OrganicPath.F(run)} m{(landings > 0 ? $" and turns at {(landings == 2 ? "both ends" : "one end")}; with a level landing at each turn" : ";")} it cannot climb within the {limit}° limit.",
                    "Lengthen the leg, climb less on it, or straighten the turn.");
            }

            legN[j] = chosen;
            legLanding[j] = (startLanding, endLanding);
        }

        return null;
    }

    /// <summary>
    /// Moves the end of every leg (by at most half a grid step per segment of that leg on each axis, a few
    /// millimetres) so the leg's horizontal grid delta divides evenly by its segment count; every later point
    /// moves with it, so every leg keeps its authored delta up to that rounding (an axis-aligned straight run stays
    /// exactly in line). Every station of a leg is then an exact multiple of that delta: the floor edges and wall strips
    /// of neighbouring segments are exactly in line or clearly folded, never folded by a snapping error. The
    /// moves add up along the path (on a long path of a hundred segments or more, the end can sit a centimetre
    /// or two from where it was authored). A closed path keeps its final point on its start: its closing leg's
    /// remainder is absorbed by lengthening or shortening earlier legs by whole multiples of their own segment
    /// counts — legs that turn at both ends first, so no straight run gains a kink — choosing the smallest
    /// total move.
    /// </summary>
    private static Vec3d[] AlignLegs(OrganicPath path, int[] legN, bool closed)
    {
        int m = path.Points.Length;
        double TurnAt(int i) => closed && (i == 0 || i == m - 1) ? path.ClosedTurn() : path.Turn(i);
        var bent = new bool[path.Legs];
        for (int j = 0; j < path.Legs; j++)
        {
            bent[j] = TurnAt(j) >= BentTurn && TurnAt(j + 1) >= BentTurn;
        }

        long[] xs = AlignAxis(path.Points.Select(p => p.X).ToArray(), legN, closed, bent);
        long[] zs = AlignAxis(path.Points.Select(p => p.Z).ToArray(), legN, closed, bent);
        var pts = new Vec3d[m];
        for (int i = 0; i < m; i++)
        {
            pts[i] = new Vec3d(xs[i] * OrganicPath.G, path.Points[i].Y, zs[i] * OrganicPath.G);
        }

        return pts;
    }

    /// <summary>One horizontal axis of <see cref="AlignLegs"/>, in grid units.</summary>
    private static long[] AlignAxis(double[] coords, int[] legN, bool closed, bool[] bent)
    {
        int m = coords.Length;
        long[] a = coords.Select(v => (long)Math.Round(v / OrganicPath.G)).ToArray();
        long[] x = new long[m];
        x[0] = a[0];
        for (int i = 1; i < m; i++)
        {
            long n = Math.Max(1, legN[i - 1]);
            x[i] = x[i - 1] + (n * (long)Math.Round((double)(a[i] - a[i - 1]) / n, MidpointRounding.AwayFromZero));
        }

        if (!closed)
        {
            return x;
        }

        // The closing leg (m-2 → m-1, the start) must divide by its count nl: its remainder r is taken up by the
        // earlier legs, each moving by a whole multiple e_j of its own count (Σ e_j·n_j ≡ r mod nl). A small
        // dynamic programme over the residues mod nl picks the moves of least total length.
        int legs = m - 1;
        long nl = Math.Max(1, legN[legs - 1]);
        x[m - 1] = a[0];
        long r = (((a[0] - x[m - 2]) % nl) + nl) % nl;
        if (r == 0)
        {
            return x;
        }

        foreach (bool bentOnly in new[] { true, false })
        {
            for (long reach = 1; ; reach = Math.Min(nl, reach * 2))
            {
                long[]? moves = Absorb(legN, legs - 1, j => !bentOnly || bent[j], nl, r, reach);
                if (moves is not null)
                {
                    long shift = 0;
                    for (int i = 1; i < m - 1; i++)
                    {
                        shift += moves[i - 1] * Math.Max(1, legN[i - 1]);
                        x[i] += shift;
                    }

                    return x;
                }

                if (reach >= nl)
                {
                    break;
                }
            }
        }

        // Unreachable: the remainder shares no divisor with the counts. The closing leg keeps its remainder.
        return x;
    }

    /// <summary>
    /// Moves e_j ∈ [−reach, reach] for legs 0 .. count−1 (0 for legs <paramref name="movable"/> rejects) with
    /// Σ e_j·n_j ≡ r (mod nl) and the least Σ |e_j|·n_j (deterministic on ties), or null when none exists.
    /// </summary>
    private static long[]? Absorb(int[] legN, int count, Func<int, bool> movable, long nl, long r, long reach)
    {
        const long Inf = long.MaxValue;
        var cost = new long[nl];
        Array.Fill(cost, Inf);
        cost[0] = 0;
        var pick = new long[count, nl];
        var from = new long[count, nl];
        for (int j = 0; j < count; j++)
        {
            long n = Math.Max(1, legN[j]);
            long span = movable(j) ? 2 * reach : 0;
            var next = new long[nl];
            Array.Fill(next, Inf);
            for (long res = 0; res < nl; res++)
            {
                if (cost[res] == Inf)
                {
                    continue;
                }

                for (long k = 0; k <= span; k++)
                {
                    long e = (k + 1) / 2 * (k % 2 == 1 ? 1 : -1); // 0, 1, -1, 2, -2, ...
                    long to = ((((res + (e * n)) % nl) + nl) % nl);
                    long c = cost[res] + (Math.Abs(e) * n);
                    if (c < next[to])
                    {
                        next[to] = c;
                        pick[j, to] = e;
                        from[j, to] = res;
                    }
                }
            }

            cost = next;
        }

        if (cost[r] == Inf)
        {
            return null;
        }

        var moves = new long[count];
        long at = r;
        for (int j = count - 1; j >= 0; j--)
        {
            moves[j] = pick[j, at];
            at = from[j, at];
        }

        return moves;
    }

    // ---- rings ----------------------------------------------------------------------------------------

    /// <summary>
    /// The ring of station <paramref name="s"/> as world points, drawn from stream (seed, station, attempt);
    /// attempt <see cref="Attempts"/> is the no-roughness fallback. Every draw happens whatever the
    /// roughness, so a ring's shape depends on nothing but its stream. Lateral offsets become whole lattice
    /// steps (exact grid points on the ring plane); floor corners sit at the station floor, the toes
    /// <see cref="ToeHeight"/> above it, the canyon rim at the common top.
    /// </summary>
    private static List<Vec3d> RingPoints(TubeOptions o, Station st, int s, int attempt, double toe, double? topY)
    {
        double rho = attempt >= Attempts ? 0 : o.Roughness;
        var raw = new List<RingPoint>(o.Sides + 2);
        var rng = new OrganicRandom(o.Seed, s, attempt);
        double w = st.W;
        if (o.Profile == TubeProfile.Cave)
        {
            double ff = o.FloorFlatness;
            raw.Add(new RingPoint(-ff * w * (1 + (0.5 * rho * rng.Range(-1, 1))), 0, RingKind.Floor));
            raw.Add(new RingPoint(ff * w * (1 + (0.5 * rho * rng.Range(-1, 1))), 0, RingKind.Floor));
            int sides = o.Sides;
            for (int j = 1; j <= sides - 2; j++)
            {
                double theta = j * Math.PI / (sides - 1);
                double k = 1 + (rho * rng.Range(-1, 1));
                raw.Add(new RingPoint(w * Math.Cos(theta) * k, st.H * Math.Sin(theta) * k, RingKind.Arch));
            }
        }
        else
        {
            double depth = topY!.Value - st.S.Y;
            raw.Add(new RingPoint(-w * (1 + (0.5 * rho * rng.Range(-1, 1))), 0, RingKind.Floor));
            raw.Add(new RingPoint(w * (1 + (0.5 * rho * rng.Range(-1, 1))), 0, RingKind.Floor));
            foreach (int side in new[] { -1, 1 })
            {
                double mid = rng.Range(0.35, 0.65);
                double km = 1 + (rho * rng.Range(-1, 1));
                double kr = 1 + (rho * rng.Range(-1, 1));
                raw.Add(new RingPoint(side * (w + (o.Lean * mid * depth)) * km, mid * depth, RingKind.Arch));
                raw.Add(new RingPoint(side * (w + (o.Lean * depth)) * kr, depth, RingKind.Rim));
            }
        }

        double step = st.D.Length;
        int toeSteps = Math.Max(1, (int)Math.Round(toe / step, MidpointRounding.AwayFromZero));
        var pts = new List<Vec3d>(raw.Count + 2);
        Vec3d At(double lattice, double y) =>
            new(OrganicPath.SnapG(st.S.X + (lattice * st.D.X)), y, OrganicPath.SnapG(st.S.Z + (lattice * st.D.Z)));

        // Floor corners move in whole steps of about FloorQuantum, so the floor edges (and the wall strips above
        // them) of neighbouring segments on a straight leg are either exactly in line or clearly folded.
        int floorSteps = Math.Max(1, (int)Math.Round(FloorQuantum / step, MidpointRounding.AwayFromZero));
        foreach (RingPoint rp in raw)
        {
            double lattice = rp.Kind == RingKind.Floor
                ? Math.Round(rp.A / step / floorSteps, MidpointRounding.AwayFromZero) * floorSteps
                : Math.Round(rp.A / step, MidpointRounding.AwayFromZero);
            switch (rp.Kind)
            {
                case RingKind.Floor:
                    pts.Add(At(lattice, st.S.Y));
                    pts.Add(At(lattice + (Math.Sign(rp.A) * toeSteps), OrganicPath.SnapG(st.S.Y + ToeHeight)));
                    break;
                case RingKind.Rim:
                    pts.Add(At(lattice, topY!.Value));
                    break;
                default:
                    pts.Add(At(lattice, OrganicPath.SnapG(st.S.Y + rp.B)));
                    break;
            }
        }

        return pts;
    }

    /// <summary>
    /// Pushes any wall corner of a ring that is nearly in line with its neighbours (its outline turns there by
    /// less than <see cref="FoldRepair.MinRingTurnDegrees"/>) outward until it clearly turns: sideways by a
    /// lattice step on the side walls (a canyon rim stays on the common top), up by a few centimetres near the
    /// crown. Floor corners stay. A ring that still has such a corner is re-drawn by the verification.
    /// </summary>
    private static void OpenRing(List<Vec3d> ring, Station st)
    {
        double len = st.D.Length;
        double top = ring.Max(q => q.Y);
        bool canyonTop = ring.Count(q => q.Y == top) >= 2;
        for (int pass = 0; pass < 16; pass++)
        {
            var frame = ring.Select(p => (p.Sub(st.S).Dot(st.D) / len, p.Y)).ToList();
            List<Vec3d> outline = HullDeform.ConvexCorners(frame.Distinct().Select(f => new Vec3d(f.Item1, f.Item2, 0)).ToList(), 0, 1);
            int worst = -1;
            double least = FoldRepair.MinRingTurnDegrees;
            for (int c = 0; c < outline.Count; c++)
            {
                Vec3d a = outline[(c + outline.Count - 1) % outline.Count], p = outline[c], b = outline[(c + 1) % outline.Count];
                double ax = p.X - a.X, ay = p.Y - a.Y, bx = b.X - p.X, by = b.Y - p.Y;
                double turn = Math.Abs(Math.Atan2((ax * by) - (ay * bx), (ax * bx) + (ay * by))) * 180 / Math.PI;
                int index = frame.IndexOf((p.X, p.Y));
                if (turn < least && index != 0 && index != 2)
                {
                    least = turn;
                    worst = index;
                }
            }

            if (worst < 0)
            {
                return;
            }

            Vec3d q = ring[worst];
            double steps = frame[worst].Item1 / len;
            if (Math.Abs(steps) >= 2 || (canyonTop && q.Y == top))
            {
                double sign = steps >= 0 ? 1 : -1;
                ring[worst] = new Vec3d(q.X + (sign * st.D.X), q.Y, q.Z + (sign * st.D.Z));
            }
            else
            {
                ring[worst] = q with { Y = OrganicPath.SnapG(q.Y + (CrownStep * OrganicPath.G)) };
            }
        }
    }

    // ---- verification ---------------------------------------------------------------------------------

    /// <summary>
    /// Builds and checks the hull of two rings. When <paramref name="fwdMovable"/>, a near-flat fold (with a placed
    /// hull or inside this one) is cleared by pushing a wall corner of the forward ring that one of the folded faces
    /// owns outward (<see cref="PushOut"/>) and checking again, at most <see cref="FoldFixes"/> times;
    /// <paramref name="fwdUsed"/> is the forward ring as finally used.
    /// </summary>
    private static (Failure, string)? Verify(List<Vec3d> back, List<Vec3d> fwd, bool fwdMovable, HullBrushParams bp, Station a, Station b, double clear, double headroom,
        List<AuditShape> placed, string label, out Brush? brush, out HullResult? hull, out List<Vec3d[]>? floorFaces, out List<Vec3d> fwdUsed)
    {
        fwdUsed = new List<Vec3d>(fwd);
        for (int fix = 0; ; fix++)
        {
            (Failure, string)? r = VerifyOnce(back, fwdUsed, bp, a, b, clear, headroom, placed, label, out brush, out hull, out floorFaces, out AuditConflict? fold, out AuditShape? shape);
            if (fold is null || !fwdMovable || fix == FoldFixes || !PushOut(fwdUsed, fold, shape!, placed, b))
            {
                return r;
            }
        }
    }

    /// <summary>
    /// Opens a near-flat fold by moving one forward-ring wall corner of a folded face outward (never a floor
    /// corner): sideways along the ring's lattice direction, away from the station, or straight up, whichever
    /// tilts the face more (a canyon rim corner stays on the common top). See <see cref="FoldRepair"/>.
    /// </summary>
    private static bool PushOut(List<Vec3d> ring, AuditConflict fold, AuditShape shape, List<AuditShape> placed, Station st)
    {
        double top = ring.Max(q => q.Y);
        bool canyonTop = ring.Count(q => q.Y == top) >= 2;
        return FoldRepair.Push(ring, fold, shape, placed, i =>
        {
            if (i == 0 || i == 2)
            {
                return Array.Empty<Vec3d>();
            }

            Vec3d p = ring[i];
            double sign = p.Sub(st.S).Dot(st.D) >= 0 ? 1 : -1;
            var side = new Vec3d(sign * st.D.X, 0, sign * st.D.Z);
            return canyonTop && p.Y == top ? new[] { side } : new[] { side, new Vec3d(0, OrganicPath.G, 0) };
        });
    }

    private static (Failure, string)? VerifyOnce(List<Vec3d> back, List<Vec3d> fwd, HullBrushParams bp, Station a, Station b, double clear, double headroom,
        List<AuditShape> placed, string label, out Brush? brush, out HullResult? hull, out List<Vec3d[]>? floorFaces, out AuditConflict? fold, out AuditShape? audited)
    {
        brush = null;
        hull = null;
        floorFaces = null;
        fold = null;
        audited = null;

        // A ring with a corner nearly in line with its neighbours would fold every hull built on it near-flat
        // across that corner; such a ring is re-drawn.
        if (FoldRepair.SmallestTurn(fwd.Select(p => (p.Sub(b.S).Dot(b.D) / b.D.Length, p.Y)).ToList()) < FoldRepair.MinRingTurnDegrees
            || FoldRepair.SmallestTurn(back.Select(p => (p.Sub(a.S).Dot(a.D) / a.D.Length, p.Y)).ToList()) < FoldRepair.MinRingTurnDegrees)
        {
            return (Failure.Audit, "a ring corner nearly in line with its neighbours");
        }

        // Each ring must lie wholly behind the next ring's plane (and vice versa), or the caps cross.
        foreach (Vec3d p in back)
        {
            if (p.Sub(b.S).Dot(b.T) > -MinRingGap)
            {
                return (Failure.Crossing, string.Empty);
            }
        }

        foreach (Vec3d p in fwd)
        {
            if (p.Sub(a.S).Dot(a.T) < MinRingGap)
            {
                return (Failure.Crossing, string.Empty);
            }
        }

        var pts = new List<Vec3d>(back.Count + fwd.Count);
        pts.AddRange(back);
        pts.AddRange(fwd);
        HullBrush.TryBuild(pts, bp, 0, out brush, out HullResult? built, out HullBrushError? err);
        hull = built;
        if (err is not null || brush is null || built is null)
        {
            return (Failure.Shape, err?.Message ?? "the hull could not be built");
        }

        GeometryReport report = GeometryCheck.Check(brush);
        if (!report.Closed || !report.Convex || !report.Planar)
        {
            return (Failure.Shape, string.Join(" ", report.Issues));
        }

        if (built.Simplified > 0)
        {
            // A ring corner dropped as a sliver would make this cap differ from the neighbour's.
            return (Failure.Shape, "a ring corner made a sliver face");
        }

        if (!CompilerFit.Fits(brush))
        {
            return (Failure.Fit, OrganicPath.F(CompilerFit.WorstError(brush) * 1000));
        }

        // The floor: the hull faces made only of the four floor corners (one quad on a straight leg).
        var floorCorners = new HashSet<Vec3d> { back[0], back[2], fwd[0], fwd[2] };
        floorFaces = new List<Vec3d[]>();
        foreach (HullFace f in built.Faces)
        {
            if (f.Normal.Y < -0.5 && f.Corners.All(c => floorCorners.Contains(built.Vertices[c])))
            {
                floorFaces.Add(f.Corners.Select(c => built.Vertices[c]).ToArray());
            }
        }

        if (floorFaces.Count == 0 || floorCorners.Any(c => !built.Vertices.Contains(c)))
        {
            return (Failure.Shape, "it has no floor of its four floor corners");
        }

        Vec3d[] floorQuad = { back[0], back[2], fwd[2], fwd[0] };
        var strip = new List<Vec3d>();
        foreach (Station st in new[] { a, b })
        {
            if (clear > 0)
            {
                double h = clear / 2 / Math.Cos(st.Turn * Math.PI / 360);
                strip.Add(st.S.Add(st.R.Scale(h)));
                strip.Add(st.S.Sub(st.R.Scale(h)));
            }
            else
            {
                strip.Add(st.S);
            }
        }

        foreach (Vec3d p in strip)
        {
            if (!TubeResult.Inside(floorQuad, p.X, p.Z, EdgeTolerance))
            {
                return (Failure.Width, string.Empty);
            }
        }

        if (headroom > 0)
        {
            // Each walkway corner lies on a station's floor edge: measure from that station's floor.
            double lowest = double.PositiveInfinity;
            for (int i = 0; i < strip.Count; i++)
            {
                Vec3d p = strip[i];
                double floorY = i < strip.Count / 2 ? a.S.Y : b.S.Y;
                foreach (HullFace f in built.Faces)
                {
                    if (f.Normal.Y > 1e-9)
                    {
                        double y = -(f.Offset + (f.Normal.X * p.X) + (f.Normal.Z * p.Z)) / f.Normal.Y;
                        lowest = Math.Min(lowest, y - floorY);
                    }
                }
            }

            if (lowest < headroom - 1e-6)
            {
                return (Failure.Headroom, OrganicPath.F(lowest));
            }
        }

        AuditShape shape = AuditShape.FromHull(built, label);
        IReadOnlyList<AuditConflict> conflicts = HullAudit.Conflicts(placed, shape);
        if (conflicts.Count > 0)
        {
            if (HullAudit.OrganicWallClash(placed, shape, conflicts) is { } clash)
            {
                return (Failure.OrganicWalls, $"its walls nearly line up with the walls of {placed[clash.PlacedIndex].Organic}: {clash}");
            }

            return (Failure.Audit, conflicts[0].ToString());
        }

        IReadOnlyList<AuditConflict> folds = HullAudit.Folds(placed, shape);
        if (folds.Count == 0)
        {
            folds = HullAudit.SelfFolds(shape);
        }

        if (folds.Count > 0)
        {
            fold = folds[0];
            audited = shape;
            return (Failure.Audit, "a near-flat fold: " + folds[0]);
        }

        return null;
    }

    private static GeoEditError FailureError(int segment, (Failure Kind, string Detail) f, double clear, double headroom, bool cave) => f.Kind switch
    {
        Failure.Audit => new GeoEditError($"could not place segment {segment} without a near-coplanar face ({f.Detail}).", RockGen.NearCoplanarHint(f.Detail, "the path")),
        Failure.OrganicWalls => new GeoEditError($"could not place segment {segment} without a near-coplanar face ({f.Detail}).",
            "A tube that starts inside another one on the same line runs its walls along the other's walls. Join it at an angle instead (turn its first leg at least 30° off the other tube's heading, starting 1 m inside it), or extend the existing tube: add the points to its path and shape it with per-point radii / heights (a canyon: widths)."),
        Failure.Headroom => new GeoEditError($"could not place segment {segment} with {OrganicPath.F(headroom)} m headroom over its walkway (lowest {f.Detail} m).",
            "Raise height, or lower roughness."),
        Failure.Width => new GeoEditError($"could not place segment {segment} with a {OrganicPath.F(clear)} m walkway on its floor.",
            cave ? "Raise radius or floor_flatness, lower roughness, or soften the bend." : "Raise width, lower roughness, or soften the bend."),
        Failure.Fit => new GeoEditError($"could not place segment {segment} with faces the compiler can fit (a face plane would miss its corners by {f.Detail} mm).",
            "Build it closer to the origin (the compiler's single-precision planes lose accuracy far out), or change seed."),
        Failure.Crossing => new GeoEditError($"could not place segment {segment}: its rings cross (a turn is too sharp for the station spacing).",
            "Soften the turn, lengthen the legs beside it, or narrow the tube."),
        _ => new GeoEditError($"could not build segment {segment} as a convex brush ({f.Detail}).", "Change seed or roughness."),
    };

    /// <summary>
    /// The integer lattice direction (a, b) — coprime, |a|, |b| ≤ <see cref="MaxLattice"/> — nearest to the
    /// horizontal direction <paramref name="r"/> (ties: the shorter step). Grid points S + m·(a, b)·G all lie
    /// exactly on the vertical plane through S along it.
    /// </summary>
    internal static (int A, int B) Lattice(Vec3d r)
    {
        (int A, int B) best = (1, 0);
        double bestCos = double.NegativeInfinity, bestLen = double.PositiveInfinity;
        for (int a = -MaxLattice; a <= MaxLattice; a++)
        {
            for (int b = -MaxLattice; b <= MaxLattice; b++)
            {
                if ((a == 0 && b == 0) || Gcd(Math.Abs(a), Math.Abs(b)) != 1)
                {
                    continue;
                }

                double len = Math.Sqrt((a * a) + (b * b));
                double c = ((a * r.X) + (b * r.Z)) / len;
                if (c > bestCos + 1e-12 || (Math.Abs(c - bestCos) <= 1e-12 && len < bestLen))
                {
                    best = (a, b);
                    bestCos = c;
                    bestLen = len;
                }
            }
        }

        return best;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private static double Toe(double w) => Math.Clamp(0.08 * w, 0.2, 1.0);

    // ---- validation -----------------------------------------------------------------------------------

    private static GeoEditError? Validate(TubeOptions o)
    {
        if (!(o.Roughness >= 0.05 && o.Roughness <= 0.5))
        {
            return Range("roughness", o.Roughness, 0.05, 0.5);
        }

        if (o.Profile == TubeProfile.Cave && !(o.FloorFlatness >= 0.3 && o.FloorFlatness <= 1))
        {
            return Range("floor_flatness", o.FloorFlatness, 0.3, 1);
        }

        if (o.Profile == TubeProfile.Canyon && !(o.Lean >= 0 && o.Lean <= 0.5))
        {
            return Range("lean", o.Lean, 0, 0.5);
        }

        if (o.Sides < 6 || o.Sides > 14)
        {
            return new GeoEditError($"'sides' must be 6 to 14 (got {o.Sides}).", $"Nearest valid value: {Math.Clamp(o.Sides, 6, 14)}.");
        }

        if (!(o.Spacing >= MinStationSpacing) || !double.IsFinite(o.Spacing))
        {
            return new GeoEditError($"'spacing' must be at least {OrganicPath.F(MinStationSpacing)} metres (got {OrganicPath.F(o.Spacing)}).", "For example spacing = 4.");
        }

        if (o.Segments is int seg && (seg < 1 || seg > OrganicLimits.MaxBulkHulls))
        {
            return new GeoEditError($"'segments' must be 1 to {OrganicLimits.MaxBulkHulls} (got {seg}).", $"Nearest valid value: {Math.Clamp(seg, 1, OrganicLimits.MaxBulkHulls)}.");
        }

        foreach ((string key, IReadOnlyList<double> values) in new[] { ("widths", o.HalfWidths), ("heights", o.Heights) })
        {
            foreach (double v in values)
            {
                if (!double.IsFinite(v) || v <= 0 || v > 1024)
                {
                    return new GeoEditError($"every {key[..^1]} must be greater than 0 and at most 1024 metres (got {OrganicPath.F(v)}).", null);
                }
            }
        }

        return null;
    }

    private static bool Count(IReadOnlyList<double> values, int points, string key, out GeoEditError? error)
    {
        error = null;
        if (values.Count == 1 || values.Count == points)
        {
            return false;
        }

        error = new GeoEditError($"'{key}' lists {values.Count} values for {points} path points.", "Give one value per path point.");
        return true;
    }

    internal static GeoEditError Range(string key, double v, double lo, double hi) =>
        new($"'{key}' must be {OrganicPath.F(lo)} to {OrganicPath.F(hi)} (got {OrganicPath.F(v)}).",
            $"Nearest valid value: {OrganicPath.F(double.IsFinite(v) ? Math.Clamp(v, lo, hi) : lo)}.");

    internal static HullBrushParams Clone(HullBrushParams p) => new()
    {
        Air = true,
        Detail = p.Detail,
        Geoable = p.Geoable,
        EmitsSteam = p.EmitsSteam,
        Life = p.Life,
        Floor = p.Floor,
        Wall = p.Wall,
        Ceiling = p.Ceiling,
        TileMetres = p.TileMetres,
        Snap = HullBrush.Grid,
    };
}
