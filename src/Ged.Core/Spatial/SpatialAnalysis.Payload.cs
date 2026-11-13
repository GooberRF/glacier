using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Spatial;

// Payload structure (DESIGN-QUALITY-SPEC §(h)): the cart track (the mover a Payload_Handler links, its keyframes in
// order), its checkpoints (keyframes whose triggered event is a Payload_Checkpoint), the segments between them, and per
// segment the flow and population numbers, the routes into each next segment, and the spawn hand-off.
public sealed partial class SpatialAnalysis
{
    /// <summary>One payload segment: the track from one checkpoint (or the start) to the next.</summary>
    internal sealed record PayloadSegment(int Index, List<Vec3d> Track, List<int> Nodes, double Area, double Loops8Per1000, double Loops4Per1000,
        double PocketShare, double ItemsPer100, double DressingPer100, int Spawns, List<(Vec3d Pos, double Area, double Share)> Pockets);

    /// <summary>One segment boundary (a checkpoint): entry routes into the next segment and the largest share one cell carries.</summary>
    internal sealed record PayloadBoundary(int Index, Vec3d Checkpoint, int Uid, int EntryRoutes, double MaxCellShare, Vec3d? WidestCell, int RedSpawns, int BlueSpawns);

    private JsonObject? _payloadReport;
    private bool _payloadDone;

    internal List<PayloadSegment> PayloadSegments { get; } = new();

    internal List<PayloadBoundary> PayloadBoundaries { get; } = new();

    /// <summary>True when the level has a Payload_Handler event.</summary>
    internal bool HasPayloadHandler => _level.Objects.Any(o => o.Kind == LevelObjectKind.Event && o.ClassName == "Payload_Handler");

