namespace Ged.Core.IO.Tex;

/// <summary>
/// A decoded 2D image in tightly-packed RGBA8 with a top-left origin
/// (row 0 is the top row; 4 bytes per pixel, R,G,B,A order). This is the
/// common currency every texture decoder produces, ready for upload or preview.
/// </summary>
public sealed class TextureImage
{
    public TextureImage(int width, int height, byte[] pixels)
    {
        if (width < 0 || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be non-negative.");
        }

        if (pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException(
                $"Pixel buffer length {pixels.Length} does not match {width}x{height} RGBA8 ({width * height * 4}).",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>RGBA8 pixels, <c>Width*Height*4</c> bytes, top-left origin.</summary>
    public byte[] Pixels { get; }

    /// <summary>Returns the RGBA of the pixel at (x, y) as a packed (r, g, b, a) tuple.</summary>
    public (byte R, byte G, byte B, byte A) GetPixel(int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }
}
