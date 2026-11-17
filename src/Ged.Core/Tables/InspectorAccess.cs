using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;

namespace Ged.Core.Tables;

/// <summary>
/// The one shared get/set path for inspector fields, used by the Properties
/// panel and the script API alike. Values cross this boundary in a canonical editor form —
/// <c>string</c>, <c>int</c> (ints, UIDs and enum indices), <c>float</c>, <c>bool</c>,
/// <see cref="RfColor"/>, <see cref="Vec3"/>, or null for a conditional member that does not apply
/// (<see cref="InspectorPresence"/>) — whatever the model's storage type is. Covers the
/// <see cref="InspectorField.Virtual"/> fields the plain reflection accessor cannot: the trigger
/// 0xAB multiplayer flags, the light editor-only membership (read-only), the brush lock /
/// breakable material / no-debris / UID / time index, and the mover movement type / hold-open.
/// <para>
/// Every write is ONE undoable command whose undo restores the exact prior raw values (the whole
/// bitfield, every conditional member), so a rolled-back script run is byte-identical. A write
/// equal to the current value is a no-op (no command). Callers validate first; a read-only field
/// or a conditional member that does not apply throws <see cref="InvalidOperationException"/>.
/// </para>
/// </summary>
public static class InspectorAccess
{
    // ---- Classification ---------------------------------------------------------

    /// <summary>True for display-only fields (brush UID / time index, the light editor-only membership).</summary>
    public static bool IsReadOnly(InspectorField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.ReadOnly;
    }

    /// <summary>The hint naming what to change first when <paramref name="field"/> does not apply to <paramref name="model"/> now; null when it applies.</summary>
    public static string? Unavailable(object model, InspectorField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Virtual ? AlpineVirtualFields.Unavailable(model, field.EffectiveKey) : InspectorPresence.UnavailableHint(model, field.Path);
    }

    /// <summary>
    /// The inclusive integer range a field can store on <paramref name="model"/>: its storage type
    /// (byte 0–255, ushort, int …) or bitfield slice, intersected with <see cref="InspectorField.Min"/>/<see cref="InspectorField.Max"/>.
    /// </summary>
    public static (double Min, double Max) IntegerRange(object? model, InspectorField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        double min = int.MinValue, max = int.MaxValue;
        if (field.Mask != 0)
        {
            min = 0;
            max = field.Mask >> field.Shift;
        }
        else if (!field.Virtual && model is not null && field.StorageType(model) is { } t)
        {
            (min, max) = TypeRange(t, min, max);
        }

        if (field.Min is double lo)
        {
            min = Math.Max(min, lo);
        }

        if (field.Max is double hi)
        {
            max = Math.Min(max, hi);
        }

        return (min, max);
    }

    /// <summary>Converts a raw stored value to the field's canonical editor form.</summary>
    public static object? Canonical(InspectorField field, object? raw)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (raw is null)
        {
            return null;
        }

