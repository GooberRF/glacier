namespace Ged.Core.Model;

/// <summary>A single lightmap atlas page (RFL <c>lightmap</c>): 24bpp RGB pixels.</summary>
public sealed class Lightmap
{
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>Width * Height * 3 bytes of 24bpp RGB data.</summary>
    public byte[] Pixels { get; set; } = Array.Empty<byte>();
}
