using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Core.Modules;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>modules</c> global: the harvested detail module library (pilasters, trims, frames, beams, trusses,
/// railings, catwalks, pipes, machinery, consoles, rocks …) — list / get / fit / place. The library is located
/// once per script context through <see cref="ModuleLibrary.Locate"/> (the host's
/// <see cref="ScriptServices.ModuleLibraryDirectory"/>, else <c>GED_MODULE_LIBRARY</c>, else the folder beside
/// Glacier or in the source checkout); an absent library is a normal state (<c>available = false</c> with a
/// reason, empty lists). Placement is <see cref="ModuleInstancer.Place"/>: the module's brushes are appended at
/// the end of the time order as one nested transaction of the run's undo step, solids forced to detail.
/// </summary>
[ScriptDoc("The detail module library: list{…} / get(id) harvested modules (pilasters, trims, frames, beams, railings, pipes, machinery …), fit{…} a size and place{id=, pos=, yaw=, length=, height=} them with stretch and repeat.", Since = 2)]
public sealed class ScriptModules
{
    private readonly ScriptContext _ctx;
    private ModuleLibrary? _library;
    private HashSet<string>? _resolvable;

    internal ScriptModules(ScriptContext ctx) => _ctx = ctx;

    /// <summary>The library (located on first use, once per context).</summary>
    internal ModuleLibrary Library => _library ??= ModuleLibrary.Locate(_ctx.Services.ModuleLibraryDirectory);

    [ScriptDoc("True when a module library with at least one module was found.", Example = "if not modules.available then log.warn(modules.reason) end", Since = 2)]
    public bool Available => Library.Available;

    [ScriptDoc("The library folder (…/library/modules), or nil when none was located.", Since = 2)]
    public string? Path => Library.Directory;

    [ScriptDoc("Why the library is unavailable (nil when it is available).", Since = 2)]
    public string? Reason => Library.Reason;

    [ScriptDoc("The number of modules in the library (0 when absent).", Since = 2)]
    public int Count => Library.Modules.Count;

    [ScriptDoc("The modules matching the filters, best score first (then id): module_info[]. An absent library gives an empty list.",
        Example = "for _, m in ipairs(modules.list{category = \"pilaster\", style = \"industrial\", limit = 5}) do print(m.id, m.size) end", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.ModulesList))]
    public ScriptModuleInfo[] List(ScriptArgs? args = null)
    {
        const string Api = "modules.list";
        args ??= new ScriptArgs();
        args.RejectUnknown(ScriptOptionSets.ModulesList);
        string? category = Vocab(Api, args, "category", ModuleVocabulary.Categories);
        string? attach = Vocab(Api, args, "attach", ModuleVocabulary.Attachments);
        string? role = Vocab(Api, args, "role", ModuleVocabulary.Roles);
        string? style = Vocab(Api, args, "style", ModuleVocabulary.Styles);
        string? stretch = args.String(Api, "stretch")?.Trim().ToLowerInvariant();
        if (stretch is not null && ModuleVocabulary.AxisIndex(stretch) < 0)
        {
            throw new ScriptApiException($"{Api}: 'stretch' must be \"x\", \"y\" or \"z\" (got \"{stretch}\").");
        }

        int? limit = args.NumberOrNull(Api, "limit") is double l ? (int)Math.Max(0, Math.Floor(l)) : null;
        Vec3? min = args.VecOrNull(Api, "min_size");
        Vec3? max = args.VecOrNull(Api, "max_size");
        var q = new ModuleQuery
        {
            Category = category,
            Attach = attach,
            Role = role,
            Style = style,
            Group = args.String(Api, "group"),
            StockOnly = args.Bool(Api, "stock_only", false),
            Stretchable = stretch,
            MinX = min?.X,
            MinY = min?.Y,
            MinZ = min?.Z,
            MaxX = max?.X,
            MaxY = max?.Y,
            MaxZ = max?.Z,
            IncludeRejected = args.Bool(Api, "include_rejected", false),
        };
        bool resolvable = args.Bool(Api, "resolvable", true);
        string? source = args.String(Api, "source");
        IEnumerable<ModuleInfo> hits = Library.Query(q);
        if (resolvable)
        {
            HashSet<string> ok = Resolvable();
            hits = hits.Where(m => ok.Contains(m.Id));
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            hits = hits.Where(m => m.Provenance.Any(p => p.Level.Contains(source, StringComparison.OrdinalIgnoreCase)));
        }

        if (limit is int n)
        {
            hits = hits.Take(n);
        }

        return hits.Select(m => new ScriptModuleInfo(m, this)).ToArray();
    }

    [ScriptDoc("The module with this id (case-insensitive), or nil. A module the mapper rejected is returned too (its verdict says so); fit and place refuse it.", Example = "local m = modules.get(\"pilaster_04f7c88b\")", Since = 2)]
    public ScriptModuleInfo? Get(string id) => string.IsNullOrWhiteSpace(id) ? null : Library.Find(id, includeRejected: true) is { } m ? new ScriptModuleInfo(m, this) : null;

    [ScriptDoc("The size a module would get for the requested length / height / depth, and which axes cannot stretch — without changing the level. Returns {ok, size, tile_counts, fixed = {axis…}, message}.",
        Example = "local f = modules.fit{id = m.id, height = 6} if f.ok then print(f.size) end", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.ModulesFit))]
    public Dictionary<string, object?> Fit(ScriptArgs args)
    {
        const string Api = "modules.fit";
        if (args is null)
        {
            throw new ScriptApiException($"{Api}: a table {{id=, length=, height=, depth=}} is required.");
        }

        args.RejectUnknown(ScriptOptionSets.ModulesFit);
        ModuleInfo m = Require(Api, args);
        double?[] want = { Positive(Api, args, "length"), Positive(Api, args, "height"), Positive(Api, args, "depth") };
        var cannot = new List<string>();
        for (int a = 0; a < 3; a++)
        {
            double natural = a == 0 ? m.Size.X : a == 1 ? m.Size.Y : m.Size.Z;
            if (want[a] is double t && Math.Abs(t - natural) >= 1e-3 && m.Stretch(a) is null)
            {
                cannot.Add(ModuleVocabulary.AxisName(a));
            }
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = m.Id,
            ["fixed"] = cannot.ToArray(),
            ["stretch_axes"] = m.StretchAxes.ToArray(),
        };
        if (cannot.Count > 0)
        {
            result["ok"] = false;
            result["size"] = Vec(m.Size);
            result["tile_counts"] = m.Tiles.Select(t => (double)t.Count).ToArray();
            result["message"] = $"'{m.Id}' cannot stretch on {string.Join(", ", cannot)} ({StretchText(m)}).";
            return result;
        }

        IReadOnlyList<Brush> local = LoadBrushes(Api, m);
        ModuleBuild build = ModuleInstancer.BuildBrushes(m, local, new ModulePlacement { Length = want[0], Height = want[1], Depth = want[2] });
        result["ok"] = build.Ok;
        result["size"] = build.Ok ? Vec(build.Size) : Vec(m.Size);
        result["tile_counts"] = build.TileCounts.Select(c => (double)c).ToArray();
        result["message"] = build.Ok ? "fits" : build.Error;
        return result;
    }

    [ScriptDoc("Places a module: local origin at pos, turned by yaw (local +Z = the room side), stretched to length/height/depth; brushes are tagged kit.module, kit.role, kit.part. Returns {brushes, size, id, role}.",
        Example = "modules.place{id = \"pilaster_04f7c88b\", pos = {0, 0, 5.9}, yaw = 180, height = 4.5}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.ModulesPlace))]
    public Dictionary<string, object?> Place(ScriptArgs args)
    {
        const string Api = "modules.place";
        if (args is null)
        {
            throw new ScriptApiException($"{Api}: a table {{id=, pos=}} is required.");
        }

        args.RejectUnknown(ScriptOptionSets.ModulesPlace);
        ModuleInfo m = Require(Api, args);
        Vec3 pos = args.VecOrNull(Api, "pos") ?? throw new ScriptApiException($"{Api}: 'pos' is required.", "The world point the module's local origin lands on (see module_info.anchor).");
        double yaw = args.Number(Api, "yaw", 0);
        if (!double.IsFinite(yaw))
        {
            throw new ScriptApiException($"{Api}: 'yaw' must be a finite number of degrees.");
        }

        Dictionary<string, string> tags = ScriptTags.Table(Api, args.Table(Api, "tags"));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args.Table(Api, "textures") is { } tex)
        {
            foreach (KeyValuePair<string, object?> kv in tex.Named.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Value is not string to || to.Trim().Length == 0)
                {
                    throw new ScriptApiException($"{Api}: textures['{kv.Key}'] must be a texture name.", "For example textures = {[\"mtl_girder06.tga\"] = \"mtl_girder03.tga\"}.");
                }

                map[kv.Key] = to.Trim();
            }
        }

        string? name = args.String(Api, "name");
        tags["kit.module"] = m.Id;
        tags.TryAdd("kit.role", m.Role);
        tags.TryAdd("kit.part", m.Category);
        if (!string.IsNullOrWhiteSpace(name))
        {
            tags.TryAdd("kit.name", name!);
        }

        if (tags.Count > ObjectTags.MaxTagsPerUid)
        {
            throw new ScriptApiException($"{Api}: at most {ObjectTags.MaxTagsPerUid} tags per brush (got {tags.Count} with kit.module, kit.role and kit.part).");
        }

        var placement = new ModulePlacement
        {
            Position = pos,
            YawDegrees = yaw,
            Length = Positive(Api, args, "length"),
            Height = Positive(Api, args, "height"),
            Depth = Positive(Api, args, "depth"),
            Mirror = args.Bool(Api, "mirror", false),
            ForceDetail = args.Bool(Api, "detail", true),
            TextureMap = map.Count > 0 ? map : null,
            Tags = tags,
        };

        IReadOnlyList<Brush> local = LoadBrushes(Api, m);
        ModulePlaceResult r = ModuleInstancer.Place(_ctx.Document, m, local, placement, _ctx.Metadata);
        if (!r.Ok)
        {
            throw new ScriptApiException($"{Api}: {r.Error}", $"Use modules.fit{{id = \"{m.Id}\", …}} first; it can stretch on {(m.StretchAxes.Count == 0 ? "no axis" : string.Join(", ", m.StretchAxes))}.");
        }

        _ctx.Brushes.NotifyExternalBrushChange(); // imported brushes bypass the brush editor's commands
        foreach (int uid in r.BrushUids)
        {
            _ctx.NoteCreated(uid);
        }

        _ctx.Changes.Record("module placed");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["brushes"] = r.BrushUids.Select(u => new ScriptBrushHandle(_ctx, u)).ToArray(),
            ["size"] = Vec(r.Size),
            ["tile_counts"] = r.TileCounts.Select(c => (double)c).ToArray(),
            ["id"] = m.Id,
            ["role"] = tags["kit.role"],
            ["category"] = m.Category,
        };
    }

    [ScriptDoc("The mean brightness (0-1) of a texture as the mounted assets decode it, or nil without assets or when it cannot be decoded (for matching module materials to a room's).", Example = "local b = modules.texture_brightness(\"cem_bunker04.tga\")", Since = 2)]
    public double? TextureBrightness(string texture) => string.IsNullOrWhiteSpace(texture) ? null : Luminance(texture.Trim());

    public override string ToString() => "modules";

    private readonly Dictionary<string, bool?> _closed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when every brush of the module is a closed solid (each edge shared by exactly two of its faces); null
    /// when the .rfg cannot be read. Cached per context. An open brush renders as stray faces and can leak.
    /// </summary>
    internal bool? Closed(ModuleInfo m)
    {
        if (_closed.TryGetValue(m.Id, out bool? cached))
        {
            return cached;
        }

        bool? closed;
        try
        {
            closed = ModuleLibrary.LoadBrushes(m).All(b =>
            {
                var edges = new Dictionary<(int, int), int>();
                foreach (Face f in b.Geometry.Faces)
                {
                    int n = f.Vertices.Count;
                    for (int i = 0; i < n; i++)
                    {
                        int a = f.Vertices[i].Index, c = f.Vertices[(i + 1) % n].Index;
                        (int, int) key = a < c ? (a, c) : (c, a);
                        edges[key] = edges.GetValueOrDefault(key) + 1;
                    }
                }

                return edges.Count > 0 && edges.Values.All(v => v == 2);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or IO.Rfl.RflFormatException)
        {
            closed = null;
        }

        _closed[m.Id] = closed;
        return closed;
    }

    private readonly Dictionary<string, double?> _luma = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The mean luminance (0-1, Rec. 601 weights, alpha-weighted) of a texture decoded through the mounted assets,
    /// cached per context; null without assets or when it cannot be decoded.
    /// </summary>
    internal double? Luminance(string texture)
    {
        if (_luma.TryGetValue(texture, out double? cached))
        {
            return cached;
        }

        double? v = null;
        if (_ctx.Assets is { } vfs)
        {
            try
            {
                if (vfs.LoadTexture(texture) is { Width: > 0, Height: > 0 } d)
                {
                    byte[] px = d.Primary.Pixels;
                    int step = Math.Max(1, px.Length / 4 / 4096) * 4;
                    double sum = 0, wsum = 0;
                    for (int i = 0; i + 3 < px.Length; i += step)
                    {
                        double a = px[i + 3] / 255.0;
                        sum += a * ((0.299 * px[i]) + (0.587 * px[i + 1]) + (0.114 * px[i + 2])) / 255.0;
                        wsum += a;
                    }

                    v = wsum > 0 ? sum / wsum : null;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
            {
                v = null;
            }
        }

        _luma[texture] = v;
        return v;
    }

    /// <summary>Ids of the modules whose every texture resolves in the mounted assets (or is stock when none are mounted).</summary>
    private HashSet<string> Resolvable()
    {
        if (_resolvable is not null)
        {
            return _resolvable;
        }

        var ok = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        Assets.AssetVfs? vfs = _ctx.Assets;
        foreach (ModuleInfo m in Library.AllModules)
        {
            bool all = true;
            foreach (ModuleTexture t in m.Textures)
            {
                if (!cache.TryGetValue(t.Name, out bool r))
                {
                    r = vfs is null ? t.Stock : vfs.ResolveTexture(t.Name) is not null;
                    cache[t.Name] = r;
                }

                if (!r)
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                ok.Add(m.Id);
            }
        }

        return _resolvable = ok;
    }

    private ModuleInfo Require(string api, ScriptArgs args)
    {
        string id = args.String(api, "id") ?? throw new ScriptApiException($"{api}: 'id' is required.", "Use modules.list{…} to find one.");
        if (Library.Find(id) is { } m)
        {
            return m;
        }

        if (Library.Find(id, includeRejected: true) is { Rejected: true } rejected)
        {
            throw new ScriptApiException($"{api}: module '{rejected.Id}' was rejected by the library's curation (curation.json).",
                "Choose another module: modules.list{…} leaves rejected modules out.");
        }

        string hint = !Library.Available
            ? (Library.Reason ?? "No module library is available.")
            : ScriptSuggest.Hint(id, Library.Modules.Select(x => x.Id), "module");
        throw new ScriptApiException($"{api}: no module '{id}'.", hint);
    }

    private static IReadOnlyList<Brush> LoadBrushes(string api, ModuleInfo m)
    {
        try
        {
            return ModuleLibrary.LoadBrushes(m);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or IO.Rfl.RflFormatException)
        {
            throw new ScriptApiException($"{api}: module '{m.Id}' could not be read: {ex.Message}", "Re-harvest the library with 'glacier-cli modules harvest'.");
        }
    }

    private static double? Positive(string api, ScriptArgs args, string key)
    {
        if (args.NumberOrNull(api, key) is not double v)
        {
            return null;
        }

        if (!double.IsFinite(v) || v <= 0)
        {
            throw new ScriptApiException($"{api}: '{key}' must be a positive number of metres (got {ScriptArgs.Format(v)}).");
        }

        return v;
    }

    private static string? Vocab(string api, ScriptArgs args, string key, IReadOnlyList<string> allowed)
    {
        string? v = args.String(api, key)?.Trim().ToLowerInvariant();
        if (v is null)
        {
            return null;
        }

        if (!allowed.Contains(v, StringComparer.Ordinal))
        {
            throw new ScriptApiException($"{api}: unknown {key} '{v}'.", ScriptSuggest.Hint(v, allowed, key));
        }

        return v;
    }

    private static string StretchText(ModuleInfo m) =>
        m.StretchAxes.Count == 0 ? "it cannot stretch" : "it can stretch on " + string.Join(", ", m.StretchAxes);

    internal static Vec3 Vec(D3 v) => new((float)v.X, (float)v.Y, (float)v.Z);
}

/// <summary>One library module (Lua type <c>module_info</c>).</summary>
[ScriptDoc("One library module: id, category, role, attach, styles, size and bounds in its local frame, stretch slices, tiles, textures, mounting measurements, score, occurrences and provenance.", Since = 2)]
public sealed class ScriptModuleInfo
{
    private readonly ScriptModules? _owner;

    internal ScriptModuleInfo(ModuleInfo m, ScriptModules? owner = null)
    {
        _owner = owner;
        Info = m;
        Stretch = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int a = 0; a < 3; a++)
        {
            if (m.Stretch(a) is { } s)
            {
                Stretch[ModuleVocabulary.AxisName(a)] = new[] { s.A, s.B };
            }
        }

        Tiles = m.Tiles.Select(t => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["axis"] = t.Axis,
            ["pitch"] = t.Pitch,
            ["count"] = (double)t.Count,
        }).ToArray();
        Provenance = m.Provenance.Select(p => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["level"] = p.Level,
            ["packfile"] = p.Packfile,
            ["pos"] = ScriptModules.Vec(p.Position),
            ["yaw"] = p.Yaw,
        }).ToArray();
        Opening = m.Opening is { } o
            ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["width"] = o.Width, ["height"] = o.Height, ["wall_thickness"] = o.WallThickness }
            : null;
    }

    internal ModuleInfo Info { get; }

    [ScriptDoc("The module id (e.g. \"pilaster_04f7c88b\").", Since = 2)]
    public string Id => Info.Id;

    [ScriptDoc("The category: pilaster, base_trim, top_trim, band, wall_panel, recess, door_frame, window_frame, beam, truss, column, catwalk, railing, pipe, machinery, console, rock, support, …", Since = 2)]
    public string Category => Info.Category;

    [ScriptDoc("The gameplay role: cover, route, landmark, theme_detail or fixture.", Since = 2)]
    public string Role => Info.Role;

    [ScriptDoc("How it attaches (what its local origin means): wall, corner, floor, ceiling, column, span, edge, opening or free.", Since = 2)]
    public string Attach => Info.Attach;

    [ScriptDoc("What the local origin means for this attach kind (plain text).", Since = 2)]
    public string Anchor => Info.Anchor;

    [ScriptDoc("The style families, primary first (string[]).", Since = 2)]
    public string[] Styles => Info.Styles.ToArray();

    [ScriptDoc("The primary style family.", Since = 2)]
    public string Style => Info.PrimaryStyle;

    [ScriptDoc("The natural size on local x, y, z (vec, metres).", Since = 2)]
    public Vec3 Size => ScriptModules.Vec(Info.Size);

    [ScriptDoc("The bounds in the local frame (aabb; a wall module's wall plane is z = 0).", Since = 2)]
    public ScriptAabb Bounds => new(ScriptModules.Vec(Info.BoundsMin), ScriptModules.Vec(Info.BoundsMax));

    [ScriptDoc("The stretch slices by axis: {x = {a, b}, y = {a, b}, z = {a, b}} (an axis is absent when it cannot stretch).", Since = 2)]
    public Dictionary<string, object?> Stretch { get; }

    [ScriptDoc("The repeating units: {axis, pitch, count}[] (re-instanced at even spacing when that axis stretches).", Since = 2)]
    public Dictionary<string, object?>[] Tiles { get; }

    [ScriptDoc("The texture names the module's faces use (string[]).", Since = 2)]
    public string[] Textures => Info.Textures.Select(t => t.Name).ToArray();

    [ScriptDoc("True when a texture is not stock (it needs its packfile).", Since = 2)]
    public bool CustomTextures => Info.CustomTextures;

    [ScriptDoc("The number of brushes.", Since = 2)]
    public int Brushes => Info.Brushes;

    [ScriptDoc("The number of faces.", Since = 2)]
    public int Faces => Info.Faces;

    [ScriptDoc("True when the module has air members (it carves a recess into the wall).", Since = 2)]
    public bool HasAir => Info.HasAir;

    [ScriptDoc("How deep a recess carves behind the wall plane (metres), or nil.", Since = 2)]
    public double? RecessDepth => Info.RecessDepth;

    [ScriptDoc("The gap from the floor to the module's bottom where it was found (metres), or nil when it stands on the floor.", Since = 2)]
    public double? MountHeight => Info.MountHeight;

    [ScriptDoc("The gap from the module's top to the ceiling where it was found (metres), or nil.", Since = 2)]
    public double? CeilingGap => Info.CeilingGap;

    [ScriptDoc("The clear opening a frame surrounds: {width, height, wall_thickness}, or nil.", Since = 2)]
    public Dictionary<string, object?>? Opening { get; }

    [ScriptDoc("The selection score that orders choices: the harvest score (0-1), plus 0.5 when the mapper kept the module (curation.json).", Since = 2)]
    public double Score => Info.SelectionScore;

    [ScriptDoc("The harvest's own quality score (0-1), before the mapper's curation.", Since = 2)]
    public double HarvestScore => Info.Score;

    [ScriptDoc("The mapper's verdict from the library's curation.json: \"keep\", \"reject\", or nil (undecided).", Since = 2)]
    public string? Verdict => Info.Verdict;

    [ScriptDoc("How many times the module was found.", Since = 2)]
    public int Occurrences => Info.Occurrences;

    [ScriptDoc("The median spacing between recurrences along a wall or edge (metres), or nil.", Since = 2)]
    public double? SuggestedPitch => Info.SuggestedPitch;

    [ScriptDoc("The representative source level (relative path; a packfile level is pack.vpp|level.rfl).", Since = 2)]
    public string Source => Info.Provenance.Count > 0 ? Info.Provenance[0].Level : string.Empty;

    [ScriptDoc("Every source level it was found in (distinct, string[]).", Since = 2)]
    public string[] Sources => Info.Provenance.Select(p => p.Level).Distinct(StringComparer.Ordinal).ToArray();

    [ScriptDoc("Where it was found: {level, packfile, pos, yaw}[] (representative first, capped).", Since = 2)]
    public Dictionary<string, object?>[] Provenance { get; }

    [ScriptDoc("The library group (e.g. \"stock\").", Since = 2)]
    public string Group => Info.Group;

    [ScriptDoc("True when every brush is a closed solid (each edge shared by two faces); false for open shells that render as stray faces; nil when unreadable. Reading it loads the module's .rfg.", Since = 2)]
    public bool? Closed => _owner?.Closed(Info);

    [ScriptDoc("The mean brightness (0-1) of its textures as the mounted assets decode them (a glowing light strip reads near 1), or nil without assets.", Since = 2)]
    public double? Brightness
    {
        get
        {
            if (_owner is null || Info.Textures.Count == 0)
            {
                return null;
            }

            var values = Info.Textures.Select(t => _owner.Luminance(t.Name)).Where(v => v is not null).Select(v => v!.Value).ToList();
            return values.Count == 0 ? null : values.Average();
        }
    }

    [ScriptDoc("The axes it can stretch on (string[]).", Since = 2)]
    public string[] StretchAxes => Info.StretchAxes.ToArray();

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"module {Info.Id} ({Info.Category}, {Info.Attach}, {Info.Size.X:0.##}x{Info.Size.Y:0.##}x{Info.Size.Z:0.##} m)");
}

