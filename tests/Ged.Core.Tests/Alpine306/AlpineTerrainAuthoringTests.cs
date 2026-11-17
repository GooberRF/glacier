using System.IO;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// Authoring Alpine terrain in Glacier (ALPINE-COMPAT-SPEC §4, chunk C8): the Lua API, the build (chunks as isolated
/// detail rooms, the mapping the game resolves, air brushes leaving terrain uncut), the save rules for a stale mapping,
/// undo, Convert to Brushes, and the lints.
/// </summary>
public sealed class AlpineTerrainAuthoringTests
{
    private const string MakeLevel =
        "level.brush{pos = {0, 20, 0}, size = {160, 60, 160}, air = true}\n" +
        "local t = level.terrain.create{center = {0, 0, 0}, nx = 65, nz = 65, cell_size = 2, name = 'hills', layers = {'grass1.tga', 'rock02.tga'}}\n" +
        "t:generate{seed = 5, height_range = 12, feature_size = 48}\n";

    private static (EditorDocument Doc, BrushEditor Brushes) Level(string lua = MakeLevel)
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15, 12, 0, 0));
        doc.MarkSaved();
        var brushes = new BrushEditor(doc);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, lua, brushes: brushes, services: d => Services(d, brushes));
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return (doc, brushes);
    }

    private static ScriptServices Services(EditorDocument doc, BrushEditor brushes) => ScriptTestFixture.Services(doc, brushes) with
    {
        FileReadAccess = (path, _) => path,
        FileWriteAccess = (path, _) => path,
    };

    private static CompiledLevel Build(EditorDocument doc)
    {
        CompileOptions options = CompileOptionsFactory.Create(doc.Rfl, BuildSettings.EditorDefaults, new CompileRequest());
        return GeometryBuildService.BuildAndApply(doc.Rfl, options);
    }

    private static AlpineTerrainSection Section(RflFile rfl) => rfl.Sections.Select(s => s.Content).OfType<AlpineTerrainSection>().Single();

    [Fact]
    public void A_Generated_Terrain_Level_Builds_Sealed_With_Chunks_The_Game_Resolves()
    {
        (EditorDocument doc, _) = Level();
        CompiledLevel result = Build(doc);
        AlpineTerrain t = Section(doc.Rfl).Terrains.Single();

        // 64 x 64 cells at the default 32-cell chunk edge: four chunks, each its own detail room.
        Assert.Equal(4, t.BuildMapping.Count);
        Assert.True(t.BuildIsCurrent);
        Assert.Null(Assert.Single(TerrainBuildMapping.Resolve(Section(doc.Rfl).Terrains, result.Geometry)).Problem);

        // The rest of the geometry is sealed; terrain (detail) never counts.
        Assert.Empty(HoleDetector.Detect(result.Geometry));

        Geometry g = result.Geometry;
        var terrainRooms = t.BuildMapping.Select(m => g.Rooms.FindIndex(r => r.Id == m.RoomUid)).ToList();
        Assert.All(terrainRooms, i => Assert.True(i >= 0 && g.Rooms[i].IsSubroom == 1));
        Assert.Equal(4, terrainRooms.Distinct().Count());
        foreach (Face f in g.Faces.Where(f => terrainRooms.Contains(f.RoomIndex)))
        {
            Assert.Equal(-1, f.SurfaceIndex);
            Assert.Equal(0, f.Flags & (int)(FaceFlags.HasAlpha | FaceFlags.HasHoles));
            Assert.NotEqual(0, f.Flags & (int)FaceFlags.IsDetail);
        }

        // Each chunk room holds exactly the emitted vertices (nothing near enough to weld to).
        var emitted = TerrainChunkEmitter.BuildChunkSolids(t.DeepCopy()).ToDictionary(s => s.Chunk);
        for (int k = 0; k < 4; k++)
        {
            (uint count, ulong hash) = AlpineTerrainRules.PositionSetHash(emitted[k].Positions);
            Assert.Equal(count, t.BuildMapping[k].VertexCount);
            Assert.Equal(hash, t.BuildMapping[k].PositionHash);
        }

        // Inside the air volume: every chunk has a parent room and lies inside its box, so the linter is quiet.
        LintReport lint = LevelLinter.Lint(doc.Rfl, new LintOptions());
        Assert.DoesNotContain(lint.Findings, f => f.Message.Contains("hills", StringComparison.Ordinal));

        // Saved and reloaded, the mapping still describes the compiled geometry; a second save is a no-op.
        doc.Rfl.UpgradeToAlpine();
        byte[] saved = doc.Rfl.Save(false);
        RflFile back = RflFile.Load(saved);
        back.ParseAllKnownSections();
        AlpineTerrain reloaded = Section(back).Terrains.Single();
        Assert.True(reloaded.BuildIsCurrent);
        Assert.Null(Assert.Single(TerrainBuildMapping.Resolve(Section(back).Terrains, back.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry)).Problem);
        Assert.DoesNotContain(back.UpgradeToAlpine(), n => n.Contains("hills", StringComparison.Ordinal));
        Assert.Equal(saved, back.Save(false));
    }

    [Fact]
    public void Air_Brushes_Do_Not_Cut_Terrain()
    {
        // Owner answer Q10: terrain is exempt from CSG; an air brush through the surface leaves every chunk intact.
        (EditorDocument plain, _) = Level();
        Build(plain);
        (EditorDocument carved, _) = Level(MakeLevel + "level.brush{pos = {4, 4, 4}, size = {12, 12, 12}, air = true}\n");
        Build(carved);
        Assert.Equal(Section(plain.Rfl).Terrains.Single().BuildMapping, Section(carved.Rfl).Terrains.Single().BuildMapping);
    }

    [Fact]
    public void An_Edit_After_The_Build_Drops_The_Stale_Mapping_On_Save()
    {
        (EditorDocument doc, BrushEditor brushes) = Level();
        Build(doc);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "level.terrain.get('hills'):set_heights(function(x, z) return x * 0.1 end)", brushes: brushes);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        AlpineTerrain t = Section(doc.Rfl).Terrains.Single();
        Assert.False(t.BuildIsCurrent);
        Assert.Contains(LevelLinter.Lint(doc.Rfl, new LintOptions()).Findings, f => f.Message.Contains("changed since the last Build Geometry", StringComparison.Ordinal));

        IReadOnlyList<string> notes = doc.Rfl.UpgradeToAlpine();
        Assert.Contains(notes, n => n.Contains("changed since the last Build Geometry", StringComparison.Ordinal));
        RflFile back = RflFile.Load(doc.Rfl.Save(false));
        back.ParseAllKnownSections();
        Assert.Empty(Section(back).Terrains.Single().BuildMapping);
        Assert.Equal("it was saved without Build Geometry", Assert.Single(TerrainBuildMapping.Resolve(Section(back).Terrains, null)).Problem);
    }

    [Fact]
    public void One_Script_Of_Terrain_Edits_Undoes_To_The_Exact_Prior_Bytes()
    {
        (EditorDocument doc, BrushEditor brushes) = Level();
        byte[] before = doc.SaveToBytes();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, string.Join('\n',
            "local t = level.terrain.get('hills')",
            "t:resize{nx = 33, nz = 49}:set_layers{'grass1.tga', {texture = 'rock02.tga', uv_scale = 8}, 'mud.tga'}:fill_layer(3)",
            "t:set_hole(3, 4, true):set_heights(function(x, z) return z * 0.25 end, {fit = false})"), brushes: brushes);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        AlpineTerrain t = Section(doc.Rfl).Terrains.Single();
        Assert.Equal(33, t.Nx);
        Assert.Equal(49, t.Nz);
        Assert.Equal(3, t.Layers.Count);
        Assert.Equal(8f, t.Layers[1].UvScale);
        Assert.True(t.Grid.IsHole(3, 4));
        Assert.Equal(2, TerrainChunkEmitter.DominantLayer(t, 0, 0));
        Assert.NotEqual(before, doc.SaveToBytes());

        doc.Undo.Undo();
        Assert.Equal(before, doc.SaveToBytes());
    }

    [Fact]
    public void Lua_Queries_Heights_And_Round_Trips_A_Heightmap()
    {
        string dir = Path.Combine(Path.GetTempPath(), "glacier-terrain-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string png = Path.Combine(dir, "h.png").Replace('\\', '/');
            string raw = Path.Combine(dir, "h.r16").Replace('\\', '/');
            (EditorDocument doc, BrushEditor brushes) = Level();
            (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, string.Join('\n',
                "local t = level.terrain.get('hills')",
                "t:set_heights(function(x, z, wx, wz) return wx * 0.1 + 6 end)",
                "local a = t:height_at(t.origin.x + 10, t.origin.z + 3)",
                "local v = t:vertex_height(5, 0)",
                $"t:export_heightmap('{png}'):export_heightmap('{raw}')",
                "local copy = level.terrain.create{center = {300, 0, 0}, nx = 65, nz = 65, name = 'copy'}",
                "copy:object():set('height_range', t.height_range)",
                $"copy:import_heightmap('{png}')",
                "return string.format('%.3f,%.3f,%d,%d', a - t.origin.y, v, level.terrain.count, #t:lint())"),
                brushes: brushes, services: d => Services(d, brushes));
            Assert.True(r.Success, r.Error?.ToDisplayString());
            string[] parts = r.ReturnValue!.Split(',');
            Assert.Equal(0.6, double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), 2);
            Assert.Equal(0.6, double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), 2);
            Assert.Equal("2", parts[2]);
            Assert.Equal("1", parts[3]); // "has no compiled geometry - run Build Geometry"

            var terrains = Section(doc.Rfl).Terrains;
            Assert.Equal(terrains[0].Grid.Heights, terrains[1].Grid.Heights);
            (ushort[] samples, int w, int h) = TerrainHeightmapFile.Read(raw);
            Assert.Equal((65, 65), (w, h));
            Assert.Equal(terrains[0].Grid.Height(0, 64), samples[0]); // image row 0 = the +Z edge
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Convert_To_Brushes_Makes_One_Detail_Brush_Per_Chunk_That_Builds_Like_The_Terrain()
    {
        (EditorDocument doc, BrushEditor brushes) = Level();
        AlpineTerrain t = Section(doc.Rfl).Terrains.Single();
        var expected = TerrainChunkEmitter.BuildChunkSolids(t.DeepCopy());
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "return level.terrain.get('hills'):to_brushes{keep = false}.count", brushes: brushes);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(expected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), r.ReturnValue);
        Assert.Empty(Section(doc.Rfl).Terrains);

        var made = brushes.Brushes.Where(b => ((BrushFlags)b.Flags & BrushFlags.Detail) != 0).ToList();
        Assert.Equal(expected.Count, made.Count);
        Assert.Equal(expected.Sum(s => s.Faces.Count), made.Sum(b => b.Geometry.Faces.Count));
        Assert.All(made, b => Assert.Equal(t.Origin, b.Position));
        Assert.Contains("grass1.tga", made[0].Geometry.Textures);

        // The brushes compile; the level stays sealed.
        CompiledLevel result = Build(doc);
        Assert.Empty(HoleDetector.Detect(result.Geometry));

        doc.Undo.Undo();
        Assert.Single(Section(doc.Rfl).Terrains);
        Assert.DoesNotContain(brushes.Brushes, b => ((BrushFlags)b.Flags & BrushFlags.Detail) != 0);
    }

    [Fact]
    public void The_Properties_Fields_Edit_A_Terrain_From_Scripts()
    {
        (EditorDocument doc, BrushEditor brushes) = Level();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, string.Join('\n',
            "local o = level.objects_of('Terrain')[1]",
            "o:set_many{cell_size = 1.5, geoable = true, thickness = 20, layer_textures = 'mud.tga; rock02.tga'}",
            "o:set_pos(5, 1, 5)",
            "return o:get('layer_textures') .. '|' .. tostring(o:get('geoable')) .. '|' .. o:get('nx')"), brushes: brushes);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("mud.tga;rock02.tga|true|65", r.ReturnValue);
        AlpineTerrain t = Section(doc.Rfl).Terrains.Single();
        Assert.Equal(1.5f, t.CellSize);
        Assert.Equal(20f, t.Thickness);
        // The object position is the footprint centre at the origin's height.
        Assert.Equal(new Vec3(5, 1, 5), AlpineTerrainEdit.FootprintCentre(t));
        Assert.Equal(new Vec3(5 - (64 * 1.5f * 0.5f), 1, 5 - (64 * 1.5f * 0.5f)), t.Origin);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('Terrain')[1]:set('layer_textures', '')", "1 to 8 texture names", null);

        // Geoable caps the chunk edge at 32 cells; the build uses the effective edge and the save stores it.
        t.ChunkCells = 64;
        Assert.Equal(32, TerrainChunkEmitter.EffectiveChunkCells(t));
        doc.Rfl.UpgradeToAlpine();
        RflFile back = RflFile.Load(doc.Rfl.Save(false));
        back.ParseAllKnownSections();
        Assert.Equal(32, Section(back).Terrains.Single().ChunkCells);
    }

    [Fact]
    public void A_Pasted_Copy_Gets_Its_Own_Uid_And_No_Build_Mapping()
    {
        (EditorDocument doc, _) = Level();
        Build(doc);
        LevelObject original = doc.Objects.Single(o => o.Kind == LevelObjectKind.Terrain);
        doc.ClearSelection();
        doc.Select(original);
        doc.CopySelection();
        int uid = Assert.Single(doc.Paste());
        Assert.NotEqual(original.Uid, uid);

        var terrains = Section(doc.Rfl).Terrains;
        Assert.Equal(2, terrains.Count);
        Assert.Equal(terrains[0].Grid.Heights, terrains[1].Grid.Heights);
        Assert.NotSame(terrains[0].Grid.Heights, terrains[1].Grid.Heights);
        Assert.True(terrains[0].BuildIsCurrent);
        Assert.False(terrains[1].BuildIsCurrent);

        // The copy's mapping belongs to the original's rooms: the save drops it, keeping the original's.
        doc.Rfl.UpgradeToAlpine();
        RflFile back = RflFile.Load(doc.Rfl.Save(false));
        back.ParseAllKnownSections();
        Assert.Equal(4, Section(back).Terrains[0].BuildMapping.Count);
        Assert.Empty(Section(back).Terrains[1].BuildMapping);
    }
    [Fact]
    public void A_Terrain_Outside_Any_Air_Is_Reported_As_Having_No_Collision()
    {
        (EditorDocument doc, _) = Level("level.terrain.create{center = {0, 0, 0}, nx = 33, nz = 33, name = 'buried'}\n" +
                                        "level.brush{pos = {200, 0, 0}, size = {8, 8, 8}, air = true}\n");
        Build(doc);
        Assert.Contains(LevelLinter.Lint(doc.Rfl, new LintOptions()).Findings,
            f => f.Message.Contains("in no room, so nothing collides with them", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Ported_Resolver_Rejects_What_The_Game_Rejects()
    {
        (EditorDocument doc, _) = Level();
        CompiledLevel result = Build(doc);
        AlpineTerrain t = Section(doc.Rfl).Terrains.Single();
        AlpineTerrain bad = t.DeepCopy();
        bad.BuildMapping[1] = bad.BuildMapping[1] with { PositionHash = bad.BuildMapping[1].PositionHash + 1 };
        Assert.Equal("the compiled geometry does not match", TerrainBuildMapping.Resolve(new[] { bad }, result.Geometry).Single().Problem);

        AlpineTerrain twice = t.DeepCopy();
        Assert.Equal("a chunk room is claimed twice", TerrainBuildMapping.Resolve(new[] { t, twice }, result.Geometry)[1].Problem);

        AlpineTerrain missing = t.DeepCopy();
        missing.BuildMapping[0] = missing.BuildMapping[0] with { RoomUid = 123456 };
        Assert.Equal("a chunk room is missing", TerrainBuildMapping.Resolve(new[] { missing }, result.Geometry).Single().Problem);

        AlpineTerrain empty = t.DeepCopy();
        empty.BuildMapping[0] = new AlpineTerrainChunkMapping(AlpineTerrainRules.NoRoomUid, 1, 0);
        Assert.Equal("an empty chunk has a hash", TerrainBuildMapping.Resolve(new[] { empty }, result.Geometry).Single().Problem);
    }

    [Fact]
    public void Geoable_And_Skirted_Chunks_Close_Their_Edges()
    {
        AlpineTerrain t = AlpineTerrainEdit.Create(1, new Vec3(0, 0, 0), 5, 5, 1f);
        TerrainOps.SetHole(t, 1, 1, true);
        TerrainOps.SetDiagonal(t, 2, 2, true);
        t.ChunkCells = 16;

        // Open sheet: two triangles per solid cell only.
        Assert.Equal(2 * 15, TerrainChunkEmitter.BuildChunkSolids(t).Single().Faces.Count);

        // Skirts: walls round the outer edge (16) and the hole (4).
        t.Skirts = true;
        Assert.Equal((2 * 15) + 16 + 4, TerrainChunkEmitter.BuildChunkSolids(t).Single().Faces.Count);

        // Geoable: closed, a bottom quad per solid cell (a hole rules out the fan), the walls as with skirts.
        t.Skirts = false;
        t.Geoable = true;
        var closed = TerrainChunkEmitter.BuildChunkSolids(t).Single();
        Assert.Equal((2 * 15) + 16 + 4 + 15, closed.Faces.Count);
        Assert.All(closed.Positions.Where(p => p.Y < 0), p => Assert.Equal(-t.Thickness, p.Y));

        // Every edge of the closed solid is shared by exactly two faces.
        var edges = new Dictionary<(int, int), int>();
        foreach ((int[] v, _, _, _) in closed.Faces)
        {
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }

        Assert.All(edges.Values, n => Assert.Equal(2, n));
    }
}
