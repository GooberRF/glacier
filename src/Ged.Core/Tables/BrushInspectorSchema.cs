using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Tables;

/// <summary>
/// The brush inspector field registry: Properties-panel parity with the Brush-mode
/// tool panel. Flag bits and life resolve directly on the <see cref="Brush"/> model
/// through the shared <see cref="InspectorField"/> reflection/mask machinery (the
/// same metadata the object inspectors use); the Alpine breakable material /
/// no-debris pair, the lock state and the read-only UID / time-index rows are
/// <see cref="InspectorField.Virtual"/> — the panel supplies their accessors
/// (alpine_level_properties entries, State mapping, BrushEditor.TimeIndex).
/// </summary>
public static class BrushInspectorCatalog
{
    /// <summary>Alpine breakable material names (byte values 0-5 in the entry's low bits).</summary>
    public static readonly IReadOnlyList<string> Materials = new[] { "Glass", "Rock", "Wood", "Metal", "Cement", "Ice" };

    /// <summary>Every brush property the Properties panel exposes, in display order.</summary>
    public static IReadOnlyList<InspectorField> Fields { get; } = new[]
    {
        new InspectorField("Kind", "Flags", InspectorEditor.Enum)
        {
            Mask = (uint)BrushFlags.Air, Shift = 1, Options = new[] { "Solid", "Air" },
            Note = "Solid adds, Air carves (world starts solid).",
        },
        new InspectorField("Is Portal", "Flags", InspectorEditor.Bool) { Mask = (uint)BrushFlags.Portal },
        new InspectorField("Is Detail", "Flags", InspectorEditor.Bool) { Mask = (uint)BrushFlags.Detail },
        new InspectorField("Emits Steam", "Flags", InspectorEditor.Bool) { Mask = (uint)BrushFlags.EmitsSteam },
        new InspectorField("Is Geoable", "Flags", InspectorEditor.Bool)
        {
            Mask = (uint)BrushFlags.Geoable, Note = "[ALPINE] A geoable brush is always also detail.",
        },
        new InspectorField("Life", "Life", InspectorEditor.Int)
        {
            Note = "-1 = infinite. Finite life on a detail brush compiles as breakable.",
        },
        new InspectorField("Locked", "State", InspectorEditor.Bool)
        {
            Virtual = true, Note = "Brush lock state (Q / Shift+Q).",
        },
        new InspectorField("Material", "Uid", InspectorEditor.Enum)
        {
            Virtual = true, Options = Materials,
            Note = "[ALPINE] Breakable material (alpine_level_properties, bits 0-6).",
        },
        new InspectorField("No Debris", "Uid", InspectorEditor.Bool)
        {
            Virtual = true, Note = "[ALPINE] Breakable no-debris flag (material byte bit 7).",
        },
        new InspectorField("UID", "Uid", InspectorEditor.Int) { Virtual = true, Note = "Read-only." },
        new InspectorField("Time Index", "Uid", InspectorEditor.Int)
        {
            Virtual = true, Note = "Read-only CSG time order (Start/End of Time reorders).",
        },
    };

    /// <summary>
    /// The flag implication the cookie-cutter applies (<c>BrushCreateParams.ToFlags</c>):
    /// a geoable brush is always also a detail brush. Apply after any flag edit.
    /// </summary>
    public static uint Normalize(uint flags) =>
        (flags & (uint)BrushFlags.Geoable) != 0 ? flags | (uint)BrushFlags.Detail : flags;
}