/// <summary>The option sets of the <c>modules</c> global.</summary>
public static partial class ScriptOptionSets
{
    private const string Size3 = "{x, y, z}";

    public static readonly ScriptOptionSet ModulesList = new("modules.list",
        new("category", "string", "any", "Only this category (pilaster, base_trim, top_trim, band, wall_panel, recess, door_frame, beam, truss, column, railing, catwalk, pipe, machinery, console, rock, …)."),
        new("attach", "string", "any", "Only this attachment kind: wall, corner, floor, ceiling, column, span, edge, opening or free."),
        new("role", "string", "any", "Only this role: cover, route, landmark, theme_detail or fixture."),
        new("style", "string", "any", "Only modules listing this style family (industrial, bunker, admin, mine, cave, canyon, mars_base, lab, tech, generic)."),
        new("group", "string", "any", "Only this library group (e.g. \"stock\")."),
        new("stock_only", "boolean", "false", "Only modules whose every texture is stock."),
        new("resolvable", "boolean", "true", "Only modules whose every texture resolves in the mounted assets (stock textures when no assets are mounted)."),
        new("min_size", Size3, "none", "Smallest natural size per local axis (metres)."),
        new("max_size", Size3, "none", "Largest natural size per local axis (metres)."),
        new("stretch", "string", "any", "Only modules that can stretch on this axis: \"x\", \"y\" or \"z\"."),
        new("source", "string", "any", "Only modules found in a level whose path contains this text."),
        new("include_rejected", "boolean", "false", "Also modules the mapper rejected in the library's curation.json."),
        new("limit", "integer", "all", "At most this many results."));

