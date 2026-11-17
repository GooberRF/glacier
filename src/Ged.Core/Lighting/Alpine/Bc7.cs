using System;
using System.Threading.Tasks;

namespace Ged.Core.Lighting.Alpine;

/// <summary>
/// The constants of the BC7 (BPTC, unorm) block format, as the format specification publishes them (the Khronos Data
/// Format Specification's BPTC section and the Direct3D 11 BC7 format reference): the per-mode field widths, the
/// interpolation weights and the 2- and 3-subset partition and anchor tables. Glacier's own clean-room codec
/// (<see cref="Bc7Decoder"/>, <see cref="Bc7Encoder"/>); no third-party encoder code is used.
/// </summary>
internal static class Bc7Tables
{
    /// <summary>Per mode: subsets, partition bits, rotation bits, index-selection bits, colour bits, alpha bits,
    /// per-endpoint p-bits, shared p-bits, index bits, secondary index bits.</summary>
    public static readonly (int Ns, int Pb, int Rb, int Isb, int Cb, int Ab, int Epb, int Spb, int Ib, int Ib2)[] Modes =
    {
        (3, 4, 0, 0, 4, 0, 1, 0, 3, 0),
        (2, 6, 0, 0, 6, 0, 0, 1, 3, 0),
        (3, 6, 0, 0, 5, 0, 0, 0, 2, 0),
        (2, 6, 0, 0, 7, 0, 1, 0, 2, 0),
        (1, 0, 2, 1, 5, 6, 0, 0, 2, 3),
        (1, 0, 2, 0, 7, 8, 0, 0, 2, 2),
        (1, 0, 0, 0, 7, 7, 1, 0, 4, 0),
        (2, 6, 0, 0, 5, 5, 1, 0, 2, 0),
    };

