namespace Ged.Core.IO.Tex;

/// <summary>
/// The result of decoding a texture file: one or more frames (each the level-0
/// image; animated formats such as VBM/ATX contribute one entry per frame),
/// plus source metadata (mip-level count, animation fps, originating format).
/// </summary>
public sealed class DecodedTexture
{
    public DecodedTexture(
        IReadOnlyList<TextureImage> frames,
        int mipCount,
        int fps,
        TextureFormatKind sourceFormat)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("A decoded texture must contain at least one frame.", nameof(frames));
        }

        Frames = frames;
        MipCount = Math.Max(1, mipCount);
        Fps = fps;
        SourceFormat = sourceFormat;
    }

    public DecodedTexture(TextureImage image, TextureFormatKind sourceFormat, int mipCount = 1, int fps = 0)
        : this(new[] { image }, mipCount, fps, sourceFormat)
    {
    }

    /// <summary>Decoded frames; <c>Frames[0]</c> is the primary/preview image. Always non-empty.</summary>
    public IReadOnlyList<TextureImage> Frames { get; }

    /// <summary>Number of mip levels present in the source (>= 1) for frame 0.</summary>
    public int MipCount { get; }

    /// <summary>Animation rate in frames per second (VBM/ATX); 0 for static formats.</summary>
    public int Fps { get; }

    public TextureFormatKind SourceFormat { get; }

    /// <summary>The primary image used for previews and thumbnails.</summary>
    public TextureImage Primary => Frames[0];

    public int Width => Primary.Width;

    public int Height => Primary.Height;

    public int FrameCount => Frames.Count;
}
