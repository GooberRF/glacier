using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

// Grade details: the data behind a check or an advisory whose fix depends on a distribution (the median space size, the
// floor-height classes), so an agent can see what moves the number instead of guessing.
public sealed partial class SpatialAnalysis
{
    /// <summary>The detail kinds <see cref="Detail"/> knows (the grader table's <c>explain</c> values).</summary>
    public static IReadOnlyList<string> DetailKinds { get; } = new[] { "space_area", "floor_height_classes" };

    /// <summary>The smallest space the median space size counts (m²; the study's <c>space_area</c> population).</summary>
    public const double CountedSpaceArea = 30;

    /// <summary>A floor-height class: a 0.5 m height bin holding at least this share of the playable floor (the study's binning).</summary>
    public const double FloorClassShare = 0.02;

    /// <summary>The height bin of the floor-height classes (metres; bins start on world multiples of it).</summary>
    public const double FloorClassBin = 0.5;

    /// <summary>
    /// One detail for a grade failure or advisory: <c>space_area</c> (every space the median counts, with the count on each
    /// side of <paramref name="limit"/> and how many more small spaces it takes) or <c>floor_height_classes</c> (the height
    /// bins with their floor area and which of them count).
    /// </summary>
    public JsonObject Detail(string kind, double? limit = null) => kind switch
    {
        "space_area" => SpaceAreaDetail(limit ?? 260),
        "floor_height_classes" => FloorClassDetail(),
        _ => throw new SpatialException("usage", $"Unknown detail kind '{kind}'.", $"Kinds: {string.Join(", ", DetailKinds)}."),
    };

    /// <summary>What separates one space from the next (the space graph's rule, in words).</summary>
    public const string SpaceRule =
        "A space is connected floor at least 4 m wide that is not corridor-shaped; floor under 4 m wide (a doorway, a gap between solids) and corridor-shaped strips (at most 8 m wide and at least 2.5 times as long) are connectors between spaces, and space pieces under 16 m2 count as connectors. An opening 4 m wide or wider joins the rooms on both sides into one space.";

