namespace Ged.Core.IO.Tex;

/// <summary>
/// Decoder for Targa (.tga) images as used by Red Faction: uncompressed and
/// RLE true-color (24/32-bit, image types 2 and 10) and 8-bit greyscale
/// (image types 3 and 11). Output is normalised to top-left-origin RGBA8.
/// </summary>
public static class TgaDecoder
{
    /// <summary>Returns true if <paramref name="data"/> plausibly begins with a supported TGA header.</summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
        {
            return false;
        }

        byte imageType = data[2];
        byte depth = data[16];
        return imageType is 2 or 3 or 10 or 11
            && (depth == 8 || depth == 24 || depth == 32);
    }

    public static DecodedTexture Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 18)
        {
            throw new TextureFormatException("TGA data too short for an 18-byte header.");
        }

        int idLength = data[0];
        int colorMapType = data[1];
        int imageType = data[2];
        int colorMapLength = data[5] | (data[6] << 8);
        int colorMapEntrySize = data[7];
        int width = data[12] | (data[13] << 8);
        int height = data[14] | (data[15] << 8);
        int depth = data[16];
        int descriptor = data[17];

        bool topOrigin = (descriptor & 0x20) != 0;
        bool rightToLeft = (descriptor & 0x10) != 0;

        if (imageType is not (2 or 3 or 10 or 11))
        {
            throw new TextureFormatException(
                $"Unsupported TGA image type {imageType} (supported: 2, 3, 10, 11).");
        }

        bool greyscale = imageType is 3 or 11;
        bool rle = imageType is 10 or 11;

        if (greyscale && depth != 8)
        {
            throw new TextureFormatException($"Greyscale TGA must be 8-bit, got {depth}-bit.");
        }

        if (!greyscale && depth is not (24 or 32))
        {
            throw new TextureFormatException($"True-color TGA must be 24- or 32-bit, got {depth}-bit.");
        }

        int bytesPerPixel = depth / 8;
        int pos = 18 + idLength;

        // Skip a color map if one is present (unused by the supported RF image types).
        if (colorMapType == 1)
        {
            pos += colorMapLength * ((colorMapEntrySize + 7) / 8);
        }

        if (pos > data.Length)
        {
            throw new TextureFormatException("TGA header/color-map extends past end of data.");
        }

        int pixelCount = checked(width * height);
        // Raw pixel bytes in the file's native channel order (BGR/BGRA/grey), one row at a time.
        var raw = new byte[pixelCount * bytesPerPixel];
        if (rle)
        {
            DecodeRle(data, pos, raw, bytesPerPixel);
        }
        else
        {
            if (pos + raw.Length > data.Length)
            {
                throw new TextureFormatException("TGA pixel data truncated.");
            }

            Array.Copy(data, pos, raw, 0, raw.Length);
        }

        var rgba = new byte[pixelCount * 4];
        for (int y = 0; y < height; y++)
        {
            // TGA stores rows bottom-to-top unless the top-origin bit is set.
            int srcRow = topOrigin ? y : (height - 1 - y);
            for (int x = 0; x < width; x++)
            {
                int srcX = rightToLeft ? (width - 1 - x) : x;
                int si = ((srcRow * width) + srcX) * bytesPerPixel;
                int di = ((y * width) + x) * 4;

                if (greyscale)
                {
                    byte g = raw[si];
                    rgba[di] = g;
                    rgba[di + 1] = g;
                    rgba[di + 2] = g;
                    rgba[di + 3] = 255;
                }
                else
                {
                    // File order is BGR(A).
                    rgba[di] = raw[si + 2];
                    rgba[di + 1] = raw[si + 1];
                    rgba[di + 2] = raw[si];
                    rgba[di + 3] = bytesPerPixel == 4 ? raw[si + 3] : (byte)255;
                }
            }
        }

        return new DecodedTexture(new TextureImage(width, height, rgba), TextureFormatKind.Tga);
    }

    private static void DecodeRle(byte[] data, int pos, byte[] dst, int bytesPerPixel)
    {
        int di = 0;
        while (di < dst.Length)
        {
            if (pos >= data.Length)
            {
                throw new TextureFormatException("TGA RLE stream ended prematurely.");
            }

            int packet = data[pos++];
            int count = (packet & 0x7F) + 1;
            int span = count * bytesPerPixel;
            if (di + span > dst.Length)
            {
                throw new TextureFormatException("TGA RLE packet overruns the image.");
            }

            if ((packet & 0x80) != 0)
            {
                // Run-length packet: one pixel repeated `count` times.
                if (pos + bytesPerPixel > data.Length)
                {
                    throw new TextureFormatException("TGA RLE run pixel truncated.");
                }

                for (int i = 0; i < count; i++)
                {
                    Array.Copy(data, pos, dst, di, bytesPerPixel);
                    di += bytesPerPixel;
                }

                pos += bytesPerPixel;
            }
            else
            {
                // Raw packet: `count` literal pixels.
                if (pos + span > data.Length)
                {
                    throw new TextureFormatException("TGA RAW packet truncated.");
                }

                Array.Copy(data, pos, dst, di, span);
                di += span;
                pos += span;
            }
        }
    }
}
