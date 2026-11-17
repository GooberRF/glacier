using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>One terrain chunk fed to a build as a temporary detail brush.</summary>
public sealed record TerrainChunkBrush(int TerrainUid, int Chunk, int BrushUid);

/// <summary>A terrain as a build compiled it: the copy it was emitted from and the resulting chunk mapping.</summary>
public sealed class TerrainBuildEntry
{
    public required int TerrainUid { get; init; }

    /// <summary>The sanitized copy the chunks were emitted from (a build may run while the terrain is edited).</summary>
    public required AlpineTerrain Source { get; init; }

    /// <summary><see cref="AlpineTerrainRules.GeometryFingerprint"/> of <see cref="Source"/>.</summary>
    public required ulong GeometryFingerprint { get; init; }

    public required int ChunkCount { get; init; }

    /// <summary>Chunks that emitted faces but could not be built (too many vertices for a room).</summary>
    public List<int> FailedChunks { get; } = new();

    /// <summary>Chunks that emitted faces, with their brush uid.</summary>
    public Dictionary<int, int> ChunkBrushUid { get; } = new();

    public long Triangles { get; set; }

    /// <summary>The mapping the build produced, or null when it cannot be written (a chunk got no room).</summary>
    public List<AlpineTerrainChunkMapping>? Mapping { get; set; }

    /// <summary>Why <see cref="Mapping"/> is null.</summary>
    public string? MappingProblem { get; set; }
}

/// <summary>The terrain part of a build: what went in, and (after the compile) the chunk-to-room mappings.</summary>
public sealed class TerrainBuild
{
    public List<TerrainBuildEntry> Terrains { get; } = new();

    /// <summary>The temporary chunk brushes, appended after every authored brush (latest in time order).</summary>
    public List<Brush> Brushes { get; } = new();

    public List<TerrainChunkBrush> Chunks { get; } = new();

    public List<string> Notes { get; } = new();

    public long Triangles => Terrains.Sum(t => t.Triangles);

    /// <summary>Uids of the chunk brushes (each isolated into a detail room of its own).</summary>
    public HashSet<int> BrushUids { get; } = new();
}

/// <summary>
/// Terrain in the geometry build, reimplemented from Alpine RED's behaviour (editor_patch/terrain_build.cpp): before the
/// build every terrain chunk is emitted as a temporary hidden detail brush in world space (:245-288), the room builder
/// isolates each into a detail room of its own (level.cpp:666-706, 874-877), and afterwards each chunk's room is recorded
/// with its distinct vertex count and position hash (<c>terrain_build_fill_mapping</c>, :1126-1170), which the game
/// matches against the compiled rooms to draw the terrain with its blend shader (game_patch/misc/alpine_terrain.cpp:63-87,
/// 186-242; <see cref="Resolve"/>). The hash covers Glacier's own compiled output, so it is self-consistent.
/// </summary>
public static class TerrainBuildMapping
{
    /// <summary>Chunk brush uids count down from here (negative, so they never meet an authored uid).</summary>
    private const int ChunkBrushUidBase = -0x40000000;

    /// <summary>Emits every terrain's chunks as detail brushes (terrain_build.cpp:268-288).</summary>
    public static TerrainBuild Prepare(IEnumerable<AlpineTerrain> terrains)
    {
        var build = new TerrainBuild();
        int next = ChunkBrushUidBase;
        foreach (AlpineTerrain original in terrains)
        {
            AlpineTerrain t = original.DeepCopy();
            AlpineTerrainEdit.Sanitize(t);
            var entry = new TerrainBuildEntry
            {
                TerrainUid = t.Uid,
                Source = t,
                GeometryFingerprint = AlpineTerrainRules.GeometryFingerprint(t),
                ChunkCount = TerrainChunkEmitter.ChunkCount(t),
            };
            var notes = new List<string>();
            List<TerrainChunkSolid> solids = TerrainChunkEmitter.BuildChunkSolids(t, notes);
            build.Notes.AddRange(notes);
            var built = new HashSet<int>(solids.Select(s => s.Chunk));
            for (int k = 0; k < entry.ChunkCount; k++)
            {
                if (!built.Contains(k) && ChunkHasFaces(t, k))
                {
                    entry.FailedChunks.Add(k);
                }
            }

            foreach (TerrainChunkSolid solid in solids)
            {
                int uid = next--;
                Brush brush = TerrainChunkEmitter.ToBrush(solid, uid, Vec3.Zero);

                // Isolated like a geoable brush (RED keeps the chunk brushes in the isolation set,
                // terrain_build.cpp:1098-1101); the flag never reaches a file, these brushes are never saved.
                brush.Flags |= (uint)BrushFlags.Geoable;
                build.Brushes.Add(brush);
                build.Chunks.Add(new TerrainChunkBrush(t.Uid, solid.Chunk, uid));
                build.BrushUids.Add(uid);
                entry.ChunkBrushUid[solid.Chunk] = uid;
                entry.Triangles += solid.Triangles;
            }

            build.Terrains.Add(entry);
        }

        return build;
    }

