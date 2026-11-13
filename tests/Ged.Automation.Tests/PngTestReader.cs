using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Ged.Automation.Tests;

/// <summary>A minimal PNG reader for the render tests: 8-bit RGB/RGBA, non-interlaced, all five filter types.</summary>
internal static class PngTestReader
{
    public static (int Width, int Height, byte[] Rgba) Read(string path) => Decode(File.ReadAllBytes(path));

    public static (int Width, int Height, byte[] Rgba) Decode(byte[] png)
    {
        int pos = 8;
        int width = 0, height = 0, colorType = 0;
        var idat = new MemoryStream();
        while (pos < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            ReadOnlySpan<byte> data = png.AsSpan(pos + 8, len);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (data[8] != 8 || data[12] != 0)
                {
                    throw new NotSupportedException("Only 8-bit non-interlaced PNGs.");
                }

                colorType = data[9];
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            else if (type == "IEND")
            {
                break;
            }

            pos += 12 + len;
        }

        int bpp = colorType switch
        {
            6 => 4,
            2 => 3,
            _ => throw new NotSupportedException($"PNG colour type {colorType}."),
        };

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        byte[] r = raw.ToArray();
        int stride = width * bpp;
        var cur = new byte[stride];
        var prev = new byte[stride];
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int f = r[y * (stride + 1)];
            Array.Copy(r, (y * (stride + 1)) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0;
                int b = prev[i];
                int c = i >= bpp ? prev[i - bpp] : 0;
                cur[i] = f switch
                {
                    0 => cur[i],
                    1 => (byte)(cur[i] + a),
                    2 => (byte)(cur[i] + b),
                    3 => (byte)(cur[i] + ((a + b) / 2)),
                    4 => (byte)(cur[i] + Paeth(a, b, c)),
                    _ => throw new NotSupportedException($"PNG filter {f}."),
                };
            }

            for (int x = 0; x < width; x++)
            {
                int o = ((y * width) + x) * 4;
                rgba[o] = cur[x * bpp];
                rgba[o + 1] = cur[(x * bpp) + 1];
                rgba[o + 2] = cur[(x * bpp) + 2];
                rgba[o + 3] = bpp == 4 ? cur[(x * bpp) + 3] : (byte)255;
            }

            (prev, cur) = (cur, prev);
        }

        return (width, height, rgba);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
