using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

// E9 set-piece detection (DESIGN-QUALITY-SPEC §(e)): bridges with their spans, landmarks with their heights, atria
// and the largest spaces. The study's bridge proxy (the `bridges` section, kept for study parity) probes for drops
// only across each sample's chest-height travel axis and stops at the first face at knee height, so railings,
// truss members and see-through sides scatter it into fragments and ledges along rails read as bridges. E9 instead
// finds, per sample, the narrowest line (of 8) with a drop of at least 3 m on both sides, ignores obstacles that
// leave any of three heights open (rails, posts, truss panels), and measures each crossing on its deck surfaces.
public sealed partial class SpatialAnalysis
{
    /// <summary>A drop of at least this much beside a deck makes it a gap (m), as in the study's bridge proxy.</summary>
    public const double SetPieceMinDrop = 3.0;

    /// <summary>A bridge's free length over the gap is at least this (m); shorter elevated pieces are walkways.</summary>
    public const double BridgeMinFreeSpan = 10.0;

    /// <summary>A landmark rises at least this far above the floor around it (m).</summary>
    public const double LandmarkMinHeight = 12.0;

    /// <summary>An atrium's median headroom is at least this (m; spec §1 finding 10).</summary>
    public const double AtriumMinHeight = 12.0;

    private const double DeckReach = 8.5;          // how far across a deck to look for its edge, each side (m): the widest bridge deck, from one edge row to the other
    private const double DeckMaxWidth = 10.0;      // wider drop-sided floor is a platform, not a deck (m)
    private const double BridgeMinWidth = 1.5;     // a player-sized deck (narrower: beams, rail tops)
    private const double BridgeMaxWidth = 8.0;     // a deck, not a platform
    private const double SideAllowance = 1.0;      // the deck's measured width may take in side members' tops (truss chords, kerbs) up to this (m)

    private static readonly double[] WallHeights = { 0.45, 1.0, 1.8 };

    private JsonObject? _setPieces;

    /// <summary>One detected bridge (internal form, for metrics and tests).</summary>
    internal sealed record SetPieceBridge(
        Vec3d Centre, Vec3d From, Vec3d To, double Span, double GapWidth, double Width, double GapDepth, double DeckY,
        bool UnderPlayable, double? SpaceBelowArea, double? SpaceBelowLength, int? RoutesBetweenEnds);

    /// <summary>The bridges E9 found, the landmark bridge first (<see cref="Significance"/>).</summary>
    internal List<SetPieceBridge> SetPieceBridges { get; } = new();

    /// <summary>
    /// E9 set pieces: bridges (span, width, gap width and depth, the space below, routes between the ends), landmarks
    /// (height above the floor around them, footprint), atria (height, storeys crossing) and the largest spaces with
    /// their floor bounding boxes, plus the summary numbers the grade metrics read.
    /// </summary>
    internal JsonObject SetPiecesReport()
    {
        if (_setPieces is not null)
        {
            return _setPieces;
        }

        (JsonArray bridges, JsonArray walkways) = DetectBridges();
        JsonArray landmarks = DetectLandmarks();
        JsonArray atria = DetectAtria();
        JsonArray largest = LargestSpaces(5);

        static double? Max(JsonArray a, string key) => a.Count == 0 ? null : a.Max(x => x![key]!.GetValue<double>());
        _setPieces = new JsonObject
        {
            ["bridge_count"] = bridges.Count,
            ["bridge_span_max"] = Max(bridges, "span"),
            ["walkway_count"] = walkways.Count,
            ["landmark_count"] = landmarks.Count,
            ["landmark_height_max"] = Max(landmarks, "height"),
            ["atrium_count"] = atria.Count,
            ["atrium_height_max"] = Max(atria, "height"),
            ["bridges"] = bridges,
            ["walkways"] = walkways,
            ["landmarks"] = landmarks,
            ["atria"] = atria,
            ["largest_spaces"] = largest,
        };
        return _setPieces;
    }