    /// <summary>The payload report, or null when the level has no cart track (no handler linked to a mover with keyframes).</summary>
    internal JsonObject? PayloadReport()
    {
        if (_payloadDone)
        {
            return _payloadReport;
        }

        _payloadDone = true;
        LevelObject? handler = _level.Objects.FirstOrDefault(o => o.Kind == LevelObjectKind.Event && o.ClassName == "Payload_Handler");
        if (handler?.Model is not RflEvent he)
        {
            return null;
        }

        // The cart: the moving group one of whose keyframes (normally the start keyframe) the handler links.
        var linked = new HashSet<int>(he.Links);
        List<Keyframe>? track = _level.Sections<GroupsSection>().SelectMany(g => g.Groups)
            .Where(g => g.IsMoving != 0 && g.MovingData is { Keyframes.Count: >= 2 })
            .Select(g => g.MovingData!.Keyframes)
            .FirstOrDefault(k => k.Any(f => linked.Contains(f.Uid)));
        if (track is null)
        {
            _payloadReport = new JsonObject { ["handler"] = handler.Uid, ["track"] = null, ["note"] = "The Payload_Handler links no mover with at least 2 keyframes." };
            return _payloadReport;
        }

        Dictionary<int, LevelObject> byUid = new();
        foreach (LevelObject o in _level.Objects)
        {
            byUid.TryAdd(o.Uid, o);
        }

        var respawnTeam = _level.Sections<MpRespawnPointsSection>().SelectMany(s => s.Points)
            .GroupBy(p => p.Uid).ToDictionary(g => g.Key, g => g.First());

        // Split the track at checkpoint keyframes (never the start keyframe).
        var segments = new List<List<Vec3d>>();
        var checkpoints = new List<(Vec3d P, int Uid, RflEvent? Ev)>();
        var current = new List<Vec3d> { SpatialObjects.V(track[0].Position) };
        for (int k = 1; k < track.Count; k++)
        {
            Vec3d p = SpatialObjects.V(track[k].Position);
            current.Add(p);
            if (byUid.GetValueOrDefault(track[k].EventUid) is { ClassName: "Payload_Checkpoint" } cp)
            {
                segments.Add(current);
                checkpoints.Add((p, cp.Uid, cp.Model as RflEvent));
                current = new List<Vec3d> { p };
            }
        }

        if (current.Count >= 2)
        {
            segments.Add(current); // the finale after the last checkpoint
        }

        CellGraph cg = Cell8, c4 = Cell4;
        int SegmentOf(Vec3d p)
        {
            int best = 0;
            double bd = double.MaxValue;
            for (int s = 0; s < segments.Count; s++)
            {
                for (int i = 0; i + 1 < segments[s].Count; i++)
                {
                    double d = SegmentDistance(p, segments[s][i], segments[s][i + 1]);
                    if (d < bd)
                    {
                        bd = d;
                        best = s;
                    }
                }
            }

            return best;
        }

        int[] seg8 = Enumerable.Range(0, cg.N).Select(n => SegmentOf(cg.Cent[n])).ToArray();
        int[] seg4 = Enumerable.Range(0, c4.N).Select(n => SegmentOf(c4.Cent[n])).ToArray();
        PopulationReport();
        for (int s = 0; s < segments.Count; s++)
        {
            List<int> nodes = Enumerable.Range(0, cg.N).Where(n => seg8[n] == s).ToList();
            double area = nodes.Sum(n => cg.Area[n]);
            (double l8, double pocket, List<(Vec3d, double, double)> pockets) = SubgraphFlow(cg, nodes);
            (double l4, double _, _) = SubgraphFlow(c4, Enumerable.Range(0, c4.N).Where(n => seg4[n] == s).ToList());
            int items = _obj.Items.Count(i => SegmentOf(i.P) == s);
            int dressing = nodes.Sum(n => NodeDressing[n]);
            int spawns = _obj.Spawns.Count(x => x.Kind == "mp" && SegmentOf(x.P) == s);
            double per1000 = area > 0 ? 1000 / area : 0;
            PayloadSegments.Add(new PayloadSegment(s, segments[s], nodes, area, l8 * per1000, l4 * per1000, pocket,
                area > 0 ? items * 100 / area : 0, area > 0 ? dressing * 100 / area : 0, spawns, pockets));
        }

        // Boundaries: node-disjoint routes from each segment into the next, and how the crossing walk edges spread over cells.
        for (int b = 0; b < checkpoints.Count && b + 1 < segments.Count; b++)
        {
            List<int> from = PayloadSegments[b].Nodes, to = PayloadSegments[b + 1].Nodes;
            int routes = from.Count > 0 && to.Count > 0 ? cg.Graph.DisjointCount(from, to, 8) : 0;
            var perCell = new Dictionary<int, int>();
            int crossings = 0;
            foreach (int i in _w.PlayIdx)
            {
                int a = cg.NodeOfSample[i];
                if (a < 0 || seg8[a] != b)
                {
                    continue;
                }

                foreach (WalkEdge e in _w.Adj[i])
                {
                    int c = cg.NodeOfSample[e.To];
                    if (c >= 0 && seg8[c] == b + 1)
                    {
                        crossings++;
                        perCell[a] = perCell.GetValueOrDefault(a) + 1;
                        perCell[c] = perCell.GetValueOrDefault(c) + 1;
                    }
                }
            }

            double share = 0;
            Vec3d? widest = null;
            if (crossings > 0)
            {
                KeyValuePair<int, int> top = perCell.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First();
                share = top.Value / (double)crossings;
                widest = cg.CellCentre(top.Key);
            }

            int red = 0, blue = 0;
            foreach (int uid in checkpoints[b].Ev?.Links ?? new List<int>())
            {
                if (respawnTeam.GetValueOrDefault(uid) is { } rp && _w.Snap(SpatialObjects.V(rp.Position)) is int si && si >= 0 && _w.Play[si])
                {
                    if (rp.RedTeam != 0)
                    {
                        red++;
                    }

                    if (rp.BlueTeam != 0)
                    {
                        blue++;
                    }
                }
            }

            PayloadBoundaries.Add(new PayloadBoundary(b, checkpoints[b].P, checkpoints[b].Uid, routes, share, widest, red, blue));
        }

        double[] areas = PayloadSegments.Select(s => s.Area).ToArray();
        _payloadReport = new JsonObject
        {
            ["handler"] = handler.Uid,
            ["track_keyframes"] = track.Count,
            ["track_length"] = R(track.Zip(track.Skip(1), (a, c) => SpatialObjects.V(a.Position).Sub(SpatialObjects.V(c.Position)).Length).Sum()),
            ["checkpoints"] = checkpoints.Count,
            ["segments"] = PayloadSegments.Count,
            ["segment_balance"] = areas.Length > 0 && areas.Max() > 0 ? R(areas.Min() / areas.Max()) : null,
            ["segment_loop_score_min"] = PayloadSegments.Count > 0 ? R(PayloadSegments.Min(s => Math.Max(s.Loops8Per1000 / 0.5, s.Loops4Per1000 / 1.5))) : null,
            ["segment_pocket_max"] = PayloadSegments.Count > 0 ? R(PayloadSegments.Max(s => s.PocketShare)) : null,
            ["segment_items_min"] = PayloadSegments.Count > 0 ? R(PayloadSegments.Min(s => s.ItemsPer100)) : null,
            ["segment_dressing_min"] = PayloadSegments.Count > 0 ? R(PayloadSegments.Min(s => s.DressingPer100)) : null,
            ["boundary_routes_min"] = PayloadBoundaries.Count > 0 ? PayloadBoundaries.Min(b => b.EntryRoutes) : null,
            ["boundary_cell_share_max"] = PayloadBoundaries.Count > 0 ? R(PayloadBoundaries.Max(b => b.MaxCellShare)) : null,
            ["handoff_coverage"] = PayloadBoundaries.Count > 0 ? R(PayloadBoundaries.Count(b => b.RedSpawns > 0 && b.BlueSpawns > 0) / (double)PayloadBoundaries.Count) : null,
            ["segment_list"] = new JsonArray(PayloadSegments.Select(s => (JsonNode)new JsonObject
            {
                ["index"] = s.Index, ["area"] = R(s.Area), ["loops8_per1000"] = R(s.Loops8Per1000), ["loops4_per1000"] = R(s.Loops4Per1000),
                ["pocket_share"] = R(s.PocketShare), ["items_per100"] = R(s.ItemsPer100), ["dressing_per100"] = R(s.DressingPer100), ["spawns"] = s.Spawns,
                ["start"] = P3(s.Track[0]), ["end"] = P3(s.Track[^1]),
            }).ToArray()),
            ["boundary_list"] = new JsonArray(PayloadBoundaries.Select(b => (JsonNode)new JsonObject
            {
                ["index"] = b.Index, ["checkpoint_uid"] = b.Uid, ["pos"] = P3(b.Checkpoint), ["entry_routes"] = b.EntryRoutes,
                ["max_cell_share"] = R(b.MaxCellShare), ["red_spawns"] = b.RedSpawns, ["blue_spawns"] = b.BlueSpawns,
            }).ToArray()),
        };
        return _payloadReport;
    }

