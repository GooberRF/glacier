using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>A brush's visibility state in the Layers panel (item 9).</summary>
public enum BrushVisibility
{
    Normal,
    Locked,
    Hidden,
}

/// <summary>
/// The solidity-filter chips: Air vs Solid, their own independent group.
/// Every brush is exactly one of the two (Solid ⇔ !Air), so this is a clean partition;
/// OR within the set.
/// </summary>
[Flags]
public enum LayerSolidity
{
    None = 0,
    Air = 1,
    Solid = 2,
    All = Air | Solid,
}

/// <summary>
/// The property-filter chips: Detail/Portal/Geoable/Breakable — the
/// non-exhaustive, non-exclusive brush modifiers (a brush may carry several or none).
/// OR within the set; a "plain" brush (no modifier) is never hidden by this group.
/// </summary>
[Flags]
public enum LayerProps
{
    None = 0,
    Detail = 1,
    Portal = 2,
    Geoable = 4,
    Breakable = 8,
    All = Detail | Portal | Geoable | Breakable,
}

/// <summary>The visibility-filter checkboxes (item 9); OR within the set.</summary>
[Flags]
public enum LayerVis
{
    None = 0,
    Normal = 1,
    Locked = 2,
    Hidden = 4,
    All = Normal | Locked | Hidden,
}

/// <summary>
/// One Layers-panel row: the brush UID, its property flags, its visibility state, and its
/// build-order time index. <see cref="TimeIndex"/> is the 0-based position in build order —
/// the same number RED prints as "t=X" in its console for a selected brush (RED.exe
/// brush_mode_handle_selection walks the brush list from the head, so the first brush is
/// t=0). It is computed, never persisted, and recomputed on every reorder.
/// </summary>
public sealed record LayerRow(
    int Uid, bool Air, bool Detail, bool Portal, bool Geoable, bool Breakable, BrushVisibility Visibility, int TimeIndex)
{
    public bool Solid => !Air;

    public bool Locked => Visibility == BrushVisibility.Locked;

    public bool Hidden => Visibility == BrushVisibility.Hidden;
}

/// <summary>
/// Pure view-model logic for the Layers panel: building rows in build/time
/// order (position = order — no layer number), the three-group filter matrix (OR within a
/// group, AND between groups: {Air|Solid} × {Detail|Portal|Geoable|Breakable} × {Normal|
/// Locked|Hidden}), and the reorder math (multi-select block move + single-step nudge).
/// The App applies the resulting order through <see cref="BrushEditor.ReorderTo"/>.
/// </summary>
public static class LayersModel
{
    /// <summary>Builds rows in build order; the row's position in the list conveys the order.</summary>
    public static IReadOnlyList<LayerRow> BuildRows(IReadOnlyList<Brush> brushes, IReadOnlySet<int>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        var rows = new List<LayerRow>(brushes.Count);
        for (int i = 0; i < brushes.Count; i++)
        {
            Brush b = brushes[i];
            var flags = (BrushFlags)b.Flags;
            BrushVisibility vis =
                hidden?.Contains(b.Uid) == true ? BrushVisibility.Hidden :
                b.State == BrushState.Locked ? BrushVisibility.Locked :
                BrushVisibility.Normal;
            rows.Add(new LayerRow(
                Uid: b.Uid,
                Air: (flags & BrushFlags.Air) != 0,
                Detail: (flags & BrushFlags.Detail) != 0,
                Portal: (flags & BrushFlags.Portal) != 0,
                Geoable: (flags & BrushFlags.Geoable) != 0,
                Breakable: b.Life != -1,
                Visibility: vis,
                TimeIndex: i)); // 0-based build position = RED's "t=X"
        }

        return rows;
    }

    /// <summary>
    /// Whether a row passes the three independent filter groups (OR within each group, AND
    /// between them):
    /// <list type="bullet">
    /// <item><b>Solidity</b> {Air, Solid}: matches if the row's Air/Solid nature is enabled.</item>
    /// <item><b>Properties</b> {Detail, Portal, Geoable, Breakable}: matches if the row carries
    /// none of these modifiers (a plain brush is never hidden by this group) OR any modifier it
    /// carries is enabled.</item>
    /// <item><b>Visibility</b> {Normal, Locked, Hidden}: matches if the row's state is enabled.</item>
    /// </list>
    /// E.g. {Air, Solid} × {} (all props off) × {Locked} shows Locked brushes that carry no
    /// modifier; {Air, Solid} × {Portal} × {Normal, Locked, Hidden} shows plain + portal brushes.
    /// </summary>
    public static bool Passes(LayerRow r, LayerSolidity solidity, LayerProps props, LayerVis vis)
    {
        bool okSolidity =
            (r.Air && (solidity & LayerSolidity.Air) != 0) ||
            (r.Solid && (solidity & LayerSolidity.Solid) != 0);

        bool hasModifier = r.Detail || r.Portal || r.Geoable || r.Breakable;
        bool okProps = !hasModifier ||
            (r.Detail && (props & LayerProps.Detail) != 0) ||
            (r.Portal && (props & LayerProps.Portal) != 0) ||
            (r.Geoable && (props & LayerProps.Geoable) != 0) ||
            (r.Breakable && (props & LayerProps.Breakable) != 0);

        bool okVis =
            (r.Visibility == BrushVisibility.Normal && (vis & LayerVis.Normal) != 0) ||
            (r.Locked && (vis & LayerVis.Locked) != 0) ||
            (r.Hidden && (vis & LayerVis.Hidden) != 0);

        return okSolidity && okProps && okVis;
    }

    /// <summary>
    /// Moves the <paramref name="selected"/> UIDs as one contiguous block (preserving their
    /// relative order) so the block begins at build index <paramref name="dropIndex"/>
    /// (0..count). Returns the new UID order.
    /// </summary>
    public static List<int> MoveBlock(IReadOnlyList<int> order, IReadOnlyCollection<int> selected, int dropIndex)
    {
        var sel = new HashSet<int>(selected);
        List<int> block = order.Where(sel.Contains).ToList();
        List<int> rest = order.Where(u => !sel.Contains(u)).ToList();

        // Insert before as many non-selected items as originally preceded the drop point.
        int insertAt = 0;
        for (int i = 0; i < order.Count && i < dropIndex; i++)
        {
            if (!sel.Contains(order[i]))
            {
                insertAt++;
            }
        }

        var result = new List<int>(order.Count);
        result.AddRange(rest.Take(insertAt));
        result.AddRange(block);
        result.AddRange(rest.Skip(insertAt));
        return result;
    }

    /// <summary>Nudges a single UID by <paramref name="delta"/> positions (−1 up / +1 down), clamped.</summary>
    public static List<int> Nudge(IReadOnlyList<int> order, int uid, int delta)
    {
        var list = order.ToList();
        int idx = list.IndexOf(uid);
        if (idx < 0)
        {
            return list;
        }

        int newIdx = Math.Clamp(idx + delta, 0, list.Count - 1);
        if (newIdx == idx)
        {
            return list;
        }

        list.RemoveAt(idx);
        list.Insert(newIdx, uid);
        return list;
    }
}
