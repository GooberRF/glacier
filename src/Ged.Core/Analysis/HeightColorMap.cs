using System;

namespace Ged.Core.Analysis;

/// <summary>
/// The floor-height colour map of the heights overlay (<c>glacier-cli render --overlays heights</c> / <c>--mode heights</c>)
/// and its inverse (Lua <c>ref.heights</c>). A height y in the map's range [min, max] is t = (y − min) / (max − min), drawn
/// as the fully saturated HSV colour of hue 240° × (1 − t): blue (low) → cyan → green → yellow → red (high). Hue alone
/// carries the height, so the map survives what the overlay does to a pixel: blending it with a grey (the lit texture's
/// luminance), darkening it (contour lines, floors seen through an opening) or lightening it (stair tread lines) all keep
/// the hue. Drop edges are drawn in a violet outside the map's hues, so a decoder tells them from floor.
/// </summary>
public static class HeightColorMap
{
    /// <summary>The hue (degrees) of the lowest height (t = 0).</summary>
    public const double HueLow = 240;

    /// <summary>The smallest saturation a decoded floor pixel has (overlay pixels are at least about 0.33).</summary>
    public const double MinSaturation = 0.28;

    /// <summary>The smallest value (brightness) a decoded floor pixel has.</summary>
    public const double MinValue = 0.12;

    /// <summary>The colour drop edges are drawn in (violet, hue 280°: outside the map's 0-240° hues).</summary>
    public static readonly (byte R, byte G, byte B) Drop = (170, 0, 255);

    /// <summary>The colour of t (clamped to 0-1).</summary>
    public static (byte R, byte G, byte B) Color(double t)
    {
        t = double.IsFinite(t) ? Math.Clamp(t, 0, 1) : 0;
        double h = HueLow * (1 - t) / 60.0;
        int sector = Math.Min(3, (int)Math.Floor(h));
        double f = h - sector;
        byte up = (byte)Math.Round(255 * f), down = (byte)Math.Round(255 * (1 - f));
        return sector switch
        {
            0 => (255, up, 0),     // red → yellow
            1 => (down, 255, 0),   // yellow → green
            2 => (0, 255, up),     // green → cyan
            _ => (0, h >= 4 ? (byte)0 : down, 255), // cyan → blue
        };
    }

    /// <summary>The colour of height <paramref name="y"/> in the range [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static (byte R, byte G, byte B) ColorOf(double y, double min, double max) => Color(T(y, min, max));

    /// <summary>t of height <paramref name="y"/> in the range.</summary>
    public static double T(double y, double min, double max) => max > min ? (y - min) / (max - min) : 0.5;

    /// <summary>The height of t in the range.</summary>
    public static double Y(double t, double min, double max) => min + (t * (max - min));

    /// <summary>The HSV hue (degrees, 0-360), saturation and value (0-1) of a colour.</summary>
    public static (double H, double S, double V) Hsv(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double v = max / 255.0;
        if (max == 0 || max == min)
        {
            return (0, 0, v);
        }

        double d = max - min;
        double h = max == r ? (g - b) / d : max == g ? 2 + ((b - r) / d) : 4 + ((r - g) / d);
        h *= 60;
        if (h < 0)
        {
            h += 360;
        }

        return (h, d / max, v);
    }

    /// <summary>
    /// t of a pixel drawn with the map, or null when the pixel is not floor colour: too grey or dark, or a hue outside the
    /// map (the drop violet, magenta). Tolerant of the overlay's blending (see the class summary).
    /// </summary>
    public static double? TOf(byte r, byte g, byte b)
    {
        (double h, double s, double v) = Hsv(r, g, b);
        if (s < MinSaturation || v < MinValue)
        {
            return null;
        }

        if (h <= HueLow)
        {
            return 1 - (h / HueLow);
        }

        if (h < 255)
        {
            return 0; // a blue just past the map's end (rounding)
        }

        return h >= 335 ? 1 : null; // a red just past the wrap, else violet / magenta: not floor
    }

    /// <summary>True when a pixel is drawn in the drop-edge violet.</summary>
    public static bool IsDrop(byte r, byte g, byte b)
    {
        (double h, double s, double v) = Hsv(r, g, b);
        return s >= 0.75 && v >= 0.55 && h >= 262 && h <= 300;
    }
}
