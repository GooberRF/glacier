using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Terrain editing (Alpine Faction terrain, chunk 0x0AFBAE0B): heights, grid size, texture layers, holes, the height
/// query, and Convert to Brushes. The pure operations change a terrain in place and keep it within what Alpine's readers
/// accept; <see cref="Apply"/> wraps any of them as one undoable document edit. Behaviour follows Alpine RED's terrain
/// editor (editor_patch/terrain.cpp, terrain_build.cpp and the derivations of common/include/common/terrain/alpine_terrain.h);
/// the code is Glacier's own.
/// </summary>
public static class TerrainOps
{
    // ---- document edits --------------------------------------------------------------------------------------------

    /// <summary>The terrain behind <paramref name="obj"/>, or an error when it is not an editable terrain.</summary>
    public static AlpineTerrain Require(LevelObject obj)
    {
        if (obj.Model is not AlpineTerrain t)
        {
            throw new InvalidOperationException($"{obj.Kind} {obj.Uid} is not a terrain.");
        }

        if (obj.IsReadOnly)
        {
            throw new InvalidOperationException(
                $"Terrain {obj.Uid} is read-only: its chunk is in a layout Alpine Faction does not load, so Glacier keeps it unchanged.");
        }

        return t;
    }

    /// <summary>
    /// Runs <paramref name="mutate"/> on a copy of the terrain, sanitizes it, and installs the result as one undoable
    /// command (undo restores the exact prior state, its loaded blob and build state included).
    /// </summary>
    public static void Apply(EditorDocument doc, LevelObject obj, string description, Action<AlpineTerrain> mutate)
    {
        ArgumentNullException.ThrowIfNull(doc);
        AlpineTerrain t = Require(obj);
        AlpineTerrain before = t.DeepCopy();
        AlpineTerrain after = t.DeepCopy();
        mutate(after);
        AlpineTerrainEdit.Sanitize(after);
        RflSection section = obj.Section;
        doc.Undo.Execute(new RelayCommand(description,
            () =>
            {
                t.Assign(after);
                section.Dirty = true;
            },
            () =>
            {
                t.Assign(before);
                section.Dirty = true;
            }));
    }

    /// <summary>
    /// Places a new terrain (Alpine RED's new terrain: 129 x 129 flat vertices, 2 m cells, one default layer — terrain.cpp:
    /// 3089-3118, unless <paramref name="nx"/> / <paramref name="nz"/> / <paramref name="cellSize"/> say otherwise) with its
    /// centre at <paramref name="center"/>, creating the terrain chunk when the level has none. One undoable command.
    /// </summary>
    public static LevelObject Create(EditorDocument doc, Vec3 center, int nx = AlpineTerrainRules.DefaultVerts,
        int nz = AlpineTerrainRules.DefaultVerts, float cellSize = AlpineTerrainRules.DefaultCellSize, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        RflSection? existing = doc.Rfl.Sections.FirstOrDefault(s => s.TypeId == (uint)SectionType.AlpineTerrain);
        if (existing is not null)
        {
            doc.Rfl.ParseAllKnownSections();
        }

        if (existing?.Content is AlpineTerrainSection { IsReadOnly: true } raw)
        {
            throw new InvalidOperationException(
                $"The level's terrain chunk is in a layout Glacier keeps unchanged ({raw.ReadOnlyReason}); no terrain can be added to it.");
        }

        var current = existing?.Content as AlpineTerrainSection;
        if ((current?.Terrains.Count ?? 0) >= AlpineTerrainRules.MaxTerrains)
        {
            throw new InvalidOperationException($"A level holds at most {AlpineTerrainRules.MaxTerrains} terrains.");
        }

        int uid = doc.AllocateUid();
        nx = Math.Clamp(nx, AlpineTerrainRules.MinVerts, AlpineTerrainRules.MaxVerts);
        nz = Math.Clamp(nz, AlpineTerrainRules.MinVerts, AlpineTerrainRules.MaxVerts);
        cellSize = AlpineTerrainRules.ClampFinite(cellSize, AlpineTerrainRules.MinCellSize, AlpineTerrainRules.MaxCellSize, AlpineTerrainRules.DefaultCellSize);
        var origin = new Vec3(
            center.X - (AlpineTerrainRules.Extent(nx, cellSize) * 0.5f), center.Y, center.Z - (AlpineTerrainRules.Extent(nz, cellSize) * 0.5f));
        AlpineTerrain terrain = AlpineTerrainEdit.Create(uid, origin, nx, nz, cellSize);
        if (!string.IsNullOrEmpty(name))
        {
            terrain.ScriptName = name;
        }

        long raw0 = (current?.Terrains ?? new List<AlpineTerrain>()).Sum(t => (long)AlpineTerrainSection.BuildBlob(t).Length);
        if (raw0 + AlpineTerrainSection.BuildBlob(terrain).Length > AlpineTerrainRules.MaxLevelRawBytes)
        {
            throw new InvalidOperationException("The level's terrain data limit (128 MB) has been reached.");
        }

        // The palette's placement path (section created inside the command, removed again on undo).
        ObjectBlueprint bp = ObjectFactory.Build(LevelObjectKind.Terrain, uid, center);
        var model = (AlpineTerrain)bp.Model;
        model.Assign(terrain);
        return doc.PlaceBlueprint(bp, "Place terrain") ?? throw new InvalidOperationException("The new terrain was not listed.");
    }

