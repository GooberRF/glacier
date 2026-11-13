using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>geo</c> builders (ORGANIC-GEOMETRY-SPEC §3): each is ONE call that generates a whole organic
/// structure in C# (<see cref="HullChain"/>, <see cref="PrismPath"/>, <see cref="RockGen"/>), adds every
/// brush with one <see cref="BrushEditor.AddBrushes"/> at the end of time, tags each brush
/// (<c>tags</c>, <c>kit.part = part..n</c> when <c>part</c> is given, <c>geo.seed</c>) and returns a
/// record. Builders never read <c>rng</c>; equal options give byte-identical brushes.
/// </summary>
public sealed partial class ScriptGeo
{
    private const string SeedTag = "geo.seed";
    private const string PartTag = "kit.part";

    // ---- tube ------------------------------------------------------------------------------------

    [ScriptDoc("Builds a cave or canyon along a floor path: convex air brushes meeting on identical faces, a level or smoothly sloped floor per leg, a guaranteed walkway, seeded rough walls. Returns tube.",
        Example = "local t = geo.tube{path = {{0,0,0}, {0,0,30}, {20,-2,50}}, profile = \"cave\", radius = 3, seed = 4}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoTube))]
    public ScriptTube Tube(ScriptArgs args)
    {
        const string Api = "geo.tube";
        args = Require(Api, args, "geo.tube{path = {{0, 0, 0}, {0, 0, 30}}}");
        args.RejectUnknown(ScriptOptionSets.GeoTube);
        string profileName = (args.String(Api, "profile", "cave") ?? "cave").Trim().ToLowerInvariant();
        if (profileName is not ("cave" or "canyon"))
        {
            throw new ScriptApiException($"{Api}: unknown profile '{profileName}'.", ScriptSuggest.Hint(profileName, new[] { "cave", "canyon" }, "profile"));
        }

        bool cave = profileName == "cave";
        foreach ((string key, bool caveOnly) in new[] { ("floor_flatness", true), ("sides", true), ("heights", true), ("lean", false) })
        {
            if (args.Has(key) && caveOnly != cave)
            {
                throw new ScriptApiException($"{Api}: '{key}' applies only to profile = \"{(caveOnly ? "cave" : "canyon")}\".", $"Drop '{key}'.");
            }
        }

        List<Vec3d> path = PointList(Api, "path", args.Named.TryGetValue("path", out object? rawPath) ? rawPath : null, 2, 256);
        IReadOnlyList<double> halfWidths = HalfWidths(Api, args, path.Count, cave ? 3 : 6);
        IReadOnlyList<double> heights = PerPoint(Api, args, "height", "heights", path.Count, cave ? 4.5 : 16);
        WalkLimits limits = Limits(Api, args);
        double spacing = args.Has("spacing") ? Positive(Api, args, "spacing") : cave ? Math.Max(3, halfWidths.Min()) : halfWidths.Min();
        int? segments = args.Has("segments") ? args.Int(Api, "segments", 1) : null;
        if (segments is not null && args.Has("spacing"))
        {
            throw new ScriptApiException($"{Api}: give either 'segments' or 'spacing', not both.");
        }

        long seed = Seed(Api, args);
        var options = new TubeOptions
        {
            Path = path,
            Profile = cave ? TubeProfile.Cave : TubeProfile.Canyon,
            HalfWidths = halfWidths,
            Heights = heights,
            Roughness = args.Number(Api, "roughness", 0.2),
            FloorFlatness = args.Number(Api, "floor_flatness", 0.6),
            Lean = args.Number(Api, "lean", 0.15),
            Sides = args.Int(Api, "sides", 8),
            Segments = segments,
            Spacing = spacing,
            Seed = seed,
            Limits = limits,
            Existing = ExistingShapes(),
            Brush = BuilderParams(Api, args, air: true),
            Cancel = _ctx.Cancellation,
        };

        TubeResult result = HullChain.Build(options, out GeoEditError? error) ?? throw Fail(Api, error);
        ScriptBrushQuery q = Emit(Api, cave ? "Create cave" : "Create canyon", result.Brushes, args, 3, seed);
        return new ScriptTube(q, result, seed);
    }

    // ---- passage ---------------------------------------------------------------------------------

    [ScriptDoc("Builds a level corridor along a floor path on any heading: one exact air prism per leg, mitred so neighbours meet on identical faces. Returns tube.",
        Example = "geo.passage{path = {{0,0,0}, {10,0,10}, {10,0,30}}, width = 3, height = 3}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoPassage))]
    public ScriptTube Passage(ScriptArgs args)
    {
        const string Api = "geo.passage";
        args = Require(Api, args, "geo.passage{path = {{0, 0, 0}, {10, 0, 10}}}");
        args.RejectUnknown(ScriptOptionSets.GeoPassage);
        List<Vec3d> path = PointList(Api, "path", args.Named.TryGetValue("path", out object? rawPath) ? rawPath : null, 2, 256);
        double width = args.Has("width") ? Positive(Api, args, "width") : 4;
        double height = args.Has("height") ? Positive(Api, args, "height") : 4;
        WalkLimits limits = Limits(Api, args);
        if (width < limits.MinClearWidth - 1e-9)
        {
            throw new ScriptApiException($"{Api}: width {Fmt(width)} m is below the {Fmt(limits.MinClearWidth)} m walkway.", "Widen it, or pass min_clear_width = 0.");
        }

        if (height < limits.MinHeadroom - 1e-9)
        {
            throw new ScriptApiException($"{Api}: height {Fmt(height)} m is below the {Fmt(limits.MinHeadroom)} m headroom.", "Raise it, or pass min_headroom = 0.");
        }

        HullBrushParams bp = BuilderParams(Api, args, air: true);
        bp.Detail = args.Bool(Api, "detail", false);
        TubeResult result = PrismPath.Build(new PassageOptions { Path = path, Width = width, Height = height, Brush = bp, Existing = ExistingShapes() }, out GeoEditError? error)
            ?? throw Fail(Api, error);
        result.ClearWidth = width;
        ScriptBrushQuery q = Emit(Api, "Create passage", result.Brushes, args, 0, null);
        return new ScriptTube(q, result, 0);
    }

    // ---- rock_row --------------------------------------------------------------------------------

    [ScriptDoc("Builds a row of overlapping rough detail rocks along the floor line from → to (flat_top: a walkable top); structural = true builds one rock ridge standing on the floor. Returns rocks.",
        Example = "geo.rock_row{from = {0,0,0}, to = {12,0,4}, height = 4, depth = 3, seed = 2}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoRockRow))]
    public ScriptRocks RockRow(ScriptArgs args)
    {
        const string Api = "geo.rock_row";
        args = Require(Api, args, "geo.rock_row{from = {0, 0, 0}, to = {10, 0, 0}, height = 3}");
        args.RejectUnknown(ScriptOptionSets.GeoRockRow);
        Vec3d from = RequiredPoint(Api, args, "from");
        Vec3d to = RequiredPoint(Api, args, "to");
        double height = RequiredNumber(Api, args, "height", 0.1, 256, "metres");
        long seed = Seed(Api, args);
        bool structural = args.Bool(Api, "structural", false);
        var o = new RockRowOptions
        {
            From = from,
            To = to,
            Height = height,
            Depth = args.Number(Api, "depth", 2),
            Spacing = args.NumberOrNull(Api, "spacing"),
            FlatTop = args.Bool(Api, "flat_top", false),
            Structural = structural,
            Roughness = args.Number(Api, "roughness", 0.2),
            Points = args.Int(Api, "points", 12),
            Seed = seed,
            Brush = BuilderParams(Api, args, air: false),
            Existing = structural ? ExistingShapes() : Array.Empty<AuditShape>(),
            Cancel = _ctx.Cancellation,
        };

        RockResult result = RockGen.Row(o, out GeoEditError? error) ?? throw Fail(Api, error);
        ScriptBrushQuery q = Emit(Api, "Create rock row", result.Brushes, args, 3, seed);
        return new ScriptRocks(q, result, seed);
    }

    // ---- scatter ---------------------------------------------------------------------------------

    [ScriptDoc("Scatters count detail boulders on a rectangle (area = {min=, max=}) or on a tube's floor (along = tube, clear of its walkway), never closer than their radii plus gap. Returns rocks.",
        Example = "geo.scatter{along = cave, count = 20, size = {0.6, 1.8}, seed = 3}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoScatter))]
    public ScriptRocks Scatter(ScriptArgs args)
    {
        const string Api = "geo.scatter";
        args = Require(Api, args, "geo.scatter{area = {min = {-8, 0, -8}, max = {8, 0, 8}}, count = 10}");
        args.RejectUnknown(ScriptOptionSets.GeoScatter);
        bool hasArea = args.Has("area"), hasAlong = args.Has("along");
        if (hasArea == hasAlong)
        {
            throw new ScriptApiException($"{Api}: give exactly one of 'area' (a rectangle {{min=, max=}}) or 'along' (a tube).",
                "For example area = {min = {-8, 0, -8}, max = {8, 0, 8}} or along = geo.tube{…}.");
        }

        (Vec3d, Vec3d)? area = null;
        TubeResult? along = null;
        if (hasArea)
        {
            ScriptArgs box = args.Table(Api, "area") ?? throw new ScriptApiException($"{Api}: 'area' must be a table {{min = vec, max = vec}}.");
            box.RejectUnknown($"{Api} area", "min", "max");
            Vec3d a = RequiredPoint(Api, box, "min", "area.min"), b = RequiredPoint(Api, box, "max", "area.max");
            area = (new Vec3d(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), new Vec3d(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
        }
        else
        {
            along = args.Named["along"] is ScriptTube t ? t.Result
                : throw new ScriptApiException($"{Api}: 'along' must be a tube (from geo.tube or geo.passage; got {ScriptArgs.LuaTypeOf(args.Named["along"])}).",
                    "Pass the record a tube builder returned, e.g. along = cave.tube for a kit record.");
        }

        (double sizeMin, double sizeMax) = Size(Api, args);
        long seed = Seed(Api, args);
        var o = new ScatterOptions
        {
            Area = area,
            Along = along,
            Count = args.Int(Api, "count", 10),
            SizeMin = sizeMin,
            SizeMax = sizeMax,
            Margin = args.Number(Api, "margin", hasAlong ? 0.25 : 0.5),
            Gap = args.Number(Api, "gap", 0.25),
            Roughness = args.Number(Api, "roughness", 0.2),
            Points = args.Int(Api, "points", 12),
            Seed = seed,
            Brush = BuilderParams(Api, args, air: false),
            Cancel = _ctx.Cancellation,
        };

        RockResult result = RockGen.Scatter(o, out GeoEditError? error) ?? throw Fail(Api, error);
        ScriptBrushQuery q = Emit(Api, "Scatter rocks", result.Brushes, args, 3, seed);
        return new ScriptRocks(q, result, seed);
    }

    // ---- pit -------------------------------------------------------------------------------------

    [ScriptDoc("Digs a rough pit down from a level floor point: a stack of convex air brushes on tapering rings meeting on identical faces, with a flat floor depth metres down. Returns pit.",
        Example = "geo.pit{pos = {0, 0, 20}, radius = 5, depth = 6, seed = 2}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoPit))]
    public ScriptPit Pit(ScriptArgs args)
    {
        const string Api = "geo.pit";
        args = Require(Api, args, "geo.pit{pos = {0, 0, 0}, radius = 4, depth = 4}");
        args.RejectUnknown(ScriptOptionSets.GeoPit);
        Vec3d pos = RequiredPoint(Api, args, "pos");
        double radius = args.Has("radius") ? Positive(Api, args, "radius") : 4;
        long seed = Seed(Api, args);
        var o = new PitOptions
        {
            Pos = pos,
            Radius = radius,
            Depth = args.Number(Api, "depth", 4),
            Sides = args.Int(Api, "sides", 8),
            Taper = args.Number(Api, "taper", 0.7),
            Roughness = args.Number(Api, "roughness", 0.2),
            Seed = seed,
            Existing = ExistingShapes(),
            Brush = BuilderParams(Api, args, air: true),
            Cancel = _ctx.Cancellation,
        };

        RockResult result = RockGen.Pit(o, out GeoEditError? error) ?? throw Fail(Api, error);
        ScriptBrushQuery q = Emit(Api, "Create pit", result.Brushes, args, 0, seed);
        return new ScriptPit(q, result, radius, seed);
    }

    // ---- builder plumbing ------------------------------------------------------------------------

    private static ScriptApiException Fail(string api, GeoEditError? e) =>
        new($"{api}: {e?.Message ?? "the geometry could not be built."}", e?.Hint);

    /// <summary>Textures (player-relative roles; swapped for air), tile and flags of generated brushes.</summary>
    private HullBrushParams BuilderParams(string api, ScriptArgs args, bool air)
    {
        var p = new HullBrushParams { Air = air, TileMetres = Tile(api, args) };
        (p.Floor, p.Wall, p.Ceiling) = ScriptLevel.ResolveBrushTextures(_ctx, api, args, air);
        return p;
    }

    /// <summary>
    /// Every structural brush already in the level, for the near-coplanar audit. A brush an organic builder made
    /// (it carries <c>geo.seed</c>) is marked with what it belongs to (<c>cave 'west_cave'</c>, from its kit tags), so a
    /// failure against its walls can name it.
    /// </summary>
    private List<AuditShape> ExistingShapes()
    {
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = _ctx.Metadata.AllTags();
        return _ctx.Brushes.Brushes
            .Where(b => (b.Flags & (uint)(BrushFlags.Detail | BrushFlags.Portal)) == 0)
            .Select(b => AuditShape.FromBrush(b, $"brush#{b.Uid}", OrganicOwner(b.Uid, tags)))
            .ToList();
    }

    private static string? OrganicOwner(int uid, IReadOnlyDictionary<int, Dictionary<string, string>> tags)
    {
        if (!tags.TryGetValue(uid, out Dictionary<string, string>? own) || !own.ContainsKey(SeedTag))
        {
            return null;
        }

        own.TryGetValue("kit", out string? fn);
        own.TryGetValue("kit.name", out string? name);
        return name is { Length: > 0 }
            ? $"{fn ?? "organic piece"} '{name}' (brush#{uid})"
            : $"an earlier organic piece (brush#{uid})";
    }

    /// <summary>
    /// Adds the brushes at the end of time as ONE undo command and tags each: the <c>tags</c> table,
    /// <c>kit.part</c> = <c>part</c> + its 1-based index (zero-padded to <paramref name="digits"/>) when
    /// <c>part</c> is given, and <c>geo.seed</c> when <paramref name="seed"/> is not null.
    /// </summary>
    private ScriptBrushQuery Emit(string api, string description, List<Brush> brushes, ScriptArgs args, int digits, long? seed)
    {
        Dictionary<string, string> tags = ScriptTags.Table(api, args.Table(api, "tags"));
        string? part = args.String(api, "part");
        if (part is not null && part.Trim().Length == 0)
        {
            throw new ScriptApiException($"{api}: 'part' must be a non-empty string.", "For example part = \"seg\".");
        }

        int extra = (part is null || tags.ContainsKey(PartTag) ? 0 : 1) + (seed is null || tags.ContainsKey(SeedTag) ? 0 : 1);
        if (tags.Count + extra > ObjectTags.MaxTagsPerUid)
        {
            throw new ScriptApiException($"{api}: every brush would carry {tags.Count + extra} tags (at most {ObjectTags.MaxTagsPerUid}).", "Pass fewer tags.");
        }

        if (part is not null)
        {
            ScriptTags.Value(api, PartTag, part + new string('9', Math.Max(digits, brushes.Count.ToString(CultureInfo.InvariantCulture).Length)));
        }

        foreach (Brush b in brushes)
        {
            b.Uid = _ctx.Document.AllocateUid();
        }

        _ctx.Brushes.AddBrushes(brushes, brushes.Count == 1 ? description : $"{description} ({brushes.Count} brushes)");
        var uids = brushes.Select(b => b.Uid).ToList();
        var writes = new List<(int, GedMetadataType, byte[]?)>(uids.Count);
        for (int i = 0; i < uids.Count; i++)
        {
            _ctx.NoteCreated(uids[i]);
            _ctx.Changes.Record("placed");
            var own = new Dictionary<string, string>(tags, StringComparer.Ordinal);
            if (part is not null)
            {
                own[PartTag] = part + (i + 1).ToString(digits > 0 ? new string('0', digits) : "0", CultureInfo.InvariantCulture);
            }

            if (seed is long s)
            {
                own[SeedTag] = s.ToString(CultureInfo.InvariantCulture);
            }

            if (own.Count > 0)
            {
                writes.Add((uids[i], GedMetadataType.Tags, ObjectTags.Encode(own)));
            }
        }

        _ctx.Metadata.SetBlocks(writes, "Tag generated brushes");
        return new ScriptBrushQuery(_ctx, uids);
    }

    private static long Seed(string api, ScriptArgs args)
    {
        double v = args.Number(api, "seed", 1);
        if (!double.IsFinite(v) || Math.Floor(v) != v || Math.Abs(v) > 9007199254740991d)
        {
            throw new ScriptApiException($"{api}: 'seed' must be an integer (got {Fmt(v)}).", "For example seed = 7.");
        }

        return (long)v;
    }

    private static WalkLimits Limits(string api, ScriptArgs args)
    {
        double clear = args.Number(api, "min_clear_width", 1.8);
        double head = args.Number(api, "min_headroom", 2.0);
        double slope = args.Number(api, "max_slope", 1.0);
        foreach ((string key, double v, double lo, double hi) in new[]
                 {
                     ("min_clear_width", clear, 0.0, 64.0), ("min_headroom", head, 0.0, 64.0),
                     ("max_slope", slope, 0.01, 16.0),
                 })
        {
            if (!double.IsFinite(v) || v < lo || v > hi)
            {
                GeoEditError e = HullChain.Range(key, v, lo, hi);
                throw new ScriptApiException($"{api}: {e.Message}", e.Hint);
            }
        }

        return new WalkLimits { MinClearWidth = clear, MinHeadroom = head, MaxSlope = slope };
    }

    /// <summary>The half-widths: one of radius / radii (half-widths) or width / widths (full widths).</summary>
    private static IReadOnlyList<double> HalfWidths(string api, ScriptArgs args, int points, double fallback)
    {
        string[] given = new[] { "radius", "radii", "width", "widths" }.Where(args.Has).ToArray();
        if (given.Length > 1)
        {
            throw new ScriptApiException($"{api}: give one of 'radius', 'radii', 'width' or 'widths' (got {string.Join(" and ", given.Select(g => $"'{g}'"))}).");
        }

        if (given.Length == 0)
        {
            return new[] { fallback };
        }

        return given[0] switch
        {
            "radius" => new[] { Positive(api, args, "radius") },
            "width" => new[] { Positive(api, args, "width") / 2 },
            "radii" => NumberList(api, "radii", args.Named["radii"], points),
            _ => NumberList(api, "widths", args.Named["widths"], points).Select(w => w / 2).ToArray(),
        };
    }

    private static IReadOnlyList<double> PerPoint(string api, ScriptArgs args, string one, string many, int points, double fallback)
    {
        if (args.Has(one) && args.Has(many))
        {
            throw new ScriptApiException($"{api}: give either '{one}' or '{many}', not both.");
        }

        return args.Has(many) ? NumberList(api, many, args.Named[many], points)
            : args.Has(one) ? new[] { Positive(api, args, one) }
            : new[] { fallback };
    }

    private static double[] NumberList(string api, string key, object? raw, int points)
    {
        if (raw is not ScriptArgs list || list.Named.Count > 0 || list.Items.Count != points || list.Items.Any(i => i is not double))
        {
            throw new ScriptApiException($"{api}: '{key}' must list one number per path point ({points}).",
                $"For example {key} = {{{string.Join(", ", Enumerable.Repeat("4", Math.Min(points, 4)))}{(points > 4 ? ", …" : string.Empty)}}}.");
        }

        double[] values = list.Items.Select(i => (double)i!).ToArray();
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]) || values[i] <= 0)
            {
                throw new ScriptApiException($"{api}: {key}[{i + 1}] must be greater than 0 (got {Fmt(values[i])}).");
            }
        }

        return values;
    }

    private static double Positive(string api, ScriptArgs args, string key)
    {
        double v = args.NumberOrNull(api, key) ?? 0;
        if (!double.IsFinite(v) || v <= 0)
        {
            throw new ScriptApiException($"{api}: '{key}' must be greater than 0 (got {Fmt(v)}).");
        }

        return v;
    }

    private static (double Min, double Max) Size(string api, ScriptArgs args)
    {
        if (!args.Named.TryGetValue("size", out object? raw) || raw is null)
        {
            return (0.6, 2.0);
        }

        return raw switch
        {
            double d => (d, d),
            ScriptArgs { Named.Count: 0, Items.Count: 2 } t when t.Items.All(i => i is double) => ((double)t.Items[0]!, (double)t.Items[1]!),
            ScriptArgs { Items.Count: 0 } t when t.Has("min") && t.Has("max") && t.Named.Count == 2 && t.Named["min"] is double a && t.Named["max"] is double b => (a, b),
            _ => throw new ScriptApiException($"{api}: 'size' must be a diameter or {{min, max}} diameters in metres.", "For example size = {0.6, 2}."),
        };
    }

    private static Vec3d RequiredPoint(string api, ScriptArgs args, string key, string? what = null)
    {
        object? raw = args.Named.TryGetValue(key, out object? v) ? v : null;
        if (raw is null)
        {
            throw new ScriptApiException($"{api}: '{what ?? key}' is required.", $"A point {{x, y, z}} in metres, e.g. {what ?? key} = {{0, 0, 0}}.");
        }

        return PointOf(raw) ?? throw new ScriptApiException($"{api}: '{what ?? key}' must be a vec, {{x, y, z}} or {{x=, y=, z=}} of finite numbers.");
    }

    private static List<Vec3d> PointList(string api, string key, object? raw, int min, int max)
    {
        if (raw is not ScriptArgs list || list.Named.Count > 0 || list.Items.Count < min || list.Items.Count > max)
        {
            string got = raw is ScriptArgs t && t.Named.Count == 0 ? t.Items.Count.ToString(CultureInfo.InvariantCulture)
                : raw is null ? "none" : $"a {ScriptArgs.LuaTypeOf(raw)}";
            throw new ScriptApiException($"{api}: '{key}' must list {min} to {max} floor points (got {got}).",
                $"For example {key} = {{{{0, 0, 0}}, {{0, 0, 30}}, {{20, 0, 50}}}}.");
        }

        var points = new List<Vec3d>(list.Items.Count);
        for (int i = 0; i < list.Items.Count; i++)
        {
            points.Add(PointOf(list.Items[i]) ?? throw new ScriptApiException($"{api}: {key}[{i + 1}] must be a vec, {{x, y, z}} or {{x=, y=, z=}} of finite numbers."));
        }

        return points;
    }

    private static Vec3d? PointOf(object? raw)
    {
        Vec3d? v = raw switch
        {
            Vec3 f => new Vec3d(f.X, f.Y, f.Z),
            ScriptArgs { Named.Count: 0, Items.Count: 3 } t when t.Items.All(i => i is double) => new Vec3d((double)t.Items[0]!, (double)t.Items[1]!, (double)t.Items[2]!),
            ScriptArgs { Items.Count: 0, Named.Count: 3 } t when new[] { "x", "y", "z" }.All(k => t.Named.TryGetValue(k, out object? o) && o is double)
                => new Vec3d((double)t.Named["x"]!, (double)t.Named["y"]!, (double)t.Named["z"]!),
            _ => null,
        };
        return v is { } p && double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z) ? p : null;
    }
}
