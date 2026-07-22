using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Ged.Core.Editing;
using Ged.Core.Input;
using Ged.Core.Model;
using Ged.Rendering.Picking;
using Brush = Ged.Core.Model.Brush;
using CoreVec3 = Ged.Core.Model.Vec3;
using Geometry = Ged.Core.Model.Geometry;

namespace Ged.App;

/// <summary>
/// Edge mode: the edge tool panel, the edge operators (bevel / extrude /
/// collapse / move), loop &amp; ring selection, edges↔verts/faces conversions, and the CPU
/// closest-edge-to-ray picking that seeds off the brush id-buffer hit.
/// </summary>
public sealed partial class MainWindow
{
    private const int EdgePickPixels = 9;

    private void InitEdges()
    {
        _dispatcher.Bind(CommandIds.EdgeBevel, () => _ = EdgeBevelDialogAsync());
        _dispatcher.Bind(CommandIds.EdgeExtrude, () => _ = EdgeExtrudeDialogAsync());
        _dispatcher.Bind(CommandIds.EdgeCollapse, () => EdgeSingle("Collapse edge", EdgeOps.Collapse));
        _dispatcher.Bind(CommandIds.EdgeLoopSelect, () => EdgeExpandSelection(loop: true));
        _dispatcher.Bind(CommandIds.EdgeRingSelect, () => EdgeExpandSelection(loop: false));
        _dispatcher.Bind(CommandIds.EdgeToVerts, EdgesToVertices);
        _dispatcher.Bind(CommandIds.EdgeToFaces, EdgesToFaces);
    }

    private Control BuildEdgePanel()
    {
        var root = new StackPanel { Margin = new Avalonia.Thickness(8), Spacing = 6 };
        root.Children.Add(Header("Edge Operations"));
        root.Children.Add(Note("Click an edge to select it (closest edge to the cursor); Ctrl-click adds. " +
            "Loop follows edges end-to-end across quads; Ring picks the parallel edges. Bevel/Extrude/Collapse act on one edge."));
        root.Children.Add(Row(Btn("Bevel…", () => _ = EdgeBevelDialogAsync()), Btn("Extrude…", () => _ = EdgeExtrudeDialogAsync())));
        root.Children.Add(Row(Btn("Collapse", () => EdgeSingle("Collapse edge", EdgeOps.Collapse)), Btn("Move…", () => _ = EdgeMoveDialogAsync())));
        root.Children.Add(Row(Btn("Select Loop", () => EdgeExpandSelection(loop: true)), Btn("Select Ring", () => EdgeExpandSelection(loop: false))));
        root.Children.Add(Header("Convert Selection"));
        root.Children.Add(Row(Btn("→ Vertices", EdgesToVertices), Btn("→ Faces", EdgesToFaces)));
        return root;
    }

    // ---- Picking --------------------------------------------------------------

    private bool HandleEdgePick(Viewport.IViewportSurface surface, PickId id, bool additive)
    {
        if (BrushEd is null)
        {
            return true;
        }

        int? brushUid = id.Kind == PickKind.Brush ? id.Index
            : (_session.TryResolveBrushFace(id, out int u, out _) ? u : (int?)null);

        bool selected = false;
        if (brushUid is int bu && BrushEd.FindBrush(bu) is Brush b &&
            surface.LastPickRay is (Vector3 ro, Vector3 rd))
        {
            var worldEdges = EdgeTopology.Edges(b.Geometry)
                .Select(e => (e, WorldVertex(b, e.V0), WorldVertex(b, e.V1)))
                .ToList();
            float tol = EdgePickTol(surface, b);
            if (EdgePicker.Pick(worldEdges, Cv(ro), Cv(rd), tol) is { } hit)
            {
                selected = additive
                    ? _session.Selection.ToggleEdge(bu, hit.V0, hit.V1)
                    : _session.Selection.SelectEdge(bu, hit.V0, hit.V1);
                if (selected)
                {
                    LastPickHighlight = id; // an accepted edge select lights its owning brush's pick
                }
            }
        }

        // Universal clear-on-empty (item 3): a non-additive click that selected no edge — empty
        // space, a wrong-kind hit, a brush with no edge under the cursor, or a locked brush (its
        // lock hint raised inside SelectEdge) — clears the edge selection.
        if (!selected && !additive)
        {
            BrushEd.ClearSelection();
        }

        return true;
    }

    private float EdgePickTol(Viewport.IViewportSurface s, Brush b) =>
        EdgePickPixels * (s.Camera?.WorldPerPixel(new Vector3(b.Position.X, b.Position.Y, b.Position.Z), s.SurfaceHeight) ?? 1f);

    private static CoreVec3 WorldVertex(Brush b, int index) =>
        b.Position.Add(b.Rotation.Transform(b.Geometry.Vertices[index]));

    private static CoreVec3 Cv(Vector3 v) => new(v.X, v.Y, v.Z);

    // ---- Operators ------------------------------------------------------------

    private async Task EdgeBevelDialogAsync()
    {
        string? text = await Dialogs.InputDialog.ShowAsync(this, "Edge Bevel", "Chamfer distance (m):", "0.25");
        if (TryParse1(text, out float d))
        {
            EdgeSingle("Bevel edge", (g, e) => EdgeOps.Bevel(g, e, d));
        }
    }

    private async Task EdgeExtrudeDialogAsync()
    {
        string? text = await Dialogs.InputDialog.ShowAsync(this, "Edge Extrude", "Distance (m):", _settings.GridSize.ToString(CultureInfo.InvariantCulture));
        if (TryParse1(text, out float d))
        {
            EdgeSingle("Extrude edge", (g, e) => EdgeOps.Extrude(g, e, d));
        }
    }

