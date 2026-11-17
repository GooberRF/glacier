using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Linting;

/// <summary>
/// Checks on Alpine terrains, each mirroring a report Alpine RED's build or save makes or a condition under which the game
/// draws, collides with or carves the terrain differently from what was authored (editor_patch/terrain_build.cpp,
/// terrain.cpp; game_patch/misc/alpine_terrain.cpp). Measured against the level's compiled geometry as it is now, so
/// a level that was never built reports that first.
/// </summary>
internal static class TerrainChecks
{
    /// <summary>Roughly the reach of stock weapon craters with the Big Craters mutator (terrain_build.cpp:314-315).</summary>
    public const float GeoableFloorWarnDepth = 8f;

    public static void Run(RflFile rfl, List<LevelObject> objects, List<LintFinding> findings)
    {
        RflSection? host = rfl.Sections.FirstOrDefault(s => s.Content is AlpineTerrainSection);
        if (host?.Content is not AlpineTerrainSection section)
        {
            return;
        }

        LevelObject? first = objects.FirstOrDefault(o => o.Model is AlpineTerrain);
        if (section.IsReadOnly)
        {
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.AlpineObject,
                $"The level's terrain chunk is in a layout Alpine Faction does not load ({section.ReadOnlyReason}): the game ignores every " +
                "terrain in it. Glacier keeps it unchanged.", first?.Uid ?? 0, Location: first?.Position));
            return;
        }

        Geometry? geometry = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault()?.Geometry;
        var props = rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().FirstOrDefault();
        var regions = rfl.Sections.Select(s => s.Content).OfType<GeoRegionsSection>().FirstOrDefault()?.Regions ?? new List<GeoRegion>();
        var resolved = TerrainBuildMapping.Resolve(section.Terrains, geometry).ToDictionary(r => r.Terrain, r => r.Problem);
        long triangles = 0, raw = 0;
        foreach (AlpineTerrain t in section.Terrains)
        {
            LevelObject? o = objects.FirstOrDefault(x => ReferenceEquals(x.Model, t));
            int uid = o?.Uid ?? t.Uid;
            Vec3 at = o?.Position ?? t.Origin;
            string who = TerrainChunkEmitter.Label(t);
            void Warn(string m) => findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.AlpineObject, m, uid, Location: at));
            void Info(string m) => findings.Add(new LintFinding(LintSeverity.Info, LintCategory.AlpineObject, m, uid, Location: at));

            AlpineTerrain copy = t.DeepCopy();
            AlpineTerrainEdit.Sanitize(copy);
            raw += AlpineTerrainSection.BuildBlob(copy).Length;
            triangles += TerrainChunkEmitter.BuildChunkSolids(copy).Sum(s => s.Triangles);
            TextureNames(t, Warn);
            if (t.ScriptName.Length > AlpineTerrainRules.MaxScriptNameLength)
            {
                Info($"{who}: its script name is longer than {AlpineTerrainRules.MaxScriptNameLength} characters; Alpine Faction keeps the first {AlpineTerrainRules.MaxScriptNameLength}.");
            }

            if (Math.Abs(t.Origin.X) > AlpineTerrainRules.MaxCoord || Math.Abs(t.Origin.Y) > AlpineTerrainRules.MaxCoord || Math.Abs(t.Origin.Z) > AlpineTerrainRules.MaxCoord)
            {
                Warn($"{who} is outside the world limit of {AlpineTerrainRules.MaxCoord}; saving moves it inside.");
            }

            // terrain_build.cpp:885-910: what a save would write the mapping from.
            if (t.BuildMapping.Count == 0)
            {
                Warn($"{who} has no compiled geometry - run Build Geometry. Until then the level has no faces or collision for it.");
            }
            else if (!t.BuildIsCurrent)
            {
                Warn($"{who} changed since the last Build Geometry - rebuild. The game would draw its old compiled geometry without the terrain blend.");
            }
            else if (resolved.GetValueOrDefault(t) is { } problem)
            {
                // game_patch/misc/alpine_terrain.cpp:221-223
                Warn($"{who}: the game draws it as plain geometry because {problem}.");
            }

            if (geometry is not null && t.BuildIsCurrent && resolved.GetValueOrDefault(t) is null)
            {
                ChunkPlacement(t, geometry, who, Warn);
            }

            if (t.Geoable)
            {
                Geoable(t, props, regions, who, Warn);
            }
        }

        if (triangles > AlpineTerrainRules.LevelTriangleBudget && first is not null)
        {
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.AlpineObject,
                $"The level's terrains make {triangles} triangles, over Alpine RED's budget of {AlpineTerrainRules.LevelTriangleBudget}.", first.Uid, Location: first.Position));
        }

        if (raw > AlpineTerrainRules.MaxLevelRawBytes && first is not null)
        {
            findings.Add(new LintFinding(LintSeverity.Error, LintCategory.AlpineObject,
                $"The level's terrain data is {raw / (1024 * 1024)} MB, over Alpine Faction's {AlpineTerrainRules.MaxLevelRawBytes / (1024 * 1024)} MB: the game drops the whole terrain chunk.", first.Uid, Location: first.Position));
        }
    }

    /// <summary>A texture name over RED's caps is replaced on save, as Alpine RED replaces it (terrain.cpp:378-381).</summary>
    private static void TextureNames(AlpineTerrain t, Action<string> warn)
    {
        string who = TerrainChunkEmitter.Label(t);
        var names = t.Layers.Select(l => l.Texture).Concat(t.Overlays.Select(l => l.Texture)).Append(t.UndersideTexture).Append(t.CraterTexture);
        foreach (string name in names.Where(n => !AlpineTerrainRules.TextureNameValid(n)).Distinct())
        {
            warn($"{who}: texture name '{name}' is longer than RFL texture names allow (31 characters); saving replaces it.");
        }
    }

    /// <summary>
    /// Collision reaches a detail room only through a parent room whose box the query touches (terrain_build.cpp:456-519):
    /// a chunk in no room has no collision, a chunk reaching outside its parents' boxes loses collision there, and the sky
    /// room does not support terrain.
    /// </summary>
    private static void ChunkPlacement(AlpineTerrain t, Geometry g, string who, Action<string> warn)
    {
        var roomByUid = new Dictionary<int, int>();
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            roomByUid.TryAdd(g.Rooms[i].Id, i);
        }

        var parents = new Dictionary<int, List<int>>();
        foreach (SubroomList list in g.SubroomLists)
        {
            if (list.RoomIndex < 0 || list.RoomIndex >= g.Rooms.Count || g.Rooms[list.RoomIndex].IsSubroom != 0)
            {
                continue;
            }

            foreach (int sub in list.SubroomIndices)
            {
                if (!parents.TryGetValue(sub, out List<int>? p))
                {
                    parents[sub] = p = new List<int>();
                }

                p.Add(list.RoomIndex);
            }
        }

        int noParent = 0, outside = 0, sky = 0;
        foreach (AlpineTerrainChunkMapping m in t.BuildMapping)
        {
            if (m.RoomUid == AlpineTerrainRules.NoRoomUid || !roomByUid.TryGetValue(m.RoomUid, out int room))
            {
                continue;
            }

            if (!parents.TryGetValue(room, out List<int>? ps) || ps.Count == 0)
            {
                noParent++;
                continue;
            }

            if (ps.Any(p => g.Rooms[p].IsSkyroom != 0))
            {
                sky++;
            }

            bool inside = true;
            foreach (Face f in g.Faces)
            {
                if (f.RoomIndex != room || !inside)
                {
                    continue;
                }

                foreach (FaceVertex fv in f.Vertices)
                {
                    if (fv.Index < 0 || fv.Index >= g.Vertices.Count)
                    {
                        continue;
                    }

                    Vec3 v = g.Vertices[fv.Index];
                    if (!ps.Any(p => InBox(v, g.Rooms[p].Aabb)))
                    {
                        inside = false;
                        break;
                    }
                }
            }

            if (!inside)
            {
                outside++;
            }
        }

        if (noParent > 0)
        {
            warn($"{who}: {noParent} chunk(s) are in no room, so nothing collides with them; enclose the terrain in air.");
        }

        if (outside > 0)
        {
            warn($"{who}: {outside} chunk(s) reach outside the bounding boxes of the rooms around them, where collision is skipped; extend the air around the terrain.");
        }

        if (sky > 0)
        {
            warn($"{who}: {sky} chunk(s) are in the sky room, which terrain does not support.");
        }
    }

    private static bool InBox(Vec3 p, Aabb box)
    {
        const float eps = 0.001f;
        Vec3 lo = new(Math.Min(box.P1.X, box.P2.X), Math.Min(box.P1.Y, box.P2.Y), Math.Min(box.P1.Z, box.P2.Z));
        Vec3 hi = new(Math.Max(box.P1.X, box.P2.X), Math.Max(box.P1.Y, box.P2.Y), Math.Max(box.P1.Z, box.P2.Z));
        return p.X >= lo.X - eps && p.X <= hi.X + eps && p.Y >= lo.Y - eps && p.Y <= hi.Y + eps && p.Z >= lo.Z - eps && p.Z <= hi.Z + eps;
    }

    /// <summary>
    /// Geoable terrains (terrain_build.cpp:520-559): carving needs RF2-style geomod, at least one geoable chunk, no Geo
    /// Region over the chunks (they cannot be carved there), and enough depth under the lowest point that craters do not
    /// punch through the bottom.
    /// </summary>
    private static void Geoable(AlpineTerrain t, AlpineLevelPropertiesSection? props, List<GeoRegion> regions, string who, Action<string> warn)
    {
        AlpineTerrainGrid g = t.Grid;
        int edge = AlpineTerrainRules.EffectiveChunkCells(g.CellsX, g.CellsZ, t.ChunkCells, AlpineTerrainRules.FlagMask & (t.Flags | AlpineTerrainRules.FlagGeoable));
        int count = AlpineTerrainRules.ChunkCount(g.CellsX, g.CellsZ, edge);
        bool sameLayout = g.GeoMask is null || g.GeoMask.Length == AlpineTerrainRules.ChunkMaskBytes(count);
        var chunks = new List<TerrainChunkRect>();
        for (int k = 0; k < count; k++)
        {
            if (g.GeoMask is null || !sameLayout || AlpineTerrainRules.GetBit(g.GeoMask, k))
            {
                chunks.Add(AlpineTerrainRules.ChunkRect(g.CellsX, g.CellsZ, edge, k));
            }
        }

        if (chunks.Count == 0)
        {
            warn($"{who} is geoable, but none of its chunks are; nothing on it can be carved.");
            return;
        }

        if (props is null || props.Rf2StyleGeomod == 0)
        {
            warn($"{who} is geoable, but RF2-style geomod is off in Level Properties; it must be enabled for the terrain to be carved.");
        }

        float bottom = TerrainChunkEmitter.BottomY(t);
        float top = AlpineTerrainRules.WorldY(t.Origin.Y, ushort.MaxValue, t.HeightMin, t.HeightRange);
        foreach (GeoRegion r in regions)
        {
            Vec3 half;
            if (r.IsSphere && r.Radius is float radius)
            {
                half = new Vec3(radius, radius, radius);
            }
            else if (r.IsBox && r.Rotation is Mat3 m)
            {
                float ex = (r.Width ?? 0) * 0.5f, ey = (r.Height ?? 0) * 0.5f, ez = (r.Depth ?? 0) * 0.5f;
                half = new Vec3(
                    (Math.Abs(m.Right.X) * ex) + (Math.Abs(m.Up.X) * ey) + (Math.Abs(m.Forward.X) * ez),
                    (Math.Abs(m.Right.Y) * ex) + (Math.Abs(m.Up.Y) * ey) + (Math.Abs(m.Forward.Y) * ez),
                    (Math.Abs(m.Right.Z) * ex) + (Math.Abs(m.Up.Z) * ey) + (Math.Abs(m.Forward.Z) * ez));
            }
            else
            {
                continue;
            }

            Vec3 p = r.Position;
            bool hit = chunks.Any(c =>
                t.Origin.X + (c.X0 * t.CellSize) <= p.X + half.X && t.Origin.X + (c.X1 * t.CellSize) >= p.X - half.X
                && bottom <= p.Y + half.Y && top >= p.Y - half.Y
                && t.Origin.Z + (c.Z0 * t.CellSize) <= p.Z + half.Z && t.Origin.Z + (c.Z1 * t.CellSize) >= p.Z - half.Z);
            if (hit)
            {
                warn($"{who} is geoable but overlaps Geo Region {r.Uid}; it cannot be carved there.");
            }
        }

        ushort lowest = ushort.MaxValue;
        foreach (TerrainChunkRect c in chunks)
        {
            for (int z = c.Z0; z <= c.Z1; z++)
            {
                for (int x = c.X0; x <= c.X1; x++)
                {
                    lowest = Math.Min(lowest, g.Height(x, z));
                }
            }
        }

        float floor = AlpineTerrainRules.WorldY(t.Origin.Y, lowest, t.HeightMin, t.HeightRange) - bottom;
        if (floor < GeoableFloorWarnDepth)
        {
            warn($"{who}: the lowest point of its geoable chunks is only {floor:0.0} above its bottom, so craters may punch through there; raise Thickness.");
        }
    }
}
