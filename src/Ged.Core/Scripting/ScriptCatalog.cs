using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>catalog</c> global: what can be placed and what each class / kind exposes —
/// placeable kinds, class names from the game tables (when an install is mounted) and the event
/// catalog, per-class field listings, textures, and nearest-name lookups. Every list is sorted
/// ordinal-ignore-case so results are deterministic.
/// </summary>
[ScriptDoc("Discover placeable kinds, classes (game tables + events), their fields, and textures.", Since = 2)]
public sealed class ScriptCatalog
{
    /// <summary>The default and maximum <c>catalog.textures</c> result sizes (protect agent context).</summary>
    internal const int DefaultTextureLimit = 200;

    internal const int MaxTextureLimit = 5000;

    private readonly ScriptContext _ctx;

    internal ScriptCatalog(ScriptContext ctx) => _ctx = ctx;

    [ScriptDoc("True when the game's entity/item/clutter tables are loaded (an RF install is mounted).", Since = 2)]
    public bool Available => _ctx.Catalogs.Any;

    [ScriptDoc("The kinds level.place{} accepts, in kind order (e.g. \"Entity\", \"Light\", \"Event\").", Example = "for _, k in ipairs(catalog.kinds()) do print(k) end", Since = 2)]
    public string[] Kinds() => PlaceableKinds().Select(k => k.ToString()).ToArray();

    [ScriptDoc("The class names of a kind: Entity/Item/Clutter from the game tables (empty when not loaded), Event from the event catalog; other kinds {}.", Example = "local items = catalog.classes(\"Item\")", Since = 2)]
    public string[] Classes(string kind) => ClassesOf(ParseKind("catalog.classes", kind)).ToArray();

    [ScriptDoc("Describes a kind, or one class of it: category, description, links and every settable field. Returns class_info.", Example = "print(#catalog.describe(\"Event\", \"Message\").fields)", Since = 2)]
    public ScriptClassInfo Describe(string kind, string? className = null)
    {
        const string Api = "catalog.describe";
        LevelObjectKind k = ParseKind(Api, kind);
        PlaceableObjectType? palette = ObjectFactory.Palette.FirstOrDefault(p => p.Kind == k);
        if (className is null)
        {
            return new ScriptClassInfo(k.ToString(), string.Empty, palette?.Category ?? (k == LevelObjectKind.Event ? "Events" : string.Empty),
                palette?.DisplayName ?? k.ToString(), palette?.Alpine ?? false, KindHasRotation(k, null),
                Array.Empty<string>(), ScriptFieldCatalog.Describe(k, null));
        }

        if (k == LevelObjectKind.Event)
        {
            EventSchema schema = FindEvent(Api, className);
            return EventInfo(schema);
        }

        string cls = className;
        if (_ctx.Catalogs.HasTable(k))
        {
            cls = _ctx.Catalogs.Canonical(k, className)
                ?? throw new ScriptApiException($"{Api}: unknown {k} class '{className}'.",
                    ScriptSuggest.Hint(className, _ctx.Catalogs.ClassNames(k), "class", k.ToString()));
        }
        else if (k is not (LevelObjectKind.Entity or LevelObjectKind.Item or LevelObjectKind.Clutter))
        {
            throw new ScriptApiException($"{Api}: {k} objects have no classes.", "Omit the class to describe the kind.");
        }

        return new ScriptClassInfo(k.ToString(), cls, _ctx.Catalogs.Category(k, cls), _ctx.Catalogs.Describe(k, cls),
            palette?.Alpine ?? false, KindHasRotation(k, cls), Array.Empty<string>(), ScriptFieldCatalog.Describe(k, null));
    }

    [ScriptDoc("Every placeable event class with its fields (class_info[]), stock then Alpine.", Example = "for _, e in ipairs(catalog.events()) do print(e.class_name) end", Since = 2)]
    public ScriptClassInfo[] Events() => EventSchemaCatalog.Placeable.Select(EventInfo).ToArray();

    [ScriptDoc("Texture names in the mounted library, sorted; filter is a substring, or a glob with * and ?; limit defaults to 200 (max 5000). {} without an install.", Example = "local rock = catalog.textures(\"rock\", 20)", Since = 2)]
    public string[] Textures(string? filter = null, int limit = DefaultTextureLimit)
    {
        if (limit < 1 || limit > MaxTextureLimit)
        {
            throw new ScriptApiException($"catalog.textures: limit must be 1 to {MaxTextureLimit} (got {limit}).");
        }

        return TextureNames(filter, limit);
    }

