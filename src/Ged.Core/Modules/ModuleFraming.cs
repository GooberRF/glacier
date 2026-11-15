using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Modules;

/// <summary>The measured frame of one component before the final quarter turn is chosen.</summary>
internal sealed class FrameMeasurement
{
    /// <summary>The component pivot (world): the θ-frame is <c>RotY(-Theta)·(p − Pivot)</c>.</summary>
    public D3 Pivot { get; init; }

    /// <summary>The axis-aligning yaw (degrees, [0, 90)).</summary>
    public double Theta { get; init; }

    /// <summary>The local origin in θ-frame coordinates.</summary>
    public D3 Origin { get; set; }

    /// <summary>Allowed quarter turns (k: local = RotY(−90k)·(θlocal − Origin)).</summary>
    public List<int> Turns { get; set; } = new();

    public string Attach { get; set; } = "free";

    /// <summary>Contact fractions per θ-frame side (−X, +X, −Y, +Y, −Z, +Z).</summary>
    public double[] Contacts { get; init; } = new double[6];

    public double ContactConfidence { get; set; }

    public bool FloorTouch { get; set; }

    public bool CeilingTouch { get; set; }

    public double? MountHeight { get; set; }

    public double? CeilingGap { get; set; }

    public ModuleOpening? Opening { get; set; }

    /// <summary>True when the bottom has air below beside an edge (a drop).</summary>
    public bool Drop { get; set; }

    /// <summary>True when an opening frame sits in a wall (structural solid with air on both sides beside it).</summary>
    public bool OpeningInWall { get; set; }
}

/// <summary>
/// Locates a component's local frame: the axis-aligning yaw from an area-weighted histogram of horizontal face
/// normals (mod 90°), contact probes on the six sides of the oriented bounds, the attachment kind, and the anchor
/// planes found by marching the solidity index (median air→solid transition, quantised to 1/64 m).
/// </summary>
internal sealed class ModuleFraming
{
    private const double Fine = 1.0 / 64.0;

    /// <summary>Bisection precision of a transition (finer than the 1/64 m quantum so the quantised plane is exact).</summary>
    private const double Bisect = 1.0 / 2048.0;

    private readonly BrushSolidityIndex _index;
    private readonly HarvestOptions _o;
    private readonly IReadOnlySet<int> _members;
    private D3 _pivot;
    private double _theta;
    private D3 _min, _max;
    private bool _openingInWall;

    public ModuleFraming(BrushSolidityIndex index, HarvestOptions options, IReadOnlySet<int> memberUids)
    {
        _index = index;
        _o = options;
        _members = memberUids;
    }

