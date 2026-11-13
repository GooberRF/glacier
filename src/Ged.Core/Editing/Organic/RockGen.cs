using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing.Organic;

/// <summary>The output of a rock or pit builder: brushes in emission order and their bounds.</summary>
public sealed class RockResult
{
    /// <summary>The brushes (UID 0; the caller assigns UIDs).</summary>
    public List<Brush> Brushes { get; } = new();

    /// <summary>Re-draws it took to place every piece.</summary>
    public int Retries { get; set; }

    /// <summary>The bounds of every brush.</summary>
    public Vec3d Min { get; set; }

    /// <summary>The bounds of every brush.</summary>
    public Vec3d Max { get; set; }

    /// <summary>Pit: the flat floor height.</summary>
    public double FloorY { get; set; }

    /// <summary>Pit: the depth used (rounded to 1/16 m).</summary>
    public double Depth { get; set; }

    /// <summary>Pit: the floor point (snapped).</summary>
    public Vec3d Center { get; set; }

    internal void ComputeBounds()
    {
        double[] lo = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        double[] hi = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (Brush b in Brushes)
        {
            foreach (Vec3d p in HullDeform.WorldVertices(b))
            {
                for (int a = 0; a < 3; a++)
                {
                    lo[a] = Math.Min(lo[a], p[a]);
                    hi[a] = Math.Max(hi[a], p[a]);
                }
            }
        }

        Min = new Vec3d(lo[0], lo[1], lo[2]);
        Max = new Vec3d(hi[0], hi[1], hi[2]);
    }
}

/// <summary>Options shared by the rock builders.</summary>
public class RockOptions
{
    /// <summary>Ellipsoid samples per rock (8 to 24).</summary>
    public int Points { get; init; } = 12;

    /// <summary>Jitter amplitude ρ (0.05 to 0.5).</summary>
    public double Roughness { get; init; } = 0.2;

    /// <summary>The random seed.</summary>
    public long Seed { get; init; } = 1;

    /// <summary>Textures, tile and flags of the rocks.</summary>
    public HullBrushParams Brush { get; init; } = new();

    /// <summary>Structural brushes already in the level, audited against structural rocks.</summary>
    public IReadOnlyList<AuditShape> Existing { get; init; } = Array.Empty<AuditShape>();

    /// <summary>Checked between rocks, so a long build stops when the script is cancelled.</summary>
    public System.Threading.CancellationToken Cancel { get; init; }
}

/// <summary>Options of <see cref="RockGen.Row"/>.</summary>
public sealed class RockRowOptions : RockOptions
{
    /// <summary>The floor point the row starts at.</summary>
    public Vec3d From { get; init; }

    /// <summary>The floor point the row ends at.</summary>
    public Vec3d To { get; init; }

    /// <summary>Visible height above the floor, metres.</summary>
    public double Height { get; init; } = 3;

    /// <summary>Thickness across the line, metres.</summary>
    public double Depth { get; init; } = 2;

    /// <summary>Rock spacing along the line, metres (null = 0.8 · depth).</summary>
    public double? Spacing { get; init; }

    /// <summary>Clip every rock at one 1/16 m-aligned top height, giving a walkable flat top.</summary>
    public bool FlatTop { get; init; }

    /// <summary>Structural solids (audited across the row) instead of detail.</summary>
    public bool Structural { get; init; }
}

/// <summary>Options of <see cref="RockGen.Scatter"/>.</summary>
public sealed class ScatterOptions : RockOptions
{
    /// <summary>The rectangle to scatter on (its floor is <c>AreaMin.Y</c>), or null with <see cref="Along"/>.</summary>
    public (Vec3d Min, Vec3d Max)? Area { get; init; }

    /// <summary>The tube whose floor the rocks scatter on, or null with <see cref="Area"/>.</summary>
    public TubeResult? Along { get; init; }

    /// <summary>How many rocks (1 to 500).</summary>
    public int Count { get; init; } = 10;

    /// <summary>Smallest and largest diameter, metres.</summary>
    public double SizeMin { get; init; } = 0.6;

    /// <summary>Smallest and largest diameter, metres.</summary>
    public double SizeMax { get; init; } = 2.0;

    /// <summary>Inset from the area's edges / the tube floor's sides, metres.</summary>
    public double Margin { get; init; } = 0.5;

    /// <summary>Extra clearance between rocks, metres.</summary>
    public double Gap { get; init; } = 0.25;
}

