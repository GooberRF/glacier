using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Per-pass fixtures for Alpine RED's build repair (<see cref="BuildRepair"/>, ALPINE-COMPAT-SPEC §3.1), on
/// synthetic solids built here (no retail data): t-junction split, crack weld, lost portal twin, split room,
/// zero-width portal strip, the wall fill and each of its refusals, and the detail-face exclusion. Plus the
/// boolean's coplanar seam tolerance (pass B) and the compile-level wiring (options, preview, counters, surfaces).
/// </summary>
public sealed class BuildRepairTests
{
    /// <summary>A hand-built solid in the compiler's post-room form: faces + pool rings + rooms.</summary>
    private sealed class Solid
    {
        public List<Vec3> Pool { get; } = new();

        public List<CsgFace> Faces { get; } = new();

        public List<int[]> Rings { get; } = new();

        public RoomBuildResult Rooms { get; } = new();

        public List<Portal> Portals { get; } = new();

        public BuildRepairStats Stats { get; } = new();

        public int V(float x, float y, float z)
        {
            Pool.Add(new Vec3(x, y, z));
            return Pool.Count - 1;
        }

        public void Room(int count, bool detail = false)
        {
            for (int i = 0; i < count; i++)
            {
                Rooms.Rooms.Add(new Room { IsSubroom = (byte)(detail ? 1 : 0) });
            }
        }

        public CsgFace Face(int room, params int[] ring) => Add(room, null, false, ring);

        public CsgFace Add(int room, CsgPlane? plane, bool portal, params int[] ring)
        {
            var verts = ring.Select(i => new CsgVertex(Pool[i], new Uv(Pool[i].X, Pool[i].Z))).ToList();
            var f = new CsgFace
            {
                Vertices = verts,
                Plane = plane ?? CsgPlane.FromPolygon(verts),
                Texture = portal ? string.Empty : "wall",
                IsPortal = portal,
                RoomIndex = room,
            };
            Faces.Add(f);
            Rings.Add(ring);
            return f;
        }

        public CsgFace Detail(int room, params int[] ring)
        {
            CsgFace f = Face(room, ring);
            f.Flags |= (ushort)FaceFlags.IsDetail;
            return f;
        }

        public int[] RingOf(CsgFace f) => Rings[Faces.IndexOf(f)];

        public BuildRepair NewRepair() => new(Stats);

        /// <summary>Pass C on the solid as it stands (face rooms from <see cref="CsgFace.RoomIndex"/>).</summary>
        public void AfterFlood(BuildRepair repair)
        {
            Rooms.FaceRoom = Faces.Select(f => f.RoomIndex).ToArray();
            repair.AfterFlood(Faces, Rings, Pool, Rooms);
        }

        /// <summary>Pass E (the hole repair).</summary>
        public void RepairHoles(BuildRepair? repair = null)
        {
            repair ??= NewRepair();
            repair.RepairHoles(Faces, Rings, Pool, Rooms, Portals);
        }
    }

    private static bool HasEdge(int[] ring, int a, int b)
    {
        for (int i = 0; i < ring.Length; i++)
        {
            int p = ring[i];
            int q = ring[(i + 1) % ring.Length];
            if ((p == a && q == b) || (p == b && q == a))
            {
                return true;
            }
        }

        return false;
    }

    // ─────────────── t-junction split ───────────────

    /// <summary>A floor quad whose long edge has a wall's corner sitting 0.5 mm off it, mid-edge.</summary>
    private static (Solid S, CsgFace Floor, int A, int B, int M) TJunction(bool floorIsDetail)
    {
        var s = new Solid();
        s.Room(1);
        int a = s.V(0, 0, 0), b = s.V(2, 0, 0), c = s.V(2, 0, 2), d = s.V(0, 0, 2);
        int m = s.V(1, 0.0005f, 0); // within 1e-3 of edge a-b, 1 unit from either end
        int top0 = s.V(0, 1, 0), top1 = s.V(1, 1, 0), top2 = s.V(2, 1, 0);
        CsgFace floor = floorIsDetail ? s.Detail(0, a, b, c, d) : s.Face(0, a, b, c, d);
        s.Face(0, a, top0, top1, m);
        s.Face(0, m, top1, top2, b);
        return (s, floor, a, b, m);
    }

