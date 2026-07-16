using System.Collections.Generic;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// RED's watertight realisation — leaf-based boundary EXTRACTION (<see cref="CompileOptions.UseLeafExtraction"/> /
/// <see cref="WorldBsp.Extract"/>, compiler-parity-notes.md). Instead of routing original brush faces through the
/// world tree (the <see cref="CompileOptions.UseWorldBsp"/> route-faces clip that regresses via T-junction storms),
/// the boundary face set is EXTRACTED from the tree: one face per open|solid leaf portal, attributed back to the
/// source brush face on that plane. These fixtures pin that the construction is watertight on the canonical shapes
/// (coincident air/solid, embedded solid, abutting rooms, a concave L-room, a staircase, an overhang) and that every
/// emitted face is attributed to a real source face (no texture loss).
/// </summary>
public sealed class LeafExtractionTests
{
    private readonly ITestOutputHelper _out;

    public LeafExtractionTests(ITestOutputHelper output) => _out = output;

    private static CompileOptions Opts() => new() { BuildSurfaces = false, UseLeafExtraction = true };

    public static IEnumerable<object[]> Fixtures()
    {
        // Canonical air/solid pair: air room + coincident air panel + coincident solid block.
        yield return new object[]
        {
            "air/solid pair",
            new List<Brush>
            {
                CompilerTestBrushes.MakeBox(1, new Vec3(0, 0, 0), 20, 20, 20, BrushFlags.Air, "roomtex"),
                CompilerTestBrushes.MakeBox(2, new Vec3(0, 0, 0), 6, 6, 6, BrushFlags.Air, "airtex"),
                CompilerTestBrushes.MakeBox(3, new Vec3(0, 0, 0), 6, 6, 6, BrushFlags.None, "solidtex"),
            },
        };

        // A solid block sunk into an air room floor (extent / overhang case).
        yield return new object[]
        {
            "embedded block",
            new List<Brush>
            {
                CompilerTestBrushes.MakeBox(1, new Vec3(0, 0, 0), 40, 20, 40, BrushFlags.Air, "room"),
                CompilerTestBrushes.MakeBox(2, new Vec3(0, -8, 0), 10, 10, 10, BrushFlags.None, "block"),
            },
        };

        // Two abutting air rooms sharing an interior wall plane (the coincident-wall case).
        yield return new object[]
        {
            "two abutting rooms",
            new List<Brush>
            {
                CompilerTestBrushes.MakeBox(1, new Vec3(-10, 0, 0), 20, 20, 20, BrushFlags.Air, "r1"),
                CompilerTestBrushes.MakeBox(2, new Vec3(10, 0, 0), 20, 20, 20, BrushFlags.Air, "r2"),
            },
        };

        // Concave L-room: two overlapping air boxes (a horizontal bar + a vertical leg).
        yield return new object[]
        {
            "L-room",
            new List<Brush>
            {
                CompilerTestBrushes.MakeBox(1, new Vec3(0, 0, 0), 40, 10, 20, BrushFlags.Air, "bar"),
                CompilerTestBrushes.MakeBox(2, new Vec3(-15, 10, 0), 10, 30, 20, BrushFlags.Air, "leg"),
            },
        };

        // Descending staircase of solid steps inside an air room (concave solid terrain).
        var stair = new List<Brush>
        {
            CompilerTestBrushes.MakeBox(1, new Vec3(0, 0, 0), 60, 40, 20, BrushFlags.Air, "room"),
        };
        for (int i = 0; i < 4; i++)
        {
            stair.Add(CompilerTestBrushes.MakeBox(
                10 + i, new Vec3(-20 + (i * 10), -15 + (i * 3), 0), 10, 6 + (i * 6), 20, BrushFlags.None, "step"));
        }

        yield return new object[] { "staircase", stair };

        // Overhang: an air ceiling slab that extends past the room's side wall (the classic overhang leak).
        yield return new object[]
        {
            "overhang",
            new List<Brush>
            {
                CompilerTestBrushes.MakeBox(1, new Vec3(0, 0, 0), 40, 20, 20, BrushFlags.Air, "room"),
                CompilerTestBrushes.MakeBox(2, new Vec3(10, 12, 0), 60, 4, 20, BrushFlags.Air, "ledge"),
            },
        };
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Extraction_Is_Watertight_And_Attributed(string label, List<Brush> scene)
    {
        CompiledLevel c = GeometryCompiler.Compile(scene, null, Opts());
        BuildReport r = c.Report;
        int holes = HoleDetector.Detect(c.Geometry).Count;
        _out.WriteLine(
            $"{label,-20} holes={holes} faces={c.Geometry.Faces.Count} portals={r.ExtractedPortals} " +
            $"byExtent={r.AttributedByContainment} byNearest={r.AttributedByNearest} unattr={r.Unattributed} " +
            $"used={r.LeafExtractionUsed}");

        Assert.True(r.LeafExtractionUsed, $"{label}: leaf extraction should be active");
        Assert.True(r.ExtractedPortals > 0, $"{label}: should extract boundary portals");
        Assert.Equal(0, r.Unattributed); // every emitted face must inherit a real source face (texture fidelity)
        Assert.Empty(HoleDetector.Detect(c.Geometry)); // watertight by construction
    }

    [Fact]
    public void Every_Extracted_Face_Has_A_Texture()
    {
        var scene = new List<Brush>
        {
            CompilerTestBrushes.MakeBox(1, new Vec3(0, 0, 0), 20, 20, 20, BrushFlags.Air, "wallA"),
            CompilerTestBrushes.MakeBox(2, new Vec3(0, 0, 0), 6, 6, 6, BrushFlags.None, "blockB"),
        };
        CompiledLevel c = GeometryCompiler.Compile(scene, null, Opts());
        foreach (Face f in c.Geometry.Faces)
        {
            Assert.True(f.Texture >= 0, "every extracted world face resolves to a real texture id");
        }
    }

    /// <summary>
    /// Texel-level attribution fidelity (mission item 4): an extracted wall's per-vertex UVs must re-project
    /// EXACTLY from the source brush face it inherits (same plane ⇒ same planar mapping). A single air box
    /// extracts to its six original faces, so every extracted corner coincides with a per-brush (source-UV)
    /// corner and its UV must match to float precision — the top regression risk, pinned.
    /// </summary>
    [Fact]
    public void Extracted_Uvs_Match_The_Source_Face_Exactly()
    {
        var scene = new List<Brush> { CompilerTestBrushes.MakeBox(1, new Vec3(3, -2, 5), 24, 16, 20, BrushFlags.Air, "wall") };

        Geometry pb = GeometryCompiler.Compile(scene, null, new CompileOptions { BuildSurfaces = false }).Geometry;
        Geometry ex = GeometryCompiler.Compile(scene, null, Opts()).Geometry;

        int checkedVerts = 0;
        foreach (Face fe in ex.Faces)
        {
            if (fe.Texture < 0)
            {
                continue;
            }

            // Match the per-brush face on the same plane (the source face carries the authored UVs verbatim).
            Face? fp = FindCoplanarFace(pb, fe);
            Assert.NotNull(fp);
            foreach (FaceVertex ve in fe.Vertices)
            {
                Vec3 pe = ex.Vertices[ve.Index];
                Uv? srcUv = UvAt(pb, fp!, pe);
                Assert.True(srcUv is not null, "extracted corner should coincide with a source-face corner");
                Assert.True(MathF.Abs(ve.TextureCoords.U - srcUv!.Value.U) < 1e-3f
                    && MathF.Abs(ve.TextureCoords.V - srcUv.Value.V) < 1e-3f,
                    $"UV re-projection diverged: extracted ({ve.TextureCoords.U},{ve.TextureCoords.V}) vs source ({srcUv.Value.U},{srcUv.Value.V})");
                checkedVerts++;
            }
        }

        Assert.True(checkedVerts >= 24, "should have compared UVs on all six box faces");
    }

    private static Face? FindCoplanarFace(Geometry g, Face target)
    {
        foreach (Face f in g.Faces)
        {
            if (f.Texture >= 0
                && f.Plane.Normal.Dot(target.Plane.Normal) > 0.999f
                && MathF.Abs(f.Plane.Offset - target.Plane.Offset) < 1e-2f)
            {
                return f;
            }
        }

        return null;
    }

    private static Uv? UvAt(Geometry g, Face f, Vec3 pos)
    {
        foreach (FaceVertex v in f.Vertices)
        {
            if (g.Vertices[v.Index].Sub(pos).LengthSquared() < 1e-4f)
            {
                return v.TextureCoords;
            }
        }

        return null;
    }
}
