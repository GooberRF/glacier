using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// RED's brush boolean is STRICTLY LINEAR IN TIME ORDER: each brush's operation is
/// applied over the accumulated result of every earlier brush. A later AIR brush
/// carves open space out of an earlier SOLID; a later SOLID fills earlier open space
/// and its faces become walls. These two directions are the foundation of the
/// coincident-face resolution (which operand is "world" vs "brush"), so they are
/// locked here independently of any corpus level.
/// </summary>
public sealed class TimeOrderParityTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    private static string Tex(Geometry g, Face f) =>
        f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : string.Empty;

    private static Vec3 Centroid(Geometry g, Face f)
    {
        var c = new Vec3(0, 0, 0);
        foreach (FaceVertex v in f.Vertices)
        {
            c = c.Add(g.Vertices[v.Index]);
        }

        return f.Vertices.Count == 0 ? c : c.Scale(1f / f.Vertices.Count);
    }

    [Fact]
    public void Later_Air_Carves_Earlier_Solid()
    {
        // Open room, then a solid floor slab, then a LATER air box that overlaps the
        // slab's +X half (open to the room, so the cavity is not sealed): the later air
        // must carve that half away, leaving the -X half solid.
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(0, 0, 0), 80, 80, 80, "room"),
            CompilerTestBrushes.SolidBox(2, V(0, 0, 0), 40, 4, 40, "floor"),
            CompilerTestBrushes.AirBox(3, V(15, 0, 0), 30, 20, 40, "carve"),
        };

        Geometry g = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = false }).Geometry;

        bool TopFloor(Face f) =>
            Tex(g, f) == "floor" && f.Plane.Normal.Y > 0.99f &&
            System.MathF.Abs(Centroid(g, f).Y - 2f) < 0.5f;

        // The -X half of the slab top survives (solid there); the +X half is carved away.
        Assert.Contains(g.Faces, f => TopFloor(f) && Centroid(g, f).X < -3f);
        Assert.DoesNotContain(g.Faces, f => TopFloor(f) && Centroid(g, f).X > 3f);
    }

    [Fact]
    public void Later_Solid_Fills_Earlier_Air()
    {
        // Open room, then a LATER solid box filling the +X region: the solid's own faces
        // become the walls (its -X face bounds the still-open -X part of the room).
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(0, 0, 0), 80, 80, 80, "room"),
            CompilerTestBrushes.SolidBox(2, V(20, 0, 0), 20, 20, 20, "block"),
        };

        Geometry g = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = false }).Geometry;

        // The solid's -X wall (at x=10, facing into open space) is present with its texture.
        Assert.Contains(g.Faces, f =>
            Tex(g, f) == "block" && f.Plane.Normal.X < -0.99f &&
            System.MathF.Abs(Centroid(g, f).X - 10f) < 0.5f);

        // And the block genuinely fills: no open-facing "block" wall exists in the solid's interior.
        Assert.DoesNotContain(g.Faces, f =>
            Tex(g, f) == "block" && Centroid(g, f).X > 12f && f.Plane.Normal.X < -0.99f);
    }
}
