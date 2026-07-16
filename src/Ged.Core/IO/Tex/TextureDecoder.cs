namespace Ged.Core.IO.Tex;

/// <summary>
/// Front door for texture decoding. Dispatches raw bytes to the right decoder by
/// content magic (TGA/VBM/DDS/PNG/JPEG). ATX descriptors are excluded here because
/// they reference other files and require a resolver — decode those through
/// <see cref="AtxDecoder"/>.
/// </summary>
public static class TextureDecoder
{
    /// <summary>True if the extension (with or without leading dot) is a directly-decodable texture format.</summary>
    public static bool IsSupportedExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        string ext = extension[0] == '.' ? extension : "." + extension;
        return SupercedeChain.IsTextureExtension(ext);
    }

    /// <summary>
    /// Decodes texture bytes by sniffing their magic. Handles TGA, VBM, DDS, PNG and
    /// JPEG. Throws for ATX (needs a resolver) and unrecognised data.
    /// </summary>
    public static DecodedTexture Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var span = data.AsSpan();

        if (StbTextureDecoder.IsPng(span) || StbTextureDecoder.IsJpeg(span))
        {
            return StbTextureDecoder.Decode(data);
        }

        if (DdsDecoder.CanDecode(span))
        {
            return DdsDecoder.Decode(data);
        }

        if (VbmDecoder.CanDecode(span))
        {
            return VbmDecoder.Decode(data);
        }

        if (TgaDecoder.CanDecode(span))
        {
            return TgaDecoder.Decode(data);
        }

        throw new TextureFormatException("Unrecognised texture data (not TGA/VBM/DDS/PNG/JPEG).");
    }

    /// <summary>
    /// Decodes texture bytes preferring the format implied by <paramref name="fileName"/>'s
    /// extension, falling back to magic sniffing. Callers that need ATX support should
    /// route <c>.atx</c> files through <see cref="AtxDecoder"/> instead.
    /// </summary>
    public static DecodedTexture Decode(string fileName, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(data);

        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".tga" => TgaDecoder.Decode(data),
                ".vbm" => VbmDecoder.Decode(data),
                ".dds" => DdsDecoder.Decode(data),
                ".png" or ".jpg" or ".jpeg" => StbTextureDecoder.Decode(data),
                ".atx" => throw new TextureFormatException(
                    "ATX descriptors must be decoded through AtxDecoder with a file resolver."),
                _ => Decode(data),
            };
        }
        catch (TextureFormatException) when (ext is not ".atx")
        {
            // Extension lied about the content (e.g. a .tga that is really a PNG); try magic.
            return Decode(data);
        }
    }
}
