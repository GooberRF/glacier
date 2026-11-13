using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

// Locations an agent can act on (DESIGN-QUALITY-SPEC §(d)): chokepoint cell centres, pocket centroids, bare cell
// centres, the longest sightlines' endpoints, exposed spawns, items with their nearest-neighbour distance, …
public sealed partial class SpatialAnalysis
{
    /// <summary>The location kinds <see cref="Locations"/> knows.</summary>
    public static IReadOnlyList<string> LocationKinds { get; } = new[]
    {
        "spaces", "sightlines", "chokepoints", "pockets", "dead_ends", "items", "spawns_far_from_items", "undressed_cells",
        "bare_cells", "exposed_spawns", "exposed_spots", "far_from_cover", "flags", "hills", "segments", "segment_pockets", "boundaries", "links",
    };

    /// <summary>Up to <paramref name="max"/> locations of one kind, worst first; every entry has <c>kind</c> and <c>pos</c> ([x, y, z] metres).</summary>
    public JsonArray Locations(string kind, int max = 8)
    {
        var arr = new JsonArray();
        foreach (JsonObject o in LocationList(kind).Take(Math.Max(0, max)))
        {
            o["kind"] = kind;
            arr.Add(o);
        }

        return arr;
    }

    private IEnumerable<JsonObject> LocationList(string kind)
    {
        switch (kind)
        {
            case "spaces":
                foreach (SpaceShape sh in SpaceShapes().Take(16))
                {
                    yield return new JsonObject
                    {
                        ["pos"] = P3(Spaces.Cent[sh.Node]), ["area"] = R(sh.Area), ["length"] = R(sh.Length), ["width"] = R(sh.Width),
                        ["headroom_p50"] = double.IsNaN(sh.HeadroomP50) ? null : R(sh.HeadroomP50), ["sky"] = R(sh.Sky), ["archetype"] = sh.Archetype,
                        ["neighbours"] = Spaces.Graph.Adj[sh.Node].Count,
                    };
                }

                break;
            case "sightlines":
                SightReport();
                foreach ((Vec3d from, Vec3d to, double len) in LongestSightlines)
                {
                    yield return new JsonObject { ["pos"] = P3(from), ["to"] = P3(to), ["length"] = R(len) };
                }

                break;
            case "chokepoints":
                foreach ((int node, double share) in Cell8.Cuts.Chokes.OrderByDescending(c => c.CutShare).ThenBy(c => c.Node))
                {
                    yield return new JsonObject { ["pos"] = P3(Cell8.CellCentre(node)), ["cuts_off"] = R(share), ["cell"] = Cell8.CellSize };
                }

                break;
            case "pockets":
                foreach ((List<int> nodes, double share) in Cell8.Cuts.PocketGroups(Cell8.Graph, Cell8.Area))
                {
                    double a = nodes.Sum(n => Cell8.Area[n]);
                    var c = new Vec3d(nodes.Sum(n => Cell8.Cent[n].X * Cell8.Area[n]) / a, nodes.Sum(n => Cell8.Cent[n].Y * Cell8.Area[n]) / a, nodes.Sum(n => Cell8.Cent[n].Z * Cell8.Area[n]) / a);
                    yield return new JsonObject { ["pos"] = P3(c), ["area"] = R(a), ["share"] = R(share) };
                }

                break;
            case "dead_ends":
                foreach (int n in Enumerable.Range(0, Spaces.N).Where(i => Spaces.IsSpace[i] && Spaces.Graph.Adj[i].Count <= 1).OrderByDescending(i => Spaces.Area[i]).ThenBy(i => i))
                {
                    yield return new JsonObject { ["pos"] = P3(Spaces.Cent[n]), ["area"] = R(Spaces.Area[n]) };
                }

                break;
            case "items":
                ItemReport();
                foreach (var it in ItemDetails.OrderByDescending(i => double.IsNaN(i.MajorNearest) ? -1 : i.MajorNearest).ThenBy(i => i.Uid))
                {
                    yield return new JsonObject
                    {
                        ["pos"] = P3(it.P), ["uid"] = it.Uid, ["class"] = it.Cls, ["category"] = it.Cat,
                        ["nearest_major"] = double.IsNaN(it.MajorNearest) ? null : R(it.MajorNearest),
                        ["traffic_pct"] = it.Traffic < 0 ? null : R(it.Traffic), ["in_pocket"] = it.Pocket,
                    };
                }

                break;
            case "spawns_far_from_items":
                SpawnReport();
                foreach (var s in SpawnDetails.Where(s => double.IsFinite(s.ToItem)).OrderByDescending(s => s.ToItem).ThenBy(s => s.Uid))
                {
                    yield return new JsonObject { ["pos"] = P3(s.P), ["uid"] = s.Uid, ["nearest_item"] = R(s.ToItem) };
                }

                break;
            case "exposed_spawns":
                SpawnReport();
                foreach (var s in SpawnDetails.OrderByDescending(s => s.Exposure + (0.1 * s.Sees.Count)).ThenBy(s => s.Uid))
                {
                    yield return new JsonObject
                    {
                        ["pos"] = P3(s.P), ["uid"] = s.Uid, ["exposure"] = R(s.Exposure), ["nearest_spawn"] = double.IsFinite(s.Nearest) && s.Nearest < double.MaxValue ? R(s.Nearest) : null,
                        ["sees_spawns"] = new JsonArray(s.Sees.Select(u => (JsonNode)u).ToArray()),
                    };
                }

                break;
            case "exposed_spots":
                SightReport();
                foreach ((Vec3d p, double x) in ExposureSamples.Where(e => e.Exposure > 0.35).OrderByDescending(e => e.Exposure).ThenBy(e => e.P.X).ThenBy(e => e.P.Z))
                {
                    yield return new JsonObject { ["pos"] = P3(p), ["exposure"] = R(x) };
                }

                break;
            case "undressed_cells":
            case "bare_cells":
                PopulationReport();
                bool bare = kind == "bare_cells";
                foreach (int n in Enumerable.Range(0, Cell8.N).Where(n => Cell8.Area[n] >= 8 && NodeDressing[n] == 0 && (!bare || NodePlanes[n] <= 5))
                    .OrderByDescending(n => Cell8.Area[n]).ThenBy(n => n))
                {
                    yield return new JsonObject { ["pos"] = P3(Cell8.CellCentre(n)), ["area"] = R(Cell8.Area[n]), ["planes"] = NodePlanes[n] };
                }

                break;
            case "far_from_cover":
                double[] d = CoverDistance;
                foreach (int n in Enumerable.Range(0, Cell8.N).Where(n => Cell8.Area[n] >= 8)
                    .Select(n => (N: n, Far: Cell8.Members[n].Max(i => Math.Min(d[i], 50))))
                    .Where(x => x.Far > 6).OrderByDescending(x => x.Far).ThenBy(x => x.N).Select(x => x.N))
                {
                    yield return new JsonObject { ["pos"] = P3(Cell8.CellCentre(n)), ["max_distance_to_cover"] = R(Cell8.Members[n].Max(i => Math.Min(d[i], 50))) };
                }

                break;
            case "flags":
                if (Flags() is { } f)
                {
                    JsonObject? ctf = RoutesReport()["ctf"] as JsonObject;
                    yield return new JsonObject { ["pos"] = P3(f.Red.P), ["uid"] = f.Red.Uid, ["team"] = "red", ["routes"] = ctf?["cell_routes"]?.DeepClone(), ["balance"] = ctf?["spawn_flag_balance"]?.DeepClone() };
                    yield return new JsonObject { ["pos"] = P3(f.Blue.P), ["uid"] = f.Blue.Uid, ["team"] = "blue", ["distance"] = ctf?["flag_euclid"]?.DeepClone() };
                }

                break;
            case "hills":
                var hills = RoutesReport()["hills"] as JsonArray;
                List<(Vec3d P, double Radius, int Uid)> hp = HillPositions();
                for (int i = 0; i < hp.Count; i++)
                {
                    JsonNode? h = hills?[i];
                    yield return new JsonObject
                    {
                        ["pos"] = P3(hp[i].P), ["uid"] = hp[i].Uid, ["radius"] = R(hp[i].Radius),
                        ["approaches"] = h?["approaches_cell"]?.DeepClone(), ["cover"] = h?["hill_cover_fraction"]?.DeepClone(),
                    };
                }

                break;
            case "segments":
                PayloadReport();
                foreach (PayloadSegment s in PayloadSegments)
                {
                    yield return new JsonObject
                    {
                        ["pos"] = P3(s.Track[0]), ["to"] = P3(s.Track[^1]), ["segment"] = s.Index, ["area"] = R(s.Area),
                        ["loops8_per1000"] = R(s.Loops8Per1000), ["pocket_share"] = R(s.PocketShare), ["items_per100"] = R(s.ItemsPer100),
                        ["dressing_per100"] = R(s.DressingPer100), ["spawns"] = s.Spawns,
                    };
                }

                break;
            case "segment_pockets":
                PayloadReport();
                foreach ((PayloadSegment s, (Vec3d pos, double area, double share)) in PayloadSegments
                    .SelectMany(s => s.Pockets.Select(p => (s, p))).OrderByDescending(x => x.p.Share).ThenBy(x => x.s.Index))
                {
                    yield return new JsonObject { ["pos"] = P3(pos), ["segment"] = s.Index, ["area"] = R(area), ["share"] = R(share) };
                }

                break;
            case "boundaries":
                PayloadReport();
                foreach (PayloadBoundary b in PayloadBoundaries)
                {
                    yield return new JsonObject
                    {
                        ["pos"] = P3(b.Checkpoint), ["checkpoint_uid"] = b.Uid, ["entry_routes"] = b.EntryRoutes, ["max_cell_share"] = R(b.MaxCellShare),
                        ["widest_cell"] = b.WidestCell is { } w ? P3(w) : null, ["red_spawns"] = b.RedSpawns, ["blue_spawns"] = b.BlueSpawns,
                    };
                }

                break;
            case "links":
                foreach ((MovementLink link, int from, int to) in _w.Links)
                {
                    yield return new JsonObject { ["pos"] = P3(_s[from].P), ["to"] = P3(_s[to].P), ["link"] = link.Kind, ["uid"] = link.Uid, ["one_way"] = link.OneWay };
                }

                break;
            default:
                throw new SpatialException("usage", $"Unknown location kind '{kind}'.", $"Kinds: {string.Join(", ", LocationKinds)}.");
        }
    }
}
