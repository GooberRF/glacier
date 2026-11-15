using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

/// <summary>A region of the level: an axis-aligned box, or everything within a radius of a point (horizontally).</summary>
public sealed record SpatialRegion(Vec3d Min, Vec3d Max, Vec3d? Centre = null, double Radius = 0)
{
    /// <summary>A box from two corners (any order).</summary>
    public static SpatialRegion Box(Vec3d a, Vec3d b) =>
        new(new Vec3d(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), new Vec3d(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));

    /// <summary>Everything within <paramref name="radius"/> metres of <paramref name="centre"/> horizontally, ±<paramref name="radius"/> vertically.</summary>
    public static SpatialRegion Around(Vec3d centre, double radius) =>
        new(centre.Sub(new Vec3d(radius, radius, radius)), centre.Add(new Vec3d(radius, radius, radius)), centre, radius);

    public bool Contains(Vec3d p)
    {
        if (p.X < Min.X || p.X > Max.X || p.Y < Min.Y || p.Y > Max.Y || p.Z < Min.Z || p.Z > Max.Z)
        {
            return false;
        }

        return Centre is not { } c || ((p.X - c.X) * (p.X - c.X)) + ((p.Z - c.Z) * (p.Z - c.Z)) <= Radius * Radius;
    }

    public JsonObject ToJson() => Centre is { } c
        ? new JsonObject { ["around"] = SpatialAnalysis.P3(c), ["radius"] = Radius }
        : new JsonObject { ["min"] = SpatialAnalysis.P3(Min), ["max"] = SpatialAnalysis.P3(Max) };
}

// The agent-facing queries (DESIGN-QUALITY-SPEC §(e) E1, E3–E8): walk, routes, layout graphs, sightlines, cover,
// cells, area report, spawn and item reports. Every result is plain JSON, shared verbatim by Lua, the CLI and MCP.
public sealed partial class SpatialAnalysis
{
    /// <summary>The measurement header every query result carries: geometry freshness, lattice pitch, links.</summary>
    public JsonObject Header() => new()
    {
        ["geometry"] = _level.Freshness.ToJson(),
        ["pitch"] = _g,
        ["samples"] = _s.Count,
        ["playable_area"] = R(_w.PlayArea),
        ["movement_links"] = _o.MovementLinks,
    };

    private JsonObject With(JsonObject header, JsonObject body)
    {
        foreach (KeyValuePair<string, JsonNode?> kv in body.ToList())
        {
            body.Remove(kv.Key);
            header[kv.Key] = kv.Value;
        }

        return header;
    }

    private bool InRegion(int sample, SpatialRegion? region) => region is null || region.Contains(_s[sample].P);

    // ---- E1 walk ------------------------------------------------------------------------------------

