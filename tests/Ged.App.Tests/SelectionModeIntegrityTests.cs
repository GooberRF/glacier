using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;
using CoreVec3 = Ged.Core.Model.Vec3;

namespace Ged.App.Tests;

/// <summary>
/// Items 1 + 2 — SIXTH-round selection report. Proves the two reported symptoms through the
/// REAL selection paths (SelectionRouter + the e72152c tiered refresh):
/// <list type="bullet">
/// <item>(a) "selecting objects in brush mode and vice versa" is a VISUAL masquerade, NOT state
/// leakage: the router rejects the out-of-mode kind so the selection STATE never changes. The
/// phantom highlight came from MainWindow drawing the raw last-pick regardless of the gate
/// (now gated to accepted picks).</item>
/// <item>(b) "brush A stays visually selected after selecting B until an operation runs" was a
/// STALE scene-baked tint: BrushEmitter baked <c>BrushStateColor(..., selected)</c> into the
/// compiled scene, which the tiered refresh no longer rebuilds on a selection change. The scene
/// is now selection-INDEPENDENT; the brush highlight lives only in the lightweight overlay, so a
/// selection change can never leave a stale highlight (and keeps the perf win).</item>
/// </list>
/// </summary>
public sealed class SelectionModeIntegrityTests
{
    private static readonly uint Highlight = Palette.Rgba(255, 240, 60);

    private static BrushCreateParams Cube(float s = 3f) => new() { Width = s, Height = s, Depth = s };

    private static (EditorSession Session, BrushEditor Brushes) BrushLevel()
    {
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        be.SetMode(EditMode.Brush);
        session.ActiveSelectKinds = SelectKinds.Brushes; // brush mode chips
        return (session, be);
    }

    // ---- (a) STATE integrity: out-of-mode picks are dropped, never leaked -------------------

    [AvaloniaFact]
    public void Object_Pick_In_Brush_Mode_Is_Rejected_State_Unchanged()
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        LevelObject obj = doc.PlaceObject(LevelObjectKind.Light, new CoreVec3(0, 0, 0))!;

        session.ActiveSelectKinds = SelectKinds.Brushes; // Brush mode: Objects chip is off

        bool ok = session.Selection.SelectObject(obj);

