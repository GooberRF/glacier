using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Spatial;

// Function checks (the intent layer): every placed element should be there for a reason a player can feel. Measured on
// the compiled geometry and the brushes of any level, so hand-made and generated levels are judged alike:
//   * bridges: a deck serves a route when, for a real share of the floor on each side, crossing it is clearly shorter
//     than any other way to its far end (walk-graph catchments with the deck taken out). A bridge whose ends are joined
//     just as well by the floor below (stairs at both ends), by a bridge beside it, or whose ends lead nowhere else,
//     serves no route.
//   * props: free-standing prop clusters (small solid brushes standing on playable floor) are read for their theme fit
//     (rock props away from walls in built spaces, interiors or walled yards, do not belong) and,
//     as cover, for the lanes they break (cover with no long sightline past it protects against nothing).
//   * landmarks: how much of the level, and how many spaces, see them.
public sealed partial class SpatialAnalysis
{
    /// <summary>A bridge serves a full route when at least this much playable floor on each side is closer to its far end across it (m²; the kit's bar for a new bridge, reported as catchment_ok).</summary>
    public const double RouteMinCatchment = 150;

    /// <summary>A bridge whose ends the floor joins with a walk at most this many times its length (plus 2 m) is redundant.</summary>
    public const double RouteRedundantRatio = 1.2;

    /// <summary>Bridges at least this long (m) are set pieces and count toward G27; shorter ones are listed but not counted.</summary>
    public const double SetPieceSpan = 12;

    /// <summary>A side with less floor than this (m²) reaching the far end sooner across the bridge, and no item or spawn, is a dead end.</summary>
    public const double RouteDeadEndCatchment = 40;

    /// <summary>The least saving that counts as a shortcut (m); a fifth of the span when that is more.</summary>
    public const double RouteMinSaving = 4;

    /// <summary>Prop clusters stand at most this high above their floor (m); taller pieces are structure.</summary>
    public const double PropMaxHeight = 3.2;

    /// <summary>Prop clusters stand at least this high above their floor (m); lower pieces are kerbs, plinths and rubble flats.</summary>
    public const double PropMinHeight = 0.5;

    /// <summary>A prop at least this far from every wall and floor edge is free-standing (m).</summary>
    public const double FreeStanding = 2.0;

    /// <summary>Cover breaks a lane when an open sightline at least this long passes it (m).</summary>
    public const double LaneMinLength = 14;

    private const double PropMaxFoot = 8.0;   // a prop cluster's largest horizontal extent (m); bigger clusters are structure
    private const double BrushMaxFoot = 6.0;  // a prop brush's largest horizontal extent (m)

    /// <summary>One bridge's route reading: the floor on each side closer to the far end across it, the walk between its ends without it.</summary>
    internal sealed record BridgeRoute(SetPieceBridge Bridge, double CatchA, double CatchB, double Detour, double Saving, bool DetourUnder, double UnderArea, double UnderHigh, string? Fault, int EndA = -1, int EndB = -1);

    /// <summary>One free-standing (or wall-side) prop cluster as read for theme fit and cover purpose.</summary>
    internal sealed record PropRead(
        Vec3d Centre, Vec3d Min, Vec3d Max, double FloorY, double Height, int Brushes, double RockShare, bool Organic, string Context,
        double Clearance, double Lane, bool Cover, string? Fault, int[] Uids);

    private JsonObject? _purpose;

    /// <summary>The bridges' route readings (after <see cref="PurposeReport"/>).</summary>
    internal List<BridgeRoute> BridgeRoutes { get; } = new();

    /// <summary>The prop clusters read (after <see cref="PurposeReport"/>).</summary>
    internal List<PropRead> Props { get; } = new();

