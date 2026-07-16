using System.Collections.Generic;
using Ged.Core.IO.Tex;
using Ged.Rendering.Graphics;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Gates for the GED-vs-original icon atlas source selection (pure, no GPU): the
/// composer blits a resolved original into its cell and falls back per-icon to the
/// GED-drawn cell when the original does not resolve.
/// </summary>
public sealed class IconSourceTests
{
    [Fact]
    public void Compose_Uses_Original_Where_Resolved_And_Falls_Back_Otherwise()
    {
        // A solid magenta 8x8 stand-in for the "original" Light icon; nothing else resolves.
        TextureImage magenta = Solid(8, 8, 255, 0, 255, 255);

        byte[] ged = IconAtlas.Build();
        byte[] composed = IconAtlas.Compose(icon => icon == EditorIcon.Light ? magenta : null);

        // The Light cell now reads magenta (the original won).
        (byte r, byte g, byte b, byte _) = CellCenter(composed, EditorIcon.Light);
        Assert.True(r > 200 && g < 60 && b > 200, $"Light cell not magenta: {r},{g},{b}");

        // The Trigger cell (mapped, but unresolved here) is unchanged from the GED base.
        Assert.Equal(CellCenter(ged, EditorIcon.Trigger), CellCenter(composed, EditorIcon.Trigger));

        // The particle Disc is never overridden even if a resolver would return one.
        byte[] discTest = IconAtlas.Compose(_ => magenta);
        Assert.Equal(CellCenter(ged, EditorIcon.Disc), CellCenter(discTest, EditorIcon.Disc));
    }

    [Fact]
    public void OriginalFileNames_Covers_The_Core_Categories()
    {
        IReadOnlyDictionary<EditorIcon, string> map = IconAtlas.OriginalFileNames;
        Assert.Equal("Icon_Light.tga", map[EditorIcon.Light]);
        Assert.Equal("Icon_Trigger.tga", map[EditorIcon.Trigger]);
        Assert.Equal("Icon_AFCorona.tga", map[EditorIcon.Corona]);
        // Meshes/particles have no RED icon → not mapped (GED fallback).
        Assert.False(map.ContainsKey(EditorIcon.Disc));
        Assert.False(map.ContainsKey(EditorIcon.Entity));
    }

    private static TextureImage Solid(int w, int h, byte r, byte g, byte b, byte a)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = r;
            px[i + 1] = g;
            px[i + 2] = b;
            px[i + 3] = a;
        }

        return new TextureImage(w, h, px);
    }

    private static (byte, byte, byte, byte) CellCenter(byte[] atlas, EditorIcon icon)
    {
        int col = (int)icon % IconAtlas.Cols;
        int row = (int)icon / IconAtlas.Cols;
        int x = (col * IconAtlas.Cell) + (IconAtlas.Cell / 2);
        int y = (row * IconAtlas.Cell) + (IconAtlas.Cell / 2);
        int i = ((y * IconAtlas.Width) + x) * 4;
        return (atlas[i], atlas[i + 1], atlas[i + 2], atlas[i + 3]);
    }
}