    [Fact]
    public void Split_Inserts_A_Vertex_Lying_Within_1mm_Of_An_Edge()
    {
        (Solid s, CsgFace floor, int a, int b, int m) = TJunction(floorIsDetail: false);
        s.RepairHoles();

        int[] ring = s.RingOf(floor);
        Assert.Equal(5, ring.Length);
        Assert.True(HasEdge(ring, a, m) && HasEdge(ring, m, b), "the floor edge a-b is split at m");
        Assert.Equal(1, s.Stats.EdgesSplit);
        Assert.Equal(0, s.Stats.VerticesWelded);
        Assert.Equal(5, floor.Vertices.Count); // the compiler face is rewritten to match its pool ring
    }

    [Fact]
    public void Split_Never_Touches_A_Detail_Face()
    {
        (Solid s, CsgFace floor, int a, int b, int _) = TJunction(floorIsDetail: true);
        int[] before = s.RingOf(floor).ToArray();
        List<CsgVertex> verts = floor.Vertices;
        CsgPlane plane = floor.Plane;
        s.RepairHoles();

        Assert.Equal(before, s.RingOf(floor));
        Assert.Same(verts, floor.Vertices);
        Assert.Equal(plane, floor.Plane);
        Assert.True(HasEdge(s.RingOf(floor), a, b));
        Assert.Equal(0, s.Stats.EdgesSplit);
    }

    // ─────────────── crack weld ───────────────

    /// <summary>A floor and a wall meant to share edge a-b, but the wall's copy of b sits 0.5 mm away (b').</summary>
    private static (Solid S, CsgFace Floor, CsgFace Wall, int B, int B2) Crack(bool wallIsDetail)
    {
        var s = new Solid();
        s.Room(1);
        int a = s.V(0, 0, 0), b = s.V(2, 0, 0), c = s.V(2, 0, 2), d = s.V(0, 0, 2);
        int b2 = s.V(2.0005f, 0, 0);
        int e = s.V(2, 1, 0), f = s.V(0, 1, 0);
        CsgFace floor = s.Face(0, a, b, c, d);
        CsgFace wall = wallIsDetail ? s.Detail(0, b2, a, f, e) : s.Face(0, b2, a, f, e);
        return (s, floor, wall, b, b2);
    }

    [Fact]
    public void Weld_Joins_Open_Edge_Ends_Within_1mm_Without_Averaging()
    {
        (Solid s, CsgFace floor, CsgFace wall, int b, int b2) = Crack(wallIsDetail: false);
        s.RepairHoles();

        Assert.Equal(1, s.Stats.VerticesWelded);
        Assert.Contains(b, s.RingOf(wall));
        Assert.DoesNotContain(b2, s.RingOf(wall));
        Assert.Equal(new Vec3(2, 0, 0), s.Pool[b]); // redirected to a member, never averaged
        Assert.Equal(new Vec3(2, 0, 0), wall.Vertices[0].Position);
        Assert.Contains(b, s.RingOf(floor));
    }

    [Fact]
    public void Weld_Never_Touches_A_Detail_Face()
    {
        (Solid s, CsgFace _, CsgFace wall, int _, int b2) = Crack(wallIsDetail: true);
        List<CsgVertex> verts = wall.Vertices;
        s.RepairHoles();

        Assert.Equal(0, s.Stats.VerticesWelded);
        Assert.Contains(b2, s.RingOf(wall));
        Assert.Same(verts, wall.Vertices);
    }

    // ─────────────── portal twins + split rooms (after the flood) ───────────────

    /// <summary>Two floors meeting under a doorway at x = 0, the doorway's portal pair, both copies left in room 0.</summary>
    private static (Solid S, CsgFace Front, CsgFace Back) LostTwin()
    {
        var s = new Solid();
        s.Room(2);
        int p0 = s.V(0, 0, 0), p1 = s.V(0, 1, 0), p2 = s.V(0, 1, 1), p3 = s.V(0, 0, 1);
        int l0 = s.V(-1, 0, 0), l1 = s.V(-1, 0, 1), r0 = s.V(1, 0, 0), r1 = s.V(1, 0, 1);
        s.Face(0, l0, p0, p3, l1);
        s.Face(1, p0, r0, r1, p3);
        CsgFace front = s.Add(0, null, true, p0, p1, p2, p3); // normal +x: faces into room 1
        CsgFace back = s.Add(0, null, true, p3, p2, p1, p0);
        Assert.True(front.Plane.Normal.X > 0.99f);
        return (s, front, back);
    }