    /// <summary>Measures the frame of the given member polyhedra.</summary>
    public FrameMeasurement Measure(IReadOnlyList<WorldPolyhedron> shapes)
    {
        _theta = DominantYaw(shapes);
        D3 wmin = new(double.MaxValue, double.MaxValue, double.MaxValue), wmax = new(double.MinValue, double.MinValue, double.MinValue);
        foreach (WorldPolyhedron s in shapes)
        {
            wmin = D3.Min(wmin, s.Min);
            wmax = D3.Max(wmax, s.Max);
        }

        D3 c = (wmin + wmax) * 0.5;
        _pivot = new D3(Q(c.X, Fine), Q(c.Y, Fine), Q(c.Z, Fine));
        _min = new D3(double.MaxValue, double.MaxValue, double.MaxValue);
        _max = new D3(double.MinValue, double.MinValue, double.MinValue);
        foreach (WorldPolyhedron s in shapes)
        {
            foreach (D3 v in s.Vertices)
            {
                D3 l = ToTheta(v);
                _min = D3.Min(_min, l);
                _max = D3.Max(_max, l);
            }
        }

        var m = new FrameMeasurement { Pivot = _pivot, Theta = _theta };
        for (int side = 0; side < 6; side++)
        {
            m.Contacts[side] = ContactFraction(side);
        }

        // Floor / ceiling planes and the gaps to them.
        double? floorT = ProbePlane(2);
        double floorY = floorT is double ft ? _min.Y - ft : double.NaN;
        m.FloorTouch = floorT is double t0 && _min.Y - floorY < _o.FloorTouch;
        double? ceilT = ProbePlane(3);
        double ceilY = ceilT is double ct ? _max.Y + ct : double.NaN;
        m.CeilingTouch = ceilT is double t1 && ceilY - _max.Y < _o.FloorTouch;
        if (!m.FloorTouch)
        {
            m.MountHeight = DistanceBelow();
        }

        m.CeilingGap = m.CeilingTouch ? 0 : DistanceAbove();
        bool floorContact = m.FloorTouch || m.Contacts[2] >= _o.ContactFraction;
        bool ceilingContact = m.CeilingTouch || m.Contacts[3] >= _o.ContactFraction;

        double yAnchorFloor = m.FloorTouch ? floorY : _min.Y;
        double xc = (_min.X + _max.X) * 0.5, zc = (_min.Z + _max.Z) * 0.5;

        // 1. A frame around an opening.
        if (DetectOpening(out int wAxis, out double wCentre, out double sill, out double tMid, out ModuleOpening? opening))
        {
            m.Attach = "opening";
            m.Opening = opening;
            m.OpeningInWall = _openingInWall;
            m.Origin = wAxis == 0 ? new D3(wCentre, sill, tMid) : new D3(tMid, sill, wCentre);
            m.Turns = wAxis == 0 ? new List<int> { 0, 2 } : new List<int> { 1, 3 };
            m.MountHeight = null;
            m.ContactConfidence = 0.8;
            return Finish(m);
        }

        // 2. Wall contacts (horizontal sides), ignoring a contact on a short end of a long module.
        var touched = new[] { 0, 1, 4, 5 }.Where(s => m.Contacts[s] >= _o.ContactFraction).ToList();
        bool oppX = touched.Contains(0) && touched.Contains(1), oppZ = touched.Contains(4) && touched.Contains(5);
        var walls = new List<int>();
        foreach (int side in touched)
        {
            double along = side < 2 ? _max.Z - _min.Z : _max.X - _min.X;  // the side's own horizontal length
            double depth = side < 2 ? _max.X - _min.X : _max.Z - _min.Z; // perpendicular to the side
            if (along >= 0.5 * depth || depth <= 1.5)
            {
                walls.Add(side);
            }
        }

        if ((oppX || oppZ) && !floorContact)
        {
            // A span between two opposite walls: x along the span.
            bool alongX = oppX && (!oppZ || _max.X - _min.X >= _max.Z - _min.Z);
            m.Attach = "span";
            double y = m.CeilingTouch ? ceilY : _min.Y;
            m.Origin = new D3(xc, y, zc);
            m.Turns = alongX ? new List<int> { 0, 2 } : new List<int> { 1, 3 };
            m.ContactConfidence = Avg(m.Contacts, alongX ? new[] { 0, 1 } : new[] { 4, 5 });
            if (m.CeilingTouch)
            {
                m.MountHeight = null;
            }

            return Finish(m);
        }

        bool wallPair = (walls.Contains(0) && walls.Contains(1)) || (walls.Contains(4) && walls.Contains(5));
        if ((walls.Count > 0 && !wallPair) || walls.Count == 3)
        {
            int back;
            int? second = null;
            if (walls.Count == 3)
            {
                // The middle wall of three is the one whose opposite side is open.
                back = walls.First(s => !walls.Contains(Opposite(s)));
            }
            else if (walls.Count == 2)
            {
                // A corner: the larger-contact wall is the back (z = 0), the other is x = 0.
                back = m.Contacts[walls[0]] >= m.Contacts[walls[1]] ? walls[0] : walls[1];
                second = back == walls[0] ? walls[1] : walls[0];
            }
            else
            {
                back = walls[0];
            }

            double backPlane = PlaneCoordinate(back);
            D3 origin = new(xc, yAnchorFloor, zc);
            origin = origin.With(SideAxis(back), backPlane);
            if (second is int s2)
            {
                origin = origin.With(SideAxis(s2), PlaneCoordinate(s2));
                int k = TurnFor(back, s2);
                if (k < 0)
                {
                    k = TurnFor(s2, back); // the other wall becomes z = 0 so the module still extends into +X,+Z
                }

                m.Attach = "corner";
                m.Turns = new List<int> { Math.Max(0, k) };
                m.ContactConfidence = Avg(m.Contacts, new[] { back, s2 });
            }
            else
            {
                m.Attach = "wall";
                m.Turns = new List<int> { TurnFor(back, null) };
                m.ContactConfidence = m.Contacts[back];
            }

            m.Origin = origin;
            return Finish(m);
        }

        if (floorContact && ceilingContact && _max.Y - _min.Y >= 1.8)
        {
            m.Attach = "column";
            m.Origin = new D3(xc, yAnchorFloor, zc);
            m.Turns = HorizontalTurns();
            m.ContactConfidence = Avg(m.Contacts, new[] { 2, 3 });
            return Finish(m);
        }

        if (ceilingContact && !floorContact)
        {
            m.Attach = "ceiling";
            m.Origin = new D3(xc, m.CeilingTouch ? ceilY : _max.Y, zc);
            m.Turns = HorizontalTurns();
            m.ContactConfidence = m.Contacts[3];
            m.MountHeight = null;
            return Finish(m);
        }

        if (floorContact)
        {
            if (m.FloorTouch && DropSide(floorY) is int drop)
            {
                m.Attach = "edge";
                m.Drop = true;
                double edgeLine = EdgeLine(drop, floorY);
                D3 origin = new(xc, floorY, zc);
                m.Origin = origin.With(SideAxis(drop), edgeLine);
                m.Turns = new List<int> { TurnFor(drop, null) };
                m.ContactConfidence = m.Contacts[2];
                return Finish(m);
            }

            m.Attach = "floor";
            m.Origin = new D3(xc, yAnchorFloor, zc);
            m.Turns = HorizontalTurns();
            m.ContactConfidence = m.Contacts[2];
            return Finish(m);
        }

        m.Attach = "free";
        m.Origin = new D3(xc, _min.Y, zc);
        m.Turns = HorizontalTurns();
        m.ContactConfidence = 0.2;
        return Finish(m);
    }

