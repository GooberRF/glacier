using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Fixtures for the convex decomposition of concave SOLID brushes (compiler-parity-notes.md — the CSG
/// watertightness cohort). A concave solid's own face planes build a solid-leaf BSP whose inside leaves are
/// its convex pieces; a foreign face is clipped against each piece it penetrates, so the silhouette is cut
/// with shared registry vertices (watertight by construction) instead of the crossing-face fallback that
/// left unshared split lines. These synthetic cases pin that behaviour: an L-shaped solid and a terrain-like
/// bumpy strip embedded in an air room compile watertight, and the brush is actually decomposed.
/// </summary>
public sealed class ConvexDecompositionTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void Concave_L_Solid_Embedded_In_Floor_Is_Decomposed_And_Watertight()
    {
        // Air room y∈[-10,10]; a concave L-shaped solid standing on the floor and sunk THROUGH it
        // (y∈[-15,-4]) so the floor air face is clipped around the L's footprint and the sunk part is
        // buried. The silhouette cut must seal the floor around the L — watertight only if the cut is
        // shared (the decomposition), not the crossing-face fallback.
        var lSection = new (float X, float Z)[] { (-6, -6), (-6, 6), (-2, 6), (-2, -2), (6, -2), (6, -6) };
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(0, 0, 0), 40, 20, 40, "room"),
            Prism(100, lSection, -15f, -4f, BrushFlags.None, "lsolid"),
        };

        CompiledLevel c = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = false });

        Assert.True(c.Report.DecomposedBrushes >= 1, "the concave L solid should be decomposed into convex pieces");
        Assert.True(c.Report.DecompMaxPieces >= 2, "an L decomposes into at least two convex pieces");
        Assert.Empty(HoleDetector.Detect(c.Geometry));
    }

    [Fact]
    public void Terrain_Like_Bumpy_Strip_Is_Decomposed_And_Watertight()
    {
        // A descending "staircase" solid — a terrain-like concave block (each step is a reflex edge) — sunk
        // into the floor of an air room. Its profile is Y-monotone so it fan-triangulates validly; the room
        // floor is clipped at the stepped silhouette and must stay watertight. The staircase decomposes into
        // one convex piece per step.
        var stair = new (float X, float Y)[]
        {
            (-12, -14), (-12, -5), (-8, -5), (-8, -7), (-4, -7), (-4, -9), (0, -9),
            (0, -11), (4, -11), (4, -13), (12, -13),
        };
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(0, 0, 0), 60, 20, 20, "room"),
            PrismXY(100, stair, -6f, 6f, BrushFlags.None, "terrain"),
        };

        CompiledLevel c = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = false });

        Assert.True(c.Report.DecomposedBrushes >= 1, "the staircase terrain should be decomposed");
        Assert.True(c.Report.DecompMaxPieces >= 3, "a multi-step staircase decomposes into several convex pieces");
        Assert.Empty(HoleDetector.Detect(c.Geometry));
    }

    [Fact]
    public void Air_Concave_Brush_Is_Not_Decomposed()
    {
        // An AIR concave brush is deliberately excluded from decomposition: its cells are open space, so
        // internal-plane cuts would land on surviving faces and over-split. It keeps the crossing-face path.
        var lSection = new (float X, float Z)[] { (-6, -6), (-6, 6), (-2, 6), (-2, -2), (6, -2), (6, -6) };
        var brushes = new List<Brush> { Prism(100, lSection, -3f, 3f, BrushFlags.Air, "lair") };

        CompiledLevel c = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = false });

        Assert.Equal(0, c.Report.DecomposedBrushes);
        Assert.Empty(HoleDetector.Detect(c.Geometry)); // still watertight via the fallback
    }

    // ---- prism builders (convex fan-triangulated caps, outward-oriented) ----

    private static Brush Prism(int uid, (float X, float Z)[] section, float yMin, float yMax, BrushFlags flags, string tex)
        => Extrude(uid, section.Select(p => (p.X, p.Z)).ToArray(), yMin, yMax, axis: 1, flags, tex);

    private static Brush PrismXY(int uid, (float X, float Y)[] section, float zMin, float zMax, BrushFlags flags, string tex)
        => Extrude(uid, section.Select(p => (p.X, p.Y)).ToArray(), zMin, zMax, axis: 2, flags, tex);

    private static Brush Extrude(int uid, (float A, float B)[] section, float lo, float hi, int axis, BrushFlags flags, string tex)
    {
        int n = section.Length;
        var verts = new List<Vec3>();
        Vec3 Make((float A, float B) s, float k) => axis == 1 ? V(s.A, k, s.B) : V(s.A, s.B, k);
        for (int i = 0; i < n; i++)
        {
            verts.Add(Make(section[i], lo));
        }

        for (int i = 0; i < n; i++)
        {
            verts.Add(Make(section[i], hi));
        }

        var centroid = new Vec3(0, 0, 0);
        foreach (Vec3 v in verts)
        {
            centroid = centroid.Add(v);
        }

        centroid = centroid.Scale(1f / verts.Count);

        var faces = new List<Face>();
        void AddFace(int[] idx)
        {
            var loop = idx.ToList();
            var poly = loop.Select(i => verts[i]).ToList();
            Vec3 nrm = CsgPlane.FromPolygon(poly.Select(p => new CsgVertex(p, default)).ToList()).Normal;
            var fc = new Vec3(0, 0, 0);
            foreach (Vec3 p in poly)
            {
                fc = fc.Add(p);
            }

            fc = fc.Scale(1f / poly.Count);
            if (nrm.Dot(fc.Sub(centroid)) < 0)
            {
                loop.Reverse(); // outward winding
            }

            faces.Add(new Face { Vertices = loop.Select(i => new FaceVertex { Index = i, TextureCoords = default }).ToList(), Texture = 0, Plane = default });
        }

        for (int i = 1; i < n - 1; i++)
        {
            AddFace(new[] { 0, i, i + 1 });         // low cap fan
            AddFace(new[] { n, n + i, n + i + 1 }); // high cap fan
        }

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            AddFace(new[] { i, j, n + j, n + i });  // side quad
        }

        var g = new Geometry();
        g.Vertices.AddRange(verts);
        g.Textures.Add(tex);
        g.Faces.AddRange(faces);
        return new Brush { Uid = uid, Position = V(0, 0, 0), Rotation = Mat3.Identity, Geometry = g, Flags = (uint)flags, Life = -1, State = BrushState.Normal };
    }
}
