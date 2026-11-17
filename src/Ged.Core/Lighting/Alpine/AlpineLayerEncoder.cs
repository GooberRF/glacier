using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ged.Core.Lighting.Alpine;

/// <summary>The layer codec of an Alpine lightmap section (LayerDirEntry::codec, alpine_lightmap.h:100-106).</summary>
public enum AlpineLightmapCodec : ushort
{
    /// <summary>Uncompressed RGB8 pages (Alpine RED's <c>-rawlightmaps</c> debug output).</summary>
    RawRgb8 = 0,

    /// <summary>BC7 blocks, Alpine RED's shipping codec.</summary>
    Bc7 = 1,
}

/// <summary>
/// The level property <c>lightmap_compression</c> (alpine_lightmap.h:797-811): 0 quality (plain BC7), 1 balanced,
/// 2 compact — the last two run a rate-distortion pass that trades a little texel error for a smaller zlib payload.
/// </summary>
public enum AlpineLightmapCompression : byte
{
    Quality = 0,
    Balanced = 1,
    Compact = 2,
}

/// <summary>
/// Encodes an Alpine lightmap atlas into its layer pages: raw RGB8 as is, or BC7 with Glacier's encoder
/// (<see cref="Bc7Encoder"/>) followed, for Balanced and Compact, by Glacier's own rate-distortion pass. Alpine RED's
/// RDO settings (alpine_lightmap.h:840-861 — lambda 0.5 / 2.0, a 128 / 256 block look-back window, a 10× RMS
/// increase cap and a tighter bound for smooth blocks) parameterise the pass; the pass itself is Glacier's: a block
/// is replaced by an exact copy of an earlier block in the window, or re-fitted onto an earlier block's index bits,
/// when the squared-error increase is below lambda × the bits a zlib match saves. Blocks holding a tile gutter texel
/// another tile shares are left as encoded so seams stay exact (as Alpine RED keeps them, alpine_lightmaps.cpp:1380-1425).
/// Every page is processed on its own, so the result does not depend on the thread count.
/// </summary>
public static class AlpineLayerEncoder
{
    /// <summary>Bits of a fresh 16-byte block in the zlib stream (no match).</summary>
    private const double FreshBits = 128;

    /// <summary>Approximate bits of a 16-byte block that is a back-reference to an earlier one.</summary>
    private const double CopyBits = 24;

    /// <summary>Approximate bits of a block whose 8 index bytes repeat an earlier block's.</summary>
    private const double IndexReuseBits = 76;

    /// <summary>Alpine RED's RDO parameters per mode (lambda, window, smooth-block MSE scale).</summary>
    public static (float Lambda, int Window, float SmoothScale) Settings(AlpineLightmapCompression mode) => mode switch
    {
        AlpineLightmapCompression.Balanced => (0.5f, 128, 15f),
        AlpineLightmapCompression.Compact => (2.0f, 256, 25f),
        _ => (0f, 128, 15f),
    };

    /// <summary>The stored level property value as a mode (anything above 2 reads as Quality, compression_mode_from_wire).</summary>
    public static AlpineLightmapCompression FromWire(byte v) => v <= 2 ? (AlpineLightmapCompression)v : AlpineLightmapCompression.Quality;

    /// <summary>The layer bytes of one page of each codec.</summary>
    public static int PageBytes(AlpineLightmapCodec codec) =>
        codec == AlpineLightmapCodec.Bc7 ? AlpineLightmapLayout.Bc7PageBytes : AlpineLightmapLayout.RawPageBytes;

