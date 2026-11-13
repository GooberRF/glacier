using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing.Organic;
using Xunit;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// The near-coplanar audit (ORGANIC-GEOMETRY-SPEC §4.2): coincident and clearly separate faces pass, the
/// band in between conflicts — unless the two faces only meet along a shared edge or do not cover a common
/// patch of their plane (the refinements that keep the audit to real double surfaces).
/// </summary>
public sealed class HullAuditTests
{
    private const double G = 1.0 / 1024;

    private static HullResult Box(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var pts = new List<Vec3d>();
        foreach (double x in new[] { x0, x1 })
        {
            foreach (double y in new[] { y0, y1 })
            {
                foreach (double z in new[] { z0, z1 })
                {
                    pts.Add(new Vec3d(x, y, z));
                }
            }
        }

        return ConvexHull3.Build(pts, new HullOptions(), out string? error) ?? throw new System.InvalidOperationException(error);
    }

    [Fact]
    public void Two_Boxes_Offset_Under_A_Millimetre_Conflict()
    {
        // One grid cell (1/1024 m, just under 1 mm — the finest offset hull points can have) above the
        // first box's top: the two faces overlap and sit inside the forbidden band.
        IReadOnlyList<AuditConflict> c = HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 4, 4) }, Box(1, 4 + G, 1, 3, 8, 3));
        AuditConflict face = Assert.Single(c, x => x.PlacedFace >= 0 && x.CandidateFace >= 0);
        Assert.Equal(G, face.Delta, 9);
        Assert.Contains("mm off one plane", face.ToString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_Floors_Do_Not_Conflict()
    {
        // Two overlapping boxes standing on the same exact floor plane: coincident, allowed.
        IReadOnlyList<AuditConflict> c = HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 3, 4) }, Box(2, 0, 2, 6, 5, 6));
        Assert.Empty(c);
    }

    [Fact]
    public void Boxes_A_Centimetre_Apart_Do_Not_Conflict()
    {
        IReadOnlyList<AuditConflict> c = HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 4, 4) }, Box(1, 4 + (10 * G), 1, 3, 8, 3));
        Assert.Empty(c);
    }

    [Fact]
    public void Faces_That_Only_Share_An_Edge_Do_Not_Conflict()
    {
        // Two slabs of a tilted plane meeting along a shared edge (neighbours, not a double surface):
        // their top faces are 1 mm off each other's plane but share the edge, so they are allowed.
        var a = new List<Vec3d> { new(0, 0, 0), new(4, 0, 0), new(0, 0, 4), new(4, 0, 4), new(0, 1, 0), new(4, 1, 0), new(0, 1, 4), new(4, 1, 4) };
        var b = new List<Vec3d> { new(4, 0, 0), new(8, 0, 0), new(4, 0, 4), new(8, 0, 4), new(4, 1, 0), new(8, 1 + G, 0), new(4, 1, 4), new(8, 1 + G, 4) };
        HullResult ha = ConvexHull3.Build(a, new HullOptions(), out string? _)!;
        HullResult hb = ConvexHull3.Build(b, new HullOptions(), out string? _)!;
        Assert.Empty(HullAudit.Conflicts(new[] { ha }, hb));
    }

    [Fact]
    public void Near_Coplanar_Faces_That_Do_Not_Overlap_Do_Not_Conflict()
    {
        // Two separate boxes whose tops are 1 mm apart in height but side by side with a 10 cm gap:
        // the tops never cover a common patch, so no double surface can form.
        IReadOnlyList<AuditConflict> c = HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 4, 4) }, Box(4 + (102 * G), 0, 0, 8, 4 + G, 4));
        Assert.Empty(c);
    }

    [Fact]
    public void Overlapping_Near_Coplanar_Faces_Conflict_Even_Facing_Opposite_Ways()
    {
        // b's bottom sits 2 mm below a's top over a common patch: a sub-epsilon membrane band.
        IReadOnlyList<AuditConflict> c = HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 4, 4) }, Box(1, 4 - (2 * G), 1, 3, 8, 3));
        Assert.Contains(c, x => x.Delta > 1e-6 && x.Delta < 0.005);
    }

    [Fact]
    public void Shapes_Whose_Bounds_Do_Not_Touch_Are_Skipped()
    {
        Assert.Empty(HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 4, 4) }, Box(10, 0, 10, 14, 4 + G, 14)));
    }

    [Fact]
    public void A_Corner_Just_Off_Another_Face_Conflicts()
    {
        // A pyramid whose apex stops 2 grid cells (about 2 mm) short of the first box's top face, inside it.
        var pyramid = new List<Vec3d> { new(1, 5, 1), new(3, 5, 1), new(1, 5, 3), new(3, 5, 3), new(2, 4 + (2 * G), 2) };
        HullResult apex = ConvexHull3.Build(pyramid, new HullOptions(), out string? _)!;
        IReadOnlyList<AuditConflict> c = HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 4, 4) }, apex);
        Assert.Contains(c, x => x.CandidateFace < 0 && Math.Abs(x.Delta - (2 * G)) < 1e-9);

        // Touching exactly is fine.
        pyramid[4] = new Vec3d(2, 4, 2);
        Assert.Empty(HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 4, 4) }, ConvexHull3.Build(pyramid, new HullOptions(), out string? _)!));
    }

    [Fact]
    public void A_Corner_Just_Beside_Another_Edge_Conflicts()
    {
        // Two boxes on one floor: the second's corner sits 1 grid cell beside the first's bottom edge (a near
        // T-junction in the shared floor plane).
        IReadOnlyList<AuditConflict> c = HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 3, 4) }, Box(4 + G, 0, 2, 8, 3, 6));
        Assert.Contains(c, x => Math.Abs(x.Delta - G) < 1e-9);
        Assert.Null(HullAudit.NearEdge(new Vec3d(2, 0, 0), new Vec3d(0, 0, 0), new Vec3d(4, 0, 0)));
        Assert.Equal(G, HullAudit.NearEdge(new Vec3d(2, G, 0), new Vec3d(0, 0, 0), new Vec3d(4, 0, 0))!.Value, 12);
    }
    private static HullResult Hull(params Vec3d[] pts) => ConvexHull3.Build(pts, new HullOptions(), out string? error) ?? throw new System.InvalidOperationException(error);

    [Fact]
    public void A_Fold_Under_Half_A_Degree_Between_Neighbours_Is_Found_And_A_Clear_One_Is_Not()
    {
        // b continues a's top past x = 4, rising by rise over 4 m: 21 grid steps is 0.29° (inside the
        // compiler's 0.44° plane-merge angle), 72 steps is 1.0°.
        HullResult a = Box(0, 0, 0, 4, 1, 4);
        HullResult B(int rise) => Hull(new(4, 0, 0), new(8, 0, 0), new(4, 0, 4), new(8, 0, 4), new(4, 1, 0), new(4, 1, 4), new(8, 1 + (rise * G), 0), new(8, 1 + (rise * G), 4));
        var placed = new[] { AuditShape.FromHull(a, "a") };
        AuditConflict fold = Assert.Single(HullAudit.Folds(placed, AuditShape.FromHull(B(21), "b")));
        Assert.Equal(AuditConflictKind.Fold, fold.Kind);
        Assert.Contains("fold by under 0.5°", fold.ToString(), System.StringComparison.Ordinal);
        Assert.Empty(HullAudit.Folds(placed, AuditShape.FromHull(B(72), "b")));
        Assert.Empty(HullAudit.Folds(placed, AuditShape.FromHull(B(0), "b")));
    }

    [Fact]
    public void A_Near_Flat_Fold_Inside_One_Hull_Is_Found()
    {
        // A roof ridge at x = 4: each half tilts by about 0.15°, so the two roof faces fold by about 0.3°.
        HullResult Roof(int rise) => Hull(new(0, 0, 0), new(8, 0, 0), new(0, 0, 4), new(8, 0, 4), new(0, 1, 0), new(0, 1, 4),
            new(4, 1 + (rise * G), 0), new(4, 1 + (rise * G), 4), new(8, 1, 0), new(8, 1, 4));
        AuditConflict self = Assert.Single(HullAudit.SelfFolds(AuditShape.FromHull(Roof(11), "roof")));
        Assert.True(self.PlacedIndex < 0 && self.Kind == AuditConflictKind.Fold);
        Assert.Empty(HullAudit.SelfFolds(AuditShape.FromHull(Roof(72), "roof")));
        Assert.Empty(HullAudit.SelfFolds(AuditShape.FromHull(Box(0, 0, 0, 4, 1, 4), "box")));
    }

    [Fact]
    public void Faces_Crossing_At_A_Shallow_Angle_Conflict_Even_With_Their_Corners_A_Centimetre_Apart()
    {
        // b's bottom runs from 10 grid steps under a's top (x = 0) to 10 over it (x = 4): 0.28° apart, every corner
        // about 1 cm off the other plane, yet the two faces cross inside the patch they share.
        HullResult b = Hull(new(0, 4 - (10 * G), 0), new(4, 4 + (10 * G), 0), new(0, 4 - (10 * G), 4), new(4, 4 + (10 * G), 4),
            new(0, 6, 0), new(4, 6, 0), new(0, 6, 4), new(4, 6, 4));
        IReadOnlyList<AuditConflict> c = HullAudit.Conflicts(new[] { AuditShape.FromHull(Box(0, 0, 0, 4, 4, 4), "a") }, AuditShape.FromHull(b, "b"));
        Assert.Contains(c, x => x.PlacedFace >= 0 && x.CandidateFace >= 0 && x.Kind == AuditConflictKind.NearCoplanar);
    }

    [Fact]
    public void A_Corner_Just_Beside_Another_Corner_Conflicts()
    {
        // Two boxes touching only near one corner: 2 grid steps apart on every axis (3.4 mm), no face or edge close.
        IReadOnlyList<AuditConflict> c = HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 4, 4) }, Box(4 + (2 * G), 4 + (2 * G), 4 + (2 * G), 8, 8, 8));
        AuditConflict corner = Assert.Single(c);
        Assert.True(corner.PlacedFace < 0 && corner.CandidateFace < 0);
        Assert.Equal(System.Math.Sqrt(3) * 2 * G, corner.Delta, 9);
        Assert.Contains("from a corner of", corner.ToString(), System.StringComparison.Ordinal);
        Assert.Empty(HullAudit.Conflicts(new[] { Box(0, 0, 0, 4, 4, 4) }, Box(4, 4, 4, 8, 8, 8)));
    }

    [Fact]
    public void Two_Edges_Passing_Just_Beside_Each_Other_Conflict()
    {
        // b's edge crosses over a's top edge (x = 4, y = 4) diagonally, 1.4 mm clear of it at both edges' middles.
        HullResult b = Hull(new(3 + G, 5 + G, 2), new(5 + G, 3 + G, 2), new(5.5, 5.5, 1.5), new(5.5, 5.5, 2.5));
        IReadOnlyList<AuditConflict> c = HullAudit.Conflicts(new[] { AuditShape.FromHull(Box(0, 0, 0, 4, 4, 4), "a") }, AuditShape.FromHull(b, "b"));
        AuditConflict edge = Assert.Single(c);
        Assert.Equal(AuditConflictKind.Edges, edge.Kind);
        Assert.Equal(System.Math.Sqrt(2) * G, edge.Delta, 9);
        Assert.Null(HullAudit.NearEdges(new Vec3d(0, 0, 0), new Vec3d(4, 0, 0), new Vec3d(2, -1, 0), new Vec3d(2, 1, 0)));
        Assert.Equal(G, HullAudit.NearEdges(new Vec3d(0, 0, 0), new Vec3d(4, 0, 0), new Vec3d(2, -1, G), new Vec3d(2, 1, G))!.Value, 12);
        Assert.Equal(G, HullAudit.NearEdges(new Vec3d(0, 0, 0), new Vec3d(4, 0, 0), new Vec3d(1, G, 0), new Vec3d(3, G, 0))!.Value, 12);
    }
}
