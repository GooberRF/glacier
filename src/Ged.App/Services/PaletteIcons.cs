using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Ged.Core.Editor;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;

namespace Ged.App.Services;

/// <summary>
/// Renders <see cref="IconAtlas"/> cells into small Avalonia bitmaps for the object-palette
/// rows, tinted with the same per-kind colour the viewport billboards use so the palette
/// glyph matches the object's in-scene identity. Every placeable kind maps to its viewport
/// icon (see <see cref="TryFor"/>). By default the icons come from GED's own drawn atlas
/// (no game resources, so the palette works before any RF install is mounted); when the
/// "use original object icons" setting is on the shell hands us the composed atlas via
/// <see cref="Configure"/> and rows render untinted (white) to match the viewport. Bitmaps
/// are cached per (icon, tint); <see cref="Configure"/> clears the cache so a settings flip
/// re-resolves live.
/// </summary>
internal static class PaletteIcons
{
    private static readonly Dictionary<(EditorIcon, uint), Bitmap> Cache = new();
    private static byte[]? _atlas;
    private static bool _useOriginalIcons;

    private static readonly uint White = Palette.Rgba(255, 255, 255, 255);

    /// <summary>
    /// Points the palette icons at a specific atlas source and tint mode, matching the viewport.
    /// Pass the composed original-icon atlas with <paramref name="useOriginalIcons"/> true (rows
    /// render untinted), or null with false to use GED's own drawn atlas per-kind-tinted. Clears
    /// the cached bitmaps so the next <see cref="TryFor"/> re-resolves. Cheap — no GPU, pure CPU
    /// blit — so the shell calls it whenever the icon atlas is (re)built.
    /// </summary>
    public static void Configure(byte[]? atlas, bool useOriginalIcons)
    {
        _atlas = atlas;
        _useOriginalIcons = useOriginalIcons;
        Cache.Clear();
    }

    /// <summary>
    /// The palette icon for an object kind (its viewport billboard glyph), or null for a kind
    /// with no billboard mapping. Uses the per-kind billboard tint, or white when original icons
    /// are in use — mirroring <c>SceneBuilder</c>'s billboard emission.
    /// </summary>
    public static Bitmap? TryFor(LevelObjectKind kind)
    {
        if (BillboardFor(kind) is not BillboardKind bk)
        {
            return null;
        }

        uint tint = _useOriginalIcons ? White : Palette.BillboardTint(bk);
        return For(SceneBuilder.IconForKind(bk), tint);
    }

    /// <summary>The viewport billboard category for a placeable object kind (null when unmapped).</summary>
    private static BillboardKind? BillboardFor(LevelObjectKind kind) => kind switch
    {
        LevelObjectKind.Entity => BillboardKind.Entity,
        LevelObjectKind.Item => BillboardKind.Item,
        LevelObjectKind.Clutter => BillboardKind.Clutter,
        LevelObjectKind.Light => BillboardKind.Light,
        LevelObjectKind.Trigger => BillboardKind.Trigger,
        LevelObjectKind.AmbientSound => BillboardKind.AmbientSound,
        LevelObjectKind.MpRespawnPoint => BillboardKind.Respawn,
        LevelObjectKind.ParticleEmitter => BillboardKind.ParticleEmitter,
        LevelObjectKind.BoltEmitter => BillboardKind.BoltEmitter,
        LevelObjectKind.NavPoint => BillboardKind.NavPoint,
        LevelObjectKind.Target => BillboardKind.Target,
        LevelObjectKind.CutsceneCamera => BillboardKind.CutsceneCamera,
        LevelObjectKind.Decal => BillboardKind.Decal,
        LevelObjectKind.GeoRegion => BillboardKind.Region,
        LevelObjectKind.GasRegion => BillboardKind.GasRegion,
        LevelObjectKind.ClimbRegion => BillboardKind.ClimbRegion,
        LevelObjectKind.PushRegion => BillboardKind.PushRegion,
        LevelObjectKind.RoomEffect => BillboardKind.RoomEffect,
        LevelObjectKind.Eax => BillboardKind.Eax,
        LevelObjectKind.MeshObject => BillboardKind.Clutter, // scene builder draws mesh objects with the clutter glyph
        LevelObjectKind.NoteObject => BillboardKind.Note,
        LevelObjectKind.CoronaObject => BillboardKind.Corona,
        LevelObjectKind.BagObject => BillboardKind.Bag,
        LevelObjectKind.PlayerStart => BillboardKind.PlayerStart,
        _ => null,
    };

    /// <summary>A 32×32 bitmap of one atlas cell, tinted (R8G8B8A8-packed tint, R in the low byte).</summary>
    public static Bitmap For(EditorIcon icon, uint tintRgba)
    {
        if (Cache.TryGetValue((icon, tintRgba), out Bitmap? cached))
        {
            return cached;
        }

        _atlas ??= IconAtlas.Build();
        byte tr = (byte)(tintRgba & 0xFF);
        byte tg = (byte)((tintRgba >> 8) & 0xFF);
        byte tb = (byte)((tintRgba >> 16) & 0xFF);

        int col = (int)icon % IconAtlas.Cols;
        int row = (int)icon / IconAtlas.Cols;
        int cell = IconAtlas.Cell;
        var pixels = new byte[cell * cell * 4];

        for (int y = 0; y < cell; y++)
        {
            int src = ((((row * cell) + y) * IconAtlas.Width) + (col * cell)) * 4;
            int dst = y * cell * 4;
            for (int x = 0; x < cell; x++, src += 4, dst += 4)
            {
                // Atlas is RGBA straight; tint like the billboard shader (RGB × tint), then
                // emit BGRA premultiplied for Avalonia.
                int a = _atlas[src + 3];
                int r = _atlas[src] * tr / 255;
                int g = _atlas[src + 1] * tg / 255;
                int b = _atlas[src + 2] * tb / 255;
                pixels[dst] = (byte)(b * a / 255);
                pixels[dst + 1] = (byte)(g * a / 255);
                pixels[dst + 2] = (byte)(r * a / 255);
                pixels[dst + 3] = (byte)a;
            }
        }

        var bmp = new WriteableBitmap(new PixelSize(cell, cell), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            if (fb.RowBytes == cell * 4)
            {
                Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
            }
            else
            {
                for (int y = 0; y < cell; y++)
                {
                    Marshal.Copy(pixels, y * cell * 4, fb.Address + (y * fb.RowBytes), cell * 4);
                }
            }
        }

        Cache[(icon, tintRgba)] = bmp;
        return bmp;
    }
}