    /// <summary>
    /// Convert to Brushes (terrain_build.cpp:1337-1357, terrain.cpp:2902-2964): one permanent detail brush per chunk,
    /// exactly as Build Geometry emits it, every chunk pivoted at the terrain origin so a vertex two chunks share lands on
    /// one position. The terrain is deleted unless <paramref name="keep"/>. One undoable command (Alpine RED's has none).
    /// </summary>
    public static IReadOnlyList<int> ConvertToBrushes(EditorDocument doc, BrushEditor brushes, LevelObject obj, bool keep, List<string>? notes = null)
    {
        AlpineTerrain t = Require(obj);
        AlpineTerrain source = t.DeepCopy();
        AlpineTerrainEdit.Sanitize(source);
        List<TerrainChunkSolid> solids = TerrainChunkEmitter.BuildChunkSolids(source, notes);
        var created = new List<Brush>();
        foreach (TerrainChunkSolid solid in solids)
        {
            created.Add(TerrainChunkEmitter.ToBrush(solid, doc.AllocateUid(), source.Origin));
        }

        using UndoStack.Transaction tx = doc.Undo.BeginTransaction($"Convert {TerrainChunkEmitter.Label(t)} to brushes");
        brushes.AddBrushes(created, $"Convert {TerrainChunkEmitter.Label(t)} to brushes");
        if (!keep && obj.OwningList is { } list)
        {
            int index = list.IndexOf(t);
            RflSection section = obj.Section;
            doc.Undo.Execute(new RelayCommand("Delete terrain",
                () =>
                {
                    list.Remove(t);
                    section.Dirty = true;
                    doc.RefreshObjects();
                },
                () =>
                {
                    list.Insert(Math.Clamp(index, 0, list.Count), t);
                    section.Dirty = true;
                    doc.RefreshObjects();
                }));
        }

        tx.Commit();
        notes?.Add($"{TerrainChunkEmitter.Label(t)} was converted into {created.Count} detail brush(es).");
        return created.Select(b => b.Uid).ToList();
    }