    /// <summary>
    /// The function report: bridges with their route catchments (and the purposeless ones), prop clusters with their theme
    /// fit and cover lanes (mismatched props, orphan cover), landmarks with their visibility, and the summary numbers the
    /// G27–G31 metrics read (flights, route ledges and the area connections too). Deterministic (no sampled stream).
    /// </summary>
    internal JsonObject PurposeReport()
    {
        if (_purpose is not null)
        {
            return _purpose;
        }

        SetPiecesReport();
        BridgeRoutes.Clear();
        foreach (SetPieceBridge b in SetPieceBridges)
        {
            BridgeRoutes.Add(ReadRoute(b));
        }

        Props.Clear();
        Props.AddRange(ReadProps());
        JsonArray landmarks = ReadLandmarks();
        ReadFlights();
        ReadLedges();
        JsonObject connections = ConnectionsReport();

        int purposeless = BridgeRoutes.Count(b => b.Fault is not null && b.Bridge.Span >= SetPieceSpan);
        int mismatched = Props.Count(p => p.Fault == "theme");
        int scattered = Props.Count(p => p.Fault == "scattered");
        int cover = Props.Count(p => p.Cover);
        int orphan = Props.Count(p => p.Fault == "orphan_cover");
        int hidden = landmarks.Count(l => l!["hidden"]!.GetValue<bool>());
        int purposelessFlights = FlightReads.Count(f => f.Kind is { } k && CountedFlightFaults.Contains(k));
        _purpose = new JsonObject
        {
            ["bridges_checked"] = BridgeRoutes.Count,
            ["purposeless_bridges"] = purposeless,
            ["props"] = Props.Count,
            ["free_standing_props"] = Props.Count(p => p.Clearance >= FreeStanding),
            ["mismatched_props"] = mismatched,
            ["scattered_rocks"] = scattered,
            ["cover_pieces"] = cover,
            ["orphan_cover"] = orphan,
            ["orphan_cover_fraction"] = R(orphan / (double)Math.Max(cover, 4)),
            ["landmarks_checked"] = landmarks.Count,
            ["hidden_landmarks"] = hidden,
            ["flights_checked"] = FlightReads.Count,
            ["purposeless_flights"] = purposelessFlights,
            ["flights_noted"] = FlightReads.Count(f => f.Fault is not null) - purposelessFlights,
            ["route_ledges"] = LedgeReads.Count,
            ["bridges"] = new JsonArray(BridgeRoutes.Select(b => (JsonNode)RouteJson(b)).ToArray()),
            ["mismatched"] = new JsonArray(Props.Where(p => p.Fault is "theme" or "scattered").Select(p => (JsonNode)PropJson(p)).ToArray()),
            ["orphans"] = new JsonArray(Props.Where(p => p.Fault == "orphan_cover").OrderBy(p => p.Lane).Select(p => (JsonNode)PropJson(p)).ToArray()),
            ["landmarks"] = landmarks,
            ["flights"] = new JsonArray(FlightReads.Select(f => (JsonNode)FlightJson(f)).ToArray()),
            ["ledges"] = new JsonArray(LedgeReads.OrderByDescending(l => l.Step).Select(l => (JsonNode)LedgeJson(l)).ToArray()),
            ["connections"] = connections,
        };
        return _purpose;
    }

    /// <summary>The function query: the report with the measurement header and the rules; lists capped at <paramref name="max"/> entries.</summary>
    public JsonObject Purpose(int max = 20)
    {
        max = Math.Clamp(max, 1, 200);
        var body = (JsonObject)PurposeReport().DeepClone();
        foreach (JsonNode? list in new[] { body["bridges"], body["mismatched"], body["orphans"], body["landmarks"], body["flights"], body["ledges"], body["connections"]?["list"] })
        {
            if (list is JsonArray a)
            {
                while (a.Count > max)
                {
                    a.RemoveAt(a.Count - 1);
                }
            }
        }

        body["rules"] = string.Create(CultureInfo.InvariantCulture,
            $"Bridge (walk graph without the deck): purposeless when redundant (the floor beside or below joins its ends with a walk at most {RouteRedundantRatio:0.##}x its length + 1 m) or leading nowhere (under {RouteDeadEndCatchment:0} m2 of floor on one side reaches the far end at least {RouteMinSaving:0} m, or a fifth of the span, sooner across it, no item or spawn waits within 15 m of that end, and that end leads on, walking or dropping, to under {4 * RouteDeadEndCatchment:0} m2); purposeless_bridges counts bridges spanning {SetPieceSpan:0} m or more; catchment_ok = at least {RouteMinCatchment:0} m2 on each side (the kit's bar for a new bridge). "
            + $"Prop: a cluster of small solid brushes standing {PropMinHeight:0.#}-{PropMaxHeight:0.#} m above playable floor (at most {PropMaxFoot:0} m across); free-standing at least {FreeStanding:0} m from every wall and floor edge. Theme mismatch: a free-standing rock prop (half its face area rock textures) in a built space (walls mostly built textures: an interior, or a yard walled all round under the sky); scattered rock: a free-standing rock prop anywhere that is not cover (rubble belongs against walls and edges). "
            + $"Cover: a free-standing prop 0.9-2.8 m tall; orphan cover has no open line of {LaneMinLength:0} m or more through it (both ways along one of 8 axes at 1.2 m: nothing to break). Landmark: hidden when fewer than 2 spaces see it. "
            + $"Flight: built stairs or a ramp climbing at least {FlightMinRise:0} m between landings (rock slopes left out). purposeless_flights counts flights that climb into a wall or onto a solid (no floor beyond the top step, under {FlightIntoSolid:0.#} m clear), are blocked (a wall or prop under {FlightBlocked:0.#} m from the first or top step), lead onto nothing (no landing beyond the top, only a drop) or lead into a dead-end pit in a bridge's gap (under {FlightPocket:0} m2, reached only by flights from the floor the bridge joins); flights_noted lists, without counting, redundant ones (another way up to the same floor on foot within {FlightRedundantRatio:0.#}x the length + 1 m of the foot) and dead ends (a pit or perch under {FlightPocket:0} m2 reached only by flights from one floor, empty or entered twice; a top leading to under {FlightDeadEnd:0} m2 with nothing on it). "
            + $"Route ledge: two landings of {LedgeMinLanding:0} m2 or more joined only by a {LedgeMinStep:0.#}-1.5 m step in a doorway or corridor (8 of 10 edges narrow), with no way round on foot within 60 m. "
            + $"Connections: areas are spaces of {ConnectionMinArea:0} m2 or more; a way out is a neighbouring connector piece or space leading to another area; poorly connected areas have at most {ConnectionFew} ways out.");
        return With(Header(), body);
    }

