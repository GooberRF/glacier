namespace Ged.Core.IO.Tex;

/// <summary>
/// Minimal Targa (.tga) encoder: uncompressed 32-bit true-color, top-left origin.
/// Used by the UV Unwrap editor's Print command to save a captured view as a .tga
/// (round-trips through <see cref="TgaDecoder"/>).
/// </summary>
public static class TgaWriter
{
    /// <summary>Encodes a top-left-origin RGBA8 image as an uncompressed 32-bit TGA.</summary>
    public static byte[] Encode(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width;
        int h = image.Height;
        var bytes = new byte[18 + (w * h * 4)];

        bytes[2] = 2;                        // uncompressed true-color
        bytes[12] = (byte)(w & 0xFF);
        bytes[13] = (byte)((w >> 8) & 0xFF);
        bytes[14] = (byte)(h & 0xFF);
        bytes[15] = (byte)((h >> 8) & 0xFF);
        bytes[16] = 32;                      // bits per pixel (BGRA)
        bytes[17] = 0x28;                    // top-left origin (0x20) + 8 alpha bits (0x08)

        int di = 18;
        byte[] px = image.Pixels;
        for (int i = 0; i < w * h; i++)
        {
            int si = i * 4;
            bytes[di++] = px[si + 2]; // B
            bytes[di++] = px[si + 1]; // G
            bytes[di++] = px[si];     // R
            bytes[di++] = px[si + 3]; // A
        }

        return bytes;
    }
}
