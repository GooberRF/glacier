using System.Buffers.Binary;

namespace Ged.Core.IO.Tex;

/// <summary>
/// Decoder for Volition Bitmap (.vbm) images (versions 1 and 2): 16-bit pixels in
/// one of three formats — ARGB1555, ARGB4444, or RGB565 — arranged as
/// <c>num_frames</c> frames each carrying <c>num_mipmaps + 1</c> mip levels. Every
/// frame's level-0 image is decoded to RGBA8; the animation rate (fps) and mip
/// count are surfaced as metadata.
/// </summary>
public static class VbmDecoder
{
    private const int Signature = 0x6D62762E; // ".vbm"
    private const int HeaderSize = 32;

    private const int FormatArgb1555 = 0;
    private const int FormatArgb4444 = 1;
    private const int FormatRgb565 = 2;

    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(data) == Signature;

    public static DecodedTexture Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize)
        {
            throw new TextureFormatException("VBM data too short for a 32-byte header.");
        }

        var span = data.AsSpan();
        int signature = BinaryPrimitives.ReadInt32LittleEndian(span);
        if (signature != Signature)
        {
            throw new TextureFormatException($"Bad VBM signature 0x{signature:X8}.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        if (version is not (1 or 2))
        {
            throw new TextureFormatException($"Unsupported VBM version {version} (expected 1 or 2).");
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        int format = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
        int fps = BinaryPrimitives.ReadInt32LittleEndian(span[20..]);
        int numFrames = BinaryPrimitives.ReadInt32LittleEndian(span[24..]);
        int numMipmaps = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);

        if (width <= 0 || height <= 0)
        {
            throw new TextureFormatException($"Invalid VBM dimensions {width}x{height}.");
        }

        if (format is not (FormatArgb1555 or FormatArgb4444 or FormatRgb565))
        {
            throw new TextureFormatException($"Unsupported VBM pixel format {format}.");
        }

        if (numFrames <= 0)
        {
            throw new TextureFormatException($"VBM frame count {numFrames} must be positive.");
        }

        if (numMipmaps < 0)
        {
            throw new TextureFormatException($"VBM mipmap count {numMipmaps} must be non-negative.");
        }

        int pos = HeaderSize;
        var frames = new List<TextureImage>(numFrames);
        for (int f = 0; f < numFrames; f++)
        {
            // Level 0 is the full-size image; decode it, then skip the remaining mip levels.
            int level0Bytes = checked(width * height * 2);
            if (pos + level0Bytes > data.Length)
            {
                throw new TextureFormatException($"VBM frame {f} level-0 data truncated.");
            }

            frames.Add(DecodeLevel(span.Slice(pos, level0Bytes), width, height, format));
            pos += level0Bytes;

            for (int level = 1; level <= numMipmaps; level++)
            {
                int mw = Math.Max(1, width >> level);
                int mh = Math.Max(1, height >> level);
                pos += mw * mh * 2;
            }

            if (pos > data.Length)
            {
                throw new TextureFormatException($"VBM frame {f} mip chain extends past end of data.");
            }
        }

        return new DecodedTexture(frames, numMipmaps + 1, fps, TextureFormatKind.Vbm);
    }

    private static TextureImage DecodeLevel(ReadOnlySpan<byte> src, int width, int height, int format)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            ushort p = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
            int di = i * 4;
            byte r, g, b, a;
            switch (format)
            {
                case FormatArgb1555:
                    a = (byte)((p & 0x8000) != 0 ? 255 : 0);
                    r = Expand5((p >> 10) & 0x1F);
                    g = Expand5((p >> 5) & 0x1F);
                    b = Expand5(p & 0x1F);
                    break;
                case FormatArgb4444:
                    a = Expand4((p >> 12) & 0xF);
                    r = Expand4((p >> 8) & 0xF);
                    g = Expand4((p >> 4) & 0xF);
                    b = Expand4(p & 0xF);
                    break;
                default: // FormatRgb565
                    a = 255;
                    r = Expand5((p >> 11) & 0x1F);
                    g = Expand6((p >> 5) & 0x3F);
                    b = Expand5(p & 0x1F);
                    break;
            }

            rgba[di] = r;
            rgba[di + 1] = g;
            rgba[di + 2] = b;
            rgba[di + 3] = a;
        }

        return new TextureImage(width, height, rgba);
    }

    private static byte Expand4(int v) => (byte)((v << 4) | v);

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));

    private static byte Expand6(int v) => (byte)((v << 2) | (v >> 4));
}
