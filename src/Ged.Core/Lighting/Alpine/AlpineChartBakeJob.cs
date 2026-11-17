using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>Receives the surfaces one <see cref="Lightmapper"/> pass just lit, with that pass's shading state.</summary>
internal interface IAlpineChartCapture
{
    void Capture(Lightmapper.ShadeContext ctx, IReadOnlyList<SurfaceBake> bakes, IReadOnlyList<Lightmap> pages, bool[] cookieCrisp, float[][]? indirect);
}

/// <summary>One mover's record in a bake's mover table (alpine_lightmap.h:207-225).</summary>
public sealed class AlpineMoverCharts
{
    public required int Uid { get; init; }

    /// <summary>Per mover surface, positionally (empty geometry = no chart).</summary>
    public required AlpineSurfaceChart[] Charts { get; init; }

    /// <summary>The stock fragment size each chart refines (written for every surface).</summary>
    public required (ushort W, ushort H)[] Fragments { get; init; }

    /// <summary>Offset of the record's <c>surface_hash</c> in the section body (stamped at save).</summary>
    public int HashOffset { get; internal set; }

    /// <summary>A token over the mover's surfaces at bake time (<see cref="AlpineLightmapBake.SurfacesSignature"/> with no pages).</summary>
    public uint Signature { get; internal set; }

    public int ChartCount => Charts.Count(c => !c.Geometry.IsEmpty);
}

/// <summary>
/// One Alpine chart bake: <see cref="Lightmapper"/> hands it the static surfaces it lit (and, during a build, each
/// mover's surfaces), with that pass's lights, occluders and ambient; <see cref="Finish"/> then lays out, shades and
/// encodes the atlas exactly as Alpine RED's <c>bake_begin_layout</c> / <c>alpine_lm_shade_surface</c> /
/// <c>alpine_lm_bake_end</c> do (editor_patch/alpine_lightmaps.cpp:2082-2306, 2331-2377, 2416-2517): the surface
/// charts at the level's density halved to fit the page budget, then the movers' charts around them at that density
/// (halved on their own until they fit, else dropped), one packing of all of them, every chart shaded on a tile view of
/// its surface, the stock fragments replaced by the box means of their charts, the layer encoded (BC7 by default,
/// raw RGB8 on request) and the section body written with its mover table. <see cref="Result"/> is null when nothing
/// qualifies (Alpine then writes no section).
/// </summary>
public sealed class AlpineChartBakeJob : IAlpineChartCapture
{
    private readonly List<Group> _movers = new();
    private Group? _static;
    private IReadOnlyList<Lightmap>? _staticPages;

    /// <param name="surfaces">The static solid's surfaces, in geometry order (the charts are positional over them).</param>
    /// <param name="settings">The level's Alpine lightmap settings.</param>
    /// <param name="geometry">The geometry the surfaces belong to, when it already exists (a compile assembles it after the bake).</param>
    public AlpineChartBakeJob(IReadOnlyList<Surface> surfaces, AlpineLightmapSettings settings, Geometry? geometry = null)
    {
        Surfaces = surfaces;
        Settings = settings;
        Geometry = geometry;
        Terrains = settings.Terrains;
    }

    public IReadOnlyList<Surface> Surfaces { get; }

    public Geometry? Geometry { get; }

    public AlpineLightmapSettings Settings { get; }

    /// <summary>
    /// Set by a build that lights movers after the static pass: the layout waits for <see cref="Finish"/> so the movers'
    /// charts pack with the surfaces' (otherwise the static capture finishes the bake at once).
    /// </summary>
    public bool DeferFinish { get; set; }

    /// <summary>True once <see cref="Finish"/> ran.</summary>
    public bool Finished { get; private set; }

    /// <summary>
    /// The terrains whose compiled geometry is current, in level order: each gets a terrain chart (alpine_lightmaps.cpp:
    /// 580-622). A build sets them from what it compiled; a relight from the level's built terrains.
    /// </summary>
    public IReadOnlyList<AlpineTerrain> Terrains { get; set; } = Array.Empty<AlpineTerrain>();