    [Fact]
    public void Twin_Fix_Moves_A_Portal_Copy_Into_The_Room_In_Front_Of_It()
    {
        (Solid s, CsgFace front, CsgFace back) = LostTwin();
        s.AfterFlood(s.NewRepair());

        Assert.Equal(1, front.RoomIndex);
        Assert.Equal(0, back.RoomIndex);
        Assert.Equal(1, s.Stats.PortalCopiesMoved);
        Assert.Equal(0, s.Stats.SplitRoomsJoined); // the shared edge lies on the portal: the rooms stay apart
        Assert.Equal(new[] { 0, 1, 1, 0 }, s.Rooms.FaceRoom);
    }

    [Fact]
    public void Twin_Fix_Leaves_A_Correctly_Placed_Pair_Alone()
    {
        (Solid s, CsgFace front, CsgFace _) = LostTwin();
        front.RoomIndex = 1;
        s.AfterFlood(s.NewRepair());
        Assert.Equal(0, s.Stats.PortalCopiesMoved);
    }

    [Fact]
    public void Split_Rooms_Sharing_A_Full_Edge_Away_From_Portals_Are_Joined()
    {
        var s = new Solid();
        s.Room(2);
        int a = s.V(0, 0, 0), b = s.V(0, 0, 1), l0 = s.V(-1, 0, 0), l1 = s.V(-1, 0, 1);
        int r0 = s.V(1, 0, 0), r1 = s.V(1, 0, 1), up0 = s.V(1, 1, 0), up1 = s.V(1, 1, 1);
        CsgFace left = s.Face(0, l0, a, b, l1);
        CsgFace right = s.Face(1, a, r0, r1, b);
        CsgFace wall = s.Face(1, r0, up0, up1, r1);
        s.AfterFlood(s.NewRepair());

        Assert.Equal(1, s.Stats.SplitRoomsJoined);
        Assert.Equal(1, left.RoomIndex); // the room with more faces absorbs the other
        Assert.Equal(1, right.RoomIndex);
        Assert.Equal(1, wall.RoomIndex);
        Assert.Equal(1, s.Rooms.Rooms[0].IsSubroom); // emptied: a zero-box detail room, index preserved
        Assert.Equal(default(Vec3), s.Rooms.Rooms[0].Aabb.P1);
        Assert.Equal(default(Vec3), s.Rooms.Rooms[0].Aabb.P2);
        Assert.Equal(2, s.Rooms.Rooms.Count);
    }

    [Fact]
    public void Detail_Rooms_Are_Never_Joined()
    {
        var s = new Solid();
        s.Room(1);
        s.Room(1, detail: true);
        int a = s.V(0, 0, 0), b = s.V(0, 0, 1), l0 = s.V(-1, 0, 0), l1 = s.V(-1, 0, 1);
        int r0 = s.V(1, 0, 0), r1 = s.V(1, 0, 1);
        s.Face(0, l0, a, b, l1);
        s.Detail(1, a, r0, r1, b);
        s.AfterFlood(s.NewRepair());
        Assert.Equal(0, s.Stats.SplitRoomsJoined);
    }

    // ─────────────── portal strips ───────────────

    [Fact]
    public void Zero_Width_Portal_Strips_Reached_From_Open_Edges_Are_Removed()
    {
        var s = new Solid();
        s.Room(1);
        int a = s.V(0, 0, 0), b = s.V(1, 0, 0), c = s.V(1, 0.0005f, 0), d = s.V(0, 0.0005f, 0);
        CsgFace strip = s.Add(0, null, true, a, b, c, d);
        int e = s.V(3, 0, 0), f = s.V(4, 0, 0), g = s.V(4, 1, 0), h = s.V(3, 1, 0);
        CsgFace door = s.Add(0, null, true, e, f, g, h);
        s.RepairHoles();

        Assert.Equal(1, s.Stats.PortalStripsRemoved);
        Assert.DoesNotContain(strip, s.Faces);
        Assert.Contains(door, s.Faces);
        Assert.Equal(s.Faces.Count, s.Rings.Count);
    }

    // ─────────────── wall fill ───────────────

