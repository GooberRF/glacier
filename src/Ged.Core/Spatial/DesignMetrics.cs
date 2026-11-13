using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Ged.Core.Spatial;

/// <summary>One design metric: its id, where it is read from the report, its group and how far it can be trusted.</summary>
/// <param name="Id">The id (the exemplar study's <c>metrics.csv</c> column).</param>
/// <param name="Path">The dotted path into <see cref="SpatialAnalysis.Report"/> (list indices as numbers).</param>
/// <param name="Group">scale, flow, shape, pop, tex, class or payload.</param>
/// <param name="Reliability">high, medium or low (DESIGN-QUALITY-SPEC §0).</param>
/// <param name="Doc">What it measures, with its unit.</param>
public sealed record DesignMetric(string Id, string Path, string Group, string Reliability, string Doc);

/// <summary>
/// The flat design-metric table the grader reads (DESIGN-QUALITY-SPEC §(a), §(d)): one number per metric id, taken from
/// <see cref="SpatialAnalysis.Report"/> exactly as the exemplar study's aggregation did (research/exemplar-study/tools/
/// aggregate.py) — a missing value, or the report's −1 for "not finite", reads as null (not measurable).
/// </summary>
public static class DesignMetrics
{
    /// <summary>Every metric, in table order.</summary>
    public static IReadOnlyList<DesignMetric> All { get; } = new DesignMetric[]
    {
        M("playable_area", "walk.playable_area", "scale", "high", "Playable floor area (m²): standable floor reached on foot from the spawns (the study: walk-graph components holding a spawn or at least 3 items)."),
        M("area_per_spawn", "walk.playable_per_spawn", "scale", "high", "Playable floor per multiplayer spawn (m²)."),
        M("spawns", "walk.spawns", "scale", "high", "Multiplayer respawn points."),
        M("clear_width_p50", "shape.clear_width.p50", "scale", "high", "Median clear width at chest height across the playable floor (m)."),
        M("clear_width_p90", "shape.clear_width.p90", "scale", "high", "90th-percentile clear width (m)."),
        M("headroom_p50", "shape.headroom_indoor.p50", "scale", "high", "Median headroom under a ceiling (m; open sky left out)."),
        M("headroom_p90", "shape.headroom_indoor.p90", "scale", "high", "90th-percentile headroom (m)."),
        M("outdoor_fraction", "shape.outdoor_fraction", "scale", "high", "Share of the playable floor under open sky or a sky face."),
        M("corridor_width_p50", "fingerprint.corridor.width.p50", "scale", "medium", "Median corridor width (m; as analyze measures it)."),
        M("corridor_height_p50", "fingerprint.corridor.height.p50", "scale", "medium", "Median corridor height (m)."),
        M("opening_width_p50", "fingerprint.opening.width.p50", "scale", "medium", "Median doorway/opening width (m)."),
        M("opening_height_p50", "fingerprint.opening.height.p50", "scale", "medium", "Median doorway/opening height (m)."),
        M("space_area_p50", "spaces.space_area.p50", "scale", "medium", "Median area of spaces of at least 30 m² (m²)."),
        M("space_area_max", "spaces.space_area.max", "scale", "medium", "Largest space (m²)."),
        M("largest_space_fraction", "spaces.largest_space_fraction", "scale", "medium", "Largest space's share of the playable floor."),
        M("vertical_span", "shape.vertical_span_p1_p99", "scale", "high", "Floor height span, 1st to 99th percentile (m)."),
        M("vertical_layers", "fingerprint.vertical.layers", "scale", "medium", "Vertical layers: runs of 1 m floor-height bins holding at least 5% of the floor."),
        M("floor_height_classes", "shape.floor_height_classes", "shape", "medium", "Floor-height classes: 0.5 m bins holding at least 2% of the playable floor."),
        M("engagement_p50", "sight.engagement_distance.p50", "scale", "medium", "Median distance between random playable eye points that see each other (m)."),
        M("engagement_p90", "sight.engagement_distance.p90", "scale", "medium", "90th-percentile engagement distance (m)."),
        M("longest_sight_p50", "sight.longest_ray_per_spot.p50", "scale", "high", "Median of each spot's longest horizontal sightline (m)."),
        M("longest_sight_p90", "sight.longest_ray_per_spot.p90", "scale", "high", "90th percentile of the longest sightline per spot (m)."),
        M("visible_pair_fraction", "sight.pair_visible_fraction", "scale", "medium", "Share of random playable eye-point pairs that see each other."),
        M("visible_within40", "sight.pair_visible_fraction_within40", "scale", "medium", "Share of eye-point pairs within 40 m that see each other."),
        M("exposure_p50", "sight.exposure50.p50", "scale", "medium", "Median exposure: share of the floor within 50 m a spot sees."),
        M("exposed_area_fraction", "sight.exposed_area_fraction", "scale", "medium", "Share of spots that see more than 35% of the floor within 50 m."),
        M("bridge_count", "bridges.count", "scale", "low", "The study's bridge proxy: elevated pieces (≥ 8 m² with ≥ 3 m drops across their travel axis). Kept for study parity; fragments see-through decks — use setpiece_bridges."),
        M("bridge_len_max", "bridges.largest.0.length", "scale", "low", "The study's bridge proxy: length of its largest piece (m). Kept for study parity; under-measures railed and truss decks — use bridge_span_max."),
        M("bridge_width_max", "bridges.largest.0.mean_width", "scale", "low", "The study's bridge proxy: mean width of its largest piece (m)."),
        M("setpiece_bridges", "setpieces.bridge_count", "scale", "medium", "Bridges (E9): decks 1.5-8 m wide crossing at least 8 m over a drop of at least 3 m on both sides, with floor at both ends."),
        M("bridge_span_max", "setpieces.bridge_span_max", "scale", "medium", "Longest bridge span (E9): the deck end to end (m)."),
        M("bridge_span", "setpieces.bridges.0.span", "scale", "medium", "Span of the landmark bridge (E9): the bridge with the largest span times depth, weighted by the square root of its deck width; deck end to end (m)."),
        M("bridge_width", "setpieces.bridges.0.width", "scale", "medium", "Deck width of the landmark bridge (m; E9)."),
        M("bridge_gap_width", "setpieces.bridges.0.gap_width", "scale", "medium", "Free length of the landmark bridge over its gap (m; E9)."),
        M("bridge_gap_depth", "setpieces.bridges.0.gap_depth", "scale", "medium", "Drop under the landmark bridge (m; E9)."),
        M("landmark_height_max", "setpieces.landmark_height_max", "scale", "medium", "Tallest landmark (E9): a free-standing structure's height above the floor around it (m; at least 12 m to count)."),
        M("atrium_height_max", "setpieces.atrium_height_max", "scale", "medium", "Tallest atrium (E9): an indoor space's median headroom (m; at least 12 m to count)."),
        M("loops8_per1000", "loops.b1_cell8_per1000m2", "flow", "medium", "Route loops around 8 m-scale obstacles per 1000 m²."),
        M("loops4_per1000", "loops.b1_cell4_per1000m2", "flow", "medium", "Route loops around 4 m-scale obstacles per 1000 m²."),
        M("loops8", "loops.b1_cell8", "flow", "medium", "Route loops around 8 m-scale obstacles."),
        M("space_loops", "spaces.loops", "flow", "low", "Cycle rank of the space/connector graph."),
        M("space_loops_per1000", "spaces.loops_per1000m2", "flow", "low", "Space-graph loops per 1000 m²."),
        M("pair_routes_p50", "routes.space_pair_routes.p50", "flow", "low", "Median node-disjoint routes between random spaces ≥ 25 m apart."),
        M("pair_routes_ge2", "routes.space_pair_routes_ge2_fraction", "flow", "low", "Share of such space pairs with at least 2 node-disjoint routes."),
        M("pocket_area_cell8", "cell8.pocket_area_fraction", "flow", "medium", "Dead-end pocket share of the floor (8 m cells cut off by one cell, each under 10%)."),
        M("chokes_cell8", "cell8.choke_nodes_10pct", "flow", "medium", "Chokepoint cells: 8 m cells whose removal cuts off at least 10% of the floor."),
        M("pocket_area_space", "spaces.pocket_area_fraction", "flow", "low", "Pocket share on the space graph."),
        M("chokes_space", "spaces.choke_nodes_10pct", "flow", "low", "Chokepoint spaces/connectors."),
        M("dead_end_spaces", "spaces.dead_end_spaces", "flow", "low", "Spaces with at most one neighbour."),
        M("dead_end_connectors", "spaces.dead_end_connectors", "flow", "low", "Connectors (≥ 12 m²) with one neighbour."),
        M("corridor_fraction", "shape.corridor_fraction", "shape", "medium", "Share of the floor classed as corridor."),
        M("wall_offaxis", "shape.wall_offaxis_fraction", "shape", "high", "Share of wall area more than 5° off the X/Z axes."),
        M("sloped_area", "shape.sloped_area_fraction", "shape", "high", "Share of visible face area sloped (neither wall, floor nor ceiling)."),
        M("wall_planes_per100", "shape.wall_planes_per_100m2", "shape", "medium", "Distinct wall planes per 100 m² of floor."),
        M("headroom_classes", "shape.headroom_classes", "shape", "medium", "1 m headroom bins holding at least 3% of the floor."),
        M("headroom_iqr_rel", "shape.headroom_iqr_over_median", "shape", "medium", "Headroom interquartile range over its median (ceiling variety)."),
        M("ceiling_change_edges", "shape.ceiling_change_edge_fraction", "shape", "medium", "Share of neighbouring 8 m cells whose ceilings differ by ≥ 1.5 m."),
        M("rectangularity", "spaces.rectangularity_areaweighted_median", "shape", "low", "Area-weighted median rectangularity of spaces ≥ 30 m²."),
        M("boxy_area_fraction", "spaces.boxy_area_fraction", "shape", "low", "Share of space area in box-shaped spaces (rectangularity ≥ 0.85)."),
        M("spaces_ge30", "spaces.spaces_ge30", "shape", "low", "Spaces of at least 30 m²."),
        M("archetypes", "spaces.archetypes", "shape", "low", "Distinct space archetypes (size / height / shape / levels / ceiling)."),
        M("archetype_entropy", "spaces.archetype_entropy", "shape", "low", "Entropy of the area share per archetype."),
        M("solid_per100", "population.solid_per100", "pop", "high", "Solid brushes per 100 m²."),
        M("detail_per100", "population.detail_per100", "pop", "high", "Detail brushes per 100 m²."),
        M("beams_per100", "population.beams_per100", "pop", "medium", "Beam brushes (≤ 0.75 m × 0.75 m, ≥ 2 m long) per 100 m²."),
        M("slabs_per100", "population.slabs_per100", "pop", "medium", "Slab brushes (≤ 0.5 m thick) per 100 m²."),
        M("small_solids_per100", "population.small_solids_per100", "pop", "medium", "Small solids (≤ 4 m, ≤ 16 m³) per 100 m²."),
        M("rotated_solid_fraction", "population.rotated_solid_fraction", "pop", "high", "Share of solid brushes rotated off the 90° lattice."),
        M("clutter_per100", "population.clutter_per100", "pop", "high", "Clutter objects per 100 m²."),
        M("decals_per100", "population.decals_per100", "pop", "high", "Decals per 100 m²."),
        M("meshes_per100", "population.meshes_per100", "pop", "high", "Mesh objects per 100 m²."),
        M("emitters_per100", "population.emitters_per100", "pop", "high", "Particle and bolt emitters per 100 m²."),
        M("lights_per100", "population.lights_per100", "pop", "high", "Lights per 100 m²."),
        M("dressing_per100", "population.dressing_per100", "pop", "medium", "Dressing elements (detail brushes, small solids, beams, slabs, clutter, decals, meshes, emitters) per 100 m²."),
        M("undressed_area", "population.undressed_area_fraction", "pop", "medium", "Share of 8 m-cell floor with no dressing element."),
        M("bare_area", "population.bare_area_fraction", "pop", "medium", "Share of 8 m-cell floor with no dressing and at most 5 visible planes."),
        M("plain_geometry_area", "population.plain_geometry_area_fraction", "pop", "medium", "Share of 8 m-cell floor seeing at most 5 planes."),
        M("cell_planes_p50", "population.cell_planes.p50", "pop", "medium", "Median distinct planes seen per 8 m cell."),
        M("cover_fraction", "cover.cover_sample_fraction", "pop", "low", "Share of the floor beside cover (low cover or a free-standing obstacle)."),
        M("dist_cover_p50", "cover.dist_to_cover.p50", "pop", "low", "Median walk distance to cover (m)."),
        M("dist_cover_p90", "cover.dist_to_cover.p90", "pop", "low", "90th-percentile walk distance to cover (m)."),
        M("dist_cover_open_p90", "cover.dist_to_cover_open.p90", "pop", "low", "90th-percentile distance to cover in open areas (clear width ≥ 8 m; m)."),
        M("far_from_cover", "cover.far_from_cover_fraction", "pop", "low", "Share of the floor more than 6 m from cover."),
        M("open_area_fraction", "cover.open_area_fraction", "pop", "medium", "Share of the floor in open areas (clear width ≥ 8 m)."),
        M("spawn_nn_p50", "spawns.nn.p50", "pop", "high", "Median nearest-neighbour distance between spawns (m)."),
        M("spawn_los30", "spawns.los_pairs_within30_fraction", "pop", "medium", "Share of spawn pairs within 30 m that see each other."),
        M("spawn_exposure_p50", "spawns.exposure40.p50", "pop", "medium", "Median spawn exposure (share of the floor within 40 m seen)."),
        M("spawn_to_item_p50", "spawns.to_nearest_item.p50", "pop", "high", "Median distance from a spawn to its nearest item (m)."),
        M("items_per100", "items.per100", "pop", "high", "Items per 100 m²."),
        M("major_item_nn_p50", "items.major_nn.p50", "pop", "high", "Median nearest-neighbour distance between weapons and powerups (m)."),
        M("weapon_traffic", "items.by_category.weapon.traffic_pct_p50", "pop", "low", "Median route-traffic percentile of weapon spots."),
        M("powerup_traffic", "items.by_category.powerup.traffic_pct_p50", "pop", "low", "Median route-traffic percentile of powerup spots."),
        M("health_traffic", "items.by_category.health.traffic_pct_p50", "pop", "low", "Median route-traffic percentile of health spots."),
        M("textures", "texturing.textures", "tex", "high", "Distinct textures on visible faces."),
        M("textures_per1000", "texturing.textures_per1000m2", "tex", "high", "Distinct textures per 1000 m²."),
        M("families", "texturing.families", "tex", "high", "Texture families (name prefix) covering at least 2% of the face area."),
        M("floor_tex90", "texturing.floor_textures_90", "tex", "high", "Textures needed to cover 90% of the floor area."),
        M("wall_tex90", "texturing.wall_textures_90", "tex", "high", "Textures needed to cover 90% of the wall area."),
        M("ceiling_tex90", "texturing.ceiling_textures_90", "tex", "high", "Textures needed to cover 90% of the ceiling area."),
        M("floor_wall_overlap", "texturing.floor_wall_overlap", "tex", "high", "Overlap of the floor and wall texture palettes (0 = disjoint)."),
        M("ceiling_wall_overlap", "texturing.ceiling_wall_overlap", "tex", "high", "Overlap of the ceiling and wall palettes."),
        M("textures_per_cell_p50", "texturing.textures_per_cell.p50", "tex", "medium", "Median distinct textures seen from an 8 m cell."),
        M("uv_offgrid_rot", "texturing.uv_offgrid_rotation_fraction", "tex", "medium", "Share of face area with UVs rotated off the 90° grid."),
        M("uv_aniso", "texturing.uv_anisotropic_fraction", "tex", "medium", "Share of face area with anisotropic (fitted) UVs."),
        M("uv_edge_aligned", "texturing.uv_edge_aligned_fraction", "tex", "low", "Share of face area whose texture rows sit on an edge."),
        M("uv_scale_consistent", "texturing.uv_scale_consistent_per_texture", "tex", "medium", "Share of face area at its texture's usual scale."),
        M("uv_cont_coplanar", "texturing.uv_continuity_coplanar", "tex", "medium", "UV continuity across shared coplanar edges."),
        M("uv_cont_corner", "texturing.uv_continuity_corner", "tex", "medium", "UV continuity across corners."),
        M("trim_per100", "texturing.trim_faces_per100", "tex", "medium", "Trim strips (thin faces with their own texture) per 100 m²."),
        M("trim_area", "texturing.trim_area_fraction", "tex", "medium", "Share of face area in trim strips."),
        M("detail_face_area", "texturing.detail_face_area_fraction", "tex", "high", "Share of face area on detail faces."),
        M("metres_per_tile_p50", "texturing.metres_per_tile.p50", "tex", "high", "Median metres per texture tile."),
        M("texel_within25", "texturing.texel_density.within_25pct", "tex", "medium", "Share of face area within 25% of the median texel density (needs texture sizes)."),
        M("texel_iqr_rel", "texturing.texel_density.iqr_over_median", "tex", "medium", "Texel-density interquartile range over its median."),
        M("ppm_p50", "texturing.texel_density.ppm_p50", "tex", "medium", "Median texel density (pixels per metre; needs texture sizes)."),
        M("sky_fraction", "class_features.sky_fraction", "class", "high", "Share of the floor under open sky or a sky face."),
        M("open_air_fraction", "class_features.open_air_fraction", "class", "high", "Share of the floor under sky or a ceiling at least 20 m up."),
        M("falloff_edge_fraction", "class_features.falloff_edge_fraction", "class", "medium", "Share of the floor within 1.5 m of an open edge dropping more than 8 m."),
        M("void_edge_fraction", "class_features.void_edge_fraction", "class", "medium", "Share of the floor within 1.5 m of an open edge whose fall (more than 8 m) lands off the playable floor."),
        M("payload_checkpoints", "payload.checkpoints", "payload", "high", "Payload checkpoints on the cart track (keyframes naming a Payload_Checkpoint)."),
        M("payload_segment_balance", "payload.segment_balance", "payload", "medium", "Smallest payload segment's floor over the largest's."),
        M("payload_segment_loop_score_min", "payload.segment_loop_score_min", "payload", "medium", "Worst segment's loop score: max(loops at 8 m / 0.5, loops at 4 m / 1.5) per 1000 m² (≥ 1 = the G14 rule holds)."),
        M("payload_segment_pocket_max", "payload.segment_pocket_max", "payload", "medium", "Largest dead-end pocket share inside one segment."),
        M("payload_segment_items_min", "payload.segment_items_min", "payload", "high", "Fewest items per 100 m² in a segment."),
        M("payload_segment_dressing_min", "payload.segment_dressing_min", "payload", "medium", "Fewest dressing elements per 100 m² in a segment."),
        M("payload_boundary_routes_min", "payload.boundary_routes_min", "payload", "medium", "Fewest node-disjoint routes from a segment into the next (8 m cells)."),
        M("payload_boundary_cell_share_max", "payload.boundary_cell_share_max", "payload", "medium", "Largest share of a boundary's crossing walk edges one 8 m cell carries."),
        M("payload_handoff_coverage", "payload.handoff_coverage", "payload", "high", "Share of checkpoints that switch on playable respawn points of both teams."),
    };

