using System;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// A brush's Alpine "No shadow cast" setting: membership in the <c>alpine_level_properties</c> no-shadow-cast list
/// (props v5, editor_patch/level.h:1001-1002). Alpine's bake removes such a brush from the shadow occluders, which it
/// can only resolve for a solid brush that survives CSG on its own — a detail brush or a moving-group member
/// (no_shadow_cast_eligible, level.h:2256-2292). Edits are one undo step; the section is created inside the command
/// when the level lacks it, so undoing the first edit leaves the file byte-identical.
/// </summary>
public static class BrushShadowProps
{
    /// <summary>True when the brush is in the no-shadow-cast list.</summary>
    public static bool GetNoShadowCast(EditorDocument doc, int brushUid)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return AlpinePropertiesEdit.Find(doc)?.NoShadowCastBrushUids.Contains(brushUid) == true;
    }

    /// <summary>
    /// True when Alpine can honour the setting for this brush: solid, and detail or a member of a moving group
    /// (level.h:2283-2292).
    /// </summary>
    public static bool IsEligible(RflFile rfl, Brush brush)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(brush);
        if ((brush.Flags & (uint)BrushFlags.Air) != 0)
        {
            return false;
        }

        if ((brush.Flags & (uint)BrushFlags.Detail) != 0)
        {
            return true;
        }

        return rfl.Sections.Select(s => s.Content).OfType<GroupsSection>()
            .SelectMany(g => g.Groups)
            .Any(g => g.IsMoving != 0 && g.Brushes.Contains(brush.Uid));
    }

    /// <summary>Adds or removes the brush from the no-shadow-cast list (one undo step).</summary>
    public static void SetNoShadowCast(EditorDocument doc, int brushUid, bool on)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (GetNoShadowCast(doc, brushUid) == on)
        {
            return;
        }

        AlpinePropertiesEdit edit = AlpinePropertiesEdit.Begin(doc);
        AlpineLevelPropertiesSection alp = edit.Content;
        int[] before = alp.NoShadowCastBrushUids.ToArray();
        doc.Undo.Execute(new RelayCommand(on ? "Set No Shadow Cast" : "Clear No Shadow Cast",
            () =>
            {
                edit.Attach();
                if (on)
                {
                    alp.NoShadowCastBrushUids.Add(brushUid);
                }
                else
                {
                    alp.NoShadowCastBrushUids.RemoveAll(u => u == brushUid);
                }
            },
            () =>
            {
                alp.NoShadowCastBrushUids.Clear();
                alp.NoShadowCastBrushUids.AddRange(before);
                edit.Detach();
            }));
    }
}
