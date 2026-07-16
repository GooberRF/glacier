using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using ConnState = Ged.Core.Editing.NavGraphService.ConnectionState;

namespace Ged.Core.Tests;

/// <summary>
/// Covers the nav-graph service that backs the three formerly-stubbed AI-nav
/// commands: J cycle-connection, Calculate Nav Paths, and Waypoint List management.
/// </summary>
public sealed class NavGraphTests
{
    private static EditorDocument NewDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xB4;
        rfl.Header.LevelName = "navtest";
        return new EditorDocument(rfl);
    }

    private static LevelObject PlaceNav(EditorDocument doc, float x, int navType = 0)
    {
        LevelObject o = doc.PlaceObject(LevelObjectKind.NavPoint, new Vec3(x, 0, 0))!;
        ((NavPoint)o.Model).NavType = navType;
        return o;
    }

    // ---- Pure state logic -----------------------------------------------------

    [Fact]
    public void NextState_Cycles_None_Forward_Backward_Both()
    {
        Assert.Equal(ConnState.Forward, NavGraphService.NextState(ConnState.None));
        Assert.Equal(ConnState.Backward, NavGraphService.NextState(ConnState.Forward));
        Assert.Equal(ConnState.Both, NavGraphService.NextState(ConnState.Backward));
        Assert.Equal(ConnState.None, NavGraphService.NextState(ConnState.Both));
    }

    [Fact]
    public void StateOf_Reads_Directional_Links()
    {
        var a = new NavPoint { Uid = 1 };
        var b = new NavPoint { Uid = 2 };
        Assert.Equal(ConnState.None, NavGraphService.StateOf(a, b));

        a.Links.Add(2);
        Assert.Equal(ConnState.Forward, NavGraphService.StateOf(a, b));

        b.Links.Add(1);
        Assert.Equal(ConnState.Both, NavGraphService.StateOf(a, b));

        a.Links.Clear();
        Assert.Equal(ConnState.Backward, NavGraphService.StateOf(a, b));
    }

    // ---- Cycle connection (undo-safe) -----------------------------------------

    [Fact]
    public void CycleConnection_Walks_All_Four_States_And_Undoes()
    {
        var doc = NewDoc();
        var svc = new NavGraphService(doc);
        int ua = PlaceNav(doc, 0).Uid;
        int ub = PlaceNav(doc, 5).Uid;
        LevelObject a = doc.FindByUid(ua)!;
        LevelObject b = doc.FindByUid(ub)!;
        var na = (NavPoint)a.Model;
        var nb = (NavPoint)b.Model;

        Assert.Equal(ConnState.Forward, svc.CycleConnection(a, b));
        Assert.Contains(ub, na.Links);
        Assert.DoesNotContain(ua, nb.Links);

        Assert.Equal(ConnState.Backward, svc.CycleConnection(a, b));
        Assert.DoesNotContain(ub, na.Links);
        Assert.Contains(ua, nb.Links);

        Assert.Equal(ConnState.Both, svc.CycleConnection(a, b));
        Assert.Contains(ub, na.Links);
        Assert.Contains(ua, nb.Links);

        Assert.Equal(ConnState.None, svc.CycleConnection(a, b));
        Assert.Empty(na.Links);
        Assert.Empty(nb.Links);

        doc.Undo.Undo(); // reverts the last cycle (None) → Both
        Assert.Equal(ConnState.Both, NavGraphService.StateOf(na, nb));
    }

    // ---- Calculate nav paths --------------------------------------------------

    [Fact]
    public void ComputeProximityLinks_Connects_Near_SameType_Pairs_Only()
    {
        var p0 = new NavPoint { Uid = 10, Position = new Vec3(0, 0, 0), NavType = 0 };
        var p1 = new NavPoint { Uid = 11, Position = new Vec3(5, 0, 0), NavType = 0 };
        var p2 = new NavPoint { Uid = 12, Position = new Vec3(100, 0, 0), NavType = 0 }; // too far
        var p3 = new NavPoint { Uid = 13, Position = new Vec3(5, 0, 0), NavType = 1 };   // different type

        List<(int From, int To)> links = NavGraphService.ComputeProximityLinks(new[] { p0, p1, p2, p3 }, 10f);

        Assert.Contains((10, 11), links);
        Assert.Contains((11, 10), links); // mutual
        Assert.DoesNotContain((10, 12), links); // beyond distance
        Assert.DoesNotContain((10, 13), links); // different nav type
        Assert.Equal(2, links.Count);
    }

    [Fact]
    public void ComputeProximityLinks_Skips_Existing()
    {
        var p0 = new NavPoint { Uid = 1, Position = new Vec3(0, 0, 0) };
        var p1 = new NavPoint { Uid = 2, Position = new Vec3(3, 0, 0) };
        p0.Links.Add(2); // already linked one way

        List<(int From, int To)> links = NavGraphService.ComputeProximityLinks(new[] { p0, p1 }, 10f);

        Assert.DoesNotContain((1, 2), links);
        Assert.Contains((2, 1), links); // only the missing direction is added
        Assert.Single(links);
    }

    [Fact]
    public void CalculatePaths_Adds_Mutual_Links_And_Is_Undoable()
    {
        var doc = NewDoc();
        var svc = new NavGraphService(doc);
        int u0 = PlaceNav(doc, 0).Uid;
        int u1 = PlaceNav(doc, 4).Uid;

        int added = svc.CalculatePaths(10f);
        Assert.Equal(2, added);
        Assert.Contains(u1, ((NavPoint)doc.FindByUid(u0)!.Model).Links);
        Assert.Contains(u0, ((NavPoint)doc.FindByUid(u1)!.Model).Links);

        doc.Undo.Undo();
        Assert.Empty(((NavPoint)doc.FindByUid(u0)!.Model).Links);
        Assert.Empty(((NavPoint)doc.FindByUid(u1)!.Model).Links);
    }

    // ---- Waypoint lists -------------------------------------------------------

    [Fact]
    public void Waypoint_Lists_Add_Edit_Remove_RoundTrip_And_Undo()
    {
        var doc = NewDoc();
        var svc = new NavGraphService(doc);

        svc.AddWaypointList("patrol");
        Assert.Single(svc.WaypointLists);
        Assert.Equal("patrol", svc.WaypointLists[0].Name);

        svc.AddWaypoints(0, new[] { 3, 7, 9 });
        Assert.Equal(new[] { 3, 7, 9 }, svc.WaypointLists[0].WaypointIndices);

        svc.RemoveWaypointAt(0, 1); // drop the '7'
        Assert.Equal(new[] { 3, 9 }, svc.WaypointLists[0].WaypointIndices);

        svc.RenameWaypointList(0, "loop");
        Assert.Equal("loop", svc.WaypointLists[0].Name);

        // Persists through the section (round-trips on save).
        WaypointListsSection sec = svc.WaypointSection().Section;
        Assert.Same(sec, doc.Rfl.Sections.Select(s => s.Content).OfType<WaypointListsSection>().Single());

        doc.Undo.Undo(); // undo the rename
        Assert.Equal("patrol", svc.WaypointLists[0].Name);

        svc.RemoveWaypointList(0);
        Assert.Empty(svc.WaypointLists);
        doc.Undo.Undo(); // restore the deleted list
        Assert.Single(svc.WaypointLists);
    }
}
