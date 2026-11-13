using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editor;

namespace Ged.Core.Tables;

/// <summary>
/// The game's object class tables (<c>entity.tbl</c>, <c>items.tbl</c>, <c>clutter.tbl</c>) read
/// through the mounted VFS — the one place the editor palette, the Properties panel's class lists and
/// the script <c>catalog</c> get valid class names from. A table that is missing or does not parse is
/// a null entry (the rest still load); <see cref="Empty"/> stands in when no install is mounted.
/// </summary>
public sealed class GameCatalogs
{
    public EntityCatalog? Entities { get; init; }

    public ItemCatalog? Items { get; init; }

    public ClutterCatalog? Clutter { get; init; }

    /// <summary>True when at least one table loaded.</summary>
    public bool Any => Entities is not null || Items is not null || Clutter is not null;

    /// <summary>No tables (no install mounted).</summary>
    public static GameCatalogs Empty { get; } = new();

    /// <summary>Loads the three tables from <paramref name="vfs"/>; each missing/unparsable one is null.</summary>
    public static GameCatalogs Load(AssetVfs vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        return new GameCatalogs
        {
            Entities = TryLoad(vfs, "entity.tbl", EntityCatalog.Load),
            Items = TryLoad(vfs, "items.tbl", ItemCatalog.Load),
            Clutter = TryLoad(vfs, "clutter.tbl", ClutterCatalog.Load),
        };
    }

    /// <summary>Whether the table for <paramref name="kind"/> is loaded (only Entity / Item / Clutter have tables).</summary>
    public bool HasTable(LevelObjectKind kind) => kind switch
    {
        LevelObjectKind.Entity => Entities is not null,
        LevelObjectKind.Item => Items is not null,
        LevelObjectKind.Clutter => Clutter is not null,
        _ => false,
    };

    /// <summary>The class names of <paramref name="kind"/>'s table, sorted ordinal-ignore-case (empty when unloaded / no table).</summary>
    public IReadOnlyList<string> ClassNames(LevelObjectKind kind)
    {
        IEnumerable<string> names = kind switch
        {
            LevelObjectKind.Entity => Entities?.Entities.Select(e => e.Name) ?? Enumerable.Empty<string>(),
            LevelObjectKind.Item => Items?.Items.Select(i => i.ClassName) ?? Enumerable.Empty<string>(),
            LevelObjectKind.Clutter => Clutter?.Clutters.Select(c => c.ClassName) ?? Enumerable.Empty<string>(),
            _ => Enumerable.Empty<string>(),
        };

        return names.Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The <c>items.tbl</c> <c>$Count</c> of an item class — what one pickup gives (an Assault Rifle's 42
    /// rounds, a Medical Kit's 25 health) and what RED writes as a new item's count — or null when the
    /// table is not loaded, the class is unknown, or it has no count.
    /// </summary>
    public int? ItemCount(string? className) =>
        className is null || Items?.Find(className)?.Count is not int count || count < 0 ? null : count;

    /// <summary>The table's own spelling of <paramref name="className"/> (case-insensitive match), or null.</summary>
    public string? Canonical(LevelObjectKind kind, string className) =>
        ClassNames(kind).FirstOrDefault(n => string.Equals(n, className, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A one-line digest of what Glacier parses for a table class (item: HUD name, weapon given,
    /// ammo; entity / clutter: mesh and life), or an empty string when unknown.
    /// </summary>
    public string Describe(LevelObjectKind kind, string className)
    {
        var parts = new List<string>();
        switch (kind)
        {
            case LevelObjectKind.Item when Items?.Find(className) is { } item:
                Add(parts, "HUD name", item.HudName);
                Add(parts, "gives weapon", item.GivesWeapon);
                Add(parts, "ammo for", item.AmmoFor);
                break;
            case LevelObjectKind.Entity when Entities?.Find(className) is { } e:
                Add(parts, "mesh", e.V3dFilename);
                if (e.LifeValue >= 0)
                {
                    parts.Add($"life {e.LifeValue.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                break;
            case LevelObjectKind.Clutter when Clutter?.Find(className) is { } c:
                Add(parts, "mesh", c.V3dFilename);
                if (c.Life is int life)
                {
                    parts.Add($"life {life}");
                }

                break;
        }

        return string.Join("; ", parts);
    }

    /// <summary>The table category path of a class (entity/clutter RFE levels), or an empty string.</summary>
    public string Category(LevelObjectKind kind, string className) => kind switch
    {
        LevelObjectKind.Entity => string.Join(" / ", Entities?.Find(className)?.CategoryPath ?? Array.Empty<string>()),
        LevelObjectKind.Clutter => string.Join(" / ", Clutter?.Find(className)?.CategoryPath ?? Array.Empty<string>()),
        _ => string.Empty,
    };

    private static void Add(List<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{label} {value}");
        }
    }

    private static T? TryLoad<T>(AssetVfs vfs, string table, Func<byte[], T> parse)
        where T : class
    {
        try
        {
            byte[]? data = vfs.ReadFile(table);
            return data is null ? null : parse(data);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