    private JsonObject RouteJson(BridgeRoute r) => new()
    {
        ["pos"] = P3(r.Bridge.Centre),
        ["end_a"] = r.EndA >= 0 ? P3(_s[r.EndA].P) : null,
        ["end_b"] = r.EndB >= 0 ? P3(_s[r.EndB].P) : null,
        ["from"] = P3(r.Bridge.From),
        ["to"] = P3(r.Bridge.To),
        ["span"] = R(r.Bridge.Span),
        ["catchment_a"] = R(r.CatchA),
        ["catchment_b"] = R(r.CatchB),
        ["detour"] = double.IsFinite(r.Detour) ? R(r.Detour) : null,
        ["saving"] = double.IsFinite(r.Saving) ? R(r.Saving) : null,
        ["detour_under"] = r.DetourUnder,
        ["floor_under"] = R(r.UnderArea),
        ["floor_under_raised"] = R(r.UnderHigh),
        ["catchment_ok"] = Math.Min(r.CatchA, r.CatchB) >= RouteMinCatchment,
        ["role"] = r.Fault is null ? "route" : null,
        ["fault"] = r.Fault,
    };

    private static JsonObject PropJson(PropRead p) => new()
    {
        ["pos"] = P3(new Vec3d(p.Centre.X, p.FloorY, p.Centre.Z)),
        ["min"] = P3(p.Min),
        ["max"] = P3(p.Max),
        ["height"] = R(p.Height),
        ["brushes"] = p.Brushes,
        ["rock_share"] = R(p.RockShare),
        ["context"] = p.Context,
        ["clearance"] = R(p.Clearance),
        ["lane"] = R(p.Lane),
        ["cover"] = p.Cover,
        ["fault"] = p.Fault,
        ["uids"] = new JsonArray(p.Uids.Take(8).Select(u => (JsonNode)u).ToArray()),
    };

    // ---- bridges: route catchments ---------------------------------------------------------------------

    private List<int>[]? _reverseAdj;

    /// <summary>The walk graph's edges reversed (playable samples only), for distances TO a sample.</summary>
    private List<int>[] ReverseAdj()
    {
        if (_reverseAdj is not null)
        {
            return _reverseAdj;
        }

        var r = new List<int>[_s.Count];
        foreach (int u in _w.PlayIdx)
        {
            foreach (WalkEdge e in _w.Adj[u])
            {
                if (_w.Play[e.To])
                {
                    (r[e.To] ??= new List<int>(4)).Add(u);
                }
            }
        }

        _reverseAdj = r;
        return r;
    }

