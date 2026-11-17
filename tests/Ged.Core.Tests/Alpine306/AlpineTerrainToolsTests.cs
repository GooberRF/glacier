using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// The Terrain Tools (ALPINE-COMPAT-SPEC C9): Alpine RED's brush arithmetic (editor_patch/terrain_paint_math.h) through
/// <see cref="TerrainBrush"/> and whole strokes through <see cref="TerrainStroke"/> (one undo step each, the rebuild-needed
/// state), decorations and overlays, Stitch Edges, and the Lua surface (terrain:sculpt / paint / set_decoration ...).
/// </summary>
public sealed class AlpineTerrainToolsTests
{
    private static (EditorDocument Doc, LevelObject Obj, AlpineTerrain T) Flat(int n = 33, float cell = 2f)
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15));
        LevelObject obj = TerrainOps.Create(doc, new Vec3(0, 0, 0), n, n, cell, "t");
        var t = (AlpineTerrain)obj.Model;
        TerrainOps.Apply(doc, obj, "layers", x =>
        {
            TerrainOps.SetLayers(x, new[] { "grass1.tga", "rock02.tga", "sand01.tga" });
            x.HeightMin = 0f;
            x.HeightRange = 16f;
        });
        return (doc, obj, t);
    }

    private static (float X, float Z) Centre(AlpineTerrain t) =>
        (t.Origin.X + (AlpineTerrainRules.Extent(t.Nx, t.CellSize) * 0.5f), t.Origin.Z + (AlpineTerrainRules.Extent(t.Nz, t.CellSize) * 0.5f));

    [Fact]
    public void Falloff_Curves_Are_Alpines()
    {
        Assert.Equal(1f, TerrainBrush.FalloffWeight(TerrainFalloff.Smooth, 0f), 5);
        Assert.Equal(0.5f, TerrainBrush.FalloffWeight(TerrainFalloff.Smooth, 0.5f), 4);
        Assert.Equal(0.75f, TerrainBrush.FalloffWeight(TerrainFalloff.Linear, 0.25f), 5);
        Assert.Equal(1f, TerrainBrush.FalloffWeight(TerrainFalloff.Constant, 0.99f));
        Assert.Equal(0f, TerrainBrush.FalloffWeight(TerrainFalloff.Constant, 1f));
        Assert.Equal(0.2f, TerrainBrush.RaiseStep(8f, 0.5f), 5); // strength x radius / 20
        Assert.Equal(1.0f, TerrainBrush.NoiseAmplitude(8f, 0.5f), 5); // strength x radius / 4
        Assert.Equal(1, TerrainBrush.SmoothKernel(4f));
        Assert.Equal(3, TerrainBrush.SmoothKernel(40f));
    }

    [Fact]
    public void A_Raise_Stroke_Is_One_Undo_Step_And_Marks_The_Build_Stale()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain t) = Flat();
        (float cx, float cz) = Centre(t);
        int undoBefore = doc.Undo.UndoEntries.Count;
        ushort[] before = (ushort[])t.Grid.Heights.Clone();
        ulong fp = AlpineTerrainRules.GeometryFingerprint(t);

        var stroke = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.Raise, Radius = 8f, Strength = 0.5f });
        stroke.MoveTo(cx - 10, cz);
        for (int i = 1; i <= 20; i++)
        {
            stroke.MoveTo(cx - 10 + i, cz);
        }

        stroke.Repeat();
        Assert.True(stroke.Finish());
        Assert.True(stroke.EditedGeometry);
        Assert.Equal(undoBefore + 1, doc.Undo.UndoEntries.Count);
        Assert.NotEqual(fp, AlpineTerrainRules.GeometryFingerprint(t));

        // The centre of the path rose; far away nothing moved.
        int mid = 16 * 33 + 16;
        Assert.True(TerrainOps.HeightOffset(t, 16, 16) > 0.2);
        Assert.Equal(before[0], t.Grid.Heights[0]);

        doc.Undo.Undo();
        Assert.Equal(before, t.Grid.Heights);
        doc.Undo.Redo();
        Assert.NotEqual(before[mid], t.Grid.Heights[mid]);
    }

    [Fact]
    public void A_Stroke_That_Changes_Nothing_Leaves_No_Undo_Step()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain t) = Flat();
        AlpineTerrainGrid grid = t.Grid;
        int undoBefore = doc.Undo.UndoEntries.Count;
        var stroke = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.PaintLayer, Layer = 0 });
        (float cx, float cz) = Centre(t);
        stroke.MoveTo(cx, cz); // layer 0 is already everywhere
        Assert.False(stroke.Finish());
        Assert.Equal(undoBefore, doc.Undo.UndoEntries.Count);
        Assert.Same(grid, t.Grid);
    }

    [Fact]
    public void Painting_Keeps_Every_Texel_Summing_To_255_And_Keeps_The_Build()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain t) = Flat();
        t.NoteBuilt();
        t.BuildMapping.Add(new AlpineTerrainChunkMapping(1, 4, 5));
        Assert.True(t.BuildIsCurrent);
        (float cx, float cz) = Centre(t);
        var stroke = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.PaintLayer, Layer = 1, Radius = 6f, Strength = 1f });
        stroke.MoveTo(cx, cz);
        stroke.MoveTo(cx + 6, cz);
        Assert.True(stroke.Finish());
        Assert.False(stroke.EditedGeometry);
        Assert.True(t.BuildIsCurrent); // paint changes no geometry (alpine_terrain.h:1839-1842)

        int map = AlpineTerrainRules.WeightMapBytes(t.Nx, t.Nz, t.Grid.WeightResolution);
        int rock = 0;
        for (int texel = 0; texel < map / 4; texel++)
        {
            int sum = 0;
            for (int c = 0; c < 4; c++)
            {
                sum += t.Grid.Weights[(texel * 4) + c] + t.Grid.Weights[map + (texel * 4) + c];
            }

            Assert.Equal(255, sum);
            rock += t.Grid.Weights[(texel * 4) + 1] > 200 ? 1 : 0;
        }

        Assert.True(rock > 20);

        // Erase to base moves them back toward layer 0; smoothing blends neighbours.
        var erase = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.EraseToBase, Radius = 20f, Strength = 1f });
        erase.MoveTo(cx, cz);
        erase.Repeat();
        Assert.True(erase.Finish());
        Assert.True(t.Grid.Weights[(((16 * 2 * 64) + 32) * 4) + 1] < 10);
    }

    [Fact]
    public void Holes_Paint_Cells_Whose_Centre_Is_Inside_The_Brush()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain t) = Flat();
        (float cx, float cz) = Centre(t);
        var stroke = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.PaintHoles, Radius = 3f });
        stroke.MoveTo(cx, cz);
        stroke.Finish();
        Assert.True(stroke.EditedGeometry);
        int holes = 0;
        for (int z = 0; z < t.Grid.CellsZ; z++)
        {
            for (int x = 0; x < t.Grid.CellsX; x++)
            {
                holes += t.Grid.IsHole(x, z) ? 1 : 0;
            }
        }

        Assert.Equal(4, holes); // radius 1.5 cells about a cell corner: the 4 cells around it
        Assert.True(t.Grid.IsHole(15, 15) && t.Grid.IsHole(16, 16));

        var clear = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.ClearHoles, Radius = 3f });
        clear.MoveTo(cx, cz);
        clear.Finish();
        Assert.False(t.Grid.IsHole(15, 15));
    }

    [Fact]
    public void Flatten_Set_Height_Smooth_Noise_And_Ramp_Shape_The_Surface()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain t) = Flat();
        (float cx, float cz) = Centre(t);

        // Set Height pulls toward a world Y; held, it converges.
        var set = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.SetHeight, SetHeight = 5f, Radius = 6f, Strength = 1f, Falloff = TerrainFalloff.Constant });
        set.MoveTo(cx, cz);
        Assert.True(set.Finish());
        Assert.Equal(5f, TerrainOps.HeightAt(t, cx, cz), 2);

        // Flatten holds the height where it began: dragging off the plateau spreads it.
        var flatten = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.Flatten, Radius = 4f, Strength = 1f, Falloff = TerrainFalloff.Constant });
        flatten.MoveTo(cx, cz);
        flatten.MoveTo(cx + 12f, cz);
        Assert.True(flatten.Finish());
        Assert.Equal(5f, TerrainOps.HeightAt(t, cx + 12f, cz), 1);

        // Smooth lowers the plateau's edge contrast.
        double Edge() => TerrainOps.HeightAt(t, cx + 14f, cz) - TerrainOps.HeightAt(t, cx + 18f, cz);
        double sharp = Edge();
        var smooth = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.SmoothHeights, Radius = 6f, Strength = 1f });
        smooth.MoveTo(cx + 16f, cz);
        smooth.Repeat();
        smooth.Repeat();
        smooth.Finish();
        Assert.True(Edge() < sharp);

        // Noise stays within +/- strength x radius / 4 and a second pass over the same spot adds nothing.
        (EditorDocument d2, LevelObject o2, AlpineTerrain t2) = Flat();
        var noise = new TerrainStroke(d2, o2, new TerrainBrushSettings { Tool = TerrainTool.Noise, Radius = 8f, Strength = 1f }, seed: 7);
        noise.MoveTo(cx, cz);
        ushort[] once = (ushort[])t2.Grid.Heights.Clone();
        noise.MoveTo(cx + 0.5f, cz);
        noise.MoveTo(cx, cz);
        noise.Finish();
        Assert.Equal(once, t2.Grid.Heights);
        for (int i = 0; i < t2.Grid.Heights.Length; i++)
        {
            Assert.InRange(TerrainOps.HeightOffset(t2, i % t2.Nx, i / t2.Nx), -2.01, 2.01);
        }

        // Ramp: after the lock distance the surface rises along the drag at the angle.
        (EditorDocument d3, LevelObject o3, AlpineTerrain t3) = Flat(65, 1f);
        (float rx, float rz) = Centre(t3);
        var ramp = new TerrainStroke(d3, o3, new TerrainBrushSettings { Tool = TerrainTool.Ramp, RampAngle = 20f, Radius = 4f, Strength = 1f, Falloff = TerrainFalloff.Constant, Spacing = 0.1f });
        for (int i = 0; i <= 20; i++)
        {
            ramp.MoveTo(rx - 10f + i, rz);
        }

        ramp.Repeat();
        ramp.Repeat();
        Assert.True(ramp.Finish());
        double slope = (TerrainOps.HeightAt(t3, rx + 5f, rz) - TerrainOps.HeightAt(t3, rx - 5f, rz)) / 10.0;
        Assert.Equal(Math.Tan(20 * Math.PI / 180), slope, 1);
    }

    [Fact]
    public void Bridge_Points_Applies_On_Release()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain t) = Flat(65, 1f);
        (float cx, float cz) = Centre(t);
        // Raise two ends to different heights first.
        foreach ((float x, float y) in new[] { (cx - 15f, 2f), (cx + 15f, 10f) })
        {
            var s = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.SetHeight, SetHeight = y, Radius = 3f, Strength = 1f, Falloff = TerrainFalloff.Constant });
            s.MoveTo(x, cz);
            s.Finish();
        }

        var bridge = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.BridgePoints, Radius = 2f, Strength = 1f, Falloff = TerrainFalloff.Constant });
        bridge.MoveTo(cx - 15f, cz);
        bridge.MoveTo(cx + 15f, cz);
        Assert.Equal(0f, TerrainOps.HeightAt(t, cx, cz), 2); // nothing until the release
        Assert.True(bridge.Finish());
        Assert.Equal(6f, TerrainOps.HeightAt(t, cx, cz), 1);
    }

    [Fact]
    public void Raising_Past_The_Range_Grows_It_With_Headroom()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain t) = Flat();
        (float cx, float cz) = Centre(t);
        var set = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.SetHeight, SetHeight = 30f, Radius = 4f, Strength = 1f, Falloff = TerrainFalloff.Constant });
        set.MoveTo(cx, cz);
        Assert.True(set.Finish());
        Assert.NotNull(set.Note);
        Assert.True(t.HeightRange >= 30f + (0.25f * 16f) - 0.01f, $"range {t.HeightRange}");
        Assert.Equal(30f, TerrainOps.HeightAt(t, cx, cz), 2);
        doc.Undo.Undo();
        Assert.Equal(16f, t.HeightRange);
    }

    [Fact]
    public void Mirror_X_Paints_Both_Sides_Symmetrically()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain t) = Flat();
        (float cx, float cz) = Centre(t);
        var stroke = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.Raise, Radius = 5f, Strength = 1f, MirrorX = true });
        stroke.MoveTo(cx - 10f, cz + 3f);
        stroke.Finish();
        for (int z = 0; z < t.Nz; z++)
        {
            for (int x = 0; x < t.Nx; x++)
            {
                Assert.Equal(t.Grid.Height(x, z), t.Grid.Height(t.Nx - 1 - x, z));
            }
        }

        Assert.True(TerrainOps.HeightAt(t, cx + 10f, cz + 3f) > 0.2f); // one dab: strength x radius / 20
    }

    [Fact]
    public void Geoable_Chunks_Toggle_To_The_First_Chunks_Opposite()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain t) = Flat(65, 1f);
        TerrainOps.Apply(doc, obj, "geo", x =>
        {
            x.Geoable = true;
            x.ChunkCells = 16;
        });
        Assert.Null(t.Grid.GeoMask);
        var stroke = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.GeoableChunks });
        stroke.MoveTo(t.Origin.X + 2f, t.Origin.Z + 2f);
        stroke.MoveTo(t.Origin.X + 40f, t.Origin.Z + 2f);
        Assert.True(stroke.Finish());
        Assert.True(stroke.EditedGeoChunks);
        Assert.Equal(3, stroke.GeoChunkCount);
        Assert.False(TerrainStroke.ChunkGeoable(t.Grid, 0));
        Assert.True(TerrainStroke.ChunkGeoable(t.Grid, 3));
        doc.Undo.Undo();
        Assert.Null(t.Grid.GeoMask);
    }

    [Fact]
    public void Stitch_Edges_Snaps_A_Shared_Border()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain a) = Flat(17, 2f);
        LevelObject other = TerrainOps.Create(doc, new Vec3(32, 0, 0), 17, 17, 2f, "b");
        var b = (AlpineTerrain)other.Model;
        TerrainOps.Apply(doc, other, "hill", x => TerrainOps.SetHeightOffsets(x, (i, j) => 3.0 + (j * 0.25)));
        int n = 0;
        TerrainOps.Apply(doc, obj, "stitch", x => n = TerrainOps.StitchEdges(x, new[] { b }).Vertices);
        Assert.Equal(17, n);
        for (int j = 0; j < 17; j++)
        {
            Assert.Equal(TerrainOps.HeightAt(b, b.Origin.X, b.Origin.Z + (j * 2f)), TerrainOps.HeightAt(a, a.Origin.X + 32f, a.Origin.Z + (j * 2f)), 2);
        }
    }

    [Fact]
    public void Decorations_And_Overlays_Edit_With_Their_Coverage()
    {
        (EditorDocument doc, LevelObject obj, AlpineTerrain t) = Flat();
        TerrainOps.Apply(doc, obj, "deco", x =>
        {
            TerrainOps.SetDecoration(x, 0, new AlpineTerrainDecoration("rock_a.v3m", 0.2f, 1f, 1f, 35f, 80f, 0f, AlpineTerrainRules.DecorationLinkNone, 0));
            TerrainOps.SetDecoration(x, 1, new AlpineTerrainDecoration("grass.v3m", 1f, 1f, 1f, 35f, 80f, 0f, 1, 0));
        });
        int plane = AlpineTerrainRules.WeightTexelCount(t.Nx, t.Nz, t.Grid.WeightResolution);
        Assert.Equal(2 * plane, t.Grid.DecorationCoverage!.Length);
        Assert.Equal(0, t.Grid.DecorationCoverage[0]); // unlinked: starts empty
        Assert.Equal(255, t.Grid.DecorationCoverage[plane]); // linked: starts full
        TerrainOps.Apply(doc, obj, "remove", x => TerrainOps.RemoveDecoration(x, 0));
        Assert.Single(t.Decorations);
        Assert.Equal(plane, t.Grid.DecorationCoverage!.Length);
        Assert.Equal(255, t.Grid.DecorationCoverage[0]);

        TerrainOps.Apply(doc, obj, "overlay", x => TerrainOps.SetOverlays(x, new[] { new AlpineTerrainLayer("mud01.tga", 6f, AlpineTerrainRules.OverlayFlagBreakTiling) }));
        Assert.Single(t.Overlays);
        Assert.NotNull(t.Grid.OverlayCoverage);

        // Painting the overlay's coverage (the Layer list entry after the base layers).
        (float cx, float cz) = Centre(t);
        var stroke = new TerrainStroke(doc, obj, new TerrainBrushSettings { Tool = TerrainTool.PaintLayer, Layer = t.Layers.Count, Radius = 4f, Strength = 1f });
        stroke.MoveTo(cx, cz);
        Assert.True(stroke.Finish());
        Assert.Contains(t.Grid.OverlayCoverage!, v => v > 200);
    }

    [Fact]
    public void Lua_Sculpt_Paint_And_Decorate()
    {
        (EditorDocument doc, _, AlpineTerrain t) = Flat();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, """
            local t = level.terrain.get("t")
            local s = t:sculpt{tool = "raise", path = {{30, 32}, {34, 32}}, radius = 6, strength = 1, hold = 3}
            assert(s.changed and s.geometry, tostring(s))
            local p = t:paint{layer = 2, at = {32, 32}, radius = 5, strength = 1}
            assert(p.changed and not p.geometry)
            local h = t:sculpt{tool = "paint_holes", at = {10, 10}, radius = 2}
            assert(h.geometry)
            local i = t:set_decoration{mesh = "rock_a.v3m", density = 0.3, coverage = 255}
            assert(i == 1 and #t:decorations() == 1 and t:decorations()[1].instances > 0)
            t:set_overlays{{texture = "mud01.tga", uv_scale = 6}}
            local o = t:paint{overlay = 1, at = {32, 32}, radius = 4, strength = 1}
            assert(o.changed)
            assert(t.lighting == "none")
            return t:height_at(32, 32)
            """);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.True(TerrainOps.HeightAt(t, 32, 32) > 0.5f);

        (ScriptRunResult bad, _) = ScriptTestFixture.Run(doc, "level.terrain.get('t'):sculpt{tool = 'rais', at = {0, 0}}");
        Assert.False(bad.Success);
        Assert.Contains("unknown tool", bad.Error!.ToDisplayString(), StringComparison.Ordinal);
    }
}
