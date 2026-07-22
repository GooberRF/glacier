using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>CPU-only tests for brush scene emission and the pick registry (no GPU).</summary>
public sealed class BrushEmitterTests
{
    private static Brush Box() => new()
    {
        Uid = 7,
        Rotation = Mat3.Identity,
        Geometry = BrushFactory.Box(2, 2, 2, 0, 0, 0, "test.tga"),
    };

    [Fact]
    public void Append_Box_Emits_Triangles_And_Twelve_Edges()
    {
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { Box() }, BrushPickGranularity.Brush);

        Assert.NotEmpty(scene.Batches);
        Assert.Equal(12, scene.TotalTriangleCount); // 6 quads -> 12 tris
        Assert.Equal(12, scene.Lines.Count); // a box has 12 unique edges (deduped)
    }

    [Fact]
    public void Brush_Granularity_Tags_Faces_With_Whole_Brush_Pick()
    {
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { Box() }, BrushPickGranularity.Brush);
        uint anyPick = scene.Batches[0].Vertices[0].PickId;
        PickId id = PickId.Decode(anyPick);
        Assert.Equal(PickKind.Brush, id.Kind);
        Assert.Equal(7, id.Index);
    }

    [Fact]
    public void Face_Granularity_Registers_Faces_And_Decodes()
    {
        var scene = new RenderScene();
        BrushPickRegistry reg = BrushEmitter.Append(scene, new[] { Box() }, BrushPickGranularity.Face);

        uint pick = scene.Batches[0].Vertices[0].PickId;
        PickId id = PickId.Decode(pick);
        Assert.Equal(PickKind.BrushFace, id.Kind);
        Assert.True(reg.TryResolveFace(id.Index, out int uid, out int face));
        Assert.Equal(7, uid);
        Assert.InRange(face, 0, 5);
    }

    [Fact]
    public void Vertex_Granularity_Emits_Dot_Billboards_And_Registers_Vertices()
    {
        var scene = new RenderScene();
        BrushPickRegistry reg = BrushEmitter.Append(scene, new[] { Box() }, BrushPickGranularity.Vertex);

        Assert.Equal(8, scene.Billboards.Count(b => b.Kind == BillboardKind.Vertex));
        Assert.Equal(8, reg.Vertices.Count);

        Billboard dot = scene.Billboards.First(b => b.Kind == BillboardKind.Vertex);
        PickId id = PickId.Decode(dot.PickId.Encode());
        Assert.Equal(PickKind.BrushVertex, id.Kind);
        Assert.True(reg.TryResolveVertex(id.Index, out int uid, out int vertex));
        Assert.Equal(7, uid);
        Assert.InRange(vertex, 0, 7);
    }

    [Fact]
    public void Selected_Brush_Uses_Highlight_Colour()
    {
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { Box() }, BrushPickGranularity.Brush, selectedBrushes: new[] { 7 });
        uint expected = Palette.Rgba(255, 240, 60);
        Assert.Equal(expected, scene.Lines[0].Color);
    }

    // ---- Item 7: clipped-face filtering ("Show Clipped Brush Faces" OFF) --------

    [Fact]
    public void Clipped_Faces_Are_Neither_Drawn_Nor_Pickable_When_Filtered()
    {
        var survival = new Dictionary<int, bool[]>
        {
            [7] = new[] { false, true, true, true, true, false }, // faces 0 and 5 clipped
        };

        var scene = new RenderScene();
        BrushPickRegistry reg = BrushEmitter.Append(
            scene, new[] { Box() }, BrushPickGranularity.Face, survivingFaces: survival);

        Assert.Equal(8, scene.TotalTriangleCount); // 4 surviving quads → 8 tris

        // Only the 4 surviving faces were registered — clipped ones are unpickable.
        for (int payload = 0; payload < 4; payload++)
        {
            Assert.True(reg.TryResolveFace(payload, out _, out int face));
            Assert.NotEqual(0, face);
            Assert.NotEqual(5, face);
        }

        Assert.False(reg.TryResolveFace(4, out _, out _));
    }

    [Fact]
    public void Toggle_On_Restores_The_Full_Overlay()
    {
        var survival = new Dictionary<int, bool[]> { [7] = new bool[6] }; // all clipped

        // Passing no survival map = "Show Clipped Brush Faces" ON (draw everything).
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { Box() }, BrushPickGranularity.Brush, survivingFaces: null);
        Assert.Equal(12, scene.TotalTriangleCount);
        Assert.Equal(12, scene.Lines.Count);

        // Sanity: the same brush with the filter active would draw nothing.
        var filtered = new RenderScene();
        BrushEmitter.Append(filtered, new[] { Box() }, BrushPickGranularity.Brush, survivingFaces: survival);
        Assert.Equal(0, filtered.TotalTriangleCount);
        Assert.Empty(filtered.Lines);
    }

    [Fact]
    public void Brushes_Without_Build_Data_Always_Draw_Fully()
    {
        // A survival map that doesn't mention brush 7 (unbuilt/dirty brush).
        var survival = new Dictionary<int, bool[]> { [99] = new bool[6] };

        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { Box() }, BrushPickGranularity.Brush, survivingFaces: survival);
        Assert.Equal(12, scene.TotalTriangleCount);
    }

    [Fact]
    public void Stale_Short_Bitset_Never_Hides_Extra_Faces()
    {
        // Build data predates an edit that added faces: only indexed faces filter.
        var survival = new Dictionary<int, bool[]> { [7] = new[] { false, true } };

        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { Box() }, BrushPickGranularity.Brush, survivingFaces: survival);
        Assert.Equal(10, scene.TotalTriangleCount); // only face 0 hidden; faces 2+ out of range draw
    }

    // ---- Item 5: partial-clip fragment overlay --------------------------------

    [Fact]
    public void Partially_Clipped_Face_Draws_Its_Surviving_Fragment_Not_The_Authored_Quad()
    {
        // Air room x∈[-4,4]; solid pillar x∈[3,5] sticks half out of the room. Its +X cap
        // (x=5) is fully clipped, and the four side faces are cut back to the room wall at
        // x=4 — so the surviving fragments only reach x=4, not the authored x=5.
        Brush room = new()
        {
            Uid = 1, Rotation = Mat3.Identity, Position = new Vec3(0, 0, 0),
            Geometry = BrushFactory.Box(8, 6, 10, 0, 0, 0, "wall"), Flags = (uint)BrushFlags.Air, Life = -1,
        };
        Brush pillar = new()
        {
            Uid = 2, Rotation = Mat3.Identity, Position = new Vec3(4, 0, 0),
            Geometry = BrushFactory.Box(2, 2, 2, 0, 0, 0, "wall"), Flags = (uint)BrushFlags.None, Life = -1,
        };

        CompiledLevel c = GeometryCompiler.Compile(new List<Brush> { room, pillar });
        BrushFragmentIndex index = BrushFragmentIndex.Build(c.Geometry, c.BrushFaceIdStart, c.SurvivingBrushFaces);
        Assert.True(index.Covers(2), "the pillar must be covered by the fragment index");

        // Authored overlay (no fragments) reaches the full pillar extent (x up to 5).
        var authored = new RenderScene();
        BrushEmitter.Append(authored, new[] { pillar }, BrushPickGranularity.Face, solidFill: true);
        Assert.True(MaxLineX(authored) > 4.9f, $"authored overlay should reach x≈5 but was {MaxLineX(authored):0.###}");

        // Fragment overlay is clipped at the room wall (x=4): the +X cap is gone and the
        // side faces are cut back to x=4, so the overlay vertex set matches the surviving
        // fragment geometry — not the authored quad.
        var frag = new RenderScene();
        BrushEmitter.Append(frag, new[] { pillar }, BrushPickGranularity.Face, solidFill: true, survivingFragments: index);

        Assert.NotEmpty(frag.Lines);
        Assert.True(MaxLineX(frag) < 4.05f, $"fragment overlay should be clipped to x≈4 but reached {MaxLineX(frag):0.###}");
        Assert.Contains(LineVerts(frag), v => Math.Abs(v.X - 4f) < 0.05f);   // the cut edge at the room wall
        Assert.DoesNotContain(LineVerts(frag), v => v.X > 4.5f);            // the authored x=5 corners are gone
    }

    [Fact]
    public void Fully_Clipped_Face_Draws_Nothing_But_Surviving_Faces_Still_Draw()
    {
        // Same fixture: the pillar's +X cap (a face fully outside the room) must contribute
        // nothing, while the surviving faces still produce overlay geometry.
        Brush room = new()
        {
            Uid = 1, Rotation = Mat3.Identity, Position = new Vec3(0, 0, 0),
            Geometry = BrushFactory.Box(8, 6, 10, 0, 0, 0, "wall"), Flags = (uint)BrushFlags.Air, Life = -1,
        };
        Brush pillar = new()
        {
            Uid = 2, Rotation = Mat3.Identity, Position = new Vec3(4, 0, 0),
            Geometry = BrushFactory.Box(2, 2, 2, 0, 0, 0, "wall"), Flags = (uint)BrushFlags.None, Life = -1,
        };

        CompiledLevel c = GeometryCompiler.Compile(new List<Brush> { room, pillar });
        BrushFragmentIndex index = BrushFragmentIndex.Build(c.Geometry, c.BrushFaceIdStart, c.SurvivingBrushFaces);

        var frag = new RenderScene();
        BrushEmitter.Append(frag, new[] { pillar }, BrushPickGranularity.Face, solidFill: true, survivingFragments: index);

        // Something survived (the -X face and the clipped sides) …
        Assert.True(frag.TotalTriangleCount > 0);
        // … but nothing reaches into the clipped-away region beyond the room wall.
        Assert.DoesNotContain(LineVerts(frag), v => v.X > 4.5f);
    }

    // ---- Item 0e: portal faces in the brush overlay honor the draw mode --------

    private static Brush PortalBox()
    {
        // A box whose face 0 is a portal face (texture −1); the other 5 faces are textured.
        var b = new Brush { Uid = 9, Rotation = Mat3.Identity, Geometry = BrushFactory.Box(2, 2, 2, 0, 0, 0, "test.tga") };
        b.Geometry.Faces[0].Texture = -1;
        return b;
    }

    [Fact]
    public void Portal_Overlay_None_Draws_Edges_Only_No_Portal_Solid()
    {
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { PortalBox() }, BrushPickGranularity.Brush,
            portalFaces: PortalFaceDrawMode.None);

        // The portal face contributes NO solid triangle (5 textured quads → 10 tris), but all
        // 12 wireframe edges still draw so the brush stays visible + selectable (RED behaviour).
        // A lone portal face on a NON-portal brush needs no pick-only face: its textured sibling
        // faces already keep the brush pickable.
        Assert.Equal(10, scene.TotalTriangleCount);
        Assert.Equal(12, scene.Lines.Count);
        Assert.DoesNotContain(scene.Batches, b => b.IsPortal);
        Assert.DoesNotContain(scene.Batches, b => b.PickOnly);
    }

    [Fact]
    public void Portal_Overlay_SeeThru_Emits_Alpha_Tinted_Portal_Batch()
    {
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { PortalBox() }, BrushPickGranularity.Brush,
            portalFaces: PortalFaceDrawMode.SeeThru);

        // Now the portal quad is drawn too (5 textured + 1 portal = 12 tris) as a separate
        // alpha-pass portal batch with the tint (alpha 0.35).
        Assert.Equal(12, scene.TotalTriangleCount);
        GeometryBatch portal = Assert.Single(scene.Batches, b => b.IsPortal);
        Assert.Equal(RenderPass.Alpha, portal.Pass);
        Assert.Equal(0.35f, portal.Tint.W, 3);
        Assert.Equal(string.Empty, portal.TextureName); // flat quad — texture dropped
    }

    [Fact]
    public void Portal_Overlay_Opaque_Emits_Opaque_Tinted_Portal_Batch()
    {
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { PortalBox() }, BrushPickGranularity.Brush,
            portalFaces: PortalFaceDrawMode.Opaque);

        Assert.Equal(12, scene.TotalTriangleCount);
        GeometryBatch portal = Assert.Single(scene.Batches, b => b.IsPortal);
        Assert.Equal(RenderPass.Opaque, portal.Pass);
        Assert.Equal(1.0f, portal.Tint.W, 3);
    }

    // ---- Item 2: a BRUSH-LEVEL portal (BrushFlags.Portal) with real textures also
    // honors the draw mode in the live-preview overlay (the compiled path never sees it
    // because the compiler converts portal brushes to texture -1 membranes). Regression:
    // such a brush used to draw as a solid opaque box, ignoring "Don't Draw Portal Faces".

    private static Brush PortalFlaggedBox() => new()
    {
        // Every face keeps a REAL texture, so Face.IsPortalFace is false for all of them —
        // only the brush-level Portal flag identifies this as a portal brush.
        Uid = 11,
        Rotation = Mat3.Identity,
        Geometry = BrushFactory.Box(2, 2, 2, 0, 0, 0, "wall.tga"),
        Flags = (uint)BrushFlags.Portal,
    };

    [Fact]
    public void Portal_Flagged_Brush_None_Draws_No_Fill_But_Stays_Pickable()
    {
        // P5 (owner correction): an authored portal brush obeys View ▸ Portal Faces = None like a
        // compiled portal face — NO fill is drawn — but it must stay SELECTABLE, so its faces are
        // emitted PICK-ONLY (rasterized into the id-buffer pick pass, skipped in the colour pass).
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { PortalFlaggedBox() }, BrushPickGranularity.Brush,
            solidFill: true, portalFaces: PortalFaceDrawMode.None);

        // No visible portal fill (no drawn portal batch), but the pick geometry is present.
        Assert.DoesNotContain(scene.Batches, b => b.IsPortal);
        GeometryBatch pick = Assert.Single(scene.Batches, b => b.PickOnly);
        Assert.Equal(12, scene.TotalTriangleCount); // 6 pick-only quads (id-buffer only)
        Assert.Equal(12, scene.Lines.Count);        // wireframe still drawn so it's visible

        // Every pick-only vertex carries the whole-brush pick id → a click resolves to the brush.
        PickId id = PickId.Decode(pick.Vertices[0].PickId);
        Assert.Equal(PickKind.Brush, id.Kind);
        Assert.Equal(11, id.Index);
    }

    [Theory]
    [InlineData(PortalFaceDrawMode.None)]
    [InlineData(PortalFaceDrawMode.SeeThru)]
    [InlineData(PortalFaceDrawMode.Opaque)]
    public void Air_Portal_Textured_Brush_Uid414_Renders_Real_Faces_Under_Every_Portal_Face_Mode(PortalFaceDrawMode mode)
    {
        // ctf06 UID 414's shape (Portal|AIR, real textures). The air-carve clone survives the CSG solve
        // as real cavity walls, so Object mode shows the real textures under EVERY setting — and the
        // authored overlay must match: the faces render as REAL TEXTURES in all three modes, never a
        // portal tint (See-thru/Opaque) and never dropped to pick-only (None). View ▸ Portal Faces does
        // not touch them; the brush stays pickable via the real fill's whole-brush id.
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { AirPortalTexturedBox() }, BrushPickGranularity.Brush,
            solidFill: true, portalFaces: mode);

        Assert.DoesNotContain(scene.Batches, b => b.IsPortal);  // never a portal-tint quad
        Assert.DoesNotContain(scene.Batches, b => b.PickOnly);  // never dropped to invisible pick-only
        GeometryBatch real = Assert.Single(scene.Batches, b => b.TextureName.Length > 0);
        Assert.Equal(RenderPass.Opaque, real.Pass);
        Assert.Equal(12, scene.TotalTriangleCount);             // 6 real-textured quads, every mode

        PickId id = PickId.Decode(real.Vertices[0].PickId);
        Assert.Equal(PickKind.Brush, id.Kind);
        Assert.Equal(414, id.Index);
    }

    // ---- An air+portal brush with REAL textures (ctf06 UID 414's shape) is the case the owner reported
    // "seem deleted" in Brush mode: Portal|Air flags, all faces textured (IsPortalFace == false), no CSG
    // survival entry. Because it is an AIR portal (its air-carve clone survives as real cavity walls in
    // the compiled world), its authored faces must render their REAL TEXTURES in every mode/setting —
    // matching Object mode — not a portal tint. ----
    private static Brush AirPortalTexturedBox() => new()
    {
        Uid = 414,
        Rotation = Mat3.Identity,
        Geometry = BrushFactory.Box(2, 2, 2, 0, 0, 0, "wall.tga"),
        Flags = (uint)(BrushFlags.Portal | BrushFlags.Air),
    };

    [Fact]
    public void Air_Portal_Textured_Brush_Renders_Real_Faces_In_Merged_Overlay()
    {
        // A merged overlay (a survival map that does NOT mention this brush — portal brushes never get an
        // entry) with PortalFaces = None: the ctf06 UID 414 reproduction that appeared "deleted" in Brush
        // mode. Now its REAL textures are drawn (not dropped to pick-only), matching Object mode, and the
        // brush stays pickable as a whole brush.
        var survival = new System.Collections.Generic.Dictionary<int, bool[]> { [999] = new bool[6] };

        var scene = new RenderScene();
        BrushPickRegistry reg = BrushEmitter.Append(
            scene, new[] { AirPortalTexturedBox() }, BrushPickGranularity.Brush,
            solidFill: true, survivingFaces: survival, portalFaces: PortalFaceDrawMode.None);

        Assert.Equal(12, scene.TotalTriangleCount);            // 6 real-textured quads (drawn, not deleted)
        Assert.Equal(12, scene.Lines.Count);                   // wireframe box
        Assert.DoesNotContain(scene.Batches, b => b.IsPortal); // not a portal tint
        Assert.DoesNotContain(scene.Batches, b => b.PickOnly); // not dropped to pick-only — real fill now
        GeometryBatch real = Assert.Single(scene.Batches, b => b.TextureName.Length > 0);
        PickId id = PickId.Decode(real.Vertices[0].PickId); // pickable as a whole brush
        Assert.Equal(PickKind.Brush, id.Kind);
        Assert.Equal(414, id.Index);
    }

    [Fact]
    public void Portal_Brush_Is_Not_Hidden_By_A_Merged_Fragment_Stash_That_Covers_It()
    {
        // Q2 — brush-edit-mode case: a portal brush must render its faces in EVERY edit mode — fill
        // governed by View ▸ Portal Faces, wireframe + pickability ALWAYS. The compiler never records a
        // portal brush in the fragment stash, but were one ever "covered" with zero surviving fragments
        // (e.g. an air+portal brush whose air-carve clone got tracked), the merged (Brush-mode) fragment
        // path would skip every face — hiding the whole brush while Object mode still shows it via the
        // compiled path. The emitter pins portal brushes to the authored-polygon path, so a covering
        // stash can never hide them. This drives that exact hazard.
        Brush portal = PortalFlaggedBox(); // Uid 11, Portal flag, real textures
        var frags = BrushFragmentIndex.Build(
            new Geometry(),                                       // ...no compiled fragments for it,
            new Dictionary<int, int> { [(int)portal.Uid] = 0 },  // ...yet the stash "covers" the brush
            new Dictionary<int, bool[]> { [(int)portal.Uid] = new bool[6] });
        Assert.True(frags.Covers((int)portal.Uid)); // the exact hazard: covered, zero fragments

        // Brush-edit mode (solidFill true) + merged stash + View ▸ Portal Faces = See-Thru: fill drawn.
        var seeThru = new RenderScene();
        BrushEmitter.Append(seeThru, new[] { portal }, BrushPickGranularity.Brush,
            solidFill: true, survivingFragments: frags, portalFaces: PortalFaceDrawMode.SeeThru);
        Assert.Equal(12, seeThru.Lines.Count); // wireframe ALWAYS (would be 0 if the stash hid the brush)
        GeometryBatch fill = Assert.Single(seeThru.Batches, b => b.IsPortal);
        Assert.Equal(RenderPass.Alpha, fill.Pass); // fill governed by the Portal Faces mode
        Assert.Equal(PickKind.Brush, PickId.Decode(fill.Vertices[0].PickId).Kind);

        // Same brush + covering stash under Portal Faces = None: no fill, but wireframe + pickability stay.
        var none = new RenderScene();
        BrushEmitter.Append(none, new[] { portal }, BrushPickGranularity.Brush,
            solidFill: true, survivingFragments: frags, portalFaces: PortalFaceDrawMode.None);
        Assert.Equal(12, none.Lines.Count); // wireframe always
        Assert.DoesNotContain(none.Batches, b => b.IsPortal); // None => no fill
        GeometryBatch pickOnly = Assert.Single(none.Batches, b => b.PickOnly);
        Assert.Equal((int)portal.Uid, PickId.Decode(pickOnly.Vertices[0].PickId).Index); // still pickable
    }

    [Fact]
    public void Group_Mode_Style_Whole_Brush_Is_Pick_Only_When_Not_Solid_Filled()
    {
        // P4 at the emitter level: a plain (non-portal) brush with solidFill OFF but pickWholeBrush
        // ON (Group mode) emits its faces PICK-ONLY so the whole brush is selectable as a group
        // member — without a colour fill that would z-fight the compiled geometry.
        var scene = new RenderScene();
        var box = new Brush { Uid = 21, Rotation = Mat3.Identity, Geometry = BrushFactory.Box(2, 2, 2, 0, 0, 0, "wall.tga") };
        BrushEmitter.Append(scene, new[] { box }, BrushPickGranularity.Brush,
            solidFill: false, pickWholeBrush: true);

        GeometryBatch pick = Assert.Single(scene.Batches, b => b.PickOnly);
        Assert.Equal(12, scene.TotalTriangleCount);
        PickId id = PickId.Decode(pick.Vertices[0].PickId);
        Assert.Equal(PickKind.Brush, id.Kind);
        Assert.Equal(21, id.Index);

        // Without pickWholeBrush (plain Object mode), nothing is emitted (unpickable, no fill).
        var scene2 = new RenderScene();
        BrushEmitter.Append(scene2, new[] { box }, BrushPickGranularity.Brush, solidFill: false);
        Assert.Empty(scene2.Batches);
    }

    [Fact]
    public void Air_Portal_Brush_Registers_Vertices_For_Sub_Geometry_Picking()
    {
        var scene = new RenderScene();
        BrushPickRegistry reg = BrushEmitter.Append(
            scene, new[] { AirPortalTexturedBox() }, BrushPickGranularity.Vertex,
            solidFill: true, portalFaces: PortalFaceDrawMode.None);

        Assert.Equal(8, reg.Vertices.Count); // its vertices register for the nearest-vertex search
        Assert.Equal(8, scene.Billboards.Count(b => b.Kind == BillboardKind.Vertex));
    }

    [Fact]
    public void Portal_Flagged_Brush_SeeThru_Emits_One_Alpha_Portal_Batch_In_Preview()
    {
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { PortalFlaggedBox() }, BrushPickGranularity.Brush,
            solidFill: true, portalFaces: PortalFaceDrawMode.SeeThru);

        Assert.Equal(12, scene.TotalTriangleCount); // 6 portal quads
        GeometryBatch portal = Assert.Single(scene.Batches, b => b.IsPortal);
        Assert.Equal(RenderPass.Alpha, portal.Pass);
        Assert.Equal(0.35f, portal.Tint.W, 3);
        Assert.Equal(string.Empty, portal.TextureName); // texture dropped — portal tint quad
    }

    private static float MaxLineX(RenderScene scene)
    {
        float max = float.MinValue;
        foreach (LineSegment l in scene.Lines)
        {
            max = Math.Max(max, Math.Max(l.A.X, l.B.X));
        }

        return max;
    }

    private static IEnumerable<Vector3> LineVerts(RenderScene scene)
    {
        foreach (LineSegment l in scene.Lines)
        {
            yield return l.A;
            yield return l.B;
        }
    }
}