    /// <summary>
    /// E1: reachable floor. Components (area, spawns, items), the spawns and items that do not stand on playable floor,
    /// the movement links, and — with <paramref name="from"/> — the floor reachable from that point and its farthest spot.
    /// </summary>
    public JsonObject WalkQuery(Vec3d? from = null)
    {
        var compArea = new Dictionary<int, int>();
        foreach (int i in Enumerable.Range(0, _s.Count).Where(i => _s[i].Valid))
        {
            compArea[_w.Comp[i]] = compArea.GetValueOrDefault(_w.Comp[i]) + 1;
        }

        var spawnComp = new Dictionary<int, int>();
        var unreachableSpawns = new JsonArray();
        for (int i = 0; i < _obj.Spawns.Count; i++)
        {
            int si = _w.SpawnSample[i];
            if (si >= 0)
            {
                spawnComp[_w.Comp[si]] = spawnComp.GetValueOrDefault(_w.Comp[si]) + 1;
            }

            if (si < 0 || !_w.Play[si])
            {
                unreachableSpawns.Add(new JsonObject { ["uid"] = _obj.Spawns[i].Uid, ["kind"] = _obj.Spawns[i].Kind, ["pos"] = P3(_obj.Spawns[i].P), ["reason"] = si < 0 ? "no floor under it" : "on floor not joined to the playable floor" });
            }
        }

        var itemComp = new Dictionary<int, int>();
        var unreachableItems = new JsonArray();
        foreach (var it in _obj.Items)
        {
            int si = _w.Snap(it.P);
            if (si >= 0)
            {
                itemComp[_w.Comp[si]] = itemComp.GetValueOrDefault(_w.Comp[si]) + 1;
            }

            if (si < 0 || !_w.Play[si])
            {
                unreachableItems.Add(new JsonObject { ["uid"] = it.Uid, ["class"] = it.Cls, ["pos"] = P3(it.P), ["reason"] = si < 0 ? "no floor under it" : "on floor not joined to the playable floor" });
            }
        }

        var comps = new JsonArray();
        foreach (KeyValuePair<int, int> kv in compArea.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(8))
        {
            Vec3d p = _s[kv.Key].P;
            comps.Add(new JsonObject
            {
                ["area"] = R(kv.Value * _g * _g), ["playable"] = _w.Play[kv.Key], ["spawns"] = spawnComp.GetValueOrDefault(kv.Key),
                ["items"] = itemComp.GetValueOrDefault(kv.Key), ["pos"] = P3(p),
            });
        }

        var body = new JsonObject
        {
            ["valid_area"] = R(_s.Count(x => x.Valid) * _g * _g),
            ["components"] = comps,
            ["unreachable_spawns"] = unreachableSpawns,
            ["unreachable_items"] = unreachableItems,
            ["links"] = LinksReport(),
        };
        if (from is { } f)
        {
            int s = _w.SnapPlay(f);
            if (s < 0)
            {
                body["from"] = new JsonObject { ["pos"] = P3(f), ["on_floor"] = false };
            }
            else
            {
                double[] d = _w.Dijkstra(new[] { s });
                List<int> reach = _w.PlayIdx.Where(i => double.IsFinite(d[i])).ToList();
                int far = reach.OrderByDescending(i => d[i]).ThenBy(i => i).First();
                body["from"] = new JsonObject
                {
                    ["pos"] = P3(f), ["on_floor"] = true, ["snapped"] = P3(_s[s].P),
                    ["reachable_area"] = R(reach.Count * _g * _g), ["reachable_fraction"] = R(reach.Count / (double)_w.PlayIdx.Count),
                    ["farthest"] = new JsonObject { ["pos"] = P3(_s[far].P), ["walk_distance"] = R(d[far]) },
                };
            }
        }

        return With(Header(), body);
    }

    // ---- E3 routes ----------------------------------------------------------------------------------

    /// <summary>
    /// E3: routes between two points: the shortest walk path (length and waypoints every ~4 m) and up to <paramref name="k"/>
    /// node-disjoint routes on the 8 m cell graph (lengths and cell waypoints), shortest first.
    /// </summary>
    public JsonObject Routes(Vec3d a, Vec3d b, int k = 3)
    {
        k = Math.Clamp(k, 1, 8);
        int sa = _w.SnapPlay(a), sb = _w.SnapPlay(b);
        if (sa < 0 || sb < 0)
        {
            throw new SpatialException("no_floor", $"The {(sa < 0 ? "start" : "end")} point is not on playable floor.", "Pass points standing on walkable floor (a spawn, an item, a floor position).");
        }

        double[] d = _w.Dijkstra(new[] { sa }, out int[] prev);
        var body = new JsonObject { ["from"] = P3(_s[sa].P), ["to"] = P3(_s[sb].P), ["straight_distance"] = R(_s[sa].P.Sub(_s[sb].P).Length) };
        if (!double.IsFinite(d[sb]))
        {
            body["reachable"] = false;
            body["routes"] = new JsonArray();
            return With(Header(), body);
        }

        var path = new List<int>();
        for (int v = sb; v >= 0; v = prev[v])
        {
            path.Add(v);
        }

        path.Reverse();
        var wps = new JsonArray();
        double acc = 0, last = -1e9;
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0)
            {
                acc += _s[path[i]].P.Sub(_s[path[i - 1]].P).Length;
            }