    /// <summary>Converts a world point into the θ-frame.</summary>
    public D3 ToTheta(D3 world) => YawFrame.Rotate(world - _pivot, -_theta);

    private D3 ToWorld(D3 theta) => _pivot + YawFrame.Rotate(theta, _theta);

    private bool Solid(D3 theta, bool structuralOnly = true, bool excludeMembers = true) =>
        _index.IsSolid(ToWorld(theta), excludeMembers ? _members : null, structuralOnly);

    private FrameMeasurement Finish(FrameMeasurement m)
    {
        D3 o = m.Origin;
        m.Origin = new D3(Q(o.X, Fine), Q(o.Y, Fine), Q(o.Z, Fine));
        return m;
    }

    /// <summary>Floor/free/column/ceiling: the longer horizontal extent goes on local X (two turns), or all four when square.</summary>
    private List<int> HorizontalTurns()
    {
        double sx = _max.X - _min.X, sz = _max.Z - _min.Z;
        if (Math.Abs(sx - sz) < 0.05)
        {
            return new List<int> { 0, 1, 2, 3 };
        }

        return sx > sz ? new List<int> { 0, 2 } : new List<int> { 1, 3 };
    }

    /// <summary>
    /// The quarter turn k that puts θ-frame side <paramref name="side"/> on local −Z (and <paramref name="xSide"/> on
    /// local −X), or −1 when no single turn does both.
    /// </summary>
    private static int TurnFor(int side, int? xSide)
    {
        for (int k = 0; k < 4; k++)
        {
            if (SideOfDirection(YawFrame.Rotate(new D3(0, 0, -1), 90.0 * k)) == side
                && (xSide is not int xs || SideOfDirection(YawFrame.Rotate(new D3(-1, 0, 0), 90.0 * k)) == xs))
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>The θ-frame side (0..5 = −X, +X, −Y, +Y, −Z, +Z) a unit axis direction points to.</summary>
    internal static int SideOfDirection(D3 d)
    {
        double ax = Math.Abs(d.X), ay = Math.Abs(d.Y), az = Math.Abs(d.Z);
        if (ax >= ay && ax >= az)
        {
            return d.X < 0 ? 0 : 1;
        }

        if (ay >= az)
        {
            return d.Y < 0 ? 2 : 3;
        }

        return d.Z < 0 ? 4 : 5;
    }

    private static int Opposite(int side) => side ^ 1;

    private static int SideAxis(int side) => side / 2;

    private static double SideSign(int side) => (side & 1) == 0 ? -1 : 1;

    private static double Avg(double[] values, int[] sides) => sides.Average(s => values[s]);

    /// <summary>The bounds coordinate of a side (min or max on its axis).</summary>
    private double BoundCoord(int side) => (side & 1) == 0 ? _min[SideAxis(side)] : _max[SideAxis(side)];

    /// <summary>The plane coordinate of a contacted side: the probed air→solid transition, else the bounds face.</summary>
    private double PlaneCoordinate(int side) =>
        ProbePlane(side) is double t ? BoundCoord(side) + (SideSign(side) * t) : BoundCoord(side);

    /// <summary>A 5×5 grid over side <paramref name="side"/> (inset), each point pushed <paramref name="offset"/> outside.</summary>
    private IEnumerable<D3> SideGrid(int side, double offset, int n = 5)
    {
        int axis = SideAxis(side);
        int u = axis == 0 ? 1 : 0, v = axis == 2 ? 1 : 2;
        double coord = BoundCoord(side) + (SideSign(side) * offset);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double fu = (i + 0.5) / n, fv = (j + 0.5) / n;
                double pu = _min[u] + ((_max[u] - _min[u]) * fu);
                double pv = _min[v] + ((_max[v] - _min[v]) * fv);
                D3 p = default;
                p = p.With(axis, coord).With(u, pu).With(v, pv);
                yield return p;
            }
        }
    }

