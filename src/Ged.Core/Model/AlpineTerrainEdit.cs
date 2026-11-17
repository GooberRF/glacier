namespace Ged.Core.Model;

/// <summary>
/// Keeps a terrain within what Alpine Faction's readers accept, the way Alpine RED does before every save and every
/// build (<c>terrain_clamp_properties</c>, editor_patch/terrain.cpp:385-431, and the chunk edge the writer stores,
/// terrain_build.cpp:1031-1032). On a record Alpine already accepted this changes nothing.
/// </summary>
public static class AlpineTerrainEdit
{
    /// <summary>A new terrain as Alpine RED places it: 129 x 129 flat vertices, one default layer (terrain.cpp:3089-3118).</summary>
    public static AlpineTerrain CreateDefault(int uid, Vec3 origin) => Create(uid, origin,
        AlpineTerrainRules.DefaultVerts, AlpineTerrainRules.DefaultVerts, AlpineTerrainRules.DefaultCellSize);

    /// <summary>A flat terrain of <paramref name="nx"/> x <paramref name="nz"/> vertices with one default layer.</summary>
    public static AlpineTerrain Create(int uid, Vec3 origin, int nx, int nz, float cellSize)
    {
        var t = new AlpineTerrain
        {
            Uid = uid,
            Origin = origin,
            ScriptName = "Terrain",
            CellSize = cellSize,
            Grid = AlpineTerrainGrid.Flat(
                Math.Clamp(nx, AlpineTerrainRules.MinVerts, AlpineTerrainRules.MaxVerts),
                Math.Clamp(nz, AlpineTerrainRules.MinVerts, AlpineTerrainRules.MaxVerts),
                AlpineTerrainRules.DefaultWeightResolution),
        };
        t.Layers.Add(new AlpineTerrainLayer());
        Sanitize(t);
        return t;
    }

    /// <summary>
    /// The terrain's editor position: the centre of its footprint at the origin's height. Alpine RED centres a new
    /// terrain on its placement point (terrain.cpp:3089-3118), so the object position is that centre; the record itself
    /// stores the min-X / min-Z corner (<see cref="AlpineTerrain.Origin"/>).
    /// </summary>
    public static Vec3 FootprintCentre(AlpineTerrain t) => new(
        t.Origin.X + (AlpineTerrainRules.Extent(t.Nx, t.CellSize) * 0.5f), t.Origin.Y,
        t.Origin.Z + (AlpineTerrainRules.Extent(t.Nz, t.CellSize) * 0.5f));

    /// <summary>The origin that puts the footprint centre of <paramref name="t"/> at <paramref name="centre"/>.</summary>
    public static Vec3 OriginForCentre(AlpineTerrain t, Vec3 centre) => new(
        centre.X - (AlpineTerrainRules.Extent(t.Nx, t.CellSize) * 0.5f), centre.Y,
        centre.Z - (AlpineTerrainRules.Extent(t.Nz, t.CellSize) * 0.5f));