    /// <summary>The sorted, filtered texture names (at most <paramref name="take"/>).</summary>
    private string[] TextureNames(string? filter, int take)
    {
        if (_ctx.Assets is not { } vfs)
        {
            return Array.Empty<string>();
        }

        IEnumerable<string> names = vfs.EnumerateTextures();
        if (!string.IsNullOrEmpty(filter))
        {
            Func<string, bool> match = filter.IndexOfAny(new[] { '*', '?' }) >= 0
                ? GlobMatcher(filter)
                : n => n.Contains(filter, StringComparison.OrdinalIgnoreCase);
            names = names.Where(match);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n, StringComparer.Ordinal)
            .Take(take)
            .ToArray();
    }

    [ScriptDoc("Textures as texture_info[] from {filter=, limit=, stock=, origin=}: provenance per texture, sorted by name; {} without an install.",
        Example = "for _, t in ipairs(catalog.textures{filter = \"rock\", stock = true}) do print(t.name) end", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.TextureQuery))]
    public ScriptTextureInfo[] Textures(ScriptArgs options)
    {
        const string Api = "catalog.textures";
        options ??= new ScriptArgs();
        options.RejectUnknown(ScriptOptionSets.TextureQuery);
        int limit = options.Int(Api, "limit", DefaultTextureLimit);
        string? filter = options.String(Api, "filter");
        bool? stock = options.Has("stock") ? options.Bool(Api, "stock", false) : null;
        Assets.AssetOrigin? origin = null;
        if (options.String(Api, "origin") is { } o)
        {
            string[] names = { "stock", "alpine", "packfile", "loose" };
            int i = Array.FindIndex(names, n => string.Equals(n, o.Trim(), StringComparison.OrdinalIgnoreCase));
            if (i < 0)
            {
                throw new ScriptApiException($"{Api}: unknown origin '{o}'.", ScriptSuggest.Hint(o, names, "origin"));
            }

            origin = (Assets.AssetOrigin)i;
        }

        if (limit < 1 || limit > MaxTextureLimit)
        {
            throw new ScriptApiException($"{Api}: limit must be 1 to {MaxTextureLimit} (got {limit}).");
        }

        if (_ctx.Assets is not { } vfs)
        {
            return Array.Empty<ScriptTextureInfo>();
        }

        var result = new List<ScriptTextureInfo>();
        foreach (string name in TextureNames(filter, int.MaxValue))
        {
            Assets.TextureProvenance? p = Assets.StockContent.Describe(vfs, name);
            if ((stock is bool s && (p?.Stock ?? false) != s) || (origin is { } want && p?.Origin != want))
            {
                continue;
            }

            result.Add(new ScriptTextureInfo(name, vfs, p));
            if (result.Count == limit)
            {
                break;
            }
        }

        return result.ToArray();
    }

    [ScriptDoc("One texture's provenance (texture_info): stock, origin, source, overridden, size; nil when it does not resolve or no install is mounted.",
        Example = "local t = catalog.texture_info(\"rock02.tga\") if t and not t.stock then log.warn(t.source) end", Since = 2)]
    public ScriptTextureInfo? TextureInfo(string name)
    {
        if (_ctx.Assets is not { } vfs || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Assets.StockContent.Describe(vfs, name) is { } p ? new ScriptTextureInfo(name, vfs, p) : null;
    }

    [ScriptDoc("The n (default 5) class names of a kind nearest to name, best first.", Example = "print(catalog.nearest(\"Item\", \"shotgn\")[1])", Since = 2)]
    public string[] Nearest(string kind, string name, int n = 5)
    {
        if (n < 1)
        {
            throw new ScriptApiException($"catalog.nearest: n must be at least 1 (got {n}).");
        }

        return ScriptSuggest.Nearest(name ?? string.Empty, ClassesOf(ParseKind("catalog.nearest", kind)), n).ToArray();
    }

    [ScriptDoc("Typical value range of a level metric (norm_info). No bucket: the curated reference set (else overall); a bucket (\"overall\", \"dm\", \"exemplar.dm\", …): that one, or nil.",
        Example = "local door = catalog.norms(\"opening.width\") print(door.p25, door.p50, door.p75)", Since = 2)]
    public ScriptNormInfo? Norms(string metric, string? bucket = null)
    {
        const string Api = "catalog.norms";
        string id = (metric ?? string.Empty).Trim();
        if (Analysis.CorpusNorms.Metric(id) is null)
        {
            throw new ScriptApiException($"{Api}: unknown metric '{id}'.",
                ScriptSuggest.Hint(id, Analysis.CorpusNorms.MetricIds, "metric"));
        }

        Analysis.NormStats? s = string.IsNullOrWhiteSpace(bucket)
            ? Analysis.CorpusNorms.Get(id)
            : Analysis.CorpusNorms.GetBucket(id, bucket.Trim().ToLowerInvariant());
        return s is null ? null : new ScriptNormInfo(s);
    }

    [ScriptDoc("The norms for a kind of level (norm_info): the most specific published bucket for its gametype, level class and player count (such as \"dm.S\"), else catalog.norms' reference chain.",
        Example = "local h = catalog.norms_for{metric = \"space.headroom\", gametype = \"dm\", class = \"open_air\", players = 12} print(h.bucket, h.p50)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.NormsFor))]
    public ScriptNormInfo? NormsFor(ScriptArgs options)
    {
        const string Api = "catalog.norms_for";
        if (options is null)
        {
            throw new ScriptApiException($"{Api}: a table {{metric = …}} is required.", "For example catalog.norms_for{metric = \"lights.per_100m2\", gametype = \"dm\", class = \"open_air\"}.");
        }

        options.RejectUnknown(ScriptOptionSets.NormsFor);
        string id = (options.String(Api, "metric") ?? string.Empty).Trim();
        if (Analysis.CorpusNorms.Metric(id) is null)
        {
            throw new ScriptApiException($"{Api}: unknown metric '{id}'.", ScriptSuggest.Hint(id, Analysis.CorpusNorms.MetricIds, "metric"));
        }

        string? cls = options.String(Api, "class")?.Trim().ToLowerInvariant();
        if (cls is not null && !Analysis.FingerprintSchema.LevelClasses.Contains(cls))
        {
            throw new ScriptApiException($"{Api}: 'class' must be \"standard\", \"open_air\" or \"payload\" (got \"{cls}\").");
        }

        string? size = null;
        if (options.Has("players"))
        {
            int players = options.Int(Api, "players", 0);
            if (players < 1)
            {
                throw new ScriptApiException($"{Api}: 'players' must be at least 1 (got {players}).");
            }

            size = Spatial.DesignGrader.PlayerClass(players, 0);
        }

        string? gametype = options.String(Api, "gametype")?.Trim().ToLowerInvariant();
        Analysis.NormStats? s = Analysis.CorpusNorms.Get(id, string.IsNullOrEmpty(gametype) ? null : gametype, cls, size);
        return s is null ? null : new ScriptNormInfo(s);
    }

    [ScriptDoc("Every metric id catalog.norms accepts, sorted.", Example = "for _, m in ipairs(catalog.norm_metrics()) do print(m) end", Since = 2)]
    public string[] NormMetrics() => Analysis.CorpusNorms.MetricIds.ToArray();

    public override string ToString() => "catalog";

    // ---- Shared with level.place --------------------------------------------------

    /// <summary>The kinds level.place{} accepts: the palette kinds plus Event, in enum order.</summary>
    internal static IEnumerable<LevelObjectKind> PlaceableKinds() =>
        Enum.GetValues<LevelObjectKind>().Where(k => k == LevelObjectKind.Event || ObjectFactory.Palette.Any(p => p.Kind == k));

    /// <summary>Class names of a kind (sorted): game tables for Entity/Item/Clutter, placeable events for Event.</summary>
    internal IReadOnlyList<string> ClassesOf(LevelObjectKind kind) => kind == LevelObjectKind.Event
        ? EventSchemaCatalog.Placeable.Select(s => s.ClassName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal).ToList()
        : _ctx.Catalogs.ClassNames(kind);

    /// <summary>A placeable event schema by class name, or an error with the nearest classes.</summary>
    internal static EventSchema FindEvent(string api, string className)
    {
        if (EventSchemaCatalog.Find(className) is { Placeable: true } schema)
        {
            return schema;
        }

        throw new ScriptApiException($"{api}: unknown event class '{className}'.",
            ScriptSuggest.Hint(className, EventSchemaCatalog.Placeable.Select(s => s.ClassName), "class", "Event"));
    }

    /// <summary>Parses a kind name: case-insensitive, underscores ignored, plus the documented kind aliases.</summary>
    internal static LevelObjectKind ParseKind(string api, string? kind, bool anyKind = false)
    {
        string raw = (kind ?? string.Empty).Trim();
        string norm = raw.Replace("_", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        LevelObjectKind? alias = norm.ToLowerInvariant() switch
        {
            "respawn" or "spawn" => LevelObjectKind.MpRespawnPoint,
            "corona" => LevelObjectKind.CoronaObject,
            "mesh" => LevelObjectKind.MeshObject,
            "note" => LevelObjectKind.NoteObject,
            _ => null,
        };
        if (alias is { } a)
        {
            return a;
        }

        foreach (LevelObjectKind k in Enum.GetValues<LevelObjectKind>())
        {
            if (string.Equals(k.ToString(), norm, StringComparison.OrdinalIgnoreCase))
            {
                return k;
            }
        }

        IEnumerable<string> names = anyKind
            ? Enum.GetNames<LevelObjectKind>().Concat(new[] { "respawn", "spawn", "corona", "mesh", "note" })
            : PlaceableKinds().Select(k => k.ToString());
        throw new ScriptApiException($"{api}: unknown kind '{raw}'.", ScriptSuggest.Hint(raw, names, "kind"));
    }

    /// <summary>Whether a freshly placed object of the kind (and class) has an orientation.</summary>
    internal static bool KindHasRotation(LevelObjectKind kind, string? className)
    {
        if (kind == LevelObjectKind.Event)
        {
            return className is not null && RflEvent.HasRotation(className, RflFile.AlpineSaveVersion);
        }

        try
        {
            object model = ObjectFactory.Build(kind, 0, Vec3.Zero, className).Model;
            return ObjectRotation.TryGetOrientation(model, out _, out _);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static ScriptClassInfo EventInfo(EventSchema s) => new(
        LevelObjectKind.Event.ToString(), s.ClassName, s.Category, s.Description, s.IsAlpine,
        RflEvent.HasRotation(s.ClassName, RflFile.AlpineSaveVersion),
        s.LinkTargets.Select(t => t.ToString()).ToArray(),
        ScriptFieldCatalog.Describe(LevelObjectKind.Event, s));

    private static Func<string, bool> GlobMatcher(string glob)
    {
        var rx = new Regex("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return n => rx.IsMatch(n);
    }
}

/// <summary>The description of a kind or class (Lua type <c>class_info</c>), returned by <c>catalog.describe</c>.</summary>
[ScriptDoc("A placeable kind or class: category, description, Alpine flag, orientation, link targets and fields.", Since = 2)]
public sealed class ScriptClassInfo
{
    internal ScriptClassInfo(string kind, string className, string category, string description, bool alpine,
        bool hasRotation, string[] linkTargets, ScriptFieldInfo[] fields)
    {
        Kind = kind;
        ClassName = className;
        Category = category;
        Description = description;
        Alpine = alpine;
        HasRotation = hasRotation;
        LinkTargets = linkTargets;
        Fields = fields;
    }

    [ScriptDoc("The object kind, e.g. \"Event\" or \"Item\".", Since = 2)]
    public string Kind { get; }

    [ScriptDoc("The class name (\"\" when describing a whole kind).", Since = 2)]
    public string ClassName { get; }

    [ScriptDoc("The browser category (\"\" when unknown).", Since = 2)]
    public string Category { get; }

    [ScriptDoc("What the class is or does (\"\" when unknown).", Since = 2)]
    public string Description { get; }

    [ScriptDoc("True for Alpine Faction additions.", Since = 2)]
    public bool Alpine { get; }

    [ScriptDoc("True when a placed object has an orientation (rot / set_rot / look_at).", Since = 2)]
    public bool HasRotation { get; }

    [ScriptDoc("For events, the object kinds it is meant to link to (empty = any).", Since = 2)]
    public string[] LinkTargets { get; }

    [ScriptDoc("Every field get/set accepts on such an object (field_info[]).", Since = 2)]
    public ScriptFieldInfo[] Fields { get; }

    public override string ToString() => ClassName.Length > 0 ? $"{Kind} {ClassName}" : Kind;
}

/// <summary>The outcome of a multi-target link edit (Lua type <c>link_report</c>).</summary>
[ScriptDoc("The result of link_many / back_link: ok, the UIDs actually stored, and every rejection with its reason.", Since = 2)]
public sealed class ScriptLinkReport
{
    internal ScriptLinkReport(int[] linked, ScriptLinkRejection[] rejected)
    {
        Linked = linked;
        Rejected = rejected;
    }

    [ScriptDoc("True when every target was linked.", Since = 2)]
    public bool Ok => Rejected.Length == 0;

    [ScriptDoc("The UIDs now linked (a mover is stored as its start keyframe UID).", Since = 2)]
    public int[] Linked { get; }

    [ScriptDoc("The targets that were not linked, with reasons (link_rejection[]).", Since = 2)]
    public ScriptLinkRejection[] Rejected { get; }

    public override string ToString() => $"linked {Linked.Length}, rejected {Rejected.Length}";
}

/// <summary>One refused link target (Lua type <c>link_rejection</c>).</summary>
[ScriptDoc("A link target that was refused: its UID and the reason.", Since = 2)]
public sealed class ScriptLinkRejection
{
    internal ScriptLinkRejection(int uid, string reason)
    {
        Uid = uid;
        Reason = reason;
    }

    [ScriptDoc("The UID that was not linked.", Since = 2)]
    public int Uid { get; }

    [ScriptDoc("Why it was refused (the editor's own link-rule message).", Since = 2)]
    public string Reason { get; }

    public override string ToString() => $"{Uid}: {Reason}";
}