    private double ContactFraction(int side)
    {
        int solid = 0, total = 0;
        foreach (D3 p in SideGrid(side, _o.ContactOffset))
        {
            total++;
            if (Solid(p))
            {
                solid++;
            }
        }

        return total == 0 ? 0 : (double)solid / total;
    }

    /// <summary>
    /// Marches along side <paramref name="side"/>'s outward normal from 2 m inside the bounds to 0.5 m outside at a
    /// 5×5 grid, and returns the median distance past the bounds face of the first air→solid transition (members
    /// excluded, structural brushes only), or null when no sample found one.
    /// </summary>
    private double? ProbePlane(int side)
    {
        var hits = new List<double>();
        D3 n = default;
        n = n.With(SideAxis(side), SideSign(side));
        foreach (D3 face in SideGrid(side, 0))
        {
            if (FirstTransition(face, n, -2.0, 0.5, toSolid: true) is double t)
            {
                hits.Add(t);
            }
        }

        return hits.Count == 0 ? null : Median(hits);
    }

    /// <summary>
    /// The first parameter t in [<paramref name="from"/>, <paramref name="to"/>] along <c>origin + dir·t</c> where the
    /// solidity changes to <paramref name="toSolid"/> after having been the opposite (coarse 1/8 m steps refined to 1/64 m).
    /// </summary>
    private double? FirstTransition(D3 origin, D3 dir, double from, double to, bool toSolid, double coarse = 0.125)
    {
        bool seenOpposite = false;
        double prevT = from;
        bool prev = Solid(origin + (dir * from));
        if (prev != toSolid)
        {
            seenOpposite = true;
        }

        int steps = (int)Math.Ceiling((to - from) / coarse);
        for (int i = 1; i <= steps; i++)
        {
            double t = Math.Min(to, from + (i * coarse));
            bool s = Solid(origin + (dir * t));
            if (seenOpposite && s == toSolid && prev != toSolid)
            {
                double lo = prevT, hi = t;
                while (hi - lo > Bisect)
                {
                    double mid = (lo + hi) * 0.5;
                    if (Solid(origin + (dir * mid)) == toSolid)
                    {
                        hi = mid;
                    }
                    else
                    {
                        lo = mid;
                    }
                }

                return hi;
            }

            if (s != toSolid)
            {
                seenOpposite = true;
            }

            prev = s;
            prevT = t;
        }

        return null;
    }

    /// <summary>Median distance from the module bottom down to the first solid (3×3 base grid, capped), or null.</summary>
    private double? DistanceBelow()
    {
        var hits = new List<double>();
        foreach (D3 p in SideGrid(2, 0.01, 3))
        {
            if (FirstSolid(p, new D3(0, -1, 0)) is double t)
            {
                hits.Add(t + 0.01);
            }
        }

        return hits.Count == 0 ? null : Q(Median(hits), Fine);
    }

    private double? DistanceAbove()
    {
        var hits = new List<double>();
        foreach (D3 p in SideGrid(3, 0.01, 3))
        {
            if (FirstSolid(p, new D3(0, 1, 0)) is double t)
            {
                hits.Add(t + 0.01);
            }
        }

        return hits.Count == 0 ? null : Q(Median(hits), Fine);
    }