    /// <summary>The bake, or null when no section is written.</summary>
    public AlpineLightmapBake? Result { get; private set; }

    /// <summary>Why no section was written, when <see cref="Result"/> is null.</summary>
    public string? Skipped { get; private set; }

    private sealed class Group
    {
        public required Lightmapper.ShadeContext Ctx { get; init; }

        public required IReadOnlyList<Surface> Surfaces { get; init; }

        public required SurfaceBake?[] Bake { get; init; }

        public required Lightmap?[] Page { get; init; }

        public required bool[] Crisp { get; init; }

        public required float[]?[] Indirect { get; init; }

        public int Uid { get; init; }
    }

    void IAlpineChartCapture.Capture(
        Lightmapper.ShadeContext ctx, IReadOnlyList<SurfaceBake> bakes, IReadOnlyList<Lightmap> pages, bool[] cookieCrisp, float[][]? indirect)
    {
        _static = MakeGroup(Surfaces, ctx, bakes, pages, cookieCrisp, indirect, 0);
        _staticPages = pages;
        if (!DeferFinish)
        {
            Finish();
        }
    }

    /// <summary>The capture a mover's bake hands its surfaces to (null when the level allows no charts).</summary>
    internal IAlpineChartCapture? ForMover(int uid) => Settings.ChartsEnabled && !Finished ? new MoverCapture(this, uid) : null;

    private sealed class MoverCapture : IAlpineChartCapture
    {
        private readonly AlpineChartBakeJob _job;
        private readonly int _uid;

        public MoverCapture(AlpineChartBakeJob job, int uid)
        {
            _job = job;
            _uid = uid;
        }

        public void Capture(Lightmapper.ShadeContext ctx, IReadOnlyList<SurfaceBake> bakes, IReadOnlyList<Lightmap> pages, bool[] cookieCrisp, float[][]? indirect)
        {
            var surfaces = bakes.Select(b => b.Surface).ToArray();
            _job._movers.Add(MakeGroup(surfaces, ctx, bakes, pages, cookieCrisp, indirect, _uid));
        }
    }

    private static Group MakeGroup(
        IReadOnlyList<Surface> surfaces, Lightmapper.ShadeContext ctx, IReadOnlyList<SurfaceBake> bakes, IReadOnlyList<Lightmap> pages,
        bool[] crisp, float[][]? indirect, int uid)
    {
        int n = surfaces.Count;
        var index = new Dictionary<Surface, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < n; i++)
        {
            index[surfaces[i]] = i;
        }

        var g = new Group
        {
            Ctx = ctx,
            Surfaces = surfaces,
            Bake = new SurfaceBake?[n],
            Page = new Lightmap?[n],
            Crisp = new bool[n],
            Indirect = new float[]?[n],
            Uid = uid,
        };
        for (int b = 0; b < bakes.Count; b++)
        {
            if (index.TryGetValue(bakes[b].Surface, out int si))
            {
                g.Bake[si] = bakes[b];
                g.Page[si] = (uint)bakes[b].Surface.LightmapIndex < (uint)pages.Count ? pages[bakes[b].Surface.LightmapIndex] : null;
                g.Crisp[si] = b < crisp.Length && crisp[b];
                g.Indirect[si] = indirect is not null && b < indirect.Length ? indirect[b] : null;
            }
        }