    /// <summary>The metrics computed from the CTF and capture-point route sections (null when the level has none).</summary>
    public static IReadOnlyList<string> GametypeMetricIds { get; } = new[]
    {
        "ctf_cell_routes", "ctf_routes_1_5x", "ctf_flag_walk", "ctf_flag_euclid", "ctf_balance", "ctf_space_routes", "ctf_own_flag",
        "hill_count", "hill_approaches_p50", "hill_exposure_p50", "hill_cover_p50",
    };

    /// <summary>Every metric id (table order, then the gametype metrics).</summary>
    public static IReadOnlyList<string> Ids { get; } = All.Select(m => m.Id).Concat(GametypeMetricIds).ToList();

    private static DesignMetric M(string id, string path, string group, string rel, string doc) => new(id, path, group, rel, doc);

    /// <summary>The metric table of a <see cref="SpatialAnalysis.Report"/> (every id present; null = not measurable).</summary>
    public static SortedDictionary<string, double?> Extract(JsonObject report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var v = new SortedDictionary<string, double?>(StringComparer.Ordinal);
        foreach (DesignMetric m in All)
        {
            double? x = Number(Get(report, m.Path));
            v[m.Id] = x is double d && d == -1 ? null : x;
        }

        foreach (string id in GametypeMetricIds)
        {
            v[id] = null;
        }

        if (Get(report, "routes.ctf") is JsonObject ctf)
        {
            v["ctf_cell_routes"] = Number(ctf["cell_routes"]);
            v["ctf_routes_1_5x"] = Number(ctf["cell_routes_within_1_5x"]);
            v["ctf_flag_walk"] = Number(ctf["flag_walk"]);
            v["ctf_flag_euclid"] = Number(ctf["flag_euclid"]);
            v["ctf_balance"] = Number(ctf["spawn_flag_balance"]);
            v["ctf_space_routes"] = Number(ctf["space_routes"]);
            v["ctf_own_flag"] = Number(ctf["red_spawn_to_own_flag_p50"]);
        }

        if (Get(report, "routes.hills") is JsonArray { Count: > 0 } hills)
        {
            double Median(string key)
            {
                double[] a = hills.Select(h => Number(h?[key]) ?? 0).OrderBy(x => x).ToArray();
                return a.Length % 2 == 1 ? a[a.Length / 2] : (a[(a.Length / 2) - 1] + a[a.Length / 2]) / 2;
            }

            v["hill_count"] = hills.Count;
            v["hill_approaches_p50"] = Median("approaches_cell");
            v["hill_exposure_p50"] = Median("exposure");
            v["hill_cover_p50"] = Median("hill_cover_fraction");
        }

        return v;
    }

    /// <summary>The node at a dotted path (list indices as numbers), or null.</summary>
    internal static JsonNode? Get(JsonNode? d, string path)
    {
        foreach (string p in path.Split('.'))
        {
            d = d switch
            {
                null => null,
                JsonArray a => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < a.Count ? a[i] : null,
                JsonObject o => o[p],
                _ => null,
            };
        }

        return d;
    }

    /// <summary>A JSON number as a double (ints included), else null.</summary>
    internal static double? Number(JsonNode? n) =>
        n is JsonValue jv && double.TryParse(jv.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
}
