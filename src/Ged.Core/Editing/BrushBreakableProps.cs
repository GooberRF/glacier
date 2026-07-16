using System;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Undo-safe access to a brush's Alpine breakable material + no-debris flag. These
/// persist per brush UID in alpine_level_properties (<see cref="AlpineBreakableEntry"/>:
/// the material byte packs a 0-6 material index in bits 0-6 and no_debris in bit 7;
/// RoomUid is resolved by the build). An entry is created on first edit and removed
/// again when that edit is undone, so the feature gate never sees a phantom
/// breakable brush after undo.
/// </summary>
public static class BrushBreakableProps
{
    private const byte NoDebrisBit = 0x80;
    private const byte MaterialMask = 0x7F;

    /// <summary>The 0-5 material index for the brush, or 0 (Glass) when unset.</summary>
    public static int GetMaterial(EditorDocument doc, int brushUid) =>
        FindEntry(doc, brushUid) is { } e ? e.Material & MaterialMask : 0;

    /// <summary>The no-debris flag (material byte bit 7), false when unset.</summary>
    public static bool GetNoDebris(EditorDocument doc, int brushUid) =>
        FindEntry(doc, brushUid) is { } e && (e.Material & NoDebrisBit) != 0;

    /// <summary>Sets the material index (bits 0-6), preserving the no-debris bit. Undo-able.</summary>
    public static void SetMaterial(EditorDocument doc, int brushUid, int material) =>
        Edit(doc, brushUid, "Edit Material", cur => (byte)((cur & NoDebrisBit) | (material & MaterialMask)));

    /// <summary>Sets/clears the no-debris flag (bit 7), preserving the material index. Undo-able.</summary>
    public static void SetNoDebris(EditorDocument doc, int brushUid, bool on) =>
        Edit(doc, brushUid, "Edit No Debris", cur => (byte)(on ? cur | NoDebrisBit : cur & MaterialMask));

    private static AlpineBreakableEntry? FindEntry(EditorDocument doc, int brushUid) =>
        doc.Rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>()
            .FirstOrDefault()?.BreakableEntries.FirstOrDefault(b => b.BrushUid == brushUid);

    private static void Edit(EditorDocument doc, int brushUid, string description, Func<byte, byte> apply)
    {
        RflSection host = doc.Rfl.GetOrCreateSection(
            SectionType.AlpineLevelProperties, () => new AlpineLevelPropertiesSection { Version = 4 });
        var alp = (AlpineLevelPropertiesSection)host.Content!;

        AlpineBreakableEntry? existing = alp.BreakableEntries.FirstOrDefault(b => b.BrushUid == brushUid);
        bool created = existing is null;
        byte oldByte = existing?.Material ?? 0;
        byte newByte = apply(oldByte);
        if (!created && oldByte == newByte)
        {
            return; // no-op edit: keep the undo stack clean
        }

        doc.Undo.Execute(new RelayCommand(description,
            () =>
            {
                AlpineBreakableEntry? e = alp.BreakableEntries.FirstOrDefault(b => b.BrushUid == brushUid);
                if (e is null)
                {
                    e = new AlpineBreakableEntry { BrushUid = brushUid };
                    alp.BreakableEntries.Add(e);
                }

                e.Material = newByte;
                host.Dirty = true;
            },
            () =>
            {
                AlpineBreakableEntry? e = alp.BreakableEntries.FirstOrDefault(b => b.BrushUid == brushUid);
                if (e is not null)
                {
                    if (created)
                    {
                        alp.BreakableEntries.Remove(e); // undo removes the entry this edit created
                    }
                    else
                    {
                        e.Material = oldByte;
                    }
                }

                host.Dirty = true;
            }));
    }
}
