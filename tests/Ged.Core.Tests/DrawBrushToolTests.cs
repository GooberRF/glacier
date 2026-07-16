using System;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 8: the draw-brush state machine — known rays in, exact box centers and
/// dimensions out, with grid snapping, plane selection (face-hit vs grid fallback),
/// degenerate-rectangle rejection, clean cancellation and the undo-integrated
/// commit path through <see cref="BrushEditor.CreateBrush"/>.
/// </summary>
public sealed class DrawBrushToolTests
{
    private static readonly Vec3 Down = new(0f, -1f, 0f);

    private static DrawBrushTool NewTool(
        float grid = 1f,
        bool snap = true,
        Func<(Vec3 Origin, Vec3 Dir), (Vec3 Point, Vec3 Normal)?>? provider = null)
    {
        var tool = new DrawBrushTool { GridSize = grid, SnapEnabled = snap, PlaneProvider = provider };
        tool.Begin();
        return tool;
    }

    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "test.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    /// <summary>Drives a tool through a full three-stage draw on the Y=0 grid plane (3×2×2 box).</summary>
    private static DrawBrushResult RunStandardDraw(DrawBrushTool tool)
    {
        Assert.Null(tool.Click(new Vec3(0.2f, 5f, 0.3f), Down));            // A → (0,0,0)
        Assert.Null(tool.Click(new Vec3(3.4f, 5f, 2.2f), Down));            // B → (3,0,2)
        DrawBrushResult? r = tool.Click(new Vec3(1.5f, 2.2f, -10f), new Vec3(0f, 0f, 1f)); // H → 2
        Assert.NotNull(r);
        return r!.Value;
    }

    // ---- (a) Full three-stage run on the grid plane with snapping ---------------

    [Fact]
    public void Full_Run_On_Grid_Plane_Snaps_To_Exact_Center_And_Dims()
    {
        DrawBrushTool tool = NewTool();

        // Stage 1: the vertical ray hits Y=0 at (0.2, 0, 0.3) and snaps to the origin.
        Assert.Equal(DrawBrushStage.BasePoint, tool.Stage);
        Assert.Null(tool.Click(new Vec3(0.2f, 5f, 0.3f), Down));
        Assert.Equal(DrawBrushClickOutcome.Advanced, tool.LastClick);
        Assert.Equal(DrawBrushStage.Rectangle, tool.Stage);

        // Stage 2: rubber-band to (3.4, ., 2.2) → snapped corner (3, 0, 2), W×D = 3×2.
        Assert.True(tool.Hover(new Vec3(3.4f, 5f, 2.2f), Down));
        Assert.Equal(3f, tool.WidthReadout, 3);
        Assert.Equal(2f, tool.DepthReadout, 3);
        Assert.Null(tool.Click(new Vec3(3.4f, 5f, 2.2f), Down));
        Assert.Equal(DrawBrushStage.Height, tool.Stage);

        // Stage 3: a horizontal ray at y=2.2 through the extrusion axis → snapped H=2.
        Assert.True(tool.Hover(new Vec3(1.5f, 2.2f, -10f), new Vec3(0f, 0f, 1f)));
        Assert.Equal(2f, tool.HeightReadout, 3);

        DrawBrushResult? r = tool.Click(new Vec3(1.5f, 2.2f, -10f), new Vec3(0f, 0f, 1f));
        Assert.NotNull(r);
        Assert.Equal(DrawBrushClickOutcome.Committed, tool.LastClick);
        Assert.True(r!.Value.Center.ApproxEquals(new Vec3(1.5f, 1f, 1f)), $"center was {r.Value.Center}");
        Assert.Equal(3f, r.Value.Width, 3);
        Assert.Equal(2f, r.Value.Height, 3);
        Assert.Equal(2f, r.Value.Depth, 3);

        // A commit resets to BasePoint: the tool stays armed for repeat draws.
        Assert.Equal(DrawBrushStage.BasePoint, tool.Stage);
    }

    [Fact]
    public void Stage2_Ghost_Is_A_Thin_Slab_And_Stage3_Ghost_Is_The_Extrusion()
    {
        DrawBrushTool tool = NewTool();
        tool.Click(new Vec3(0.2f, 5f, 0.3f), Down);
        tool.Hover(new Vec3(3.4f, 5f, 2.2f), Down);

        Assert.True(tool.GhostBox is (Vec3 c2, float w2, float h2, float d2)
            && c2.ApproxEquals(new Vec3(1.5f, 0f, 1f))
            && MathF.Abs(w2 - 3f) < 1e-4f && MathF.Abs(h2 - 0.05f) < 1e-4f && MathF.Abs(d2 - 2f) < 1e-4f);

        tool.Click(new Vec3(3.4f, 5f, 2.2f), Down);
        tool.Hover(new Vec3(1.5f, 2.2f, -10f), new Vec3(0f, 0f, 1f));
        Assert.True(tool.GhostBox is (Vec3 c3, float w3, float h3, float d3)
            && c3.ApproxEquals(new Vec3(1.5f, 1f, 1f))
            && MathF.Abs(w3 - 3f) < 1e-4f && MathF.Abs(h3 - 2f) < 1e-4f && MathF.Abs(d3 - 2f) < 1e-4f);
    }