    public static readonly int[] Weights2 = { 0, 21, 43, 64 };
    public static readonly int[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    public static readonly int[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    public static int[] WeightsFor(int bits) => bits switch { 2 => Weights2, 3 => Weights3, _ => Weights4 };

    /// <summary>Two-subset partitions: bit i set = pixel i (row major) is in subset 1.</summary>
    public static readonly ushort[] Partition2 =
    {
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80,
        0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE,
        0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A,
        0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C,
        0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    };

    /// <summary>The anchor pixel of subset 1 in each two-subset partition.</summary>
    public static readonly byte[] Anchor2 =
    {
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
        6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    };

    /// <summary>Three-subset partitions, the subset of each pixel (row major).</summary>
    public static readonly byte[][] Partition3 =
    {
        new byte[] { 0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 1, 2, 2, 2, 2 },
        new byte[] { 0, 0, 0, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 2, 1 },
        new byte[] { 0, 0, 0, 0, 2, 0, 0, 1, 2, 2, 1, 1, 2, 2, 1, 1 },
        new byte[] { 0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 1, 0, 1, 1, 1 },
        new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2 },
        new byte[] { 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 2, 2 },
        new byte[] { 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1 },
        new byte[] { 0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1 },
        new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2 },
        new byte[] { 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2 },
        new byte[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2 },
        new byte[] { 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2 },
        new byte[] { 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2 },
        new byte[] { 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2 },
        new byte[] { 0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2, 1, 2, 2, 2 },
        new byte[] { 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0, 2, 2, 2, 0 },
        new byte[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2 },
        new byte[] { 0, 1, 1, 1, 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0 },
        new byte[] { 0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2 },
        new byte[] { 0, 0, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1 },
        new byte[] { 0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2 },
        new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 2, 2, 2, 1, 2, 2, 2, 1 },
        new byte[] { 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2 },
        new byte[] { 0, 0, 0, 0, 1, 1, 0, 0, 2, 2, 1, 0, 2, 2, 1, 0 },
        new byte[] { 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1, 0, 0, 0, 0 },
        new byte[] { 0, 0, 1, 2, 0, 0, 1, 2, 1, 1, 2, 2, 2, 2, 2, 2 },
        new byte[] { 0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1, 0, 1, 1, 0 },
        new byte[] { 0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1 },
        new byte[] { 0, 0, 2, 2, 1, 1, 0, 2, 1, 1, 0, 2, 0, 0, 2, 2 },
        new byte[] { 0, 1, 1, 0, 0, 1, 1, 0, 2, 0, 0, 2, 2, 2, 2, 2 },
        new byte[] { 0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1 },
        new byte[] { 0, 0, 0, 0, 2, 0, 0, 0, 2, 2, 1, 1, 2, 2, 2, 1 },
        new byte[] { 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 2, 2, 2 },
        new byte[] { 0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 2, 0, 0, 1, 1 },
        new byte[] { 0, 0, 1, 1, 0, 0, 1, 2, 0, 0, 2, 2, 0, 2, 2, 2 },
        new byte[] { 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0 },
        new byte[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0 },
        new byte[] { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0 },
        new byte[] { 0, 1, 2, 0, 2, 0, 1, 2, 1, 2, 0, 1, 0, 1, 2, 0 },
        new byte[] { 0, 0, 1, 1, 2, 2, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1 },
        new byte[] { 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1 },
        new byte[] { 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2 },
        new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1 },
        new byte[] { 0, 0, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 1, 1, 2, 2 },
        new byte[] { 0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 1, 1 },
        new byte[] { 0, 2, 2, 0, 1, 2, 2, 1, 0, 2, 2, 0, 1, 2, 2, 1 },
        new byte[] { 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 0, 1 },
        new byte[] { 0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1 },
        new byte[] { 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2 },
        new byte[] { 0, 2, 2, 2, 0, 1, 1, 1, 0, 2, 2, 2, 0, 1, 1, 1 },
        new byte[] { 0, 0, 0, 2, 1, 1, 1, 2, 0, 0, 0, 2, 1, 1, 1, 2 },
        new byte[] { 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2 },
        new byte[] { 0, 2, 2, 2, 0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2 },
        new byte[] { 0, 0, 0, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2 },
        new byte[] { 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2 },
        new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2 },
        new byte[] { 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2, 2, 2, 2, 2 },
        new byte[] { 0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2 },
        new byte[] { 0, 0, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2 },
        new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2 },
        new byte[] { 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1 },
        new byte[] { 0, 2, 2, 2, 1, 2, 2, 2, 0, 2, 2, 2, 1, 2, 2, 2 },
        new byte[] { 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 },
        new byte[] { 0, 1, 1, 1, 2, 0, 1, 1, 2, 2, 0, 1, 2, 2, 2, 0 },
    };

    /// <summary>The anchor pixel of subset 1 in each three-subset partition.</summary>
    public static readonly byte[] Anchor3a =
    {
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
        3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
        3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    };

    /// <summary>The anchor pixel of subset 2 in each three-subset partition.</summary>
    public static readonly byte[] Anchor3b =
    {
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
        15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
        15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    };

    /// <summary>The subset of pixel <paramref name="i"/> under partition <paramref name="p"/> of an <paramref name="ns"/>-subset mode.</summary>
    public static int Subset(int ns, int p, int i) => ns switch
    {
        1 => 0,
        2 => (Partition2[p] >> i) & 1,
        _ => Partition3[p][i],
    };

    /// <summary>True when pixel <paramref name="i"/> is an anchor (its index is stored one bit shorter).</summary>
    public static bool IsAnchor(int ns, int p, int i) => i == 0 || ns switch
    {
        1 => false,
        2 => i == Anchor2[p],
        _ => i == Anchor3a[p] || i == Anchor3b[p],
    };

    /// <summary>The anchor pixel of subset <paramref name="s"/>.</summary>
    public static int AnchorOf(int ns, int p, int s) => s == 0 ? 0 : ns == 2 ? Anchor2[p] : s == 1 ? Anchor3a[p] : Anchor3b[p];

    /// <summary>A <paramref name="prec"/>-bit endpoint value expanded to 8 bits by bit replication.</summary>
    public static int Expand(int v, int prec) => prec >= 8 ? v : (v << (8 - prec)) | (v >> ((2 * prec) - 8));

    public static int Interp(int e0, int e1, int w) => (((64 - w) * e0) + (w * e1) + 32) >> 6;
}

/// <summary>A little-endian 128-bit block, written and read least significant bit first.</summary>
internal struct Bc7Bits
{
    private ulong _lo;
    private ulong _hi;
    private int _pos;

    public static Bc7Bits From(ReadOnlySpan<byte> block) => new()
    {
        _lo = BitConverter.ToUInt64(block[..8]),
        _hi = BitConverter.ToUInt64(block.Slice(8, 8)),
    };

    public int Position => _pos;

    public int Read(int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++, _pos++)
        {
            ulong bit = _pos < 64 ? (_lo >> _pos) & 1 : (_hi >> (_pos - 64)) & 1;
            v |= (int)bit << i;
        }