    private JsonObject SpaceAreaDetail(double limit)
    {
        List<SpaceShape> counted = SpaceShapes().Where(s => s.Area >= CountedSpaceArea).ToList(); // largest first
        double[] asc = counted.Select(s => s.Area).OrderBy(a => a).ToArray();
        double? median = asc.Length > 0 ? Math.Round(Percentiles.Type7(asc, 0.5), 1) : null; // as space_area_p50 reports it
        int above = asc.Count(a => a > limit), below = asc.Length - above;

        // The fewest extra spaces of the smallest counted size that bring the median to the limit (exact, with the same
        // percentile the metric uses).
        int needed = 0;
        if (median is double m0 && m0 > limit)
        {
            var list = asc.ToList();
            while (needed < 1000)
            {
                needed++;
                list.Insert(0, CountedSpaceArea);
                if (Percentiles.Type7(list.ToArray(), 0.5) <= limit)
                {
                    break;
                }
            }
        }

        string L(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        string summary = median is not double med
            ? $"No space of at least {L(CountedSpaceArea)} m2: the median is not measurable."
            : med <= limit
                ? $"The median of {asc.Length} spaces of at least {L(CountedSpaceArea)} m2 is {L(med)} m2 (limit {L(limit)}): {above} over the limit, {below} at or under it."
                : $"The median of {asc.Length} spaces of at least {L(CountedSpaceArea)} m2 is {L(med)} m2 (limit {L(limit)}): {above} are over {L(limit)} m2 and {below} at or under it. "
                  + $"The median falls to the limit only when the spaces at or under it outnumber the larger ones: add {needed} space(s) of {L(CountedSpaceArea)}-{L(limit)} m2 "
                  + $"(side rooms, alcoves, galleries set off by an opening under 4 m wide or a short corridor), or split spaces over {L(limit)} m2 so that every piece is under {L(limit)} m2 "
                  + $"(splitting one space over the limit into two pieces under it counts as 3 added spaces, a split that leaves one piece over the limit as 1). "
                  + $"Splitting a space into pieces that all stay over {L(limit)} m2 raises the median, and floor pieces under {L(CountedSpaceArea)} m2 are not counted.";

        var spaces = new JsonArray();
        for (int k = 0; k < counted.Count && k < 80; k++)
        {
            SpaceShape sh = counted[k];
            List<int> mem = Spaces.Members[sh.Node];
            spaces.Add(new JsonObject
            {
                ["rank"] = k + 1,
                ["area"] = R(sh.Area),
                ["side"] = sh.Area > limit ? "over" : "under",
                ["pos"] = P3(Spaces.Cent[sh.Node]),
                ["min"] = P3(new Vec3d(mem.Min(i => _s[i].P.X), mem.Min(i => _s[i].P.Y), mem.Min(i => _s[i].P.Z))),
                ["max"] = P3(new Vec3d(mem.Max(i => _s[i].P.X), mem.Max(i => _s[i].P.Y), mem.Max(i => _s[i].P.Z))),
                ["archetype"] = sh.Archetype,
            });
        }

        // The spaces either side of the median (the ones a change has to move past).
        var nearMedian = new JsonArray();
        if (median is double md)
        {
            foreach (int k in Enumerable.Range(0, counted.Count).OrderBy(k => Math.Abs(counted[k].Area - md)).ThenBy(k => k).Take(4).OrderBy(k => k))
            {
                nearMedian.Add(k + 1);
            }
        }

        return new JsonObject
        {
            ["kind"] = "space_area",
            ["summary"] = summary,
            ["counted"] = asc.Length,
            ["counted_from_m2"] = CountedSpaceArea,
            ["median"] = median is double mm ? R(mm) : null,
            ["limit"] = limit,
            ["over_limit"] = above,
            ["at_or_under_limit"] = below,
            ["spaces_needed"] = needed,
            ["near_median_ranks"] = nearMedian,
            ["spaces"] = spaces,
            ["truncated"] = counted.Count > spaces.Count,
            ["rule"] = SpaceRule,
        };
    }

    private JsonObject FloorClassDetail()
    {
        List<int> play = _w.PlayIdx;
        double cellArea = _g * _g;
        double minArea = FloorClassShare * play.Count * cellArea;
        var bins = play.GroupBy(i => (int)Math.Floor(_s[i].P.Y / FloorClassBin))
            .Select(gp => (Bin: gp.Key, Area: gp.Count() * cellArea, Counts: gp.Count() >= FloorClassShare * play.Count))
            .OrderBy(b => b.Bin)
            .ToList();
        int classes = bins.Count(b => b.Counts);
        string L(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        JsonObject Bin((int Bin, double Area, bool Counts) b) => new()
        {
            ["y_from"] = b.Bin * FloorClassBin,
            ["y_to"] = (b.Bin + 1) * FloorClassBin,
            ["area"] = R(b.Area),
            ["counts"] = b.Counts,
        };

        var near = bins.Where(b => !b.Counts && b.Area >= 0.25 * minArea).OrderByDescending(b => b.Area).Take(8).ToList();
        string summary = $"{classes} floor-height class(es): playable floor binned by height in {L(FloorClassBin)} m steps on world multiples of {L(FloorClassBin)} m "
            + $"(a floor at y 4.0 to 4.49 is one bin); a bin is a class when it holds at least {L(FloorClassShare * 100)}% of the playable floor ({L(R(minArea))} m2 here). "
            + "A raised or sunken piece adds a class only when the floor in its own bin reaches that area (floor at the same height anywhere in the level adds up); stairs and ramps spread thin over many bins and rarely count."
            + (near.Count > 0 ? $" Closest bins that do not count: {string.Join(", ", near.Take(4).Select(b => $"y {L(b.Bin * FloorClassBin)} ({L(R(b.Area))} m2, {L(R(minArea - b.Area))} m2 short)"))}." : string.Empty);
        return new JsonObject
        {
            ["kind"] = "floor_height_classes",
            ["summary"] = summary,
            ["classes"] = classes,
            ["bin_m"] = FloorClassBin,
            ["min_share"] = FloorClassShare,
            ["min_area_m2"] = R(minArea),
            ["bins"] = new JsonArray(bins.Where(b => b.Counts).Select(b => (JsonNode)Bin(b)).ToArray()),
            ["near_misses"] = new JsonArray(near.Select(b => (JsonNode)Bin(b)).ToArray()),
        };
    }
}
