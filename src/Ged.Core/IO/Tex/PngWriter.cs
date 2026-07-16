using System.Buffers.Binary;
using System.IO.Compression;

namespace Ged.Core.IO.Tex;

/// <summary>
/// Minimal PNG encoder for RGBA8 images (8-bit, colour type 6). Uses the BCL
/// <see cref="ZLibStream"/> for the IDAT deflate stream, so no image-writing
/// dependency is needed. Intended for the thumbnail cache.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static byte[] Encode(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Encode(image.Width, image.Height, image.Pixels);
    }

    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Pixel buffer size does not match dimensions.", nameof(rgba));
        }

        using var ms = new MemoryStream();
        ms.Write(Signature, 0, Signature.Length);

        // IHDR
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: truecolour + alpha
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(ms, "IHDR", ihdr);

        // IDAT: filtered scanlines (filter 0 = none) compressed as a zlib stream.
        byte[] raw = BuildRawScanlines(width, height, rgba);
        byte[] compressed = Deflate(raw);
        WriteChunk(ms, "IDAT", compressed);

        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] BuildRawScanlines(int width, int height, byte[] rgba)
    {
        int stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (stride + 1);
            raw[dst] = 0; // filter type: none
            Array.Copy(rgba, y * stride, raw, dst + 1, stride);
        }

        return raw;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        stream.Write(len);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        stream.Write(data, 0, data.Length);

        uint crc = Crc32.OfChunk(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        /// <summary>CRC-32 of a PNG chunk: the type bytes followed by the data.</summary>
        public static uint OfChunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            c = Update(c, type);
            c = Update(c, data);
            return c ^ 0xFFFFFFFFu;
        }

        private static uint Update(uint c, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            }

            return c;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