        return v;
    }

    public void Write(int value, int n)
    {
        for (int i = 0; i < n; i++, _pos++)
        {
            ulong bit = (ulong)((value >> i) & 1);
            if (_pos < 64)
            {
                _lo |= bit << _pos;
            }
            else
            {
                _hi |= bit << (_pos - 64);
            }
        }
    }

    public readonly void CopyTo(Span<byte> block)
    {
        BitConverter.TryWriteBytes(block[..8], _lo);
        BitConverter.TryWriteBytes(block.Slice(8, 8), _hi);
    }
}

/// <summary>
/// Decodes BC7 blocks (all eight modes) to RGBA8, from the format specification. A reserved mode (first byte 0)
/// decodes to transparent black, as the specification requires.
/// </summary>
public static class Bc7Decoder
{
    /// <summary>The mode of an encoded block (8 for the reserved encoding).</summary>
    public static int ModeOf(ReadOnlySpan<byte> block)
    {
        byte b = block[0];
        for (int m = 0; m < 8; m++)
        {
            if ((b & (1 << m)) != 0)
            {
                return m;
            }
        }

        return 8;
    }

    /// <summary>Decodes one 16-byte block into 16 RGBA8 pixels (row major).</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        int mode = ModeOf(block);
        if (mode > 7)
        {
            rgba[..64].Clear();
            return;
        }

        var m = Bc7Tables.Modes[mode];
        Bc7Bits bits = Bc7Bits.From(block);
        bits.Read(mode + 1);
        int partition = bits.Read(m.Pb);
        int rotation = bits.Read(m.Rb);
        int isb = bits.Read(m.Isb);
        int ne = m.Ns * 2;
        Span<int> ep = stackalloc int[6 * 4];
        for (int c = 0; c < 3; c++)
        {
            for (int e = 0; e < ne; e++)
            {
                ep[(e * 4) + c] = bits.Read(m.Cb);
            }
        }

        for (int e = 0; e < ne; e++)
        {
            ep[(e * 4) + 3] = m.Ab > 0 ? bits.Read(m.Ab) : 255;
        }

        Span<int> pb = stackalloc int[6];
        if (m.Epb != 0)
        {
            for (int e = 0; e < ne; e++)
            {
                pb[e] = bits.Read(1);
            }
        }
        else if (m.Spb != 0)
        {
            for (int s = 0; s < m.Ns; s++)
            {
                int p = bits.Read(1);
                pb[2 * s] = p;
                pb[(2 * s) + 1] = p;
            }
        }

        bool hasP = m.Epb != 0 || m.Spb != 0;
        for (int e = 0; e < ne; e++)
        {
            for (int c = 0; c < 4; c++)
            {
                if (c == 3 && m.Ab == 0)
                {
                    continue;
                }

                int prec = c == 3 ? m.Ab : m.Cb;
                int v = ep[(e * 4) + c];
                if (hasP)
                {
                    v = (v << 1) | pb[e];
                    prec++;
                }

                ep[(e * 4) + c] = Bc7Tables.Expand(v, prec);
            }
        }