/// <summary>Options of <see cref="RockGen.Pit"/>.</summary>
public sealed class PitOptions
{
    /// <summary>The floor point the pit opens from (x, z snap to 1/1024 m, y rounds to 1/16 m).</summary>
    public Vec3d Pos { get; init; }

    /// <summary>The radius at the top, metres.</summary>
    public double Radius { get; init; } = 4;

    /// <summary>The depth below <see cref="Pos"/>, metres (rounded to 1/16 m, at least 1).</summary>
    public double Depth { get; init; } = 4;

    /// <summary>Points per ring (6 to 16).</summary>
    public int Sides { get; init; } = 8;

    /// <summary>The bottom radius as a fraction of the top radius (0.2 to 2).</summary>
    public double Taper { get; init; } = 0.7;

    /// <summary>Radial jitter ρ (0.05 to 0.5).</summary>
    public double Roughness { get; init; } = 0.2;

    /// <summary>The random seed.</summary>
    public long Seed { get; init; } = 1;

    /// <summary>Textures, tile and flags (Air is forced on).</summary>
    public HullBrushParams Brush { get; init; } = new();

    /// <summary>Structural brushes already in the level that the audit also checks against.</summary>
    public IReadOnlyList<AuditShape> Existing { get; init; } = Array.Empty<AuditShape>();

    /// <summary>Checked between segments, so a long build stops when the script is cancelled.</summary>
    public System.Threading.CancellationToken Cancel { get; init; }
}

/// <summary>
/// Rock dressing and pits (ORGANIC-GEOMETRY-SPEC §3.4). A rock is the convex hull of jittered samples of an
/// ellipsoid (a seeded Fibonacci sphere): the upper half reaches the requested visible height above the
/// floor, the lower half is a shallow cap buried <c>sink</c> under it so the rock always meets the floor.
/// Rocks are built in world space with their long axis along the row (no brush rotation) and re-drawn
/// until <see cref="GeometryCheck"/> passes with at most <see cref="MaxRockFaces"/> faces. A pit is a
/// stack of air hulls on tapering jittered horizontal rings: each ring is an exact horizontal plane, so
/// neighbouring hulls abut on identical caps (W4); the top ring lies on the floor the pit opens from and the
/// bottom ring forms the pit's flat floor.
/// </summary>
public static class RockGen
{
    /// <summary>The most faces a generated rock may have.</summary>
    public const int MaxRockFaces = 40;

    /// <summary>How many seeded re-draws a rock or pit ring gets before the roughness fallback.</summary>
    public const int Attempts = 8;

    /// <summary>How many times one ridge piece or pit segment may move a corner outward to clear near-flat folds.</summary>
    private const int FoldFixes = 12;

    private static readonly double GoldenAngle = Math.PI * (3 - Math.Sqrt(5));

    // ---- rock_row ---------------------------------------------------------------------------------