    private static readonly CsgPlane Floor = new(new Vec3(0, 1, 0), 0f);

    /// <summary>
    /// An n×n grid of coplanar quads (cell size <paramref name="cell"/>, plane y = 0, normal +y) with the
    /// <paramref name="holes"/> cells left out. Returns the solid and the vertex index grid.
    /// </summary>
    private static (Solid S, int[,] Vtx) Grid(int n, float cell, IEnumerable<(int I, int J)> holes, Func<int, int, Vec3>? place = null)
    {
        var s = new Solid();
        s.Room(1);
        var vtx = new int[n + 1, n + 1];
        for (int i = 0; i <= n; i++)
        {
            for (int j = 0; j <= n; j++)
            {
                Vec3 p = place?.Invoke(i, j) ?? new Vec3(i * cell, 0, j * cell);
                vtx[i, j] = s.V(p.X, p.Y, p.Z);
            }
        }

        var skip = new HashSet<(int, int)>(holes);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (!skip.Contains((i, j)))
                {
                    // (i,j) → (i,j+1) → (i+1,j+1) → (i+1,j): Newell normal +y
                    s.Add(0, Floor, false, vtx[i, j], vtx[i, j + 1], vtx[i + 1, j + 1], vtx[i + 1, j]);
                }
            }
        }

        return (s, vtx);
    }

    [Fact]
    public void Wall_Fill_Closes_A_Small_Flat_Convex_Gap_With_An_Unlit_Face()
    {
        (Solid s, int[,] v) = Grid(3, 0.5f, new[] { (1, 1) });
        int before = s.Faces.Count;
        s.RepairHoles();

        Assert.Equal(1, s.Stats.WallGapsFilled);
        Assert.Equal(before + 1, s.Faces.Count);
        CsgFace fill = s.Faces[^1];
        Assert.True(fill.RepairFill);
        Assert.False(fill.IsPortal);
        Assert.Equal(0, fill.RoomIndex);
        Assert.Equal(Floor, fill.Plane); // forced to the wall's plane
        Assert.Equal("wall", fill.Texture);
        int[] ring = s.RingOf(fill);
        Assert.Equal(4, ring.Length);
        Assert.Equal(
            new[] { v[1, 1], v[1, 2], v[2, 1], v[2, 2] }.OrderBy(x => x),
            ring.OrderBy(x => x));

        // UVs continue the wall's mapping (the fixture maps u = x, v = z).
        for (int k = 0; k < ring.Length; k++)
        {
            Assert.Equal(s.Pool[ring[k]].X, fill.Vertices[k].Uv.U, 4);
            Assert.Equal(s.Pool[ring[k]].Z, fill.Vertices[k].Uv.V, 4);
        }
    }

    [Fact]
    public void Wall_Fill_Refuses_A_Gap_Over_One_Square_Unit()
    {
        (Solid s, _) = Grid(3, 1.5f, new[] { (1, 1) });
        s.RepairHoles();
        Assert.Equal(0, s.Stats.WallGapsFilled);
    }

    [Fact]
    public void Wall_Fill_Refuses_A_Non_Convex_Gap()
    {
        (Solid s, _) = Grid(4, 0.25f, new[] { (1, 1), (1, 2), (2, 1) });
        s.RepairHoles();
        Assert.Equal(0, s.Stats.WallGapsFilled);
    }

    [Fact]
    public void Wall_Fill_Refuses_A_Gap_Not_Coplanar_With_Its_Border()
    {
        // One corner of the gap sits 1 cm above the wall plane every bordering face carries.
        (Solid s, _) = Grid(3, 0.5f, new[] { (1, 1) }, (i, j) => new Vec3(i * 0.5f, i == 1 && j == 1 ? 0.01f : 0f, j * 0.5f));
        s.RepairHoles();
        Assert.Equal(0, s.Stats.WallGapsFilled);
    }

    [Fact]
    public void Wall_Fill_Refuses_A_Gap_Bordered_By_A_Non_Regular_Face()
    {
        (Solid s, int[,] v) = Grid(3, 0.5f, new[] { (1, 1) });
        CsgFace border = s.Faces.First(f => s.RingOf(f).Contains(v[1, 1]) && s.RingOf(f).Contains(v[1, 2]));
        border.IsPortal = true;
        s.RepairHoles();
        Assert.Equal(0, s.Stats.WallGapsFilled);
    }

    [Fact]
    public void Wall_Fill_Leaves_A_Lost_Doorway_Open()
    {
        (Solid s, int[,] v) = Grid(3, 0.5f, new[] { (1, 1) });

        // A portal face over the gap that portal linking later removed: its box still marks a doorway.
        int a = v[1, 1], b = v[1, 2], c = v[2, 2], d = v[2, 1];
        s.Add(0, Floor, true, a, b, c, d);
        BuildRepair repair = s.NewRepair();
        s.AfterFlood(repair);
        s.Faces.RemoveAt(s.Faces.Count - 1);
        s.Rings.RemoveAt(s.Rings.Count - 1);
        repair.CountUnplacedPortals(s.Faces);
        s.RepairHoles(repair);

        Assert.Equal(1, s.Stats.UnplacedPortalFacesRemoved);
        Assert.Equal(0, s.Stats.WallGapsFilled);
    }

    /// <summary>A gap shaped as a regular <paramref name="corners"/>-gon, ringed by coplanar quads.</summary>
    private static Solid PolygonGap(int corners)
    {
        var s = new Solid();
        s.Room(1);
        var inner = new int[corners];
        var outer = new int[corners];
        for (int k = 0; k < corners; k++)
        {
            double t = 2 * Math.PI * k / corners;
            inner[k] = s.V((float)(0.3 * Math.Cos(t)), 0, (float)(0.3 * Math.Sin(t)));
            outer[k] = s.V((float)(1.0 * Math.Cos(t)), 0, (float)(1.0 * Math.Sin(t)));
        }

        for (int k = 0; k < corners; k++)
        {
            int n = (k + 1) % corners;
            s.Face(0, inner[k], inner[n], outer[n], outer[k]);
        }

        return s;
    }

    [Fact]
    public void Wall_Fill_Takes_Up_To_Sixteen_Corners()
    {
        Solid s = PolygonGap(16);
        s.RepairHoles();
        Assert.Equal(1, s.Stats.WallGapsFilled);
    }

    [Fact]
    public void Wall_Fill_Refuses_More_Than_Sixteen_Corners()
    {
        Solid s = PolygonGap(17);
        s.RepairHoles();
        Assert.Equal(0, s.Stats.WallGapsFilled);
    }

    // ─────────────── hole count ───────────────

    [Fact]
    public void Holes_After_Rooms_Counts_Open_Edge_Groups_Per_Room()
    {
        (Solid s, _) = Grid(3, 1.5f, new[] { (1, 1) });
        s.RepairHoles();

        // the outer rim and the (unfillable) centre gap: two groups of open edges
        Assert.Equal(2, s.Stats.HolesAfterRooms);
        Assert.True(s.Stats.Ran);
    }

    // ─────────────── pass B: coplanar seam tolerance ───────────────

    [Fact]
    public void Coplanar_Seam_Tolerance_Claims_Points_Within_1e4_Of_An_Edge()
    {
        var kin = new List<CsgFace>
        {
            new()
            {
                Vertices = new List<CsgVertex>
                {
                    new(new Vec3(0, 0, 0), default), new(new Vec3(0, 0, 1), default),
                    new(new Vec3(1, 0, 1), default), new(new Vec3(1, 0, 0), default),
                },
                Plane = Floor,
            },
        };
        var justOutside = new Vec3(1.00005f, 0, 0.5f);
        var farOutside = new Vec3(1.0003f, 0, 0.5f);

        var tolerant = new CsgSolver { CoplanarSeamTolerance = true };
        var strict = new CsgSolver { CoplanarSeamTolerance = false };
        Assert.Same(kin[0], tolerant.CoveringFace(kin, new Vec3(0.5f, 0, 0.5f)));
        Assert.Same(kin[0], tolerant.CoveringFace(kin, justOutside));
        Assert.Null(strict.CoveringFace(kin, justOutside));
        Assert.Null(tolerant.CoveringFace(kin, farOutside));
    }

    // ─────────────── compile-level wiring ───────────────

    private static List<Brush> Doorway() => new()
    {
        CompilerTestBrushes.AirBox(1, new Vec3(-5, 0, 0), 10, 10, 10),
        CompilerTestBrushes.AirBox(2, new Vec3(5, 0, 0), 10, 4, 4),
        CompilerTestBrushes.MakeBox(3, new Vec3(0, 0, 0), 0.4f, 4, 4, BrushFlags.Portal | BrushFlags.Air, "wall"),
        CompilerTestBrushes.DetailBox(4, new Vec3(-5, -4, 0), 1, 1, 1),
    };

    [Fact]
    public void Repair_Is_On_By_Default_And_Counts_On_A_Sealed_Build()
    {
        Assert.True(new CompileOptions().AlpineRepair);
        Assert.True(BuildSettings.EditorDefaults.RepairBuildHoles);

        CompiledLevel c = GeometryCompiler.Compile(Doorway());
        Assert.True(c.Report.Repair.Ran);
        Assert.Equal(11, c.Report.Repair.Counters().Count);
        Assert.Equal("Holes after building rooms", c.Report.Repair.Counters()[0].Name);
        Assert.Equal("Unplaced portal faces removed", c.Report.Repair.Counters()[10].Name);
    }

    [Fact]
    public void Preview_And_Repair_Off_Builds_Do_Not_Repair_Holes()
    {
        CompiledLevel preview = GeometryCompiler.Compile(Doorway(), options: new CompileOptions { FixTJoints = false, BuildSurfaces = false });
        Assert.False(preview.Report.Repair.Ran);

        CompiledLevel off = GeometryCompiler.Compile(Doorway(), options: new CompileOptions { AlpineRepair = false });
        Assert.False(off.Report.Repair.Ran);
        Assert.All(off.Report.Repair.Counters(), kv => Assert.Equal(0, kv.Value));
    }

    [Fact]
    public void Options_Factory_Maps_The_Repair_Setting()
    {
        var rfl = new Ged.Core.IO.Rfl.RflFile();
        Assert.True(CompileOptionsFactory.Create(rfl, BuildSettings.EditorDefaults, new CompileRequest()).AlpineRepair);
        Assert.False(CompileOptionsFactory.Create(rfl, BuildSettings.EditorDefaults with { RepairBuildHoles = false }, new CompileRequest()).AlpineRepair);
    }

    [Fact]
    public void Repair_Is_A_No_Op_On_A_Watertight_Level()
    {
        Geometry on = GeometryCompiler.Compile(Doorway()).Geometry;
        Geometry off = GeometryCompiler.Compile(Doorway(), options: new CompileOptions { AlpineRepair = false }).Geometry;

        Assert.Equal(off.Faces.Count, on.Faces.Count);
        Assert.Equal(off.Rooms.Count, on.Rooms.Count);
        Assert.Equal(off.Portals.Count, on.Portals.Count);
        Assert.Equal(off.Vertices, on.Vertices);
        for (int i = 0; i < on.Faces.Count; i++)
        {
            Assert.Equal(off.Faces[i].Vertices.Select(v => v.Index), on.Faces[i].Vertices.Select(v => v.Index));
            Assert.Equal(off.Faces[i].Plane, on.Faces[i].Plane);
            Assert.Equal(off.Faces[i].SurfaceIndex, on.Faces[i].SurfaceIndex);
        }
    }

    [Fact]
    public void A_Repair_Fill_Gets_No_Lightmap_Surface()
    {
        var verts = new List<CsgVertex>
        {
            new(new Vec3(0, 0, 0), default), new(new Vec3(0, 0, 1), default),
            new(new Vec3(1, 0, 1), default), new(new Vec3(1, 0, 0), default),
        };
        var wall = new CsgFace { Vertices = verts, Plane = Floor, Texture = "wall", RoomIndex = 0 };
        var fill = new CsgFace
        {
            Vertices = verts.Select(v => new CsgVertex(v.Position.Add(new Vec3(2, 0, 0)), v.Uv)).ToList(),
            Plane = Floor,
            Texture = "wall",
            RoomIndex = 0,
            RepairFill = true,
        };
        var rooms = new RoomBuildResult { FaceRoom = new[] { 0, 0 } };
        rooms.Rooms.Add(new Room());
        new SurfaceBuilder().Build(new List<CsgFace> { wall, fill }, rooms, new CompiledLevel(), group: false);

        Assert.True(wall.SurfaceIndex >= 0);
        Assert.Equal(-1, fill.SurfaceIndex);
    }
}
