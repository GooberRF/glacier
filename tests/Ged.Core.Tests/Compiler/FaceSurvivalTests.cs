using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Item 7 regression coverage: the compiler exports per-brush-face survival
/// (brush UID → local face index → survived) so the brush overlays can hide
/// faces the build clipped away (outside the level / consumed by CSG). Portal
/// and detail brushes never get an entry — they always draw in full.
/// </summary>
public sealed class FaceSurvivalTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    /// <summary>World-space X of a brush face's centroid (identity rotation).</summary>
    private static float FaceCentroidX(Brush b, int fi)
    {
        Face f = b.Geometry.Faces[fi];
        float sum = 0f;
        foreach (FaceVertex fv in f.Vertices)
        {
            sum += b.Geometry.Vertices[fv.Index].X;
        }

        return b.Position.X + (sum / f.Vertices.Count);
    }

    [Fact]
    public void Solid_Brush_Half_Outside_An_Air_Room_Marks_The_Outside_Face_Unsurvived()
    {
        // Air room x ∈ [-4,4]; solid pillar x ∈ [3,5] — its +X cap (x=5) is buried in
        // the solid world outside the room; every other face keeps a fragment inside.
        Brush room = CompilerTestBrushes.AirBox(1, V(0, 0, 0), 8, 6, 10);
        Brush pillar = CompilerTestBrushes.SolidBox(2, V(4, 0, 0), 2, 2, 2);
        CompiledLevel c = GeometryCompiler.Compile(new List<Brush> { room, pillar });

        Assert.True(c.SurvivingBrushFaces.TryGetValue(2, out bool[]? bits), "the pillar must have survival data");
        Assert.Equal(pillar.Geometry.Faces.Count, bits!.Length);

        for (int fi = 0; fi < pillar.Geometry.Faces.Count; fi++)
        {
            bool isOutsideCap = FaceCentroidX(pillar, fi) > 4.9f; // the x=5 cap
            Assert.True(bits[fi] == !isOutsideCap,
                $"face {fi} (centroid x {FaceCentroidX(pillar, fi):0.##}) expected {(isOutsideCap ? "clipped" : "survived")}");
        }

        Assert.Contains(bits, b => !b); // exactly the clipped cap exists
        Assert.Equal(1, bits.Count(b => !b));

        // The air room's own faces are the walls — all survive.
        Assert.True(c.SurvivingBrushFaces.TryGetValue(1, out bool[]? roomBits));
        Assert.All(roomBits!, s => Assert.True(s));
    }

    [Fact]
    public void Solid_Brush_Fully_Outside_The_Level_Has_No_Surviving_Faces()
    {
        Brush room = CompilerTestBrushes.AirBox(1, V(0, 0, 0), 8, 6, 8);
        Brush buried = CompilerTestBrushes.SolidBox(2, V(50, 0, 0), 2, 2, 2); // solid space
        CompiledLevel c = GeometryCompiler.Compile(new List<Brush> { room, buried });

        Assert.True(c.SurvivingBrushFaces.TryGetValue(2, out bool[]? bits));
        Assert.All(bits!, s => Assert.False(s));
    }

    [Fact]
    public void Solid_Brush_Fully_Inside_The_Room_Survives_Completely()
    {
        Brush room = CompilerTestBrushes.AirBox(1, V(0, 0, 0), 10, 6, 10);
        Brush pillar = CompilerTestBrushes.SolidBox(2, V(0, 0, 0), 2, 2, 2);
        CompiledLevel c = GeometryCompiler.Compile(new List<Brush> { room, pillar });

        Assert.True(c.SurvivingBrushFaces.TryGetValue(2, out bool[]? bits));
        Assert.All(bits!, s => Assert.True(s));
    }

    [Fact]
    public void Detail_Brushes_Get_No_Survival_Entry_And_Always_Draw()
    {
        Brush room = CompilerTestBrushes.AirBox(1, V(0, 0, 0), 10, 6, 10);
        Brush detail = CompilerTestBrushes.DetailBox(3, V(0, 0, 0), 2, 2, 2);
        CompiledLevel c = GeometryCompiler.Compile(new List<Brush> { room, detail });

        Assert.True(c.SurvivingBrushFaces.ContainsKey(1));
        Assert.False(c.SurvivingBrushFaces.ContainsKey(3),
            "detail/geoable brushes bypass the CSG solve and must draw in full");
    }
}