        return g;
    }

    /// <summary>Lays out, shades and encodes the atlas (idempotent; a no-op once finished).</summary>
    public void Finish()
    {
        if (Finished)
        {
            return;
        }

        Finished = true;
        Result = null;
        var log = new List<string>();

        // Terrain charts are written with either pipeline: a Legacy level (or one with Alpine surface lightmaps off)
        // gets a terrain-only section (alpine_lightmaps.cpp:2116-2127, 2202-2206; lightmap.cpp:118-124).
        List<AlpineTerrainCharts.Entry> terrains = AlpineTerrainCharts.Collect(Terrains, log);
        bool surfacesAllowed = Settings.ChartsEnabled;
        string notAllowed = Settings.Pipeline == LightmapPipeline.Legacy
            ? "Legacy lighting is set: surfaces keep their stock lightmaps only."
            : "Alpine surface lightmaps are off: surfaces keep their stock lightmaps only.";
        if (!surfacesAllowed && terrains.Count == 0)
        {
            Skipped = notAllowed;
            return;
        }

        if (_static is not { } st)
        {
            Skipped = "Nothing was lit.";
            return;
        }

        if (!surfacesAllowed)
        {
            log.Add(notAllowed);
        }

        int stockPage = FirstPage(st);
        foreach (Group m in _movers)
        {
            if (stockPage == 0)
            {
                stockPage = FirstPage(m);
            }
        }

        bool stockValid = AlpineLightmapLayout.StockPageEdgeValid(stockPage);
        if (!stockValid)
        {
            string why = stockPage == 0
                ? "The level has no stock lightmap pages to refine."
                : $"The stock lightmap pages are {stockPage} texels wide: surfaces keep their stock lightmaps only.";
            if (terrains.Count == 0)
            {
                Skipped = why;
                return;
            }

            if (surfacesAllowed)
            {
                log.Add(why);
            }

            surfacesAllowed = false;
        }

        int budget = Settings.Codec == AlpineLightmapCodec.RawRgb8 ? AlpineLightmapLayout.StageRawPages : AlpineLightmapLayout.StagePageBudget;

        // 1. The surfaces' charts at the level's density, halved until they fit (alpine_lightmaps.cpp:2116-2154).
        int requested = Settings.EffectiveDensity;
        int density = requested;
        int halvings = 0;
        int n = st.Surfaces.Count;
        var surfCharts = new AlpineChartGeometry[n];
        var surfKu = new ushort[n];
        var surfKv = new ushort[n];
        bool hasSurface = false;
        while (n > 0 && surfacesAllowed)
        {
            AlpineChartGeometry[] c = AlpineChartBaker.DeriveCharts(st.Surfaces, i => st.Page[i], stockPage, density, out ushort[] ku, out ushort[] kv, i => st.Bake[i] is not null);
            AlpinePackResult res = AlpineAtlasPacker.Pack(c, budget, out _, out _, out _);
            if (res == AlpinePackResult.NoCharts)
            {
                log.Add("No surface qualifies for an Alpine chart.");
                break;
            }

            if (res == AlpinePackResult.Ok)
            {
                (surfCharts, surfKu, surfKv) = (c, ku, kv);
                hasSurface = true;
                break;
            }

            log.Add($"Density {density} needs more than the {budget} page budget.");
            if (density <= AlpineLightmapLayout.DensityMin)
            {
                log.Add("The level does not fit the page budget even at density 1: surfaces keep their stock lightmaps only.");
                break;
            }

            density = Math.Max(AlpineLightmapLayout.DensityMin, density / 2);
            halvings++;
        }

        // 2. The movers around them, never at the surfaces' expense (alpine_lightmaps.cpp:2156-2200, 643-704).
        List<Group> movers = surfacesAllowed ? CollectMovers(log) : new List<Group>();
        var moverCharts = new List<(Group Mover, AlpineChartGeometry[] Charts, ushort[] Ku, ushort[] Kv)>();
        int moverDensity = 0;
        if (movers.Count > 0)
        {
            int dm = hasSurface ? density : requested;
            while (true)
            {
                moverCharts.Clear();
                foreach (Group m in movers)
                {
                    AlpineChartGeometry[] c = AlpineChartBaker.DeriveCharts(m.Surfaces, i => m.Page[i], stockPage, dm, out ushort[] ku, out ushort[] kv, i => m.Bake[i] is not null);
                    if (c.Any(x => !x.IsEmpty))
                    {
                        moverCharts.Add((m, c, ku, kv));
                    }
                }

                if (moverCharts.Count == 0)
                {
                    log.Add("No mover surface qualifies for an Alpine chart.");
                    break;
                }

                AlpineChartGeometry[] fit = surfCharts.Concat(moverCharts.SelectMany(x => x.Charts)).ToArray();
                if (AlpineAtlasPacker.Pack(fit, budget, out _, out _, out _) == AlpinePackResult.Ok)
                {
                    moverDensity = dm;
                    break;
                }

                if (dm <= AlpineLightmapLayout.DensityMin)
                {
                    log.Add("The mover charts do not fit the page budget next to the surfaces: movers keep their stock lightmaps.");
                    moverCharts.Clear();
                    break;
                }

                dm = Math.Max(AlpineLightmapLayout.DensityMin, dm / 2);
            }
        }

        if (!hasSurface && moverCharts.Count == 0 && terrains.Count == 0)
        {
            Skipped = log.Count > 0 ? log[^1] : "No surface qualifies for an Alpine chart.";
            return;
        }

        // 3. One packing of everything, the reader's tile order: surfaces, then each mover's surfaces, then the terrains,
        // whose density halves until they fit around the brushwork (alpine_lightmaps.cpp:2208-2242).
        AlpineChartGeometry[] brushwork = surfCharts.Concat(moverCharts.SelectMany(x => x.Charts)).ToArray();
        AlpineChartGeometry[] all;
        int[] bases = Array.Empty<int>();
        AlpineTile[] tiles = Array.Empty<AlpineTile>();
        int numPages = 0;
        while (true)
        {
            long terrainTexels = terrains.Sum(e => (long)e.W * e.H);
            all = brushwork.Concat(terrains.Select(e => AlpineTerrainCharts.ChartGeometry(e.W, e.H))).ToArray();
            AlpinePackResult res = AlpinePackResult.OverBudget;
            if (terrainTexels <= AlpineTerrainCharts.MaxChartTexels)
            {
                res = AlpineAtlasPacker.Pack(all, budget, out bases, out tiles, out numPages);
            }

            if (res == AlpinePackResult.Ok)
            {
                break;
            }

            if (res == AlpinePackResult.NoCharts)
            {
                Skipped = "No chart qualifies.";
                return;
            }

            if (terrains.Any(e => e.Density > 1))
            {
                log.Add("The terrain charts do not fit the page budget next to the surface charts: halving terrain lightmap density.");
                foreach (AlpineTerrainCharts.Entry e in terrains)
                {
                    e.Density = Math.Max(1, e.Density / 2);
                }

                continue;
            }

            if (terrains.Count == 0)
            {
                Skipped = "The charts do not fit the page budget.";
                return;
            }

            log.Add("The terrain charts do not fit the page budget even at one texel per cell: terrain lighting is not baked.");
            terrains.Clear();
            if (!hasSurface && moverCharts.Count == 0)
            {
                Skipped = log[^1];
                return;
            }
        }

        if (!stockValid)
        {
            stockPage = Settings.HighRes ? 256 : 128; // stock_page_size(highres), alpine_lightmaps.cpp:2261-2262
        }

        var pageData = new byte[numPages][];
        for (int p = 0; p < numPages; p++)
        {
            pageData[p] = new byte[AlpineLightmapLayout.RawPageBytes];
        }

        var work = new List<(Group G, int Surface, int Chart, ushort Ku, ushort Kv)>();
        for (int i = 0; i < n; i++)
        {
            if (!surfCharts[i].IsEmpty)
            {
                work.Add((st, i, i, surfKu[i], surfKv[i]));
            }
        }

        int at = n;
        foreach ((Group m, AlpineChartGeometry[] c, ushort[] ku, ushort[] kv) in moverCharts)
        {
            for (int i = 0; i < c.Length; i++)
            {
                if (!c[i].IsEmpty)
                {
                    work.Add((m, i, at + i, ku[i], kv[i]));
                }
            }

            at += c.Length;
        }

        var shared = new bool[numPages * AlpineLightmapLayout.PageSize / 4 * AlpineLightmapLayout.PageSize / 4];
        long texels = work.Sum(w => (long)all[w.Chart].Cw * all[w.Chart].Ch);
        int done = 0;
        ParallelOptions po = st.Ctx.Parallel;
        st.Ctx.Options.Progress?.Invoke(new BakeProgress("Lighting Alpine charts", 0, work.Count));
        Parallel.ForEach(work.OrderByDescending(w => (long)all[w.Chart].Cw * all[w.Chart].Ch).ThenBy(w => w.Chart), po, w =>
        {
            SurfaceBake sb = w.G.Bake[w.Surface]!;
            Lightmap page = w.G.Page[w.Surface]!;
            AlpineChartGeometry geom = all[w.Chart];
            byte[] image = ShadeChart(w.G.Ctx, sb, page, geom, w.Ku, w.Kv, w.G.Crisp[w.Surface], w.G.Indirect[w.Surface]);
            StoreTiles(image, geom, bases[w.Chart], tiles, pageData, shared);
            if (!IsStockFragment(geom, w.Ku, w.Kv))
            {
                DownsampleIntoStock(image, sb.Surface, page, geom, w.Ku, w.Kv);
            }

            int d = System.Threading.Interlocked.Increment(ref done);
            if (d % 64 == 0 || d == work.Count)
            {
                st.Ctx.Options.Progress?.Invoke(new BakeProgress("Lighting Alpine charts", d, work.Count));
            }
        });

        // The terrain charts, after the movers (alpine_lightmaps.cpp:1247-1276, lightmap.cpp:4832-4837).
        int firstTerrain = brushwork.Length;
        var terrainRecords = new List<AlpineTerrainChartRecord>();
        for (int k = 0; k < terrains.Count; k++)
        {
            AlpineTerrainCharts.Entry e = terrains[k];
            st.Ctx.Options.Progress?.Invoke(new BakeProgress("Lighting terrain", k, terrains.Count));
            AlpineChartGeometry geom = all[firstTerrain + k];
            byte[] image = AlpineTerrainCharts.Shade(e, (p, nrm, lift, ts) => st.Ctx.ShadePoints(p, nrm, lift, ts));
            StoreTiles(image, geom, bases[firstTerrain + k], tiles, pageData, shared);
            terrainRecords.Add(e.Record);
            texels += (long)e.W * e.H;
            log.Add($"{Compiler.TerrainChunkEmitter.Label(e.Terrain)}: {e.W}x{e.H} texels, {e.Density} per cell.");
        }

        // 4. The layer (alpine_lightmaps.cpp:2453-2479) and the section body.
        st.Ctx.Options.Progress?.Invoke(new BakeProgress("Encoding Alpine lightmaps", 0, numPages));
        (byte[][] layer, int rdo) = AlpineLayerEncoder.Encode(pageData, Settings.Codec, Settings.Compression, shared, po);

        var surfaceCharts = new AlpineSurfaceChart[n];
        int tiled = 0;
        for (int i = 0; i < n; i++)
        {
            surfaceCharts[i] = surfCharts[i].IsEmpty ? default : new AlpineSurfaceChart(surfKu[i], surfKv[i], surfCharts[i], bases[i]);
            tiled += surfCharts[i].TileCount > 1 ? 1 : 0;
        }

        var moverRecords = new List<AlpineMoverCharts>();
        at = n;
        foreach ((Group m, AlpineChartGeometry[] c, ushort[] ku, ushort[] kv) in moverCharts)
        {
            var charts = new AlpineSurfaceChart[c.Length];
            var frags = new (ushort, ushort)[c.Length];
            for (int i = 0; i < c.Length; i++)
            {
                charts[i] = c[i].IsEmpty ? default : new AlpineSurfaceChart(ku[i], kv[i], c[i], bases[at + i]);
                frags[i] = (m.Surfaces[i].W, m.Surfaces[i].H);
                tiled += c[i].TileCount > 1 ? 1 : 0;
            }

            moverRecords.Add(new AlpineMoverCharts
            {
                Uid = m.Uid,
                Charts = charts,
                Fragments = frags,
                Signature = AlpineLightmapBake.SurfacesSignature(m.Surfaces, null),
            });
            at += c.Length;
        }

        for (int k = 0; k < terrains.Count; k++)
        {
            tiled += all[firstTerrain + k].TileCount > 1 ? 1 : 0;
        }

        byte[] body = AlpineLightmapWriter.BuildBody(
            surfaceCharts, moverRecords, terrainRecords, tiles, layer, Settings.Codec, (byte)density, AlpineLightmapLayout.StockPageLog2Of(stockPage));
        int charted = surfaceCharts.Count(c => !c.Geometry.IsEmpty);
        log.Add($"Density {density}: {n} surfaces, {charted} charts, {moverRecords.Count} movers ({moverRecords.Sum(m => m.ChartCount)} mover charts" +
                (moverRecords.Count > 0 ? $" at density {moverDensity}" : string.Empty) + $"), {terrainRecords.Count} terrain charts, {tiles.Length} tiles ({tiled} charts tiled), {numPages} pages.");
        log.Add($"Encoded {numPages} pages as {(Settings.Codec == AlpineLightmapCodec.Bc7 ? "BC7" : "raw RGB8")}" +
                (Settings.Codec == AlpineLightmapCodec.Bc7 ? $", compression {Settings.Compression.ToString().ToLowerInvariant()}" + (rdo > 0 ? $" ({rdo} blocks reduced)" : string.Empty) : string.Empty) +
                $": section {body.Length} bytes.");
        var bake = new AlpineLightmapBake
        {
            Geometry = Geometry,
            Signature = AlpineLightmapBake.SurfacesSignature(st.Surfaces, _staticPages),
            Body = body,
            Charts = surfaceCharts,
            Tiles = tiles,
            Pages = pageData,
            StockPage = stockPage,
            Density = density,
            RequestedDensity = requested,
            DensityHalvings = halvings,
            ChartedSurfaces = charted,
            TiledCharts = tiled,
            ChartTexels = texels,
            Movers = moverRecords,
            MoverDensity = moverDensity,
            Terrains = terrainRecords,
            FirstTerrainTile = terrainRecords.Count > 0 ? bases[firstTerrain] : tiles.Length,
            Codec = Settings.Codec,
            Compression = Settings.Compression,
            RdoModifiedBlocks = rdo,
        };
        bake.Log.AddRange(log);
        Result = bake;
    }

    /// <summary>The movers that may get charts: one per uid, none for a uid two movers share (alpine_lightmaps.cpp:645-684).</summary>
    private List<Group> CollectMovers(List<string> log)
    {
        var byUid = _movers.GroupBy(m => m.Uid).ToList();
        var outp = new List<Group>();
        foreach (var g in byUid)
        {
            if (g.Count() > 1)
            {
                log.Add($"Two mover brushes share uid {g.Key}: neither gets Alpine lighting.");
                continue;
            }

            Group m = g.First();
            if (m.Surfaces.Count == 0)
            {
                continue;
            }

            if (m.Surfaces.Count > MaxMoverSurfaces)
            {
                log.Add($"Mover {m.Uid} has more lightmap surfaces than a mover chart can hold: it keeps its stock lightmaps.");
                continue;
            }

            outp.Add(m);
        }

        if (outp.Count > MaxMoverCharts)
        {
            log.Add($"Only the first {MaxMoverCharts} movers get Alpine lighting.");
            outp.RemoveRange(MaxMoverCharts, outp.Count - MaxMoverCharts);
        }

        return outp;
    }

    /// <summary>alpine_lightmap.h:272-273.</summary>
    public const int MaxMoverCharts = 8192;

    public const int MaxMoverSurfaces = 32767;

    private static int FirstPage(Group g)
    {
        foreach (Lightmap? p in g.Page)
        {
            if (p is { Width: > 0 })
            {
                return p.Width;
            }
        }

        return 0;
    }

    /// <summary>At k = 1 a single-tile chart is the stock fragment rect to the texel (alpine_lightmaps.cpp:837-842).</summary>
    private static bool IsStockFragment(in AlpineChartGeometry g, int kU, int kV) => kU == 1 && kV == 1 && g.Nx == 1 && g.Ny == 1;

    /// <summary>
    /// The chart's lit interior as RGB8, <c>cw × ch</c>. The view covers the chart plus a one-texel ring — the region a
    /// stock fragment of the same refinement would be — so the stock texel conversion (and its interior filter) sees
    /// the same neighbourhood at k = 1. A k = 1 single-tile chart takes the stock fragment the stock pass just lit
    /// instead of lighting it again (alpine_lightmaps.cpp:844-867, 2358-2362).
    /// </summary>
    private static byte[] ShadeChart(
        Lightmapper.ShadeContext ctx, SurfaceBake sb, Lightmap page, in AlpineChartGeometry g, int kU, int kV,
        bool cookieCrisp, float[]? indirect)
    {
        Surface s = sb.Surface;
        int cw = g.Cw, ch = g.Ch;
        var image = new byte[cw * ch * 3];
        if (IsStockFragment(g, kU, kV))
        {
            for (int y = 0; y < ch; y++)
            {
                Buffer.BlockCopy(page.Pixels, (((s.Y + 1 + y) * page.Width) + s.X + 1) * 3, image, y * cw * 3, cw * 3);
            }

            return image;
        }

        int vw = cw + 2, vh = ch + 2;
        if (sb.FullBright)
        {
            Array.Fill(image, (byte)128);
            return image;
        }

        // View texel (0, 0) is chart texel (−1, −1), at k·(fragment start + 1) − 1 in the page scaled by k
        // (alpine_lightmap.h:581-591 stock_view_coord).
        long originX = ((long)kU * (s.X + 1)) - 1;
        long originY = ((long)kV * (s.Y + 1)) - 1;
        var mapper = new SurfaceTexelMapper(s, originX, originY, (long)kU * page.Width, (long)kV * page.Height);
        List<int> affecting = ctx.Affecting(sb);
        float[] buf = ctx.Shade(sb, mapper, vw, vh, affecting);

        // The stock surface's indirect (bounce) term, carried onto the chart texels it covers.
        if (indirect is not null && indirect.Length == s.W * s.H * 3)
        {
            for (int y = 0; y < vh; y++)
            {
                int sy = Math.Clamp(FloorDiv(y - 1, kV) + 1, 0, s.H - 1);
                for (int x = 0; x < vw; x++)
                {
                    int sx = Math.Clamp(FloorDiv(x - 1, kU) + 1, 0, s.W - 1);
                    int o = ((y * vw) + x) * 3, io = ((sy * s.W) + sx) * 3;
                    buf[o] += indirect[io];
                    buf[o + 1] += indirect[io + 1];
                    buf[o + 2] += indirect[io + 2];
                }
            }
        }

        byte[] view = Lightmapper.EncodeBuffer(buf, vw, vh, ctx.Options, cookieCrisp);
        for (int y = 0; y < ch; y++)
        {
            Buffer.BlockCopy(view, (((y + 1) * vw) + 1) * 3, image, y * cw * 3, cw * 3);
        }

        return image;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -(((-a) + b - 1) / b);

    /// <summary>
    /// Writes the chart into every texel of every tile that stores it. A texel past the chart takes the nearest chart
    /// texel (alpine_lightmaps.cpp:1062-1107 <c>replicate_chart_edges</c>) and a texel several tiles store takes its
    /// owner's value (:1109-1157 <c>reconcile_gutters</c>); with the chart lit in one piece both come to the chart
    /// texel at the clamped coordinate. The BC7 blocks holding a texel another tile also stores are marked in
    /// <paramref name="shared"/>, as Alpine RED marks them, so the rate-distortion pass leaves them as encoded.
    /// </summary>
    internal static void StoreTiles(byte[] image, in AlpineChartGeometry g, int tileBase, AlpineTile[] tiles, byte[][] pages, bool[]? shared = null)
    {
        const int p = AlpineLightmapLayout.PageSize;
        const int bw = p / 4;
        for (int ty = 0; ty < g.Ny; ty++)
        {
            for (int tx = 0; tx < g.Nx; tx++)
            {
                int self = AlpineLightmapLayout.TileIndex(tileBase, g, tx, ty);
                AlpineTile t = tiles[self];
                AlpineTileDims td = AlpineLightmapLayout.TileDims(g, tx, ty);
                (int ou, int ov) = AlpineLightmapLayout.TileOrigin(g, tx, ty);
                byte[] page = pages[t.Page];
                for (int sy = 0; sy < td.StoredH && t.Y + sy < p; sy++)
                {
                    int cv = Math.Clamp(ov + sy, 0, g.Ch - 1);
                    for (int sx = 0; sx < td.StoredW && t.X + sx < p; sx++)
                    {
                        int cu = Math.Clamp(ou + sx, 0, g.Cw - 1);
                        int src = ((cv * g.Cw) + cu) * 3;
                        int dst = (((t.Y + sy) * p) + t.X + sx) * 3;
                        page[dst] = image[src];
                        page[dst + 1] = image[src + 1];
                        page[dst + 2] = image[src + 2];
                        if (shared is not null && g.TileCount > 1)
                        {
                            (int owner, int osx, int osy, bool valid) = AlpineLightmapLayout.TileSlot(g, tileBase, ou + sx, ov + sy);
                            if (valid && owner != self && (uint)owner < (uint)tiles.Length)
                            {
                                AlpineTile ot = tiles[owner];
                                shared[(((t.Page * bw) + ((t.Y + sy) / 4)) * bw) + ((t.X + sx) / 4)] = true;
                                shared[(((ot.Page * bw) + ((ot.Y + osy) / 4)) * bw) + ((ot.X + osx) / 4)] = true;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every stock interior texel becomes the rounded mean of the k_u × k_v chart texels covering it
    /// (alpine_lightmaps.cpp:797-835 <c>downsample_into_stock</c>); the fragment's border ring is left as the stock
    /// pass lit it.
    /// </summary>
    private static void DownsampleIntoStock(byte[] image, Surface s, Lightmap page, in AlpineChartGeometry g, int kU, int kV)
    {
        int n = kU * kV;
        for (int j = 0; j + 2 < s.H; j++)
        {
            int py = s.Y + 1 + j;
            if (py >= page.Height)
            {
                break;
            }

            for (int i = 0; i + 2 < s.W; i++)
            {
                int px = s.X + 1 + i;
                if (px >= page.Width)
                {
                    break;
                }

                int r = 0, gg = 0, b = 0;
                for (int dv = 0; dv < kV; dv++)
                {
                    int row = ((j * kV) + dv) * g.Cw;
                    for (int du = 0; du < kU; du++)
                    {
                        int o = (row + (i * kU) + du) * 3;
                        r += image[o];
                        gg += image[o + 1];
                        b += image[o + 2];
                    }
                }

                int dst = ((py * page.Width) + px) * 3;
                page.Pixels[dst] = (byte)((r + (n / 2)) / n);
                page.Pixels[dst + 1] = (byte)((gg + (n / 2)) / n);
                page.Pixels[dst + 2] = (byte)((b + (n / 2)) / n);
            }
        }
    }
}
