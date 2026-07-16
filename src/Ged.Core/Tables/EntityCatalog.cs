namespace Ged.Core.Tables;

/// <summary>A named entity skin: its key plus the ordered list of texture files it swaps in.</summary>
public sealed class EntitySkin
{
    public EntitySkin(string name, IReadOnlyList<string> textures)
    {
        Name = name;
        Textures = textures;
    }

    public string Name { get; }

    public IReadOnlyList<string> Textures { get; }
}

/// <summary>A single entity class from <c>entity.tbl</c>.</summary>
public sealed class EntityDef
{
    public EntityDef(TblRecord record)
    {
        Record = record;
        Name = record.GetString("Name") ?? string.Empty;
        V3dFilename = record.GetString("V3D Filename");
        DebrisFilename = record.GetString("Debris Filename");
        CorpseV3dFilename = record.GetString("Corpse V3D Filename");
        Material = record.GetString("Material");
        Flags = record.GetList("Flags");
        RfeLevel1 = Clean(record.GetString("RFE Level1"));
        RfeLevel2 = Clean(record.GetString("RFE Level2"));

        LodDistances = record.GetList("LOD Distances")
            .Select(t => TblValue.TryFloat(t, out float f) ? f : (float?)null)
            .Where(f => f.HasValue)
            .Select(f => f!.Value)
            .ToList();

        Skins = ParseSkins(record);

        // Fields the object→mesh converter inherits (Alpine EntityClassInfo, tbl.cpp:324-393).
        Flags2 = record.GetList("Flags2");
        LifeValue = record.GetFloat("Life") ?? -1f;
        MaterialIndex = MaterialTypes.ParseMaterial(Material);
        CollisionMode = MaterialTypes.EntityCollisionMode(Flags.Concat(Flags2));
        ExplodeVclip = record.GetString("Explode Anim");
        ExplodeRadius = record.GetFloat("Explode Anim Radius") ?? 1f;
        DamageTypeFactors = MaterialTypes.ParseDamageFactors(record);
        CoronaGlareNames = FirstTokens(record, "Corona (Glare)");
        ThrusterVfxNames = FirstTokens(record, "Thruster VFX");
        StandAnim = ParseStandAnim(record);
    }

    /// <summary>Second flag list (Alpine <c>$Flags2:</c>), e.g. <c>collide_player</c>.</summary>
    public IReadOnlyList<string> Flags2 { get; } = Array.Empty<string>();

    /// <summary>Hit points as a float (Alpine <c>$Life:</c>, default -1 = invulnerable).</summary>
    public float LifeValue { get; }

    /// <summary>Impact-material index (0 Default … 9 Glass) parsed from <see cref="Material"/>.</summary>
    public int MaterialIndex { get; }

    /// <summary>Derived collision mode (0 None, 1 Only Weapons, 2 All) from the flag bits.</summary>
    public int CollisionMode { get; }

    /// <summary>Explosion vclip name (Alpine <c>$Explode Anim:</c>), or null.</summary>
    public string? ExplodeVclip { get; }

    public float ExplodeRadius { get; }

    /// <summary>The 11 per-damage-type factors (default 1.0), for a converted entity mesh.</summary>
    public float[] DamageTypeFactors { get; } = new float[11];

    /// <summary>Per-corona glare names (Alpine <c>$Corona (Glare) N:</c>), in order — 1:1 with the
    /// mesh's <c>corona_N</c> tag points. Each spawns a corona child on conversion.</summary>
    public IReadOnlyList<string> CoronaGlareNames { get; } = Array.Empty<string>();

    /// <summary>Per-thruster VFX filenames (Alpine <c>$Thruster VFX N:</c>), in order — 1:1 with the
    /// mesh's <c>thruster_N</c> tag points. Each spawns a thruster mesh child on conversion.</summary>
    public IReadOnlyList<string> ThrusterVfxNames { get; } = Array.Empty<string>();

    /// <summary>The <c>+State: "stand" "anim"</c> animation, applied as the mesh's idle pose, or null.</summary>
    public string? StandAnim { get; }

    /// <summary>First token of every entry whose key begins with <paramref name="keyPrefix"/> (order preserved).</summary>
    private static IReadOnlyList<string> FirstTokens(TblRecord record, string keyPrefix)
    {
        var result = new List<string>();
        foreach (TblEntry e in record.Entries)
        {
            if (e.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyList<string> t = TblValue.Tokenize(e.Value);
                if (t.Count > 0 && !string.IsNullOrWhiteSpace(t[0]))
                {
                    result.Add(t[0]);
                }
            }
        }

        return result;
    }