        Span<int> idx = stackalloc int[16];
        Span<int> idx2 = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            idx[i] = bits.Read(m.Ib - (Bc7Tables.IsAnchor(m.Ns, partition, i) ? 1 : 0));
        }

        if (m.Ib2 > 0)
        {
            for (int i = 0; i < 16; i++)
            {
                idx2[i] = bits.Read(m.Ib2 - (i == 0 ? 1 : 0));
            }
        }

        int[] wc = Bc7Tables.WeightsFor(m.Ib);
        int[] wa = Bc7Tables.WeightsFor(m.Ib2 > 0 ? m.Ib2 : m.Ib);
        for (int i = 0; i < 16; i++)
        {
            int s = Bc7Tables.Subset(m.Ns, partition, i);
            int e0 = s * 2 * 4, e1 = ((s * 2) + 1) * 4;
            int ci, ai;
            int[] colorW, alphaW;
            if (m.Ib2 == 0)
            {
                ci = ai = idx[i];
                colorW = alphaW = wc;
            }
            else if (isb == 0)
            {
                ci = idx[i];
                ai = idx2[i];
                colorW = wc;
                alphaW = wa;
            }
            else
            {
                ci = idx2[i];
                ai = idx[i];
                colorW = wa;
                alphaW = wc;
            }

            int r = Bc7Tables.Interp(ep[e0], ep[e1], colorW[ci]);
            int g = Bc7Tables.Interp(ep[e0 + 1], ep[e1 + 1], colorW[ci]);
            int b = Bc7Tables.Interp(ep[e0 + 2], ep[e1 + 2], colorW[ci]);
            int a = m.Ab == 0 ? 255 : Bc7Tables.Interp(ep[e0 + 3], ep[e1 + 3], alphaW[ai]);
            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, b) = (b, a); break;
            }

            rgba[i * 4] = (byte)r;
            rgba[(i * 4) + 1] = (byte)g;
            rgba[(i * 4) + 2] = (byte)b;
            rgba[(i * 4) + 3] = (byte)a;
        }
    }

    /// <summary>Decodes a page of <paramref name="size"/>² texels of BC7 blocks (row major) to RGB8.</summary>
    public static byte[] DecodePageRgb(ReadOnlySpan<byte> blocks, int size)
    {
        int bw = size / 4;
        var rgb = new byte[size * size * 3];
        Span<byte> px = stackalloc byte[64];
        for (int by = 0; by < bw; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                DecodeBlock(blocks.Slice(((by * bw) + bx) * 16, 16), px);
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int d = ((((by * 4) + y) * size) + (bx * 4) + x) * 3;
                        int s = ((y * 4) + x) * 4;
                        rgb[d] = px[s];
                        rgb[d + 1] = px[s + 1];
                        rgb[d + 2] = px[s + 2];
                    }
                }
            }
        }

        return rgb;
    }
}

/// <summary>
/// Glacier's clean-room BC7 encoder for opaque RGB blocks (lightmap radiance), written from the format specification.
/// Each block is tried in mode 6 (one subset, 4-bit indices, 8-bit effective endpoints) and in mode 1 (two subsets,
/// 3-bit indices) over the partitions a fast line-fit estimate ranks best — the two modes Alpine RED's encoder emits for
/// lightmaps; endpoints come from the principal axis and are
/// refined by least squares against the chosen indices, every p-bit choice is searched, and the block with the lowest
/// squared RGB error (uniform channel weights, as Alpine's "perceptual off") is kept. RNG-free and per block, so the
/// output depends only on the pixels.
/// </summary>
public static class Bc7Encoder
{
    /// <summary>Two-subset partitions fully encoded per block, best estimate first.</summary>
    private const int PartitionsTried = 6;

    /// <summary>A mode-6 block at or below this squared RGB error (one level per channel on average) is kept as is.</summary>
    private const int GoodEnoughError = 48;

    /// <summary>Encodes 16 RGB pixels (48 bytes, row major) into a 16-byte block; returns its squared RGB error.</summary>
    public static int EncodeBlock(ReadOnlySpan<byte> rgb, Span<byte> block)
    {
        Span<int> px = stackalloc int[48];
        bool flat = true;
        for (int i = 0; i < 48; i++)
        {
            px[i] = rgb[i];
            flat &= rgb[i] == rgb[i % 3];
        }

        Span<byte> best = stackalloc byte[16];
        Span<byte> trial = stackalloc byte[16];
        int bestErr = EncodeMode6(px, best);

        // Mode 6 within one level per channel on average is as good as lightmap texels need: skip the partition search.
        if (bestErr <= GoodEnoughError || flat)
        {
            best.CopyTo(block);
            return bestErr;
        }

        Span<int> parts = stackalloc int[PartitionsTried];
        RankPartitions(px, parts);
        foreach (int p in parts)
        {
            int e1 = EncodeTwoSubset(px, p, mode: 1, trial);
            if (e1 < bestErr)
            {
                bestErr = e1;
                trial.CopyTo(best);
            }

        }

        best.CopyTo(block);
        return bestErr;
    }

    /// <summary>Squared RGB error of <paramref name="block"/> against 16 RGB pixels.</summary>
    public static int BlockError(ReadOnlySpan<byte> block, ReadOnlySpan<byte> rgb)
    {
        Span<byte> d = stackalloc byte[64];
        Bc7Decoder.DecodeBlock(block, d);
        int err = 0;
        for (int i = 0; i < 16; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                int e = d[(i * 4) + c] - rgb[(i * 3) + c];
                err += e * e;
            }
        }

