using System.Numerics;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Editing;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Rfg;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 2 — the drag ghost's local-space wireframe builders (mesh LOD0 edges, prefab brush edges +
/// object markers, bounds/box fallbacks + budget), the SHARED placement-offset function that drives
/// both the ghost and the drop (bottom-align, pickup 1 m raise, center fallback), and (item 1) the
/// brush-faces-only texture ray resolver (skips compiled geometry, hidden + locked brushes).
/// </summary>
public sealed class DragGhostTests
{
    // ---- Item 2: ghost wireframe + bounds ------------------------------------

    [Fact]
    public void UnitBox_Has_Twelve_Edges_And_Symmetric_Bounds()
    {
        GhostGeometry g = DragGhost.UnitBox(0xFF);
        Assert.Equal(12, g.Lines.Count);
        Assert.True(g.HasBounds);
        Assert.Equal(new Vector3(-0.75f), g.Min);
        Assert.Equal(new Vector3(0.75f), g.Max);
    }

    [Fact]
    public void Null_Mesh_Falls_Back_To_A_Unit_Box()
    {
        Assert.Equal(12, DragGhost.MeshWireframe(null, 0xFF).Lines.Count);
    }

    [Fact]
    public void Small_Mesh_Yields_Its_Unique_Edges_And_Bounds()
    {
        GhostGeometry g = DragGhost.MeshWireframe(MeshWithTriangles(1), 0xFF);
        Assert.Equal(3, g.Lines.Count); // one triangle → 3 unique edges
        Assert.Equal(new Vector3(0, 0, 0), g.Min);
        Assert.Equal(new Vector3(0, 1, 1), g.Max);
    }

    [Fact]
    public void Over_Budget_Mesh_Falls_Back_To_A_Bounds_Box_But_Keeps_Its_Bounds()
    {
        GhostGeometry g = DragGhost.MeshWireframe(MeshWithTriangles(4000), 0xFF); // 12000 edges > 10k
        Assert.Equal(12, g.Lines.Count);
        Assert.Equal(new Vector3(0, 0, 0), g.Min);
        Assert.Equal(new Vector3(3999, 1, 1), g.Max);
    }

    [Fact]
    public void Prefab_Wireframe_Includes_Brush_Edges()
    {
        var payload = new RfgFile { Version = 0xC8 };
        var group = new RfgGroup { Name = "p" };
        var be = new BrushEditor(EmptyDoc());
        int uid = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(0, 0, 0), Mat3.Identity);
        group.Brushes.Brushes.Add(GeometryClone.Deep(be.FindBrush(uid)!));
        payload.Groups.Add(group);