    /// <summary>A row of overlapping rocks along the floor line From → To.</summary>
    public static RockResult? Row(RockRowOptions o, out GeoEditError? error)
    {
        ArgumentNullException.ThrowIfNull(o);
        error = Common(o);
        if (error is not null)
        {
            return null;
        }

        if (!(o.Height > 0.1 && o.Height <= 256))
        {
            error = HullChain.Range("height", o.Height, 0.1, 256);
            return null;
        }

        if (!(o.Depth >= 0.25 && o.Depth <= 256))
        {
            error = HullChain.Range("depth", o.Depth, 0.25, 256);
            return null;
        }

        double spacing = o.Spacing ?? 0.8 * o.Depth;
        if (!(spacing >= 0.25 && spacing <= 256))
        {
            error = HullChain.Range("spacing", spacing, 0.25, 256);
            return null;
        }

        var from = new Vec3d(OrganicPath.SnapG(o.From.X), OrganicPath.RoundQ(o.From.Y), OrganicPath.SnapG(o.From.Z));
        var to = new Vec3d(OrganicPath.SnapG(o.To.X), OrganicPath.RoundQ(o.To.Y), OrganicPath.SnapG(o.To.Z));
        double dx = to.X - from.X, dz = to.Z - from.Z;
        double len = Math.Sqrt((dx * dx) + (dz * dz));
        if (len > 4096)
        {
            error = new GeoEditError($"the row is {OrganicPath.F(len)} m long; the limit is 4096 m.", "Split it into several rows.");
            return null;
        }

        int count = len < 1e-6 ? 1 : Math.Max(1, (int)Math.Ceiling((len / spacing) - 1e-9)) + 1;
        if (count > OrganicLimits.MaxBulkHulls)
        {
            error = new GeoEditError($"the row needs {count} rocks; the limit is {OrganicLimits.MaxBulkHulls}.", "Raise spacing.");
            return null;
        }

        Vec3d axis = len < 1e-6 ? new Vec3d(1, 0, 0) : new Vec3d(dx / len, 0, dz / len);
        HullBrushParams bp = Solid(o.Brush, detail: !o.Structural);
        if (o.Structural)
        {
            return Ridge(o, from, to, len, spacing, bp, out error);
        }

        // Detail rocks: they never seal, so they overlap freely and are not audited.
        var result = new RockResult();
        for (int k = 0; k < count; k++)
        {
            o.Cancel.ThrowIfCancellationRequested();
            double f = count == 1 ? 0 : (double)k / (count - 1);
            var basePoint = new Vec3d(from.X + (dx * f), OrganicPath.RoundQ(from.Y + ((to.Y - from.Y) * f)), from.Z + (dz * f));
            double? top = o.FlatTop ? OrganicPath.RoundQ(basePoint.Y + o.Height) : null;
            Brush? rock = null;
            string? why = null;
            for (int attempt = 0; attempt <= Attempts && rock is null; attempt++)
            {
                double rho = attempt >= Attempts ? 0 : o.Roughness;
                var rng = new OrganicRandom(o.Seed, k, attempt);
                double h = o.Height * (1 + (rho / 2 * rng.Range(-1, 1)));
                List<Vec3d> pts = RockPoints(basePoint, axis, spacing * 0.75, h, o.Depth / 2, o.Points, rho, rng, top);
                rock = TryRock(pts, bp, null, $"rock {k + 1}", out why, out _);
                if (rock is not null)
                {
                    result.Retries += attempt;
                }
            }

            if (rock is null)
            {
                error = RockError(k + 1, why);
                return null;
            }

            result.Brushes.Add(rock);
        }

        result.ComputeBounds();
        return result;
    }

    /// <summary>
    /// A structural rock wall as an abutting ridge: exactly planar vertical rings across the row (whole lattice
    /// steps on the vertical plane through each station, as <see cref="HullChain"/> rings), a rounded rough
    /// profile whose two bottom corners lie exactly on the floor plane, the end rings shrunk so the ends round
    /// off. Neighbouring pieces meet on identical caps, so no two structural rocks cross each other.
    /// </summary>
    private static RockResult? Ridge(RockRowOptions o, Vec3d from, Vec3d to, double len, double spacing, HullBrushParams bp, out GeoEditError? error)
    {
        error = null;
        int n = Math.Max(1, (int)Math.Ceiling((len / spacing) - 1e-9));
        if (len < 0.5)
        {
            error = new GeoEditError($"a structural rock wall needs from and to at least 0.5 m apart (got {OrganicPath.F(len)} m).", "Move to further from from, or drop structural = true.");
            return null;
        }

        var axis = new Vec3d((to.X - from.X) / len, 0, (to.Z - from.Z) / len);
        (int la, int lb) = HullChain.Lattice(OrganicPath.Right(axis));
        var d = new Vec3d(la * OrganicPath.G, 0, lb * OrganicPath.G);
        double step = d.Length;
        double? top = o.FlatTop ? OrganicPath.RoundQ(from.Y + o.Height) : null;
        const int Arch = 5;

        List<Vec3d> Ring(int k, int attempt)
        {
            var rng = new OrganicRandom(o.Seed, k, attempt);
            double rho = attempt >= Attempts ? 0 : o.Roughness;
            double f = (double)k / n;
            var station = new Vec3d(OrganicPath.SnapG(from.X + ((to.X - from.X) * f)), RidgeBase(o.From.Y + ((o.To.Y - o.From.Y) * f)), OrganicPath.SnapG(from.Z + ((to.Z - from.Z) * f)));
            double end = k == 0 || k == n ? 0.35 : 1;
            double half = o.Depth / 2 * end, height = Math.Max(0.1, o.Height * (end == 1 ? 1 : 0.6));
            var pts = new List<Vec3d>(Arch + 2);
            Vec3d At(double lateral, double y)
            {
                double m = Math.Round(lateral / step, MidpointRounding.AwayFromZero);
                return new Vec3d(OrganicPath.SnapG(station.X + (m * d.X)), y, OrganicPath.SnapG(station.Z + (m * d.Z)));
            }

            pts.Add(At(-half * (1 + (0.5 * rho * rng.Range(-1, 1))), station.Y));
            pts.Add(At(half * (1 + (0.5 * rho * rng.Range(-1, 1))), station.Y));
            double h = height * (1 + (rho / 2 * rng.Range(-1, 1)));
            for (int j = 1; j <= Arch; j++)
            {
                double theta = j * Math.PI / (Arch + 1);
                double kk = 1 + (rho * rng.Range(-1, 1));
                double y = station.Y + (h * Math.Sin(theta) * kk);
                pts.Add(At(half * Math.Cos(theta) * kk, top is double t && y >= t ? t : OrganicPath.SnapG(y)));
            }

            return pts;
        }

        var result = new RockResult();
        var placed = new List<AuditShape>(o.Existing);
        List<Vec3d>? previous = null;
        for (int k = 0; k < n; k++)
        {
            o.Cancel.ThrowIfCancellationRequested();
            Brush? rock = null;
            string? why = null;
            for (int attempt = 0; attempt <= Attempts && rock is null; attempt++)
            {
                List<Vec3d> back = previous ?? Ring(0, attempt), fwd = Ring(k + 1, attempt);
                rock = TryRing(back, fwd, (r, i) => RidgeSteps(r, i, d, top), q => (((q.X * d.X) + (q.Z * d.Z)) / step, q.Y), bp, placed, $"rock {k + 1}", out why, out HullResult? hull, out List<Vec3d> used);
                if (rock is not null)
                {
                    previous = used;
                    result.Retries += attempt;
                    placed.Add(AuditShape.FromHull(hull!, $"rock {k + 1}"));
                }
            }

            if (rock is null)
            {
                error = RockError(k + 1, why);
                return null;
            }

            result.Brushes.Add(rock);
        }

        result.ComputeBounds();
        return result;
    }