    /// <summary>
    /// The layer pages of <paramref name="rgbPages"/>. <paramref name="shared"/> marks, per BC7 block of the whole
    /// atlas, the blocks the RDO pass must keep (null: none). Returns the pages and the blocks the RDO pass changed.
    /// </summary>
    public static (byte[][] Layer, int RdoModified) Encode(
        IReadOnlyList<byte[]> rgbPages, AlpineLightmapCodec codec, AlpineLightmapCompression mode, bool[]? shared,
        ParallelOptions? po = null)
    {
        var layer = new byte[rgbPages.Count][];
        if (codec == AlpineLightmapCodec.RawRgb8)
        {
            for (int p = 0; p < layer.Length; p++)
            {
                layer[p] = rgbPages[p];
            }

            return (layer, 0);
        }

        po ??= new ParallelOptions();
        const int size = AlpineLightmapLayout.PageSize;
        for (int p = 0; p < layer.Length; p++)
        {
            layer[p] = Bc7Encoder.EncodePage(rgbPages[p], size, po);
        }

        (float lambda, int window, float smooth) = Settings(mode);
        if (!(lambda > 0f))
        {
            return (layer, 0);
        }

        int modified = 0;
        const int blocksPerPage = (size / 4) * (size / 4);
        Parallel.For(0, layer.Length, po, p =>
        {
            int m = ReduceEntropy(layer[p], rgbPages[p], lambda, window, smooth, shared, p * blocksPerPage);
            Interlocked.Add(ref modified, m);
        });
        return (layer, modified);
    }

    /// <summary>The rate-distortion pass over one page's blocks, in block order; returns the blocks changed.</summary>
    private static int ReduceEntropy(
        byte[] blocks, byte[] rgb, float lambda, int window, float smoothScale, bool[]? shared, int firstBlock)
    {
        const int size = AlpineLightmapLayout.PageSize;
        const int bw = size / 4;
        int n = bw * bw;
        int changed = 0;
        Span<byte> px = stackalloc byte[48];
        Span<byte> cand = stackalloc byte[16];
        Span<byte> best = stackalloc byte[16];
        for (int b = 0; b < n; b++)
        {
            if (shared is not null && firstBlock + b < shared.Length && shared[firstBlock + b])
            {
                continue;
            }

            Bc7Encoder.GatherBlock(rgb, size, b % bw, b / bw, px);
            Span<byte> cur = blocks.AsSpan(b * 16, 16);
            int origErr = Bc7Encoder.BlockError(cur, px);
            double origCost = origErr + (lambda * FreshBits);

            // Bounds: at most 10× the block's RMS error, and a tighter cap for a smooth block, whose banding shows.
            double maxErr = origErr == 0 ? 0 : 100.0 * origErr;
            double std = StdDev(px);
            if (std < 18.0)
            {
                maxErr = Math.Min(maxErr, origErr + (smoothScale * 48.0 * Math.Max(0.1, std / 18.0)));
            }

            double bestCost = origCost;
            bool found = false;
            int start = Math.Max(0, b - window);
            for (int j = b - 1; j >= start; j--)
            {
                ReadOnlySpan<byte> prev = blocks.AsSpan(j * 16, 16);
                if (prev.SequenceEqual(cur))
                {
                    found = false;
                    bestCost = double.MinValue; // already a repeat: nothing to gain
                    break;
                }

                int e = Bc7Encoder.BlockError(prev, px);
                double cost = e + (lambda * CopyBits);
                if (e <= maxErr && cost < bestCost)
                {
                    bestCost = cost;
                    prev.CopyTo(best);
                    found = true;
                }
            }

            if (bestCost != double.MinValue)
            {
                // Index reuse: an earlier mode-6 block's index bits under endpoints re-fitted to this block.
                int reuseStart = Math.Max(0, b - Math.Min(window, 32));
                for (int j = b - 1; j >= reuseStart; j--)
                {
                    ReadOnlySpan<byte> prev = blocks.AsSpan(j * 16, 16);
                    if (Bc7Decoder.ModeOf(prev) != 6 || !Bc7Encoder.RefitMode6(prev, px, cand))
                    {
                        continue;
                    }

                    int e = Bc7Encoder.BlockError(cand, px);
                    double cost = e + (lambda * IndexReuseBits);
                    if (e <= maxErr && cost < bestCost)
                    {
                        bestCost = cost;
                        cand.CopyTo(best);
                        found = true;
                    }
                }
            }

            if (found)
            {
                best.CopyTo(cur);
                changed++;
            }
        }

        return changed;
    }

    private static double StdDev(ReadOnlySpan<byte> px)
    {
        double sum = 0, sq = 0;
        foreach (byte v in px)
        {
            sum += v;
            sq += v * v;
        }

        double mean = sum / px.Length;
        return Math.Sqrt(Math.Max(0, (sq / px.Length) - (mean * mean)));
    }
}