    // ---- heights ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets every vertex's height from <paramref name="offsetAt"/> (x, z) = metres above the terrain origin. With
    /// <paramref name="fit"/> the height mapping is refitted to exactly the new span (finest steps); otherwise it is only
    /// widened as Alpine's sculpting widens it (<see cref="GrowHeightMapping"/>).
    /// </summary>
    public static void SetHeightOffsets(AlpineTerrain t, Func<int, int, double> offsetAt, bool fit = true)
    {
        AlpineTerrainGrid g = t.Grid.Clone();
        var offsets = new double[g.Nx * g.Nz];
        double lo = double.MaxValue, hi = double.MinValue;
        for (int z = 0; z < g.Nz; z++)
        {
            for (int x = 0; x < g.Nx; x++)
            {
                double v = offsetAt(x, z);
                if (!double.IsFinite(v))
                {
                    throw new ArgumentException($"The height at vertex ({x}, {z}) is not a finite number.");
                }

                offsets[(z * g.Nx) + x] = v;
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
        }

        if (fit)
        {
            float max = AlpineTerrainRules.MaxCoord;
            t.HeightMin = (float)Math.Clamp(lo, -max, max);
            t.HeightRange = (float)Math.Clamp(hi - t.HeightMin, AlpineTerrainRules.MinHeightRange, AlpineTerrainRules.MaxHeightRange);
        }
        else
        {
            float oldMin = t.HeightMin;
            (float min, float range, _) = GrowHeightMapping(t.HeightMin, t.HeightRange, (float)lo, (float)hi,
                t.Geoable ? t.HeightMin - t.Thickness + AlpineTerrainRules.MinThickness : double.NegativeInfinity);
            t.HeightMin = min;
            t.HeightRange = range;
            t.Thickness = ThicknessAfterGrowth(t.Flags, t.Thickness, oldMin, min);
        }

        for (int i = 0; i < offsets.Length; i++)
        {
            g.Heights[i] = AlpineTerrainRules.EncodeHeight(offsets[i], t.HeightMin, t.HeightRange);
        }

        t.Grid = g;
    }

    /// <summary>Sets every vertex from a 0..1 value over the current height mapping (0 = Height Min, 1 = Height Min + Range).</summary>
    public static void SetNormalizedHeights(AlpineTerrain t, Func<int, int, double> value01)
    {
        AlpineTerrainGrid g = t.Grid.Clone();
        for (int z = 0; z < g.Nz; z++)
        {
            for (int x = 0; x < g.Nx; x++)
            {
                g.Heights[(z * g.Nx) + x] = AlpineTerrainRules.EncodeHeight01((float)value01(x, z));
            }
        }

        t.Grid = g;
    }

    /// <summary>Metres above the terrain origin of vertex (x, z), in double (alpine_terrain.h:387-390).</summary>
    public static double HeightOffset(AlpineTerrain t, int x, int z) =>
        t.HeightMin + (t.Grid.Height(x, z) / 65535.0 * t.HeightRange);

    /// <summary>
    /// World y of the emitted surface at world (x, z): on the triangle the cell's diagonal bit picks, exact at every vertex,
    /// clamped to the grid; NaN for a NaN coordinate (<c>height_at</c>, alpine_terrain.h:1078-1101).
    /// </summary>
    public static float HeightAt(AlpineTerrain t, float worldX, float worldZ)
    {
        AlpineTerrainGrid g = t.Grid;
        int cx = g.CellsX, cz = g.CellsZ;
        float fx = Math.Clamp((worldX - t.Origin.X) / t.CellSize, 0f, cx);
        float fz = Math.Clamp((worldZ - t.Origin.Z) / t.CellSize, 0f, cz);
        if (float.IsNaN(fx) || float.IsNaN(fz))
        {
            return float.NaN;
        }

        const float coordUlps = 4.8e-7f;
        float ulps = coordUlps / t.CellSize;
        float tolX = Math.Clamp((MathF.Abs(worldX) + MathF.Abs(t.Origin.X)) * ulps, 1e-4f, 0.1f);
        float tolZ = Math.Clamp((MathF.Abs(worldZ) + MathF.Abs(t.Origin.Z)) * ulps, 1e-4f, 0.1f);
        if (MathF.Abs(fx - MathF.Round(fx)) < tolX)
        {
            fx = MathF.Round(fx);
        }

        if (MathF.Abs(fz - MathF.Round(fz)) < tolZ)
        {
            fz = MathF.Round(fz);
        }

        int x = Math.Min((int)fx, cx - 1);
        int z = Math.Min((int)fz, cz - 1);
        float u = fx - x, v = fz - z;
        float h00 = TerrainChunkEmitter.TopY(t, x, z), h10 = TerrainChunkEmitter.TopY(t, x + 1, z);
        float h11 = TerrainChunkEmitter.TopY(t, x + 1, z + 1), h01 = TerrainChunkEmitter.TopY(t, x, z + 1);
        if (!g.Diagonal(x, z))
        {
            return u >= v ? h00 + (u * (h10 - h00)) + (v * (h11 - h10)) : h00 + (v * (h01 - h00)) + (u * (h11 - h01));
        }

        return u + v <= 1f ? h00 + (u * (h10 - h00)) + (v * (h01 - h00))
            : h11 + ((1f - u) * (h01 - h11)) + ((1f - v) * (h10 - h11));
    }

    /// <summary>
    /// World y of the terrain's walkable top at world (x, z) as the build emits it (<see cref="HeightAt"/>), or null where
    /// there is no top: outside the footprint or over a hole cell (a hole emits no top triangles, alpine_terrain.h:1207-1310).
    /// A point on a cell edge reads the cell whose corner it is not past, so the footprint's +X / +Z edges count.
    /// </summary>
    public static float? SurfaceAt(AlpineTerrain t, float worldX, float worldZ)
    {
        AlpineTerrainGrid g = t.Grid;
        int cx = g.CellsX, cz = g.CellsZ;
        float fx = (worldX - t.Origin.X) / t.CellSize;
        float fz = (worldZ - t.Origin.Z) / t.CellSize;
        if (!(fx >= 0f && fz >= 0f && fx <= cx && fz <= cz))
        {
            return null; // outside the footprint (or NaN)
        }

        int x = Math.Min((int)fx, cx - 1);
        int z = Math.Min((int)fz, cz - 1);
        return g.IsHole(x, z) ? null : HeightAt(t, worldX, worldZ);
    }

    /// <summary>
    /// The first point where the ray from <paramref name="origin"/> along <paramref name="dir"/> meets the terrain's top
    /// surface within its footprint (hole cells count as surface, as Alpine RED's brush ray does — terrain_paint.cpp:
    /// 1026-1038): the distance along the ray, or null. Marches the footprint in half-cell steps and refines by bisection.
    /// </summary>
    public static float? RayHit(AlpineTerrain t, Vec3 origin, Vec3 dir, float maxT = 100000f)
    {
        float ex = AlpineTerrainRules.Extent(t.Nx, t.CellSize), ez = AlpineTerrainRules.Extent(t.Nz, t.CellSize);
        float x0 = t.Origin.X, x1 = t.Origin.X + ex, z0 = t.Origin.Z, z1 = t.Origin.Z + ez;
        float tMin = 0f, tMax = maxT;
        bool Slab(float o, float d, float lo, float hi)
        {
            if (MathF.Abs(d) < 1e-12f)
            {
                return o >= lo && o <= hi;
            }

            float a = (lo - o) / d, b = (hi - o) / d;
            if (a > b)
            {
                (a, b) = (b, a);
            }

            tMin = MathF.Max(tMin, a);
            tMax = MathF.Min(tMax, b);
            return tMin <= tMax;
        }

        if (!Slab(origin.X, dir.X, x0, x1) || !Slab(origin.Z, dir.Z, z0, z1))
        {
            return null;
        }

        float Above(float s) => origin.Y + (dir.Y * s) - HeightAt(t, origin.X + (dir.X * s), origin.Z + (dir.Z * s));
        float horiz = MathF.Sqrt((dir.X * dir.X) + (dir.Z * dir.Z));
        float step = horiz > 1e-6f ? t.CellSize * 0.5f / horiz : MathF.Max(tMax - tMin, 1e-3f);
        float prevS = tMin, prev = Above(tMin);
        if (prev <= 0f)
        {
            return prev > -t.CellSize ? tMin : null;
        }

        for (float s = tMin + step; ; s += step)
        {
            float cur = Above(MathF.Min(s, tMax));
            if (cur <= 0f)
            {
                float lo = prevS, hi = MathF.Min(s, tMax);
                for (int i = 0; i < 24; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Above(mid) > 0f)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                return hi;
            }

            if (s >= tMax)
            {
                return null;
            }

            prevS = s;
            prev = cur;
        }
    }

    /// <summary>
    /// Snaps this terrain's border vertices to the heights of any other terrain with the same cell size whose grid is
    /// aligned with it and has a vertex at the same x/z (Alpine RED's Stitch Edges, terrain_paint.cpp:1630-1704). Returns
    /// how many border vertices matched a neighbour and how many neighbours touched.
    /// </summary>
    public static (int Vertices, int Neighbours) StitchEdges(AlpineTerrain t, IEnumerable<AlpineTerrain> others)
    {
        float cs = t.CellSize;
        var neighbours = new List<(AlpineTerrain T, long Dx, long Dz)>();
        foreach (AlpineTerrain o in others)
        {
            if (ReferenceEquals(o, t) || o.Uid == t.Uid || MathF.Abs(o.CellSize - cs) > cs * 1e-4f)
            {
                continue;
            }

            float fx = (o.Origin.X - t.Origin.X) / cs, fz = (o.Origin.Z - t.Origin.Z) / cs;
            float rx = MathF.Round(fx), rz = MathF.Round(fz);
            if (MathF.Abs(fx - rx) > 1e-3f || MathF.Abs(fz - rz) > 1e-3f)
            {
                continue;
            }

            neighbours.Add((o, (long)rx, (long)rz));
        }

        AlpineTerrainGrid g = t.Grid;
        var targets = new List<TerrainBrush.HeightTarget>();
        var used = new HashSet<int>();
        void Match(int i, int j)
        {
            foreach ((AlpineTerrain o, long dx, long dz) in neighbours)
            {
                long oi = i - dx, oj = j - dz;
                if (oi < 0 || oj < 0 || oi >= o.Nx || oj >= o.Nz)
                {
                    continue;
                }

                float y = TerrainChunkEmitter.TopY(o, (int)oi, (int)oj);
                targets.Add(new TerrainBrush.HeightTarget((j * g.Nx) + i, (double)y - t.Origin.Y));
                used.Add(o.Uid);
                return;
            }
        }

        for (int i = 0; i < g.Nx; i++)
        {
            Match(i, 0);
            Match(i, g.Nz - 1);
        }

        for (int j = 1; j + 1 < g.Nz; j++)
        {
            Match(0, j);
            Match(g.Nx - 1, j);
        }

        if (targets.Count == 0)
        {
            return (0, 0);
        }

        AlpineTerrainGrid copy = g.Clone();
        var hg = new TerrainBrush.HeightGrid
        {
            Heights = copy.Heights,
            Nx = copy.Nx,
            Nz = copy.Nz,
            HeightMin = t.HeightMin,
            HeightRange = t.HeightRange,
            HeadroomFloor = t.Geoable ? (double)t.HeightMin - t.Thickness + AlpineTerrainRules.MinThickness : double.NegativeInfinity,
        };
        TerrainBrush.HeightWrite w = TerrainBrush.WriteHeights(hg, targets);
        if (w.Requantized)
        {
            t.Thickness = ThicknessAfterGrowth(t.Flags, t.Thickness, t.HeightMin, hg.HeightMin);
            t.HeightMin = hg.HeightMin;
            t.HeightRange = hg.HeightRange;
        }

        t.Grid = copy;
        return (targets.Count, used.Count);
    }

    /// <summary>
    /// The height mapping for offsets spanning [<paramref name="needLo"/>, <paramref name="needHi"/>]: unchanged when they
    /// fit, otherwise widened on the side(s) that need it by the need plus 25% headroom of the old range, the headroom
    /// below stopping at <paramref name="headroomFloor"/>; never narrower, within the limits (<c>grow_height_mapping</c>,
    /// alpine_terrain.h:392-453).
    /// </summary>
    public static (float HeightMin, float HeightRange, bool Clamped) GrowHeightMapping(
        float heightMin, float heightRange, float needLo, float needHi, double headroomFloor = double.NegativeInfinity)
    {
        if (!float.IsFinite(needLo) || !float.IsFinite(needHi))
        {
            return (heightMin, heightRange, false);
        }

        bool clamped = false;
        double oldLo = heightMin, oldHi = (double)heightMin + heightRange;
        double head = 0.25 * heightRange;
        double needL = oldLo, needH = oldHi, wantLo = oldLo, wantHi = oldHi;
        if (needLo < oldLo)
        {
            needL = needLo;
            wantLo = Math.Max(needL - head, Math.Min(needL, headroomFloor));
        }

        if (needHi > oldHi)
        {
            needH = needHi;
            wantHi = needH + head;
        }

        if (wantLo == oldLo && wantHi == oldHi)
        {
            return (heightMin, heightRange, false);
        }

        double floorLo = -AlpineTerrainRules.MaxCoord;
        wantLo = Math.Max(wantLo, Math.Min(floorLo, oldLo));
        if (needL < floorLo)
        {
            needL = Math.Min(floorLo, oldLo);
            clamped = true;
        }

        double cap = AlpineTerrainRules.MaxHeightRange;
        if (wantHi - wantLo > cap)
        {
            if (needH - needL <= cap)
            {
                double headLo = needL - wantLo, headHi = wantHi - needH;
                double keep = (cap - (needH - needL)) / (headLo + headHi);
                wantLo = needL - (headLo * keep);
                wantHi = needH + (headHi * keep);
            }
            else
            {
                clamped = true;
                double spare = Math.Max(cap - (oldHi - oldLo), 0.0);
                double growLo = oldLo - needL, growHi = needH - oldHi;
                wantLo = oldLo - (spare * growLo / (growLo + growHi));
                wantHi = oldHi + (spare * growHi / (growLo + growHi));
            }
        }

        float min = Math.Min((float)wantLo, heightMin);
        double top = Math.Max(wantHi, oldHi);
        float range = (float)Math.Min(top - min, cap);
        range = Math.Max(range, heightRange);
        return (min, range, clamped);
    }

    /// <summary>A geoable terrain's bottom stays put while a growth lowers Height Min (alpine_terrain.h:466-472).</summary>
    public static float ThicknessAfterGrowth(byte flags, float thickness, float oldHeightMin, float newHeightMin)
    {
        if ((flags & AlpineTerrainRules.FlagGeoable) == 0 || !(newHeightMin < oldHeightMin))
        {
            return thickness;
        }

        double v = thickness - ((double)oldHeightMin - newHeightMin);
        return (float)Math.Clamp(v, AlpineTerrainRules.MinThickness, AlpineTerrainRules.MaxThickness);
    }

    // ---- grid size -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Resamples the grid to <paramref name="nx"/> x <paramref name="nz"/> vertices (and optionally another weight
    /// resolution): heights filtered on the vertex lattice, weights over texel centres and renormalised, overlay and
    /// decoration coverage like the weights, holes and diagonals nearest by cell centre (<c>terrain_resample_grid</c>,
    /// terrain.cpp:234-319). The chunk geo mask is reset to every chunk.
    /// </summary>
    public static void Resize(AlpineTerrain t, int nx, int nz, int? weightResolution = null)
    {
        nx = Math.Clamp(nx, AlpineTerrainRules.MinVerts, AlpineTerrainRules.MaxVerts);
        nz = Math.Clamp(nz, AlpineTerrainRules.MinVerts, AlpineTerrainRules.MaxVerts);
        int mul = weightResolution ?? t.Grid.WeightResolution;
        if (!AlpineTerrainRules.IsAllowedWeightResolution(mul))
        {
            throw new ArgumentException("The weight resolution must be 1, 2 or 4.");
        }

        AlpineTerrainGrid src = t.Grid;
        if (nx == src.Nx && nz == src.Nz && mul == src.WeightResolution)
        {
            return;
        }

        var g = new AlpineTerrainGrid { Nx = nx, Nz = nz, WeightResolution = mul, Heights = new ushort[nx * nz] };
        Filter(MakeTaps(src.Nx, nx, true), MakeTaps(src.Nz, nz, true), src.Nx, 1,
            (c, r, _) => src.Heights[(r * src.Nx) + c],
            (i, j, v) => g.Heights[(j * nx) + i] = AlpineTerrainRules.EncodeHeight01(v[0] / 65535.0f));

        int sw = AlpineTerrainRules.WeightWidth(src.Nx, src.WeightResolution), sh = AlpineTerrainRules.WeightHeight(src.Nz, src.WeightResolution);
        int dw = AlpineTerrainRules.WeightWidth(nx, mul), dh = AlpineTerrainRules.WeightHeight(nz, mul);
        int srcMap = AlpineTerrainRules.WeightMapBytes(src.Nx, src.Nz, src.WeightResolution);
        int dstMap = AlpineTerrainRules.WeightMapBytes(nx, nz, mul);
        g.Weights = new byte[dstMap * 2];
        Taps tx = MakeTaps(sw, dw, false), tz = MakeTaps(sh, dh, false);
        Filter(tx, tz, sw, AlpineTerrainRules.MaxLayers,
            (c, r, ch) => src.Weights[(ch < 4 ? 0 : srcMap) + (((r * sw) + c) * 4) + (ch & 3)],
            (i, j, v) =>
            {
                var w = new byte[AlpineTerrainRules.MaxLayers];
                for (int c = 0; c < w.Length; c++)
                {
                    w[c] = WeightByte(v[c]);
                }

                NormalizeWeights(w);
                int texel = (j * dw) + i;
                for (int c = 0; c < 4; c++)
                {
                    g.Weights[(texel * 4) + c] = w[c];
                    g.Weights[dstMap + (texel * 4) + c] = w[4 + c];
                }
            });

        if (src.OverlayCoverage is { } overlay)
        {
            g.OverlayCoverage = new byte[dstMap];
            Filter(tx, tz, sw, 4, (c, r, ch) => overlay[(((r * sw) + c) * 4) + ch],
                (i, j, v) =>
                {
                    for (int c = 0; c < 4; c++)
                    {
                        g.OverlayCoverage[(((j * dw) + i) * 4) + c] = WeightByte(v[c]);
                    }
                });
        }

        int srcPlane = sw * sh, dstPlane = dw * dh;
        if (src.DecorationCoverage is { } planes && srcPlane > 0)
        {
            int count = planes.Length / srcPlane;
            g.DecorationCoverage = new byte[count * dstPlane];
            for (int p = 0; p < count; p++)
            {
                int pp = p;
                Filter(tx, tz, sw, 1, (c, r, _) => planes[(pp * srcPlane) + (r * sw) + c],
                    (i, j, v) => g.DecorationCoverage[(pp * dstPlane) + (j * dw) + i] = WeightByte(v[0]));
            }
        }

        int scx = src.CellsX, scz = src.CellsZ, dcx = g.CellsX, dcz = g.CellsZ;
        int mask = AlpineTerrainRules.BitmaskBytes(dcx, dcz);
        g.Holes = new byte[mask];
        g.Diagonals = new byte[mask];
        for (int z = 0; z < dcz; z++)
        {
            int sz = Math.Min((int)((z + 0.5f) * scz / dcz), scz - 1);
            for (int x = 0; x < dcx; x++)
            {
                int sx = Math.Min((int)((x + 0.5f) * scx / dcx), scx - 1);
                AlpineTerrainRules.SetBit(g.Holes, (z * dcx) + x, src.IsHole(sx, sz));
                AlpineTerrainRules.SetBit(g.Diagonals, (z * dcx) + x, src.Diagonal(sx, sz));
            }
        }

        t.Grid = g;
        t.ChunkCells = (byte)AlpineTerrainRules.EffectiveChunkCells(g.CellsX, g.CellsZ, t.ChunkCells, t.Flags & AlpineTerrainRules.FlagMask);
    }

    // ---- layers and cells ------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets the texture layers (1..8; the first is the base): existing layers keep their UV scale and flags by index, new
    /// ones take the given scale or the default 4 m. Weights of removed layers move onto layer 0 so every texel still sums
    /// to 255, and a decoration linked to a removed layer is unlinked (terrain.cpp:1193-1205).
    /// </summary>
    public static void SetLayers(AlpineTerrain t, IReadOnlyList<string> textures, IReadOnlyList<float>? uvScales = null)
    {
        if (textures.Count < 1 || textures.Count > AlpineTerrainRules.MaxLayers)
        {
            throw new ArgumentException($"A terrain has 1 to {AlpineTerrainRules.MaxLayers} layers.");
        }

        foreach (string name in textures)
        {
            if (!AlpineTerrainRules.TextureNameValid(name))
            {
                throw new ArgumentException($"'{name}' is too long for an RFL texture name (31 characters, a 14-character extension).");
            }
        }

        int oldCount = t.Layers.Count;
        for (int i = 0; i < textures.Count; i++)
        {
            if (i >= t.Layers.Count)
            {
                t.Layers.Add(new AlpineTerrainLayer());
            }

            t.Layers[i].Texture = textures[i];
            if (uvScales is not null && i < uvScales.Count)
            {
                t.Layers[i].UvScale = uvScales[i];
            }
        }

        if (t.Layers.Count > textures.Count)
        {
            t.Layers.RemoveRange(textures.Count, t.Layers.Count - textures.Count);
        }

        if (textures.Count < oldCount)
        {
            AlpineTerrainGrid g = t.Grid.Clone();
            int map = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, g.WeightResolution);
            int texels = map / 4;
            for (int texel = 0; texel < texels; texel++)
            {
                int moved = 0;
                for (int layer = textures.Count; layer < AlpineTerrainRules.MaxLayers; layer++)
                {
                    int at = (layer < 4 ? 0 : map) + (texel * 4) + (layer & 3);
                    moved += g.Weights[at];
                    g.Weights[at] = 0;
                }

                g.Weights[texel * 4] = (byte)Math.Min(255, g.Weights[texel * 4] + moved);
            }

            t.Grid = g;
        }
    }

