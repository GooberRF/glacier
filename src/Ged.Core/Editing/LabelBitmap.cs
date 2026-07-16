using System;
using System.Collections.Generic;

namespace Ged.Core.Editing;

/// <summary>
/// A tiny CPU 5×7 bitmap-font rasterizer for the measurement/annotation label textures
/// (feature 4 / B7): renders a string into a small RGBA bitmap (white glyphs on a
/// semi-opaque dark plate) with no external dependency. Deterministic for a given string
/// so results cache cleanly per label. Lowercase maps to uppercase; unknown glyphs render
/// as blank cells.
/// </summary>
public static class LabelBitmap
{
    /// <summary>Glyph cell width in font pixels.</summary>
    public const int GlyphWidth = 5;

    /// <summary>Glyph cell height in font pixels.</summary>
    public const int GlyphHeight = 7;

    /// <summary>
    /// Rasterizes <paramref name="text"/> to an RGBA bitmap. <paramref name="scale"/> is
    /// the integer pixel scale; <paramref name="pad"/> the transparent-plate border in
    /// scaled pixels. Empty text yields a 1×1 transparent pixel.
    /// </summary>
    public static (int Width, int Height, byte[] Rgba) Render(string text, int scale = 2, int pad = 2)
    {
        ArgumentNullException.ThrowIfNull(text);
        scale = Math.Max(1, scale);
        pad = Math.Max(0, pad);
        if (text.Length == 0)
        {
            return (1, 1, new byte[4]);
        }

        int cols = (text.Length * GlyphWidth) + (text.Length - 1); // 1-col spacing
        int rows = GlyphHeight;
        int w = (cols * scale) + (pad * 2);
        int h = (rows * scale) + (pad * 2);
        var rgba = new byte[w * h * 4];

        // Semi-opaque dark plate so the label reads over any surface.
        for (int i = 0; i < w * h; i++)
        {
            rgba[(i * 4) + 3] = 170;
        }

        for (int gi = 0; gi < text.Length; gi++)
        {
            byte[] glyph = GlyphFor(text[gi]);
            int cellCol = gi * (GlyphWidth + 1);
            for (int r = 0; r < GlyphHeight; r++)
            {
                int bits = glyph[r];
                for (int c = 0; c < GlyphWidth; c++)
                {
                    if ((bits & (1 << (GlyphWidth - 1 - c))) == 0)
                    {
                        continue;
                    }

                    int px0 = pad + ((cellCol + c) * scale);
                    int py0 = pad + (r * scale);
                    for (int sy = 0; sy < scale; sy++)
                    {
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int o = (((py0 + sy) * w) + (px0 + sx)) * 4;
                            rgba[o] = 255; rgba[o + 1] = 255; rgba[o + 2] = 255; rgba[o + 3] = 255;
                        }
                    }
                }
            }
        }

        return (w, h, rgba);
    }

    private static byte[] GlyphFor(char ch)
    {
        char c = char.ToUpperInvariant(ch);
        c = c switch { '×' => 'X', _ => c };
        return Font.TryGetValue(c, out byte[]? g) ? g : Blank;
    }

    private static readonly byte[] Blank = new byte[GlyphHeight];

    // Row-major 5×7 glyphs (each row's low 5 bits, MSB = leftmost column).
    private static readonly Dictionary<char, byte[]> Font = new()
    {
        [' '] = new byte[] { 0, 0, 0, 0, 0, 0, 0 },
        ['.'] = new byte[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100 },
        ['-'] = new byte[] { 0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000 },
        [':'] = new byte[] { 0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b01100, 0b00000 },
        ['/'] = new byte[] { 0b00001, 0b00010, 0b00100, 0b00100, 0b01000, 0b10000, 0b10000 },
        ['°'] = new byte[] { 0b01100, 0b10010, 0b10010, 0b01100, 0b00000, 0b00000, 0b00000 },
        ['%'] = new byte[] { 0b11000, 0b11001, 0b00010, 0b00100, 0b01000, 0b10011, 0b00011 },
        ['0'] = new byte[] { 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110 },
        ['1'] = new byte[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['2'] = new byte[] { 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111 },
        ['3'] = new byte[] { 0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110 },
        ['4'] = new byte[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010 },
        ['5'] = new byte[] { 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110 },
        ['6'] = new byte[] { 0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110 },
        ['7'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000 },
        ['8'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110 },
        ['9'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100 },
        ['A'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['B'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110 },
        ['C'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110 },
        ['D'] = new byte[] { 0b11100, 0b10010, 0b10001, 0b10001, 0b10001, 0b10010, 0b11100 },
        ['E'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111 },
        ['F'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['G'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111 },
        ['H'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['I'] = new byte[] { 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['J'] = new byte[] { 0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100 },
        ['K'] = new byte[] { 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001 },
        ['L'] = new byte[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111 },
        ['M'] = new byte[] { 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001 },
        ['N'] = new byte[] { 0b10001, 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001 },
        ['O'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['P'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['Q'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101 },
        ['R'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 },
        ['S'] = new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 },
        ['T'] = new byte[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['U'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['V'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100 },
        ['W'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010 },
        ['X'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001 },
        ['Y'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['Z'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111 },
    };
}
