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
/// Item 4 — Fit (stretch a face selection's combined UV bbox to fill one [0,1] tile,
/// aspect-preserving) and item 3 — the UV Unwrap editor's box-select / edge / face
/// hit-testing. Pure Core ops, tested independent of the window.
/// </summary>
public sealed class UvFitSelectionTests
{
    // ---- Fit: single face -----------------------------------------------------

    [Fact]
    public void Fit_Square_Maps_Exactly_To_The_Unit_Tile()
    {
        // A 4-unit square away from the origin fills [0,1]x[0,1] edge-to-edge.
        Face f = Quad(10f, 10f, 14f, 14f);
        UvOps.FitFacesToTile(new[] { f });

        Assert.Equal(0f, MinU(f), 3);
        Assert.Equal(1f, MaxU(f), 3);
        Assert.Equal(0f, MinV(f), 3);
        Assert.Equal(1f, MaxV(f), 3);
    }

    [Fact]
    public void Fit_Wide_Rectangle_Uniform_Scales_And_Centres_The_Short_Axis()
    {
        // 2:1 landscape rectangle: U fills [0,1], V keeps aspect (0.5 tall) and centres.
        Face f = Quad(0f, 0f, 2f, 1f);
        UvOps.FitFacesToTile(new[] { f });

        Assert.Equal(0f, MinU(f), 3);
        Assert.Equal(1f, MaxU(f), 3);
        Assert.Equal(0.5f, MaxV(f) - MinV(f), 3);
        Assert.Equal(0.25f, MinV(f), 3);
        Assert.Equal(0.75f, MaxV(f), 3);
    }

    [Fact]
    public void Fit_Tall_Rectangle_Centres_The_Short_U_Axis()
    {
        // 1:2 portrait: V fills [0,1], U centres.
        Face f = Quad(0f, 0f, 1f, 2f);
        UvOps.FitFacesToTile(new[] { f });

        Assert.Equal(0f, MinV(f), 3);
        Assert.Equal(1f, MaxV(f), 3);
        Assert.Equal(0.25f, MinU(f), 3);
        Assert.Equal(0.75f, MaxU(f), 3);
    }

    [Fact]
    public void Fit_Circle_Keeps_Its_Shape()
    {
        // A ring of points on a circle of radius r keeps a circular outline after Fit,
        // because the scale is uniform. Sample the extents on both axes: equal diameters.
        var f = new Face();
        const int N = 16;
        for (int i = 0; i < N; i++)
        {
            float a = i * MathF.PI * 2f / N;
            f.Vertices.Add(new FaceVertex { TextureCoords = new Uv(5f + (2f * MathF.Cos(a)), 5f + (2f * MathF.Sin(a))) });
        }

        UvOps.FitFacesToTile(new[] { f });
        Assert.Equal(MaxU(f) - MinU(f), MaxV(f) - MinV(f), 3); // circle stays a circle
        Assert.Equal(1f, MaxU(f) - MinU(f), 3);                // and fills the tile
    }

    [Fact]
    public void Fit_Without_Aspect_Stretches_Both_Axes_To_The_Tile()
    {
        Face f = Quad(3f, 3f, 5f, 4f);
        UvOps.FitFacesToTile(new[] { f }, preserveAspect: false);

        Assert.Equal(0f, MinU(f), 3);
        Assert.Equal(1f, MaxU(f), 3);
        Assert.Equal(0f, MinV(f), 3);
        Assert.Equal(1f, MaxV(f), 3);
    }

    [Fact]
    public void Fit_Degenerate_Axis_Centres_At_Half()
    {
        // A horizontal line (zero V extent): U fits, V centres at 0.5.
        var f = new Face();
        f.Vertices.Add(new FaceVertex { TextureCoords = new Uv(2f, 9f) });
        f.Vertices.Add(new FaceVertex { TextureCoords = new Uv(6f, 9f) });

        UvOps.FitFacesToTile(new[] { f });
        Assert.All(f.Vertices, fv => Assert.Equal(0.5f, fv.TextureCoords.V, 3));
        Assert.Equal(1f, MaxU(f) - MinU(f), 3);
    }

    // ---- Fit: multi-face combined bbox ----------------------------------------

    [Fact]
    public void Fit_Uses_One_Combined_Bbox_Across_Multiple_Faces()
    {
        // Face A occupies U[0,1] V[0,1]; face B occupies U[2,3] V[0,0.5].
        // Combined bbox U[0,3] V[0,1] is 3:1 wide -> uniform scale 1/3; U fills [0,1] and the
        // shorter V (combined extent 1 -> 1/3 tall) centres, so offV = 1/3.
        Face a = Quad(0f, 0f, 1f, 1f);
        Face b = Quad(2f, 0f, 3f, 0.5f);
        UvOps.FitFacesToTile(new[] { a, b });

        // A anchors at the bbox min on U and centres on V.
        Assert.Equal(0f, MinU(a), 3);
        Assert.Equal(1f / 3f, MaxU(a), 3);
        Assert.Equal(1f / 3f, MinV(a), 3); // 0 -> 0*1/3 + offV(1/3)
        Assert.Equal(2f / 3f, MaxV(a), 3); // 1 -> 1*1/3 + 1/3

        // B sits at the far U end; both faces were 1 wide, so both are 1/3 wide after the
        // shared scale (relative world proportions preserved).
        Assert.Equal(2f / 3f, MinU(b), 3);
        Assert.Equal(1f, MaxU(b), 3);
        Assert.Equal(1f / 3f, MinV(b), 3);            // 0 -> 1/3
        Assert.Equal(1f / 3f + (0.5f / 3f), MaxV(b), 3); // 0.5 -> 0.5*1/3 + 1/3
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

        // The exact wiring the Texture panel's Fit button uses: compute once, apply per-face.
        var faces = new[] { brush.Geometry.Faces[0], brush.Geometry.Faces[1] };
        UvOps.UvFitTransform fit = UvOps.ComputeFitTransform(faces);
        ed.EditSelectedFaces("Fit UVs to tile", (g, fi) => UvOps.ApplyFit(g.Faces[fi], fit));

        List<Uv> after = SelectedUvs(ed.FindBrush(uid)!, 0, 1);
        Assert.True(after.Max(p => p.U) <= 1.001f && after.Min(p => p.U) >= -0.001f);
        Assert.True(after.Max(p => p.V) <= 1.001f && after.Min(p => p.V) >= -0.001f);
        Assert.NotEqual(before[0], after[0]); // it actually moved

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

    private static Face Quad(float minU, float minV, float maxU, float maxV)
    {
        var f = new Face();
        f.Vertices.Add(new FaceVertex { TextureCoords = new Uv(minU, minV) });
        f.Vertices.Add(new FaceVertex { TextureCoords = new Uv(maxU, minV) });
        f.Vertices.Add(new FaceVertex { TextureCoords = new Uv(maxU, maxV) });
        f.Vertices.Add(new FaceVertex { TextureCoords = new Uv(minU, maxV) });
        return f;
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