    /// <summary>Loops (cycle rank minus filled squares, ≥ 0) and pocket share of the subgraph a node set induces.</summary>
    /// <summary>
    /// A segment's own flow: its route loops (cycle rank less the filled squares) and its dead-end pockets — pieces of
    /// floor that a single cell cuts off from the rest of the segment, under 10% of its area (G13's rule), with no way
    /// out except back through that cell. A piece that also leaves the segment (a flank stair that runs on into the next
    /// leg, or the near strip of a room that straddles the checkpoint line, whose other exits lie past the line) is a
    /// route, not a dead end: the track-distance split cuts such pieces off inside the segment only.
    /// </summary>
    private static (double Loops, double Pocket, List<(Vec3d Pos, double Area, double Share)> Pockets) SubgraphFlow(CellGraph cg, List<int> nodes)
    {
        if (nodes.Count == 0)
        {
            return (0, 0, new List<(Vec3d, double, double)>());
        }

        var index = new Dictionary<int, int>();
        var sub = new UGraph();
        var area = new List<double>();
        foreach (int n in nodes)
        {
            index[n] = sub.AddNode();
            area.Add(cg.Area[n]);
        }

        foreach (int n in nodes)
        {
            foreach (int m in cg.Graph.Adj[n])
            {
                if (index.TryGetValue(m, out int j))
                {
                    sub.AddEdge(index[n], j);
                }
            }
        }

        int squares = 0;
        foreach (int a in nodes)
        {
            (int cx, int cz) = cg.CellOf[a];
            foreach (int b in cg.Graph.Adj[a].Where(x => index.ContainsKey(x) && cg.CellOf[x] == (cx + 1, cz)))
            {
                foreach (int c in cg.Graph.Adj[a].Where(x => index.ContainsKey(x) && cg.CellOf[x] == (cx, cz + 1)))
                {
                    foreach (int d in cg.Graph.Adj[b].Where(x => index.ContainsKey(x) && cg.CellOf[x] == (cx + 1, cz + 1)))
                    {
                        if (cg.Graph.Adj[c].Contains(d))
                        {
                            squares++;
                        }
                    }
                }
            }
        }

        double loops = Math.Max(0, sub.EdgeCount - sub.N + sub.Components() - squares);

        // Exits: cells with a walk edge to floor outside the segment.
        var exit = new bool[sub.N];
        foreach (int n in nodes)
        {
            exit[index[n]] = cg.Graph.Adj[n].Any(m => !index.ContainsKey(m));
        }

        double total = area.Sum();
        var pocket = new bool[sub.N];
        foreach (int v in sub.Articulation().OrderBy(x => x))
        {
            List<List<int>> comps = sub.ComponentsWithout(v);
            if (comps.Count < 2)
            {
                continue;
            }

            foreach (List<int> c in comps.OrderByDescending(c => c.Sum(i => area[i])).ThenBy(c => c.Min()).Skip(1))
            {
                if (c.Sum(i => area[i]) / total < 0.10 && !c.Any(i => exit[i]))
                {
                    foreach (int i in c)
                    {
                        pocket[i] = true;
                    }
                }
            }
        }

        // Connected pocket groups, largest first, for the locations.
        var groups = new List<(Vec3d Pos, double Area, double Share)>();
        var seen = new bool[sub.N];
        for (int i = 0; i < sub.N; i++)
        {
            if (!pocket[i] || seen[i])
            {
                continue;
            }

            var comp = new List<int>();
            var st = new Stack<int>();
            st.Push(i);
            seen[i] = true;
            while (st.Count > 0)
            {
                int u = st.Pop();
                comp.Add(u);
                foreach (int w in sub.Adj[u].Where(w => pocket[w] && !seen[w]))
                {
                    seen[w] = true;
                    st.Push(w);
                }
            }

            double a = comp.Sum(k => area[k]);
            Vec3d c = a > 0
                ? new Vec3d(comp.Sum(k => cg.Cent[nodes[k]].X * area[k]) / a, comp.Sum(k => cg.Cent[nodes[k]].Y * area[k]) / a, comp.Sum(k => cg.Cent[nodes[k]].Z * area[k]) / a)
                : cg.Cent[nodes[comp[0]]];
            groups.Add((c, a, total > 0 ? a / total : 0));
        }

        double share = total > 0 ? Enumerable.Range(0, sub.N).Where(k => pocket[k]).Sum(k => area[k]) / total : 0;
        return (loops, share, groups.OrderByDescending(g => g.Area).ToList());
    }

    private static double SegmentDistance(Vec3d p, Vec3d a, Vec3d b)
    {
        Vec3d ab = b.Sub(a);
        double len2 = ab.Dot(ab);
        double t = len2 > 1e-12 ? Math.Clamp(p.Sub(a).Dot(ab) / len2, 0, 1) : 0;
        return p.Sub(a.Add(ab.Scale(t))).Length;
    }
}