        return err;
    }

    /// <summary>Encodes a page of <paramref name="size"/>² RGB8 texels into BC7 blocks (row major), in parallel.</summary>
    public static byte[] EncodePage(byte[] rgb, int size, ParallelOptions? po = null)
    {
        int bw = size / 4;
        var blocks = new byte[bw * bw * 16];
        Parallel.For(0, bw, po ?? new ParallelOptions(), by =>
        {
            Span<byte> px = stackalloc byte[48];
            for (int bx = 0; bx < bw; bx++)
            {
                GatherBlock(rgb, size, bx, by, px);
                EncodeBlock(px, blocks.AsSpan(((by * bw) + bx) * 16, 16));
            }
        });
        return blocks;
    }

    /// <summary>Copies block (bx, by) of an RGB8 page into 48 bytes.</summary>
    public static void GatherBlock(byte[] rgb, int size, int bx, int by, Span<byte> px)
    {
        for (int y = 0; y < 4; y++)
        {
            rgb.AsSpan(((((by * 4) + y) * size) + (bx * 4)) * 3, 12).CopyTo(px.Slice(y * 12, 12));
        }
    }

    /// <summary>
    /// A mode-6 block that keeps <paramref name="source"/>'s index bits (its last 63 bits, byte for byte) with
    /// endpoints least-squares fitted to <paramref name="rgb"/> under those indices — the rate-distortion pass's
    /// "reuse the selectors" candidate. False when the indices do not determine a line.
    /// </summary>
    public static bool RefitMode6(ReadOnlySpan<byte> source, ReadOnlySpan<byte> rgb, Span<byte> block)
    {
        Bc7Bits bits = Bc7Bits.From(source);
        bits.Read(7 + 56 + 2);
        Span<int> idx = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            idx[i] = bits.Read(i == 0 ? 3 : 4);
        }

        Span<int> px = stackalloc int[48];
        Span<int> members = stackalloc int[16];
        for (int i = 0; i < 48; i++)
        {
            px[i] = rgb[i];
        }

        for (int i = 0; i < 16; i++)
        {
            members[i] = i;
        }

        Span<float> e = stackalloc float[6];
        if (!LeastSquares(px, members, idx, Bc7Tables.Weights4, e))
        {
            return false;
        }

        int bestErr = int.MaxValue;
        Span<int> q = stackalloc int[6];
        Span<int> bq = stackalloc int[6];
        Span<int> recon = stackalloc int[6];
        int bp0 = 0, bp1 = 0;
        for (int pc = 0; pc < 4; pc++)
        {
            int p0 = pc & 1, p1 = pc >> 1;
            for (int k = 0; k < 6; k++)
            {
                (q[k], recon[k]) = Quantize(e[k], 7, true, k < 3 ? p0 : p1);
            }

            int err = 0;
            for (int i = 0; i < 16; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int d = Bc7Tables.Interp(recon[c], recon[3 + c], Bc7Tables.Weights4[idx[i]]) - px[(i * 3) + c];
                    err += d * d;
                }
            }

            if (err < bestErr)
            {
                bestErr = err;
                q.CopyTo(bq);
                bp0 = p0;
                bp1 = p1;
            }
        }

        var w = default(Bc7Bits);
        w.Write(1 << 6, 7);
        for (int c = 0; c < 3; c++)
        {
            w.Write(bq[c], 7);
            w.Write(bq[3 + c], 7);
        }

        w.Write(127, 7);
        w.Write(127, 7);
        w.Write(bp0, 1);
        w.Write(bp1, 1);
        for (int i = 0; i < 16; i++)
        {
            w.Write(idx[i], i == 0 ? 3 : 4);
        }

        w.CopyTo(block);
        return true;
    }

    // ---- mode 6 --------------------------------------------------------------------------------------------------

    private static int EncodeMode6(ReadOnlySpan<int> px, Span<byte> block)
    {
        Span<int> members = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            members[i] = i;
        }

        Span<int> q = stackalloc int[6]; // quantized e0 rgb, e1 rgb (7 bit)
        Span<int> pbits = stackalloc int[2];
        Span<int> idx = stackalloc int[16];
        int err = FitSubset(px, members, colorBits: 7, pbitMode: 2, indexBits: 4, q, pbits, idx);

        // anchor: pixel 0's index must have its top bit clear
        if (idx[0] >= 8)
        {
            Swap(q, pbits, 0);
            for (int i = 0; i < 16; i++)
            {
                idx[i] = 15 - idx[i];
            }
        }

        var bits = default(Bc7Bits);
        bits.Write(1 << 6, 7);
        for (int c = 0; c < 3; c++)
        {
            bits.Write(q[c], 7);
            bits.Write(q[3 + c], 7);
        }

        bits.Write(127, 7); // alpha: opaque
        bits.Write(127, 7);
        bits.Write(pbits[0], 1);
        bits.Write(pbits[1], 1);
        for (int i = 0; i < 16; i++)
        {
            bits.Write(idx[i], i == 0 ? 3 : 4);
        }

        bits.CopyTo(block);
        return err;
    }

    // ---- modes 1 and 3 -------------------------------------------------------------------------------------------

    private static int EncodeTwoSubset(ReadOnlySpan<int> px, int partition, int mode, Span<byte> block)
    {
        int cb = mode == 1 ? 6 : 7;
        int ib = mode == 1 ? 3 : 2;
        int pbitMode = mode == 1 ? 1 : 2;
        Span<int> q = stackalloc int[12];
        Span<int> pb = stackalloc int[4];
        Span<int> idx = stackalloc int[16];
        Span<int> sub = stackalloc int[16];
        Span<int> subIdx = stackalloc int[16];
        int err = 0;
        for (int s = 0; s < 2; s++)
        {
            int n = 0;
            for (int i = 0; i < 16; i++)
            {
                if (Bc7Tables.Subset(2, partition, i) == s)
                {
                    sub[n++] = i;
                }
            }

            Span<int> qs = q.Slice(s * 6, 6);
            Span<int> ps = pb.Slice(s * 2, 2);
            err += FitSubset(px, sub[..n], cb, pbitMode, ib, qs, ps, subIdx);
            int max = (1 << ib) - 1;
            int anchor = Bc7Tables.AnchorOf(2, partition, s);
            int anchorIdx = 0;
            for (int k = 0; k < n; k++)
            {
                if (sub[k] == anchor)
                {
                    anchorIdx = subIdx[k];
                }
            }

            bool flip = anchorIdx > (max >> 1);
            if (flip)
            {
                Swap(qs, ps, 0);
            }

            for (int k = 0; k < n; k++)
            {
                idx[sub[k]] = flip ? max - subIdx[k] : subIdx[k];
            }
        }

        var bits = default(Bc7Bits);
        bits.Write(1 << mode, mode + 1);
        bits.Write(partition, 6);
        for (int c = 0; c < 3; c++)
        {
            bits.Write(q[c], cb);      // subset 0, endpoint 0
            bits.Write(q[3 + c], cb);  // subset 0, endpoint 1
            bits.Write(q[6 + c], cb);  // subset 1, endpoint 0
            bits.Write(q[9 + c], cb);  // subset 1, endpoint 1
        }

        if (mode == 1)
        {
            bits.Write(pb[0], 1); // one shared p-bit per subset
            bits.Write(pb[2], 1);
        }
        else
        {
            for (int e = 0; e < 4; e++)
            {
                bits.Write(pb[e], 1);
            }
        }

        for (int i = 0; i < 16; i++)
        {
            bits.Write(idx[i], ib - (Bc7Tables.IsAnchor(2, partition, i) ? 1 : 0));
        }

        bits.CopyTo(block);
        return err;
    }

    private static void Swap(Span<int> q, Span<int> pbits, int unused)
    {
        _ = unused;
        for (int c = 0; c < 3; c++)
        {
            (q[c], q[3 + c]) = (q[3 + c], q[c]);
        }

        (pbits[0], pbits[1]) = (pbits[1], pbits[0]);
    }

    /// <summary>Ranks the 64 two-subset partitions by the residual of a line fit per subset; writes the best few.</summary>
    private static void RankPartitions(ReadOnlySpan<int> px, Span<int> outParts)
    {
        Span<float> score = stackalloc float[64];
        Span<int> sub = stackalloc int[16];
        for (int p = 0; p < 64; p++)
        {
            float total = 0;
            for (int s = 0; s < 2; s++)
            {
                int n = 0;
                for (int i = 0; i < 16; i++)
                {
                    if (((Bc7Tables.Partition2[p] >> i) & 1) == s)
                    {
                        sub[n++] = i;
                    }
                }

                total += LineResidual(px, sub[..n]);
            }

            score[p] = total;
        }

        for (int k = 0; k < outParts.Length; k++)
        {
            int bestP = 0;
            float bestS = float.MaxValue;
            for (int p = 0; p < 64; p++)
            {
                if (score[p] < bestS)
                {
                    bestS = score[p];
                    bestP = p;
                }
            }

            outParts[k] = bestP;
            score[bestP] = float.MaxValue;
        }
    }

    private static float LineResidual(ReadOnlySpan<int> px, ReadOnlySpan<int> members)
    {
        Axis(px, members, out float mr, out float mg, out float mb, out float ar, out float ag, out float ab);
        float res = 0;
        foreach (int i in members)
        {
            float dr = px[i * 3] - mr, dg = px[(i * 3) + 1] - mg, db = px[(i * 3) + 2] - mb;
            float t = (dr * ar) + (dg * ag) + (db * ab);
            res += (dr * dr) + (dg * dg) + (db * db) - (t * t);
        }

        return res;
    }

    /// <summary>Mean and principal axis (unit, power iteration on the covariance) of the member pixels.</summary>
    private static void Axis(
        ReadOnlySpan<int> px, ReadOnlySpan<int> members,
        out float mr, out float mg, out float mb, out float ar, out float ag, out float ab)
    {
        mr = mg = mb = 0;
        foreach (int i in members)
        {
            mr += px[i * 3];
            mg += px[(i * 3) + 1];
            mb += px[(i * 3) + 2];
        }

        float inv = 1f / Math.Max(1, members.Length);
        mr *= inv;
        mg *= inv;
        mb *= inv;
        float c00 = 0, c01 = 0, c02 = 0, c11 = 0, c12 = 0, c22 = 0;
        foreach (int i in members)
        {
            float r = px[i * 3] - mr, g = px[(i * 3) + 1] - mg, b = px[(i * 3) + 2] - mb;
            c00 += r * r;
            c01 += r * g;
            c02 += r * b;
            c11 += g * g;
            c12 += g * b;
            c22 += b * b;
        }

        // start from the largest-variance channel so the iteration cannot start orthogonal to the answer
        float vr = c00 >= c11 && c00 >= c22 ? 1 : 0.1f, vg = c11 > c00 && c11 >= c22 ? 1 : 0.1f, vb = c22 > c00 && c22 > c11 ? 1 : 0.1f;
        for (int it = 0; it < 8; it++)
        {
            float nr = (c00 * vr) + (c01 * vg) + (c02 * vb);
            float ng = (c01 * vr) + (c11 * vg) + (c12 * vb);
            float nb = (c02 * vr) + (c12 * vg) + (c22 * vb);
            float len = MathF.Sqrt((nr * nr) + (ng * ng) + (nb * nb));
            if (!(len > 1e-12f))
            {
                break;
            }

            vr = nr / len;
            vg = ng / len;
            vb = nb / len;
        }

        float l = MathF.Sqrt((vr * vr) + (vg * vg) + (vb * vb));
        ar = l > 0 ? vr / l : 0.57735f;
        ag = l > 0 ? vg / l : 0.57735f;
        ab = l > 0 ? vb / l : 0.57735f;
    }

    /// <summary>
    /// Fits one subset: line endpoints from the principal axis, then rounds of quantize → pick indices → least-squares
    /// re-fit, keeping the best quantized result. <paramref name="pbitMode"/>: 0 none, 1 one shared p-bit, 2 one per
    /// endpoint. Writes the quantized endpoints (q[0..2] e0, q[3..5] e1), p-bits and per-member indices; returns the
    /// squared error.
    /// </summary>
    private static int FitSubset(
        ReadOnlySpan<int> px, ReadOnlySpan<int> members, int colorBits, int pbitMode, int indexBits,
        Span<int> q, Span<int> pbits, Span<int> idx)
    {
        Axis(px, members, out float mr, out float mg, out float mb, out float ar, out float ag, out float ab);
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (int i in members)
        {
            float t = ((px[i * 3] - mr) * ar) + ((px[(i * 3) + 1] - mg) * ag) + ((px[(i * 3) + 2] - mb) * ab);
            lo = MathF.Min(lo, t);
            hi = MathF.Max(hi, t);
        }

        Span<float> e = stackalloc float[6];
        e[0] = mr + (ar * lo); e[1] = mg + (ag * lo); e[2] = mb + (ab * lo);
        e[3] = mr + (ar * hi); e[4] = mg + (ag * hi); e[5] = mb + (ab * hi);

        int[] weights = Bc7Tables.WeightsFor(indexBits);
        int bestErr = int.MaxValue;
        Span<int> tq = stackalloc int[6];
        Span<int> tp = stackalloc int[2];
        Span<int> tidx = stackalloc int[16];
        Span<int> recon = stackalloc int[6];
        for (int round = 0; round < 3; round++)
        {
            int combos = pbitMode == 0 ? 1 : pbitMode == 1 ? 2 : 4;
            for (int pc = 0; pc < combos; pc++)
            {
                tp[0] = pbitMode == 0 ? 0 : pc & 1;
                tp[1] = pbitMode == 2 ? (pc >> 1) & 1 : tp[0];
                for (int k = 0; k < 6; k++)
                {
                    (tq[k], recon[k]) = Quantize(e[k], colorBits, pbitMode != 0, tp[k / 3]);
                }

                int err = AssignIndices(px, members, recon, weights, tidx);
                if (err < bestErr)
                {
                    bestErr = err;
                    tq.CopyTo(q);
                    tp.CopyTo(pbits);
                    tidx[..members.Length].CopyTo(idx);
                }
            }

            if (bestErr == 0 || !LeastSquares(px, members, idx, weights, e))
            {
                break;
            }
        }

        return bestErr;
    }

    /// <summary>The stored value and its 8-bit reconstruction nearest to <paramref name="x"/>.</summary>
    private static (int Stored, int Value) Quantize(float x, int bits, bool hasP, int p)
    {
        int prec = bits + (hasP ? 1 : 0);
        int max = (1 << bits) - 1;
        float scaled = x / 255f * ((1 << prec) - 1);
        int guess = (int)MathF.Round(hasP ? (scaled - p) / 2f : scaled);
        int bestS = 0, bestV = 0;
        float bestD = float.MaxValue;
        for (int c = guess - 1; c <= guess + 1; c++)
        {
            int s = Math.Clamp(c, 0, max);
            int v = Bc7Tables.Expand(hasP ? (s << 1) | p : s, prec);
            float d = MathF.Abs(v - x);
            if (d < bestD)
            {
                bestD = d;
                bestS = s;
                bestV = v;
            }
        }

        return (bestS, bestV);
    }

    private static int AssignIndices(ReadOnlySpan<int> px, ReadOnlySpan<int> members, ReadOnlySpan<int> recon, int[] weights, Span<int> idx)
    {
        int n = weights.Length;
        Span<int> pal = stackalloc int[16 * 3];
        for (int k = 0; k < n; k++)
        {
            for (int c = 0; c < 3; c++)
            {
                pal[(k * 3) + c] = Bc7Tables.Interp(recon[c], recon[3 + c], weights[k]);
            }
        }

        int total = 0;
        for (int m = 0; m < members.Length; m++)
        {
            int i = members[m];
            int r = px[i * 3], g = px[(i * 3) + 1], b = px[(i * 3) + 2];
            int best = int.MaxValue, bi = 0;
            for (int k = 0; k < n; k++)
            {
                int dr = pal[k * 3] - r, dg = pal[(k * 3) + 1] - g, db = pal[(k * 3) + 2] - b;
                int d = (dr * dr) + (dg * dg) + (db * db);
                if (d < best)
                {
                    best = d;
                    bi = k;
                }
            }

            idx[m] = bi;
            total += best;
        }

        return total;
    }

    /// <summary>Least-squares endpoints for fixed indices; false when the indices do not determine a line.</summary>
    private static bool LeastSquares(ReadOnlySpan<int> px, ReadOnlySpan<int> members, ReadOnlySpan<int> idx, int[] weights, Span<float> e)
    {
        double a = 0, b = 0, c = 0;
        Span<double> r0 = stackalloc double[3];
        Span<double> r1 = stackalloc double[3];
        for (int m = 0; m < members.Length; m++)
        {
            double t = weights[idx[m]] / 64.0, s = 1 - t;
            a += s * s;
            b += s * t;
            c += t * t;
            int i = members[m];
            for (int ch = 0; ch < 3; ch++)
            {
                r0[ch] += s * px[(i * 3) + ch];
                r1[ch] += t * px[(i * 3) + ch];
            }
        }

        double det = (a * c) - (b * b);
        if (Math.Abs(det) < 1e-9)
        {
            return false;
        }

        for (int ch = 0; ch < 3; ch++)
        {
            e[ch] = (float)Math.Clamp(((c * r0[ch]) - (b * r1[ch])) / det, 0, 255);
            e[3 + ch] = (float)Math.Clamp(((a * r1[ch]) - (b * r0[ch])) / det, 0, 255);
        }

        return true;
    }
}
