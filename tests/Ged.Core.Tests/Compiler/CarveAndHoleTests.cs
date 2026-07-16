using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>Tests for brush-level Carve and the hole/leak detector.</summary>
public sealed class CarveAndHoleTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void Carve_Notch_Out_Of_A_Box_Adds_Cavity_Faces()
    {
        Brush target = CompilerTestBrushes.SolidBox(1, V(0, 0, 0), 10, 10, 10);
        Brush cutter = CompilerTestBrushes.SolidBox(2, V(5, 5, 0), 6, 6, 4); // corner notch

        Geometry? carved = CarveOps.Carve(target, cutter);

        Assert.NotNull(carved);
        // A notch splits faces and adds cavity walls -> more than a plain box's 6 faces.
        Assert.True(carved!.Faces.Count > 6, $"expected cavity faces, got {carved.Faces.Count}");
        Assert.All(carved.Faces, f => Assert.True(f.Vertices.Count >= 3));
    }

    [Fact]
    public void Carve_Disjoint_Brushes_Returns_Null()
    {
        Brush target = CompilerTestBrushes.SolidBox(1, V(0, 0, 0), 4, 4, 4);
        Brush cutter = CompilerTestBrushes.SolidBox(2, V(100, 0, 0), 4, 4, 4);

        Assert.Null(CarveOps.Carve(target, cutter));
    }

    [Fact]
    public void Validation_Warns_On_Solid_Portal_Brush()
    {
        Brush air = CompilerTestBrushes.AirBox(1, V(0, 0, 0), 20, 10, 20);
        Brush solidPortal = CompilerTestBrushes.MakeBox(2, V(0, 0, 0), 0.4f, 4, 4, BrushFlags.Portal, "wall");

        CompiledLevel c = GeometryCompiler.Compile(new List<Brush> { air, solidPortal });

        Assert.Contains(c.Report.Messages, m =>
            m.Severity == BuildSeverity.Warning && m.Text.Contains("portal brush should be air"));
    }

    [Fact]
    public void Hole_Detector_Sealed_Room_Has_No_Holes()
    {
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, V(0, 0, 0), 10, 10, 10) };
        CompiledLevel c = GeometryCompiler.Compile(brushes);

        Assert.Empty(HoleDetector.Detect(c.Geometry));
    }

    [Fact]
    public void Hole_Detector_Flags_An_Open_Boundary()
    {
        // Compile a sealed room, then delete a face to open it: the freed edges leak.
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, V(0, 0, 0), 10, 10, 10) };
        Geometry g = GeometryCompiler.Compile(brushes).Geometry;

        g.Faces.RemoveAt(0); // punch a hole

        List<Vec3> holes = HoleDetector.Detect(g);
        Assert.NotEmpty(holes);
    }
}
