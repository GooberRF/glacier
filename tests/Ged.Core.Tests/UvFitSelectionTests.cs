using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 4 — Fit (world-projection: planar-project a face selection onto its shared plane and
/// normalise the projected footprint to one [0,1] tile) and item 3 — the UV Unwrap editor's
/// box-select / edge / face hit-testing. Pure Core ops, tested independent of the window.
/// </summary>
public sealed class UvFitSelectionTests
{
    // ---- Fit: world-projection (ignore aspect = "Fit") ------------------------

    [Fact]
    public void Fit_AxisAlignedQuad_Maps_CornerToCorner_On_The_Tile()
    {
        // "Fit" (ignore aspect): a single axis-aligned 6x3 world quad, wherever it sits, maps 1:1
        // onto the whole tile — each corner lands on a tile corner (V is negated: +Y is up in world,
        // down in the tile, matching the box/planar map orientation).
        (Geometry g, Face f) = QuadXY(10f, 20f, 16f, 23f); // 6 wide x 3 tall, +Z facing
        UvOps.FitFacesToTile(new[] { (g, f) }, preserveAspect: false);

        AssertUv(f.Vertices[0].TextureCoords, 0f, 1f); // (x0,y0)
        AssertUv(f.Vertices[1].TextureCoords, 1f, 1f); // (x1,y0)
        AssertUv(f.Vertices[2].TextureCoords, 1f, 0f); // (x1,y1)
        AssertUv(f.Vertices[3].TextureCoords, 0f, 0f); // (x0,y1)
    }

    [Fact]
    public void Fit_Reprojects_Ignoring_Rotated_Prior_Uvs()
    {
        // A square world quad whose PRIOR UVs are rotated/skewed in UV space. Fit is geometry-based,
        // so it discards those and re-projects to an axis-aligned 0..1 layout — this is the
        // deliberate single-face behaviour change (the old bbox-normalising Fit preserved rotation).
        (Geometry g, Face f) = QuadXY(0f, 0f, 4f, 4f);
        float r = 37f * MathF.PI / 180f, cos = MathF.Cos(r), sin = MathF.Sin(r);
        Uv[] seeds = { new(0.2f, 0.1f), new(0.9f, 0.3f), new(0.8f, 0.95f), new(0.1f, 0.7f) };
        for (int i = 0; i < f.Vertices.Count; i++)
        {
            float su = seeds[i].U - 0.5f, sv = seeds[i].V - 0.5f;
            f.Vertices[i].TextureCoords = new Uv(0.5f + (su * cos) - (sv * sin), 0.5f + (su * sin) + (sv * cos));
        }

        UvOps.FitFacesToTile(new[] { (g, f) }, preserveAspect: false);

        // Axis-aligned 0..1 (square → both axes fill), independent of the prior rotation.
        Assert.Equal(0f, MinU(f), 3);
        Assert.Equal(1f, MaxU(f), 3);
        Assert.Equal(0f, MinV(f), 3);
        Assert.Equal(1f, MaxV(f), 3);
        foreach (FaceVertex fv in f.Vertices)
        {
            Assert.True(MathF.Abs(fv.TextureCoords.U) < 1e-3f || MathF.Abs(fv.TextureCoords.U - 1f) < 1e-3f, "U axis-aligned");
            Assert.True(MathF.Abs(fv.TextureCoords.V) < 1e-3f || MathF.Abs(fv.TextureCoords.V - 1f) < 1e-3f, "V axis-aligned");
        }
    }

    [Fact]
    public void Fit_Degenerate_Projected_Axis_Centres_At_Half()
    {
        // A world-horizontal edge (all corners at the same Y): the projected V extent is zero, so
        // V centres at 0.5 and U fills the tile — degenerate axes never throw or produce NaN.
        var g = new Geometry();
        g.Vertices.Add(new Vec3(2f, 5f, 0f));
        g.Vertices.Add(new Vec3(6f, 5f, 0f));
        var f = new Face();
        f.Vertices.Add(new FaceVertex { Index = 0 });
        f.Vertices.Add(new FaceVertex { Index = 1 });
        g.Faces.Add(f);

        UvOps.FitFacesToTile(new[] { (g, f) });
        Assert.All(f.Vertices, fv => Assert.Equal(0.5f, fv.TextureCoords.V, 3));
        Assert.Equal(1f, MaxU(f) - MinU(f), 3);
    }

    // ---- Fit: keep-aspect ("Fit (Keep Aspect)") -------------------------------

    [Fact]
    public void Fit_KeepAspect_Centres_The_Short_Axis_On_A_2to1_Group()
    {
        // Keep-aspect on a 2:1 world group (4 wide x 2 tall): U fills the tile, V keeps world aspect
        // (0.5 tall) and centres.
        (Geometry g, Face f) = QuadXY(0f, 0f, 4f, 2f);
        UvOps.FitFacesToTile(new[] { (g, f) }, preserveAspect: true);

        Assert.Equal(0f, MinU(f), 3);
        Assert.Equal(1f, MaxU(f), 3);
        Assert.Equal(0.5f, MaxV(f) - MinV(f), 3);
        Assert.Equal(0.25f, MinV(f), 3);
        Assert.Equal(0.75f, MaxV(f), 3);
    }

    [Fact]
    public void Fit_IgnoreAspect_And_KeepAspect_Differ_On_A_NonSquare_Group()
    {
        // Same 2:1 world quad both ways. Ignore-aspect fills BOTH axes to 1.0; keep-aspect uniform-
        // scales, so the short V axis spans only 0.5 (world aspect preserved) — exactly what the two
        // side-by-side buttons must do differently.
        (Geometry gi, Face fi) = QuadXY(0f, 0f, 4f, 2f);
        (Geometry gk, Face fk) = QuadXY(0f, 0f, 4f, 2f);
        UvOps.FitFacesToTile(new[] { (gi, fi) }, preserveAspect: false);
        UvOps.FitFacesToTile(new[] { (gk, fk) }, preserveAspect: true);

        Assert.Equal(1f, MaxU(fi) - MinU(fi), 3);
        Assert.Equal(1f, MaxV(fi) - MinV(fi), 3); // ignore-aspect: V fills the tile too
        Assert.Equal(1f, MaxU(fk) - MinU(fk), 3);
        Assert.Equal(0.5f, MaxV(fk) - MinV(fk), 3); // keep-aspect: V stays 2:1 and centres
    }

    [Fact]
    public void Fit_Transform_Is_Pinned_To_The_Projection_Behaviour()
    {
        // Replaces the old "keep-aspect byte-identical to the historical bbox Fit" pin: Fit is now
        // world-projection based. Pin the exact transform for a known +Z 4x2 quad in both modes.
        (Geometry g, Face f) = QuadXY(0f, 0f, 4f, 2f);
        UvOps.UvFitTransform keep = UvOps.ComputeFitTransform(new[] { (g, f) }, preserveAspect: true);
        Assert.Equal(0, keep.UAxis);          // +Z facing → project X,Y
        Assert.Equal(1, keep.VAxis);
        Assert.Equal(0f, keep.MinU, 6);       // projected min X
        Assert.Equal(-2f, keep.MinV, 6);      // projected min (-Y)
        Assert.Equal(0.25f, keep.ScaleU, 6);  // uniform scale 1/4 (U is the larger extent)
        Assert.Equal(0.25f, keep.ScaleV, 6);
        Assert.Equal(0f, keep.OffsetU, 6);
        Assert.Equal(0.25f, keep.OffsetV, 6); // centres the 0.5-tall V axis

        // The default overload (no argument) equals the explicit keep-aspect one.
        UvOps.UvFitTransform def = UvOps.ComputeFitTransform(new[] { (g, f) });
        Assert.Equal(keep, def);

        // Ignore-aspect is a genuinely different transform: V is scaled to fill the tile.
        UvOps.UvFitTransform ignore = UvOps.ComputeFitTransform(new[] { (g, f) }, preserveAspect: false);
        Assert.Equal(0.5f, ignore.ScaleV, 6); // dv=2 → 1/2
        Assert.Equal(0f, ignore.OffsetV, 6);
        Assert.NotEqual(keep, ignore);
    }

    // ---- Fit: multi-face + non-coplanar ---------------------------------------

    [Fact]
    public void Fit_TwoCoplanarQuads_Split_The_Tile_With_A_Continuous_Seam()
    {
        // Two coplanar 6m-wide quads side by side (a shared edge at x=6): "Fit" spans the pair across
        // the tile so each occupies exactly half in U, continuous at the seam.
        var g = new Geometry();
        Face a = AppendQuadXY(g, 0f, 0f, 6f, 6f);
        Face b = AppendQuadXY(g, 6f, 0f, 12f, 6f);
        UvOps.FitFacesToTile(new[] { (g, a), (g, b) }, preserveAspect: false);

        // A on the left half, B on the right half.
        Assert.Equal(0f, MinU(a), 3);
        Assert.Equal(0.5f, MaxU(a), 3);
        Assert.Equal(0.5f, MinU(b), 3);
        Assert.Equal(1f, MaxU(b), 3);
        Assert.Equal(0f, MinV(a), 3);
        Assert.Equal(1f, MaxV(a), 3);

        // The shared edge is continuous: A's x=6 corners and B's x=6 corners land on identical UVs.
        AssertUv(a.Vertices[1].TextureCoords, b.Vertices[0].TextureCoords); // (6,0)
        AssertUv(a.Vertices[2].TextureCoords, b.Vertices[3].TextureCoords); // (6,6)
        AssertUv(a.Vertices[1].TextureCoords, 0.5f, 1f);
        AssertUv(a.Vertices[2].TextureCoords, 0.5f, 0f);
    }

    [Fact]
    public void Fit_NonCoplanar_Pair_Produces_Finite_Sane_Uvs()
    {
        // Two perpendicular faces sharing an edge (a box corner). The area-weighted normal falls
        // between them; the projection is still well-defined and every resulting UV is finite and in
        // the tile (a face edge-on to the chosen plane collapses to a line — sane, not NaN/Inf).
        var g = new Geometry();
        Face a = AppendQuadXY(g, 0f, 0f, 2f, 2f); // +Z facing
        Face b = AppendQuadXZ(g, 0f, 0f, 2f, 2f); // ±Y facing, shares edge (0,0,0)-(2,0,0)
        UvOps.FitFacesToTile(new[] { (g, a), (g, b) }, preserveAspect: false);

        foreach (Face f in new[] { a, b })
        {
            foreach (FaceVertex fv in f.Vertices)
            {
                Assert.True(float.IsFinite(fv.TextureCoords.U) && float.IsFinite(fv.TextureCoords.V));
                Assert.InRange(fv.TextureCoords.U, -0.001f, 1.001f);
                Assert.InRange(fv.TextureCoords.V, -0.001f, 1.001f);
            }
        }
    }

    // ---- Fit: WORLD-space (per-brush transforms) ------------------------------

    [Fact]
    public void Fit_TwoSideBySideBrushes_Tile_Continuously_In_World_Space()
    {
        // The owner's exact scenario: two IDENTICAL 2m-wide box faces with identical LOCAL geometry,
        // but one brush at world X=0 and the other at world X=2. Brush-LOCAL projection would map both
        // onto (nearly) the whole tile and overlap; WORLD projection splits them — left U[0,0.5], right
        // U[0.5,1] — with a continuous seam.
        (Geometry ga, Face fa) = QuadXY(0f, 0f, 2f, 2f); // local front face, +Z, X[0,2]
        (Geometry gb, Face fb) = QuadXY(0f, 0f, 2f, 2f); // identical LOCAL geometry
        var left = new UvOps.FitFace(ga, fa, Mat3.Identity, new Vec3(0f, 0f, 0f));
        var right = new UvOps.FitFace(gb, fb, Mat3.Identity, new Vec3(2f, 0f, 0f));

        UvOps.FitFacesToTile(new[] { left, right }, preserveAspect: false);

        Assert.Equal(0f, MinU(fa), 3);   // left brush → left half
        Assert.Equal(0.5f, MaxU(fa), 3);
        Assert.Equal(0.5f, MinU(fb), 3); // right brush → right half
        Assert.Equal(1f, MaxU(fb), 3);
        Assert.Equal(0f, MinV(fa), 3);
        Assert.Equal(1f, MaxV(fa), 3);

        // Continuous seam: the left face's world-X=2 corners equal the right face's world-X=2 corners.
        AssertUv(fa.Vertices[1].TextureCoords, fb.Vertices[0].TextureCoords);
        AssertUv(fa.Vertices[2].TextureCoords, fb.Vertices[3].TextureCoords);
        AssertUv(fa.Vertices[1].TextureCoords, 0.5f, 1f);
        AssertUv(fa.Vertices[2].TextureCoords, 0.5f, 0f);
    }

    [Fact]
    public void Fit_RotatedBrush_Projects_By_The_Face_World_Orientation()
    {
        // A brush rotated 90° about Y turns its local +Z (front) face into a world +X-facing face. World
        // projection must use the WORLD normal (axes Z,Y) and fill the tile; a brush-local +Z projection
        // (axes X,Y) would be edge-on in world and collapse U to a degenerate 0.5 line.
        (Geometry g, Face f) = QuadXY(0f, 0f, 2f, 2f);
        Mat3 ry = Mat3Math.RotationY(MathF.PI / 2f);
        UvOps.FitFacesToTile(new[] { new UvOps.FitFace(g, f, ry, new Vec3(0f, 0f, 0f)) }, preserveAspect: false);

        Assert.Equal(0f, MinU(f), 3); // fills both axes (not collapsed) — world orientation drove it
        Assert.Equal(1f, MaxU(f), 3);
        Assert.Equal(0f, MinV(f), 3);
        Assert.Equal(1f, MaxV(f), 3);
        foreach (FaceVertex fv in f.Vertices)
        {
            Assert.True(MathF.Abs(fv.TextureCoords.U) < 1e-3f || MathF.Abs(fv.TextureCoords.U - 1f) < 1e-3f, "U axis-aligned");
            Assert.True(MathF.Abs(fv.TextureCoords.V) < 1e-3f || MathF.Abs(fv.TextureCoords.V - 1f) < 1e-3f, "V axis-aligned");
        }
    }

    [Fact]
    public void Fit_TwoRotatedBrushes_Place_On_The_Correct_World_Side()
    {
        // Two identical brushes both rotated 90° about Y (faces now +X-facing, projected onto world
        // Z,Y). Brush A at the origin spans world Z[-2,0]; brush B offset to world Z[-4,-2]. The brush
        // further along -Z (B, smaller world Z) must land on the LEFT, A on the RIGHT, seam continuous.
        (Geometry ga, Face fa) = QuadXY(0f, 0f, 2f, 2f);
        (Geometry gb, Face fb) = QuadXY(0f, 0f, 2f, 2f);
        Mat3 ry = Mat3Math.RotationY(MathF.PI / 2f);
        var a = new UvOps.FitFace(ga, fa, ry, new Vec3(0f, 0f, 0f));
        var b = new UvOps.FitFace(gb, fb, ry, new Vec3(0f, 0f, -2f));

        UvOps.FitFacesToTile(new[] { a, b }, preserveAspect: false);

        Assert.Equal(0.5f, MinU(fa), 3); // A (larger world Z) → right half
        Assert.Equal(1f, MaxU(fa), 3);
        Assert.Equal(0f, MinU(fb), 3);   // B (smaller world Z) → left half
        Assert.Equal(0.5f, MaxU(fb), 3);

        // Continuous seam at world Z=-2: A's near corners equal B's far corners.
        AssertUv(fa.Vertices[1].TextureCoords, fb.Vertices[0].TextureCoords);
        AssertUv(fa.Vertices[2].TextureCoords, fb.Vertices[3].TextureCoords);
    }

    // ---- Fit: undo (App-parity path through BrushEditor) ----------------------

    [Fact]
    public void Fit_Through_BrushEditor_Is_A_Single_Undoable_Step()
    {
        var doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int uid = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Texture = "a.tga" }, default, Mat3.Identity);
        ed.SetMode(EditMode.Face);
        ed.SelectFace(uid, 0);
        ed.SelectFace(uid, 1, additive: true);

        Brush brush = ed.FindBrush(uid)!;
        List<Uv> before = SelectedUvs(brush, 0, 1);

        // The exact wiring the Texture panel's Fit button uses: compute once from geometry, apply per face.
        var faces = new[] { (brush.Geometry, brush.Geometry.Faces[0]), (brush.Geometry, brush.Geometry.Faces[1]) };
        UvOps.UvFitTransform fit = UvOps.ComputeFitTransform(faces);
        ed.EditSelectedFaces("Fit UVs to tile", (g, fi) => UvOps.ApplyFit(g, g.Faces[fi], fit));

        List<Uv> after = SelectedUvs(ed.FindBrush(uid)!, 0, 1);
        Assert.True(after.Max(p => p.U) <= 1.001f && after.Min(p => p.U) >= -0.001f);
        Assert.True(after.Max(p => p.V) <= 1.001f && after.Min(p => p.V) >= -0.001f);
        Assert.NotEqual(before, after); // it actually moved

        doc.Undo.Undo(); // one step restores everything
        List<Uv> restored = SelectedUvs(ed.FindBrush(uid)!, 0, 1);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].U, restored[i].U, 4);
            Assert.Equal(before[i].V, restored[i].V, 4);
        }
    }

    // ---- Selection: box membership --------------------------------------------

    private static (List<Uv> Uvs, List<IReadOnlyList<int>> Rings) TwoSquares()
    {
        var uvs = new List<Uv>
        {
            new(0, 0), new(1, 0), new(1, 1), new(0, 1), // face A
            new(3, 0), new(4, 0), new(4, 1), new(3, 1), // face B
        };
        var rings = new List<IReadOnlyList<int>> { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 } };
        return (uvs, rings);
    }

    [Fact]
    public void VerticesInRect_Selects_Only_Enclosed_Points()
    {
        (List<Uv> uvs, _) = TwoSquares();
        List<int> hit = UvSelection.VerticesInRect(uvs, -0.1f, -0.1f, 1.1f, 1.1f);
        Assert.Equal(new[] { 0, 1, 2, 3 }, hit);
    }

    [Fact]
    public void EdgeVerticesInRect_Needs_Both_Endpoints_Inside()
    {
        (List<Uv> uvs, List<IReadOnlyList<int>> rings) = TwoSquares();
        // A thin band over the bottom edge of face A catches only edge (0,1).
        List<int> hit = UvSelection.EdgeVerticesInRect(uvs, rings, -0.1f, -0.1f, 1.1f, 0.1f);
        Assert.Equal(new[] { 0, 1 }, hit.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void FaceVerticesInRect_Needs_All_Corners_Inside()
    {
        (List<Uv> uvs, List<IReadOnlyList<int>> rings) = TwoSquares();
        // Box over face A only.
        List<int> hit = UvSelection.FaceVerticesInRect(uvs, rings, -0.1f, -0.1f, 1.1f, 1.1f);
        Assert.Equal(new[] { 0, 1, 2, 3 }, hit.OrderBy(i => i).ToArray());

        // A box clipping only part of face B catches nothing (whole-island rule).
        Assert.Empty(UvSelection.FaceVerticesInRect(uvs, rings, 3.4f, -0.1f, 5f, 1.1f));
    }

    // ---- Selection: click picking ---------------------------------------------

    [Fact]
    public void NearestVertex_Finds_The_Closest_Within_Radius()
    {
        (List<Uv> uvs, _) = TwoSquares();
        Assert.Equal(0, UvSelection.NearestVertex(uvs, 0.05f, 0.05f, 0.2f));
        Assert.Equal(-1, UvSelection.NearestVertex(uvs, 2.0f, 0.5f, 0.2f)); // nothing close
    }

    [Fact]
    public void NearestEdge_Picks_The_Segment_Under_The_Point()
    {
        (List<Uv> uvs, List<IReadOnlyList<int>> rings) = TwoSquares();
        (int a, int b) = UvSelection.NearestEdge(uvs, rings, 0.5f, 0.02f, 0.2f);
        Assert.Equal(new[] { 0, 1 }, new[] { a, b }.OrderBy(i => i).ToArray());
        Assert.Equal((-1, -1), UvSelection.NearestEdge(uvs, rings, 2.0f, 0.5f, 0.2f));
    }

    [Fact]
    public void FaceContainingPoint_Uses_Point_In_Polygon()
    {
        (List<Uv> uvs, List<IReadOnlyList<int>> rings) = TwoSquares();
        Assert.Equal(0, UvSelection.FaceContainingPoint(uvs, rings, 0.5f, 0.5f));
        Assert.Equal(1, UvSelection.FaceContainingPoint(uvs, rings, 3.5f, 0.5f));
        Assert.Equal(-1, UvSelection.FaceContainingPoint(uvs, rings, 2.0f, 0.5f)); // between islands
    }

    // ---- Helpers --------------------------------------------------------------

    /// <summary>A standalone +Z-facing world quad (its own <see cref="Geometry"/>) spanning X[x0,x1] Y[y0,y1].</summary>
    private static (Geometry G, Face F) QuadXY(float x0, float y0, float x1, float y1, float z = 0f)
    {
        var g = new Geometry();
        return (g, AppendQuadXY(g, x0, y0, x1, y1, z));
    }

    /// <summary>Appends a +Z-facing world quad to <paramref name="g"/>: corners (x0,y0)(x1,y0)(x1,y1)(x0,y1).</summary>
    private static Face AppendQuadXY(Geometry g, float x0, float y0, float x1, float y1, float z = 0f)
    {
        int b = g.Vertices.Count;
        g.Vertices.Add(new Vec3(x0, y0, z));
        g.Vertices.Add(new Vec3(x1, y0, z));
        g.Vertices.Add(new Vec3(x1, y1, z));
        g.Vertices.Add(new Vec3(x0, y1, z));
        var f = new Face { Plane = new RfPlane(new Vec3(0f, 0f, 1f), z) };
        for (int i = 0; i < 4; i++)
        {
            f.Vertices.Add(new FaceVertex { Index = b + i });
        }

        g.Faces.Add(f);
        return f;
    }

    /// <summary>Appends a ±Y-facing world quad to <paramref name="g"/> on the Y=<paramref name="y"/> plane.</summary>
    private static Face AppendQuadXZ(Geometry g, float x0, float z0, float x1, float z1, float y = 0f)
    {
        int b = g.Vertices.Count;
        g.Vertices.Add(new Vec3(x0, y, z0));
        g.Vertices.Add(new Vec3(x1, y, z0));
        g.Vertices.Add(new Vec3(x1, y, z1));
        g.Vertices.Add(new Vec3(x0, y, z1));
        var f = new Face { Plane = new RfPlane(new Vec3(0f, 1f, 0f), y) };
        for (int i = 0; i < 4; i++)
        {
            f.Vertices.Add(new FaceVertex { Index = b + i });
        }

        g.Faces.Add(f);
        return f;
    }

    private static void AssertUv(Uv actual, float expectedU, float expectedV)
    {
        Assert.Equal(expectedU, actual.U, 3);
        Assert.Equal(expectedV, actual.V, 3);
    }

    private static void AssertUv(Uv actual, Uv expected)
    {
        Assert.Equal(expected.U, actual.U, 3);
        Assert.Equal(expected.V, actual.V, 3);
    }

    private static float MinU(Face f) => f.Vertices.Min(v => v.TextureCoords.U);

    private static float MaxU(Face f) => f.Vertices.Max(v => v.TextureCoords.U);

    private static float MinV(Face f) => f.Vertices.Min(v => v.TextureCoords.V);

    private static float MaxV(Face f) => f.Vertices.Max(v => v.TextureCoords.V);

    private static List<Uv> SelectedUvs(Brush b, params int[] faceIndices) =>
        faceIndices.SelectMany(fi => b.Geometry.Faces[fi].Vertices.Select(v => v.TextureCoords)).ToList();

    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "t.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }
}