    /// <summary>
    /// Walk distance from every playable sample TO <paramref name="target"/>, never through a <paramref name="blocked"/>
    /// sample; <paramref name="next"/> is each sample's next step toward the target (−1 at the target and unreached).
    /// </summary>
    private double[] DistanceTo(int target, bool[] blocked, out int[] next)
    {
        List<int>[] radj = ReverseAdj();
        var d = new double[_s.Count];
        next = new int[_s.Count];
        Array.Fill(d, double.PositiveInfinity);
        Array.Fill(next, -1);
        var pq = new PriorityQueue<int, double>();
        d[target] = 0;
        pq.Enqueue(target, 0);
        while (pq.TryDequeue(out int u, out double du))
        {
            if (du > d[u] || radj[u] is not { } into)
            {
                continue;
            }

            foreach (int v in into)
            {
                if (blocked[v])
                {
                    continue;
                }

                double nd = du + _s[u].P.Sub(_s[v].P).Length;
                if (nd < d[v])
                {
                    d[v] = nd;
                    next[v] = u;
                    pq.Enqueue(v, nd);
                }
            }
        }

        return d;
    }

    /// <summary>
    /// A bridge's route reading: with its deck taken out of the walk graph (every playable sample on the deck's strip at
    /// deck height), the playable floor that reaches the far end at least the saving sooner across the deck (each side),
    /// the walk between its ends, whether that walk passes under the deck, and the floor under its free span.
    /// </summary>
    private BridgeRoute ReadRoute(SetPieceBridge b)
    {
        if (b.Deck is not { Length: > 0 } deck || b.EndA < 0 || b.EndB < 0 || b.Frame is not { } f)
        {
            return new BridgeRoute(b, 0, 0, double.PositiveInfinity, double.NaN, false, 0, 0, "its ends were not found on walkable floor");
        }

        var blocked = new bool[_s.Count];
        foreach (int i in deck)
        {
            blocked[i] = true;
        }

        // The walk-on points: the floor just off each end of the deck nearest its centre line at deck height (not a rail
        // or chord top beside it).
        int endA = b.EndA, endB = b.EndB;
        double bestA = double.PositiveInfinity, bestB = double.PositiveInfinity, vm = (f.V0 + f.V1) / 2;
        foreach (int c in deck)
        {
            foreach (WalkEdge e in _w.Adj[c])
            {
                if (e.Link || e.Drop || blocked[e.To] || !_w.Play[e.To] || Math.Abs(_s[e.To].P.Y - b.DeckY) > 1.0)
                {
                    continue;
                }

                Vec3d p = _s[e.To].P;
                double u = f.U(p), score = Math.Abs(f.V(p) - vm) + (4 * Math.Abs(p.Y - b.DeckY));
                if (u < f.U0 - 0.2 && score < bestA)
                {
                    bestA = score;
                    endA = e.To;
                }
                else if (u > f.U1 + 0.2 && score < bestB)
                {
                    bestB = score;
                    endB = e.To;
                }
            }
        }

        double underArea = 0, underHigh = 0, lowest = double.PositiveInfinity;
        var under = new List<int>();
        foreach (int i in _w.PlayIdx)
        {
            Vec3d p = _s[i].P;
            double u = f.U(p), v = f.V(p);
            if (u < f.U0 - 0.5 || u > f.U1 + 0.5 || v < f.V0 - 1.0 || v > f.V1 + 1.0)
            {
                continue;
            }

            if (Math.Abs(p.Y - b.DeckY) <= 1.0)
            {
                blocked[i] = true;
            }
            else if (p.Y < b.DeckY - 2 && u >= f.U0 + 1 && u <= f.U1 - 1 && v >= f.V0 && v <= f.V1)
            {
                under.Add(i);
                lowest = Math.Min(lowest, p.Y);
            }
        }

        blocked[endA] = blocked[endB] = false; // the ends stand just off the deck
        foreach (int i in under)
        {
            underArea += _g * _g;
            if (_s[i].P.Y >= lowest + 1.5)
            {
                underHigh += _g * _g;
            }
        }

        double[] toA = DistanceTo(endA, blocked, out int[] nextA), toB = DistanceTo(endB, blocked, out _);
        double cross = _s[endA].P.Sub(_s[endB].P).Length;
        double margin = Math.Max(RouteMinSaving, 0.2 * cross);
        int nA = 0, nB = 0;
        foreach (int i in _w.PlayIdx)
        {
            if (blocked[i])
            {
                continue;
            }

            double a = toA[i], c = toB[i];
            if (double.IsFinite(a) && a + cross + margin <= c)
            {
                nA++;
            }
            else if (double.IsFinite(c) && c + cross + margin <= a)
            {
                nB++;
            }
        }

        // The walk around from the far end back to this one: does it pass under the deck?
        bool viaUnder = false;
        if (double.IsFinite(toA[endB]))
        {
            for (int s = endB, guard = 0; s >= 0 && guard < _s.Count; s = nextA[s], guard++)
            {
                Vec3d p = _s[s].P;
                double u = f.U(p), v = f.V(p);
                if (u >= f.U0 && u <= f.U1 && v >= f.V0 - 1 && v <= f.V1 + 1 && p.Y <= b.DeckY - 2)
                {
                    viaUnder = true;
                    break;
                }
            }
        }

        double cell = _g * _g;
        double catchA = nA * cell, catchB = nB * cell;
        double detour = Math.Min(toB[endA], toA[endB]);
        double saving = detour - cross;

        // A dead-end side still has a purpose when it holds a reward or a spawn: an item or a spawn within 15 m walk of
        // that end (a perch the bridge leads to).
        bool Reward(double[] to)
        {
            foreach (Vec3d p in _obj.Items.Select(i => i.P).Concat(_obj.Spawns.Select(sp => sp.P)))
            {
                int si = _w.Snap(p);
                if (si >= 0 && to[si] <= 15)
                {
                    return true;
                }
            }

            return false;
        }

        string? fault = null;
        // The floor the small side's end leads on to without the deck, walking or dropping (a drop-off end is a one-way route down).
        double Onward(int end)
        {
            var seen = new HashSet<int> { end };
            var q = new Queue<int>();
            q.Enqueue(end);
            while (q.Count > 0 && seen.Count * cell < 4 * RouteDeadEndCatchment)
            {
                foreach (WalkEdge e in _w.Adj[q.Dequeue()])
                {
                    if (_w.Play[e.To] && !blocked[e.To] && seen.Add(e.To))
                    {
                        q.Enqueue(e.To);
                    }
                }
            }

            return seen.Count * cell;
        }

        if (double.IsFinite(detour) && detour <= (RouteRedundantRatio * cross) + 1)
        {
            fault = string.Create(CultureInfo.InvariantCulture, $"redundant: the floor beside or below joins its ends almost as directly ({detour:0} m walk around vs {cross:0} m across)");
        }
        else if (Math.Min(catchA, catchB) < RouteDeadEndCatchment && !Reward(catchA <= catchB ? toA : toB)
            && Onward(catchA <= catchB ? endA : endB) < 4 * RouteDeadEndCatchment)
        {
            fault = string.Create(CultureInfo.InvariantCulture, $"leads nowhere: only {Math.Min(catchA, catchB):0} m2 of floor on one side reaches the far end sooner across it and no item or spawn waits there (its end is a dead end, or reached only from the floor it crosses)");
        }

        return new BridgeRoute(b, catchA, catchB, detour, saving, viaUnder, underArea, underHigh, fault, endA, endB);
    }

