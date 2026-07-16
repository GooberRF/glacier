using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Unit coverage for the RED-style shared-plane split substrate
/// (<see cref="PlaneRegistry"/> / <see cref="CsgSharedSplit"/>): planes intern to a
/// canonical id (orientation folded), three-plane intersections are byte-identical
/// regardless of triple order, and the accumulating clip-and-classify (now the sole
/// CSG path) resolves the canonical air/solid coincidence watertight.
/// </summary>
public sealed class SharedSplitTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void Intern_Folds_Orientation_And_Dedups_Coincident_Planes()
    {
        var reg = new PlaneRegistry();
        int a = reg.Intern(new CsgPlane(V(1, 0, 0), -5f));       // x = 5
        int b = reg.Intern(new CsgPlane(V(-1, 0, 0), 5f));       // same plane, flipped normal
        int c = reg.Intern(new CsgPlane(V(1, 0, 0), -5.0005f));  // 0.5 mm off → still the same surface
        int d = reg.Intern(new CsgPlane(V(1, 0, 0), -5.5f));     // 0.5 m off → a distinct plane

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.NotEqual(a, d);
        Assert.Equal(2, reg.Count);
    }

    [Fact]
    public void Intersect_Is_Order_Independent_And_Byte_Identical()
    {
        var reg = new PlaneRegistry();
        int x = reg.Intern(new CsgPlane(V(1, 0, 0), -3f)); // x = 3
        int y = reg.Intern(new CsgPlane(V(0, 1, 0), -4f)); // y = 4
        int z = reg.Intern(new CsgPlane(V(0, 0, 1), -5f)); // z = 5

        Vec3? p1 = reg.Intersect(x, y, z);
        Vec3? p2 = reg.Intersect(z, x, y);
        Vec3? p3 = reg.Intersect(y, z, x);

        Assert.NotNull(p1);
        Assert.Equal(V(3, 4, 5), p1!.Value);
        Assert.Equal(p1!.Value, p2!.Value); // byte-identical regardless of triple order
        Assert.Equal(p1!.Value, p3!.Value);
        Assert.Null(reg.Intersect(x, x, y)); // degenerate (repeated plane)
    }

    [Fact]
    public void Intersect_Returns_Null_For_Parallel_Planes()
    {
        var reg = new PlaneRegistry();
        int x = reg.Intern(new CsgPlane(V(1, 0, 0), -3f));
        int y = reg.Intern(new CsgPlane(V(0, 1, 0), -4f));
        int xParallel = reg.Intern(new CsgPlane(V(1, 0, 0), -9f)); // parallel to x → no unique point
        Assert.Null(reg.Intersect(x, xParallel, y));
    }

    [Fact]
    public void Accumulator_Keeps_The_Solid_Drops_The_Air_Panel_And_Stays_Watertight()
    {
        // Canonical coincidence fixture: air room, an air panel, and an identical solid.
        var scene = new List<Brush>
        {
            CompilerTestBrushes.MakeBox(1, V(0, 0, 0), 20, 20, 20, BrushFlags.Air, "roomtex"),
            CompilerTestBrushes.MakeBox(2, V(0, 0, 0), 6, 6, 6, BrushFlags.Air, "airtex"),
            CompilerTestBrushes.MakeBox(3, V(0, 0, 0), 6, 6, 6, BrushFlags.None, "solidtex"),
        };

        Geometry g = GeometryCompiler.Compile(scene, null, new CompileOptions { BuildSurfaces = false }).Geometry;

        // The accumulator keeps the solid's faces, drops the air panel's, and stays watertight.
        Assert.DoesNotContain(g.Faces, f => TexName(g, f) == "airtex");
        Assert.Empty(HoleDetector.Detect(g));
    }

    private static string TexName(Geometry g, Face f) =>
        f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : string.Empty;
}
