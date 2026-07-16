using System.Globalization;

namespace Ged.Core.Editing;

/// <summary>
/// The first-class grid-size / rotation-increment ladders (item 4). The top-bar pickers,
/// the status-bar popovers and the [ / ] and Shift+[ / Shift+] hotkeys all step through
/// the same ladders, and free-entry values are validated/clamped here so every snap
/// consumer (SnapPolicy, keyboard nudges, gizmo) sees one consistent value.
/// </summary>
public static class SnapIncrements
{
    /// <summary>
    /// Grid-size quick-select presets in metres shown by the pickers: powers of two from
    /// 1/32 m up to 8 m (values above 8 are too rare for the quick-select; the hotkeys
    /// still step the full <see cref="GridLadder"/> and free entry accepts any valid size).
    /// </summary>
    public static readonly IReadOnlyList<float> GridPresets =
        new[] { 0.03125f, 0.0625f, 0.125f, 0.25f, 0.5f, 1f, 2f, 4f, 8f };

    /// <summary>Full grid hotkey ladder (RED halving/doubling): 1/32 m up to 256 m.</summary>
    public static readonly IReadOnlyList<float> GridLadder =
        new[] { 0.03125f, 0.0625f, 0.125f, 0.25f, 0.5f, 1f, 2f, 4f, 8f, 16f, 32f, 64f, 128f, 256f };

    /// <summary>Rotation-increment presets in degrees (quick-select and hotkey ladder).</summary>
    public static readonly IReadOnlyList<float> RotationPresets = new[] { 1f, 5f, 15f, 30f, 45f, 90f };

    public const float GridMin = 0.01f;

    public const float GridMax = 256f;

    public const float RotationMin = 1f;

    public const float RotationMax = 180f;

    /// <summary>
    /// The next ladder value above <paramref name="current"/>, or <paramref name="current"/>
    /// unchanged when it already sits at/above the top. Off-ladder (free-entry) values step
    /// to their nearest ladder neighbour in the requested direction.
    /// </summary>
    public static float StepUp(IReadOnlyList<float> ladder, float current)
    {
        foreach (float preset in ladder)
        {
            if (preset > current && !NearlyEqual(preset, current))
            {
                return preset;
            }
        }

        return current;
    }

    /// <summary>
    /// The next ladder value below <paramref name="current"/>, or <paramref name="current"/>
    /// unchanged when it already sits at/below the bottom.
    /// </summary>
    public static float StepDown(IReadOnlyList<float> ladder, float current)
    {
        for (int i = ladder.Count - 1; i >= 0; i--)
        {
            if (ladder[i] < current && !NearlyEqual(ladder[i], current))
            {
                return ladder[i];
            }
        }

        return current;
    }

    /// <summary>Free-entry grid size: a finite positive number, clamped to [0.01, 256] m.</summary>
    public static bool TryParseGrid(string? text, out float value) =>
        TryParse(text, GridMin, GridMax, out value);

    /// <summary>Free-entry rotation increment: a finite positive number, clamped to [1, 180]°.</summary>
    public static bool TryParseRotation(string? text, out float value) =>
        TryParse(text, RotationMin, RotationMax, out value);

    private static bool TryParse(string? text, float min, float max, out float value)
    {
        value = 0f;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            && !float.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return false;
        }

        if (!float.IsFinite(parsed) || parsed <= 0f)
        {
            return false;
        }

        value = Math.Clamp(parsed, min, max);
        return true;
    }

    private static bool NearlyEqual(float a, float b) =>
        MathF.Abs(a - b) <= MathF.Max(MathF.Abs(a), MathF.Abs(b)) * 1e-4f;
}