    /// <summary>The distance to the first solid along <paramref name="dir"/> (0.25 m steps refined), or null within the cap.</summary>
    private double? FirstSolid(D3 origin, D3 dir)
    {
        if (Solid(origin))
        {
            return 0;
        }

        double prev = 0;
        for (double t = 0.25; t <= _o.MaxProbeDistance + 1e-9; t += 0.25)
        {
            if (Solid(origin + (dir * t)))
            {
                double lo = prev, hi = t;
                while (hi - lo > Bisect)
                {
                    double mid = (lo + hi) * 0.5;
                    if (Solid(origin + (dir * mid)))
                    {
                        hi = mid;
                    }
                    else
                    {
                        lo = mid;
                    }
                }

                return hi;
            }

            prev = t;
        }

        return null;
    }

    /// <summary>
    /// The horizontal side beyond which the floor drops away (air at least <see cref="HarvestOptions.EdgeDrop"/> deep
    /// just past the side, for most of its length), preferring long sides; null when none.
    /// </summary>
    private int? DropSide(double floorY)
    {
        int? best = null;
        double bestScore = 0;
        foreach (int side in new[] { 0, 1, 4, 5 })
        {
            int axis = SideAxis(side);
            int along = axis == 0 ? 2 : 0;
            double len = _max[along] - _min[along];
            double depth = _max[axis] - _min[axis];
            if (len < Math.Max(0.5, depth * 0.75))
            {
                continue; // only a long side can be an edge
            }

            int drops = 0;
            const int n = 5;
            for (int i = 0; i < n; i++)
            {
                double a = _min[along] + ((len * (i + 0.5)) / n);
                D3 p = default;
                p = p.With(axis, BoundCoord(side) + (SideSign(side) * 0.25)).With(along, a);
                bool open = true;
                foreach (double dy in new[] { 0.05, 0.4, _o.EdgeDrop - 0.01 })
                {
                    if (Solid(p.With(1, floorY - dy)))
                    {
                        open = false;
                        break;
                    }
                }

                if (open)
                {
                    drops++;
                }
            }

            double frac = drops / (double)n;
            if (frac >= 0.6 && frac * len > bestScore)
            {
                bestScore = frac * len;
                best = side;
            }
        }

        return best;
    }

    /// <summary>The edge line coordinate on the drop side's axis: where the floor slab ends (median solid→air).</summary>
    private double EdgeLine(int side, double floorY)
    {
        int axis = SideAxis(side);
        int along = axis == 0 ? 2 : 0;
        D3 n = default;
        n = n.With(axis, SideSign(side));
        var hits = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            double a = _min[along] + ((_max[along] - _min[along]) * (i + 0.5) / 5);
            D3 face = default;
            face = face.With(axis, BoundCoord(side)).With(along, a).With(1, floorY - 0.05);
            if (FirstTransition(face, n, -2.0, 1.0, toSolid: false) is double t)
            {
                hits.Add(t);
            }
        }

