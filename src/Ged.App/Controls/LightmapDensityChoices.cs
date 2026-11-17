using System.Collections.Generic;
using System.Linq;

namespace Ged.App.Controls;

/// <summary>
/// The Lightmap density choices, as Alpine RED's Level Properties lists them (editor_patch/level.cpp:2131-2155):
/// Default (8), Off, the presets 2–128, and the level's own value marked "(custom)" when it is none of those. Shared by
/// the Level Properties dialog and the Lightmap Method flyout, which edit the same stored property.
/// </summary>
public static class LightmapDensityChoices
{
    /// <summary>The stored value of "Off" (Alpine's density_off).</summary>
    public const int Off = 255;

    private static readonly int[] Presets = { 2, 4, 8, 16, 32, 64, 128 };

    /// <summary>The standard choices: stored value and label.</summary>
    public static IReadOnlyList<(int Value, string Label)> Standard { get; } =
        new[] { (0, "Default (8)"), (Off, "Off") }.Concat(Presets.Select(p => (p, p.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToList();

    /// <summary>The choices for a level whose stored density is <paramref name="current"/> (a custom value is appended).</summary>
    public static IReadOnlyList<(int Value, string Label)> For(int current) =>
        Standard.Any(c => c.Value == current)
            ? Standard
            : Standard.Append((current, current.ToString(System.Globalization.CultureInfo.InvariantCulture) + " (custom)")).ToList();
}
