using System.Linq;
using Ged.Core.Editing;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Feature 4 (B7) label rasterizer: the CPU 5×7 font produces a non-empty, deterministic
/// RGBA bitmap for a distance string (used to texture the dimension label billboard,
/// cached per string).
/// </summary>
public sealed class LabelBitmapTests
{
    private static bool HasWhitePixel(byte[] rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i] == 255 && rgba[i + 1] == 255 && rgba[i + 2] == 255 && rgba[i + 3] == 255)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Renders_A_Non_Empty_Bitmap_With_Glyph_Pixels()
    {
        (int w, int h, byte[] rgba) = LabelBitmap.Render("12.34 M");
        Assert.True(w > 0 && h > 0);
        Assert.Equal(w * h * 4, rgba.Length);
        Assert.True(HasWhitePixel(rgba), "the rasterized label must contain lit glyph pixels");
    }

    [Fact]
    public void Is_Deterministic_For_A_Given_String()
    {
        (int w1, int h1, byte[] a) = LabelBitmap.Render("3.50 M");
        (int w2, int h2, byte[] b) = LabelBitmap.Render("3.50 M");
        Assert.Equal(w1, w2);
        Assert.Equal(h1, h2);
        Assert.True(a.SequenceEqual(b), "same string must rasterize to identical bytes (cacheable)");
    }

    [Fact]
    public void Different_Strings_Produce_Different_Bitmaps()
    {
        (_, _, byte[] a) = LabelBitmap.Render("1 M");
        (_, _, byte[] b) = LabelBitmap.Render("8 M");
        Assert.False(a.SequenceEqual(b));
    }

    [Fact]
    public void Wider_Text_Is_Wider()
    {
        (int wShort, _, _) = LabelBitmap.Render("1 M");
        (int wLong, _, _) = LabelBitmap.Render("123.45 M");
        Assert.True(wLong > wShort);
    }

    [Fact]
    public void Empty_String_Is_A_Single_Transparent_Pixel()
    {
        (int w, int h, byte[] rgba) = LabelBitmap.Render(string.Empty);
        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Equal(4, rgba.Length);
        Assert.Equal(0, rgba[3]); // transparent
    }
}
