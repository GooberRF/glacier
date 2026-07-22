using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Ged.App.Dialogs;
using Ged.Core.Editing;
using Ged.Core.Model;
using Brush = Ged.Core.Model.Brush;
using Geometry = Ged.Core.Model.Geometry;
using Vec3 = Ged.Core.Model.Vec3;

namespace Ged.App;

/// <summary>The Brush / Face / Vertex tool panels and the operator executors they drive.</summary>
public sealed partial class MainWindow
{
    // ---- Panel construction ---------------------------------------------------

    private static Control Placeholder(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Avalonia.Thickness(10),
        Foreground = Brushes.Gray,
    };

    private Control BuildBrushPanel()
    {
        var root = new StackPanel { Margin = new Avalonia.Thickness(8), Spacing = 6 };

        var shape = new ComboBox { ItemsSource = Enum.GetValues<BrushShape>(), SelectedItem = _brushParams.Shape, HorizontalAlignment = HorizontalAlignment.Stretch };
        shape.SelectionChanged += (_, _) =>
        {
            if (shape.SelectedItem is BrushShape s)
            {
                _brushParams.Shape = s;
                if (s == BrushShape.Mesh)
                {
                    _ = BrowseMeshAsync();
                }

                RefreshSelectionOverlay();
            }
        };

        root.Children.Add(Header("Create Brush"));
        root.Children.Add(Labeled("Shape", shape));
        root.Children.Add(Num("Width (X)", _brushParams.Width, v => _brushParams.Width = v));
        root.Children.Add(Num("Height (Y)", _brushParams.Height, v => _brushParams.Height = v));
        root.Children.Add(Num("Depth (Z)", _brushParams.Depth, v => _brushParams.Depth = v));
        root.Children.Add(IntNum("W Splits / sides", _brushParams.WidthSplits, v => _brushParams.WidthSplits = v));
        root.Children.Add(IntNum("H Splits / stacks", _brushParams.HeightSplits, v => _brushParams.HeightSplits = v));
        root.Children.Add(IntNum("D Splits", _brushParams.DepthSplits, v => _brushParams.DepthSplits = v));

        var air = Check("Air (else Solid)", _brushParams.Air, v => _brushParams.Air = v);
        root.Children.Add(air);
        root.Children.Add(Check("Is Portal", _brushParams.Portal, v => _brushParams.Portal = v));
        root.Children.Add(Check("Is Detail", _brushParams.Detail, v => _brushParams.Detail = v));
        root.Children.Add(Check("Emits Steam (≤3 jets)", _brushParams.EmitsSteam, v => _brushParams.EmitsSteam = v));
        root.Children.Add(Check("Is Geoable [ALPINE]", _brushParams.Geoable, v => _brushParams.Geoable = v));

        var material = new ComboBox { ItemsSource = new[] { "Glass", "Rock", "Wood", "Metal", "Cement", "Ice" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(Labeled("Breakable Material (applied at build)", material));
        root.Children.Add(IntNum("Life (-1 = infinite)", _brushParams.Life, v => _brushParams.Life = v));

        root.Children.Add(Row(Btn("Create Brush", CreateBrushFromPanel), Btn("Draw Brush", () => _dispatcher.Invoke(Ged.Core.Input.CommandIds.BrushDraw))));
        root.Children.Add(Note("B snaps the cutter ghost to the camera; Create places it. Draw Brush draws a box interactively: base point, rectangle, height (ESC cancels). Default texture until Texture mode."));

        root.Children.Add(Header("Operators"));
        root.Children.Add(Row(Btn("Clip X", () => ClipSelected(0)), Btn("Clip Y", () => ClipSelected(1)), Btn("Clip Z", () => ClipSelected(2))));
        root.Children.Add(Row(Check("Cut", _clipCut, v => _clipCut = v), Check("Flip", _clipFlip, v => _clipFlip = v)));
        root.Children.Add(Btn("Clip (two-point plane)…", ShowClipDialog));
        root.Children.Add(Row(Btn("Move Tool", () => SetGizmoTool(Ged.Core.Editing.GizmoTool.Move)), Btn("Rotate Tool", () => SetGizmoTool(Ged.Core.Editing.GizmoTool.Rotate)), Btn("Scale Tool", () => SetGizmoTool(Ged.Core.Editing.GizmoTool.Scale))));
        root.Children.Add(Row(Btn("Fuse", FuseSelected), Btn("Delete", DeleteSelectedBrushes)));
        root.Children.Add(Row(Btn("Mirror X", () => MirrorSelected(0)), Btn("Mirror Y", () => MirrorSelected(1)), Btn("Mirror Z", () => MirrorSelected(2))));
        root.Children.Add(Row(Btn("Stretch…", () => _ = StretchDialogAsync()), Btn("Bend…", () => _ = BendDialogAsync(null)), Btn("Twist…", () => _ = TwistDialogAsync(null))));
        root.Children.Add(Row(Btn("Snap Grid", SnapSelectionToGrid), Btn("Reorient", ReorientSelected), Btn("Move Centers", MoveCentersSelected)));
        root.Children.Add(Row(Btn("Start of Time", StartOfTime), Btn("End of Time", EndOfTime)));
        root.Children.Add(Note("Carve subtracts one brush from another via CSG; mirror flips a group/object selection."));
        return root;
    }

    private Control BuildFacePanel()
    {
        var root = new StackPanel { Margin = new Avalonia.Thickness(8), Spacing = 6 };
        root.Children.Add(Header("Face Operations"));
        root.Children.Add(Note("Click faces to select; Ctrl-click adds. Shift+S grows to all faces of the brush; Shift+D selects same-texture faces."));
        root.Children.Add(Row(Btn("Extrude…", () => _ = ExtrudeDialogAsync()), Btn("Bevel…", () => _ = BevelDialogAsync())));
        root.Children.Add(Row(Btn("Flip Normal", () => FaceOpMulti("Flip normal", (g, f) => Aggregate(f, i => FaceOps.FlipNormal(g, i)))), Btn("Flip Edge", () => FaceOpMulti("Flip edge", FaceOps.FlipEdge))));
        root.Children.Add(Row(Btn("Triangulate", () => FaceOpEach("Triangulate", FaceOps.Triangulate)), Btn("Pinwheel", () => FaceOpEach("Pinwheel", FaceOps.Pinwheel))));
        root.Children.Add(Row(Btn("Collapse", () => FaceOpEach("Collapse", FaceOps.Collapse)), Btn("Mesh Smooth", () => FaceOpMulti("Mesh smooth", FaceOps.MeshSmooth))));
        root.Children.Add(Row(Btn("Combine", () => FaceOpMulti("Combine", (g, f) => FaceOps.Combine(g, f))), Btn("Make Portal", () => FaceOpMulti("Make portal", FaceOps.MakePortal))));
        root.Children.Add(Row(Btn("Delete", () => FaceOpMulti("Delete faces", FaceOps.Delete)), Btn("Delete Ext.", () => FaceOpMulti("Delete faces+verts", FaceOps.DeleteExt))));
        root.Children.Add(Row(Btn("Split U…", () => _ = SplitDialogAsync(true)), Btn("Split V…", () => _ = SplitDialogAsync(false))));
        root.Children.Add(Row(Btn("Bend…", () => _ = BendDialogAsync(SelectedFaceVertexSet())), Btn("Twist…", () => _ = TwistDialogAsync(SelectedFaceVertexSet())), Btn("Stretch…", () => _ = FaceStretchDialogAsync())));
        return root;
    }

    private Control BuildVertexPanel()
    {
        var root = new StackPanel { Margin = new Avalonia.Thickness(8), Spacing = 6 };
        root.Children.Add(Header("Vertex Operations"));
        root.Children.Add(Note("Click vertex dots to select; Ctrl-click adds. Weld merges onto the last selected."));
        root.Children.Add(Row(Btn("Weld", () => VertexOpList("Weld", VertexOps.Weld)), Btn("Collapse", () => VertexOpList("Collapse", VertexOps.Collapse))));
        root.Children.Add(Row(Btn("Delete", () => VertexOpSet("Delete verts", VertexOps.Delete)), Btn("Bridge", () => VertexOpList("Bridge", VertexOps.Bridge))));
        root.Children.Add(Row(Btn("Align X", () => VertexOpSet("Align X", (g, s) => VertexOps.Align(g, s, 0))), Btn("Align Y", () => VertexOpSet("Align Y", (g, s) => VertexOps.Align(g, s, 1))), Btn("Align Z", () => VertexOpSet("Align Z", (g, s) => VertexOps.Align(g, s, 2)))));
        root.Children.Add(Row(Btn("Snap Grid", () => VertexOpSet("Snap verts", (g, s) => VertexOps.SnapToGrid(g, s, _settings.GridSize))), Btn("Jitter…", () => _ = JitterDialogAsync())));
        root.Children.Add(Row(Btn("Bend…", () => _ = BendDialogAsync(SelectedVertexSet())), Btn("Twist…", () => _ = TwistDialogAsync(SelectedVertexSet())), Btn("Stretch…", () => _ = VertexStretchDialogAsync())));
        return root;
    }

    // ---- Brush operators ------------------------------------------------------

    private void ClipSelected(int axis)
    {
        if (!EnsureBrushSelection())
        {
            return;
        }

        Vec3 centroid = BrushTransform.SelectionPivot(SelectedBrushUids().Select(u => BrushEd!.FindBrush(u)!).ToList());
        Vec3 normal = TransformMath.Axis(axis);
        OpResult r = BrushEd!.Clip(centroid, normal, _clipCut ? ClipMode.Cut : ClipMode.Split, _clipFlip);
        Report(r);
        AfterBrushEdit();
    }

    private void FuseSelected()
    {
        if (BrushEd is null)
        {
            return;
        }

        Report(BrushEd.Fuse());
        AfterBrushEdit();
    }

    private void MirrorSelected(int axis)
    {
        if (!EnsureBrushSelection())
        {
            return;
        }

        BrushEd!.EditBrushes(SelectedBrushUids(), $"Mirror {"XYZ"[axis]}", b => { BrushOps.Mirror(b, axis); return OpResult.Ok(); });
        AfterBrushEdit();
    }

    private void DeleteSelectedBrushes()
    {
        if (BrushEd is null || BrushEd.SelectedBrushes.Count == 0)
        {
            return;
        }

        BrushEd.DeleteBrushes(SelectedBrushUids());
        AfterBrushEdit();
    }

    private void SnapSelectionToGrid()
    {
        if (BrushEd is null)
        {
            return;
        }

        if (BrushEd.Mode == EditMode.Vertex)
        {
            VertexOpSet("Snap verts", (g, s) => VertexOps.SnapToGrid(g, s, _settings.GridSize));
            return;
        }

        if (!EnsureBrushSelection())
        {
            return;
        }

        // Snapping every vertex to the grid moves them independently, so a face can end up bent —
        // triangulate any that did so brush faces stay flat (RED parity; see FacePlanarizer). Whole-
        // brush rigid transforms (Move/Rotate/Reorient/Stretch) are affine and never need this.
        int triangulated = 0;
        BrushEd.EditBrushes(SelectedBrushUids(), "Snap to grid", b =>
        {
            BrushTransform.SnapVerticesToGrid(b, _settings.GridSize);
            triangulated += FacePlanarizer.Planarize(b.Geometry);
            return OpResult.Ok();
        });
        NotePlanarized(triangulated);
        AfterBrushEdit();
    }

    private void ReorientSelected()
    {
        if (!EnsureBrushSelection())
        {
            return;
        }

        BrushEd!.EditBrushes(SelectedBrushUids(), "Reorient", b => { BrushTransform.Reorient(b); return OpResult.Ok(); });
        AfterBrushEdit();
    }

    private void MoveCentersSelected()
    {
        if (!EnsureBrushSelection())
        {
            return;
        }

        BrushEd!.EditBrushes(SelectedBrushUids(), "Move centers", b => { BrushTransform.RecenterToCentroid(b); return OpResult.Ok(); });
        AfterBrushEdit();
    }

    private void StartOfTime()
    {
        if (EnsureBrushSelection())
        {
            BrushEd!.MoveToStartOfTime(SelectedBrushUids());
            AfterBrushEdit();
        }
    }

    private void EndOfTime()
    {
        if (EnsureBrushSelection())
        {
            BrushEd!.MoveToEndOfTime(SelectedBrushUids());
            AfterBrushEdit();
        }
    }

    private async Task StretchDialogAsync()
    {
        if (!EnsureBrushSelection())
        {
            return;
        }

        Brush b = BrushEd!.FindBrush(SelectedBrushUids()[0])!;
        Vec3 d = BrushTransform.Dimensions(b);
        string? text = await InputDialog.ShowAsync(this, "Stretch", "New W H D (m):", $"{d.X:0.##} {d.Y:0.##} {d.Z:0.##}");
        if (TryParse3(text, out float w, out float h, out float dp))
        {
            BrushEd.EditBrushes(SelectedBrushUids(), "Stretch", br => { BrushTransform.StretchToDimensions(br, w, h, dp); return OpResult.Ok(); });
            AfterBrushEdit();
        }
    }

    // ---- Face operators -------------------------------------------------------

    private async Task ExtrudeDialogAsync()
    {
        string? text = await InputDialog.ShowAsync(this, "Extrude", "Distance (m):", _settings.GridSize.ToString(CultureInfo.InvariantCulture));
        if (TryParse1(text, out float dist))
        {
            FaceOpEach("Extrude", (g, f) => FaceOps.Extrude(g, f, dist));
        }
    }

    private async Task BevelDialogAsync()
    {
        string? text = await InputDialog.ShowAsync(this, "Bevel", "Inset (0..1):", "0.25");
        if (TryParse1(text, out float amt))
        {
            FaceOpEach("Bevel", (g, f) => FaceOps.Bevel(g, f, amt));
        }
    }

    private async Task SplitDialogAsync(bool alongU)
    {
        string? text = await InputDialog.ShowAsync(this, "N-Way Split", "Pieces:", "2");
        if (TryParseInt(text, out int n))
        {
            FaceOpEach($"Split {(alongU ? "U" : "V")}", (g, f) => FaceOps.NWaySplit(g, f, n, alongU));
        }
    }

    private async Task FaceStretchDialogAsync()
    {
        string? text = await InputDialog.ShowAsync(this, "Stretch Faces", "Factor X Y Z:", "1 1 1");
        if (TryParse3(text, out float x, out float y, out float z))
        {
            IReadOnlyCollection<int>? set = SelectedFaceVertexSet();
            ApplyDeformer("Stretch faces", g => Deformers.Stretch(g, new Vec3(x, y, z), set));
        }
    }

    // ---- Vertex operators -----------------------------------------------------

    private async Task JitterDialogAsync()
    {
        string? text = await InputDialog.ShowAsync(this, "Jitter", "Amount (m):", "0.1");
        if (TryParse1(text, out float amt))
        {
            IReadOnlyCollection<int>? set = SelectedVertexSet();
            ApplyDeformer("Jitter", g => Deformers.Jitter(g, amt, 12345, set));
        }
    }

    private async Task VertexStretchDialogAsync()
    {
        string? text = await InputDialog.ShowAsync(this, "Stretch Vertices", "Factor X Y Z:", "1 1 1");
        if (TryParse3(text, out float x, out float y, out float z))
        {
            IReadOnlyCollection<int>? set = SelectedVertexSet();
            ApplyDeformer("Stretch verts", g => Deformers.Stretch(g, new Vec3(x, y, z), set));
        }
    }

    private async Task BendDialogAsync(IReadOnlyCollection<int>? subset)
    {
        string? text = await InputDialog.ShowAsync(this, "Bend", "Degrees:", "45");
        if (TryParse1(text, out float deg))
        {
            ApplyDeformer("Bend", g => Deformers.Bend(g, 0, 1, deg, subset));
        }
    }

    private async Task TwistDialogAsync(IReadOnlyCollection<int>? subset)
    {
        string? text = await InputDialog.ShowAsync(this, "Twist", "Degrees:", "45");
        if (TryParse1(text, out float deg))
        {
            ApplyDeformer("Twist", g => Deformers.Twist(g, 1, deg, subset));
        }
    }

    // ---- Op plumbing ----------------------------------------------------------

    private bool EnsureBrushSelection()
    {
        if (BrushEd is null || BrushEd.SelectedBrushes.Count == 0)
        {
            _dispatcher.ShowMessage("Select a brush first.");
            return false;
        }

        return true;
    }

    /// <summary>Applies a single-face op to each selected face (per brush, descending index).</summary>
    private void FaceOpEach(string desc, Func<Geometry, int, OpResult> op) =>
        FaceOpMulti(desc, (g, faces) => Aggregate(faces, i => op(g, i)));

    /// <summary>Applies a multi-face op with the selected face indices of each brush.</summary>
    private void FaceOpMulti(string desc, Func<Geometry, IReadOnlyList<int>, OpResult> op)
    {
        if (BrushEd is null || BrushEd.SelectedFaces.Count == 0)
        {
            _dispatcher.ShowMessage("Select a face first.");
            return;
        }

        var groups = BrushEd.SelectedFaces.GroupBy(f => f.Brush).ToList();
        Ged.Core.Editor.UndoStack.Transaction? tx = groups.Count > 1 ? Document!.Undo.BeginTransaction(desc) : null;
        OpResult worst = OpResult.Ok();
        int triangulated = 0;
        foreach (var grp in groups)
        {
            var faces = grp.Select(f => f.Face).OrderByDescending(i => i).ToList();
            OpResult r = BrushEd.EditBrushes(new[] { grp.Key }, desc, b => op(b.Geometry, faces));
            if (!r)
            {
                worst = r;
            }

            triangulated += r.FacesTriangulated;
        }

        tx?.Commit();
        Report(worst);
        NotePlanarized(triangulated); // face ops that bend neighbours (Collapse, Mesh Smooth) triangulate them
        AfterBrushEdit();
    }

    private void VertexOpList(string desc, Func<Geometry, IReadOnlyList<int>, OpResult> op)
    {
        if (BrushEd is null || BrushEd.SelectedVertices.Count == 0)
        {
            _dispatcher.ShowMessage("Select a vertex first.");
            return;
        }

        var groups = BrushEd.SelectedVertices.GroupBy(v => v.Brush).ToList();
        Ged.Core.Editor.UndoStack.Transaction? tx = groups.Count > 1 ? Document!.Undo.BeginTransaction(desc) : null;
        OpResult worst = OpResult.Ok();
        int triangulated = 0;
        foreach (var grp in groups)
        {
            var verts = grp.Select(v => v.Vertex).ToList();
            OpResult r = BrushEd.EditBrushes(new[] { grp.Key }, desc, b => op(b.Geometry, verts));
            if (!r)
            {
                worst = r;
            }

            triangulated += r.FacesTriangulated;
        }

        tx?.Commit();
        Report(worst);
        NotePlanarized(triangulated);
        AfterBrushEdit();
    }

    private void VertexOpSet(string desc, Func<Geometry, IReadOnlyCollection<int>, OpResult> op) =>
        VertexOpList(desc, (g, list) => op(g, list));

    private void ApplyDeformer(string desc, Action<Geometry> deform)
    {
        // Deformers target the whole brush unless a face/vertex subset is passed inside `deform`.
        var uids = BrushEd?.Mode == EditMode.Brush
            ? SelectedBrushUids()
            : BrushEd?.SelectedFaces.Select(f => f.Brush).Concat(BrushEd.SelectedVertices.Select(v => v.Brush)).Distinct().ToList() ?? new List<int>();
        if (BrushEd is null || uids.Count == 0)
        {
            _dispatcher.ShowMessage("Select a brush/face/vertex first.");
            return;
        }

        // RED-parity: a deformer (stretch/bend/twist/jitter) can bend faces off-plane; triangulate
        // those to stay flat, joined to the deformer's single undo entry (see FacePlanarizer).
        int triangulated = 0;
        BrushEd.EditBrushes(uids, desc, b =>
        {
            deform(b.Geometry);
            triangulated += FacePlanarizer.Planarize(b.Geometry);
            return GeometryUtil.Validate(b.Geometry);
        });
        NotePlanarized(triangulated);
        AfterBrushEdit();
    }

    /// <summary>Surfaces the RED-parity edit-time planarity guard when it fired (discoverability).</summary>
    private void NotePlanarized(int count)
    {
        if (count > 0)
        {
            _dispatcher.ShowMessage($"{count} face(s) triangulated to stay planar.");
        }
    }

    private IReadOnlyCollection<int>? SelectedFaceVertexSet()
    {
        if (BrushEd is null || BrushEd.SelectedFaces.Count == 0)
        {
            return null;
        }

        int brush = BrushEd.SelectedFaces.First().Brush;
        Brush? b = BrushEd.FindBrush(brush);
        if (b is null)
        {
            return null;
        }

        var set = new HashSet<int>();
        foreach ((int u, int f) in BrushEd.SelectedFaces.Where(x => x.Brush == brush))
        {
            if (f >= 0 && f < b.Geometry.Faces.Count)
            {
                foreach (FaceVertex fv in b.Geometry.Faces[f].Vertices)
                {
                    set.Add(fv.Index);
                }
            }
        }

        return set;
    }

    private IReadOnlyCollection<int>? SelectedVertexSet()
    {
        if (BrushEd is null || BrushEd.SelectedVertices.Count == 0)
        {
            return null;
        }

        int brush = BrushEd.SelectedVertices.First().Brush;
        return BrushEd.SelectedVertices.Where(v => v.Brush == brush).Select(v => v.Vertex).ToHashSet();
    }

    private static OpResult Aggregate(IReadOnlyList<int> faces, Func<int, OpResult> op)
    {
        OpResult worst = OpResult.Ok();
        int triangulated = 0;
        foreach (int f in faces)
        {
            OpResult r = op(f);
            if (!r)
            {
                worst = r;
            }

            triangulated += r.FacesTriangulated;
        }

        // Preserve the planarity-guard count across the per-face fold so FaceOpEach/FaceOpMulti can
        // surface it (a single-face op's OpResult would otherwise be discarded into `worst`).
        return worst with { FacesTriangulated = triangulated };
    }

    private void Report(OpResult r)
    {
        if (!r)
        {
            _dispatcher.ShowMessage(r.Message);
        }
    }

    private async Task BrowseMeshAsync()
    {
        var files =
            await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Pick a mesh (.v3m/.v3c)",
                AllowMultiple = false,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("V3M/V3C mesh") { Patterns = new[] { "*.v3m", "*.v3c" } } },
            });
        if (files.Count > 0 && files[0].TryGetLocalPath() is string path)
        {
            _brushParams.MeshFilename = path;
        }
    }

    // ---- Small UI + parse helpers --------------------------------------------

    private static Control Header(string t) => new TextBlock { Text = t, FontWeight = FontWeight.Bold, Margin = new Avalonia.Thickness(0, 6, 0, 2) };

    private static Control Note(string t) => new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray };

    private static Control Labeled(string label, Control c)
    {
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(new TextBlock { Text = label, FontSize = 11 });
        p.Children.Add(c);
        return p;
    }

    private static Control Num(string label, float value, Action<float> set)
    {
        var box = new NumericUpDown { Value = (decimal)value, Increment = 1m, Minimum = -100000m, Maximum = 100000m, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.ValueChanged += (_, _) => set((float)(box.Value ?? 0));
        return Labeled(label, box);
    }

    private static Control IntNum(string label, int value, Action<int> set)
    {
        var box = new NumericUpDown { Value = value, Increment = 1m, Minimum = -1m, Maximum = 4096m, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.ValueChanged += (_, _) => set((int)(box.Value ?? 0));
        return Labeled(label, box);
    }

    private static CheckBox Check(string label, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        cb.IsCheckedChanged += (_, _) => set(cb.IsChecked == true);
        return cb;
    }

    private Button Btn(string text, Action action)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Avalonia.Thickness(0, 1) };
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>
    /// A horizontal button/control row that WRAPS to the next line at narrow panel widths instead
    /// of overflowing the panel (the docked tool panels and the Asset Browser tabs are narrow). A
    /// small per-child margin supplies the inter-item and inter-line spacing.
    /// </summary>
    private static Control Row(params Control[] children)
    {
        var p = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (Control c in children)
        {
            c.Margin = new Avalonia.Thickness(0, 0, 4, 4);
            p.Children.Add(c);
        }

        return p;
    }

    private static bool TryParse1(string? s, out float v) =>
        float.TryParse((s ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool TryParseInt(string? s, out int v) =>
        int.TryParse((s ?? string.Empty).Trim(), out v);

    private static bool TryParse3(string? s, out float a, out float b, out float c)
    {
        a = b = c = 0;
        string[] parts = (s ?? string.Empty).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 &&
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b) &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c);
    }
}
