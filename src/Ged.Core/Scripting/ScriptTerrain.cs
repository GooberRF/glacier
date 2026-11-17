using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>A height callback for <c>terrain:set_heights(fn)</c>: (x, z, world_x, world_z) → metres above the terrain origin.</summary>
public delegate double ScriptTerrainHeight(double x, double z, double worldX, double worldZ);

/// <summary>A hole callback for <c>terrain:set_holes(fn)</c>: (cell x, cell z, world x, world z of the cell centre) → true for a hole.</summary>
public delegate bool ScriptTerrainHole(double x, double z, double worldX, double worldZ);

/// <summary>
/// Lua <c>level.terrain</c>: the level's Alpine Faction terrains (heightfields that Build Geometry compiles into detail
/// rooms, one per chunk, and Alpine Faction draws with its terrain shader on Direct3D 11).
/// </summary>
[ScriptDoc("The level's Alpine terrains: create one, list or look them up. A terrain compiles (Build Geometry) into detail rooms, one per chunk; it does not seal the level, so it needs air around it.", Since = 2)]
public sealed class ScriptTerrains
{
    private readonly ScriptContext _ctx;

    internal ScriptTerrains(ScriptContext ctx) => _ctx = ctx;

    [ScriptDoc("Creates a flat terrain centred on 'center' and returns its terrain handle. A level holds at most 64.",
        Example = "local t = level.terrain.create{center = {0, 0, 0}, nx = 65, nz = 65, cell_size = 2, layers = {\"grass1.tga\", \"rock02.tga\"}}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.TerrainCreate))]
    public ScriptTerrainHandle Create(ScriptArgs? args)
    {
        const string Api = "level.terrain.create";
        args ??= new ScriptArgs();
        args.RejectUnknown(ScriptOptionSets.TerrainCreate);
        Vec3 center = args.Vec(Api, "center", Vec3.Zero);
        int nx = Verts(Api, args, "nx"), nz = Verts(Api, args, "nz");
        float cell = (float)args.Number(Api, "cell_size", AlpineTerrainRules.DefaultCellSize);
        if (!(cell >= AlpineTerrainRules.MinCellSize && cell <= AlpineTerrainRules.MaxCellSize))
        {
            throw new ScriptApiException($"{Api}: 'cell_size' must be {AlpineTerrainRules.MinCellSize} to {AlpineTerrainRules.MaxCellSize} (got {ScriptArgs.Format(cell)}).");
        }

        LevelObject obj;
        try
        {
            obj = TerrainOps.Create(_ctx.Document, center, nx, nz, cell, args.String(Api, "name"));
        }
        catch (InvalidOperationException ex)
        {
            throw new ScriptApiException($"{Api}: {ex.Message}");
        }

        _ctx.NoteCreated(obj.Uid);
        var handle = new ScriptTerrainHandle(_ctx, (AlpineTerrain)obj.Model);
        handle.ApplyShape(Api, args);
        return handle;
    }

    [ScriptDoc("Every terrain of the level, in file order.", Example = "for _, t in ipairs(level.terrain.all()) do log.info(t.name) end", Since = 2)]
    public ScriptTerrainHandle[] All() => Section()?.Terrains.Select(t => new ScriptTerrainHandle(_ctx, t)).ToArray() ?? Array.Empty<ScriptTerrainHandle>();

    [ScriptDoc("The terrain with this UID or script name, or nil.", Example = "local t = level.terrain.get(\"hills\")", Since = 2)]
    public ScriptTerrainHandle? Get(object key)
    {
        AlpineTerrain? t = key switch
        {
            double d => Section()?.Terrains.FirstOrDefault(x => x.Uid == (int)d),
            string s => Section()?.Terrains.FirstOrDefault(x => string.Equals(x.ScriptName, s, StringComparison.OrdinalIgnoreCase)),
            ScriptObjectHandle o => o.Object.Model as AlpineTerrain,
            _ => null,
        };
        return t is null ? null : new ScriptTerrainHandle(_ctx, t);
    }

    [ScriptDoc("How many terrains the level has.", Since = 2)]
    public int Count => Section()?.Terrains.Count ?? 0;

    private AlpineTerrainSection? Section()
    {
        _ctx.Document.Rfl.ParseAllKnownSections();
        return _ctx.Document.Rfl.Sections.Select(s => s.Content).OfType<AlpineTerrainSection>().FirstOrDefault();
    }

    private static int Verts(string api, ScriptArgs args, string key)
    {
        int v = args.Int(api, key, AlpineTerrainRules.DefaultVerts);
        if (v < AlpineTerrainRules.MinVerts || v > AlpineTerrainRules.MaxVerts)
        {
            throw new ScriptApiException($"{api}: '{key}' must be {AlpineTerrainRules.MinVerts} to {AlpineTerrainRules.MaxVerts} vertices (got {v}).",
                "A terrain of n vertices has n - 1 cells along that axis; 129 (Alpine RED's default) or 257 at most.");
        }

        return v;
    }
}

/// <summary>
/// Lua <c>terrain</c>: one Alpine terrain. Grid indices x (0..nx-1) and z (0..nz-1) are vertex indices from the origin
/// corner (the terrain's min-X / min-Z corner); cells are (0..nx-2, 0..nz-2). Heights are metres above the origin's Y.
/// Every edit is one undo step of the run; rebuild (ops.build) after editing so the game sees the change.
/// </summary>
[ScriptDoc("An Alpine terrain handle: shape, heights (set, query, generate, heightmap import / export), layers, holes, and Convert to Brushes. Edits chain (setters return the handle).", Since = 2)]
public sealed class ScriptTerrainHandle
{
    private readonly ScriptContext _ctx;
    private readonly AlpineTerrain _t;

    internal ScriptTerrainHandle(ScriptContext ctx, AlpineTerrain terrain)
    {
        _ctx = ctx;
        _t = terrain;
    }

    [ScriptDoc("The terrain's UID.", Since = 2)]
    public int Uid => _t.Uid;

    [ScriptDoc("The script name.", Since = 2)]
    public string Name => _t.ScriptName;

    [ScriptDoc("The origin: the min-X / min-Z grid corner (terrains are never rotated).", Since = 2)]
    public Vec3 Origin => _t.Origin;

    [ScriptDoc("Vertices along X.", Since = 2)]
    public int Nx => _t.Nx;

    [ScriptDoc("Vertices along Z.", Since = 2)]
    public int Nz => _t.Nz;

    [ScriptDoc("Metres between vertices.", Since = 2)]
    public double CellSize => _t.CellSize;

    [ScriptDoc("World size along X, metres ((nx - 1) * cell_size).", Since = 2)]
    public double SizeX => AlpineTerrainRules.Extent(_t.Nx, _t.CellSize);

    [ScriptDoc("World size along Z, metres.", Since = 2)]
    public double SizeZ => AlpineTerrainRules.Extent(_t.Nz, _t.CellSize);

    [ScriptDoc("Metres above the origin that the lowest height sample maps to.", Since = 2)]
    public double HeightMin => _t.HeightMin;

    [ScriptDoc("Metres the 0..65535 height samples span.", Since = 2)]
    public double HeightRange => _t.HeightRange;

    [ScriptDoc("The chunk edge a build uses, in cells (each chunk compiles to its own detail room).", Since = 2)]
    public int ChunkCells => TerrainChunkEmitter.EffectiveChunkCells(_t);

    [ScriptDoc("How many chunks (detail rooms) a build makes.", Since = 2)]
    public int ChunkCount => TerrainChunkEmitter.ChunkCount(_t);

    [ScriptDoc("The layer texture names, base first.", Since = 2)]
    public string[] Layers => _t.Layers.Select(l => l.Texture).ToArray();

    [ScriptDoc("True when the compiled geometry holds this terrain as it is now (built and not edited since).", Since = 2)]
    public bool Built => _t.BuildIsCurrent;

    [ScriptDoc("Closed solid chunks that geomod can carve.", Since = 2)]
    public bool Geoable => _t.Geoable;

    [ScriptDoc("Walls hanging below the outer edges and hole rims.", Since = 2)]
    public bool Skirts => _t.Skirts;

    [ScriptDoc("The terrain as a generic object handle (get / set its Properties fields: cell_size, height_range, geoable, …).", Since = 2)]
    public ScriptObjectHandle Object() => new(_ctx, Obj("terrain:object"));

    [ScriptDoc("Sets every vertex height from fn(x, z, world_x, world_z), which returns metres above the origin. {fit = true} refits the height range to the new heights; otherwise it only widens as needed.",
        Example = "t:set_heights(function(x, z, wx, wz) return 4 * math.sin(wx / 10) end)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.TerrainSetHeights))]
    public ScriptTerrainHandle SetHeights(ScriptTerrainHeight fn, ScriptArgs? options)
    {
        const string Api = "terrain:set_heights";
        bool fit = Fit(Api, options);
        Vec3 o = _t.Origin;
        float cell = _t.CellSize;
        Edit("Set terrain heights", t => TerrainOps.SetHeightOffsets(t, (x, z) => fn(x, z, o.X + (x * cell), o.Z + (z * cell)), fit), Api);
        return this;
    }

    [ScriptDoc("Sets every vertex height from a flat list of nx * nz numbers (row by row: index z * nx + x + 1), metres above the origin.",
        Example = "t:set_heights(heights, {fit = true})", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.TerrainSetHeights))]
    public ScriptTerrainHandle SetHeights(ScriptArgs heights, ScriptArgs? options)
    {
        const string Api = "terrain:set_heights";
        bool fit = Fit(Api, options);
        int n = _t.Nx * _t.Nz;
        if (heights.Items.Count != n)
        {
            throw new ScriptApiException($"{Api}: the list holds {heights.Items.Count} heights; this terrain has {_t.Nx} x {_t.Nz} = {n} vertices.");
        }

        var values = new double[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = heights.Items[i] is double d && double.IsFinite(d)
                ? d
                : throw new ScriptApiException($"{Api}: entry {i + 1} is not a finite number.");
        }

        int nx = _t.Nx;
        Edit("Set terrain heights", t => TerrainOps.SetHeightOffsets(t, (x, z) => values[(z * nx) + x], fit), Api);
        return this;
    }

    [ScriptDoc("The world Y of the terrain surface at world (x, z), on the triangle the cell's diagonal picks (clamped to the grid).",
        Example = "local y = t:height_at(10, -4)", Since = 2)]
    public double HeightAt(double x, double z) => TerrainOps.HeightAt(_t, (float)x, (float)z);

    [ScriptDoc("The world Y of the terrain's walkable top at world (x, z), or nil outside its footprint or over a hole cell (where the build emits no ground). The design kit's ground provider reads terrain this way.",
        Example = "local y = t:surface_at(10, -4) if y then log.info(y) end", Since = 2)]
    public double? SurfaceAt(double x, double z) => TerrainOps.SurfaceAt(_t, (float)x, (float)z) is float y ? y : null;

    [ScriptDoc("True when cell (x, z) is a hole (cells are 0..nx-2 by 0..nz-2).", Example = "if t:is_hole(10, 12) then t:set_hole(10, 12, false) end", Since = 2)]
    public bool IsHole(int x, int z)
    {
        if (x < 0 || z < 0 || x >= _t.Grid.CellsX || z >= _t.Grid.CellsZ)
        {
            throw new ScriptApiException($"terrain:is_hole: cell ({x}, {z}) is outside the {_t.Grid.CellsX} x {_t.Grid.CellsZ} cells.");
        }

        return _t.Grid.IsHole(x, z);
    }

    [ScriptDoc("Metres above the origin of vertex (x, z).", Example = "local h = t:vertex_height(0, 0)", Since = 2)]
    public double VertexHeight(int x, int z)
    {
        if (x < 0 || z < 0 || x >= _t.Nx || z >= _t.Nz)
        {
            throw new ScriptApiException($"terrain:vertex_height: vertex ({x}, {z}) is outside the {_t.Nx} x {_t.Nz} grid.");
        }

        return TerrainOps.HeightOffset(_t, x, z);
    }

    [ScriptDoc("Fills the heights with seeded procedural noise over the current height range (or the given one). The same settings always give the same heights.",
        Example = "t:generate{seed = 7, type = \"hybrid\", feature_size = 96, height_range = 40, falloff = \"island\"}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.TerrainGenerate))]
    public ScriptTerrainHandle Generate(ScriptArgs? args)
    {
        const string Api = "terrain:generate";
        args ??= new ScriptArgs();
        args.RejectUnknown(ScriptOptionSets.TerrainGenerate);
        var d = new TerrainGenSettings();
        var s = new TerrainGenSettings
        {
            Seed = (uint)Math.Clamp(args.Number(Api, "seed", d.Seed), 0, uint.MaxValue),
            Type = Choice(Api, args, "type", d.Type),
            FeatureSize = (float)args.Number(Api, "feature_size", d.FeatureSize),
            Octaves = args.Int(Api, "octaves", d.Octaves),
            Roughness = (float)args.Number(Api, "roughness", d.Roughness),
            Lacunarity = (float)args.Number(Api, "lacunarity", d.Lacunarity),
            Warp = (float)args.Number(Api, "warp", d.Warp),
            Exponent = (float)args.Number(Api, "exponent", d.Exponent),
            Terraces = args.Int(Api, "terraces", d.Terraces),
            Falloff = Choice(Api, args, "falloff", d.Falloff),
            FalloffWidth = (float)args.Number(Api, "falloff_width", d.FalloffWidth),
            Offset = (float)args.Number(Api, "offset", d.Offset),
            SmoothPasses = args.Int(Api, "smooth", d.SmoothPasses),
        };
        double? range = args.NumberOrNull(Api, "height_range");
        double? min = args.NumberOrNull(Api, "height_min");
        Edit("Generate terrain", t =>
        {
            if (range is { } r)
            {
                t.HeightRange = (float)r;
            }

            if (min is { } m)
            {
                t.HeightMin = (float)m;
            }

            AlpineTerrainEdit.Sanitize(t);
            double[] h = TerrainGenerator.Generate(s, t.Nx, t.Nz, t.CellSize);
            int nx = t.Nx;
            TerrainOps.SetNormalizedHeights(t, (x, z) => h[(z * nx) + x]);
        }, Api);
        return this;
    }

    [ScriptDoc("Resamples the grid to new vertex counts (heights filtered, paint kept): {nx = , nz = [, weight_resolution = 1|2|4]}.",
        Example = "t:resize{nx = 257, nz = 257}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.TerrainResize))]
    public ScriptTerrainHandle Resize(ScriptArgs args)
    {
        const string Api = "terrain:resize";
        args.RejectUnknown(ScriptOptionSets.TerrainResize);
        int nx = args.Int(Api, "nx", _t.Nx), nz = args.Int(Api, "nz", _t.Nz);
        if (nx < AlpineTerrainRules.MinVerts || nx > AlpineTerrainRules.MaxVerts || nz < AlpineTerrainRules.MinVerts || nz > AlpineTerrainRules.MaxVerts)
        {
            throw new ScriptApiException($"{Api}: nx and nz must be {AlpineTerrainRules.MinVerts} to {AlpineTerrainRules.MaxVerts}.");
        }

        int? mul = args.Has("weight_resolution") ? args.Int(Api, "weight_resolution", 2) : null;
        if (mul is { } m && !AlpineTerrainRules.IsAllowedWeightResolution(m))
        {
            throw new ScriptApiException($"{Api}: 'weight_resolution' must be 1, 2 or 4.");
        }

        Edit("Resize terrain", t => TerrainOps.Resize(t, nx, nz, mul), Api);
        return this;
    }

    [ScriptDoc("Sets the texture layers (1 to 8, base first): a list of texture names, or of {texture = , uv_scale = } tables (uv_scale: metres per repeat, default 4). Each cell's faces take its most painted layer.",
        Example = "t:set_layers{\"grass1.tga\", {texture = \"rock02.tga\", uv_scale = 8}}", Since = 2)]
    public ScriptTerrainHandle SetLayers(ScriptArgs layers)
    {
        const string Api = "terrain:set_layers";
        var names = new List<string>();
        var scales = new List<float>();
        for (int i = 0; i < layers.Items.Count; i++)
        {
            switch (layers.Items[i])
            {
                case string s:
                    names.Add(s);
                    scales.Add(i < _t.Layers.Count ? _t.Layers[i].UvScale : AlpineTerrainRules.DefaultUvScale);
                    break;
                case ScriptArgs a:
                    a.RejectUnknown(Api, new ScriptOptionSet(Api, new ScriptOption("texture", "string", null, ""), new ScriptOption("uv_scale", "number", "4", "")));
                    names.Add(a.String(Api, "texture") ?? throw new ScriptApiException($"{Api}: layer {i + 1} has no 'texture'."));
                    scales.Add((float)a.Number(Api, "uv_scale", i < _t.Layers.Count ? _t.Layers[i].UvScale : AlpineTerrainRules.DefaultUvScale));
                    break;
                default:
                    throw new ScriptApiException($"{Api}: layer {i + 1} must be a texture name or a {{texture = , uv_scale = }} table.");
            }
        }

        Edit("Set terrain layers", t => TerrainOps.SetLayers(t, names, scales), Api);
        return this;
    }

    [ScriptDoc("Paints the whole terrain with one layer (1-based).", Example = "t:fill_layer(2)", Since = 2)]
    public ScriptTerrainHandle FillLayer(int layer)
    {
        Edit("Fill terrain layer", t => TerrainOps.FillLayer(t, layer - 1), "terrain:fill_layer");
        return this;
    }

    [ScriptDoc("Makes cell (x, z) a hole (true) or ground (false). Cells are 0..nx-2 by 0..nz-2.", Example = "t:set_hole(10, 12, true)", Since = 2)]
    public ScriptTerrainHandle SetHole(int x, int z, bool hole)
    {
        Edit(hole ? "Make terrain hole" : "Fill terrain hole", t => TerrainOps.SetHole(t, x, z, hole), "terrain:set_hole");
        return this;
    }

    [ScriptDoc("Sets every cell's hole state from fn(x, z, world_x, world_z) (cell indices and the cell centre in world metres; true = a hole), as one edit. Returns the hole count.",
        Example = "t:set_holes(function(x, z, wx, wz) return wx * wx + wz * wz > 400 end)", Since = 2)]
    public int SetHoles(ScriptTerrainHole fn)
    {
        Vec3 o = _t.Origin;
        float cell = _t.CellSize;
        int n = 0;
        Edit("Set terrain holes", t => n = TerrainOps.SetHoles(t, (x, z) => fn(x, z, o.X + ((x + 0.5) * cell), o.Z + ((z + 0.5) * cell))), "terrain:set_holes");
        return n;
    }

    [ScriptDoc("Sets every cell's hole state from a flat list of (nx - 1) * (nz - 1) booleans (index z * (nx - 1) + x + 1; true = a hole), as one edit; faster than the function form. Returns the hole count.",
        Example = "t:set_holes(holes)", Since = 2)]
    public int SetHoles(ScriptArgs holes)
    {
        const string Api = "terrain:set_holes";
        int cx = _t.Grid.CellsX, n = cx * _t.Grid.CellsZ;
        if (holes.Items.Count != n)
        {
            throw new ScriptApiException($"{Api}: the list holds {holes.Items.Count} entries; this terrain has {cx} x {_t.Grid.CellsZ} = {n} cells.");
        }

        var values = new bool[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = holes.Items[i] is bool b ? b : throw new ScriptApiException($"{Api}: entry {i + 1} is not true or false.");
        }

        int count = 0;
        Edit("Set terrain holes", t => count = TerrainOps.SetHoles(t, (x, z) => values[(z * cx) + x]), Api);
        return count;
    }

    [ScriptDoc("Imports a heightmap: a greyscale PNG (8 or 16 bit) or other image, or a square RAW16 (.r16 / .raw). Image row 0 is the +Z edge; it is resampled onto the grid over the current height range.",
        Example = "t:import_heightmap(\"island.png\")", Since = 2)]
    public ScriptTerrainHandle ImportHeightmap(string path)
    {
        const string Api = "terrain:import_heightmap";
        Func<string, string, string> access = _ctx.Services.FileReadAccess
            ?? throw new ScriptApiException($"{Api}: file import is not available in this host.");
        string full = access(ResolvePath(path), Api);
        (ushort[] samples, int w, int h) image;
        try
        {
            image = TerrainHeightmapFile.Read(full);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new ScriptApiException($"{Api}: {ex.Message}");
        }

        Edit("Import heightmap", t => TerrainOps.SetHeightsFromImage(t, image.samples, image.w, image.h), Api);
        return this;
    }

    [ScriptDoc("Writes the heights as a 16-bit greyscale PNG (or RAW16 for .r16 / .raw). A destructive operation (it writes a file).",
        Example = "t:export_heightmap(\"hills.png\")", Since = 2)]
    public ScriptTerrainHandle ExportHeightmap(string path)
    {
        const string Api = "terrain:export_heightmap";
        Func<string, string, string> access = _ctx.Services.FileWriteAccess
            ?? throw new ScriptApiException($"{Api}: file export is not available in this host.");
        string target = access(ResolvePath(path), Api);
        _ctx.RequireDestructive($"write {System.IO.Path.GetFileName(target)}");
        try
        {
            TerrainHeightmapFile.Write(target, _t.Grid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ScriptApiException($"{Api}: could not write '{System.IO.Path.GetFileName(target)}': {ex.Message}");
        }

        return this;
    }

    [ScriptDoc("Converts the terrain into detail brushes, one per chunk, as Build Geometry compiles it (no layer blend; stock lightmaps). Deletes the terrain (destructive) unless {keep = true}. Returns a brush_query.",
        Example = "local brushes = t:to_brushes{keep = false}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.TerrainToBrushes))]
    public ScriptBrushQuery ToBrushes(ScriptArgs? args)
    {
        const string Api = "terrain:to_brushes";
        args ??= new ScriptArgs();
        args.RejectUnknown(ScriptOptionSets.TerrainToBrushes);
        bool keep = args.Bool(Api, "keep", false);
        LevelObject obj = Obj(Api);
        if (!keep)
        {
            _ctx.RequireDestructive($"delete {TerrainChunkEmitter.Label(_t)} after converting it to brushes");
        }

        IReadOnlyList<int> uids;
        try
        {
            uids = TerrainOps.ConvertToBrushes(_ctx.Document, _ctx.Brushes, obj, keep);
        }
        catch (InvalidOperationException ex)
        {
            throw new ScriptApiException($"{Api}: {ex.Message}");
        }

        foreach (int uid in uids)
        {
            _ctx.NoteCreated(uid);
        }

        return new ScriptBrushQuery(_ctx, uids);
    }

    [ScriptDoc("One Terrain Tools stroke (any of Alpine RED's 14 modes) at a point or along a path, as one undo step. Returns {changed, geometry, note}; geometry = true means rebuild before playing.",
        Example = "t:sculpt{tool = \"raise\", path = {{0, 0}, {20, 5}}, radius = 6, strength = 0.8}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.TerrainSculpt))]
    public ScriptTerrainStroke Sculpt(ScriptArgs args) => Stroke("terrain:sculpt", args, TerrainTool.Raise);

    [ScriptDoc("Paints a base layer (layer = n), an overlay's (overlay = n) or a decoration's coverage (decoration = n) at a point or along a path, as one undo step; tool defaults to \"paint_layer\".",
        Example = "t:paint{layer = 2, path = {{-10, 0}, {10, 0}}, radius = 4, strength = 1}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.TerrainSculpt))]
    public ScriptTerrainStroke Paint(ScriptArgs args) => Stroke("terrain:paint", args, TerrainTool.PaintLayer);

    [ScriptDoc("The terrain's decorations (meshes scattered over it by painted coverage), in order.", Since = 2)]
    public ScriptTerrainDecoration[] Decorations() =>
        _t.Decorations.Select((d, i) => new ScriptTerrainDecoration(i + 1, d, _t)).ToArray();

    [ScriptDoc("Adds a decoration (or changes decoration 'index') and returns its 1-based index. Its coverage starts full when linked to a layer, else empty; 'coverage' paints it everywhere. One undo step.",
        Example = "local i = t:set_decoration{mesh = \"brush_grass1.v3m\", density = 0.4, scale_min = 0.8, scale_max = 1.3, coverage = 255}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.TerrainDecoration))]
    public int SetDecoration(ScriptArgs args)
    {
        const string Api = "terrain:set_decoration";
        args.RejectUnknown(ScriptOptionSets.TerrainDecoration);
        int index = args.Has("index") ? args.Int(Api, "index", 1) - 1 : _t.Decorations.Count;
        if (index < 0 || index > _t.Decorations.Count)
        {
            throw new ScriptApiException($"{Api}: 'index' must be 1 to {_t.Decorations.Count} (or omitted to add one).");
        }

        AlpineTerrainDecoration old = index < _t.Decorations.Count ? _t.Decorations[index] : new AlpineTerrainDecoration { Density = 0.5f, ScaleMin = 1f, ScaleMax = 1f, MaxSlopeDegrees = 35f, DrawDistance = 80f, Flags = AlpineTerrainSurface.DecorationFlagRandomYaw };
        string mesh = args.String(Api, "mesh") ?? old.Mesh;
        if (string.IsNullOrEmpty(mesh))
        {
            throw new ScriptApiException($"{Api}: a new decoration needs a 'mesh'.");
        }

        int link = args.Has("link_layer") ? args.Int(Api, "link_layer", 0) : old.LinkLayer == AlpineTerrainRules.DecorationLinkNone ? 0 : old.LinkLayer + 1;
        if (link < 0 || link > _t.Layers.Count)
        {
            throw new ScriptApiException($"{Api}: 'link_layer' must be 1 to {_t.Layers.Count}, or 0 for none.");
        }

        byte flags = old.Flags;
        flags = Bit(flags, AlpineTerrainSurface.DecorationFlagAlignToSlope, args.Bool(Api, "align_to_slope", (flags & AlpineTerrainSurface.DecorationFlagAlignToSlope) != 0));
        flags = Bit(flags, AlpineTerrainSurface.DecorationFlagRandomYaw, args.Bool(Api, "random_yaw", (flags & AlpineTerrainSurface.DecorationFlagRandomYaw) != 0));
        flags = Bit(flags, AlpineTerrainSurface.DecorationFlagCastsShadows, args.Bool(Api, "casts_shadows", (flags & AlpineTerrainSurface.DecorationFlagCastsShadows) != 0));
        flags = Bit(flags, AlpineTerrainSurface.DecorationFlagDitherFade, args.Bool(Api, "dither_fade", (flags & AlpineTerrainSurface.DecorationFlagDitherFade) != 0));
        var d = new AlpineTerrainDecoration(mesh,
            (float)args.Number(Api, "density", old.Density), (float)args.Number(Api, "scale_min", old.ScaleMin),
            (float)args.Number(Api, "scale_max", old.ScaleMax), (float)args.Number(Api, "max_slope", old.MaxSlopeDegrees),
            (float)args.Number(Api, "draw_distance", old.DrawDistance), (float)args.Number(Api, "vertical_offset", old.VerticalOffset),
            link == 0 ? AlpineTerrainRules.DecorationLinkNone : (byte)(link - 1), flags);
        int? coverage = args.Has("coverage") ? args.Int(Api, "coverage", 255) : null;
        if (coverage is { } c && (c < 0 || c > 255))
        {
            throw new ScriptApiException($"{Api}: 'coverage' must be 0 to 255.");
        }

        Edit(index == _t.Decorations.Count ? "Add terrain decoration" : "Change terrain decoration", t =>
        {
            TerrainOps.SetDecoration(t, index, d);
            if (coverage is { } cov)
            {
                TerrainOps.FillDecorationCoverage(t, index, (byte)cov);
            }
        }, Api);
        return index + 1;
    }

    [ScriptDoc("Removes decoration 'index' (1-based) and its painted coverage. One undo step.", Example = "t:remove_decoration(1)", Since = 2)]
    public ScriptTerrainHandle RemoveDecoration(int index)
    {
        Edit("Remove terrain decoration", t => TerrainOps.RemoveDecoration(t, index - 1), "terrain:remove_decoration");
        return this;
    }

    [ScriptDoc("Sets the overlays (0 to 4; alpha textures laid over the blend by their painted coverage): names or {texture = , uv_scale = , triplanar = , break_tiling = } tables. Coverage is kept by index.",
        Example = "t:set_overlays{{texture = \"mud01.tga\", uv_scale = 6}}", Since = 2)]
    public ScriptTerrainHandle SetOverlays(ScriptArgs overlays)
    {
        const string Api = "terrain:set_overlays";
        var list = new List<AlpineTerrainLayer>();
        var opts = new ScriptOptionSet(Api, new ScriptOption("texture", "string", null, ""), new ScriptOption("uv_scale", "number", "4", ""),
            new ScriptOption("triplanar", "boolean", "false", ""), new ScriptOption("break_tiling", "boolean", "false", ""));
        for (int i = 0; i < overlays.Items.Count; i++)
        {
            switch (overlays.Items[i])
            {
                case string s:
                    list.Add(new AlpineTerrainLayer(s, i < _t.Overlays.Count ? _t.Overlays[i].UvScale : AlpineTerrainRules.DefaultUvScale, 0));
                    break;
                case ScriptArgs a:
                    a.RejectUnknown(Api, opts);
                    byte f = (byte)((a.Bool(Api, "triplanar", false) ? AlpineTerrainRules.LayerFlagTriplanar : 0) | (a.Bool(Api, "break_tiling", false) ? AlpineTerrainRules.OverlayFlagBreakTiling : 0));
                    list.Add(new AlpineTerrainLayer(a.String(Api, "texture") ?? throw new ScriptApiException($"{Api}: overlay {i + 1} has no 'texture'."),
                        (float)a.Number(Api, "uv_scale", AlpineTerrainRules.DefaultUvScale), f));
                    break;
                default:
                    throw new ScriptApiException($"{Api}: overlay {i + 1} must be a texture name or a {{texture = , ...}} table.");
            }
        }

        Edit("Set terrain overlays", t => TerrainOps.SetOverlays(t, list), Api);
        return this;
    }

    [ScriptDoc("The overlay texture names, in drawing order.", Since = 2)]
    public string[] Overlays => _t.Overlays.Select(o => o.Texture).ToArray();

    [ScriptDoc("Stitch Edges: snaps the border to any grid-aligned terrain of the same cell size that shares it, as one undo step. Returns the border vertices matched (0 when no neighbour touches).",
        Example = "local n = t:stitch()", Since = 2)]
    public int Stitch()
    {
        var others = _ctx.Document.Objects.Select(o => o.Model).OfType<AlpineTerrain>().Where(o => !ReferenceEquals(o, _t)).ToList();
        int matched = 0;
        Edit("Stitch terrain edges", t => matched = TerrainOps.StitchEdges(t, others).Vertices, "terrain:stitch");
        return matched;
    }

    [ScriptDoc("The terrain's baked lighting: \"baked\" (a chart that matches it), \"stale\" (a chart from before an edit), \"none\" or \"fullbright\". Calculate Lighting (ops.light) bakes charts for built terrains.", Since = 2)]
    public string Lighting
    {
        get
        {
            if (_t.Fullbright)
            {
                return "fullbright";
            }

            if (Ged.Core.Lighting.Alpine.AlpineTerrainLight.For(_ctx.Document.Rfl, _t) is not null)
            {
                return "baked";
            }

            return Ged.Core.Lighting.Alpine.AlpineTerrainLight.Charts(_ctx.Document.Rfl).Any(c => c.Chart.TerrainUid == _t.Uid) ? "stale" : "none";
        }
    }

    private ScriptTerrainStroke Stroke(string api, ScriptArgs args, TerrainTool defaultTool)
    {
        args.RejectUnknown(ScriptOptionSets.TerrainSculpt);
        TerrainTool tool = args.String(api, "tool") is { } name ? ParseTool(api, name) : defaultTool;
        int layer = args.Int(api, "layer", 1) - 1;
        int layers = Math.Min(_t.Layers.Count, AlpineTerrainRules.MaxLayers);
        if (args.Has("overlay"))
        {
            int o = args.Int(api, "overlay", 1);
            if (o < 1 || o > _t.Overlays.Count)
            {
                throw new ScriptApiException($"{api}: the terrain has overlays 1 to {_t.Overlays.Count}.");
            }

            layer = layers + o - 1;
        }
        else if (args.Has("decoration"))
        {
            int d = args.Int(api, "decoration", 1);
            if (d < 1 || d > _t.Decorations.Count)
            {
                throw new ScriptApiException($"{api}: the terrain has decorations 1 to {_t.Decorations.Count}.");
            }

            layer = layers + _t.Overlays.Count + d - 1;
        }
        else if (layer < 0 || layer >= layers)
        {
            throw new ScriptApiException($"{api}: the terrain has layers 1 to {layers}.");
        }

        var settings = new TerrainBrushSettings
        {
            Tool = tool,
            Layer = layer,
            Radius = (float)args.Number(api, "radius", 8),
            Strength = (float)args.Number(api, "strength", 0.5),
            Falloff = args.String(api, "falloff") is { } f ? Choice(api, args, "falloff", TerrainFalloff.Smooth) : TerrainFalloff.Smooth,
            Spacing = (float)args.Number(api, "spacing", 0.25),
            SetHeight = (float)args.Number(api, "height", 0),
            RampAngle = (float)args.Number(api, "angle", 15),
            MirrorX = args.Bool(api, "mirror_x", false),
            MirrorZ = args.Bool(api, "mirror_z", false),
            MirrorXOffset2 = (int)Math.Round(args.Number(api, "mirror_x_offset", 0) * 2),
            MirrorZOffset2 = (int)Math.Round(args.Number(api, "mirror_z_offset", 0) * 2),
        };
        var points = new List<(float X, float Z)>();
        if (args.Table(api, "at") is { } at)
        {
            points.Add(Point(api, "at", at));
        }

        if (args.Table(api, "path") is { } path)
        {
            for (int i = 0; i < path.Items.Count; i++)
            {
                points.Add(path.Items[i] is ScriptArgs p ? Point(api, $"path[{i + 1}]", p) : throw new ScriptApiException($"{api}: path[{i + 1}] must be a {{x, z}} point."));
            }
        }

        if (points.Count == 0)
        {
            throw new ScriptApiException($"{api}: give 'at' = {{x, z}} or 'path' = {{{{x, z}}, ...}}.");
        }

        int hold = Math.Clamp(args.Int(api, "hold", 0), 0, 10000);
        uint seed = (uint)Math.Clamp(args.Number(api, "seed", 1), 0, uint.MaxValue);
        LevelObject obj = Obj(api);
        TerrainStroke stroke;
        try
        {
            stroke = new TerrainStroke(_ctx.Document, obj, settings, seed);
        }
        catch (InvalidOperationException ex)
        {
            throw new ScriptApiException($"{api}: {ex.Message}");
        }

        foreach ((float x, float z) in points)
        {
            stroke.MoveTo(x, z);
        }

        for (int i = 0; i < hold; i++)
        {
            stroke.Repeat();
        }

        bool changed = stroke.Finish();
        return new ScriptTerrainStroke(changed, stroke.EditedGeometry, stroke.Note ?? string.Empty);
    }

    private static (float X, float Z) Point(string api, string key, ScriptArgs p)
    {
        if (p.Items.Count is not (2 or 3) || p.Items.Any(v => v is not double d || !double.IsFinite(d)))
        {
            throw new ScriptApiException($"{api}: '{key}' must be {{x, z}} (or {{x, y, z}}) in world units.");
        }

        return ((float)(double)p.Items[0]!, (float)(double)p.Items[p.Items.Count - 1]!);
    }

    private static TerrainTool ParseTool(string api, string name)
    {
        string norm = name.Replace("_", string.Empty, StringComparison.Ordinal).Trim();
        if (string.Equals(norm, "erase", StringComparison.OrdinalIgnoreCase))
        {
            return TerrainTool.EraseToBase;
        }

        if (Enum.TryParse(norm, ignoreCase: true, out TerrainTool t))
        {
            return t;
        }

        string[] names = Enum.GetNames<TerrainTool>().Select(n => string.Concat(n.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()))).ToArray();
        throw new ScriptApiException($"{api}: unknown tool '{name}'.", ScriptSuggest.Hint(name, names, "tool"));
    }

    private static byte Bit(byte flags, byte bit, bool on) => (byte)(on ? flags | bit : flags & ~bit);

    [ScriptDoc("The lint findings about this terrain (build state, collision, geomod setup, Alpine limits), as messages.",
        Example = "for _, m in ipairs(t:lint()) do log.warn(m) end", Since = 2)]
    public string[] Lint()
    {
        var objects = _ctx.Document.Objects.ToList();
        var findings = new List<LintFinding>();
        TerrainChecks.Run(_ctx.Document.Rfl, objects, findings);
        LevelObject? obj = _ctx.Document.Objects.FirstOrDefault(o => ReferenceEquals(o.Model, _t));
        return findings.Where(f => f.Uid == (obj?.Uid ?? _t.Uid)).Select(f => $"{f.Severity}: {f.Message}").ToArray();
    }

    public override string ToString() => $"{TerrainChunkEmitter.Label(_t)} ({_t.Nx} x {_t.Nz})";

    /// <summary>Applies the shape keys of <c>level.terrain.create</c> (one more undo step of the same run).</summary>
    internal void ApplyShape(string api, ScriptArgs args)
    {
        bool any = new[] { "height_min", "height_range", "geoable", "skirts", "fullbright", "thickness", "skirt_depth", "chunk_cells", "layers" }.Any(args.Has);
        if (!any)
        {
            return;
        }

        var names = new List<string>();
        if (args.Table(api, "layers") is { } layers)
        {
            foreach (object? item in layers.Items)
            {
                names.Add(item as string ?? throw new ScriptApiException($"{api}: 'layers' must be a list of texture names."));
            }
        }

        foreach (string n in names)
        {
            if (!AlpineTerrainRules.TextureNameValid(n))
            {
                throw new ScriptApiException($"{api}: texture name '{n}' is too long for RFL (31 characters).");
            }
        }

        int chunk = args.Int(api, "chunk_cells", _t.ChunkCells);
        if (!AlpineTerrainRules.IsAllowedChunkCells(chunk))
        {
            throw new ScriptApiException($"{api}: 'chunk_cells' must be 16, 32, 64 or 128.");
        }

        Edit("Set terrain shape", t =>
        {
            t.HeightMin = (float)args.Number(api, "height_min", t.HeightMin);
            t.HeightRange = (float)args.Number(api, "height_range", t.HeightRange);
            t.Geoable = args.Bool(api, "geoable", t.Geoable);
            t.Skirts = args.Bool(api, "skirts", t.Skirts);
            t.Fullbright = args.Bool(api, "fullbright", t.Fullbright);
            t.Thickness = (float)args.Number(api, "thickness", t.Thickness);
            t.SkirtDepth = (float)args.Number(api, "skirt_depth", t.SkirtDepth);
            t.ChunkCells = (byte)chunk;
            if (names.Count > 0)
            {
                TerrainOps.SetLayers(t, names);
            }
        }, api);
    }

    private void Edit(string description, Action<AlpineTerrain> mutate, string api)
    {
        LevelObject obj = Obj(api);
        try
        {
            TerrainOps.Apply(_ctx.Document, obj, description, mutate);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new ScriptApiException($"{api}: {ex.Message}");
        }
    }

    private LevelObject Obj(string api) =>
        _ctx.Document.Objects.FirstOrDefault(o => ReferenceEquals(o.Model, _t))
        ?? throw new ScriptApiException($"{api}: terrain {_t.Uid} is no longer in the level.");

    private static bool Fit(string api, ScriptArgs? options)
    {
        if (options is null)
        {
            return true;
        }

        options.RejectUnknown(ScriptOptionSets.TerrainSetHeights);
        return options.Bool(api, "fit", true);
    }

    private static T Choice<T>(string api, ScriptArgs args, string key, T fallback)
        where T : struct, Enum
    {
        string? s = args.String(api, key);
        if (s is null)
        {
            return fallback;
        }

        string norm = s.Replace("_", string.Empty, StringComparison.Ordinal).Trim();
        if (Enum.TryParse(norm, ignoreCase: true, out T value))
        {
            return value;
        }

        string[] names = Enum.GetNames<T>().Select(n => n.ToLowerInvariant()).ToArray();
        throw new ScriptApiException($"{api}: unknown '{key}' value '{s}'.", ScriptSuggest.Hint(s, names, "value"));
    }

    private string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ScriptApiException("A file path is required.");
        }

        if (System.IO.Path.IsPathRooted(path))
        {
            return System.IO.Path.GetFullPath(path);
        }

        string? dir = _ctx.Document.Path is { Length: > 0 } p ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(p)) : null;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(dir ?? Environment.CurrentDirectory, path));
    }
}

/// <summary>Lua: what one <c>terrain:sculpt</c> / <c>terrain:paint</c> stroke did.</summary>
[ScriptDoc("What one terrain stroke did.", Since = 2)]
public sealed class ScriptTerrainStroke
{
    internal ScriptTerrainStroke(bool changed, bool geometry, string note)
    {
        Changed = changed;
        Geometry = geometry;
        Note = note;
    }

    [ScriptDoc("True when the stroke changed the terrain (one undo step).", Since = 2)]
    public bool Changed { get; }

    [ScriptDoc("True when heights, holes or diagonals changed: the compiled geometry is out of date until the next build.", Since = 2)]
    public bool Geometry { get; }

    [ScriptDoc("Alpine RED's status note for the stroke (the height range grew, clamped, chunks toggled ...), or empty.", Since = 2)]
    public string Note { get; }

    public override string ToString() => $"terrain stroke (changed = {Changed}, geometry = {Geometry})";
}

/// <summary>Lua: one terrain decoration's settings.</summary>
[ScriptDoc("One terrain decoration: a mesh scattered over the terrain by its painted coverage.", Since = 2)]
public sealed class ScriptTerrainDecoration
{
    private readonly AlpineTerrainDecoration _d;
    private readonly AlpineTerrain _t;

    internal ScriptTerrainDecoration(int index, AlpineTerrainDecoration d, AlpineTerrain t)
    {
        Index = index;
        _d = d;
        _t = t;
    }

    [ScriptDoc("1-based position in the terrain's list.", Since = 2)]
    public int Index { get; }

    [ScriptDoc("The mesh file.", Since = 2)]
    public string Mesh => _d.Mesh;

    [ScriptDoc("Instances per square metre at full coverage.", Since = 2)]
    public double Density => _d.Density;

    [ScriptDoc("Smallest instance scale.", Since = 2)]
    public double ScaleMin => _d.ScaleMin;

    [ScriptDoc("Largest instance scale.", Since = 2)]
    public double ScaleMax => _d.ScaleMax;

    [ScriptDoc("Steepest ground, degrees.", Since = 2)]
    public double MaxSlope => _d.MaxSlopeDegrees;

    [ScriptDoc("Metres the game draws them to.", Since = 2)]
    public double DrawDistance => _d.DrawDistance;

    [ScriptDoc("Mesh units along the instance's up axis.", Since = 2)]
    public double VerticalOffset => _d.VerticalOffset;

    [ScriptDoc("The linked base layer (1-based), or 0 for none.", Since = 2)]
    public int LinkLayer => _d.LinkLayer == AlpineTerrainRules.DecorationLinkNone ? 0 : _d.LinkLayer + 1;

    [ScriptDoc("Instances stand on the surface normal.", Since = 2)]
    public bool AlignToSlope => (_d.Flags & AlpineTerrainSurface.DecorationFlagAlignToSlope) != 0;

    [ScriptDoc("Each instance is turned at random.", Since = 2)]
    public bool RandomYaw => (_d.Flags & AlpineTerrainSurface.DecorationFlagRandomYaw) != 0;

    [ScriptDoc("Its shadows are baked into the terrain's chart in Alpine RED.", Since = 2)]
    public bool CastsShadows => (_d.Flags & AlpineTerrainSurface.DecorationFlagCastsShadows) != 0;

    [ScriptDoc("How many instances the game places for it (Alpine's placement rule, within the level budget).", Since = 2)]
    public int Instances
    {
        get
        {
            int n = 0;
            AlpineTerrainSurface.ForEachTerrainDecoration(_t, new AlpineTerrainSurface.Budget(), inst =>
            {
                n += inst.Decoration == Index - 1 ? 1 : 0;
                return true;
            });
            return n;
        }
    }

    public override string ToString() => $"decoration {Index} ({Mesh})";
}