    // ---- (b) Plane selection: face hit vs grid fallback -------------------------

    [Fact]
    public void Face_Plane_From_Provider_Hosts_The_Rectangle()
    {
        // A near-up face normal snaps to +Y; the work plane sits at the hit's Y=3.
        DrawBrushTool tool = NewTool(provider: _ => (new Vec3(2f, 3f, 1f), new Vec3(0.1f, 0.95f, 0.05f)));

        Assert.Null(tool.Click(new Vec3(0f, 10f, 0f), Down));
        Assert.Equal(DrawBrushStage.Rectangle, tool.Stage);

        // Stage 2 rays intersect the elevated plane: (5.4, ., 2.6) → (5, 3, 3).
        Assert.True(tool.Hover(new Vec3(5.4f, 10f, 2.6f), Down));
        Assert.Equal(3f, tool.WidthReadout, 3);
        Assert.Equal(2f, tool.DepthReadout, 3);
        Assert.Null(tool.Click(new Vec3(5.4f, 10f, 2.6f), Down));

        DrawBrushResult? r = tool.Click(new Vec3(3.5f, 4.8f, -10f), new Vec3(0f, 0f, 1f)); // t=1.8 → H=2
        Assert.NotNull(r);
        Assert.True(r!.Value.Center.ApproxEquals(new Vec3(3.5f, 4f, 2f)), $"center was {r.Value.Center}");
        Assert.Equal(3f, r.Value.Width, 3);
        Assert.Equal(2f, r.Value.Height, 3);
        Assert.Equal(2f, r.Value.Depth, 3);
    }

    [Fact]
    public void Side_Facing_Plane_Extrudes_Along_Its_Dominant_Axis()
    {
        // A wall-ish normal snaps to +X: the rect spans Y/Z at X=5, the box extrudes +X.
        DrawBrushTool tool = NewTool(provider: _ => (new Vec3(5f, 0.2f, 0.4f), new Vec3(0.9f, 0.1f, 0f)));

        Assert.Null(tool.Click(new Vec3(-10f, 0f, 0f), new Vec3(1f, 0f, 0f)));
        Assert.True(tool.Hover(new Vec3(-10f, 2.2f, 3.1f), new Vec3(1f, 0f, 0f))); // → (5, 2, 3)
        Assert.Equal(2f, tool.WidthReadout, 3); // in-plane extents: Y then Z
        Assert.Equal(3f, tool.DepthReadout, 3);
        Assert.Null(tool.Click(new Vec3(-10f, 2.2f, 3.1f), new Vec3(1f, 0f, 0f)));

        DrawBrushResult? r = tool.Click(new Vec3(7.3f, 1f, -10f), new Vec3(0f, 0f, 1f)); // t=2.3 → H=2
        Assert.NotNull(r);
        Assert.True(r!.Value.Center.ApproxEquals(new Vec3(6f, 1f, 1.5f)), $"center was {r.Value.Center}");
        Assert.Equal(2f, r.Value.Width, 3);  // world X extent = the extrusion
        Assert.Equal(2f, r.Value.Height, 3); // world Y extent
        Assert.Equal(3f, r.Value.Depth, 3);  // world Z extent
    }

    [Fact]
    public void No_Provider_Hit_Falls_Back_To_The_Grid_Plane()
    {
        DrawBrushTool tool = NewTool(provider: _ => null);
        Assert.True(tool.Hover(new Vec3(0.2f, 5f, 0.3f), Down));
        Assert.Equal(new Vec3(0f, 0f, 0f), tool.PreviewPoint);

        DrawBrushTool bare = NewTool(); // no provider at all
        Assert.True(bare.Hover(new Vec3(1.6f, 5f, 0.3f), Down));
        Assert.Equal(new Vec3(2f, 0f, 0f), bare.PreviewPoint);
    }