    /// <summary>E9 query: the set-piece report with the measurement header; lists capped at <paramref name="max"/> entries.</summary>
    public JsonObject SetPieces(int max = 10)
    {
        max = Math.Clamp(max, 1, 50);
        var body = (JsonObject)SetPiecesReport().DeepClone();
        foreach (string key in new[] { "bridges", "walkways", "landmarks", "atria", "largest_spaces" })
        {
            if (body[key] is JsonArray a)
            {
                while (a.Count > max)
                {
                    a.RemoveAt(a.Count - 1);
                }
            }
        }

        body["rules"] = string.Create(CultureInfo.InvariantCulture, $"Bridge: a walkable deck {BridgeMinWidth:0.#}-{BridgeMaxWidth:0} m wide with drops of at least {SetPieceMinDrop:0} m on both sides along at least {BridgeMinFreeSpan:0} m (gap_width), its deck at least twice as long as wide, with floor at both ends (width read on the 0.5 m lattice, side members' tops up to {SideAllowance:0} m included); span = the deck end to end (its coplanar deck faces, never into a corridor or doorway it runs on into); bridges are listed landmark first (span times depth, weighted by the square root of the deck width); other drop-sided floor pieces of at least 4 m2 are walkways, listed with the reason. "
            + $"Landmark: a free-standing structure 2-40 m across, at least {LandmarkMinHeight:0} m above the floor around it, with open air above it. Atrium: an indoor space with median headroom at least {AtriumMinHeight:0} m; storeys = floor levels inside its volume.");
        return With(Header(), body);
    }

    // ---- bridges ------------------------------------------------------------------------------------

    /// <summary>One sample's deck reading: the line across it (direction index 0..7), the deck width along that line and the smaller drop.</summary>
    internal readonly record struct DeckRead(int Line, double Width, double Drop);

    private (JsonArray Bridges, JsonArray Walkways) DetectBridges()
    {
        List<int> play = _w.PlayIdx;
        var reads = new DeckRead?[play.Count];
        _ = FloorAt(0, 0, 0); // builds the lattice index before the parallel pass

        // Only floor within 5 m of its edge can have a drop on both sides within 5 m: a breadth-first pass from the
        // edge samples (a lattice neighbour without floor at their height) over level walk edges marks it.
        var edge = new bool[play.Count];
        For(play.Count, j =>
        {
            SpaceSampler.Sample s = _s[play[j]];
            edge[j] = !FloorAt(s.P.X + _g, s.P.Z, s.P.Y) || !FloorAt(s.P.X - _g, s.P.Z, s.P.Y) || !FloorAt(s.P.X, s.P.Z + _g, s.P.Y) || !FloorAt(s.P.X, s.P.Z - _g, s.P.Y);
        });
        var steps = new Dictionary<int, int>();
        var frontier = new Queue<int>();
        for (int j = 0; j < play.Count; j++)
        {
            if (edge[j])
            {
                steps[play[j]] = 0;
                frontier.Enqueue(play[j]);
            }
        }

        int reachSteps = (int)Math.Ceiling(DeckReach / _g);
        while (frontier.Count > 0)
        {
            int u = frontier.Dequeue();
            if (steps[u] >= reachSteps)
            {
                continue;
            }

            foreach (WalkEdge e in _w.Adj[u])
            {
                if (!e.Drop && !e.Link && _w.Play[e.To] && Math.Abs(_s[e.To].P.Y - _s[u].P.Y) < 0.6 && steps.TryAdd(e.To, steps[u] + 1))
                {
                    frontier.Enqueue(e.To);
                }
            }
        }

        For(play.Count, j => reads[j] = steps.ContainsKey(play[j]) ? ReadDeck(play[j]) : null);
        var deck = new Dictionary<int, DeckRead>();
        for (int j = 0; j < play.Count; j++)
        {
            if (reads[j] is { } r)
            {
                deck[play[j]] = r;
            }
        }

        var seen = new HashSet<int>();
        var found = new List<SetPieceBridge>();
        var walkways = new List<JsonObject>();
        foreach (int i in play)
        {
            if (!deck.ContainsKey(i) || !seen.Add(i))
            {
                continue;
            }

            var comp = new List<int>();
            var q = new Queue<int>();
            q.Enqueue(i);
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                comp.Add(u);
                foreach (WalkEdge e in _w.Adj[u])
                {
                    if (!e.Drop && !e.Link && deck.ContainsKey(e.To) && Math.Abs(_s[e.To].P.Y - _s[u].P.Y) < 0.6 && seen.Add(e.To))
                    {
                        q.Enqueue(e.To);
                    }
                }
            }

            if (comp.Count * _g * _g < 4)
            {
                continue;
            }

            if (MeasureBridge(comp, deck, out JsonObject? walkway) is { } b)
            {
                found.Add(b);
            }
            else if (walkway is not null)
            {
                walkways.Add(walkway);
            }
        }

