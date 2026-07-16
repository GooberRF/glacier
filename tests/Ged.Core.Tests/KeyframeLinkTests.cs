using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editing.Graph;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Keyframes-as-objects + keyframe/mover links: mover keyframes are projected as
/// selectable, UID-resolvable level objects; the moving-group structural edges (member
/// mover -> start keyframe, keyframe sequence chain) surface through MovingGroupLinks /
/// DocumentLinks and into the Link Graph panel model exactly like event/trigger links.
/// </summary>
public sealed class KeyframeLinkTests
{
    /// <summary>A level with a two-brush mover and two keyframes (start + a raised one).</summary>
    private static (EditorDocument Doc, int B1, int B2, Group Group, Keyframe Start, Keyframe Top) NewMoverLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "kf.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        var doc = new EditorDocument(rfl);
        var editor = new BrushEditor(doc);
        int b1 = editor.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        int b2 = editor.CreateBrush(new BrushCreateParams(), new Vec3(4, 0, 0), Mat3.Identity);

        var movers = new MoverService(doc);
        Group group = movers.CreateMover(new[] { b1, b2 }, System.Array.Empty<int>(), "Elevator");
        Keyframe start = group.MovingData!.Keyframes[0];
        Keyframe top = movers.AddKeyframe(group, new Vec3(2, 8, 0), Mat3.Identity);
        doc.RefreshObjects();
        return (doc, b1, b2, group, start, top);
    }

    [Fact]
    public void Keyframes_Are_Projected_As_Resolvable_Level_Objects()
    {
        var (doc, _, _, _, start, top) = NewMoverLevel();

        LevelObject[] keyframes = doc.Objects.Where(o => o.Kind == LevelObjectKind.Keyframe).ToArray();
        Assert.Equal(2, keyframes.Length);

        LevelObject? s = doc.FindByUid(start.Uid);
        Assert.NotNull(s);
        Assert.Equal(LevelObjectKind.Keyframe, s!.Kind);
        Assert.Equal(new Vec3(2, 0, 0), s.Position);

        Assert.NotNull(doc.FindByUid(top.Uid));
    }

    [Fact]
    public void MovingGroupLinks_Yields_Member_To_Start_And_Sequence_Chain()
    {
        var (_, b1, b2, group, start, top) = NewMoverLevel();

        var edges = MovingGroupLinks.Edges(new[] { group }).ToList();

        // Each member mover links to the start keyframe.
        Assert.Contains((b1, start.Uid), edges);
        Assert.Contains((b2, start.Uid), edges);
        // The keyframe sequence chains start -> top.
        Assert.Contains((start.Uid, top.Uid), edges);
        // No self edges and exactly the three expected edges.
        Assert.Equal(3, edges.Count);
        Assert.DoesNotContain(edges, e => e.From == e.To);
    }

    [Fact]
    public void DocumentLinks_AllEdges_Includes_MovingGroup_And_Originator_Edges()
    {
        var (doc, b1, _, _, start, top) = NewMoverLevel();

        // Add an ordinary originator link (trigger -> mover) so both sources are present.
        LevelObject trigger = doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 0))!;
        ((Trigger)trigger.Model).Links.Add(b1);
        doc.RefreshObjects();

        var edges = DocumentLinks.AllEdges(doc).ToList();
        Assert.Contains((trigger.Uid, b1), edges);
        Assert.Contains((b1, start.Uid), edges);
        Assert.Contains((start.Uid, top.Uid), edges);
    }

    [Fact]
    public void LinkGraph_Shows_Keyframe_And_Mover_Nodes_And_Their_Edges()
    {
        var (doc, b1, _, _, start, top) = NewMoverLevel();

        LinkGraph g = LinkGraphModel.Build(doc, new LinkGraphFilter { ShowAll = true });

        Assert.Contains(g.Nodes, n => n.Kind == LevelObjectKind.Mover && n.Uid == b1);
        Assert.Contains(g.Nodes, n => n.Kind == LevelObjectKind.Keyframe && n.Uid == start.Uid);
        Assert.Contains(g.Nodes, n => n.Kind == LevelObjectKind.Keyframe && n.Uid == top.Uid);
        Assert.Contains(g.Edges, e => e.From == b1 && e.To == start.Uid);
        Assert.Contains(g.Edges, e => e.From == start.Uid && e.To == top.Uid);
    }

    [Fact]
    public void LinkGraph_Selecting_A_Mover_Restricts_To_Its_Keyframe_Component()
    {
        var (doc, b1, _, _, start, top) = NewMoverLevel();

        LinkGraph g = LinkGraphModel.Build(doc, new LinkGraphFilter { SelectionUids = new[] { b1 } });

        // The selection component brings in the mover's keyframes.
        Assert.Contains(g.Nodes, n => n.Uid == start.Uid);
        Assert.Contains(g.Nodes, n => n.Uid == top.Uid);
    }
}