    public static readonly ScriptOptionSet ModulesFit = new("modules.fit",
        new("id", "string", null, "Required: the module id."),
        new("length", "number", "natural", "Wanted size on local x (metres)."),
        new("height", "number", "natural", "Wanted size on local y (metres)."),
        new("depth", "number", "natural", "Wanted size on local z (metres)."));

    public static readonly ScriptOptionSet ModulesPlace = new("modules.place",
        new("id", "string", null, "Required: the module id (modules.list / modules.get)."),
        new("pos", "vec", null, "Required: the world point the module's local origin lands on (module_info.anchor says what it is)."),
        new("yaw", "number", "0", "Heading in degrees about +Y: 0 keeps local +Z on world +Z, 90 turns it onto world +X."),
        new("length", "number", "natural", "Size on local x (metres; the module must stretch on x)."),
        new("height", "number", "natural", "Size on local y (metres; the module must stretch on y)."),
        new("depth", "number", "natural", "Size on local z (metres; the module must stretch on z)."),
        new("mirror", "boolean", "false", "Flip local x (faces stay outward, textures readable)."),
        new("detail", "boolean", "true", "Make every solid member a detail brush, so a placed module never unseals the level (air members stay air)."),
        new("textures", "{old = new}", "none", "Texture replacements by name."),
        new("tags", "{key = value}", "none", "Extra tags on every brush (kit.module, kit.role and kit.part are always set)."),
        new("name", "string", "none", "Tags every brush kit.name = name."));
}
