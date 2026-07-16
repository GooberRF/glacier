using Ged.Core.Editing;

namespace Ged.Rendering.Scene;

/// <summary>
/// The editor aid for faces flagged <c>show_sky</c>: a single semitransparent sky-blue
/// diffuse texture with the literal "SHOW SKY" text rasterized INTO it, bound as the
/// face's diffuse and mapped across the face by its UVs. The label is therefore part of
/// the surface — it moves and tilts with the face — rather than a camera-facing billboard.
/// Shared by the compiled emitter (<see cref="SceneBuilder"/>) and the brush overlay
/// (<see cref="BrushEmitter"/>) so a sky face looks identical in every mode. The texture is
/// rasterized once per scene (cached under <see cref="TextureKey"/> in the scene's inline
/// textures) and resolved by the GPU layer without a VFS lookup.
/// </summary>
public static class SkyFaceAid
{
    /// <summary>Synthetic inline-texture key for the shared baked "SHOW SKY" diffuse.</summary>
    public const string TextureKey = "$sky:SHOW SKY";

    /// <summary>The text baked into the sky-face texture.</summary>
    public const string LabelText = "SHOW SKY";

    // Sky-blue fill at ~35% alpha (90/255); the glyphs are baked in opaque white.
    private const byte FillR = 90;
    private const byte FillG = 160;
    private const byte FillB = 245;
    private const byte FillA = 90;

    /// <summary>
    /// Registers the baked "SHOW SKY" diffuse in the scene's inline textures (once) and
    /// returns its key, so a sky-face batch can bind it as its diffuse.
    /// </summary>
    public static string EnsureTexture(RenderScene scene)
    {
        if (!scene.InlineTextures.ContainsKey(TextureKey))
        {
            scene.InlineTextures[TextureKey] = BuildTexture();
        }

        return TextureKey;
    }

    /// <summary>
    /// Builds the semitransparent sky-blue RGBA texture with "SHOW SKY" rasterized into it.
    /// Deterministic (drives the LabelBitmap glyph rasterizer), so it caches cleanly.
    /// </summary>
    public static InlineTexture BuildTexture()
    {
        // Rasterize the label to get an opaque-white glyph mask over a plate, then recolour:
        // glyph pixels → opaque white; everything else → the sky-blue semitransparent fill.
        (int w, int h, byte[] label) = LabelBitmap.Render(LabelText, scale: 3, pad: 6);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            bool glyph = label[o] > 127; // LabelBitmap glyphs are white (R=255); plate is R=0
            if (glyph)
            {
                rgba[o] = 255;
                rgba[o + 1] = 255;
                rgba[o + 2] = 255;
                rgba[o + 3] = 255;
            }
            else
            {
                rgba[o] = FillR;
                rgba[o + 1] = FillG;
                rgba[o + 2] = FillB;
                rgba[o + 3] = FillA;
            }
        }

        return new InlineTexture(w, h, rgba);
    }
}
