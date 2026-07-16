using System.Buffers.Binary;

namespace Ged.Core.IO.Tex;

/// <summary>
/// Decoder for DirectDraw Surface (.dds) textures: the block-compressed formats
/// BC1/DXT1, BC2/DXT2-3, and BC3/DXT4-5, plus uncompressed RGB/RGBA and 8-bit
/// luminance surfaces described by channel bit-masks. Only mip level 0 is decoded
/// to RGBA8; the source mip count is reported as metadata.
/// </summary>
/// <remarks>
/// The BCn block decoders are a compact from-scratch implementation (no external
/// codec dependency), following the publicly documented S3TC/DXT layouts.
/// </remarks>
public static class DdsDecoder
{
    private const uint Magic = 0x20534444; // "DDS "
    private const int HeaderSize = 124;
    private const int PixelDataOffset = 4 + HeaderSize;

    private const uint DdpfAlphaPixels = 0x1;
    private const uint DdpfFourCc = 0x4;
    private const uint DdpfRgb = 0x40;
    private const uint DdpfLuminance = 0x20000;

    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    public static DecodedTexture Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < PixelDataOffset)
        {
            throw new TextureFormatException("DDS data too short for magic + 124-byte header.");
        }

        var span = data.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
        {
            throw new TextureFormatException("Bad DDS magic (expected 'DDS ').");
        }

        int height = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
        int mipCount = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);

        // DDS_PIXELFORMAT begins at DDS_HEADER offset 72, i.e. file offset 4 (magic) + 72 = 76.
        // Its dwFourCC field then sits at 76 + 8 = 84, matching real DDS files.
        const int pf = 4 + 72;
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(span[(pf + 4)..]);
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(span[(pf + 8)..]);
        int rgbBitCount = BinaryPrimitives.ReadInt32LittleEndian(span[(pf + 12)..]);
        uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(span[(pf + 16)..]);
        uint gMask = BinaryPrimitives.ReadUInt32LittleEndian(span[(pf + 20)..]);
        uint bMask = BinaryPrimitives.ReadUInt32LittleEndian(span[(pf + 24)..]);
        uint aMask = BinaryPrimitives.ReadUInt32LittleEndian(span[(pf + 28)..]);

        if (width <= 0 || height <= 0)
        {
            throw new TextureFormatException($"Invalid DDS dimensions {width}x{height}.");
        }

        int pixelStart = PixelDataOffset;
        byte[] rgba;

        if ((pfFlags & DdpfFourCc) != 0)
        {
            string cc = FourCcToString(fourCc);
            if (cc == "DX10")
            {
                throw new TextureFormatException(
                    "DDS DX10 extended header is not supported; use a classic DXT1/3/5 or uncompressed DDS.");
            }

            rgba = cc switch
            {
                "DXT1" => DecodeBc1(span, pixelStart, width, height),
                "DXT2" or "DXT3" => DecodeBc2(span, pixelStart, width, height),
                "DXT4" or "DXT5" => DecodeBc3(span, pixelStart, width, height),
                _ => throw new TextureFormatException($"Unsupported DDS fourCC '{cc}'."),
            };
        }
        else if ((pfFlags & DdpfRgb) != 0)
        {
            rgba = DecodeUncompressed(span, pixelStart, width, height, rgbBitCount, rMask, gMask, bMask,
                (pfFlags & DdpfAlphaPixels) != 0 ? aMask : 0);
        }
        else if ((pfFlags & DdpfLuminance) != 0)
        {
            rgba = DecodeLuminance(span, pixelStart, width, height, rgbBitCount,
                rMask != 0 ? rMask : 0xFF, (pfFlags & DdpfAlphaPixels) != 0 ? aMask : 0);
        }
        else
        {
            throw new TextureFormatException("DDS pixel format is neither FourCC, RGB, nor luminance.");
        }

        return new DecodedTexture(
            new TextureImage(width, height, rgba), TextureFormatKind.Dds, Math.Max(1, mipCount));
    }

    private static string FourCcToString(uint fourCc) =>
        new(new[] { (char)(fourCc & 0xFF), (char)((fourCc >> 8) & 0xFF), (char)((fourCc >> 16) & 0xFF), (char)((fourCc >> 24) & 0xFF) });

    private static byte[] DecodeBc1(ReadOnlySpan<byte> data, int start, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int pos = start;
        Span<byte> block = stackalloc byte[16 * 4];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                Require(data, pos, 8, "BC1");
                DecodeBc1Color(data.Slice(pos, 8), block, allowTransparent: true);
                pos += 8;
                Blit(block, rgba, width, height, bx, by);
            }
        }

        return rgba;
    }

    private static byte[] DecodeBc2(ReadOnlySpan<byte> data, int start, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int pos = start;
        Span<byte> block = stackalloc byte[16 * 4];
        Span<byte> alpha = stackalloc byte[16];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                Require(data, pos, 16, "BC2");
                // 8 bytes explicit 4-bit alpha, then 8 bytes BC1 color (always opaque mode).
                for (int i = 0; i < 16; i++)
                {
                    int nibble = (data[pos + (i / 2)] >> ((i % 2) * 4)) & 0xF;
                    alpha[i] = (byte)((nibble << 4) | nibble);
                }

                DecodeBc1Color(data.Slice(pos + 8, 8), block, allowTransparent: false);
                for (int i = 0; i < 16; i++)
                {
                    block[(i * 4) + 3] = alpha[i];
                }

                pos += 16;
                Blit(block, rgba, width, height, bx, by);
            }
        }

        return rgba;
    }

    private static byte[] DecodeBc3(ReadOnlySpan<byte> data, int start, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int pos = start;
        Span<byte> block = stackalloc byte[16 * 4];
        Span<byte> alpha = stackalloc byte[16];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                Require(data, pos, 16, "BC3");
                DecodeBc3Alpha(data.Slice(pos, 8), alpha);
                DecodeBc1Color(data.Slice(pos + 8, 8), block, allowTransparent: false);
                for (int i = 0; i < 16; i++)
                {
                    block[(i * 4) + 3] = alpha[i];
                }

                pos += 16;
                Blit(block, rgba, width, height, bx, by);
            }
        }

        return rgba;
    }

    private static void DecodeBc1Color(ReadOnlySpan<byte> src, Span<byte> outBlock, bool allowTransparent)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(src);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(src[2..]);
        Span<byte> r = stackalloc byte[4];
        Span<byte> g = stackalloc byte[4];
        Span<byte> b = stackalloc byte[4];
        Span<byte> a = stackalloc byte[4];

        Unpack565(c0, out r[0], out g[0], out b[0]);
        Unpack565(c1, out r[1], out g[1], out b[1]);
        a[0] = a[1] = a[2] = a[3] = 255;

        if (c0 > c1 || !allowTransparent)
        {
            // 4-colour opaque mode.
            r[2] = (byte)(((2 * r[0]) + r[1]) / 3);
            g[2] = (byte)(((2 * g[0]) + g[1]) / 3);
            b[2] = (byte)(((2 * b[0]) + b[1]) / 3);
            r[3] = (byte)((r[0] + (2 * r[1])) / 3);
            g[3] = (byte)((g[0] + (2 * g[1])) / 3);
            b[3] = (byte)((b[0] + (2 * b[1])) / 3);
        }
        else
        {
            // 3-colour + transparent black mode.
            r[2] = (byte)((r[0] + r[1]) / 2);
            g[2] = (byte)((g[0] + g[1]) / 2);
            b[2] = (byte)((b[0] + b[1]) / 2);
            r[3] = g[3] = b[3] = 0;
            a[3] = 0;
        }

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
        for (int i = 0; i < 16; i++)
        {
            int idx = (int)((indices >> (i * 2)) & 0x3);
            int o = i * 4;
            outBlock[o] = r[idx];
            outBlock[o + 1] = g[idx];
            outBlock[o + 2] = b[idx];
            outBlock[o + 3] = a[idx];
        }
    }

    private static void DecodeBc3Alpha(ReadOnlySpan<byte> src, Span<byte> alpha)
    {
        Span<int> a = stackalloc int[8];
        a[0] = src[0];
        a[1] = src[1];
        if (a[0] > a[1])
        {
            for (int i = 1; i <= 5; i++)
            {
                a[i + 1] = (((6 - i) * a[0]) + (i * a[1])) / 7;
            }
        }
        else
        {
            for (int i = 1; i <= 3; i++)
            {
                a[i + 1] = (((4 - i) * a[0]) + (i * a[1])) / 5;
            }

            a[6] = 0;
            a[7] = 255;
        }

        // 16 pixels × 3-bit indices packed into the 6 bytes following the two endpoints.
        ulong bits = 0;
        for (int i = 0; i < 6; i++)
        {
            bits |= (ulong)src[2 + i] << (8 * i);
        }

        for (int i = 0; i < 16; i++)
        {
            int idx = (int)((bits >> (i * 3)) & 0x7);
            alpha[i] = (byte)a[idx];
        }
    }

    private static void Blit(ReadOnlySpan<byte> block, byte[] rgba, int width, int height, int bx, int by)
    {
        for (int py = 0; py < 4; py++)
        {
            int y = (by * 4) + py;
            if (y >= height)
            {
                break;
            }

            for (int px = 0; px < 4; px++)
            {
                int x = (bx * 4) + px;
                if (x >= width)
                {
                    continue;
                }

                int s = ((py * 4) + px) * 4;
                int d = ((y * width) + x) * 4;
                rgba[d] = block[s];
                rgba[d + 1] = block[s + 1];
                rgba[d + 2] = block[s + 2];
                rgba[d + 3] = block[s + 3];
            }
        }
    }

    private static byte[] DecodeUncompressed(
        ReadOnlySpan<byte> data, int start, int width, int height, int bitCount,
        uint rMask, uint gMask, uint bMask, uint aMask)
    {
        if (bitCount is not (16 or 24 or 32))
        {
            throw new TextureFormatException($"Unsupported uncompressed DDS bit depth {bitCount}.");
        }

        int bytesPerPixel = bitCount / 8;
        int need = width * height * bytesPerPixel;
        Require(data, start, need, "uncompressed DDS");

        (int rShift, int rMax) = MaskInfo(rMask);
        (int gShift, int gMax) = MaskInfo(gMask);
        (int bShift, int bMax) = MaskInfo(bMask);
        (int aShift, int aMax) = MaskInfo(aMask);

        var rgba = new byte[width * height * 4];
        int pos = start;
        for (int i = 0; i < width * height; i++)
        {
            uint px = 0;
            for (int k = 0; k < bytesPerPixel; k++)
            {
                px |= (uint)data[pos + k] << (8 * k);
            }

            pos += bytesPerPixel;
            int d = i * 4;
            rgba[d] = Channel(px, rMask, rShift, rMax, 0);
            rgba[d + 1] = Channel(px, gMask, gShift, gMax, 0);
            rgba[d + 2] = Channel(px, bMask, bShift, bMax, 0);
            rgba[d + 3] = aMask != 0 ? Channel(px, aMask, aShift, aMax, 255) : (byte)255;
        }

        return rgba;
    }

    private static byte[] DecodeLuminance(
        ReadOnlySpan<byte> data, int start, int width, int height, int bitCount, uint lMask, uint aMask)
    {
        if (bitCount is not (8 or 16))
        {
            throw new TextureFormatException($"Unsupported luminance DDS bit depth {bitCount}.");
        }

        int bytesPerPixel = bitCount / 8;
        int need = width * height * bytesPerPixel;
        Require(data, start, need, "luminance DDS");

        (int lShift, int lMax) = MaskInfo(lMask);
        (int aShift, int aMax) = MaskInfo(aMask);

        var rgba = new byte[width * height * 4];
        int pos = start;
        for (int i = 0; i < width * height; i++)
        {
            uint px = 0;
            for (int k = 0; k < bytesPerPixel; k++)
            {
                px |= (uint)data[pos + k] << (8 * k);
            }

            pos += bytesPerPixel;
            byte l = Channel(px, lMask, lShift, lMax, 0);
            int d = i * 4;
            rgba[d] = l;
            rgba[d + 1] = l;
            rgba[d + 2] = l;
            rgba[d + 3] = aMask != 0 ? Channel(px, aMask, aShift, aMax, 255) : (byte)255;
        }

        return rgba;
    }

    private static (int Shift, int Max) MaskInfo(uint mask)
    {
        if (mask == 0)
        {
            return (0, 0);
        }

        int shift = 0;
        while ((mask & 1) == 0)
        {
            mask >>= 1;
            shift++;
        }

        return (shift, (int)mask);
    }

    private static byte Channel(uint px, uint mask, int shift, int max, byte fallback)
    {
        if (mask == 0 || max == 0)
        {
            return fallback;
        }

        int v = (int)((px & mask) >> shift);
        return (byte)(((v * 255) + (max / 2)) / max);
    }

    private static void Unpack565(ushort c, out byte r, out byte g, out byte b)
    {
        int r5 = (c >> 11) & 0x1F;
        int g6 = (c >> 5) & 0x3F;
        int b5 = c & 0x1F;
        r = (byte)((r5 << 3) | (r5 >> 2));
        g = (byte)((g6 << 2) | (g6 >> 4));
        b = (byte)((b5 << 3) | (b5 >> 2));
    }

    private static void Require(ReadOnlySpan<byte> data, int pos, int count, string what)
    {
        if (pos + count > data.Length)
        {
            throw new TextureFormatException($"{what} pixel data truncated at offset {pos}.");
        }
    }
}