        Assert.False(ok); // the router DROPPED it (mode/chip gate) ...
        Assert.Empty(doc.Selection); // ... and the selection STATE is untouched (no leak)
    }

    [AvaloniaFact]
    public void Brush_Pick_In_Object_Mode_Is_Rejected_State_Unchanged()
    {
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        int uid = be.CreateBrush(Cube(), new CoreVec3(0, 0, 0), Mat3.Identity);

        session.ActiveSelectKinds = SelectKinds.Objects; // Object mode: Brushes chip is off

        bool ok = session.Selection.SelectBrush(uid);

        Assert.False(ok);
        Assert.Empty(be.SelectedBrushes); // "vice versa" is also gated at the state layer
    }

    // ---- (b) The compiled scene is SELECTION-INDEPENDENT for brushes ------------------------

    [AvaloniaFact]
    public void Brush_Selection_Is_Never_Baked_Into_The_Compiled_Scene()
    {
        (EditorSession session, BrushEditor be) = BrushLevel();
        be.CreateBrush(Cube(), new CoreVec3(-8, 0, 0), Mat3.Identity);
        int a = be.CreateBrush(Cube(), new CoreVec3(8, 0, 0), Mat3.Identity);

        // No selection: the scene carries no highlight colour.
        RenderScene none = session.BuildScene();
        Assert.DoesNotContain(none.Lines, l => l.Color == Highlight);
        Assert.DoesNotContain(none.Batches.SelectMany(b => b.Vertices), v => v.Color == Highlight);

        // Selecting a brush must NOT change the compiled scene at all — the highlight is an
        // overlay concern now, so a selection change needs no BuildScene (mirrors the object
        // BuildScene_Does_Not_Depend_On_The_Selection tier lock).
        session.Selection.SelectBrush(a);
        RenderScene withA = session.BuildScene();
        Assert.DoesNotContain(withA.Lines, l => l.Color == Highlight);
        Assert.DoesNotContain(withA.Batches.SelectMany(b => b.Vertices), v => v.Color == Highlight);
        Assert.Equal(
            none.Lines.Select(l => l.Color).ToList(),
            withA.Lines.Select(l => l.Color).ToList());
    }

    [AvaloniaFact]
    public void Overlay_Highlight_Follows_Only_The_Current_Brush_Selection()
    {
        (EditorSession session, BrushEditor be) = BrushLevel();
        int a = be.CreateBrush(Cube(), new CoreVec3(-10, 0, 0), Mat3.Identity);
        int b = be.CreateBrush(Cube(), new CoreVec3(10, 0, 0), Mat3.Identity);

        session.Selection.SelectBrush(a);
        IReadOnlyList<LineSegment> overlayA = session.BuildBrushSelectionLines();
        Assert.Contains(overlayA, l => l.Color == Highlight);
        Assert.All(overlayA, l => Assert.True(l.A.X < 0 && l.B.X < 0, "A's highlight is on A's side"));

        // Select B (replaces A). Under the tiered refresh only THIS overlay is rebuilt.
        session.Selection.SelectBrush(b);
        IReadOnlyList<LineSegment> overlayB = session.BuildBrushSelectionLines();
        Assert.Contains(overlayB, l => l.Color == Highlight);
        // A's highlight is GONE: not one overlay line touches A's side.
        Assert.All(overlayB, l => Assert.True(l.A.X > 0 && l.B.X > 0, "only B is highlighted after A→B"));
    }

    // ---- Cross-kind replace in Group mode: a plain click drops the OTHER kind too -----------

    /// <summary>A Group-mode session with one brush and one object, both co-selectable.</summary>
    private static (EditorSession Session, BrushEditor Brushes, EditorDocument Doc, int BrushUid, LevelObject Obj) GroupLevel()
    {
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        EditorDocument doc = session.Document!;
        int uid = be.CreateBrush(Cube(), new CoreVec3(-10, 0, 0), Mat3.Identity);
        LevelObject obj = doc.PlaceObject(LevelObjectKind.Light, new CoreVec3(10, 0, 0))!;

        // Enter Group mode through the ACTUAL chokepoint (both whole brushes AND objects selectable).
        be.SetMode(EditMode.Group);
        session.SyncSelectionToKinds(SelectionFilter.PrimaryKindFor(EditMode.Group));
        Assert.Equal(SelectKinds.Groups, session.ActiveSelectKinds);
        return (session, be, doc, uid, obj);
    }

    [AvaloniaFact]
    public void Group_Mode_Plain_Object_Click_Drops_A_Lingering_Brush_Selection()
    {
        (EditorSession session, BrushEditor be, EditorDocument doc, int uid, LevelObject obj) = GroupLevel();

        // Brush selected first (e.g. carried in from Brush mode), then a PLAIN object click.
        Assert.True(session.Selection.SelectBrush(uid));
        Assert.Contains(uid, be.SelectedBrushes);

        Assert.True(session.Selection.SelectObject(obj)); // non-additive: replaces the WHOLE selection

        // STATE: the object is selected and the brush is deselected (not just the object added).
        Assert.Contains(obj, doc.Selection);
        Assert.Empty(be.SelectedBrushes);

        // VISUALS: the overlay rebuilds from live state, so the brush highlight lines are gone.
        Assert.Empty(session.BuildBrushSelectionLines());
    }

    [AvaloniaFact]
    public void Group_Mode_Plain_Brush_Click_Drops_A_Lingering_Object_Selection()
    {
        (EditorSession session, BrushEditor be, EditorDocument doc, int uid, LevelObject obj) = GroupLevel();

        // Object selected first (carried in from Object mode), then a PLAIN brush click (the mirror case).
        Assert.True(session.Selection.SelectObject(obj));
        Assert.Contains(obj, doc.Selection);

        Assert.True(session.Selection.SelectBrush(uid)); // non-additive: replaces the WHOLE selection

        Assert.Contains(uid, be.SelectedBrushes);
        Assert.Empty(doc.Selection);

        // The object's highlight box lines are gone (overlay driven by the live document selection).
        Assert.Empty(session.BuildSelectionLines(doc.Selection));
    }

    [AvaloniaFact]
    public void Group_Mode_Additive_Ctrl_Click_Keeps_Both_Kinds()
    {
        (EditorSession session, BrushEditor be, EditorDocument doc, int uid, LevelObject obj) = GroupLevel();

        Assert.True(session.Selection.SelectBrush(uid));
        Assert.True(session.Selection.SelectObject(obj, additive: true)); // Ctrl-click: keep the brush too

        Assert.Contains(obj, doc.Selection);
        Assert.Contains(uid, be.SelectedBrushes); // additive never cross-clears

        // And the mirror direction: additive brush click keeps the object.
        int uid2 = be.CreateBrush(Cube(), new CoreVec3(0, 12, 0), Mat3.Identity);
        Assert.True(session.Selection.SelectBrush(uid2, additive: true));
        Assert.Contains(obj, doc.Selection);
        Assert.Contains(uid2, be.SelectedBrushes);
    }

    [AvaloniaFact]
    public void Group_Mode_Plain_Batch_Select_Also_Replaces_The_Other_Kind()
    {
        // The marquee's batch guarantee: a non-additive batch object select (e.g. a group double-tap or a
        // plain marquee's object catch) drops a lingering brush selection, and vice-versa — the same
        // whole-selection replace the single-click path now enforces, so box-select never drifts from click.
        (EditorSession session, BrushEditor be, EditorDocument doc, int uid, LevelObject obj) = GroupLevel();

        Assert.True(session.Selection.SelectBrush(uid));
        Assert.True(session.Selection.SelectObjects(new[] { obj }));
        Assert.Empty(be.SelectedBrushes);
        Assert.Contains(obj, doc.Selection);

        Assert.True(session.Selection.SelectBrushes(new[] { uid }));
        Assert.Empty(doc.Selection);
        Assert.Contains(uid, be.SelectedBrushes);
    }

    [AvaloniaFact]
    public void Object_Mode_Plain_Object_Click_Leaves_Brush_Sub_Selection_Untouched()
    {
        // Guard the gate: in single-kind Object mode the OTHER kind is not co-selectable (Permits(Brushes)
        // is false), so a plain object click must NOT reach across and clear a brush selection. (In the
        // real app the brush selection was already purged entering Object mode; this pins that the router
        // never over-clears when the other kind is out of scope.)
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        EditorDocument doc = session.Document!;
        int uid = be.CreateBrush(Cube(), new CoreVec3(-10, 0, 0), Mat3.Identity);
        LevelObject obj = doc.PlaceObject(LevelObjectKind.Light, new CoreVec3(10, 0, 0))!;

        // Directly seed a brush selection, then switch to Object-mode chips WITHOUT the mode purge, so we
        // isolate the router gate itself.
        session.ActiveSelectKinds = SelectKinds.Brushes;
        Assert.True(session.Selection.SelectBrush(uid));
        session.ActiveSelectKinds = SelectKinds.Objects;

        Assert.True(session.Selection.SelectObject(obj));
        Assert.Contains(obj, doc.Selection);
        Assert.Contains(uid, be.SelectedBrushes); // untouched: Brushes not co-selectable in Object mode
    }

    // ---- (b) Offscreen pixel proof: select A then B (tiered), A's highlight is GONE ---------

    [AvaloniaFact]
    public void Offscreen_Select_A_Then_B_Tiered_Leaves_Only_B_Highlighted()
    {
        GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return; // no D3D11 device available (headless CI without WARP) — skip gracefully
        }

        using (gd)
        {
            (EditorSession session, BrushEditor be) = BrushLevel();
            int a = be.CreateBrush(Cube(4f), new CoreVec3(-9, 0, 0), Mat3.Identity);
            int b = be.CreateBrush(Cube(4f), new CoreVec3(9, 0, 0), Mat3.Identity);

            // Select A and build the scene ONCE (the tiered model rebuilds the scene only on a
            // structural change, never on a selection change).
            session.Selection.SelectBrush(a);
            RenderScene scene = session.BuildScene();
            int baseLines = scene.Lines.Count;

            const int w = 512, h = 512;
            var camera = new Ged.Rendering.Camera { Position = new Vector3(0, 3, -26), AspectRatio = (float)w / h };
            camera.LookAt(camera.Position, new Vector3(0, 0, 0));

            byte[] pxA = RenderWithOverlay(gd, scene, baseLines, session.BuildBrushSelectionLines(), camera, w, h);

            // Switch selection to B WITHOUT rebuilding the scene — exactly the reported repro.
            session.Selection.SelectBrush(b);
            byte[] pxB = RenderWithOverlay(gd, scene, baseLines, session.BuildBrushSelectionLines(), camera, w, h);

            int leftA = CountYellow(pxA, w, h, 0, w / 2), rightA = CountYellow(pxA, w, h, w / 2, w);
            int leftB = CountYellow(pxB, w, h, 0, w / 2), rightB = CountYellow(pxB, w, h, w / 2, w);

            // A's highlight lands on exactly one half...
            Assert.True(leftA + rightA > 20, $"A must be highlighted (left={leftA} right={rightA})");
            bool aOnLeft = leftA > rightA;
            int aSideInB = aOnLeft ? leftB : rightB;
            int bSideInB = aOnLeft ? rightB : leftB;

            // ...and after switching to B (scene reused) that half is now essentially clear (A's
            // highlight is GONE — no stale scene tint) while B's half lights up.
            Assert.True(bSideInB > 20, $"B must be highlighted after the switch (bSide={bSideInB})");
            Assert.True(aSideInB < bSideInB / 4, $"A's highlight must clear (aSide={aSideInB}, bSide={bSideInB})");

            SaveArtifact("selection_tiered_A.png", w, h, pxA);
            SaveArtifact("selection_tiered_B.png", w, h, pxB);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static GraphicsDevice? TryDevice()
    {
        try
        {
            return new GraphicsDevice();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] RenderWithOverlay(
        GraphicsDevice gd, RenderScene scene, int baseLines, IReadOnlyList<LineSegment> overlay,
        Ged.Rendering.Camera camera, int w, int h)
    {
        scene.Lines.RemoveRange(baseLines, scene.Lines.Count - baseLines);
        scene.Lines.AddRange(overlay);
        return OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, w, h);
    }

    private static int CountYellow(byte[] rgba, int w, int h, int x0, int x1)
    {
        int count = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = x0; x < x1; x++)
            {
                int i = row + (x * 4);
                if (rgba[i] > 200 && rgba[i + 1] > 180 && rgba[i + 2] < 120)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static void SaveArtifact(string file, int w, int h, byte[] px)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return;
        }

        string outDir = Path.Combine(dir.FullName, "tests", "artifacts", "selection");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, file), PngWriter.Encode(w, h, px));
    }
}