        return hits.Count == 0 ? BoundCoord(side) : BoundCoord(side) + (SideSign(side) * Median(hits));
    }

    /// <summary>
    /// A frame around an opening: along the thin horizontal axis the centre region is air through-and-through (all
    /// brushes, members included) over at least <see cref="HarvestOptions.MinOpeningWidth"/> ×
    /// <see cref="HarvestOptions.MinOpeningHeight"/>, with member geometry on both sides and/or above.
    /// </summary>
    private bool DetectOpening(out int wAxis, out double wCentre, out double sill, out double tMid, out ModuleOpening? opening)
    {
        wAxis = 0;
        wCentre = sill = tMid = 0;
        opening = null;
        double sx = _max.X - _min.X, sy = _max.Y - _min.Y, sz = _max.Z - _min.Z;
        if (sy < _o.MinOpeningHeight || Math.Max(sx, sz) < _o.MinOpeningWidth)
        {
            return false;
        }

        foreach (int w in sx >= sz ? new[] { 0, 2 } : new[] { 2, 0 })
        {
            int t = w == 0 ? 2 : 0;
            double sw = _max[w] - _min[w], st = _max[t] - _min[t];
            if (sw < _o.MinOpeningWidth || st > 3.0 || st > sw)
            {
                continue;
            }

            double wc = (_min[w] + _max[w]) * 0.5;
            bool Open(double wv, double yv)
            {
                for (double tv = _min[t] - 0.3; tv <= _max[t] + 0.3 + 1e-9; tv += 0.1)
                {
                    D3 p = default;
                    p = p.With(w, wv).With(1, yv).With(t, tv);
                    if (Solid(p, structuralOnly: false, excludeMembers: false))
                    {
                        return false;
                    }
                }

                return true;
            }

            // The longest open run up the centre column.
            (double y0, double y1) = LongestRun(_min.Y, _max.Y, 0.1, y => Open(wc, y));
            if (y1 - y0 < _o.MinOpeningHeight - 0.1)
            {
                continue;
            }

            double ym = (y0 + y1) * 0.5;
            (double w0, double w1) = RunContaining(_min[w], _max[w], 0.05, wc, x => Open(x, ym));
            if (w1 - w0 < _o.MinOpeningWidth - 0.05)
            {
                continue;
            }

            // Refine the run ends (the samples sit half a step inside them) to the bisection precision.
            w0 = Edge(w0 + 0.025, w0 - 0.025, x => Open(x, ym));
            w1 = Edge(w1 - 0.025, w1 + 0.025, x => Open(x, ym));
            y1 = Edge(y1 - 0.05, y1 + 0.05, y => Open((w0 + w1) * 0.5, y));

            // Member geometry beside or above the hole (a frame, not a free-standing slab next to a gap).
            bool above = _max.Y > y1 + 0.05;
            bool left = w0 > _min[w] + 0.05;
            bool right = w1 < _max[w] - 0.05;
            if (!((left && right) || above))
            {
                continue;
            }

            // Refine the sill: march down the centre from inside the hole.
            D3 c = default;
            c = c.With(w, (w0 + w1) * 0.5).With(1, y0 + 0.05).With(t, (_min[t] + _max[t]) * 0.5);
            double sillY = FirstTransition(c, new D3(0, -1, 0), 0, 1.0, toSolid: true) is double ds ? y0 + 0.05 - ds : y0;

            // The wall's mid-plane: the structural solid just beside the module, marched through along t.
            (double t1, double t2)? wall = WallSpan(w, t, ym);
            _openingInWall = wall is not null;
            double mid = wall is { } span ? (span.t1 + span.t2) * 0.5 : (_min[t] + _max[t]) * 0.5;
            double thick = wall is { } span2 ? span2.t2 - span2.t1 : st;

            wAxis = w;
            wCentre = (w0 + w1) * 0.5;
            sill = sillY;
            tMid = mid;
            opening = new ModuleOpening(Q(w1 - w0, Fine), Q(y1 - sillY, Fine), Q(thick, Fine));
            return true;
        }

        return false;
    }

    /// <summary>
    /// The wall an opening frame sits in: structural solid just beside the frame at its mid-plane, with room air on at
    /// least one side within 1.5 m of the frame (a wall between rooms, or a corridor mouth). Solid on both sides for
    /// that far (rock around a tunnel support) or no solid beside it (a free-standing arch) is no wall. Returns the
    /// wall's extent along the through axis, clamped to the frame bounds on a side without air.
    /// </summary>
    private (double, double)? WallSpan(int w, int t, double ym)
    {
        double tm = (_min[t] + _max[t]) * 0.5;
        foreach (double wv in new[] { _min[w] - 0.25, _max[w] + 0.25 })
        {
            D3 p = default;
            p = p.With(w, wv).With(1, ym).With(t, tm);
            if (!Solid(p))
            {
                continue;
            }

            D3 fwd = default;
            fwd = fwd.With(t, 1);
            double reachUp = (_max[t] - tm) + 1.5, reachDown = (tm - _min[t]) + 1.5;
            double? up = FirstSolidToAir(p, fwd, reachUp);
            double? down = FirstSolidToAir(p, -fwd, reachDown);
            if (up is null && down is null)
            {
                continue;
            }

            double t2 = up is double u ? tm + u : _max[t];
            double t1 = down is double d ? tm - d : _min[t];
            if (t2 - t1 > 0.03)
            {
                return (t1, t2);
            }
        }

        return null;
    }

    /// <summary>The distance along <paramref name="dir"/> from a solid point to the first air (1/16 m steps, refined), or null.</summary>
    private double? FirstSolidToAir(D3 origin, D3 dir, double reach)
    {
        double prev = 0;
        for (double s = 1.0 / 16; s <= reach + 1e-9; s += 1.0 / 16)
        {
            if (!Solid(origin + (dir * s)))
            {
                double lo = prev, hi = s;
                while (hi - lo > Bisect)
                {
                    double mid = (lo + hi) * 0.5;
                    if (Solid(origin + (dir * mid)))
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                return hi;
            }

            prev = s;
        }

        return null;
    }

    /// <summary>Bisects between an open coordinate and a closed one to the boundary (returns the closed side's edge).</summary>
    private static double Edge(double open, double closed, Func<double, bool> isOpen)
    {
        if (isOpen(closed))
        {
            return closed;
        }

        double a = open, b = closed;
        while (Math.Abs(b - a) > Bisect)
        {
            double mid = (a + b) * 0.5;
            if (isOpen(mid))
            {
                a = mid;
            }
            else
            {
                b = mid;
            }
        }

        return (a + b) * 0.5;
    }

    private static (double, double) LongestRun(double from, double to, double step, Func<double, bool> open)
    {
        double bestA = 0, bestB = 0, runA = double.NaN;
        double last = from;
        for (double v = from + (step * 0.5); v <= to; v += step)
        {
            if (open(v))
            {
                if (double.IsNaN(runA))
                {
                    runA = v - (step * 0.5);
                }

                last = v + (step * 0.5);
                if (last - runA > bestB - bestA)
                {
                    bestA = runA;
                    bestB = last;
                }
            }
            else
            {
                runA = double.NaN;
            }
        }

        return (bestA, bestB);
    }

    private static (double, double) RunContaining(double from, double to, double step, double centre, Func<double, bool> open)
    {
        if (!open(centre))
        {
            return (centre, centre);
        }

        double a = centre, b = centre;
        while (a - step >= from - 1e-9 && open(a - step))
        {
            a -= step;
        }

        while (b + step <= to + 1e-9 && open(b + step))
        {
            b += step;
        }

        return (a - (step * 0.5), b + (step * 0.5));
    }

    /// <summary>
    /// The axis-aligning yaw: an area-weighted 1° histogram of horizontal face-normal directions mod 90°, its peak bin
    /// refined by the weighted mean of its neighbourhood; snapped to 0 within 0.05°, rounded to 0.01°.
    /// </summary>
    internal static double DominantYaw(IReadOnlyList<WorldPolyhedron> shapes)
    {
        var hist = new double[90];
        var sumAngle = new double[90];
        foreach (WorldPolyhedron s in shapes)
        {
            for (int fi = 0; fi < s.Faces.Length; fi++)
            {
                D3 n = s.Normals[fi];
                double h = Math.Sqrt((n.X * n.X) + (n.Z * n.Z));
                if (h < 0.3)
                {
                    continue;
                }

                double area = s.FaceArea(fi) * h;
                if (area <= 1e-9)
                {
                    continue;
                }

                double ang = Math.Atan2(n.X, n.Z) * 180.0 / Math.PI;
                ang %= 90.0;
                if (ang < 0)
                {
                    ang += 90.0;
                }

                int bin = Math.Clamp((int)Math.Floor(ang), 0, 89);
                hist[bin] += area;
                sumAngle[bin] += area * ang;
            }
        }

        int peak = 0;
        for (int i = 1; i < 90; i++)
        {
            if (hist[i] > hist[peak] + 1e-12)
            {
                peak = i;
            }
        }

        if (hist[peak] <= 1e-12)
        {
            return 0;
        }

        // Weighted mean over peak-1..peak+1, unwrapped around the peak.
        double wsum = 0, asum = 0;
        for (int d = -1; d <= 1; d++)
        {
            int b = (peak + d + 90) % 90;
            if (hist[b] <= 0)
            {
                continue;
            }

            double mean = sumAngle[b] / hist[b];
            double centre = peak + 0.5;
            double unwrapped = mean;
            if (unwrapped - centre > 45)
            {
                unwrapped -= 90;
            }
            else if (centre - unwrapped > 45)
            {
                unwrapped += 90;
            }

            wsum += hist[b];
            asum += hist[b] * unwrapped;
        }

        double theta = asum / wsum;
        theta %= 90.0;
        if (theta < 0)
        {
            theta += 90.0;
        }

        theta = Math.Round(theta, 2);
        if (theta < 0.05 || theta > 89.95)
        {
            theta = 0;
        }

        return theta;
    }

    internal static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return (n & 1) == 1 ? values[n / 2] : (values[(n / 2) - 1] + values[n / 2]) * 0.5;
    }

    internal static double Q(double v, double step)
    {
        double r = Math.Round(v / step, MidpointRounding.AwayFromZero) * step;
        return r == 0 ? 0 : r;
    }
}