    [Fact]
    public void Ray_Parallel_To_The_Work_Plane_Does_Not_Advance()
    {
        // An ortho Top-view ray runs parallel to the Y-up grid plane: no intersection
        // exists, so the tool reports PlaneUnreachable and stays put (the App shows
        // "use the perspective view" — perspective-only for that plane for now).
        DrawBrushTool tool = NewTool();
        var alongPlane = new Vec3(1f, 0f, 0f);

        Assert.False(tool.Hover(new Vec3(0f, 5f, 0f), alongPlane));
        Assert.Null(tool.Click(new Vec3(0f, 5f, 0f), alongPlane));
        Assert.Equal(DrawBrushClickOutcome.PlaneUnreachable, tool.LastClick);
        Assert.Equal(DrawBrushStage.BasePoint, tool.Stage);
    }

    // ---- (c) Snapping honors the grid size and can be disabled ------------------

    [Fact]
    public void Snap_Uses_The_Configured_Grid_Size()
    {
        DrawBrushTool tool = NewTool(grid: 0.5f);
        Assert.True(tool.Hover(new Vec3(0.3f, 5f, 0.85f), Down));
        Assert.True(tool.PreviewPoint!.Value.ApproxEquals(new Vec3(0.5f, 0f, 1f)));
    }

    [Fact]
    public void Snap_Disabled_Keeps_Continuous_Coordinates()
    {
        DrawBrushTool tool = NewTool(snap: false);
        Assert.True(tool.Hover(new Vec3(0.3f, 5f, 0.85f), Down));
        Assert.True(tool.PreviewPoint!.Value.ApproxEquals(new Vec3(0.3f, 0f, 0.85f)));

        Assert.Null(tool.Click(new Vec3(0.3f, 5f, 0.85f), Down));
        Assert.True(tool.Hover(new Vec3(1.7f, 5f, 2.15f), Down));
        Assert.Equal(1.4f, tool.WidthReadout, 3);
        Assert.Equal(1.3f, tool.DepthReadout, 3);
    }

    // ---- (d) A degenerate rectangle does not advance to Height ------------------

    [Fact]
    public void Degenerate_Rectangle_Stays_In_Rectangle_Stage()
    {
        DrawBrushTool tool = NewTool();
        tool.Click(new Vec3(0.2f, 5f, 0.3f), Down); // A → (0,0,0)

        // Corner B snaps onto corner A: zero width and depth → rejected.
        Assert.Null(tool.Click(new Vec3(0.4f, 5f, 0.3f), Down));
        Assert.Equal(DrawBrushClickOutcome.DegenerateRectangle, tool.LastClick);
        Assert.Equal(DrawBrushStage.Rectangle, tool.Stage);

        // Zero along one axis only is still degenerate.
        Assert.Null(tool.Click(new Vec3(2.1f, 5f, 0.2f), Down)); // (2, 0, 0): depth 0
        Assert.Equal(DrawBrushClickOutcome.DegenerateRectangle, tool.LastClick);
        Assert.Equal(DrawBrushStage.Rectangle, tool.Stage);

        // A real corner advances.
        Assert.Null(tool.Click(new Vec3(2.1f, 5f, 1.9f), Down));
        Assert.Equal(DrawBrushClickOutcome.Advanced, tool.LastClick);
        Assert.Equal(DrawBrushStage.Height, tool.Stage);
    }

    [Fact]
    public void Zero_Height_Click_Does_Not_Commit()
    {
        DrawBrushTool tool = NewTool();
        tool.Click(new Vec3(0.2f, 5f, 0.3f), Down);
        tool.Click(new Vec3(3.4f, 5f, 2.2f), Down);

        // A ray crossing the axis at the base level snaps to H=0: a zero-height box
        // would be degenerate geometry, so the commit click is rejected.
        Assert.Null(tool.Click(new Vec3(1.5f, 0.2f, -10f), new Vec3(0f, 0f, 1f)));
        Assert.Equal(DrawBrushClickOutcome.ZeroHeight, tool.LastClick);
        Assert.Equal(DrawBrushStage.Height, tool.Stage);
    }

    // ---- (e) Cancel at every stage leaves the document untouched ----------------

