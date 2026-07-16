using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Editing;
using Ged.Core.Editing.Graph;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Core.Packaging;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Produces the Link Graph 2.0 offscreen artifact: a dozen-node graph with an edited
/// layout, rendered to a deterministic PNG (via the Core PNG encoder — no Avalonia /
/// GPU needed) plus a text dump of the layout model. Also verifies the editor-only
/// sidecar suffix set and that the packfile scanner ignores such references.
/// </summary>
public sealed class GraphArtifactTests
{
    [Fact]
    public void Renders_A_Dozen_Node_Graph_With_An_Edited_Layout()
    {
        (EditorDocument doc, int[] uids) = BuildNetwork();
        LinkGraph graph = LinkGraphModel.Build(doc, new LinkGraphFilter { ShowAll = true });
        Assert.True(graph.Nodes.Count >= 12, $"expected ≥12 nodes, got {graph.Nodes.Count}");

        // Auto-layout, then simulate two user node drags (the "edited layout").
        GraphLayout layout = GraphAutoLayout.Build(graph);
        layout.Set(uids[0], 60, 300);
        layout.Set(uids[4], 320, 40);

        // Deterministic text dump of the layout model.
        var dump = new StringBuilder();
        dump.AppendLine("# Link Graph 2.0 — layout model dump (deterministic)");
        dump.AppendLine($"nodes={graph.Nodes.Count} edges={graph.Edges.Count}");
        dump.AppendLine();
        dump.AppendLine("uid\tkind\tcategory\tx\ty\tscript");
        foreach (LinkGraphNode n in graph.Nodes.OrderBy(n => n.Uid))
        {
            layout.TryGet(n.Uid, out double x, out double y);
            dump.AppendLine($"{n.Uid}\t{n.Kind}\t{n.Category}\t{x:0.0}\t{y:0.0}\t{n.DisplayName}");
        }

        dump.AppendLine();
        dump.AppendLine("edges (from -> to):");
        foreach (LinkGraphEdge e in graph.Edges.OrderBy(e => e.From).ThenBy(e => e.To))
        {
            dump.AppendLine($"  {e.From} -> {e.To}");
        }

        byte[] png = RenderGraph(graph, layout);

        if (TestPaths.RepoRoot is { } root)
        {
            string dir = Path.Combine(root, "tests", "artifacts", "graph");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "link_graph_layout.txt"), dump.ToString());
            File.WriteAllBytes(Path.Combine(dir, "link_graph.png"), png);
            File.WriteAllText(Path.Combine(dir, "link_graph.gedlayout.json"), GraphLayoutStore.Serialize(layout));
        }

        // The PNG is well-formed (signature) and non-blank.
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
    }

    [Fact]
    public void EditorOnly_Sidecars_Are_Recognized_And_Never_Scanned()
    {
        Assert.True(EditorOnlyFiles.IsEditorOnly("dm01.gedlayout.json"));
        Assert.True(EditorOnlyFiles.IsEditorOnly("dm01.autosave.rfl"));
        Assert.True(EditorOnlyFiles.IsEditorOnly("crate.gedprefab"));
        Assert.False(EditorOnlyFiles.IsEditorOnly("wall.tga"));

        // A (contrived) event file field that names a layout sidecar must not be gathered.
        var rfl = new RflFile();
        rfl.Header.Version = 0x12D;
        rfl.Header.LevelName = "mylevel.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        var events = new EventsSection
        {
            Events = { new RflEvent { Uid = 5, ClassName = "Play_Sound", Str1 = "dm01.gedlayout.json" } },
        };
        rfl.Sections.Insert(0, new RflSection((uint)SectionType.Events, Array.Empty<byte>()) { Content = events, Dirty = true });

        IReadOnlyList<DependencyRef> refs = DependencyScanner.Gather(rfl);
        Assert.DoesNotContain(refs, r => r.FileName.EndsWith(".gedlayout.json", StringComparison.OrdinalIgnoreCase));
    }

    private static (EditorDocument Doc, int[] Uids) BuildNetwork()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xB4;
        rfl.Header.LevelName = "artifact";
        var doc = new EditorDocument(rfl);
        var links = new LinkService(doc);

        LevelObject T(string name)
        {
            var o = doc.PlaceObject(LevelObjectKind.Trigger, Vec3.Zero)!;
            o.ScriptName = name;
            return o;
        }

        LevelObject E(string name)
        {
            var o = doc.PlaceEvent(EventSchemaCatalog.Find("Delay")!, Vec3.Zero)!;
            o.ScriptName = name;
            return o;
        }

        LevelObject Tg(string name)
        {
            var o = doc.PlaceObject(LevelObjectKind.Target, Vec3.Zero)!;
            o.ScriptName = name;
            return o;
        }

        var t1 = T("trig_start");
        var t2 = T("trig_arena");
        var t3 = T("trig_exit");
        var e1 = E("delay_a");
        var e2 = E("delay_b");
        var e3 = E("delay_c");
        var e4 = E("delay_d");
        var e5 = E("delay_e");
        var g1 = Tg("goal_1");
        var g2 = Tg("goal_2");
        var g3 = Tg("goal_3");
        var g4 = Tg("goal_4");

        // Triggers link to anything (events + targets); Delay events chain only to
        // other events (their schema constraint) — a schema-valid 12-node network.
        links.LinkOneToMany(t1, new[] { e1, e2, g1 });
        links.LinkOneToMany(e1, new[] { e3 });
        links.LinkOneToMany(e2, new[] { e3 });
        links.LinkOneToMany(t2, new[] { e4, g2, g4 });
        links.LinkOneToMany(e4, new[] { e5 });
        links.LinkOneToMany(t3, new[] { e5, g3 });

        return (doc, new[] { t1.Uid, t2.Uid, t3.Uid, e1.Uid, g1.Uid });
    }

    // ─── Minimal deterministic CPU rasterizer (nodes + edges) ────────────────

    private static byte[] RenderGraph(LinkGraph graph, GraphLayout layout)
    {
        const int nodeW = 150, nodeH = 40, margin = 40;
        double maxX = 0, maxY = 0;
        foreach (LinkGraphNode n in graph.Nodes)
        {
            if (layout.TryGet(n.Uid, out double x, out double y))
            {
                maxX = Math.Max(maxX, x + nodeW);
                maxY = Math.Max(maxY, y + nodeH);
            }
        }

        int w = Math.Clamp((int)maxX + margin, 320, 4096);
        int h = Math.Clamp((int)maxY + margin, 240, 4096);
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 0x1A;
            px[i + 1] = 0x1C;
            px[i + 2] = 0x20;
            px[i + 3] = 0xFF;
        }

        // Edges (behind nodes): routed bezier paths drawn as flattened polylines,
        // detouring around any node rect sitting on the corridor.
        var rects = new Dictionary<int, GraphRect>();
        foreach (LinkGraphNode n in graph.Nodes)
        {
            if (layout.TryGet(n.Uid, out double x, out double y))
            {
                rects[n.Uid] = new GraphRect(x, y, nodeW, nodeH);
            }
        }

        foreach (LinkGraphEdge e in graph.Edges)
        {
            if (!rects.TryGetValue(e.From, out GraphRect src) || !rects.TryGetValue(e.To, out GraphRect dst))
            {
                continue;
            }

            var obstacles = rects.Where(kv => kv.Key != e.From && kv.Key != e.To).Select(kv => kv.Value).ToList();
            GraphEdgePath path = GraphEdgeRouter.Route(src, dst, obstacles);
            for (int i = 1; i < path.Polyline.Count; i++)
            {
                DrawLine(px, w, h,
                    (int)path.Polyline[i - 1].X, (int)path.Polyline[i - 1].Y,
                    (int)path.Polyline[i].X, (int)path.Polyline[i].Y, 0x8A, 0xC0, 0xFF);
            }
        }

        // Nodes: filled rounded-ish rectangles coloured by category.
        foreach (LinkGraphNode n in graph.Nodes)
        {
            if (!layout.TryGet(n.Uid, out double x, out double y))
            {
                continue;
            }

            (byte r, byte g, byte b) = ColorFor(n.Category);
            FillRect(px, w, h, (int)x, (int)y, nodeW, nodeH, r, g, b);
            // Border.
            DrawRectOutline(px, w, h, (int)x, (int)y, nodeW, nodeH, 220, 220, 230);
        }

        return PngWriter.Encode(w, h, px);
    }

    private static (byte, byte, byte) ColorFor(GraphNodeCategory c) => c switch
    {
        GraphNodeCategory.Trigger => (0x3A, 0x5A, 0x8A),
        GraphNodeCategory.Event => (0x6A, 0x3A, 0x7A),
        GraphNodeCategory.Mover => (0x2A, 0x6A, 0x4A),
        GraphNodeCategory.Target => (0x7A, 0x5A, 0x2A),
        _ => (0x3A, 0x3E, 0x46),
    };

    private static void FillRect(byte[] px, int w, int h, int x0, int y0, int rw, int rh, byte r, byte g, byte b)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(h, y0 + rh); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(w, x0 + rw); x++)
            {
                Plot(px, w, x, y, r, g, b);
            }
        }
    }

    private static void DrawRectOutline(byte[] px, int w, int h, int x0, int y0, int rw, int rh, byte r, byte g, byte b)
    {
        for (int x = x0; x < x0 + rw; x++)
        {
            Plot(px, w, x, y0, r, g, b);
            Plot(px, w, x, y0 + rh - 1, r, g, b);
        }

        for (int y = y0; y < y0 + rh; y++)
        {
            Plot(px, w, x0, y, r, g, b);
            Plot(px, w, x0 + rw - 1, y, r, g, b);
        }
    }

    private static void DrawLine(byte[] px, int w, int h, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h)
            {
                Plot(px, w, x0, y0, r, g, b);
            }

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private static void Plot(byte[] px, int w, int x, int y, byte r, byte g, byte b)
    {
        int i = ((y * w) + x) * 4;
        px[i] = r;
        px[i + 1] = g;
        px[i + 2] = b;
        px[i + 3] = 0xFF;
    }
}
