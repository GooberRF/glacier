namespace Ged.Core.Tables;

/// <summary>A single placeable item from <c>items.tbl</c>.</summary>
public sealed class ItemDef
{
    public ItemDef(TblRecord record)
    {
        Record = record;
        ClassName = record.GetString("Class Name") ?? string.Empty;
        HudName = record.GetString("HUD Msg Name");
        V3dFilename = record.GetString("V3D Filename");
        V3dType = record.GetString("V3D Type");
        Count = record.GetInt("Count");
        RespawnTime = record.GetInt("Respawn Time");
        GivesWeapon = record.GetString("Gives Weapon");
        AmmoFor = record.GetString("Ammo For");
        Flags = record.GetList("Flags");
    }

    public string ClassName { get; }

    public string? HudName { get; }

    /// <summary>The mesh (.v3d/.v3m/.v3c) rendered for this item, if any.</summary>
    public string? V3dFilename { get; }

    public string? V3dType { get; }

    public int? Count { get; }

    public int? RespawnTime { get; }

    public string? GivesWeapon { get; }

    public string? AmmoFor { get; }

    public IReadOnlyList<string> Flags { get; }

    /// <summary>The underlying record for access to fields not surfaced here.</summary>
    public TblRecord Record { get; }

    public override string ToString() => ClassName;
}

/// <summary>Typed catalog of <c>items.tbl</c>, indexed by class name (case-insensitive).</summary>
public sealed class ItemCatalog
{
    private readonly Dictionary<string, ItemDef> _byName;

    private ItemCatalog(IReadOnlyList<ItemDef> items)
    {
        Items = items;
        _byName = new Dictionary<string, ItemDef>(StringComparer.OrdinalIgnoreCase);
        foreach (ItemDef i in items)
        {
            _byName.TryAdd(i.ClassName, i);
        }
    }

    public IReadOnlyList<ItemDef> Items { get; }

    public static ItemCatalog Load(byte[] data) => Parse(TblParser.Parse(data));

    public static ItemCatalog Load(string text) => Parse(TblParser.Parse(text));

    public static ItemCatalog Parse(TblDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var items = doc.Records
            .Where(r => r.Has("Class Name") && !string.IsNullOrEmpty(r.GetString("Class Name")))
            .Select(r => new ItemDef(r))
            .ToList();
        return new ItemCatalog(items);
    }

    public ItemDef? Find(string className) =>
        className is not null && _byName.TryGetValue(className, out ItemDef? d) ? d : null;
}