    /// <summary>Clamps every field into the ranges the readers accept and matches the optional arrays to the lists.</summary>
    public static void Sanitize(AlpineTerrain t)
    {
        float max = AlpineTerrainRules.MaxCoord;
        Vec3 o = t.Origin;
        t.Origin = new Vec3(Coord(o.X, max), Coord(o.Y, max), Coord(o.Z, max));
        t.CellSize = AlpineTerrainRules.ClampFinite(t.CellSize, AlpineTerrainRules.MinCellSize, AlpineTerrainRules.MaxCellSize, AlpineTerrainRules.DefaultCellSize);
        t.HeightMin = AlpineTerrainRules.ClampFinite(t.HeightMin, -max, max, 0f);
        t.HeightRange = AlpineTerrainRules.ClampFinite(t.HeightRange, AlpineTerrainRules.MinHeightRange, AlpineTerrainRules.MaxHeightRange, AlpineTerrainRules.DefaultHeightRange);
        t.LightmapDensity = Math.Clamp(t.LightmapDensity, AlpineTerrainRules.LightmapDensityMin, AlpineTerrainRules.LightmapDensityMax);
        t.Flags &= AlpineTerrainRules.FlagMask | AlpineTerrainRules.FlagFullbright;
        t.Thickness = AlpineTerrainRules.ClampFinite(t.Thickness, AlpineTerrainRules.MinThickness, AlpineTerrainRules.MaxThickness, AlpineTerrainRules.DefaultThickness);
        t.SkirtDepth = AlpineTerrainRules.ClampFinite(t.SkirtDepth, 0f, AlpineTerrainRules.MaxSkirtDepth, AlpineTerrainRules.DefaultSkirtDepth);
        t.UndersideTexture = Texture(t.UndersideTexture, AlpineTerrainRules.DefaultLayerTexture);
        t.CraterTexture = Texture(t.CraterTexture, string.Empty);

        if (t.Layers.Count == 0)
        {
            t.Layers.Add(new AlpineTerrainLayer());
        }

        Trim(t.Layers, AlpineTerrainRules.MaxLayers);
        foreach (AlpineTerrainLayer layer in t.Layers)
        {
            layer.Texture = Texture(layer.Texture, AlpineTerrainRules.DefaultLayerTexture);
            layer.UvScale = Uv(layer.UvScale);
            layer.Flags &= AlpineTerrainRules.LayerFlagTriplanar;
        }

        Trim(t.Overlays, AlpineTerrainRules.MaxOverlays);
        foreach (AlpineTerrainLayer overlay in t.Overlays)
        {
            overlay.Texture = Texture(overlay.Texture, string.Empty);
            overlay.UvScale = Uv(overlay.UvScale);
            overlay.Flags &= AlpineTerrainRules.OverlayFlagMask;
        }

        Trim(t.Decorations, AlpineTerrainRules.MaxDecorations);
        foreach (AlpineTerrainDecoration d in t.Decorations)
        {
            if (!AlpineTerrainRules.DecorationMeshValid(d.Mesh))
            {
                d.Mesh = string.Empty;
            }

            d.Density = AlpineTerrainRules.ClampFinite(d.Density, 0f, AlpineTerrainRules.MaxDecorationDensity, 0.5f);
            d.ScaleMin = AlpineTerrainRules.ClampFinite(d.ScaleMin, AlpineTerrainRules.MinDecorationScale, AlpineTerrainRules.MaxDecorationScale, 1f);
            d.ScaleMax = AlpineTerrainRules.ClampFinite(d.ScaleMax, AlpineTerrainRules.MinDecorationScale, AlpineTerrainRules.MaxDecorationScale, 1f);
            d.ScaleMax = Math.Max(d.ScaleMax, d.ScaleMin);
            d.MaxSlopeDegrees = AlpineTerrainRules.ClampFinite(d.MaxSlopeDegrees, 0f, AlpineTerrainRules.MaxDecorationSlope, 35f);
            d.DrawDistance = AlpineTerrainRules.ClampFinite(d.DrawDistance, AlpineTerrainRules.MinDecorationDrawDistance, AlpineTerrainRules.MaxDecorationDrawDistance, 80f);
            d.VerticalOffset = AlpineTerrainRules.ClampFinite(d.VerticalOffset, -AlpineTerrainRules.MaxDecorationOffset, AlpineTerrainRules.MaxDecorationOffset, 0f);
            if (d.LinkLayer != AlpineTerrainRules.DecorationLinkNone && d.LinkLayer >= t.Layers.Count)
            {
                d.LinkLayer = AlpineTerrainRules.DecorationLinkNone;
            }

            d.Flags &= AlpineTerrainRules.DecorationFlagMask;
            if (((d.Flags & 0x30) >> 4) >= 3)
            {
                d.Flags &= 0x0F;
            }
        }

        if (!GridConsistent(t.Grid))
        {
            t.Grid = AlpineTerrainGrid.Flat(AlpineTerrainRules.DefaultVerts, AlpineTerrainRules.DefaultVerts, AlpineTerrainRules.DefaultWeightResolution);
        }

        AlpineTerrainGrid g = t.Grid;
        if (!AlpineTerrainRules.IsAllowedChunkCells(t.ChunkCells))
        {
            t.ChunkCells = AlpineTerrainRules.DefaultChunkCells;
        }

        t.ChunkCells = (byte)AlpineTerrainRules.EffectiveChunkCells(g.CellsX, g.CellsZ, t.ChunkCells, t.Flags & AlpineTerrainRules.FlagMask);

        int chunkMask = AlpineTerrainRules.ChunkMaskBytes(AlpineTerrainRules.ChunkCount(g.CellsX, g.CellsZ, t.ChunkCells));
        if (g.GeoMask is not null && g.GeoMask.Length != chunkMask)
        {
            g.GeoMask = null;
        }

        int map = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, g.WeightResolution);
        if (t.Overlays.Count == 0)
        {
            g.OverlayCoverage = null;
        }
        else if (g.OverlayCoverage is null || g.OverlayCoverage.Length != map)
        {
            g.OverlayCoverage = new byte[map];
        }