        Assert.Equal(12, DragGhost.PrefabWireframe(payload, 0xFF).Lines.Count); // a box brush has 12 unique edges
    }

    // ---- Item 2: shared placement offset (ghost == drop) ---------------------

    [Fact]
    public void PlacementOffset_Bottom_Aligns_On_A_Surface()
    {
        // Box spanning y ∈ [−2, 2]: origin raised by 2 so the bbox bottom rests on the point.
        Assert.Equal(new Vector3(0, 2, 0), DragGhost.PlacementOffset(new Vector3(-1, -2, -1), new Vector3(1, 2, 1), onSurface: true, pickup: false));
    }

    [Fact]
    public void PlacementOffset_Centers_Horizontally_For_An_Off_Origin_Bbox()
    {
        // bbox [0,0,0]..[2,4,2]: centre (1,2,1); bottom-centre offset = (−1, −min.Y, −1) = (−1, 0, −1).
        Assert.Equal(new Vector3(-1, 0, -1), DragGhost.PlacementOffset(new Vector3(0, 0, 0), new Vector3(2, 4, 2), onSurface: true, pickup: false));
    }

    [Fact]
    public void PlacementOffset_Raises_Pickups_One_Metre_Above_The_Surface()
    {
        // centre (0,0,0): pickup offset raises the centre 1 m → (0, 1, 0).
        Assert.Equal(new Vector3(0, 1, 0), DragGhost.PlacementOffset(new Vector3(-1, -1, -1), new Vector3(1, 1, 1), onSurface: true, pickup: true));
    }

    [Fact]
    public void PlacementOffset_Off_Surface_Is_Centre_Placement()
    {
        Assert.Equal(Vector3.Zero, DragGhost.PlacementOffset(new Vector3(-5), new Vector3(5), onSurface: false, pickup: false));
        Assert.Equal(Vector3.Zero, DragGhost.PlacementOffset(new Vector3(-5), new Vector3(5), onSurface: false, pickup: true));
    }

    // ---- Item 1: brush-faces-only texture ray resolver -----------------------

    [AvaloniaFact]
    public void RayBrushFaceHit_Resolves_An_Authored_Brush_Face_Not_Compiled_Geometry()
    {
        (EditorSession session, _) = SessionWithBox(out int uid);
        EditorSession.BrushFaceHit hit = session.RayBrushFaceHit(new Vector3(0, 0, 20), new Vector3(0, 0, -1));

        Assert.True(hit.Hit);
        Assert.False(hit.BlockedByLock);
        Assert.Equal(uid, hit.BrushUid); // a real editable brush face (never the compiled −1 marker)
        Assert.True(hit.FaceIndex >= 0);
        Assert.True(System.MathF.Abs(hit.Point.Z - 2f) < 1e-2f);
    }

    [AvaloniaFact]
    public void RayBrushFaceHit_On_A_Locked_Brush_Is_Blocked_Not_A_Target()
    {
        (EditorSession session, BrushEditor be) = SessionWithBox(out int uid);
        be.SetBrushLocked(new[] { uid }, locked: true);

        EditorSession.BrushFaceHit hit = session.RayBrushFaceHit(new Vector3(0, 0, 20), new Vector3(0, 0, -1));
        Assert.False(hit.Hit);
        Assert.True(hit.BlockedByLock);
        Assert.Equal(uid, hit.BrushUid); // the locked face id, so the caller can hint/highlight it
    }

    [AvaloniaFact]
    public void RayBrushFaceHit_Skips_Hidden_Brushes_Entirely()
    {
        (EditorSession session, BrushEditor be) = SessionWithBox(out int uid);
        be.SetBrushHidden(new[] { uid }, hidden: true);

        EditorSession.BrushFaceHit hit = session.RayBrushFaceHit(new Vector3(0, 0, 20), new Vector3(0, 0, -1));
        Assert.False(hit.Hit);
        Assert.False(hit.BlockedByLock); // hidden is not "blocked by lock" — it is simply not there
    }

    // ---- helpers -------------------------------------------------------------

    private static (EditorSession, BrushEditor) SessionWithBox(out int uid)
    {
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        uid = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 4, Height = 4, Depth = 4 }, new Vec3(0, 0, 0), Mat3.Identity);
        return (session, be);
    }

    private static Ged.Core.Editor.EditorDocument EmptyDoc()
    {
        var rfl = new Ged.Core.IO.Rfl.RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Sections.Add(new Ged.Core.IO.Rfl.RflSection((uint)Ged.Core.IO.Rfl.SectionType.End, System.Array.Empty<byte>()));
        return new Ged.Core.Editor.EditorDocument(rfl);
    }

    private static V3dFile MeshWithTriangles(int triangles)
    {
        var batch = new V3dBatch
        {
            NumVertices = triangles * 3,
            NumTriangles = triangles,
            Positions = new Vec3[triangles * 3],
            Triangles = new V3dTriangle[triangles],
        };
        for (int i = 0; i < triangles; i++)
        {
            batch.Positions[(i * 3) + 0] = new Vec3(i, 0, 0);
            batch.Positions[(i * 3) + 1] = new Vec3(i, 1, 0);
            batch.Positions[(i * 3) + 2] = new Vec3(i, 0, 1);
            batch.Triangles[i] = new V3dTriangle((ushort)((i * 3) + 0), (ushort)((i * 3) + 1), (ushort)((i * 3) + 2), 0);
        }

        var lod = new V3dLod();
        lod.Batches.Add(batch);
        var sm = new V3dSubmesh();
        sm.Lods.Add(lod);
        var mesh = new V3dFile();
        mesh.Submeshes.Add(sm);
        return mesh;
    }
}