    // ---- decorations and overlays (editor_patch/terrain.cpp:118-131, 1193-1260; alpine_terrain.h:108-157) ----------

    /// <summary>
    /// Adds a decoration (at the end) or replaces decoration <paramref name="index"/>'s settings, keeping its painted
    /// coverage. A new decoration's coverage starts full when it is linked to a layer, else empty (terrain.cpp:118-131).
    /// The values are clamped as Alpine RED clamps them.
    /// </summary>
    public static int SetDecoration(AlpineTerrain t, int index, AlpineTerrainDecoration d)
    {
        if (!AlpineTerrainRules.DecorationMeshValid(d.Mesh))
        {
            throw new ArgumentException($"'{d.Mesh}' is not a decoration mesh Alpine Faction loads (a .v3m / .v3c name of at most 31 characters).");
        }

        if (index < 0 || index > t.Decorations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"The terrain has decorations 1 to {t.Decorations.Count}.");
        }

        if (index == t.Decorations.Count)
        {
            if (t.Decorations.Count >= AlpineTerrainRules.MaxDecorations)
            {
                throw new InvalidOperationException($"A terrain holds at most {AlpineTerrainRules.MaxDecorations} decorations.");
            }

            t.Grid = t.Grid.Clone();
            t.Decorations.Add(d);
        }
        else
        {
            t.Decorations[index] = d;
        }