        MatchDecorationPlanes(t);
    }

    /// <summary>One coverage plane per decoration; a new one starts full when linked to a layer, else empty (terrain.cpp:118-131).</summary>
    private static void MatchDecorationPlanes(AlpineTerrain t)
    {
        AlpineTerrainGrid g = t.Grid;
        int plane = AlpineTerrainRules.WeightTexelCount(g.Nx, g.Nz, g.WeightResolution);
        int want = plane * t.Decorations.Count;
        if (t.Decorations.Count == 0)
        {
            g.DecorationCoverage = null;
            return;
        }

        if (g.DecorationCoverage is { } have && have.Length == want)
        {
            return;
        }

        var planes = new byte[want];
        int keep = Math.Min(g.DecorationCoverage?.Length / plane ?? 0, t.Decorations.Count);
        if (g.DecorationCoverage is not null)
        {
            Array.Copy(g.DecorationCoverage, planes, keep * plane);
        }

        for (int i = keep; i < t.Decorations.Count; i++)
        {
            if (t.Decorations[i].LinkLayer != AlpineTerrainRules.DecorationLinkNone)
            {
                Array.Fill(planes, (byte)255, i * plane, plane);
            }
        }

        g.DecorationCoverage = planes;
    }

    /// <summary>The grid's arrays are sized for its vertex counts and weight resolution (terrain.cpp:87-100).</summary>
    public static bool GridConsistent(AlpineTerrainGrid g)
    {
        if (g.Nx < AlpineTerrainRules.MinVerts || g.Nx > AlpineTerrainRules.MaxVerts || g.Nz < AlpineTerrainRules.MinVerts
            || g.Nz > AlpineTerrainRules.MaxVerts || !AlpineTerrainRules.IsAllowedWeightResolution(g.WeightResolution))
        {
            return false;
        }

        int mask = AlpineTerrainRules.BitmaskBytes(g.CellsX, g.CellsZ);
        return g.Heights.Length == g.Nx * g.Nz
            && g.Weights.Length == AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, g.WeightResolution) * 2
            && g.Holes.Length == mask && g.Diagonals.Length == mask;
    }

    private static float Coord(float v, float max) => float.IsFinite(v) ? Math.Clamp(v, -max, max) : 0f;

    private static float Uv(float v) =>
        AlpineTerrainRules.ClampFinite(v, AlpineTerrainRules.MinUvScale, AlpineTerrainRules.MaxUvScale, AlpineTerrainRules.DefaultUvScale);

    private static string Texture(string? name, string fallback) =>
        name is not null && AlpineTerrainRules.TextureNameValid(name) ? name : fallback;

    private static void Trim<T>(List<T> list, int max)
    {
        if (list.Count > max)
        {
            list.RemoveRange(max, list.Count - max);
        }
    }
}