    // ---- props: theme fit and cover lanes -----------------------------------------------------------------

    /// <summary>True for a natural-rock texture name (stock rck_/rock/cliff/cave/boulder families).</summary>
    internal static bool RockTexture(string lower) =>
        lower.Contains("rck", StringComparison.Ordinal) || lower.Contains("rock", StringComparison.Ordinal) || lower.Contains("cliff", StringComparison.Ordinal)
        || lower.Contains("boulder", StringComparison.Ordinal) || lower.StartsWith("cav", StringComparison.Ordinal) || lower.Contains("_cave", StringComparison.Ordinal);

    /// <summary>The share of a brush's face area under natural-rock textures.</summary>
    private static double RockShare(Brush b)
    {
        Geometry g = b.Geometry;
        double rock = 0, all = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            Vec3 o = g.Vertices[f.Vertices[0].Index];
            double ax = 0, ay = 0, az = 0;
            for (int k = 1; k + 1 < f.Vertices.Count; k++)
            {
                Vec3 p = g.Vertices[f.Vertices[k].Index], q = g.Vertices[f.Vertices[k + 1].Index];
                double ux = p.X - o.X, uy = p.Y - o.Y, uz = p.Z - o.Z, vx = q.X - o.X, vy = q.Y - o.Y, vz = q.Z - o.Z;
                ax += (uy * vz) - (uz * vy);
                ay += (uz * vx) - (ux * vz);
                az += (ux * vy) - (uy * vx);
            }

            double area = 0.5 * Math.Sqrt((ax * ax) + (ay * ay) + (az * az));
            all += area;
            if (f.Texture >= 0 && f.Texture < g.Textures.Count && RockTexture(g.Textures[f.Texture].ToLowerInvariant()))
            {
                rock += area;
            }
        }

