using System.Collections.Generic;
using Avalonia.Media;
using Ged.App.Dialogs;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 3b(1..3) — the UV Unwrap editor's multi-face identification: the per-face Okabe–Ito colour
/// cycling (single face falls back to the toolbar line colour), the status readout format, and the
/// mixed-texture summary. Pure helpers, tested without the window.
/// </summary>
public sealed class UvFaceIdentityTests
{
    [Fact]
    public void FaceColor_SingleFace_Uses_The_Fallback_Line_Colour()
    {
        Color single = Colors.Lime;
        Assert.Equal(single, UvFaceIdentity.FaceColor(0, 1, single));
        Assert.Equal(single, UvFaceIdentity.FaceColor(-1, 5, single)); // unresolved ring index
    }

    [Fact]
    public void FaceColor_MultiFace_Gives_Each_Of_The_First_Eight_Faces_A_Distinct_Colour()
    {
        var seen = new HashSet<Color>();
        for (int i = 0; i < 8; i++)
        {
            Assert.True(seen.Add(UvFaceIdentity.FaceColor(i, 8, Colors.Lime)), $"face {i} colour must be unique");
        }

        Assert.Equal(8, seen.Count);
    }

    [Fact]
    public void FaceColor_Cycles_Past_The_Palette_Length()
    {
        Assert.Equal(UvFaceIdentity.FaceColor(0, 20, Colors.Lime), UvFaceIdentity.FaceColor(8, 20, Colors.Lime));
        Assert.Equal(UvFaceIdentity.FaceColor(1, 20, Colors.Lime), UvFaceIdentity.FaceColor(9, 20, Colors.Lime));
        Assert.NotEqual(UvFaceIdentity.FaceColor(7, 20, Colors.Lime), UvFaceIdentity.FaceColor(8, 20, Colors.Lime));
    }

    [Fact]
    public void FaceColor_MultiFace_Uses_The_Palette_Not_The_Fallback()
    {
        // Multi-face always uses the palette even if the fallback line colour happens to match one.
        Assert.Equal(UvFaceIdentity.Palette[0], UvFaceIdentity.FaceColor(0, 3, Colors.Black));
    }

    [Fact]
    public void Readout_Formats_Index_Brush_Face_And_Texture()
    {
        Assert.Equal("Face 3: brush 42 face 5 — wall.tga", UvFaceIdentity.Readout(2, 42, 5, "wall.tga"));
        Assert.Equal("Face 1: brush 1 face 0 — (no texture)", UvFaceIdentity.Readout(0, 1, 0, null));
    }

    [Fact]
    public void TextureSummary_Surfaces_A_Mixed_Selection()
    {
        Assert.Equal("a.tga", UvFaceIdentity.TextureSummary(new[] { "a.tga", "a.tga" }));
        Assert.Equal("mixed: a.tga, b.tga", UvFaceIdentity.TextureSummary(new[] { "a.tga", "b.tga", "a.tga" }));
    }
}
