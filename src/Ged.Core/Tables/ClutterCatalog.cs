namespace Ged.Core.Tables;

/// <summary>A single clutter class from <c>clutter.tbl</c>.</summary>
public sealed class ClutterDef
{
    public ClutterDef(TblRecord record)
    {
        Record = record;
        ClassName = record.GetString("Class Name") ?? string.Empty;
        V3dFilename = record.GetString("V3D Filename");
        DebrisFilename = record.GetString("Debris Filename");
        CorpseClassName = record.GetString("Corpse Class Name");
        Material = record.GetString("Material");
        Life = record.GetInt("Life");
        Flags = record.GetList("Flags");
        RfeLevel1 = Clean(record.GetString("RFE Level1"));
        RfeLevel2 = Clean(record.GetString("RFE Level2"));

        // Fields the object→mesh converter inherits (Alpine ClutterClassInfo, tbl.cpp:131-165).
        LifeValue = record.GetFloat("Life") ?? -1f;
        MaterialIndex = MaterialTypes.ParseMaterial(Material);
        CollisionMode = MaterialTypes.ClutterCollisionMode(Flags);
        DebrisVelocity = record.GetFloat("Debris Velocity") ?? 10f;
        ExplodeVclip = record.GetString("Explode Anim");
        ExplodeRadius = record.GetFloat("Explode Anim Radius") ?? 1f;
        DamageTypeFactors = MaterialTypes.ParseDamageFactors(record);
        GlareName = Clean(record.GetString("Glare"));
    }

    /// <summary>Hit points as a float (Alpine <c>$Life:</c>, default -1 = invulnerable).</summary>
    public float LifeValue { get; }

    /// <summary>Impact-material index (0 Default … 9 Glass) parsed from <see cref="Material"/>.</summary>
    public int MaterialIndex { get; }

    /// <summary>Derived collision mode (0 None, 1 Only Weapons, 2 All) from the flag bits.</summary>
    public int CollisionMode { get; }

    public float DebrisVelocity { get; }

    /// <summary>Explosion vclip name (Alpine <c>$Explode Anim:</c>), or null.</summary>
    public string? ExplodeVclip { get; }

    public float ExplodeRadius { get; }

    /// <summary>The 11 per-damage-type factors (default 1.0), for a converted clutter mesh.</summary>
    public float[] DamageTypeFactors { get; }

    /// <summary>The corona glare effect name (Alpine <c>$Glare:</c>), or null — spawns a corona child.</summary>
    public string? GlareName { get; }

    private static string? Clean(string? s)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public string ClassName { get; }

    /// <summary>The mesh (.v3d/.v3m/.v3c) rendered for this clutter, if any.</summary>
    public string? V3dFilename { get; }

    public string? DebrisFilename { get; }

    public string? CorpseClassName { get; }

    public string? Material { get; }

    public int? Life { get; }

    public IReadOnlyList<string> Flags { get; }

    /// <summary>
    /// The editor sub-directory this clutter appears under (<c>$RFE Level1:</c>), or null for
    /// the clutter-palette root. RED groups clutter into these folders (Furniture, Computers,
    /// Lights, Natural, …).
    /// </summary>
    public string? RfeLevel1 { get; }

    /// <summary>
    /// The second-level editor sub-directory (<c>$RFE Level2:</c>), or null. Only meaningful
    /// when <see cref="RfeLevel1"/> is set (e.g. Natural ▸ Plants / Rocks / Water).
    /// </summary>
    public string? RfeLevel2 { get; }

    /// <summary>
    /// The palette category path for this clutter: <c>[]</c> (root) when it has no
    /// <see cref="RfeLevel1"/>, <c>[Level1]</c>, or <c>[Level1, Level2]</c>. Level2 is dropped
    /// when Level1 is absent (the table requires Level2 to accompany a Level1 tag).
    /// </summary>
    public IReadOnlyList<string> CategoryPath =>
        RfeLevel1 is null ? Array.Empty<string>()
        : RfeLevel2 is null ? new[] { RfeLevel1 }
        : new[] { RfeLevel1, RfeLevel2 };

    public TblRecord Record { get; }

    public override string ToString() => ClassName;
}

/// <summary>Typed catalog of <c>clutter.tbl</c>, indexed by class name (case-insensitive).</summary>
public sealed class ClutterCatalog
{
    private readonly Dictionary<string, ClutterDef> _byName;

    private ClutterCatalog(IReadOnlyList<ClutterDef> clutters)
    {
        Clutters = clutters;
        _byName = new Dictionary<string, ClutterDef>(StringComparer.OrdinalIgnoreCase);
        foreach (ClutterDef c in clutters)
        {
            _byName.TryAdd(c.ClassName, c);
        }
    }

    public IReadOnlyList<ClutterDef> Clutters { get; }

    public static ClutterCatalog Load(byte[] data) => Parse(TblParser.Parse(data));

    public static ClutterCatalog Load(string text) => Parse(TblParser.Parse(text));

    public static ClutterCatalog Parse(TblDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var clutters = doc.Records
            .Where(r => r.Has("Class Name") && !string.IsNullOrEmpty(r.GetString("Class Name")))
            .Select(r => new ClutterDef(r))
            .ToList();
        return new ClutterCatalog(clutters);
    }

    public ClutterDef? Find(string className) =>
        className is not null && _byName.TryGetValue(className, out ClutterDef? d) ? d : null;

    /// <summary>
    /// Builds the clutter palette's subcategory tree from the <c>$RFE Level1/Level2</c> tags
    /// (Furniture, Computers, Natural ▸ Plants, …), alphabetical at every level. Clutter with
    /// no Level1 tag sits at the root alongside the folders.
    /// </summary>
    public Ged.Core.Editing.PaletteCategoryNode BuildPaletteTree() =>
        Ged.Core.Editing.PaletteCategoryTree.Build(Clutters.Select(c => (c.ClassName, c.CategoryPath)));
}