    private async Task EdgeMoveDialogAsync()
    {
        string? text = await Dialogs.InputDialog.ShowAsync(this, "Move Edges", "Delta X Y Z (m):", "0 1 0");
        if (!TryParse3(text, out float x, out float y, out float z) || BrushEd is null)
        {
            return;
        }

        var delta = new CoreVec3(x, y, z);
        var groups = BrushEd.SelectedEdges.GroupBy(e => e.Brush).ToList();
        if (groups.Count == 0)
        {
            _dispatcher.ShowMessage("Select an edge first.");
            return;
        }

        Ged.Core.Editor.UndoStack.Transaction? tx = groups.Count > 1 ? Document!.Undo.BeginTransaction("Move edges") : null;
        int triangulated = 0;
        foreach (var grp in groups)
        {
            var edges = grp.Select(e => BrushEdge.Canonical(e.V0, e.V1)).ToList();
            var endpoints = EdgeEndpointSet(edges);
            Report(BrushEd.EditBrushes(new[] { grp.Key }, "Move edges", b =>
            {
                // EdgeOps.Move keeps no per-frame planarize (the gizmo reuses it every drag frame),
                // so triangulate the faces this discrete move bent, scoped to the moved endpoints.
                OpResult r = EdgeOps.Move(b.Geometry, edges, delta);
                if (r)
                {
                    triangulated += FacePlanarizer.Planarize(b.Geometry, endpoints);
                }

                return r;
            }));
        }

        tx?.Commit();
        NotePlanarized(triangulated);
        AfterBrushEdit();
    }

    /// <summary>The distinct pool-index endpoints of a set of edges — the planarize scope for an edge move.</summary>
    private static HashSet<int> EdgeEndpointSet(IEnumerable<BrushEdge> edges)
    {
        var set = new HashSet<int>();
        foreach (BrushEdge e in edges)
        {
            set.Add(e.V0);
            set.Add(e.V1);
        }

        return set;
    }

    /// <summary>Applies a single-edge op to the first selected edge (topology-changing ops).</summary>
    private void EdgeSingle(string description, Func<Geometry, BrushEdge, OpResult> op)
    {
        if (BrushEd is null || BrushEd.SelectedEdges.Count == 0)
        {
            _dispatcher.ShowMessage("Select an edge first.");
            return;
        }

        (int brush, int v0, int v1) = BrushEd.SelectedEdges.First();
        OpResult r = BrushEd.EditBrushes(new[] { brush }, description, b => op(b.Geometry, BrushEdge.Canonical(v0, v1)));
        Report(r);
        NotePlanarized(r.FacesTriangulated); // Collapse/Bevel triangulate faces they bent (Extrude: 0)
        AfterBrushEdit();
    }

    // ---- Loop / ring selection ------------------------------------------------

    private void EdgeExpandSelection(bool loop)
    {
        if (BrushEd is null || BrushEd.SelectedEdges.Count == 0)
        {
            _dispatcher.ShowMessage("Select a seed edge first.");
            return;
        }

        var perBrush = new Dictionary<int, HashSet<BrushEdge>>();
        foreach (var grp in BrushEd.SelectedEdges.GroupBy(e => e.Brush))
        {
            if (BrushEd.FindBrush(grp.Key) is not Brush b)
            {
                continue;
            }

            var set = new HashSet<BrushEdge>();
            foreach ((int _, int v0, int v1) in grp)
            {
                BrushEdge seed = BrushEdge.Canonical(v0, v1);
                set.UnionWith(loop ? EdgeTopology.Loop(b.Geometry, seed) : EdgeTopology.Ring(b.Geometry, seed));
            }

            perBrush[grp.Key] = set;
        }

        bool first = true;
        foreach ((int brush, HashSet<BrushEdge> set) in perBrush)
        {
            _session.Selection.SelectEdges(brush, set, additive: !first);
            first = false;
        }

        RefreshSelectionOverlay();
        _dispatcher.ShowMessage($"Selected edge {(loop ? "loop" : "ring")} ({perBrush.Values.Sum(s => s.Count)} edges).");
    }

    // ---- Selection conversion -------------------------------------------------

    private void EdgesToVertices()
    {
        if (BrushEd is null || BrushEd.SelectedEdges.Count == 0)
        {
            return;
        }

        var verts = BrushEd.SelectedEdges
            .SelectMany(e => new[] { (e.Brush, e.V0), (e.Brush, e.V1) })
            .Distinct()
            .ToList();
        SetMode(EditMode.Vertex);
        foreach ((int brush, int v) in verts)
        {
            _session.Selection.SelectVertex(brush, v, additive: true);
        }

        RefreshSelectionOverlay();
    }

    private void EdgesToFaces()
    {
        if (BrushEd is null || BrushEd.SelectedEdges.Count == 0)
        {
            return;
        }

        var faces = new HashSet<(int Brush, int Face)>();
        foreach (var grp in BrushEd.SelectedEdges.GroupBy(e => e.Brush))
        {
            if (BrushEd.FindBrush(grp.Key) is not Brush b)
            {
                continue;
            }

            Dictionary<BrushEdge, List<(int Face, int Corner)>> adj = EdgeTopology.Adjacency(b.Geometry);
            foreach ((int _, int v0, int v1) in grp)
            {
                if (adj.TryGetValue(BrushEdge.Canonical(v0, v1), out List<(int Face, int Corner)>? f))
                {
                    foreach ((int fi, int _) in f)
                    {
                        faces.Add((grp.Key, fi));
                    }
                }
            }
        }

        SetMode(EditMode.Face);
        foreach ((int brush, int face) in faces)
        {
            _session.Selection.SelectFace(brush, face, additive: true);
        }

        RefreshSelectionOverlay();
    }
}
