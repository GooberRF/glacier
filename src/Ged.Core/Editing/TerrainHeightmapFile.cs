using System.Buffers.Binary;
using System.IO.Compression;
using Ged.Core.Model;
using StbImageSharp;

namespace Ged.Core.Editing;

/// <summary>
/// Heightmap files for terrains, as Alpine RED reads and writes them (editor_patch/terrain.cpp:907-1080): a greyscale image
/// (PNG 8 or 16 bit, or any image format the loader knows, read as 8 bit and widened) or a headerless little-endian
/// RAW16 (<c>.r16</c> / <c>.raw</c>, square, the size inferred from the length). Image row 0 is the terrain's +Z edge.
/// Edges 2..4097. Export writes a 16-bit greyscale PNG, or RAW16 for those extensions.
/// </summary>
public static class TerrainHeightmapFile
{
    /// <summary>The largest image edge accepted (terrain.cpp:925-927).</summary>
    public const int MaxImageEdge = 4097;

    private const long MaxFileBytes = 256L * 1024 * 1024;

    public static bool IsRaw16(string path) =>
        path.EndsWith(".r16", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".raw", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a heightmap as 16-bit samples, row 0 first.</summary>
    public static (ushort[] Samples, int Width, int Height) Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes)
        {
            throw new IOException($"The heightmap '{path}' could not be read.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (IsRaw16(path))
        {
            if (bytes.Length % 2 != 0)
            {
                throw new InvalidDataException("A RAW16 file must hold whole 16-bit samples.");
            }

            int n = bytes.Length / 2;
            int side = (int)Math.Round(Math.Sqrt(n));
            if (side * side != n || side < AlpineTerrainRules.MinVerts || side > MaxImageEdge)
            {
                throw new InvalidDataException($"A RAW16 heightmap must be square ({AlpineTerrainRules.MinVerts}x{AlpineTerrainRules.MinVerts} to {MaxImageEdge}x{MaxImageEdge}).");
            }

            var raw = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                raw[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
            }

            return (raw, side, side);
        }

        (ushort[] Samples, int Width, int Height)? png = TryReadGreyPng(bytes);
        (ushort[] samples, int w, int h) = png ?? ReadAny(bytes);
        if (w < AlpineTerrainRules.MinVerts || h < AlpineTerrainRules.MinVerts || w > MaxImageEdge || h > MaxImageEdge)
        {
            throw new InvalidDataException($"The image must be between {AlpineTerrainRules.MinVerts}x{AlpineTerrainRules.MinVerts} and {MaxImageEdge}x{MaxImageEdge} pixels.");
        }

        return (samples, w, h);
    }

    /// <summary>Writes the terrain's heights: a 16-bit greyscale PNG, or RAW16 for <c>.r16</c> / <c>.raw</c> (terrain.cpp:1022-1080).</summary>
    public static void Write(string path, AlpineTerrainGrid g)
    {
        int w = g.Nx, h = g.Nz;
        if (IsRaw16(path))
        {
            var raw = new byte[w * h * 2];
            int at = 0;
            for (int r = 0; r < h; r++)
            {
                int z = AlpineTerrainRules.ImageRow(r, h);
                for (int x = 0; x < w; x++, at += 2)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at, 2), g.Height(x, z));
                }
            }

            File.WriteAllBytes(path, raw);
            return;
        }

        var scan = new byte[(w * 2 + 1) * h];
        int p = 0;
        for (int r = 0; r < h; r++)
        {
            int z = AlpineTerrainRules.ImageRow(r, h);
            scan[p++] = 0;
            for (int x = 0; x < w; x++)
            {
                ushort v = g.Height(x, z);
                scan[p++] = (byte)(v >> 8);
                scan[p++] = (byte)v;
            }
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 16; // bit depth
        ihdr[9] = 0;  // greyscale
        Chunk(ms, "IHDR", ihdr);
        using (var idat = new MemoryStream())
        {
            using (var z = new ZLibStream(idat, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                z.Write(scan);
            }

            Chunk(ms, "IDAT", idat.ToArray());
        }

        Chunk(ms, "IEND", Array.Empty<byte>());
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFFu);
        crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc;
    }

    /// <summary>A non-interlaced greyscale PNG (8 or 16 bit) decoded at full precision, or null for anything else.</summary>
    private static (ushort[], int, int)? TryReadGreyPng(byte[] b)
    {
        if (b.Length < 33 || b[0] != 0x89 || b[1] != 'P' || b[2] != 'N' || b[3] != 'G')
        {
            return null;
        }

        int at = 8, w = 0, h = 0, depth = 0;
        using var idat = new MemoryStream();
        while (at + 8 <= b.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at, 4));
            string type = System.Text.Encoding.ASCII.GetString(b, at + 4, 4);
            if (len < 0 || at + 12 + len > b.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> data = b.AsSpan(at + 8, len);
            if (type == "IHDR")
            {
                w = BinaryPrimitives.ReadInt32BigEndian(data);
                h = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                depth = data[8];
                if (data[9] != 0 || (depth != 8 && depth != 16) || data[12] != 0)
                {
                    return null;
                }
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            else if (type == "IEND")
            {
                break;
            }

            at += 12 + len;
        }

        if (w <= 0 || h <= 0 || w > MaxImageEdge || h > MaxImageEdge)
        {
            return null;
        }

        int bpp = depth / 8;
        int stride = w * bpp;
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int got = 0;
            while (got < raw.Length)
            {
                int n = z.Read(raw, got, raw.Length - got);
                if (n == 0)
                {
                    return null;
                }

                got += n;
            }
        }

        var cur = new byte[stride];
        var prev = new byte[stride];
        var samples = new ushort[w * h];
        for (int r = 0; r < h; r++)
        {
            int filter = raw[r * (stride + 1)];
            Array.Copy(raw, (r * (stride + 1)) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int left = i >= bpp ? cur[i - bpp] : 0, up = prev[i], ul = i >= bpp ? prev[i - bpp] : 0;
                int add = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, ul),
                    _ => 0,
                };
                cur[i] = (byte)(cur[i] + add);
            }

            for (int x = 0; x < w; x++)
            {
                samples[(r * w) + x] = depth == 16 ? (ushort)((cur[x * 2] << 8) | cur[(x * 2) + 1]) : (ushort)(cur[x] * 257);
            }

            (prev, cur) = (cur, prev);
        }

        return (samples, w, h);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Any other image as 8-bit grey widened to 16 bits (v * 257), as Alpine's loader returns it.</summary>
    private static (ushort[], int, int) ReadAny(byte[] bytes)
    {
        ImageResult img;
        try
        {
            img = ImageResult.FromMemory(bytes, ColorComponents.Grey);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("The image format is not supported.", ex);
        }

        var samples = new ushort[img.Width * img.Height];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)(img.Data[i] * 257);
        }

        return (samples, img.Width, img.Height);
    }
}
