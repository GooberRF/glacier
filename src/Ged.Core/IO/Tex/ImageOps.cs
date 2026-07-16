namespace Ged.Core.IO.Tex;

/// <summary>CPU image operations used by previews and the thumbnail cache.</summary>
public static class ImageOps
{
    /// <summary>
    /// Box-filter downscale of an RGBA8 image so its larger side is at most
    /// <paramref name="maxSize"/>, preserving aspect ratio. Each destination pixel
    /// is the unweighted average of the source pixels that map to it. Images that
    /// already fit are returned unchanged.
    /// </summary>
    public static TextureImage DownscaleToFit(TextureImage source, int maxSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSize));
        }

        int sw = source.Width;
        int sh = source.Height;
        if (sw <= 0 || sh <= 0 || (sw <= maxSize && sh <= maxSize))
        {
            return source;
        }

        double scale = (double)maxSize / Math.Max(sw, sh);
        int dw = Math.Max(1, (int)Math.Round(sw * scale));
        int dh = Math.Max(1, (int)Math.Round(sh * scale));
        return BoxDownscale(source, dw, dh);
    }

    /// <summary>Box-filter resample of an RGBA8 image to the exact target dimensions.</summary>
    public static TextureImage BoxDownscale(TextureImage source, int dw, int dh)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (dw <= 0 || dh <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dw));
        }

        int sw = source.Width;
        int sh = source.Height;
        byte[] src = source.Pixels;
        var dst = new byte[dw * dh * 4];

        for (int y = 0; y < dh; y++)
        {
            int sy0 = (int)((long)y * sh / dh);
            int sy1 = (int)(((long)(y + 1) * sh / dh));
            if (sy1 <= sy0)
            {
                sy1 = sy0 + 1;
            }

            for (int x = 0; x < dw; x++)
            {
                int sx0 = (int)((long)x * sw / dw);
                int sx1 = (int)(((long)(x + 1) * sw / dw));
                if (sx1 <= sx0)
                {
                    sx1 = sx0 + 1;
                }

                long r = 0, g = 0, b = 0, a = 0;
                int count = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int row = sy * sw * 4;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int si = row + (sx * 4);
                        r += src[si];
                        g += src[si + 1];
                        b += src[si + 2];
                        a += src[si + 3];
                        count++;
                    }
                }

                int di = ((y * dw) + x) * 4;
                dst[di] = (byte)(r / count);
                dst[di + 1] = (byte)(g / count);
                dst[di + 2] = (byte)(b / count);
                dst[di + 3] = (byte)(a / count);
            }
        }

        return new TextureImage(dw, dh, dst);
    }
}
