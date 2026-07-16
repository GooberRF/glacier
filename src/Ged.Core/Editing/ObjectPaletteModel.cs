using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;

namespace Ged.Core.Editing;

/// <summary>
/// The headless, UI-free state behind the object palette's class-bearing rows
/// (entity / clutter / item): the available class names per kind and the currently
/// selected ("pending placement") class. The <c>PalettePanel</c> is a thin view over
/// this model so the selection/placement contract can be exercised without a UI.
///
/// The class lists come from the <c>.tbl</c> catalogs, which are only populated once
/// an RF install is mounted. Because the palette can be realized before that happens,
/// the model must be refreshable: <see cref="RefreshClasses"/> re-reads the class
/// names (preserving the current selection when it survives) and is what the shell
/// calls when an install mounts. A palette whose classes are never refreshed after a
/// late mount is exactly the "dropdowns are empty" regression this model guards.
/// </summary>
public sealed class ObjectPaletteModel
{
    private readonly Dictionary<LevelObjectKind, IReadOnlyList<string>> _classes = new();
    private readonly Dictionary<LevelObjectKind, string?> _selected = new();

    /// <summary>The placeable object types shown in the palette (from <see cref="ObjectFactory.Palette"/>).</summary>
    public IReadOnlyList<PlaceableObjectType> Types => ObjectFactory.Palette;

    /// <summary>The kinds that carry a catalog class name (entity / clutter / item).</summary>
    public static IEnumerable<LevelObjectKind> ClassBearingKinds =>
        ObjectFactory.Palette.Where(t => t.NeedsClassName).Select(t => t.Kind);

    /// <summary>True when at least one class-bearing kind currently has class names.</summary>
    public bool HasAnyClasses => _classes.Values.Any(v => v.Count > 0);

    /// <summary>
    /// Re-reads the class names for every class-bearing kind from <paramref name="provider"/>
    /// (typically the mounted catalogs). A surviving selection is kept; otherwise the first
    /// class is auto-selected so a plain "Place" always has a valid class. Safe to call
    /// repeatedly (e.g. whenever an install is mounted or the tables are reloaded).
    /// </summary>
    public void RefreshClasses(Func<LevelObjectKind, IReadOnlyList<string>?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        foreach (LevelObjectKind kind in ClassBearingKinds)
        {
            IReadOnlyList<string> names = provider(kind) ?? Array.Empty<string>();
            _classes[kind] = names;

            string? current = Selected(kind);
            if (current is null || !names.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                _selected[kind] = names.Count > 0 ? names[0] : null;
            }
        }
    }

    /// <summary>The available class names for a kind (empty until refreshed with a mounted install).</summary>
    public IReadOnlyList<string> ClassesFor(LevelObjectKind kind) =>
        _classes.TryGetValue(kind, out IReadOnlyList<string>? names) ? names : Array.Empty<string>();

    /// <summary>The selected ("pending placement") class for a kind, or null.</summary>
    public string? Selected(LevelObjectKind kind) =>
        _selected.TryGetValue(kind, out string? s) ? s : null;

    /// <summary>Sets the pending-placement class for a kind (from a dropdown selection).</summary>
    public void Select(LevelObjectKind kind, string? name) => _selected[kind] = name;

    /// <summary>
    /// The class name to place for a kind: the selected class for class-bearing kinds
    /// (null when the kind takes no class, e.g. Light/Trigger).
    /// </summary>
    public string? PlacementClass(LevelObjectKind kind) =>
        ObjectFactory.Palette.Any(t => t.Kind == kind && t.NeedsClassName) ? Selected(kind) : null;
}
