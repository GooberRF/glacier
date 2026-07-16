using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editing.Graph;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for Link Graph 2.0: graph building (Show All vs selection component, kind
/// filter, search, dangling targets), layout persistence round-trip, layered
/// auto-layout (full + additive), and create/break through the panel editor API
/// with undo verified and validation refusals.
/// </summary>
public sealed class LinkGraphTests
{
    private static EditorDocument NewDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xB4;
        rfl.Header.LevelName = "graphtest";
        return new EditorDocument(rfl);
    }

    private static LevelObject Place(EditorDocument doc, LevelObjectKind kind) => doc.PlaceObject(kind, Vec3.Zero)!;

    private static LevelObject PlaceEvent(EditorDocument doc, string cls) =>
        doc.PlaceEvent(EventSchemaCatalog.Find(cls)!, Vec3.Zero)!;

    // ─── Graph building ──────────────────────────────────────────────────────

    [Fact]
    public void Build_ShowAll_Returns_Every_Component_But_Selection_Returns_One()
    {
        var doc = NewDoc();
        var links = new LinkService(doc);

        // Component A: t1 -> e1.  Component B: t2 -> e2 (disjoint).
        var t1 = Place(doc, LevelObjectKind.Trigger);
        var e1 = PlaceEvent(doc, "Delay");
        var t2 = Place(doc, LevelObjectKind.Trigger);
        var e2 = PlaceEvent(doc, "Delay");
        links.LinkOneToMany(t1, new[] { e1 });
        links.LinkOneToMany(t2, new[] { e2 });

        LinkGraph all = LinkGraphModel.Build(doc, new LinkGraphFilter { ShowAll = true });
        Assert.Equal(4, all.Nodes.Count);
        Assert.Equal(2, all.Edges.Count);

        // Selecting t1 (not Show All) restricts to component A.
        LinkGraph component = LinkGraphModel.Build(doc, new LinkGraphFilter { SelectionUids = new[] { t1.Uid } });
        Assert.Equal(new[] { t1.Uid, e1.Uid }.OrderBy(x => x), component.Nodes.Select(n => n.Uid).OrderBy(x => x));
        Assert.Single(component.Edges);
    }

    [Fact]
    public void Build_KindFilter_Hides_A_Category_And_Its_Edges()
    {
        var doc = NewDoc();
        var links = new LinkService(doc);
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var ev = PlaceEvent(doc, "Delay");
        var target = Place(doc, LevelObjectKind.Target);
        // Trigger originates to both an event and a target (triggers link to anything).
        links.LinkOneToMany(trigger, new[] { ev, target });

        var filter = new LinkGraphFilter { ShowAll = true };
        filter.Categories.Add(GraphNodeCategory.Trigger);
        filter.Categories.Add(GraphNodeCategory.Target); // events unchecked
        LinkGraph g = LinkGraphModel.Build(doc, filter);

        Assert.DoesNotContain(g.Nodes, n => n.Uid == ev.Uid);
        Assert.Contains(g.Nodes, n => n.Uid == trigger.Uid);
        Assert.Contains(g.Nodes, n => n.Uid == target.Uid);
        // The trigger→event edge is dropped (event hidden); trigger→target survives.
        Assert.DoesNotContain(g.Edges, e => e.From == ev.Uid || e.To == ev.Uid);
        Assert.Contains(g.Edges, e => e.From == trigger.Uid && e.To == target.Uid);
    }

    [Fact]
    public void Build_Search_Matches_Uid_Script_And_Class()
    {
        var doc = NewDoc();
        var links = new LinkService(doc);
        var trigger = Place(doc, LevelObjectKind.Trigger);
        trigger.ScriptName = "alpha_trigger";
        var ev = PlaceEvent(doc, "Delay");
        ev.ScriptName = "beta_event";
        links.LinkOneToMany(trigger, new[] { ev });

        LinkGraph byScript = LinkGraphModel.Build(doc, new LinkGraphFilter { ShowAll = true, Search = "alpha" });
        Assert.Single(byScript.Nodes);
        Assert.Equal(trigger.Uid, byScript.Nodes[0].Uid);

        LinkGraph byUid = LinkGraphModel.Build(doc, new LinkGraphFilter { ShowAll = true, Search = ev.Uid.ToString() });
        Assert.Contains(byUid.Nodes, n => n.Uid == ev.Uid);

        LinkGraph byClass = LinkGraphModel.Build(doc, new LinkGraphFilter { ShowAll = true, Search = "Delay" });
        Assert.Contains(byClass.Nodes, n => n.Uid == ev.Uid);
    }

    [Fact]
    public void Build_Includes_Dangling_Target_As_Missing_Node()
    {
        var doc = NewDoc();
        var trigger = Place(doc, LevelObjectKind.Trigger);
        ((Trigger)trigger.Model).Links.Add(999999); // points at a non-existent UID

        LinkGraph g = LinkGraphModel.Build(doc, new LinkGraphFilter { ShowAll = true });
        LinkGraphNode? missing = g.Node(999999);
        Assert.NotNull(missing);
        Assert.True(missing!.Missing);
        Assert.Null(missing.Kind);
        Assert.Contains(g.Edges, e => e.From == trigger.Uid && e.To == 999999);
    }

    // ─── Layout persistence ──────────────────────────────────────────────────

    [Fact]
    public void Layout_RoundTrips_Through_Json()
    {
        var layout = new GraphLayout();
        layout.Set(1, 20.5, 40.25);
        layout.Set(7, -100, 300);
        layout.Set(42, 12345.5, 6789.0);

        string json = GraphLayoutStore.Serialize(layout);
        GraphLayout back = GraphLayoutStore.Deserialize(json);

        Assert.Equal(layout.Count, back.Count);
        foreach (var kv in layout.Positions)
        {
            Assert.True(back.TryGet(kv.Key, out double x, out double y));
            Assert.Equal(kv.Value.X, x, 6);
            Assert.Equal(kv.Value.Y, y, 6);
        }
    }

    [Fact]
    public void Sidecar_Path_Is_Level_Name_With_GedLayout_Suffix()
    {
        string p = GraphLayoutStore.SidecarPathFor(System.IO.Path.Combine("maps", "dm01.rfl"));
        Assert.Equal(System.IO.Path.Combine("maps", "dm01.gedlayout.json"), p);
        Assert.EndsWith(".gedlayout.json", p);
    }

    [Fact]
    public void Missing_Sidecar_Loads_As_Empty_Layout()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ged_no_such_" + System.Guid.NewGuid().ToString("N") + ".gedlayout.json");
        GraphLayout layout = GraphLayoutStore.Load(path);
        Assert.Equal(0, layout.Count);
    }

    // ─── Auto-layout ─────────────────────────────────────────────────────────

    [Fact]
    public void AutoLayout_Places_Nodes_In_Kind_Columns()
    {
        var doc = NewDoc();
        var links = new LinkService(doc);
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var ev = PlaceEvent(doc, "Delay");
        var target = Place(doc, LevelObjectKind.Target);
        links.LinkOneToMany(trigger, new[] { ev, target });

        LinkGraph g = LinkGraphModel.Build(doc, new LinkGraphFilter { ShowAll = true });
        GraphLayout layout = GraphAutoLayout.Build(g);

        Assert.Equal(3, layout.Count);
        layout.TryGet(trigger.Uid, out double tx, out _);
        layout.TryGet(ev.Uid, out double ex, out _);
        layout.TryGet(target.Uid, out double sx, out _);
        // Triggers left of events left of targets (column order).
        Assert.True(tx < ex);
        Assert.True(ex < sx);
    }

    [Fact]
    public void AutoLayout_Additive_Keeps_Saved_Positions_And_Places_Only_New()
    {
        var doc = NewDoc();
        var links = new LinkService(doc);
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var ev = PlaceEvent(doc, "Delay");
        links.LinkOneToMany(trigger, new[] { ev });

        LinkGraph g1 = LinkGraphModel.Build(doc, new LinkGraphFilter { ShowAll = true });
        var layout = new GraphLayout();
        layout.Set(trigger.Uid, 555, 777); // user-arranged position

        // A newly added event node gets placed; the arranged trigger is untouched.
        GraphAutoLayout.Apply(g1, layout, relayoutAll: false);
        layout.TryGet(trigger.Uid, out double tx, out double ty);
        Assert.Equal(555, tx);
        Assert.Equal(777, ty);
        Assert.True(layout.Has(ev.Uid));

        // Re-layout all discards the manual position.
        GraphAutoLayout.Apply(g1, layout, relayoutAll: true);
        layout.TryGet(trigger.Uid, out double tx2, out _);
        Assert.NotEqual(555, tx2);
    }

    // ─── Editor API: create / break / validate ───────────────────────────────

    [Fact]
    public void Editor_CreateLink_Commits_And_Is_Undoable()
    {
        var doc = NewDoc();
        var editor = new LinkGraphEditor(doc);
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var ev = PlaceEvent(doc, "Delay");

        LinkResult r = editor.CreateLink(trigger.Uid, ev.Uid);
        Assert.True(r.Ok);
        Assert.Contains(ev.Uid, ((Trigger)trigger.Model).Links);

        doc.Undo.Undo();
        Assert.DoesNotContain(ev.Uid, ((Trigger)trigger.Model).Links);
        doc.Undo.Redo();
        Assert.Contains(ev.Uid, ((Trigger)trigger.Model).Links);
    }

    [Fact]
    public void Editor_BreakLink_Removes_The_Edge_And_Is_Undoable()
    {
        var doc = NewDoc();
        var editor = new LinkGraphEditor(doc);
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var ev = PlaceEvent(doc, "Delay");
        editor.CreateLink(trigger.Uid, ev.Uid);

        Assert.True(editor.BreakLink(trigger.Uid, ev.Uid));
        Assert.Empty(((Trigger)trigger.Model).Links);

        doc.Undo.Undo();
        Assert.Contains(ev.Uid, ((Trigger)trigger.Model).Links);
    }

    [Fact]
    public void Editor_ValidateDrop_Refuses_Invalid_Targets()
    {
        var doc = NewDoc();
        var editor = new LinkGraphEditor(doc);
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var entity = Place(doc, LevelObjectKind.Entity);
        var playSound = PlaceEvent(doc, "Play_Sound");
        var otherEvent = PlaceEvent(doc, "Delay");

        // Self-link.
        Assert.False(editor.ValidateDrop(trigger.Uid, trigger.Uid).Ok);
        // Non-originator source (an entity carries no Links).
        Assert.False(editor.ValidateDrop(entity.Uid, trigger.Uid).Ok);
        // Event whose schema forbids the target kind (Play_Sound → an event).
        Assert.False(editor.ValidateDrop(playSound.Uid, otherEvent.Uid).Ok);

        // A valid drop passes and does not mutate until committed.
        Assert.True(editor.ValidateDrop(trigger.Uid, playSound.Uid).Ok);
        Assert.Empty(((Trigger)trigger.Model).Links);

        // After committing, the same drop is now a rejected duplicate.
        editor.CreateLink(trigger.Uid, playSound.Uid);
        Assert.False(editor.ValidateDrop(trigger.Uid, playSound.Uid).Ok);
    }
}