    /// <summary>
    /// The height a structural ridge's bottom corners sit at for the floor height <paramref name="y"/>: exactly on
    /// it when it is on the 1/16 m grid (the ridge then stands on the floor plane), else the 1/16 m step at least
    /// 5 mm below it (sunk into the solid under the floor, never floating over it or skimming it within the
    /// near-coplanar band).
    /// </summary>
    internal static double RidgeBase(double y)
    {
        double q = OrganicPath.RoundQ(y);
        return Math.Abs(q - y) <= 1e-9 ? q : Math.Floor(((y - HullAudit.SeparationDistance) / OrganicPath.Q) + 1e-9) * OrganicPath.Q;
    }

    // ---- scatter ----------------------------------------------------------------------------------

    /// <summary>Boulders scattered on a rectangle or along a tube's floor, never closer than their radii plus the gap.</summary>
    public static RockResult? Scatter(ScatterOptions o, out GeoEditError? error)
    {
        ArgumentNullException.ThrowIfNull(o);
        error = Common(o);
        if (error is not null)
        {
            return null;
        }

        if (o.Count < 1 || o.Count > 500)
        {
            error = new GeoEditError($"'count' must be 1 to 500 (got {o.Count}).", $"Nearest valid value: {Math.Clamp(o.Count, 1, 500)}.");
            return null;
        }

        if (!(o.SizeMin >= 0.1) || !(o.SizeMax >= o.SizeMin) || !(o.SizeMax <= 64))
        {
            error = new GeoEditError($"'size' must be {{min, max}} diameters with 0.1 <= min <= max <= 64 metres (got {{{OrganicPath.F(o.SizeMin)}, {OrganicPath.F(o.SizeMax)}}}).",
                "For example size = {0.6, 2}.");
            return null;
        }

        if (!(o.Margin >= 0 && o.Margin <= 64))
        {
            error = HullChain.Range("margin", o.Margin, 0, 64);
            return null;
        }

        if (!(o.Gap >= 0 && o.Gap <= 64))
        {
            error = HullChain.Range("gap", o.Gap, 0, 64);
            return null;
        }

        HullBrushParams bp = Solid(o.Brush, detail: true);
        var rng = new OrganicRandom(o.Seed, -1);
        var centres = new List<(Vec3d C, double R)>();
        var result = new RockResult();
        TubeResult? tube = o.Along;
        double length = tube?.StationLength() ?? 0;
        int draws = 200 * o.Count;
        for (int d = 0; d < draws && centres.Count < o.Count; d++)
        {
            double size = rng.Range(o.SizeMin, o.SizeMax);
            double r = size / 2;
            double reach = r * (1 + o.Roughness);
            Vec3d at;
            Vec3d yaw;
            double lift = 0;
            if (tube is null)
            {
                (Vec3d lo, Vec3d hi) = o.Area!.Value;
                double x0 = lo.X + o.Margin + r, x1 = hi.X - o.Margin - r, z0 = lo.Z + o.Margin + r, z1 = hi.Z - o.Margin - r;
                double x = rng.Range(0, 1), z = rng.Range(0, 1), a = rng.Range(0, 2 * Math.PI);
                if (x1 < x0 || z1 < z0)
                {
                    continue;
                }

                at = new Vec3d(x0 + ((x1 - x0) * x), OrganicPath.RoundQ(lo.Y), z0 + ((z1 - z0) * z));
                yaw = new Vec3d(Math.Cos(a), 0, Math.Sin(a));
            }
            else
            {
                double s = rng.Range(0, length), side = rng.Range(-1, 1), t = rng.Range(0, 1);
                (Vec3d pos, _, double width, _, Vec3d dir) = tube.Sample(s);
                // Along a tube a rock may tuck against the wall foot (its centre up to the floor edge less the
                // margin), but never reaches into the guaranteed walkway.
                double hiLat = (width / 2) - o.Margin;
                double loLat = tube.ClearWidth > 0 ? (tube.ClearWidth / 2) + reach + 0.05 : 0;
                if (hiLat < loLat)
                {
                    continue;
                }

                double lat = (side < 0 ? -1 : 1) * (loLat + ((hiLat - loLat) * t));
                Vec3d right = OrganicPath.Right(dir);
                double x = pos.X + (right.X * lat), z = pos.Z + (right.Z * lat);
                if (tube.FloorAt(x, z) is not double floor)
                {
                    continue;
                }

                // On a slope the floor under the rock's footprint falls away from the centre: stand the rock on the
                // lowest floor under it (raising it by as much), so its sunk base meets the floor all round.
                double low = floor;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    if (tube.FloorAt(x + (reach * Math.Cos(a)), z + (reach * Math.Sin(a))) is double under)
                    {
                        low = Math.Min(low, under);
                    }
                }

                at = new Vec3d(x, low, z);
                lift = floor - low;
                yaw = dir;
            }

            bool clear = true;
            foreach ((Vec3d c, double rr) in centres)
            {
                double ddx = c.X - at.X, ddz = c.Z - at.Z;
                if (Math.Sqrt((ddx * ddx) + (ddz * ddz)) < r + rr + o.Gap)
                {
                    clear = false;
                    break;
                }
            }

            if (clear)
            {
                o.Cancel.ThrowIfCancellationRequested();
                centres.Add((at, r));
                Brush? rock = null;
                string? why = null;
                int k = centres.Count - 1;
                for (int attempt = 0; attempt <= Attempts && rock is null; attempt++)
                {
                    double rho = attempt >= Attempts ? 0 : o.Roughness;
                    var shape = new OrganicRandom(o.Seed, k, attempt);
                    double h = (size * 0.75 * shape.Range(0.8, 1.0)) + lift;
                    double depth = r * shape.Range(0.75, 1.0);
                    List<Vec3d> pts = RockPoints(new Vec3d(OrganicPath.SnapG(at.X), at.Y, OrganicPath.SnapG(at.Z)), yaw, r, h, depth, o.Points, rho, shape, null);
                    rock = TryRock(pts, bp, null, $"rock {k + 1}", out why, out _);
                    result.Retries += rock is null ? 0 : attempt;
                }

                if (rock is null)
                {
                    error = RockError(k + 1, why);
                    return null;
                }

                result.Brushes.Add(rock);
            }
        }