        SetPieceBridges.Clear();
        SetPieceBridges.AddRange(found.OrderByDescending(Significance).ThenByDescending(b => b.Width).ThenByDescending(b => b.Span).ThenBy(b => b.Centre.X).ThenBy(b => b.Centre.Z));
        var list = new JsonArray();
        foreach (SetPieceBridge b in SetPieceBridges)
        {
            list.Add(new JsonObject
            {
                ["pos"] = P3(b.Centre),
                ["from"] = P3(b.From),
                ["to"] = P3(b.To),
                ["span"] = R(b.Span),
                ["gap_width"] = R(b.GapWidth),
                ["width"] = R(b.Width),
                ["gap_depth"] = R(b.GapDepth),
                ["deck_y"] = R(b.DeckY),
                ["under_playable"] = b.UnderPlayable,
                ["space_below_area"] = b.SpaceBelowArea is double sa ? R(sa) : null,
                ["space_below_length"] = b.SpaceBelowLength is double sl ? R(sl) : null,
                ["routes_between_ends"] = b.RoutesBetweenEnds,
            });
        }

        return (list, new JsonArray(walkways.OrderByDescending(w => w["area"]!.GetValue<double>()).Select(w => (JsonNode)w).ToArray()));
    }

    /// <summary>
    /// How much of a set piece a bridge is: its span times the drop under it (to 30 m), weighted by the square root of its
    /// deck width (to 8 m) — so a road bridge over a deep chasm outranks a long catwalk a storey above the floor and a
    /// footbridge beside it.
    /// </summary>
    internal static double Significance(SetPieceBridge b) => Math.Round(b.Span * Math.Min(b.GapDepth, 30) * Math.Sqrt(Math.Min(b.Width, BridgeMaxWidth)), 1);

    private Dictionary<(int, int), List<int>>? _latticeIndex;

    /// <summary>True when the lattice has a floor sample within 0.6 m of <paramref name="y"/> at the node nearest (x, z).</summary>
    private bool FloorAt(double x, double z, double y)
    {
        if (_latticeIndex is null)
        {
            var idx = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < _s.Count; i++)
            {
                (int, int) key = (_s[i].I, _s[i].K);
                if (!idx.TryGetValue(key, out List<int>? list))
                {
                    idx[key] = list = new List<int>(1);
                }

                list.Add(i);
            }

            _latticeIndex = idx;
        }

        if (_latticeIndex.TryGetValue(((int)Math.Round((x / _g) - 0.5), (int)Math.Round((z / _g) - 0.5)), out List<int>? at))
        {
            foreach (int i in at)
            {
                if (Math.Abs(_s[i].P.Y - y) <= 0.6)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The narrowest of the 8 lines through sample <paramref name="i"/> with a deck edge and a ≥ 3 m drop on both sides, or null.</summary>
    internal DeckRead? ReadDeck(int i)
    {
        DeckRead? best = null;
        for (int line = 0; line < 8; line++)
        {
            if (EdgeDrop(i, line) is not { } a || EdgeDrop(i, line + 8) is not { } b)
            {
                continue;
            }

            double w = a.Dist + b.Dist;
            if (w <= DeckMaxWidth && (best is null || w < best.Value.Width - 1e-9))
            {
                best = new DeckRead(line, w, Math.Min(a.Drop, b.Drop));
            }
        }

        return best;
    }

    /// <summary>
    /// The deck edge from sample <paramref name="i"/> along direction <paramref name="k"/> (0..15): the first point (0.5 m
    /// steps, within 5 m) with a drop of at least 3 m (or open void) below deck level; null when a wall (blocking at knee,
    /// chest and head height — a railing, post or truss panel leaves one open), solid, or a smaller step down comes first.
    /// </summary>
    internal (double Dist, double Drop)? EdgeDrop(int i, int k)
    {
        SpaceSampler.Sample s = _s[i];
        Vec3d p = s.P, dir = SpaceSampler.Directions[k], down = new(0, -1, 0);
        for (double d = 0.5; d <= DeckReach + 1e-9; d += 0.5)
        {
            double x = p.X + (dir.X * d), z = p.Z + (dir.Z * d);
            if (FloorAt(x, z, p.Y))
            {
                continue; // the floor goes on at deck level (no ray needed)
            }

            var o = new Vec3d(x, p.Y + 0.5, z);
            if (_sp.Cast(o, down, SetPieceMinDrop + 0.5, SpaceSampler.HitFilter.Any, out int fi) is double t)
            {
                if (_sp.Faces[fi].N.Y < -1e-3 || t - 0.5 > 0.6)
                {
                    return null; // inside solid (a face seen from behind), or a step down smaller than a gap
                }

                continue;
            }

            // A drop of at least 3 m here — unless a wall stands before it: blocked at knee, chest and head height (a
            // railing, post or truss panel leaves one of them open).
            double wall = s.Dist[k]; // chest height (SpaceSampler.ChestHeight), measured with the lattice
            foreach (double h in WallHeights)
            {
                if (wall < d && h != SpaceSampler.ChestHeight)
                {
                    wall = Math.Max(wall, _sp.Cast(new Vec3d(p.X, p.Y + h, p.Z), dir, d, SpaceSampler.HitFilter.Any) ?? double.PositiveInfinity);
                }
            }

            if (wall < d - 0.05)
            {
                return null;
            }

            // Its depth (nothing below = open void, unless the probe point is inside solid).
            if (_sp.Cast(o, down, 60, SpaceSampler.HitFilter.Any, out fi) is double far)
            {
                return _sp.Faces[fi].N.Y < -1e-3 ? null : (d, far - 0.5);
            }

            return _sp.Cast(o, new Vec3d(0, 1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.Any, out int up) is not null && _sp.Faces[up].N.Y < -1e-3
                ? (d, 60)
                : null;
        }

        return null;
    }

    /// <summary>Measures a drop-sided floor piece; null when it is not a bridge (too short, too narrow or wide, or not a crossing), with <paramref name="walkway"/> saying why.</summary>
    private SetPieceBridge? MeasureBridge(List<int> comp, Dictionary<int, DeckRead> deck, out JsonObject? walkway)
    {
        walkway = null;
        // The deck's axis: the principal axis of the piece (u along, v across).
        double mx = comp.Average(c => _s[c].P.X), mz = comp.Average(c => _s[c].P.Z);
        double sxx = 0, szz = 0, sxz = 0;
        foreach (int c in comp)
        {
            double dx = _s[c].P.X - mx, dz = _s[c].P.Z - mz;
            sxx += dx * dx;
            szz += dz * dz;
            sxz += dx * dz;
        }

        double th = 0.5 * Math.Atan2(2 * sxz, sxx - szz);
        double ax = Math.Cos(th), az = Math.Sin(th);
        double U(Vec3d p) => ((p.X - mx) * ax) + ((p.Z - mz) * az);
        double V(Vec3d p) => (-(p.X - mx) * az) + ((p.Z - mz) * ax);
        double u0 = comp.Min(c => U(_s[c].P)), u1 = comp.Max(c => U(_s[c].P));
        double v0 = comp.Min(c => V(_s[c].P)), v1 = comp.Max(c => V(_s[c].P));
        double gap = u1 - u0 + _g;
        double area = comp.Count * _g * _g;
        double width = area / gap;
        double deckY = Med(comp.Select(c => _s[c].P.Y));
        JsonObject Walkway(string why) => new()
        {
            ["pos"] = P3(new Vec3d(mx, deckY, mz)),
            ["area"] = R(area),
            ["gap_width"] = R(gap),
            ["width"] = R(width),
            ["reason"] = why,
        };

        string? shape = gap < BridgeMinFreeSpan ? string.Create(CultureInfo.InvariantCulture, $"shorter than {BridgeMinFreeSpan:0} m over the gap")
            : width < BridgeMinWidth ? string.Create(CultureInfo.InvariantCulture, $"narrower than {BridgeMinWidth:0.#} m")
            : width > BridgeMaxWidth + SideAllowance ? string.Create(CultureInfo.InvariantCulture, $"wider than {BridgeMaxWidth:0} m (a platform)")
            : null;
        if (shape is not null)
        {
            walkway = Walkway(shape);
            return null;
        }

        // A crossing: floor continues beyond both ends at walking height (not a drop off the end), inside the deck's strip.
        var set = new HashSet<int>(comp);
        int endLo = -1, endHi = -1;
        foreach (int c in comp)
        {
            foreach (WalkEdge e in _w.Adj[c])
            {
                if (e.Link || e.Drop || set.Contains(e.To) || !_w.Play[e.To] || Math.Abs(_s[e.To].P.Y - _s[c].P.Y) > 1.5)
                {
                    continue; // a walk on, not a drop off the end
                }

                Vec3d p = _s[e.To].P;
                double u = U(p), v = V(p);
                if (v < v0 - 1 || v > v1 + 1)
                {
                    continue;
                }

                if (u < u0 - 0.2 && (endLo < 0 || u < U(_s[endLo].P)))
                {
                    endLo = e.To;
                }
                else if (u > u1 + 0.2 && (endHi < 0 || u > U(_s[endHi].P)))
                {
                    endHi = e.To;
                }
            }
        }

        if (endLo < 0 || endHi < 0)
        {
            walkway = Walkway(endLo < 0 && endHi < 0
                ? "no walkable floor at either end (an island, reached by a jump, a ladder or a drop)"
                : "walkable floor at one end only (a jetty)");
            return null;
        }



        double depth = Med(comp.Select(c => deck[c].Drop));

        // The span: the deck end to end, read on its coplanar deck faces that stay within the strip; each end may
        // extend past the gap by at most twice the deck width (or 4 m), so a coplanar approach floor never counts.
        (double s0, double s1) = DeckExtent(comp, U, V, v0, v1, u0, u1);
        double ext = Math.Max(2 * width, 4);
        s0 = Math.Max(s0, u0 - (_g / 2) - ext);
        s1 = Math.Min(s1, u1 + (_g / 2) + ext);
        double vm = (v0 + v1) / 2;
        Vec3d At(double u) => new(mx + (u * ax) - (vm * az), deckY, mz + (u * az) + (vm * ax));

        // A deck that runs on into a corridor or doorway ends where the walls close in: the approach floor is not span.
        var across = new Vec3d(-az, 0, ax);
        double reachAcross = ((v1 - v0 + _g) / 2) + 1.5;
        bool Enclosed(double u)
        {
            Vec3d c = At(u);
            foreach (double h in WallHeights)
            {
                var o = new Vec3d(c.X, deckY + h, c.Z);
                if (_sp.Cast(o, across, reachAcross, SpaceSampler.HitFilter.Any) is null || _sp.Cast(o, across.Scale(-1), reachAcross, SpaceSampler.HitFilter.Any) is null)
                {
                    return false;
                }
            }

            return true;
        }

        double OpenEnd(double from, double to, double step)
        {
            for (double u = from; step > 0 ? u <= to : u >= to; u += step)
            {
                if (Enclosed(u) && Enclosed(u + step))
                {
                    return u;
                }
            }

            return to;
        }

        s1 = Math.Max(u1 + (_g / 2), OpenEnd(u1 + (_g / 2), s1, 0.5));
        s0 = Math.Min(u0 - (_g / 2), OpenEnd(u0 - (_g / 2), s0, -0.5));
        double span = Math.Max(gap, s1 - s0);
        if (span < 2 * width)
        {
            walkway = Walkway("not twice as long as wide (a platform)");
            return null;
        }

        // The space below mid-span and the routes between the two ends.
        Vec3d mid = At((u0 + u1) / 2);
        bool underPlayable = false;
        double? belowArea = null, belowLen = null;
        if (_sp.Cast(new Vec3d(mid.X, deckY - 1.5, mid.Z), new Vec3d(0, -1, 0), 200, SpaceSampler.HitFilter.UpFacing) is double down)
        {
            int si = _sp.NearestValid(new Vec3d(mid.X, deckY - 1.5 - down, mid.Z), 3.0, 1.0, 1.0);
            if (si >= 0 && _w.Play[si] && deckY - _s[si].P.Y >= SetPieceMinDrop - 0.5)
            {
                underPlayable = true;
                int node = Spaces.NodeOf(si);
                if (node >= 0)
                {
                    belowArea = Spaces.Area[node];
                    belowLen = SpaceShapes().FirstOrDefault(sh => sh.Node == node)?.Length
                        ?? PcaExtent(Spaces.Members[node].Select(m => _s[m].P)).Len;
                }
            }
        }

        int? routes = null;
        CellGraph cg = Cell8;
        int na = cg.NodeOfSample[endLo], nb = cg.NodeOfSample[endHi];
        if (na >= 0 && nb >= 0 && na != nb)
        {
            routes = cg.Graph.DisjointPaths(new[] { na }, new[] { nb }, 4, null).Count;
        }

        return new SetPieceBridge(mid, At(s0), At(s1), span, gap, width, depth, deckY, underPlayable, belowArea, belowLen, routes);
    }

    /// <summary>
    /// The deck's extent along its axis (u): the union of the floor faces under the piece and the coplanar floor faces
    /// that join it end to end inside its strip (across extent within 1 m of the piece's).
    /// </summary>
    private (double Lo, double Hi) DeckExtent(List<int> comp, Func<Vec3d, double> u, Func<Vec3d, double> v, double v0, double v1, double u0, double u1)
    {
        double lo = u0 - (_g / 2), hi = u1 + (_g / 2);
        var under = new List<int>();
        for (int n = 0; n < comp.Count; n += 3)
        {
            Vec3d p = _s[comp[n]].P;
            if (_sp.Cast(new Vec3d(p.X, p.Y + 0.05, p.Z), new Vec3d(0, -1, 0), 0.2, SpaceSampler.HitFilter.UpFacing, out int fi) is not null && fi >= 0)
            {
                under.Add(fi);
            }
        }

        if (under.Count == 0)
        {
            return (lo, hi);
        }

        bool InStrip(SpaceSampler.SFace f) => f.P.All(p => v(p) >= v0 - 1 && v(p) <= v1 + 1);
        (double A, double B) Range(SpaceSampler.SFace f) => (f.P.Min(p => u(p)), f.P.Max(p => u(p)));

        // The deck plane: the face under most of the piece.
        SpaceSampler.SFace plane = _sp.Faces[under.GroupBy(fi => fi).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key];
        var used = new HashSet<int>();
        foreach (int fi in under.Distinct())
        {
            SpaceSampler.SFace f = _sp.Faces[fi];
            if (InStrip(f))
            {
                (double a, double b) = Range(f);
                lo = Math.Min(lo, a);
                hi = Math.Max(hi, b);
                used.Add(fi);
            }
        }

        // Coplanar deck faces end to end (a deck split by the build), grown until nothing more joins.
        var candidates = new List<(int Fi, double A, double B)>();
        double minX = comp.Min(c => _s[c].P.X) - 40, maxX = comp.Max(c => _s[c].P.X) + 40;
        double minZ = comp.Min(c => _s[c].P.Z) - 40, maxZ = comp.Max(c => _s[c].P.Z) + 40;
        for (int fi = 0; fi < _sp.Faces.Count; fi++)
        {
            SpaceSampler.SFace f = _sp.Faces[fi];
            if (used.Contains(fi) || !f.Floor || f.MaxX < minX || f.MinX > maxX || f.MaxZ < minZ || f.MinZ > maxZ)
            {
                continue;
            }

            if (Math.Abs(f.N.X - plane.N.X) > 1e-3 || Math.Abs(f.N.Y - plane.N.Y) > 1e-3 || Math.Abs(f.N.Z - plane.N.Z) > 1e-3 || Math.Abs(f.D - plane.D) > 0.02 || !InStrip(f))
            {
                continue;
            }

            (double a, double b) = Range(f);
            candidates.Add((fi, a, b));
        }

        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((int fi, double a, double b) in candidates)
            {
                if (!used.Contains(fi) && a <= hi + 0.1 && b >= lo - 0.1 && (a < lo || b > hi))
                {
                    lo = Math.Min(lo, a);
                    hi = Math.Max(hi, b);
                    used.Add(fi);
                    grew = true;
                }
            }
        }

        return (lo, hi);
    }

    // ---- landmarks ----------------------------------------------------------------------------------

    /// <summary>
    /// Landmarks: free-standing structures — groups of 1 m cells without playable floor, enclosed by playable floor
    /// (obstacles within 4 m of each other joined), 2-40 m across — whose geometry rises at least 12 m above the floor
    /// around them with open air (sky, or a ceiling higher than their top) above most of that floor.
    /// </summary>
    private JsonArray DetectLandmarks()
    {
        List<int> play = _w.PlayIdx;
        const double C = 1.0;
        double gx0 = play.Min(i => _s[i].P.X) - 2, gz0 = play.Min(i => _s[i].P.Z) - 2;
        int nx = (int)Math.Ceiling((play.Max(i => _s[i].P.X) + 2 - gx0) / C) + 1, nz = (int)Math.Ceiling((play.Max(i => _s[i].P.Z) + 2 - gz0) / C) + 1;
        if ((long)nx * nz > 4_000_000)
        {
            return new JsonArray();
        }

        var floorAt = new int[nx * nz];
        Array.Fill(floorAt, -1);
        foreach (int i in play)
        {
            int cx = (int)Math.Floor((_s[i].P.X - gx0) / C), cz = (int)Math.Floor((_s[i].P.Z - gz0) / C);
            int c = (cz * nx) + cx;
            if (floorAt[c] < 0 || _s[i].P.Y < _s[floorAt[c]].P.Y)
            {
                floorAt[c] = i;
            }
        }

        // Obstacle components (8-connected cells without playable floor) that do not touch the grid border.
        var comp = new int[nx * nz];
        Array.Fill(comp, -1);
        var comps = new List<List<int>>();
        for (int c0 = 0; c0 < nx * nz; c0++)
        {
            if (floorAt[c0] >= 0 || comp[c0] >= 0)
            {
                continue;
            }

            var cells = new List<int>();
            bool border = false;
            var stack = new Stack<int>();
            stack.Push(c0);
            comp[c0] = comps.Count;
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                cells.Add(c);
                int cx = c % nx, cz = c / nx;
                border |= cx == 0 || cz == 0 || cx == nx - 1 || cz == nz - 1;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = cx + dx, z = cz + dz;
                        if (x < 0 || z < 0 || x >= nx || z >= nz)
                        {
                            continue;
                        }

                        int n = (z * nx) + x;
                        if (floorAt[n] < 0 && comp[n] < 0)
                        {
                            comp[n] = comps.Count;
                            stack.Push(n);
                        }
                    }
                }
            }

            comps.Add(border || cells.Count > 2500 ? new List<int>() : cells);
        }

        // Join obstacles within 4 m (a pylon's legs, a statue's plinth and figure) into structures.
        var boxes = comps.Select(cells => cells.Count == 0 ? (X0: 0, Z0: 0, X1: -1, Z1: -1)
            : (X0: cells.Min(c => c % nx), Z0: cells.Min(c => c / nx), X1: cells.Max(c => c % nx), Z1: cells.Max(c => c / nx))).ToList();
        var uf = new UnionFind(comps.Count);
        for (int a = 0; a < comps.Count; a++)
        {
            for (int b = a + 1; b < comps.Count && boxes[a].X1 >= 0; b++)
            {
                if (boxes[b].X1 >= 0 && boxes[b].X0 <= boxes[a].X1 + 4 && boxes[a].X0 <= boxes[b].X1 + 4 && boxes[b].Z0 <= boxes[a].Z1 + 4 && boxes[a].Z0 <= boxes[b].Z1 + 4)
                {
                    uf.Union(a, b);
                }
            }
        }

        var list = new List<(double Height, JsonObject Json)>();
        foreach (IGrouping<int, int> grp in Enumerable.Range(0, comps.Count).Where(k => comps[k].Count > 0).GroupBy(k => uf.Find(k)))
        {
            List<int> cells = grp.SelectMany(k => comps[k]).ToList();
            int x0 = cells.Min(c => c % nx), x1 = cells.Max(c => c % nx), z0 = cells.Min(c => c / nx), z1 = cells.Max(c => c / nx);
            if ((x1 - x0 + 1) * C > 40 || (z1 - z0 + 1) * C > 40 || (x1 - x0 + 1) * C < 2 || (z1 - z0 + 1) * C < 2)
            {
                continue; // a building block, or a pole or wall slab
            }

            // The floor around it: the playable sample of every cell bordering the structure.
            var cellSet = new HashSet<int>(cells);
            var ring = new HashSet<int>();
            foreach (int c in cells)
            {
                int cx = c % nx, cz = c / nx;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = cx + dx, z = cz + dz;
                        if (x >= 0 && z >= 0 && x < nx && z < nz && !cellSet.Contains((z * nx) + x) && floorAt[(z * nx) + x] >= 0)
                        {
                            ring.Add(floorAt[(z * nx) + x]);
                        }
                    }
                }
            }

            if (ring.Count < 4)
            {
                continue;
            }

            double[] ringY = ring.Select(i => _s[i].P.Y).OrderBy(y => y).ToArray();
            double baseY = Percentiles.Type7(ringY, 0.25);

            // Its top: the highest geometry lying (horizontally) within the structure's footprint grown by 2 m.
            double fx0 = gx0 + (x0 * C) - 2, fx1 = gx0 + ((x1 + 1) * C) + 2, fz0 = gz0 + (z0 * C) - 2, fz1 = gz0 + ((z1 + 1) * C) + 2;
            double top = double.NegativeInfinity;
            foreach (SpaceSampler.SFace f in _sp.Faces)
            {
                if (f.MinX >= fx0 && f.MaxX <= fx1 && f.MinZ >= fz0 && f.MaxZ <= fz1 && f.MaxY > top)
                {
                    top = f.MaxY;
                }
            }

            double height = top - baseY;
            if (!(height >= LandmarkMinHeight))
            {
                continue;
            }

            // Free-standing in open air: the ceiling over most of the floor around it is sky or above its top.
            int free = 0, sky = 0;
            foreach (int i in ring)
            {
                bool skyAbove = SkyAbove(i);
                sky += skyAbove ? 1 : 0;
                free += skyAbove || _s[i].Headroom >= top - _s[i].P.Y + 0.5 ? 1 : 0;
            }

            if (free < 0.5 * ring.Count)
            {
                continue;
            }

            var centre = new Vec3d(gx0 + ((x0 + x1 + 1) * C / 2), baseY, gz0 + ((z0 + z1 + 1) * C / 2));
            list.Add((height, new JsonObject
            {
                ["pos"] = P3(centre),
                ["height"] = R(height),
                ["base_y"] = R(baseY),
                ["top_y"] = R(top),
                ["footprint"] = new JsonArray((x1 - x0 + 1) * C, (z1 - z0 + 1) * C),
                ["open_air"] = sky >= 0.5 * ring.Count,
            }));
        }

        return new JsonArray(list.OrderByDescending(l => l.Height).Select(l => (JsonNode)l.Json).ToArray());
    }

    // ---- atria and the largest spaces ---------------------------------------------------------------

    /// <summary>Atria: indoor spaces (under 50% open sky or sky faces) of at least 60 m² whose median headroom is at least 12 m, with the floor levels inside their volume.</summary>
    private JsonArray DetectAtria()
    {
        var list = new List<(double Height, JsonObject Json)>();
        foreach (SpaceShape sh in SpaceShapes())
        {
            if (sh.Area < 60 || sh.Sky >= 0.5 || !(sh.HeadroomP50 >= AtriumMinHeight))
            {
                continue;
            }

            List<int> m = Spaces.Members[sh.Node];
            int probes = 0, skyFaces = 0;
            for (int n = 0; n < m.Count; n += Math.Max(1, m.Count / 64))
            {
                probes++;
                skyFaces += SkyAbove(m[n]) ? 1 : 0;
            }

            if (skyFaces * 2 >= probes)
            {
                continue; // under a sky ceiling: outdoor, not an atrium
            }

            double floorY = Med(m.Select(i => _s[i].P.Y));
            double topY = floorY + sh.HeadroomP50;
            var foot = new HashSet<(int, int)>(m.Select(i => ((int)Math.Floor(_s[i].P.X), (int)Math.Floor(_s[i].P.Z))));

            // Storeys crossing: floor levels (1 m bins of at least 4 m², 2.5 m apart) inside the footprint, above its floor.
            var bins = new SortedDictionary<int, int>();
            foreach (int i in _w.PlayIdx)
            {
                Vec3d p = _s[i].P;
                if (p.Y > floorY + 2.5 && p.Y < topY && foot.Contains(((int)Math.Floor(p.X), (int)Math.Floor(p.Z))))
                {
                    int b = (int)Math.Floor(p.Y);
                    bins[b] = bins.GetValueOrDefault(b) + 1;
                }
            }

            int storeys = 1;
            double last = floorY;
            foreach ((int b, int n) in bins)
            {
                if (n * _g * _g >= 4 && b - last >= 2.5)
                {
                    storeys++;
                    last = b;
                }
            }

            list.Add((sh.HeadroomP50, new JsonObject
            {
                ["pos"] = P3(Spaces.Cent[sh.Node]),
                ["height"] = R(sh.HeadroomP50),
                ["area"] = R(sh.Area),
                ["storeys"] = storeys,
                ["min"] = P3(new Vec3d(m.Min(i => _s[i].P.X), m.Min(i => _s[i].P.Y), m.Min(i => _s[i].P.Z))),
                ["max"] = P3(new Vec3d(m.Max(i => _s[i].P.X), topY, m.Max(i => _s[i].P.Z))),
            }));
        }

        return new JsonArray(list.OrderByDescending(l => l.Height).Select(l => (JsonNode)l.Json).ToArray());
    }

    /// <summary>The largest spaces: area, length and width, headroom, sky share, archetype and the floor's bounding box.</summary>
    private JsonArray LargestSpaces(int n)
    {
        var list = new JsonArray();
        foreach (SpaceShape sh in SpaceShapes().Take(n))
        {
            List<int> m = Spaces.Members[sh.Node];
            list.Add(new JsonObject
            {
                ["pos"] = P3(Spaces.Cent[sh.Node]),
                ["area"] = R(sh.Area),
                ["length"] = R(sh.Length),
                ["width"] = R(sh.Width),
                ["headroom"] = double.IsFinite(sh.HeadroomP50) ? R(sh.HeadroomP50) : null,
                ["sky"] = R(sh.Sky),
                ["archetype"] = sh.Archetype,
                ["min"] = P3(new Vec3d(m.Min(i => _s[i].P.X), m.Min(i => _s[i].P.Y), m.Min(i => _s[i].P.Z))),
                ["max"] = P3(new Vec3d(m.Max(i => _s[i].P.X), m.Max(i => _s[i].P.Y), m.Max(i => _s[i].P.Z))),
            });
        }

        return list;
    }
}
