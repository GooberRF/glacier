using System;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Moves a light between the compiled <c>lights</c> section and the
/// <c>editor_only_lights</c> section (undo-safe). An editor-only light is authoring
/// scaffolding the game never sees; relocating it in/out of the runtime section is
/// the stock "Editor Only" section move (distinct from the per-light Editor-Only
/// flag bit, which is edited on the light itself).
/// </summary>
public static class LightRelocation
{
    /// <summary>Whether <paramref name="uid"/> currently lives in the editor-only section.</summary>
    public static bool IsEditorOnly(EditorDocument doc, int uid)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return FindLight(doc.Rfl, SectionType.EditorOnlyLights, uid) is not null;
    }

    /// <summary>
    /// Moves the light with <paramref name="uid"/> to the other lights section
    /// (runtime ⇄ editor-only), creating the target section if needed. Returns false
    /// when no such light exists.
    /// </summary>
    public static bool Toggle(EditorDocument doc, int uid)
    {
        ArgumentNullException.ThrowIfNull(doc);
        RflFile rfl = doc.Rfl;
        rfl.ParseAllKnownSections();

        SectionType from = SectionType.Lights;
        Light? light = FindLight(rfl, SectionType.Lights, uid);
        if (light is null)
        {
            light = FindLight(rfl, SectionType.EditorOnlyLights, uid);
            from = SectionType.EditorOnlyLights;
        }

        if (light is null)
        {
            return false;
        }

        SectionType to = from == SectionType.Lights ? SectionType.EditorOnlyLights : SectionType.Lights;
        Light moved = light;

        RflSection fromSection = SectionOf(rfl, from)!;
        RflSection toSection = rfl.GetOrCreateSection(to, () => new LightsSection(to));
        var fromList = ((LightsSection)fromSection.Content!).Lights;
        var toList = ((LightsSection)toSection.Content!).Lights;
        int index = fromList.IndexOf(moved);

        doc.Undo.Execute(new RelayCommand(
            to == SectionType.EditorOnlyLights ? "Make light editor-only" : "Make light runtime",
            () =>
            {
                fromList.Remove(moved);
                toList.Add(moved);
                fromSection.Dirty = true;
                toSection.Dirty = true;
                doc.RefreshObjects();
            },
            () =>
            {
                toList.Remove(moved);
                fromList.Insert(Math.Clamp(index, 0, fromList.Count), moved);
                fromSection.Dirty = true;
                toSection.Dirty = true;
                doc.RefreshObjects();
            }));

        return true;
    }

    private static Light? FindLight(RflFile rfl, SectionType type, int uid) =>
        SectionOf(rfl, type)?.Content is LightsSection s ? s.Lights.FirstOrDefault(l => l.Uid == uid) : null;

    private static RflSection? SectionOf(RflFile rfl, SectionType type)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)type)
            {
                return s;
            }
        }

        return null;
    }
}