    [Fact]
    public void Cancel_At_Every_Stage_Leaves_Document_Untouched()
    {
        EditorDocument doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        DrawBrushTool tool = NewTool();

        // Stage 1 (BasePoint).
        tool.Hover(new Vec3(0.2f, 5f, 0.3f), Down);
        tool.Cancel();
        Assert.Equal(DrawBrushStage.Idle, tool.Stage);

        // Stage 2 (Rectangle).
        tool.Begin();
        tool.Click(new Vec3(0.2f, 5f, 0.3f), Down);
        tool.Hover(new Vec3(3.4f, 5f, 2.2f), Down);
        tool.Cancel();
        Assert.Equal(DrawBrushStage.Idle, tool.Stage);

        // Stage 3 (Height).
        tool.Begin();
        tool.Click(new Vec3(0.2f, 5f, 0.3f), Down);
        tool.Click(new Vec3(3.4f, 5f, 2.2f), Down);
        tool.Hover(new Vec3(1.5f, 2.2f, -10f), new Vec3(0f, 0f, 1f));
        tool.Cancel();
        Assert.Equal(DrawBrushStage.Idle, tool.Stage);

        // ESC with the tool already idle is likewise a no-op.
        tool.Cancel();

        Assert.False(doc.Undo.CanUndo);
        Assert.Equal(0, doc.Undo.Position);
        Assert.Empty(ed.Brushes);
        Assert.False(doc.IsDirty);
    }

    // ---- (f) Commit path through BrushEditor.CreateBrush ------------------------

    [Fact]
    public void Commit_Creates_One_Undo_Entry_Selects_And_Preserves_Template_Flags()
    {
        EditorDocument doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        DrawBrushResult r = RunStandardDraw(NewTool());

        // The App's commit: clone the panel template, override only the dimensions.
        var template = new BrushCreateParams { Air = true, Detail = true, Life = 42, Texture = "Rck_Custom.tga" };
        var p = new BrushCreateParams
        {
            Shape = BrushShape.Box,
            Width = r.Width,
            Height = r.Height,
            Depth = r.Depth,
            Texture = template.Texture,
            Air = template.Air,
            Portal = template.Portal,
            Detail = template.Detail,
            EmitsSteam = template.EmitsSteam,
            Geoable = template.Geoable,
            Life = template.Life,
        };

        int posBefore = doc.Undo.Position;
        int uid = ed.CreateBrush(p, r.Center, Mat3.Identity);
        ed.SelectBrush(uid);

        Assert.Equal(posBefore + 1, doc.Undo.Position); // exactly ONE undo entry
        Assert.Equal(uid, Assert.Single(ed.SelectedBrushes));

        Brush b = Assert.IsType<Brush>(ed.FindBrush(uid));
        Assert.True(b.Position.ApproxEquals(new Vec3(1.5f, 1f, 1f)));
        Assert.True(BrushTransform.Dimensions(b).ApproxEquals(new Vec3(3f, 2f, 2f)));
        Assert.NotEqual(0u, b.Flags & (uint)BrushFlags.Air);
        Assert.NotEqual(0u, b.Flags & (uint)BrushFlags.Detail);
        Assert.Equal(0u, b.Flags & (uint)BrushFlags.Portal);
        Assert.Equal(42, b.Life);
        Assert.Contains("Rck_Custom.tga", b.Geometry.Textures);

        doc.Undo.Undo();
        Assert.Empty(ed.Brushes);
    }

    // ---- GeometryRaycast (the App's stage-1 plane provider) ---------------------

    [Fact]
    public void GeometryRaycast_Hits_The_Nearest_Face_With_Its_Normal()
    {
        Geometry box = BrushFactory.Box(2f, 2f, 2f, 0, 0, 0, "tex");
        (Vec3 Point, Vec3 Normal)? hit = GeometryRaycast.Raycast(box, new Vec3(0.25f, 5f, 0.25f), Down);

        Assert.NotNull(hit);
        Assert.True(hit!.Value.Point.ApproxEquals(new Vec3(0.25f, 1f, 0.25f)), $"point was {hit.Value.Point}");
        Assert.True(hit.Value.Normal.ApproxEquals(new Vec3(0f, 1f, 0f)), $"normal was {hit.Value.Normal}");
    }

    [Fact]
    public void GeometryRaycast_Misses_Return_Null()
    {
        Geometry box = BrushFactory.Box(2f, 2f, 2f, 0, 0, 0, "tex");
        Assert.Null(GeometryRaycast.Raycast(box, new Vec3(5f, 5f, 5f), Down));
    }

    [Fact]
    public void GeometryRaycast_Flips_A_Back_Facing_Normal_Toward_The_Viewer()
    {
        // From inside the box a downward ray hits the floor whose outward normal
        // points down; the returned normal opposes the ray (the visible side).
        Geometry box = BrushFactory.Box(2f, 2f, 2f, 0, 0, 0, "tex");
        (Vec3 Point, Vec3 Normal)? hit = GeometryRaycast.Raycast(box, new Vec3(0f, 0f, 0f), Down);

        Assert.NotNull(hit);
        Assert.True(hit!.Value.Point.ApproxEquals(new Vec3(0f, -1f, 0f)));
        Assert.True(hit.Value.Normal.ApproxEquals(new Vec3(0f, 1f, 0f)));
    }
}