            if (acc - last >= 4 || i == path.Count - 1)
            {
                wps.Add(P3(_s[path[i]].P));
                last = acc;
            }
        }

        body["reachable"] = true;
        body["walk_distance"] = R(d[sb]);
        body["shortest"] = new JsonObject { ["length"] = R(d[sb]), ["waypoints"] = wps };
        CellGraph cg = Cell8;
        int na = cg.NodeOfSample[sa], nb = cg.NodeOfSample[sb];
        var routes = new JsonArray();
        if (na >= 0 && nb >= 0 && na != nb)
        {
            List<List<int>> disjoint = cg.Graph.DisjointPaths(new[] { na }, new[] { nb }, k, null);
            foreach (List<int> r in disjoint.Where(r => r.Count > 0).OrderBy(r => CellPathLength(cg, r)))
            {
                routes.Add(new JsonObject
                {
                    ["length"] = R(CellPathLength(cg, r)),
                    ["cells"] = r.Count,
                    ["waypoints"] = new JsonArray(r.Select(n => (JsonNode)P3(cg.Cent[n])).ToArray()),
                });
            }

            body["disjoint_routes"] = disjoint.Count;
        }
        else
        {
            body["disjoint_routes"] = na == nb ? 1 : 0;
        }

        body["routes"] = routes;
        return With(Header(), body);
    }

    private static double CellPathLength(CellGraph cg, List<int> p)
    {
        double s = 0;
        for (int i = 1; i < p.Count; i++)
        {
            s += cg.Cent[p[i]].Sub(cg.Cent[p[i - 1]]).Length;
        }

        return s;
    }

    // ---- E4 graphs ----------------------------------------------------------------------------------

    /// <summary>
    /// E4: a layout graph — the 8 m or 4 m cell graph, or the space/connector graph: size, loops, chokepoints (with the
    /// share each cuts off), pockets, dead ends, and up to <paramref name="maxNodes"/> nodes (inside <paramref name="region"/>).
    /// </summary>
    public JsonObject Graph(string kind = "cell", int scale = 8, SpatialRegion? region = null, int maxNodes = 200)
    {
        var body = new JsonObject { ["kind"] = kind };
        var nodes = new JsonArray();
        int total = 0;
        if (kind == "space")
        {
            SpaceGraph sg = Spaces;
            CutReport cuts = sg.Cuts;
            JsonObject rep = SpacesReport();
            foreach (string key in new[] { "nodes", "spaces", "connectors", "loops", "loops_per1000m2", "dead_end_spaces", "dead_end_connectors", "choke_nodes_10pct", "pocket_area_fraction", "largest_space_fraction", "archetypes", "archetype_entropy" })
            {
                body[key] = rep[key]?.DeepClone();
            }

            var shapes = SpaceShapes().ToDictionary(s => s.Node);
            foreach (int n in Enumerable.Range(0, sg.N).Where(n => region is null || region.Contains(sg.Cent[n])).OrderByDescending(n => sg.Area[n]).ThenBy(n => n))
            {
                total++;
                if (nodes.Count >= maxNodes)
                {
                    continue;
                }

                nodes.Add(new JsonObject
                {
                    ["id"] = n, ["pos"] = P3(sg.Cent[n]), ["space"] = sg.IsSpace[n], ["area"] = R(sg.Area[n]),
                    ["neighbours"] = new JsonArray(sg.Graph.Adj[n].OrderBy(x => x).Select(x => (JsonNode)x).ToArray()),
                    ["choke"] = cuts.Chokes.Any(c => c.Node == n), ["pocket"] = cuts.Pocket[n],
                    ["archetype"] = shapes.TryGetValue(n, out SpaceShape? sh) ? sh.Archetype : null,
                });
            }
        }
        else if (kind == "cell")
        {
            if (scale is not (8 or 4))
            {
                throw new SpatialException("usage", $"Cell graphs come at 8 m or 4 m, not {scale} m.", "Use scale = 8 (layout) or 4 (fine loops).");
            }

            CellGraph cg = scale == 8 ? Cell8 : Cell4;
            CutReport cuts = cg.Cuts;
            body["scale"] = scale;
            body["nodes"] = cg.N;
            body["edges"] = cg.E;
            body["loops"] = cg.B1();
            body["loops_per1000m2"] = R(cg.B1() / (_w.PlayArea / 1000));
            body["chokepoints"] = new JsonArray(cuts.Chokes.OrderByDescending(c => c.CutShare).ThenBy(c => c.Node)
                .Select(c => (JsonNode)new JsonObject { ["id"] = c.Node, ["pos"] = P3(cg.CellCentre(c.Node)), ["cuts_off"] = R(c.CutShare) }).ToArray());
            body["pocket_area_fraction"] = R(cuts.PocketArea);
            body["pockets"] = new JsonArray(cuts.PocketGroups(cg.Graph, cg.Area).Take(16).Select(p => (JsonNode)new JsonObject
            {
                ["pos"] = P3(cg.Cent[p.Nodes.OrderByDescending(n => cg.Area[n]).First()]), ["area"] = R(p.Nodes.Sum(n => cg.Area[n])), ["share"] = R(p.Share), ["cells"] = p.Nodes.Count,
            }).ToArray());
            body["dead_ends"] = Enumerable.Range(0, cg.N).Count(i => cg.Graph.Adj[i].Count == 1);
            if (scale == 8)
            {
                PopulationReport();
            }

            foreach (int n in Enumerable.Range(0, cg.N).Where(n => region is null || region.Contains(cg.Cent[n])))
            {
                total++;
                if (nodes.Count >= maxNodes)
                {
                    continue;
                }

                var o = new JsonObject
                {
                    ["id"] = n, ["pos"] = P3(cg.CellCentre(n)), ["floor_y"] = R(cg.Cent[n].Y), ["area"] = R(cg.Area[n]),
                    ["headroom"] = double.IsNaN(cg.Headroom[n]) ? null : R(cg.Headroom[n]),
                    ["neighbours"] = new JsonArray(cg.Graph.Adj[n].OrderBy(x => x).Select(x => (JsonNode)x).ToArray()),
                    ["pocket"] = cuts.Pocket[n],
                };
                if (scale == 8)
                {
                    o["dressing"] = NodeDressing[n];
                    o["planes"] = NodePlanes[n];
                }

                nodes.Add(o);
            }
        }
        else
        {
            throw new SpatialException("usage", $"Unknown graph kind '{kind}'.", "Use kind = \"cell\" (with scale 8 or 4) or \"space\".");
        }

        body["region"] = region?.ToJson();
        body["listed"] = nodes.Count;
        body["total_in_region"] = total;
        body["truncated"] = total > nodes.Count;
        body["node_list"] = nodes;
        return With(Header(), body);
    }

    // ---- E5 sightlines ------------------------------------------------------------------------------

    /// <summary>
    /// E5: sightlines and exposure. The level-wide figures (engagement distances, longest sightline per spot, visible-pair
    /// shares, exposure) with the longest sightlines and most exposed spots; with <paramref name="region"/>, the same
    /// measured from up to 200 spots inside it (16 rays each; exposure against the whole floor within 50 m).
    /// </summary>
    public JsonObject Sightlines(SpatialRegion? region = null)
    {
        JsonObject sight = SightReport();
        var body = new JsonObject
        {
            ["engagement"] = sight["engagement_distance"]?.DeepClone(),
            ["longest_per_spot"] = sight["longest_ray_per_spot"]?.DeepClone(),
            ["visible_pair_fraction"] = sight["pair_visible_fraction"]?.DeepClone(),
            ["visible_within40"] = sight["pair_visible_fraction_within40"]?.DeepClone(),
            ["exposure"] = sight["exposure50"]?.DeepClone(),
            ["exposed_area_fraction"] = sight["exposed_area_fraction"]?.DeepClone(),
            ["longest"] = Locations("sightlines", 8),
            ["exposed_spots"] = Locations("exposed_spots", 8),
        };
        if (region is not null)
        {
            List<int> inside = _w.PlayIdx.Where(i => InRegion(i, region)).ToList();
            int step = Math.Max(1, inside.Count / 200);
            List<int> spots = _o.StableSampling
                ? StableOrder.Where(i => InRegion(i, region)).Take(200).ToList()
                : inside.Where((_, j) => j % step == 0).Take(200).ToList();
            var longest = new double[spots.Count];
            For(spots.Count, j =>
            {
                Vec3d e = EyeOf(spots[j]);
                double mx = 0;
                foreach (Vec3d dir in SpaceSampler.Directions)
                {
                    mx = Math.Max(mx, _sp.Cast(e, dir, 200, SpaceSampler.HitFilter.Any) ?? 200);
                }

                longest[j] = mx;
            });
            var rng = new Random(_o.Seed);
            var draws = _o.StableSampling ? spots.Select(i => DrawStable(_s[i].P, 50, 60, i)).ToList() : spots.Select(i =>
            {
                var pairs = new List<(Vec3d, Vec3d)>();
                Vec3d e = EyeOf(i);
                for (int t = 0; t < 240 && pairs.Count < 60; t++)
                {
                    Vec3d q = EyeOf(_w.PlayIdx[rng.Next(_w.PlayIdx.Count)]);
                    if (q.Sub(e).Length <= 50)
                    {
                        pairs.Add((e, q));
                    }
                }

                return pairs;
            }).ToList();
            double[] expo = EvaluateExposure(draws);
            body["region"] = new JsonObject
            {
                ["bounds"] = region.ToJson(),
                ["floor_area"] = R(inside.Count * _g * _g),
                ["spots"] = spots.Count,
                ["longest_per_spot"] = Dist(longest),
                ["exposure"] = Dist(expo),
                ["exposure_radius"] = 50,
                ["exposed_fraction"] = spots.Count > 0 ? R(expo.Count(x => x > 0.35) / (double)spots.Count) : null,
                ["most_exposed"] = new JsonArray(spots.Select((s, j) => (S: s, X: expo[j])).OrderByDescending(x => x.X).Take(5)
                    .Select(x => (JsonNode)new JsonObject { ["pos"] = P3(_s[x.S].P), ["exposure"] = R(x.X) }).ToArray()),
            };
        }

        return With(Header(), body);
    }

    // ---- E6 cover and cells -------------------------------------------------------------------------

    /// <summary>E6: cover — the share of floor beside cover, the walk distance to cover, and the cells farthest from it (in <paramref name="region"/>).</summary>
    public JsonObject Cover(SpatialRegion? region = null)
    {
        JsonObject level = CoverReport();
        List<int> inside = _w.PlayIdx.Where(i => InRegion(i, region)).ToList();
        double[] d = CoverDistance;
        var body = new JsonObject
        {
            ["level"] = level.DeepClone(),
            ["region"] = region?.ToJson(),
            ["floor_area"] = R(inside.Count * _g * _g),
            ["cover_fraction"] = inside.Count > 0 ? R(inside.Count(i => CoverSamples[i]) / (double)inside.Count) : null,
            ["dist_to_cover"] = Dist(inside.Select(i => Math.Min(d[i], 50))),
            ["dist_to_cover_open"] = Dist(inside.Where(i => _s[i].Width >= 8).Select(i => Math.Min(d[i], 50))),
            ["far_from_cover"] = new JsonArray(LocationList("far_from_cover").Where(o => region is null || region.Contains(PosOf(o))).Take(12).Select(o => { o["kind"] = "far_from_cover"; return (JsonNode)o; }).ToArray()),
        };
        return With(Header(), body);
    }

    /// <summary>E6: the 8 m cells (in <paramref name="region"/>): dressing count, visible planes, undressed / bare flags — the input to kit.dress.</summary>
    public JsonObject Cells(SpatialRegion? region = null, int max = 200)
    {
        PopulationReport();
        CellGraph cg = Cell8;
        var list = new JsonArray();
        int total = 0, undressed = 0, bare = 0;
        double area = 0, undressedA = 0, bareA = 0;
        foreach (int n in Enumerable.Range(0, cg.N).Where(n => cg.Area[n] >= 8 && (region is null || region.Contains(cg.Cent[n]))))
        {
            total++;
            area += cg.Area[n];
            bool u = NodeDressing[n] == 0, b = u && NodePlanes[n] <= 5;
            if (u)
            {
                undressed++;
                undressedA += cg.Area[n];
            }

            if (b)
            {
                bare++;
                bareA += cg.Area[n];
            }

            if (list.Count < max)
            {
                list.Add(new JsonObject
                {
                    ["pos"] = P3(cg.CellCentre(n)), ["floor_y"] = R(cg.Cent[n].Y), ["area"] = R(cg.Area[n]), ["dressing"] = NodeDressing[n],
                    ["planes"] = NodePlanes[n], ["undressed"] = u, ["bare"] = b,
                });
            }
        }

        var body = new JsonObject
        {
            ["region"] = region?.ToJson(),
            ["cells"] = total,
            ["undressed"] = undressed,
            ["bare"] = bare,
            ["undressed_area_fraction"] = area > 0 ? R(undressedA / area) : null,
            ["bare_area_fraction"] = area > 0 ? R(bareA / area) : null,
            ["listed"] = list.Count,
            ["truncated"] = total > list.Count,
            ["cell_list"] = list,
        };
        return With(Header(), body);
    }

    private static Vec3d PosOf(JsonObject o) => o["pos"] is JsonArray a ? new Vec3d(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>()) : default;

    // ---- E7 area report -----------------------------------------------------------------------------

    /// <summary>
    /// E7: an area report for a region (null = the whole level): floor area, clear width and headroom distributions,
    /// ceiling variety, wall-angle mix, the spaces in it with their archetype guesses, dressing density, textures,
    /// items and spawns — each compared with the exemplar targets (DESIGN-QUALITY-SPEC §(a)) for the player class.
    /// </summary>
    public JsonObject AreaReport(SpatialRegion? region = null, string? playerClass = null)
    {
        PopulationReport();
        List<int> inside = _w.PlayIdx.Where(i => InRegion(i, region)).ToList();
        if (inside.Count == 0)
        {
            throw new SpatialException("no_floor", "The region holds no playable floor.", "Widen the region, or check it with spatial.walk (floor reachable on foot from a spawn).");
        }

        double area = inside.Count * _g * _g;
        double[] hr = inside.Where(i => !_s[i].OpenSky).Select(i => _s[i].Headroom).OrderBy(x => x).ToArray();
        double? iqr = hr.Length > 4 ? R((Percentiles.Type7(hr, 0.75) - Percentiles.Type7(hr, 0.25)) / Percentiles.Type7(hr, 0.5)) : null;
        CellGraph cg = Cell8;
        var cells = Enumerable.Range(0, cg.N).Where(n => cg.Area[n] >= 8 && (region is null || region.Contains(cg.Cent[n]))).ToList();
        int dressing = cells.Sum(n => NodeDressing[n]);

        // Walls of the visible faces in the region.
        double wallA = 0, offA = 0;
        var tex = new Dictionary<string, double>();
        foreach ((Model.Face f, Vec3d[] poly, Vec3d n, double a) in VisibleFaces())
        {
            Vec3d c = poly.Aggregate(new Vec3d(0, 0, 0), (x, y) => x.Add(y)).Scale(1.0 / poly.Length);
            if (region is not null && !region.Contains(c))
            {
                continue;
            }

            string t = TexName(f);
            tex[t] = tex.GetValueOrDefault(t) + a;
            if (Math.Abs(n.Y) < 0.3)
            {
                wallA += a;
                double ang = Math.Atan2(n.Z, n.X) * 180 / Math.PI;
                double m = ((ang % 90) + 90) % 90;
                if (Math.Min(m, 90 - m) > 5)
                {
                    offA += a;
                }
            }
        }

        var spaces = SpaceShapes().Where(s => region is null || region.Contains(Spaces.Cent[s.Node])).ToList();
        var items = _obj.Items.Where(i => region is null || region.Contains(i.P)).ToList();
        var spawns = _obj.Spawns.Where(s => s.Kind == "mp" && (region is null || region.Contains(s.P))).ToList();
        string pc = playerClass ?? DesignGrader.PlayerClass(null, _obj.MpSpawnCount);
        JsonNode? targets = DesignGrader.Table["players"]?[pc];
        double per100 = 100 / area;
        var body = new JsonObject
        {
            ["region"] = region?.ToJson(),
            ["floor_area"] = R(area),
            ["clear_width"] = Dist(inside.Select(i => _s[i].Width)),
            ["headroom"] = Dist(hr),
            ["outdoor_fraction"] = R(SkyShare(inside)),
            ["open_sky_fraction"] = R(inside.Count(i => _s[i].OpenSky) / (double)inside.Count),
            ["headroom_iqr_over_median"] = iqr,
            ["corridor_fraction"] = R(inside.Count(i => _s[i].Corridor) / (double)inside.Count),
            ["floor_span"] = R(inside.Max(i => _s[i].P.Y) - inside.Min(i => _s[i].P.Y)),
            ["wall_offaxis_fraction"] = wallA > 0 ? R(offA / wallA) : null,
            ["spaces"] = new JsonArray(spaces.Take(12).Select(s => (JsonNode)new JsonObject
            {
                ["pos"] = P3(Spaces.Cent[s.Node]), ["area"] = R(s.Area), ["length"] = R(s.Length), ["width"] = R(s.Width),
                ["headroom_p50"] = double.IsNaN(s.HeadroomP50) ? null : R(s.HeadroomP50), ["sky"] = R(SkyShare(Spaces.Members[s.Node])),
                ["archetype"] = s.Archetype,
            }).ToArray()),
            ["dressing_per100"] = R(dressing * per100),
            ["undressed_cells"] = cells.Count(n => NodeDressing[n] == 0),
            ["cells"] = cells.Count,
            ["textures"] = tex.Count,
            ["top_textures"] = new JsonArray(tex.OrderByDescending(kv => kv.Value).Take(8).Select(kv => (JsonNode)kv.Key).ToArray()),
            ["items"] = items.Count,
            ["items_per100"] = R(items.Count * per100),
            ["items_by_category"] = new JsonObject(items.GroupBy(i => i.Cat).OrderBy(g => g.Key).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            ["spawns"] = spawns.Count,
            ["floor_per_spawn"] = spawns.Count > 0 ? R(area / spawns.Count) : null,
            ["player_class"] = pc,
            ["targets"] = new JsonObject
            {
                ["clear_width_p50"] = new JsonArray(8.0, 10.0),
                ["headroom_p50"] = new JsonArray(5.0, 9.0),
                ["headroom_iqr_over_median"] = new JsonArray(0.68, 1.27),
                ["wall_offaxis_fraction"] = new JsonArray(0.08, 0.35),
                ["dressing_per100"] = new JsonArray(5.0, 10.0),
                ["items_per100"] = new JsonArray(1.2, 2.0),
                ["level"] = targets?.DeepClone(),
                ["source"] = "DESIGN-QUALITY-SPEC §(a): exemplar p25-p75",
            },
        };
        return With(Header(), body);
    }

    // ---- E8 spawn and item reports ---------------------------------------------------------------------

    /// <summary>E8: every playable multiplayer spawn with its nearest spawn, exposure, nearest item and the spawns it sees within 30 m, plus the level figures.</summary>
    public JsonObject SpawnReportQuery(int max = 100)
    {
        JsonObject level = SpawnReport();
        var body = new JsonObject
        {
            ["level"] = level.DeepClone(),
            ["exposure_radius"] = SpawnExposureRadius,
            ["spawn_list"] = new JsonArray(SpawnDetails.Take(max).Select(s => (JsonNode)new JsonObject
            {
                ["uid"] = s.Uid, ["pos"] = P3(s.P), ["nearest_spawn"] = s.Nearest < double.MaxValue ? R(s.Nearest) : null, ["exposure"] = R(s.Exposure),
                ["nearest_item"] = double.IsFinite(s.ToItem) ? R(s.ToItem) : null, ["sees_spawns"] = new JsonArray(s.Sees.Select(u => (JsonNode)u).ToArray()),
            }).ToArray()),
            ["truncated"] = SpawnDetails.Count > max,
        };
        return With(Header(), body);
    }

    /// <summary>E8: every item with its category, route-traffic percentile, pocket membership and (weapons, powerups) nearest-neighbour distance, plus the level figures.</summary>
    public JsonObject ItemReportQuery(int max = 200)
    {
        JsonObject level = ItemReport();
        var body = new JsonObject
        {
            ["level"] = level.DeepClone(),
            ["item_list"] = new JsonArray(ItemDetails.Take(max).Select(i => (JsonNode)new JsonObject
            {
                ["uid"] = i.Uid, ["class"] = i.Cls, ["category"] = i.Cat, ["pos"] = P3(i.P),
                ["traffic_pct"] = i.Traffic < 0 ? null : R(i.Traffic), ["in_pocket"] = i.Pocket,
                ["nearest_major"] = double.IsNaN(i.MajorNearest) ? null : R(i.MajorNearest),
            }).ToArray()),
            ["truncated"] = ItemDetails.Count > max,
        };
        return With(Header(), body);
    }
}