        return field.Editor switch
        {
            InspectorEditor.Int or InspectorEditor.Uid or InspectorEditor.Enum => raw is bool b ? (b ? 1 : 0) : ToInt(raw),
            InspectorEditor.Float => Convert.ToSingle(raw, CultureInfo.InvariantCulture),
            InspectorEditor.Bool => raw is bool flag ? flag : Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0,
            InspectorEditor.Text => raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture),
            _ => raw,
        };
    }

    // ---- Level objects ------------------------------------------------------------

    /// <summary>The canonical value of <paramref name="field"/> on <paramref name="obj"/> (null when it does not apply).</summary>
    public static object? Get(EditorDocument doc, LevelObject obj, InspectorField field)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(field);
        object model = obj.Model;
        if (field.Virtual)
        {
            return GetVirtual(doc, obj, field);
        }

        return Unavailable(model, field) is not null ? null : Canonical(field, field.Get(model));
    }

    /// <summary>
    /// Writes a canonical value as one undoable command. Keyframe fields route through
    /// <see cref="MoverService.EditKeyframe"/> (as the keyframe inspector does); everything else is a
    /// document command dirtying the object's section. A conditional discriminator (shape,
    /// directional, …) also brings the dependent members to their canonical shape in the same command.
    /// </summary>
    public static void Set(EditorDocument doc, LevelObject obj, InspectorField field, object? value, MoverService? movers = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(field);
        RequireWritable(obj.Model, field);
        if (Equals(Get(doc, obj, field), value))
        {
            return;
        }

        if (field.Virtual)
        {
            SetVirtual(doc, obj, field, value);
            return;
        }

        object model = obj.Model;
        string description = "Edit " + field.Label;
        (Action apply, Action revert) = RawEdit(model, field, value);
        if (model is Keyframe kf)
        {
            (movers ?? new MoverService(doc)).EditKeyframe(kf, description, _ => apply(), _ => revert());
            return;
        }

        RflSection section = obj.Section;
        doc.Undo.Execute(new RelayCommand(description,
            () =>
            {
                apply();
                section.Dirty = true;
            },
            () =>
            {
                revert();
                section.Dirty = true;
            }));
    }

    // ---- Event fields -------------------------------------------------------------

    /// <summary>The canonical editor value of an event field (<see cref="EventFieldAccess.Get"/>).</summary>
    public static object? Get(RflEvent ev, EventFieldSpec field) => EventFieldAccess.Get(field, ev);

    /// <summary>
    /// Writes an event field through <see cref="EventFieldAccess"/> (never by poking slots) as one
    /// undoable command; undo restores every raw slot verbatim, so a trap encoding (int-in-float,
    /// single-character flag) rolls back byte-exactly.
    /// </summary>
    public static void Set(EditorDocument doc, LevelObject obj, EventFieldSpec field, object? value)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(field);
        if (obj.Model is not RflEvent ev)
        {
            throw new InvalidOperationException($"{obj.Kind} {obj.Uid} is not an event.");
        }

        if (Equals(EventFieldAccess.Get(field, ev), value))
        {
            return;
        }

        var before = EventSlots.Capture(ev);
        RflSection section = obj.Section;
        doc.Undo.Execute(new RelayCommand("Edit " + field.Label,
            () =>
            {
                EventFieldAccess.Set(field, ev, value);
                section.Dirty = true;
            },
            () =>
            {
                before.Restore(ev);
                section.Dirty = true;
            }));
    }

    // ---- Brushes ------------------------------------------------------------------

    /// <summary>The canonical value of a <see cref="BrushInspectorCatalog"/> field on brush <paramref name="uid"/>.</summary>
    public static object? Get(EditorDocument doc, BrushEditor brushes, int uid, InspectorField field)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(brushes);
        ArgumentNullException.ThrowIfNull(field);
        Brush b = brushes.FindBrush(uid) ?? throw new InvalidOperationException($"Brush {uid} does not exist.");
        if (!field.Virtual)
        {
            return Canonical(field, field.Get(b));
        }

        return field.EffectiveKey switch
        {
            "locked" => b.State == BrushState.Locked,
            "material" => BrushBreakableProps.GetMaterial(doc, uid),
            "no_debris" => BrushBreakableProps.GetNoDebris(doc, uid),
            "no_shadow_cast" => BrushShadowProps.GetNoShadowCast(doc, uid),
            "uid" => b.Uid,
            "time_index" => brushes.TimeIndex(uid),
            _ => null,
        };
    }

    /// <summary>
    /// Writes a brush field on every brush in <paramref name="uids"/>: plain fields as one
    /// <see cref="BrushEditor.EditBrushes"/> (flag implication via <see cref="BrushInspectorCatalog.Normalize"/>,
    /// exactly as the Properties panel), the lock through <see cref="BrushEditor.SetBrushLocked"/>,
    /// the Alpine material / no-debris through <see cref="BrushBreakableProps"/>.
    /// </summary>
    public static void Set(EditorDocument doc, BrushEditor brushes, IReadOnlyCollection<int> uids, InspectorField field, object? value)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(brushes);
        ArgumentNullException.ThrowIfNull(uids);
        ArgumentNullException.ThrowIfNull(field);
        if (IsReadOnly(field))
        {
            throw new InvalidOperationException($"Field '{field.EffectiveKey}' is read-only.");
        }

        var targets = uids.Where(u => !Equals(Get(doc, brushes, u, field), value)).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        switch (field.Virtual ? field.EffectiveKey : null)
        {
            case "locked":
                brushes.SetBrushLocked(targets, value is true);
                return;
            case "material":
                foreach (int u in targets)
                {
                    BrushBreakableProps.SetMaterial(doc, u, ToInt(value!));
                }

                return;
            case "no_debris":
                foreach (int u in targets)
                {
                    BrushBreakableProps.SetNoDebris(doc, u, value is true);
                }

                return;
            case "no_shadow_cast":
                using (doc.Undo.BeginTransaction("Edit " + field.Label))
                {
                    foreach (int u in targets)
                    {
                        BrushShadowProps.SetNoShadowCast(doc, u, value is true);
                    }
                }

                return;
            case null:
                OpResult r = brushes.EditBrushes(targets, "Edit " + field.Label, b =>
                {
                    field.Set(b, value);
                    b.Flags = BrushInspectorCatalog.Normalize(b.Flags);
                    return OpResult.Ok();
                });
                if (!r.Success)
                {
                    throw new InvalidOperationException(r.Message);
                }

                return;
            default:
                throw new InvalidOperationException($"Field '{field.EffectiveKey}' cannot be written.");
        }
    }

    // ---- Movers -------------------------------------------------------------------

    /// <summary>The canonical value of a <see cref="MoverInspectorSchema.MoverFields"/> field (movement type = 0-based option index).</summary>
    public static object? Get(MoverService movers, Group mover, InspectorField field)
    {
        ArgumentNullException.ThrowIfNull(movers);
        ArgumentNullException.ThrowIfNull(mover);
        ArgumentNullException.ThrowIfNull(field);
        MovingGroupData? data = mover.MovingData;
        if (data is null)
        {
            return null;
        }

        if (field.Virtual && AlpineVirtualFields.IsMoverPathKey(field.EffectiveKey))
        {
            return AlpineVirtualFields.GetMoverPath(movers.Document, mover, field.EffectiveKey);
        }

        return field.EffectiveKey switch
        {
            "movement_type" when field.Virtual => data.MovementType - 1,
            "hold_open" when field.Virtual => movers.IsHoldOpen(mover),
            _ => Canonical(field, field.Get(data)),
        };
    }

    /// <summary>Writes a mover field: <see cref="MoverService.EditMover"/> (movement type stored 1-based), hold-open via <see cref="MoverService.SetHoldOpen"/>.</summary>
    public static void Set(MoverService movers, Group mover, InspectorField field, object? value)
    {
        ArgumentNullException.ThrowIfNull(movers);
        ArgumentNullException.ThrowIfNull(mover);
        ArgumentNullException.ThrowIfNull(field);
        MovingGroupData data = mover.MovingData ?? throw new InvalidOperationException($"'{mover.Name}' is not a mover.");
        if (Equals(Get(movers, mover, field), value))
        {
            return;
        }

        string description = "Edit " + field.Label;
        if (field.Virtual && AlpineVirtualFields.IsMoverPathKey(field.EffectiveKey))
        {
            AlpineVirtualFields.SetMoverPath(movers.Document, mover, field, value);
            return;
        }

        switch (field.Virtual ? field.EffectiveKey : null)
        {
            case "movement_type":
                int oldType = data.MovementType;
                int newType = ToInt(value!) + 1;
                movers.EditMover(data, description, d => d.MovementType = newType, d => d.MovementType = oldType);
                return;
            case "hold_open":
                movers.SetHoldOpen(mover, value is true);
                return;
            case null:
                object? oldRaw = field.GetRaw(data);
                movers.EditMover(data, description, d => field.Set(d, value), d => field.SetRaw(d, oldRaw));
                return;
            default:
                throw new InvalidOperationException($"Field '{field.EffectiveKey}' cannot be written.");
        }
    }

    // ---- Helpers ------------------------------------------------------------------

    private static void RequireWritable(object model, InspectorField field)
    {
        if (IsReadOnly(field))
        {
            throw new InvalidOperationException($"Field '{field.EffectiveKey}' is read-only.");
        }

        if (Unavailable(model, field) is { } hint)
        {
            throw new InvalidOperationException($"Field '{field.EffectiveKey}' does not apply right now. {hint}");
        }
    }

    /// <summary>An apply/revert pair for a plain field: raw snapshot of the field and every conditional member.</summary>
    private static (Action Apply, Action Revert) RawEdit(object model, InspectorField field, object? value)
    {
        object? oldRaw = field.GetRaw(model);
        IReadOnlyList<(ConditionalMember Member, object? Raw)> snapshot = InspectorPresence.Snapshot(model);
        return (
            () =>
            {
                field.Set(model, value);
                InspectorPresence.Normalize(model);
            },
            () =>
            {
                field.SetRaw(model, oldRaw);
                InspectorPresence.Restore(model, snapshot);
            });
    }

    private static object? GetVirtual(EditorDocument doc, LevelObject obj, InspectorField field)
    {
        switch (obj.Model)
        {
            case Trigger t when TriggerFlagBit(field) is int bit:
                return t.IsPureFactionEncoded && (t.ScriptName[1] & bit) != 0;
            case Light when field.EffectiveKey == "editor_only":
                return obj.Section.TypeId == (uint)SectionType.EditorOnlyLights;
            case AlpineRopeEmitter rope:
                return AlpineVirtualFields.GetRope(rope, field.EffectiveKey);
            case RoomEffect fx:
                return AlpineVirtualFields.GetEmission(doc, fx.Header.Uid, field.EffectiveKey);
            case Keyframe kf:
                return AlpineVirtualFields.GetKeyframePath(doc, kf.Uid, field.EffectiveKey);
            case AlpineTerrain terrain when field.EffectiveKey == "layer_textures":
                return string.Join(";", terrain.Layers.Select(l => l.Texture));
            default:
                return null;
        }
    }

    private static void SetVirtual(EditorDocument doc, LevelObject obj, InspectorField field, object? value)
    {
        switch (obj.Model)
        {
            case AlpineRopeEmitter rope when AlpineVirtualFields.IsRopeKey(field.EffectiveKey):
                AlpineVirtualFields.SetRope(doc, obj, rope, field, value);
                return;
            case RoomEffect fx when AlpineVirtualFields.IsEmissionKey(field.EffectiveKey):
                AlpineVirtualFields.SetEmission(doc, fx.Header.Uid, field, value);
                return;
            case Keyframe kf when AlpineVirtualFields.IsKeyframePathKey(field.EffectiveKey):
                AlpineVirtualFields.SetKeyframePath(doc, kf.Uid, field, value);
                return;
            case AlpineTerrain when field.EffectiveKey == "layer_textures":
                // Up to 8 names separated by ';' (the first is the base): one undoable terrain edit (TerrainOps.SetLayers).
                string[] names = (value as string ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (names.Length is < 1 or > AlpineTerrainRules.MaxLayers)
                {
                    throw new InvalidOperationException($"Field 'layer_textures' takes 1 to {AlpineTerrainRules.MaxLayers} texture names separated by ';'.");
                }

                try
                {
                    Editing.TerrainOps.Apply(doc, obj, "Edit " + field.Label, t => Editing.TerrainOps.SetLayers(t, names));
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException(ex.Message, ex);
                }
                return;
        }

        if (obj.Model is Trigger t && TriggerFlagBit(field) is int bit)
        {
            int flags = t.IsPureFactionEncoded ? t.ScriptName[1] : 0;
            flags = value is true ? flags | bit : flags & ~bit;

            // Preserve the trigger's real script name that follows the flag byte; toggling a flag must
            // never discard it (behaviour reference: Alpine PutFlagsIntoScriptName, trigger.cpp:64-72).
            string baseName = t.IsPureFactionEncoded ? t.ScriptName[2..] : t.ScriptName;
            string old = t.ScriptName;
            string next = flags == 0 ? baseName : "«" + (char)flags + baseName;
            doc.EditValue(obj.Section, "Edit " + field.Label, old, next, v => t.ScriptName = v);
            return;
        }

        throw new InvalidOperationException($"Field '{field.EffectiveKey}' cannot be written.");
    }

    /// <summary>The 0xAB script-name flag bit of a trigger multiplayer field, or null.</summary>
    private static int? TriggerFlagBit(InspectorField field) => field.EffectiveKey switch
    {
        "mp_clientside" => 0x2,
        "mp_solo" => 0x4,
        "mp_solo_ignore_resets" => 0x8,
        _ => null,
    };

    private static int ToInt(object raw) => raw switch
    {
        int i => i,
        uint u => u > int.MaxValue ? int.MaxValue : (int)u,
        long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
        _ => Convert.ToInt32(raw, CultureInfo.InvariantCulture),
    };

    private static (double Min, double Max) TypeRange(Type t, double min, double max)
    {
        if (t == typeof(byte)) { return (byte.MinValue, byte.MaxValue); }
        if (t == typeof(sbyte)) { return (sbyte.MinValue, sbyte.MaxValue); }
        if (t == typeof(short)) { return (short.MinValue, short.MaxValue); }
        if (t == typeof(ushort)) { return (ushort.MinValue, ushort.MaxValue); }
        if (t == typeof(uint)) { return (0, int.MaxValue); }
        return (min, max);
    }

    /// <summary>A verbatim copy of an event's generic slots (the undo image of an event-field edit).</summary>
    private readonly record struct EventSlots(byte Bool1, byte Bool2, int Int1, int Int2, float Float1, float Float2, string Str1, string Str2)
    {
        public static EventSlots Capture(RflEvent e) => new(e.Bool1, e.Bool2, e.Int1, e.Int2, e.Float1, e.Float2, e.Str1, e.Str2);

        public void Restore(RflEvent e)
        {
            e.Bool1 = Bool1;
            e.Bool2 = Bool2;
            e.Int1 = Int1;
            e.Int2 = Int2;
            e.Float1 = Float1;
            e.Float2 = Float2;
            e.Str1 = Str1;
            e.Str2 = Str2;
        }
    }
}