    private static bool ChunkHasFaces(AlpineTerrain t, int k)
    {
        int edge = TerrainChunkEmitter.EffectiveChunkCells(t);
        TerrainChunkRect r = AlpineTerrainRules.ChunkRect(t.Grid.CellsX, t.Grid.CellsZ, edge, k);
        for (int z = r.Z0; z < r.Z1; z++)
        {
            for (int x = r.X0; x < r.X1; x++)
            {
                if (!t.Grid.IsHole(x, z))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// After the compile: each chunk's room and its hash, per terrain (terrain_build.cpp:361-454, 1126-1170). A chunk that
    /// emitted faces but reached no room leaves the terrain without a mapping, as Alpine RED saves it then.
    /// </summary>
    public static void Finish(TerrainBuild build, CompiledLevel result)
    {
        Geometry g = result.Geometry;
        var roomByUid = new Dictionary<int, int>();
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            roomByUid.TryAdd(g.Rooms[i].Id, i);
        }

        foreach (TerrainBuildEntry entry in build.Terrains)
        {
            string who = TerrainChunkEmitter.Label(entry.Source);
            var mapping = new List<AlpineTerrainChunkMapping>(entry.ChunkCount);
            string? problem = entry.FailedChunks.Count > 0
                ? $"{who}: chunk {entry.FailedChunks[0]} has no compiled room - rebuild before saving."
                : null;
            for (int k = 0; k < entry.ChunkCount && problem is null; k++)
            {
                if (!entry.ChunkBrushUid.TryGetValue(k, out int brushUid))
                {
                    mapping.Add(new AlpineTerrainChunkMapping(AlpineTerrainRules.NoRoomUid, 0, 0));
                    continue;
                }

                if (!result.BrushRoomUid.TryGetValue(brushUid, out int roomUid) || !roomByUid.TryGetValue(roomUid, out int roomIndex))
                {
                    problem = $"{who}: chunk {k} produced no compiled room.";
                    break;
                }

                (uint count, ulong hash) = RoomHash(g, roomIndex);
                mapping.Add(new AlpineTerrainChunkMapping(roomUid, count, hash));
            }

            entry.Mapping = problem is null ? mapping : null;
            entry.MappingProblem = problem;
            if (problem is not null)
            {
                result.Report.Add(BuildSeverity.Warning, problem);
            }
        }

        foreach (string note in build.Notes)
        {
            result.Report.Add(BuildSeverity.Warning, note);
        }

        if (build.Triangles > AlpineTerrainRules.LevelTriangleBudget)
        {
            result.Report.Add(BuildSeverity.Warning,
                $"The level's terrains make {build.Triangles} triangles, over the budget of {AlpineTerrainRules.LevelTriangleBudget}.");
        }
    }

    /// <summary>The distinct vertex count and position hash of one compiled room's faces (alpine_terrain.h:1610-1621).</summary>
    public static (uint Count, ulong Hash) RoomHash(Geometry g, int roomIndex) =>
        AlpineTerrainRules.PositionSetHash(RoomPositions(g, roomIndex));

    private static IEnumerable<Vec3> RoomPositions(Geometry g, int roomIndex)
    {
        foreach (Face f in g.Faces)
        {
            if (f.RoomIndex != roomIndex)
            {
                continue;
            }

            foreach (FaceVertex fv in f.Vertices)
            {
                if (fv.Index >= 0 && fv.Index < g.Vertices.Count)
                {
                    yield return g.Vertices[fv.Index];
                }
            }
        }
    }

    /// <summary>
    /// The game's check of the level's terrains against its compiled geometry (game_patch/misc/alpine_terrain.cpp:63-87,
    /// 186-242): per terrain, null when every chunk matches its room (the terrain draws with the terrain renderer), else why
    /// the game draws it as plain geometry. A mapping whose length is not the chunk count is dropped on read
    /// (alpine_terrain_reader.h:170), so it counts as absent.
    /// </summary>
    public static List<(AlpineTerrain Terrain, string? Problem)> Resolve(IReadOnlyList<AlpineTerrain> terrains, Geometry? g)
    {
        var results = new List<(AlpineTerrain, string?)>();
        var rooms = new Dictionary<int, int?>();
        if (g is not null)
        {
            for (int i = 0; i < g.Rooms.Count; i++)
            {
                int uid = g.Rooms[i].Id;
                if (uid == -1)
                {
                    continue;
                }

                // A uid two rooms share cannot identify either (:194-199).
                rooms[uid] = rooms.ContainsKey(uid) ? null : i;
            }
        }

        var claimed = new HashSet<int>();
        foreach (AlpineTerrain t in terrains)
        {
            string? err = ResolveOne(t, g, rooms, out List<int> indices);
            if (err is null)
            {
                bool shared = indices.Count != indices.Distinct().Count() || indices.Any(claimed.Contains);
                if (shared)
                {
                    err = "a chunk room is claimed twice";
                }
            }

            if (err is null)
            {
                claimed.UnionWith(indices);
            }

            results.Add((t, err));
        }

        return results;
    }

    private static string? ResolveOne(AlpineTerrain t, Geometry? g, Dictionary<int, int?> rooms, out List<int> indices)
    {
        indices = new List<int>();
        int headerChunks = AlpineTerrainRules.ChunkCount(t.Grid.CellsX, t.Grid.CellsZ, t.ChunkCells);
        if (t.BuildMapping.Count == 0 || t.BuildMapping.Count != headerChunks)
        {
            return "it was saved without Build Geometry";
        }

        foreach (AlpineTerrainChunkMapping m in t.BuildMapping)
        {
            if (m.RoomUid == AlpineTerrainRules.NoRoomUid)
            {
                if (m.VertexCount != 0 || m.PositionHash != 0)
                {
                    return "an empty chunk has a hash";
                }

                continue;
            }

            if (g is null || !rooms.TryGetValue(m.RoomUid, out int? index) || index is null)
            {
                return "a chunk room is missing";
            }

            Room room = g.Rooms[index.Value];
            if (room.IsSubroom == 0 || room.IsSkyroom != 0)
            {
                return "a chunk room is not a detail room";
            }

            (uint count, ulong hash) = RoomHash(g, index.Value);
            if (count != m.VertexCount || hash != m.PositionHash)
            {
                return "the compiled geometry does not match";
            }

            indices.Add(index.Value);
        }

        return null;
    }
}