        return all > 0 ? rock / all : 0;
    }

    private IEnumerable<int> SamplesAt(double x, double z) =>
        _w.Lattice.TryGetValue(((int)Math.Floor(x / _g), (int)Math.Floor(z / _g)), out List<int>? l) ? l : Enumerable.Empty<int>();

    private bool PlayFloorNear(double x, double z, double y, double tol)
    {
        foreach (int i in SamplesAt(x, z))
        {
            if (_w.Play[i] && Math.Abs(_s[i].P.Y - y) <= tol)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Distance from a box's centre to its side along a horizontal unit direction.</summary>
    private static double ToSide(double hx, double hz, Vec3d dir)
    {
        double tx = Math.Abs(dir.X) > 1e-9 ? hx / Math.Abs(dir.X) : double.PositiveInfinity;
        double tz = Math.Abs(dir.Z) > 1e-9 ? hz / Math.Abs(dir.Z) : double.PositiveInfinity;
        return Math.Min(tx, tz);
    }

    /// <summary>
    /// The prop clusters: solid brushes at most 6 m across and 3.6 m tall, grouped when their boxes touch, kept when the
    /// group is at most 8 m across, stands 0.5-3.2 m above playable floor around it and is not a stair run.
    /// </summary>
    private List<PropRead> ReadProps()
    {
        var cand = new List<(Brush B, Vec3d Mn, Vec3d Mx)>();
        foreach (Brush b in _level.Brushes())
        {
            if (b.Geometry.Vertices.Count == 0 || (b.Flags & (uint)BrushFlags.Air) != 0)
            {
                continue;
            }

            (Vec3d mn, Vec3d mx, _) = BrushBox(b);
            double w = mx.X - mn.X, d = mx.Z - mn.Z, h = mx.Y - mn.Y;
            if (w <= BrushMaxFoot && d <= BrushMaxFoot && h <= PropMaxHeight + 0.4 && h >= 0.2)
            {
                cand.Add((b, mn, mx));
            }
        }

        // Group touching boxes (0.15 m tolerance) on a 4 m grid.
        int n = cand.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            for (int gx = (int)Math.Floor((cand[i].Mn.X - 0.15) / 4); gx <= (int)Math.Floor((cand[i].Mx.X + 0.15) / 4); gx++)
            {
                for (int gz = (int)Math.Floor((cand[i].Mn.Z - 0.15) / 4); gz <= (int)Math.Floor((cand[i].Mx.Z + 0.15) / 4); gz++)
                {
                    if (!grid.TryGetValue((gx, gz), out List<int>? cellList))
                    {
                        grid[(gx, gz)] = cellList = new List<int>();
                    }

                    foreach (int j in cellList)
                    {
                        if (cand[i].Mn.X <= cand[j].Mx.X + 0.15 && cand[j].Mn.X <= cand[i].Mx.X + 0.15 && cand[i].Mn.Y <= cand[j].Mx.Y + 0.15
                            && cand[j].Mn.Y <= cand[i].Mx.Y + 0.15 && cand[i].Mn.Z <= cand[j].Mx.Z + 0.15 && cand[j].Mn.Z <= cand[i].Mx.Z + 0.15)
                        {
                            parent[Find(i)] = Find(j);
                        }
                    }

                    cellList.Add(i);
                }
            }
        }

        var groups = Enumerable.Range(0, n).GroupBy(Find).Select(g => g.ToList()).ToList();
        var clusters = new List<(Vec3d Mn, Vec3d Mx, List<int> Members)>();
        foreach (List<int> g in groups)
        {
            var mn = new Vec3d(g.Min(i => cand[i].Mn.X), g.Min(i => cand[i].Mn.Y), g.Min(i => cand[i].Mn.Z));
            var mx = new Vec3d(g.Max(i => cand[i].Mx.X), g.Max(i => cand[i].Mx.Y), g.Max(i => cand[i].Mx.Z));
            if (mx.X - mn.X <= PropMaxFoot && mx.Z - mn.Z <= PropMaxFoot && mx.Y - mn.Y <= PropMaxHeight + 0.6)
            {
                clusters.Add((mn, mx, g));
            }
        }

        var reads = new PropRead?[clusters.Count];
        For(clusters.Count, c => reads[c] = ReadProp(clusters[c].Mn, clusters[c].Mx, clusters[c].Members.Select(i => cand[i].B).ToList()));
        return reads.Where(r => r is not null).Select(r => r!).OrderBy(r => r.Centre.X).ThenBy(r => r.Centre.Z).ThenBy(r => r.FloorY).ToList();
    }

    private PropRead? ReadProp(Vec3d mn, Vec3d mx, List<Brush> brushes)
    {
        // The floor it stands on: playable samples in a 1 m ring around its box near its base.
        var ring = new List<double>();
        var inside = new List<double>();
        for (double x = mn.X - 1.0 + (_g / 2); x <= mx.X + 1.0; x += _g)
        {
            for (double z = mn.Z - 1.0 + (_g / 2); z <= mx.Z + 1.0; z += _g)
            {
                bool inBox = x > mn.X && x < mx.X && z > mn.Z && z < mx.Z;
                foreach (int i in SamplesAt(x, z))
                {
                    double y = _s[i].P.Y;
                    if (!_w.Play[i])
                    {
                        continue;
                    }

                    if (inBox)
                    {
                        if (y > mn.Y + 0.25 && y <= mx.Y + 0.1)
                        {
                            inside.Add(y);
                        }
                    }
                    else if (y >= mn.Y - 0.75 && y <= mn.Y + 0.6)
                    {
                        ring.Add(y);
                    }
                }
            }
        }

        if (ring.Count < 3)
        {
            return null; // not standing on playable floor (on a shelf, in a wall, off the playable area)
        }

        double floorY = Med(ring);
        double height = mx.Y - floorY;
        if (height < PropMinHeight || height > PropMaxHeight)
        {
            return null;
        }

        if (inside.Select(y => Math.Floor((y - floorY) / 0.25)).Distinct().Count() >= 3)
        {
            return null; // walkable tops at three or more heights: a stair run or terrace, not a prop
        }

        double hx = (mx.X - mn.X) / 2, hz = (mx.Z - mn.Z) / 2;
        var c = new Vec3d((mn.X + mx.X) / 2, floorY, (mn.Z + mx.Z) / 2);

        // Clearance to walls and floor edges (8 directions, from the box's side at 1 m), and the longest lane past it
        // (16 directions at 1.2 m).
        double clearance = double.PositiveInfinity;
        Span<double> ray = stackalloc double[16];
        for (int k = 0; k < 16; k++)
        {
            Vec3d dir = SpaceSampler.Directions[k];
            double t0 = ToSide(hx, hz, dir) + 0.05;
            if (k % 2 == 0)
            {
                Vec3d o = new(c.X + (dir.X * t0), floorY + 1.0, c.Z + (dir.Z * t0));
                double wall = _sp.Cast(o, dir, 6, SpaceSampler.HitFilter.Any) ?? 6;
                double edge = 6;
                for (double t = 0.25; t <= wall; t += _g)
                {
                    if (!PlayFloorNear(o.X + (dir.X * t), o.Z + (dir.Z * t), floorY, 0.6))
                    {
                        edge = t;
                        break;
                    }
                }

                clearance = Math.Min(clearance, Math.Min(wall, edge));
            }

            Vec3d e = new(c.X + (dir.X * t0), floorY + 1.2, c.Z + (dir.Z * t0));
            ray[k] = (_sp.Cast(e, dir, 60, SpaceSampler.HitFilter.Any) ?? 60) + t0;
        }

        // The longest open line through it (both ways along one of 8 axes): the lane it breaks.
        double lane = 0;
        for (int k = 0; k < 8; k++)
        {
            lane = Math.Max(lane, ray[k] + ray[k + 8]);
        }

        // Context: open sky or a ceiling 20 m up over the floor around it, or walls mostly natural rock.
        int hits = 0, rockHits = 0;
        var top = new Vec3d(c.X, mx.Y + 0.05, c.Z);
        bool sky = _sp.Cast(top, new Vec3d(0, 1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.DownFacing, out int fi) is not double h
            || h + top.Y - floorY >= 20
            || (fi >= 0 && (_level.Geometry.Faces[_sp.Faces[fi].Source].Flags & (ushort)FaceFlags.ShowSky) != 0);

        // The walls around it, read above the props (3.4 m up, under the ceiling) so rock props do not read as rock walls.
        double wallY = floorY + 3.4;
        if (_sp.Cast(top, new Vec3d(0, 1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.DownFacing) is double up)
        {
            wallY = Math.Max(floorY + 2.0, Math.Min(wallY, top.Y + up - 0.3));
        }

        for (int k = 0; k < 16; k++)
        {
            Vec3d dir = SpaceSampler.Directions[k];
            Vec3d o = new(c.X + (dir.X * (ToSide(hx, hz, dir) + 0.05)), wallY, c.Z + (dir.Z * (ToSide(hx, hz, dir) + 0.05)));
            if (_sp.Cast(o, dir, 30, SpaceSampler.HitFilter.Any, out int wf) is not null && wf >= 0)
            {
                hits++;
                if (RockTexture(TexName(_level.Geometry.Faces[_sp.Faces[wf].Source])))
                {
                    rockHits++;
                }
            }
        }

        double rockWalls = hits > 0 ? rockHits / (double)hits : 0;
        // Built walls all round make a built space even under the sky (a walled yard); open ground has few walls in reach.
        string context = rockWalls >= 0.35 ? "rock_walls" : !sky ? "built_interior" : hits >= 8 ? "built_yard" : "open";
        bool organic = context is "rock_walls" or "open";
        double area = 0, rock = 0;
        foreach (Brush b in brushes)
        {
            (Vec3d bn, Vec3d bx, _) = BrushBox(b);
            double a = Math.Max(0.01, (bx.X - bn.X) * (bx.Z - bn.Z));
            area += a;
            rock += a * RockShare(b);
        }

        double rockShare = area > 0 ? rock / area : 0;
        bool cover = clearance >= FreeStanding && height >= 0.9 && height <= 2.8 && (mx.X - mn.X) * (mx.Z - mn.Z) >= 0.5;
        string? fault = rockShare >= 0.5 && !organic && clearance >= FreeStanding ? "theme"
            : rockShare >= 0.5 && clearance >= FreeStanding && !cover ? "scattered"
            : cover && lane < LaneMinLength ? "orphan_cover"
            : null;
        return new PropRead(c, mn, mx, floorY, height, brushes.Count, rockShare, organic, context, double.IsFinite(clearance) ? clearance : 6, lane, cover, fault,
            brushes.Select(b => b.Uid).OrderBy(u => u).ToArray());
    }

    // ---- landmarks: visibility -----------------------------------------------------------------------------

    /// <summary>Each landmark with the playable floor (every 4th lattice sample, within 120 m) and the spaces that see its upper part.</summary>
    private JsonArray ReadLandmarks()
    {
        var list = new JsonArray();
        if (SetPiecesReport()["landmarks"] is not JsonArray lms)
        {
            return list;
        }

        List<int> eyes = _w.PlayIdx.Where(i => _s[i].I % 2 == 0 && _s[i].K % 2 == 0).ToList();
        foreach (JsonNode? l in lms)
        {
            JsonArray pos = l!["pos"]!.AsArray();
            var basePt = new Vec3d(pos[0]!.GetValue<double>(), l["base_y"]!.GetValue<double>(), pos[2]!.GetValue<double>());
            double height = l["height"]!.GetValue<double>();
            JsonArray fp = l["footprint"]!.AsArray();
            double radius = (Math.Max(fp[0]!.GetValue<double>(), fp[1]!.GetValue<double>()) / 2) + 1.5;
            var target = new Vec3d(basePt.X, basePt.Y + (0.75 * height), basePt.Z);
            var seen = new bool[eyes.Count];
            For(eyes.Count, j =>
            {
                Vec3d eye = EyeOf(eyes[j]);
                Vec3d d = target.Sub(eye);
                double len = d.Length;
                if (len > 120 || len < 1)
                {
                    return;
                }

                Vec3d u = d.Scale(1 / len);
                if (_sp.Cast(eye, u, len, SpaceSampler.HitFilter.Any) is not double t)
                {
                    seen[j] = true;
                    return;
                }

                Vec3d hit = eye.Add(u.Scale(t));
                double hd = Math.Sqrt(((hit.X - basePt.X) * (hit.X - basePt.X)) + ((hit.Z - basePt.Z) * (hit.Z - basePt.Z)));
                seen[j] = hd <= radius && hit.Y >= basePt.Y + 2; // the first thing in the way is the landmark itself
            });
            var spaces = new HashSet<int>();
            int count = 0;
            for (int j = 0; j < eyes.Count; j++)
            {
                if (seen[j])
                {
                    count++;
                    int node = Spaces.NodeOf(eyes[j]);
                    if (node >= 0)
                    {
                        spaces.Add(node);
                    }
                }
            }

            double area = count * 4 * _g * _g;
            list.Add(new JsonObject
            {
                ["pos"] = P3(basePt),
                ["height"] = R(height),
                ["seen_from_area"] = R(area),
                ["seen_from_share"] = R(area / Math.Max(1, _w.PlayArea)),
                ["seen_from_spaces"] = spaces.Count,
                ["hidden"] = spaces.Count < 2,
                ["role"] = spaces.Count >= 2 ? "landmark" : null,
            });
        }

        return list;
    }
}