    private static string? ParseStandAnim(TblRecord record)
    {
        foreach (string raw in record.GetAllRaw("State"))
        {
            IReadOnlyList<string> t = TblValue.Tokenize(raw);
            if (t.Count >= 2 && string.Equals(t[0], "stand", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(t[1]))
            {
                return t[1];
            }
        }

        return null;
    }

    private static string? Clean(string? s)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public string Name { get; }

    /// <summary>
    /// The editor sub-directory this entity appears under (<c>$RFE Level1:</c>), or null for the
    /// palette root. RED groups entities into these folders (Ultor, Robots, Vehicles, Creatures,
    /// Miners …); the reserved value <c>"Ignore"</c> marks editor-internal entities not shown in
    /// the palette (see <see cref="HideFromPalette"/>).
    /// </summary>
    public string? RfeLevel1 { get; }

    /// <summary>
    /// The second-level editor sub-directory (<c>$RFE Level2:</c>), or null. Only meaningful when
    /// <see cref="RfeLevel1"/> is set. (Stock <c>entity.tbl</c> carries no Level2; parsed for
    /// forward-compatibility with mods that add one, mirroring the clutter table.)
    /// </summary>
    public string? RfeLevel2 { get; }

    /// <summary>
    /// The palette category path for this entity: <c>[]</c> (root) when it has no
    /// <see cref="RfeLevel1"/>, <c>[Level1]</c>, or <c>[Level1, Level2]</c>. Level2 is dropped
    /// when Level1 is absent (the table requires Level2 to accompany a Level1 tag).
    /// </summary>
    public IReadOnlyList<string> CategoryPath =>
        RfeLevel1 is null ? Array.Empty<string>()
        : RfeLevel2 is null ? new[] { RfeLevel1 }
        : new[] { RfeLevel1, RfeLevel2 };

    /// <summary>
    /// True for editor-internal entities RED hides from the placement palette — those tagged
    /// <c>$RFE Level1: "Ignore"</c> (e.g. the "Freelook camera"). Excluded from the entity
    /// palette tree so only placeable game entities appear.
    /// </summary>
    public bool HideFromPalette =>
        string.Equals(RfeLevel1, "Ignore", StringComparison.OrdinalIgnoreCase);

    /// <summary>The entity's mesh (typically a <c>.vcm</c>/<c>.v3c</c> character mesh).</summary>
    public string? V3dFilename { get; }

    public string? DebrisFilename { get; }

    public string? CorpseV3dFilename { get; }

    public string? Material { get; }

    public IReadOnlyList<float> LodDistances { get; }

    public IReadOnlyList<string> Flags { get; }

    public IReadOnlyList<EntitySkin> Skins { get; }

    public TblRecord Record { get; }

    public override string ToString() => Name;

    private static IReadOnlyList<EntitySkin> ParseSkins(TblRecord record)
    {
        var skins = new List<EntitySkin>();
        foreach (string raw in record.GetAllRaw("Skin"))
        {
            // Format: "<name>" ( "tex0" "tex1" ... )
            int paren = raw.IndexOf('(', StringComparison.Ordinal);
            string namePart = paren >= 0 ? raw[..paren] : raw;
            string name = TblValue.Unquote(namePart.Trim());
            if (name.Length == 0)
            {
                continue;
            }

            IReadOnlyList<string> textures = paren >= 0
                ? TblValue.ParseList(raw[paren..].Trim())
                : Array.Empty<string>();
            skins.Add(new EntitySkin(name, textures));
        }

        return skins;
    }
}

/// <summary>Typed catalog of <c>entity.tbl</c>, indexed by name (case-insensitive).</summary>
public sealed class EntityCatalog
{
    private readonly Dictionary<string, EntityDef> _byName;

    private EntityCatalog(IReadOnlyList<EntityDef> entities)
    {
        Entities = entities;
        _byName = new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase);
        foreach (EntityDef e in entities)
        {
            _byName.TryAdd(e.Name, e);
        }
    }

    public IReadOnlyList<EntityDef> Entities { get; }

    public static EntityCatalog Load(byte[] data) => Parse(TblParser.Parse(data));

    public static EntityCatalog Load(string text) => Parse(TblParser.Parse(text));

    public static EntityCatalog Parse(TblDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var entities = doc.Records
            .Where(r => r.Has("Name") && !string.IsNullOrEmpty(r.GetString("Name")))
            .Select(r => new EntityDef(r))
            .ToList();
        return new EntityCatalog(entities);
    }

    public EntityDef? Find(string name) =>
        name is not null && _byName.TryGetValue(name, out EntityDef? d) ? d : null;

    /// <summary>
    /// Builds the entity palette's subcategory tree from the <c>$RFE Level1/Level2</c> tags
    /// (Ultor, Robots, Vehicles, Creatures, Miners …), alphabetical at every level. Editor-internal
    /// entities tagged <c>$RFE Level1: "Ignore"</c> (e.g. the "Freelook camera") are excluded,
    /// matching RED; any entity with no Level1 tag sits at the root alongside the folders.
    /// </summary>
    public Ged.Core.Editing.PaletteCategoryNode BuildPaletteTree() =>
        Ged.Core.Editing.PaletteCategoryTree.Build(
            Entities.Where(e => !e.HideFromPalette).Select(e => (e.Name, e.CategoryPath)));
}