        AlpineTerrainEdit.Sanitize(t);
        return index;
    }

    /// <summary>Removes decoration <paramref name="index"/> and its coverage plane.</summary>
    public static void RemoveDecoration(AlpineTerrain t, int index)
    {
        if (index < 0 || index >= t.Decorations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"The terrain has decorations 1 to {t.Decorations.Count}.");
        }

        AlpineTerrainGrid g = t.Grid.Clone();
        int plane = AlpineTerrainRules.WeightTexelCount(g.Nx, g.Nz, g.WeightResolution);
        if (g.DecorationCoverage is { } planes && planes.Length >= t.Decorations.Count * plane)
        {
            var kept = new byte[(t.Decorations.Count - 1) * plane];
            Array.Copy(planes, 0, kept, 0, index * plane);
            Array.Copy(planes, (index + 1) * plane, kept, index * plane, kept.Length - (index * plane));
            g.DecorationCoverage = kept.Length > 0 ? kept : null;
        }

        t.Grid = g;
        t.Decorations.RemoveAt(index);
        AlpineTerrainEdit.Sanitize(t);
    }

    /// <summary>Sets decoration <paramref name="index"/>'s coverage everywhere (0 none .. 255 full).</summary>
    public static void FillDecorationCoverage(AlpineTerrain t, int index, byte value)
    {
        if (index < 0 || index >= t.Decorations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"The terrain has decorations 1 to {t.Decorations.Count}.");
        }

        AlpineTerrainGrid g = t.Grid.Clone();
        int plane = AlpineTerrainRules.WeightTexelCount(g.Nx, g.Nz, g.WeightResolution);
        Array.Fill(g.DecorationCoverage!, value, index * plane, plane);
        t.Grid = g;
    }

    /// <summary>
    /// Sets the overlays (0..4): texture, UV scale and flags (0x1 triplanar, 0x2 break tiling). Existing overlays keep their
    /// painted coverage by index; a new one starts uncovered; removed ones lose theirs.
    /// </summary>
    public static void SetOverlays(AlpineTerrain t, IReadOnlyList<AlpineTerrainLayer> overlays)
    {
        if (overlays.Count > AlpineTerrainRules.MaxOverlays)
        {
            throw new ArgumentException($"A terrain has at most {AlpineTerrainRules.MaxOverlays} overlays.");
        }

        foreach (AlpineTerrainLayer o in overlays)
        {
            if (!AlpineTerrainRules.TextureNameValid(o.Texture) || o.Texture.Length == 0)
            {
                throw new ArgumentException($"'{o.Texture}' is not a texture name an RFL can store.");
            }
        }

        AlpineTerrainGrid g = t.Grid.Clone();
        if (g.OverlayCoverage is { } cov)
        {
            for (int texel = 0; texel < cov.Length / 4; texel++)
            {
                for (int c = overlays.Count; c < 4; c++)
                {
                    cov[(texel * 4) + c] = 0;
                }
            }
        }

        t.Grid = g;
        t.Overlays.Clear();
        foreach (AlpineTerrainLayer o in overlays)
        {
            t.Overlays.Add(new AlpineTerrainLayer(o.Texture, o.UvScale, o.Flags));
        }

        AlpineTerrainEdit.Sanitize(t);
    }

    /// <summary>Paints the whole terrain with one layer (every weight texel 255 on <paramref name="layer"/>).</summary>
    public static void FillLayer(AlpineTerrain t, int layer)
    {
        if (layer < 0 || layer >= t.Layers.Count)
        {
            throw new ArgumentException($"The terrain has layers 1 to {t.Layers.Count}.");
        }

        AlpineTerrainGrid g = t.Grid.Clone();
        int map = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, g.WeightResolution);
        Array.Clear(g.Weights);
        for (int texel = 0; texel < map / 4; texel++)
        {
            g.Weights[(layer < 4 ? 0 : map) + (texel * 4) + (layer & 3)] = 255;
        }

        t.Grid = g;
    }

    /// <summary>Makes cell (x, z) a hole or solid ground.</summary>
    public static void SetHole(AlpineTerrain t, int x, int z, bool hole)
    {
        AlpineTerrainGrid g = t.Grid;
        if (x < 0 || z < 0 || x >= g.CellsX || z >= g.CellsZ)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Cell ({x}, {z}) is outside the {g.CellsX} x {g.CellsZ} cells.");
        }

        g = g.Clone();
        AlpineTerrainRules.SetBit(g.Holes, (z * g.CellsX) + x, hole);
        t.Grid = g;
    }

    /// <summary>Sets every cell's hole bit from <paramref name="hole"/>(cell x, cell z) in one grid edit; returns the hole count.</summary>
    public static int SetHoles(AlpineTerrain t, Func<int, int, bool> hole)
    {
        AlpineTerrainGrid g = t.Grid.Clone();
        int n = 0;
        for (int z = 0; z < g.CellsZ; z++)
        {
            for (int x = 0; x < g.CellsX; x++)
            {
                bool h = hole(x, z);
                AlpineTerrainRules.SetBit(g.Holes, (z * g.CellsX) + x, h);
                n += h ? 1 : 0;
            }
        }

        t.Grid = g;
        return n;
    }

    /// <summary>Sets cell (x, z)'s diagonal: false along (x, z)-(x+1, z+1), true along (x+1, z)-(x, z+1).</summary>
    public static void SetDiagonal(AlpineTerrain t, int x, int z, bool flipped)
    {
        AlpineTerrainGrid g = t.Grid;
        if (x < 0 || z < 0 || x >= g.CellsX || z >= g.CellsZ)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Cell ({x}, {z}) is outside the {g.CellsX} x {g.CellsZ} cells.");
        }

        g = g.Clone();
        AlpineTerrainRules.SetBit(g.Diagonals, (z * g.CellsX) + x, flipped);
        t.Grid = g;
    }

    /// <summary>Rescales a texel's 8 weights to sum exactly 255; an all-zero texel becomes layer 0 (alpine_terrain.h:638-657).</summary>
    public static void NormalizeWeights(byte[] w)
    {
        int sum = 0;
        foreach (byte v in w)
        {
            sum += v;
        }

        if (sum == 255)
        {
            return;
        }

        if (sum == 0)
        {
            w[0] = 255;
            return;
        }

        int total = 0, largest = 0;
        for (int i = 0; i < w.Length; i++)
        {
            w[i] = (byte)(((w[i] * 255) + (sum / 2)) / sum);
            total += w[i];
            if (w[i] > w[largest])
            {
                largest = i;
            }
        }

        w[largest] = (byte)Math.Clamp(w[largest] + (255 - total), 0, 255);
    }

    // ---- separable resampling (terrain.cpp:148-232) ----------------------------------------------------------------

    private sealed class Taps
    {
        public List<int> Start { get; } = new();

        public List<int> Index { get; } = new();

        public List<float> Weight { get; } = new();

        public int Count => Start.Count - 1;
    }

    /// <summary>Per destination sample, the source taps and weights summing to 1: bilinear upsampling, box downsampling.</summary>
    private static Taps MakeTaps(int sn, int dn, bool lattice)
    {
        var t = new Taps();
        double step = lattice ? (double)(sn - 1) / (dn - 1) : (double)sn / dn;
        double last = sn - 1;
        for (int i = 0; i < dn; i++)
        {
            int first = t.Index.Count;
            t.Start.Add(first);
            double c = lattice ? i * step : ((i + 0.5) * step) - 0.5;
            if (step > 1.0)
            {
                double lo = c - (step * 0.5), hi = c + (step * 0.5);
                int k0 = (int)Math.Clamp(Math.Floor(lo + 0.5), 0.0, last);
                int k1 = (int)Math.Clamp(Math.Floor(hi + 0.5), 0.0, last);
                for (int k = k0; k <= k1; k++)
                {
                    double overlap = Math.Min(hi, k + 0.5) - Math.Max(lo, k - 0.5);
                    if (overlap > 0.0)
                    {
                        t.Index.Add(k);
                        t.Weight.Add((float)overlap);
                    }
                }
            }
            else
            {
                double u = Math.Clamp(c, 0.0, last);
                int k0 = Math.Min((int)u, sn - 1);
                float f = (float)(u - k0);
                t.Index.Add(k0);
                t.Weight.Add(1f - f);
                if (f > 0f && k0 + 1 < sn)
                {
                    t.Index.Add(k0 + 1);
                    t.Weight.Add(f);
                }
            }

            float sum = 0f;
            for (int k = first; k < t.Weight.Count; k++)
            {
                sum += t.Weight[k];
            }

            for (int k = first; k < t.Weight.Count; k++)
            {
                t.Weight[k] = sum > 0f ? t.Weight[k] / sum : 0f;
            }
        }

        t.Start.Add(t.Index.Count);
        return t;
    }

    private static void Filter(Taps tx, Taps tz, int srcWidth, int channels, Func<int, int, int, float> src, Action<int, int, float[]> dst)
    {
        var row = new float[srcWidth * channels];
        for (int j = 0; j < tz.Count; j++)
        {
            Array.Clear(row);
            for (int a = tz.Start[j]; a < tz.Start[j + 1]; a++)
            {
                int sr = tz.Index[a];
                float wz = tz.Weight[a];
                int r = 0;
                for (int c = 0; c < srcWidth; c++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        row[r++] += wz * src(c, sr, ch);
                    }
                }
            }

            for (int i = 0; i < tx.Count; i++)
            {
                var o = new float[channels];
                for (int b = tx.Start[i]; b < tx.Start[i + 1]; b++)
                {
                    int s = tx.Index[b] * channels;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        o[ch] += tx.Weight[b] * row[s + ch];
                    }
                }

                dst(i, j, o);
            }
        }
    }

    private static byte WeightByte(float v) => (byte)Math.Clamp((long)MathF.Round(v, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>Samples an image onto the vertex lattice (image row 0 = the terrain's +Z edge; terrain.cpp:990-1002).</summary>
    public static void SetHeightsFromImage(AlpineTerrain t, ushort[] image, int width, int height)
    {
        AlpineTerrainGrid g = t.Grid.Clone();
        Filter(MakeTaps(width, g.Nx, true), MakeTaps(height, g.Nz, true), width, 1,
            (c, r, _) => image[(AlpineTerrainRules.ImageRow(r, height) * width) + c],
            (i, j, v) => g.Heights[(j * g.Nx) + i] = AlpineTerrainRules.EncodeHeight01(v[0] / 65535.0f));
        t.Grid = g;
    }
}
