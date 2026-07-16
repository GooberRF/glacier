using StbImageSharp;

namespace Ged.Core.IO.Tex;

/// <summary>
/// PNG and JPEG decoding via StbImageSharp (public-domain/MIT). stb auto-detects
/// the container from its magic bytes and yields top-left-origin RGBA8, which is
/// exactly GED's <see cref="TextureImage"/> convention.
/// </summary>
public static class StbTextureDecoder
{
    public static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length >= 8 &&
        data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
        data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    public static bool IsJpeg(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static DecodedTexture Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        TextureFormatKind kind =
            IsPng(data) ? TextureFormatKind.Png :
            IsJpeg(data) ? TextureFormatKind.Jpeg :
            throw new TextureFormatException("Data is neither a PNG nor a JPEG.");

        ImageResult result;
        try
        {
            result = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            throw new TextureFormatException($"Failed to decode {kind} image: {ex.Message}", ex);
        }

        if (result.Data is null || result.Width <= 0 || result.Height <= 0)
        {
            throw new TextureFormatException($"{kind} decode produced an empty image.");
        }

        var image = new TextureImage(result.Width, result.Height, result.Data);
        return new DecodedTexture(image, kind);
    }
}
