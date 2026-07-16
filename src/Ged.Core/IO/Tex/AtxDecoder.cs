namespace Ged.Core.IO.Tex;

/// <summary>
/// Decodes an ATX animated texture for editor preview. Given a parsed
/// <see cref="AtxDescriptor"/> and a resolver that turns a referenced frame file
/// name into its raw bytes (typically the VFS applying the supercede chain), this
/// decodes frame 0 (mandatory) and any further resolvable frames, mirroring the
/// game's semantics from <c>game_patch/bmpman/atx.cpp</c> (reimplemented, not copied).
/// </summary>
public static class AtxDecoder
{
    /// <summary>
    /// Decodes the descriptor's frames. Frame 0 must resolve and decode or a
    /// <see cref="TextureFormatException"/> is thrown; later frames are best-effort
    /// (a missing/undecodable frame stops the chain, and the result carries what was
    /// decoded so far). Animation fps is derived from the header frame time.
    /// </summary>
    /// <param name="descriptor">The parsed ATX.</param>
    /// <param name="resolveBytes">Maps a frame file name to its bytes, or null if unavailable.</param>
    public static DecodedTexture Decode(AtxDescriptor descriptor, Func<string, byte[]?> resolveBytes)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(resolveBytes);

        var images = new List<TextureImage>(descriptor.Frames.Count);
        for (int i = 0; i < descriptor.Frames.Count; i++)
        {
            string file = descriptor.Frames[i].File;
            byte[]? bytes = resolveBytes(file);
            if (bytes is null)
            {
                if (i == 0)
                {
                    throw new TextureFormatException($"ATX frame 0 '{file}' could not be resolved.");
                }

                break;
            }

            DecodedTexture frame;
            try
            {
                frame = TextureDecoder.Decode(file, bytes);
            }
            catch (TextureFormatException) when (i > 0)
            {
                break; // tolerate a broken later frame; keep the preview usable
            }

            images.Add(frame.Primary);
        }

        int fps = descriptor.FrameTimeMs > 0
            ? (int)Math.Round(1000.0 / descriptor.FrameTimeMs)
            : 0;

        // Static single-frame ATXes read as fps 0; anything animated reports a rate.
        if (descriptor.AnimationMode == AtxAnimationMode.Static || images.Count < 2)
        {
            fps = 0;
        }

        return new DecodedTexture(images, mipCount: 1, fps: fps, TextureFormatKind.Atx);
    }
}
