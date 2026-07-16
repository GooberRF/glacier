using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Closes the build → render loop: compile a from-scratch level, apply it to a
/// document, and verify the renderer's scene builder turns the compiled static
/// geometry into drawable batches with lightmap pages — i.e. GED's own compiled
/// output is renderable exactly like a stock-compiled level.
/// </summary>
public sealed class CompiledGeometryRenderTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void Compiled_Level_Produces_Render_Batches_And_Lightmaps()
    {
        var brushes = new List<Brush>
        {
            Box(1, V(0, 0, 0), 16, 10, 16, BrushFlags.Air),
            Box(2, V(0, 0, 0), 3, 10, 3, BrushFlags.None), // pillar
        };

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Sections.Add(new RflSection((uint)SectionType.Brushes, System.Array.Empty<byte>())
        {
            Content = new BrushesSection { Brushes = brushes },
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));

        CompiledLevel built = GeometryBuildService.BuildAndApply(rfl);
        Assert.True(built.Report.Faces > 6);

        RenderScene scene = SceneBuilder.Build(rfl, new SceneBuildOptions());

        // Static geometry became drawable batches with real triangles.
        Assert.NotEmpty(scene.Batches);
        Assert.True(scene.Batches.Sum(b => b.Indices.Count) > 0);
        Assert.NotEmpty(scene.Lightmaps);
    }

    private static Brush Box(int uid, Vec3 c, float w, float h, float d, BrushFlags flags) => new()
    {
        Uid = uid,
        Position = c,
        Rotation = Mat3.Identity,
        Geometry = BrushFactory.Box(w, h, d, 0, 0, 0, "wall"),
        Flags = (uint)flags,
        Life = -1,
    };
}
