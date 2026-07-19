using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Ged.App.Dialogs;

/// <summary>
/// Per-face identification for the UV Unwrap editor when several faces are loaded at once: a
/// colourblind-safe outline/label colour per face and the status-bar readout text. The palette is
/// the Okabe–Ito qualitative set — the same family the manipulator axis triad (settings
/// ColorAxisX/Y/Z) draws from — so the editor keeps one colour language. Pure and window-free so
/// the colour cycling and readout formatting are unit-testable.
/// </summary>
internal static class UvFaceIdentity
{
    /// <summary>
    /// The Okabe–Ito qualitative palette. The eighth entry swaps Okabe–Ito's black (invisible on the
    /// dark canvas) for a light neutral grey, which stays CVD-distinguishable. Faces past eight cycle.
    /// </summary>
    internal static readonly Color[] Palette =
    {
        Color.FromRgb(0xE6, 0x9F, 0x00), // orange
        Color.FromRgb(0x56, 0xB4, 0xE9), // sky blue   (= axis Z)
        Color.FromRgb(0x00, 0x9E, 0x73), // bluish green (= axis Y)
        Color.FromRgb(0xF0, 0xE4, 0x42), // yellow
        Color.FromRgb(0x00, 0x72, 0xB2), // blue
        Color.FromRgb(0xD5, 0x5E, 0x00), // vermillion (= axis X)
        Color.FromRgb(0xCC, 0x79, 0xA7), // reddish purple
        Color.FromRgb(0xAE, 0xB6, 0xBF), // light grey (dark-bg stand-in for Okabe–Ito black)
    };

    /// <summary>
    /// The outline / vertex / label colour for a loaded face. With a single loaded face (or an
    /// unresolved index) the caller's chosen line colour governs and is the fallback; with multiple
    /// faces the palette distinguishes each one, cycling once past its length.
    /// </summary>
    internal static Color FaceColor(int faceIndex, int faceCount, Color single) =>
        faceCount <= 1 || faceIndex < 0 ? single : Palette[faceIndex % Palette.Length];

    /// <summary>The status readout for one loaded face: <c>Face N: brush U face F — texture</c>.</summary>
    internal static string Readout(int faceIndex, int brushUid, int faceInBrush, string? texture) =>
        $"Face {faceIndex + 1}: brush {brushUid} face {faceInBrush} — {texture ?? "(no texture)"}";

    /// <summary>
    /// A one-line texture summary for a multi-face selection, surfacing a MIXED-texture set (the
    /// canvas backdrop only ever shows the first face's texture, so the readout must make a mix visible).
    /// </summary>
    internal static string TextureSummary(IEnumerable<string?> textures)
    {
        List<string> names = textures.Select(t => t ?? "(none)").Distinct().ToList();
        return names.Count <= 1 ? (names.Count == 1 ? names[0] : string.Empty) : "mixed: " + string.Join(", ", names);
    }
}