        if (centres.Count < o.Count)
        {
            error = new GeoEditError($"only {centres.Count} of {o.Count} rocks fit.", "Lower count or size, or enlarge the area.");
            return null;
        }

        result.ComputeBounds();
        return result;
    }

    // ---- pit --------------------------------------------------------------------------------------

    /// <summary>A stack of abutting air hulls going down from a floor point; the bottom ring is the flat pit floor.</summary>
    public static RockResult? Pit(PitOptions o, out GeoEditError? error)
    {
        ArgumentNullException.ThrowIfNull(o);
        error = !(o.Roughness >= 0.05 && o.Roughness <= 0.5) ? HullChain.Range("roughness", o.Roughness, 0.05, 0.5)
            : !(o.Radius >= 0.5 && o.Radius <= 512) ? HullChain.Range("radius", o.Radius, 0.5, 512)
            : !(o.Taper >= 0.2 && o.Taper <= 2) ? HullChain.Range("taper", o.Taper, 0.2, 2)
            : o.Sides < 6 || o.Sides > 16 ? new GeoEditError($"'sides' must be 6 to 16 (got {o.Sides}).", $"Nearest valid value: {Math.Clamp(o.Sides, 6, 16)}.")
            : !double.IsFinite(o.Pos.X) || !double.IsFinite(o.Pos.Y) || !double.IsFinite(o.Pos.Z) ? new GeoEditError("'pos' must be a vec of finite numbers.", null)
            : null;
        if (error is not null)
        {
            return null;
        }

        double depth = OrganicPath.RoundQ(o.Depth);
        if (!(depth >= 1 && depth <= 1024))
        {
            error = new GeoEditError($"'depth' must be 1 to 1024 metres (got {OrganicPath.F(o.Depth)}).", $"Nearest valid value: {OrganicPath.F(double.IsFinite(o.Depth) ? Math.Clamp(o.Depth, 1, 1024) : 1)}.");
            return null;
        }

        var pos = new Vec3d(OrganicPath.SnapG(o.Pos.X), OrganicPath.RoundQ(o.Pos.Y), OrganicPath.SnapG(o.Pos.Z));

        // The top ring stands a lap ABOVE the floor point (air over the room's or tube's air is harmless), so the
        // pit opens through the floor whatever its height: a floor that is not on the 1/16 m grid would otherwise
        // keep a skin over a top ring rounded below it. The lap is at least half a 1/16 m step.
        double top = Math.Ceiling(((o.Pos.Y + (OrganicPath.Q / 2)) / OrganicPath.Q) - 1e-9) * OrganicPath.Q, bottom = pos.Y - depth;
        double span = top - bottom;
        int hulls = Math.Max(1, (int)Math.Round(span / o.Radius, MidpointRounding.AwayFromZero));
        hulls = Math.Max(1, Math.Min(hulls, (int)Math.Floor(span / 0.5)));
        var ringY = new double[hulls + 1];
        for (int k = 0; k <= hulls; k++)
        {
            ringY[k] = k == 0 ? top : k == hulls ? bottom : OrganicPath.RoundQ(top - (span * k / hulls));
        }

        double phase = new OrganicRandom(o.Seed, -1).Range(0, 2 * Math.PI);
        List<Vec3d> Ring(int k, int attempt)
        {
            var rng = new OrganicRandom(o.Seed, k, attempt);
            double rho = attempt >= Attempts ? 0 : o.Roughness;
            double radius = o.Radius * (1 + ((o.Taper - 1) * k / hulls));
            var ring = new List<Vec3d>(o.Sides);
            for (int j = 0; j < o.Sides; j++)
            {
                double a = phase + (2 * Math.PI * j / o.Sides);
                double r = radius * (1 + (rho * rng.Range(-1, 1)));
                ring.Add(new Vec3d(OrganicPath.SnapG(pos.X + (r * Math.Cos(a))), ringY[k], OrganicPath.SnapG(pos.Z + (r * Math.Sin(a)))));
            }

            return ring;
        }

        HullBrushParams bp = HullChain.Clone(o.Brush);
        var result = new RockResult { FloorY = bottom, Depth = depth, Center = pos };
        var placed = new List<AuditShape>(o.Existing);
        List<Vec3d>? upper = null;
        for (int k = 0; k < hulls; k++)
        {
            o.Cancel.ThrowIfCancellationRequested();
            Brush? brush = null;
            string? why = null;
            for (int attempt = 0; attempt <= Attempts && brush is null; attempt++)
            {
                List<Vec3d> back = upper ?? Ring(0, attempt), fwd = Ring(k + 1, attempt);
                brush = TryRing(back, fwd, (r, i) => PitSteps(r, i, pos), q => (q.X, q.Z), bp, placed, $"ring {k + 1}", out why, out HullResult? hull, out List<Vec3d> used);
                if (brush is not null)
                {
                    upper = used;
                    result.Retries += attempt;
                    placed.Add(AuditShape.FromHull(hull!, $"ring {k + 1}"));
                }
            }

            if (brush is null)
            {
                error = why is not null && why.StartsWith("near-coplanar", StringComparison.Ordinal)
                    ? new GeoEditError($"could not place pit segment {k + 1} without a near-coplanar face ({why}).", NearCoplanarHint(why, "the pit"))
                    : new GeoEditError($"could not build pit segment {k + 1} as a convex brush ({why}).", "Change seed, radius or depth.");
                return null;
            }

            result.Brushes.Add(brush);
        }

        result.ComputeBounds();
        return result;
    }

    // ---- shared -----------------------------------------------------------------------------------

    /// <summary>
    /// The samples of one rock: <paramref name="n"/> points of a seeded Fibonacci sphere scaled to semi-axes
    /// (<paramref name="sx"/> along <paramref name="axis"/>, height, <paramref name="sz"/> across), each jittered by
    /// (1 + ρξ). The upper half reaches <paramref name="height"/> above the base; the lower half is a shallow cap
    /// buried under it. With <paramref name="flatTop"/> every point above that height moves down onto it.
    /// </summary>
    internal static List<Vec3d> RockPoints(Vec3d basePoint, Vec3d axis, double sx, double height, double sz, int n, double rho, OrganicRandom rng, double? flatTop)
    {
        double sink = Math.Clamp(0.2 * height, 0.1, 1.0);
        double cy = basePoint.Y - sink;
        double up = height + sink, down = 2 * sink;
        if (flatTop is double ft)
        {
            // Tall enough that every sample in the upper half of the sphere (u.y >= 0.5) reaches the top.
            up = Math.Max(up, (ft - cy) / (0.5 * (1 - rho)));
        }

        Vec3d across = OrganicPath.Right(axis);
        double phase = rng.Range(0, 2 * Math.PI);
        var pts = new List<Vec3d>(n);
        for (int i = 0; i < n; i++)
        {
            double uy = 1 - (2 * (i + 0.5) / n);
            double rr = Math.Sqrt(Math.Max(0, 1 - (uy * uy)));
            double phi = phase + (i * GoldenAngle);
            double k = 1 + (rho * rng.Range(-1, 1));
            double ux = rr * Math.Cos(phi) * sx * k, uz = rr * Math.Sin(phi) * sz * k;
            double y = cy + (uy * (uy >= 0 ? up : down) * k);
            double x = basePoint.X + (axis.X * ux) + (across.X * uz);
            double z = basePoint.Z + (axis.Z * ux) + (across.Z * uz);
            bool clipped = flatTop is double t && y >= t;
            pts.Add(new Vec3d(OrganicPath.SnapG(x), clipped ? flatTop!.Value : OrganicPath.SnapG(y), OrganicPath.SnapG(z)));
        }

        return pts;
    }

    /// <summary>
    /// Builds the hull of a fixed ring and a ring being placed (exact caps, the audit against
    /// <paramref name="placed"/>). A near-flat fold is opened by moving a corner of the placed ring outward
    /// (<see cref="FoldRepair"/>, using <paramref name="steps"/>) and checking again; <paramref name="used"/> is the
    /// placed ring as finally used.
    /// </summary>
    private static Brush? TryRing(List<Vec3d> back, List<Vec3d> fwd, Func<List<Vec3d>, int, IReadOnlyList<Vec3d>> steps, Func<Vec3d, (double, double)> frame,
        HullBrushParams bp, List<AuditShape> placed, string label, out string? why, out HullResult? hull, out List<Vec3d> used)
    {
        used = new List<Vec3d>(fwd);
        List<Vec3d> ring = used;
        for (int fix = 0; ; fix++)
        {
            // A ring corner nearly in line with its neighbours folds every hull built on the ring near-flat.
            if (FoldRepair.SmallestTurn(ring.Select(frame).ToList()) < FoldRepair.MinRingTurnDegrees
                || FoldRepair.SmallestTurn(back.Select(frame).ToList()) < FoldRepair.MinRingTurnDegrees)
            {
                why = "a ring corner nearly in line with its neighbours";
                hull = null;
                return null;
            }

            Brush? rock = TryRock(back.Concat(ring).ToList(), bp, placed, label, out why, out hull, out AuditConflict? fold, out AuditShape? shape,
                maxFaces: OrganicLimits.MaxHullFaces, exactCaps: true);
            if (rock is not null || fold is null || fix == FoldFixes || !FoldRepair.Push(ring, fold, shape!, placed, i => steps(ring, i)))
            {
                return rock;
            }
        }
    }

    /// <summary>A structural ridge ring's moves: its two floor corners stay; the others go sideways along the lattice or up (not past a flat top).</summary>
    private static IReadOnlyList<Vec3d> RidgeSteps(List<Vec3d> ring, int i, Vec3d d, double? top)
    {
        if (i < 2)
        {
            return Array.Empty<Vec3d>();
        }

        Vec3d mid = ring[0].Add(ring[1]).Scale(0.5);
        double sign = ring[i].Sub(mid).Dot(d) >= 0 ? 1 : -1;
        var side = new Vec3d(sign * d.X, 0, sign * d.Z);
        return top is double t && ring[i].Y >= t ? new[] { side } : new[] { side, new Vec3d(0, OrganicPath.G, 0) };
    }

    /// <summary>A pit ring's moves: radially outward from the pit axis, in whole grid steps on its horizontal plane.</summary>
    private static IReadOnlyList<Vec3d> PitSteps(List<Vec3d> ring, int i, Vec3d axis)
    {
        double dx = ring[i].X - axis.X, dz = ring[i].Z - axis.Z, len = Math.Sqrt((dx * dx) + (dz * dz));
        if (len < 1e-9)
        {
            return Array.Empty<Vec3d>();
        }

        var step = new Vec3d(Math.Round(8 * dx / len) * OrganicPath.G, 0, Math.Round(8 * dz / len) * OrganicPath.G);
        return step.X == 0 && step.Z == 0 ? Array.Empty<Vec3d>() : new[] { step };
    }

    /// <summary>Builds one piece and checks it (shape, face count, and the audit when <paramref name="placed"/> is given); null + why on failure.</summary>
    private static Brush? TryRock(List<Vec3d> pts, HullBrushParams bp, List<AuditShape>? placed, string label, out string? why, out HullResult? hull,
        int maxFaces = MaxRockFaces, bool exactCaps = false) => TryRock(pts, bp, placed, label, out why, out hull, out _, out _, maxFaces, exactCaps);

    private static Brush? TryRock(List<Vec3d> pts, HullBrushParams bp, List<AuditShape>? placed, string label, out string? why, out HullResult? hull,
        out AuditConflict? fold, out AuditShape? audited, int maxFaces = MaxRockFaces, bool exactCaps = false)
    {
        fold = null;
        audited = null;
        HullBrush.TryBuild(pts, bp, 0, out Brush? brush, out hull, out HullBrushError? err);
        if (err is not null || brush is null || hull is null)
        {
            why = err?.Message ?? "the hull could not be built";
            return null;
        }

        GeometryReport report = GeometryCheck.Check(brush);
        if (!report.Closed || !report.Convex || !report.Planar)
        {
            why = string.Join(" ", report.Issues);
            return null;
        }

        if (!CompilerFit.Fits(brush))
        {
            why = "a face the compiler would fit off its corners";
            return null;
        }

        if (exactCaps && hull.Simplified > 0)
        {
            why = "a ring corner made a sliver face";
            return null;
        }

        if (hull.Faces.Count > maxFaces)
        {
            why = $"{hull.Faces.Count} faces, more than {maxFaces}";
            return null;
        }

        if (placed is not null)
        {
            AuditShape shape = AuditShape.FromHull(hull, label);
            IReadOnlyList<AuditConflict> conflicts = HullAudit.Conflicts(placed, shape);
            if (conflicts.Count > 0)
            {
                why = "near-coplanar: " + conflicts[0];
                return null;
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
                why = "near-coplanar: a near-flat fold " + folds[0];
                return null;
            }
        }

        why = null;
        return brush;
    }

    private static GeoEditError RockError(int k, string? why) =>
        why is not null && why.StartsWith("near-coplanar", StringComparison.Ordinal)
            ? new GeoEditError($"could not place rock {k} without a near-coplanar face ({why}).", NearCoplanarHint(why, "the row"))
            : new GeoEditError($"could not build rock {k} as a convex brush ({why}).", "Change seed or the rock size.");

    /// <summary>
    /// What to change for a near-coplanar failure: an existing brush in the way (its label is <c>brush#uid</c>)
    /// needs the piece moved; a clash between the generator's own pieces needs another draw.
    /// </summary>
    internal static string NearCoplanarHint(string why, string what) =>
        why.Contains("brush#", StringComparison.Ordinal)
            ? $"An existing brush almost touches {what}: move {what} so it stays at least 5 mm clear of that brush or meets its faces exactly (a floor whose height is not a multiple of 1/16 m is the usual cause; put the floor on that grid)."
            : "Change seed or raise roughness.";

    private static GeoEditError? Common(RockOptions o) =>
        !(o.Roughness >= 0.05 && o.Roughness <= 0.5) ? HullChain.Range("roughness", o.Roughness, 0.05, 0.5)
        : o.Points < 8 || o.Points > 24 ? new GeoEditError($"'points' must be 8 to 24 (got {o.Points}).", $"Nearest valid value: {Math.Clamp(o.Points, 8, 24)}.")
        : null;

    private static HullBrushParams Solid(HullBrushParams p, bool detail) => new()
    {
        Air = false,
        Detail = detail || p.Detail,
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
