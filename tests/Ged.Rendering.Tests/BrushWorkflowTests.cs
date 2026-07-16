using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// End-to-end workflow test mirroring what the shell does with no window: create
/// primitives, render/pick them per mode (as EditorSession does), run brush/face/
/// vertex operators through undo, then save and reload. Verifies the Core editing
/// layer and the render/pick glue agree.
/// </summary>
public sealed class BrushWorkflowTests
{
    private static (EditorDocument Doc, BrushEditor Ed) NewLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "wf.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        var doc = new EditorDocument(rfl);
        return (doc, new BrushEditor(doc));
    }

    private static int Create(BrushEditor ed, BrushShape shape, Vec3 pos) =>
        ed.CreateBrush(new BrushCreateParams { Shape = shape, Width = 2, Height = 2, Depth = 2, WidthSplits = shape == BrushShape.Box ? 0 : 8 }, pos, Mat3.Identity);

    [Fact]
    public void Full_Editing_Session_Round_Trips()
    {
        (EditorDocument doc, BrushEditor ed) = NewLevel();

        // Create box, cylinder, sphere.
        int box = Create(ed, BrushShape.Box, new Vec3(0, 0, 0));
        int cyl = Create(ed, BrushShape.Cylinder, new Vec3(6, 0, 0));
        int sph = Create(ed, BrushShape.Sphere, new Vec3(12, 0, 0));
        Assert.Equal(3, ed.Brushes.Count);

        // Brush mode: emit + pick a whole brush.
        ed.SetMode(EditMode.Brush);
        var scene = new RenderScene();
        BrushPickRegistry reg = BrushEmitter.Append(scene, ed.Brushes, BrushPickGranularity.Brush);
        Assert.NotEmpty(scene.Batches);
        Assert.NotEmpty(scene.Lines);
        PickId boxPick = PickId.Decode(scene.Batches.SelectMany(b => b.Vertices).First(v => PickId.Decode(v.PickId).Index == box).PickId);
        Assert.Equal(PickKind.Brush, boxPick.Kind);

        // Keyboard move (nudge): move the cylinder +X one grid step, undo-able.
        ed.SelectBrush(cyl);
        Vec3 before = ed.FindBrush(cyl)!.Position;
        ed.TransformSelected("Move", b => BrushTransform.Move(b, new Vec3(1, 0, 0)));
        Assert.True(ed.FindBrush(cyl)!.Position.ApproxEquals(before.Add(new Vec3(1, 0, 0))));

        // Clip the box in half (split), then undo.
        ed.SelectBrush(box);
        OpResult clip = ed.Clip(ed.FindBrush(box)!.Position, new Vec3(1, 0, 0), ClipMode.Split, false);
        Assert.True(clip.Success, clip.Message);
        Assert.Equal(4, ed.Brushes.Count);
        doc.Undo.Undo();
        Assert.Equal(3, ed.Brushes.Count);

        // Fuse the sphere is meaningless; fuse two fresh adjacent boxes instead.
        int b1 = Create(ed, BrushShape.Box, new Vec3(-10, 0, 0));
        int b2 = Create(ed, BrushShape.Box, new Vec3(-8, 0, 0));
        ed.ClearSelection();
        ed.SelectBrush(b1);
        ed.SelectBrush(b2, additive: true);
        Assert.True(ed.Fuse().Success);

        // Face mode: extrude a face of the box.
        ed.SetMode(EditMode.Face);
        var faceScene = new RenderScene();
        BrushPickRegistry faceReg = BrushEmitter.Append(faceScene, ed.Brushes, BrushPickGranularity.Face);
        uint facePick = faceScene.Batches.SelectMany(b => b.Vertices)
            .First(v => faceReg.TryResolveFace(PickId.Decode(v.PickId).Index, out int u, out _) && u == box).PickId;
        Assert.True(faceReg.TryResolveFace(PickId.Decode(facePick).Index, out int fUid, out int fIdx));
        OpResult extrude = ed.EditBrushes(new[] { fUid }, "Extrude", b => FaceOps.Extrude(b.Geometry, fIdx, 2f));
        Assert.True(extrude.Success, extrude.Message);
        Assert.True(GeometryUtil.Validate(ed.FindBrush(box)!.Geometry));

        // Vertex mode: weld two verts of the cylinder.
        ed.SetMode(EditMode.Vertex);
        Brush cylBrush = ed.FindBrush(cyl)!;
        var pair = new[] { cylBrush.Geometry.Faces[0].Vertices[0].Index, cylBrush.Geometry.Faces[0].Vertices[1].Index };
        OpResult weld = ed.EditBrushes(new[] { cyl }, "Weld", b => VertexOps.Weld(b.Geometry, pair));
        Assert.True(weld.Success, weld.Message);

        // Undo everything back to the empty document, then redo it all.
        while (doc.Undo.CanUndo)
        {
            doc.Undo.Undo();
        }

        Assert.Empty(ed.Brushes);
        while (doc.Undo.CanRedo)
        {
            doc.Undo.Redo();
        }

        Assert.True(ed.Brushes.Count >= 3);

        // Save and reload: every brush survives.
        byte[] bytes = doc.SaveToBytes(updateTimestamp: false);
        RflFile reloaded = RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();
        BrushesSection bs = reloaded.Sections.Select(s => s.Content).OfType<BrushesSection>().Single();
        Assert.Equal(ed.Brushes.Count, bs.Brushes.Count);
        Assert.All(bs.Brushes, b => Assert.True(GeometryUtil.Validate(b.Geometry)));
    }

    [Fact]
    public void Vertex_Mode_Emits_Pickable_Dots_Resolving_To_Real_Vertices()
    {
        (EditorDocument _, BrushEditor ed) = NewLevel();
        int box = Create(ed, BrushShape.Box, new Vec3(0, 0, 0));
        ed.SetMode(EditMode.Vertex);

        var scene = new RenderScene();
        BrushPickRegistry reg = BrushEmitter.Append(scene, ed.Brushes, BrushPickGranularity.Vertex);
        Assert.Equal(8, scene.Billboards.Count(b => b.Kind == BillboardKind.Vertex));

        Billboard dot = scene.Billboards.First(b => b.Kind == BillboardKind.Vertex);
        Assert.True(reg.TryResolveVertex(dot.PickId.Index, out int uid, out int vertex));
        Assert.Equal(box, uid);
        Assert.InRange(vertex, 0, 7);
    }
}
