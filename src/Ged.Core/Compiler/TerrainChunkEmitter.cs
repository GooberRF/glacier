using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>One face of a terrain chunk: 3 or 4 vertex slots, their texture UVs and the material (a layer index, or the underside).</summary>
public readonly record struct TerrainEmitFace(int Count, int S0, int S1, int S2, int S3, Uv Uv0, Uv Uv1, Uv Uv2, Uv Uv3, int Material)
{
    public int Slot(int i) => i switch { 0 => S0, 1 => S1, 2 => S2, _ => S3 };

    public Uv Uv(int i) => i switch { 0 => Uv0, 1 => Uv1, 2 => Uv2, _ => Uv3 };
}

/// <summary>A chunk as one solid: its world positions (in order of first use) and faces over them.</summary>
public sealed class TerrainChunkSolid
{
    public int Chunk { get; init; }

    public List<Vec3> Positions { get; } = new();

    /// <summary>Faces as position indices, with UVs and the resolved texture name.</summary>
    public List<(int[] Vertices, Uv[] Uvs, string Texture, int Material)> Faces { get; } = new();

    public long Triangles { get; set; }
}

/// <summary>
/// The geometry Build Geometry compiles for an Alpine terrain, reimplemented from the behaviour of
/// <c>alpine_terrain::emit_chunk</c> and its helpers (common/include/common/terrain/alpine_terrain.h:971-1310) and
/// Alpine RED's chunk solid builder (editor_patch/terrain_build.cpp:181-243). Positions and UVs are computed in single
/// precision in the header's order of operations, so every emitted vertex is bit-identical to Alpine RED's.
/// <list type="bullet">
/// <item>top: two triangles per non-hole cell, split by the cell's diagonal bit, textured with its dominant layer
/// (argmax of summed weights, lowest index on a tie, an untextured layer falls back to layer 0, :1037-1056), planar UV
/// <c>(x / uv_scale, z / uv_scale)</c> (:956-960);</item>
/// <item>geoable: a closed solid — walls on every edge between a solid cell of the chunk and anything else, and a
/// bottom at <c>origin.y + height_min - thickness</c> (a quad per cell, or a fan from the middle when the chunk has no
/// hole and is at least 2 x 2, :1194-1205);</item>
/// <item>otherwise with skirts (depth at least 0.01): walls on the terrain's outer edge and around holes;</item>
/// <item>otherwise the top alone (an open sheet).</item>
/// </list>
/// Walls and bottoms carry the underside texture at layer 0's scale (:962-969).
/// </summary>
public static class TerrainChunkEmitter
{
    /// <summary>The chunk edge a build uses for <paramref name="t"/> (terrain_build.cpp:991-994).</summary>
    public static int EffectiveChunkCells(AlpineTerrain t) =>
        AlpineTerrainRules.EffectiveChunkCells(t.Grid.CellsX, t.Grid.CellsZ, t.ChunkCells, t.Flags & AlpineTerrainRules.FlagMask);

    /// <summary>The chunk count a build makes for <paramref name="t"/> (the build mapping's length).</summary>
    public static int ChunkCount(AlpineTerrain t) =>
        AlpineTerrainRules.ChunkCount(t.Grid.CellsX, t.Grid.CellsZ, EffectiveChunkCells(t));

    /// <summary>Every face of chunk <paramref name="r"/>, in Alpine's emission order.</summary>
    public static List<TerrainEmitFace> EmitChunk(AlpineTerrain t, TerrainChunkRect r)
    {
        var faces = new List<TerrainEmitFace>();
        var v = new View(t);
        bool closed = (v.Flags & AlpineTerrainRules.FlagGeoable) != 0;
        bool skirts = !closed && (v.Flags & AlpineTerrainRules.FlagSkirts) != 0 && t.SkirtDepth >= AlpineTerrainRules.MinSkirtDepth;
        bool fan = BottomIsFan(v, r);
        float s0 = v.UvScale[0];

        void Bottom(int count, int a, int b, int c, int d)
        {
            Uv U(int slot)
            {
                Vec3 p = SlotPosition(v, r, slot);
                return new Uv(p.X / s0, p.Z / s0);
            }

            faces.Add(new TerrainEmitFace(count, a, b, c, d, U(a), U(b), U(c), count == 4 ? U(d) : default, AlpineTerrainRules.MaterialUnderside));
        }

        void Wall(int px, int pz, int qx, int qz, bool alongX)
        {
            int a = TopSlot(r, px, pz), b = TopSlot(r, qx, qz), c = LowSlot(r, qx, qz), d = LowSlot(r, px, pz);
            Uv U(int slot)
            {
                Vec3 p = SlotPosition(v, r, slot);
                return new Uv((alongX ? p.X : p.Z) / s0, -p.Y / s0);
            }

            faces.Add(new TerrainEmitFace(4, a, b, c, d, U(a), U(b), U(c), U(d), AlpineTerrainRules.MaterialUnderside));
        }

        bool Walled(int nx, int nz)
        {
            if (closed)
            {
                bool inside = nx >= r.X0 && nx < r.X1 && nz >= r.Z0 && nz < r.Z1;
                return !inside || !CellSolid(v, nx, nz);
            }

            return skirts && !CellSolid(v, nx, nz);
        }

        for (int z = r.Z0; z < r.Z1; z++)
        {
            for (int x = r.X0; x < r.X1; x++)
            {
                if (!CellSolid(v, x, z))
                {
                    continue;
                }

                int a = TopSlot(r, x, z), b = TopSlot(r, x + 1, z);
                int c = TopSlot(r, x + 1, z + 1), d = TopSlot(r, x, z + 1);
                int layer = DominantLayer(v, x, z);
                float scale = v.UvScale[layer];
                bool diag = t.Grid.Diagonal(x, z);
                Tri(a, diag ? d : c, b);
                Tri(diag ? b : a, d, c);

                void Tri(int p0, int p1, int p2)
                {
                    Uv U(int slot)
                    {
                        Vec3 p = SlotPosition(v, r, slot);
                        return new Uv(p.X / scale, p.Z / scale);
                    }

                    faces.Add(new TerrainEmitFace(3, p0, p1, p2, 0, U(p0), U(p1), U(p2), default, layer));
                }

                if (Walled(x, z - 1))
                {
                    Wall(x, z, x + 1, z, true); // faces -z
                }

                if (Walled(x, z + 1))
                {
                    Wall(x + 1, z + 1, x, z + 1, true); // faces +z
                }

                if (Walled(x - 1, z))
                {
                    Wall(x, z + 1, x, z, false); // faces -x
                }

                if (Walled(x + 1, z))
                {
                    Wall(x + 1, z, x + 1, z + 1, false); // faces +x
                }

                if (closed && !fan)
                {
                    Bottom(4, LowSlot(r, x, z), LowSlot(r, x + 1, z), LowSlot(r, x + 1, z + 1), LowSlot(r, x, z + 1));
                }
            }
        }

        if (fan)
        {
            // The lower perimeter in the bottom quads' winding: -z side, +x, +z, -x (:1302-1309).
            int mid = LowSlot(r, r.X0 + ((r.X1 - r.X0) / 2), r.Z0 + ((r.Z1 - r.Z0) / 2));
            for (int x = r.X0; x < r.X1; x++)
            {
                Bottom(3, mid, LowSlot(r, x, r.Z0), LowSlot(r, x + 1, r.Z0), 0);
            }

            for (int z = r.Z0; z < r.Z1; z++)
            {
                Bottom(3, mid, LowSlot(r, r.X1, z), LowSlot(r, r.X1, z + 1), 0);
            }

            for (int x = r.X1; x > r.X0; x--)
            {
                Bottom(3, mid, LowSlot(r, x, r.Z1), LowSlot(r, x - 1, r.Z1), 0);
            }

            for (int z = r.Z1; z > r.Z0; z--)
            {
                Bottom(3, mid, LowSlot(r, r.X0, z), LowSlot(r, r.X0, z - 1), 0);
            }
        }

        return faces;
    }

    /// <summary>World position of a chunk vertex slot (top grid, then the lower grid; :1166-1192).</summary>
    public static Vec3 SlotPosition(AlpineTerrain t, TerrainChunkRect r, int slot) => SlotPosition(new View(t), r, slot);

    /// <summary>
    /// The chunks of <paramref name="t"/> as solids (empty chunks left out; terrain_build.cpp:181-243): positions in order
    /// of first reference, faces with the resolved texture names (an empty layer name takes the base layer's, an empty
    /// base the RED default; the underside likewise falls back to the base, :147-174). <paramref name="notes"/> receives
    /// a line for every chunk that cannot be built (too many vertices for a room).
    /// </summary>
    public static List<TerrainChunkSolid> BuildChunkSolids(AlpineTerrain t, List<string>? notes = null)
    {
        string[] textures = ResolveTextures(t);
        var result = new List<TerrainChunkSolid>();
        int cx = t.Grid.CellsX, cz = t.Grid.CellsZ;
        int edge = EffectiveChunkCells(t);
        int count = AlpineTerrainRules.ChunkCount(cx, cz, edge);
        var v = new View(t);
        for (int k = 0; k < count; k++)
        {
            TerrainChunkRect rect = AlpineTerrainRules.ChunkRect(cx, cz, edge, k);
            List<TerrainEmitFace> faces = EmitChunk(t, rect);
            if (faces.Count == 0)
            {
                continue;
            }

            var solid = new TerrainChunkSolid { Chunk = k };
            var slotIndex = new Dictionary<int, int>();
            foreach (TerrainEmitFace f in faces)
            {
                var idx = new int[f.Count];
                var uvs = new Uv[f.Count];
                for (int i = 0; i < f.Count; i++)
                {
                    int slot = f.Slot(i);
                    if (!slotIndex.TryGetValue(slot, out int index))
                    {
                        index = solid.Positions.Count;
                        slotIndex[slot] = index;
                        solid.Positions.Add(SlotPosition(v, rect, slot));
                    }

                    idx[i] = index;
                    uvs[i] = f.Uv(i);
                }

                solid.Faces.Add((idx, uvs, textures[f.Material], f.Material));
                solid.Triangles += f.Count - 2;
            }

            if (solid.Positions.Count >= AlpineTerrainRules.MaxRoomVertices)
            {
                notes?.Add($"{Label(t)}: chunk {k} has {solid.Positions.Count} vertices, but a room holds at most {AlpineTerrainRules.MaxRoomVertices - 1}; it was left out.");
                continue;
            }

            result.Add(solid);
        }

        return result;
    }

    /// <summary>
    /// The chunk as a detail brush (brush flags detail): positions relative to <paramref name="pivot"/> under an identity
    /// rotation, planes recomputed. With a zero pivot the compiled positions are the emitted ones bit for bit, which the
    /// build mapping hash relies on (terrain_build.cpp:255-264); convert-to-brushes pivots every chunk at the terrain
    /// origin so a vertex two chunks share lands on one position (:1344-1352).
    /// </summary>
    public static Brush ToBrush(TerrainChunkSolid solid, int uid, Vec3 pivot)
    {
        var g = new Geometry { Name = string.Empty };
        var textureIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Vec3 p in solid.Positions)
        {
            g.Vertices.Add(new Vec3(p.X - pivot.X, p.Y - pivot.Y, p.Z - pivot.Z));
        }

        foreach ((int[] verts, Uv[] uvs, string texture, int _) in solid.Faces)
        {
            if (!textureIndex.TryGetValue(texture, out int ti))
            {
                ti = g.Textures.Count;
                g.Textures.Add(texture);
                textureIndex[texture] = ti;
            }

            var face = new Face { Texture = ti, SurfaceIndex = -1, RoomIndex = -1, FaceId = g.Faces.Count };
            for (int i = 0; i < verts.Length; i++)
            {
                face.Vertices.Add(new FaceVertex { Index = verts[i], TextureCoords = uvs[i] });
            }

            g.Faces.Add(face);
        }

        GeometryUtil.RecomputeAllPlanes(g);
        return new Brush
        {
            Uid = uid,
            Position = pivot,
            Rotation = Mat3.Identity,
            Geometry = g,
            Flags = (uint)BrushFlags.Detail,
            Life = -1,
            State = BrushState.Normal,
        };
    }

    /// <summary>"Terrain uid 'name'" for messages (terrain_build.cpp:985-989).</summary>
    public static string Label(AlpineTerrain t) =>
        string.IsNullOrEmpty(t.ScriptName) ? $"Terrain {t.Uid}" : $"Terrain {t.Uid} '{t.ScriptName}'";

    /// <summary>The texture name per material slot: layers 0..7, then the underside.</summary>
    public static string[] ResolveTextures(AlpineTerrain t)
    {
        var names = new string[AlpineTerrainRules.MaxLayers + 1];
        string first = t.Layers.Count > 0 ? t.Layers[0].Texture : string.Empty;
        string baseName = string.IsNullOrEmpty(first) ? AlpineTerrainRules.DefaultLayerTexture : first;
        for (int i = 0; i < AlpineTerrainRules.MaxLayers; i++)
        {
            names[i] = i > 0 && i < t.Layers.Count && !string.IsNullOrEmpty(t.Layers[i].Texture) ? t.Layers[i].Texture : baseName;
        }

        names[AlpineTerrainRules.MaterialUnderside] = string.IsNullOrEmpty(t.UndersideTexture) ? baseName : t.UndersideTexture;
        return names;
    }

    /// <summary>The dominant layer of a cell (exposed for the viewport).</summary>
    public static int DominantLayer(AlpineTerrain t, int x, int z) => DominantLayer(new View(t), x, z);

    /// <summary>World y of the top surface at grid vertex (x, z) (:1058-1062).</summary>
    public static float TopY(AlpineTerrain t, int x, int z) =>
        AlpineTerrainRules.WorldY(t.Origin.Y, t.Grid.Height(x, z), t.HeightMin, t.HeightRange);

    /// <summary>The world y of a closed terrain's flat bottom (:1154-1157).</summary>
    public static float BottomY(AlpineTerrain t) => t.Origin.Y + t.HeightMin - t.Thickness;

    // ---- the grid view (make_grid_view, :975-1019) -----------------------------------------------------------------

    private readonly struct View
    {
        public View(AlpineTerrain t)
        {
            T = t;
            Flags = t.Flags & AlpineTerrainRules.FlagMask;
            UvScale = new float[AlpineTerrainRules.MaxLayers];
            int textured = 0;
            for (int l = 0; l < t.Layers.Count && l < AlpineTerrainRules.MaxLayers; l++)
            {
                if (!string.IsNullOrEmpty(t.Layers[l].Texture))
                {
                    textured |= 1 << l;
                }

                UvScale[l] = t.Layers[l].UvScale;
            }

            TexturedLayers = textured;
        }

        public AlpineTerrain T { get; }

        public int Flags { get; }

        public float[] UvScale { get; }

        public int TexturedLayers { get; }
    }

    private static int DominantLayer(View v, int x, int z)
    {
        AlpineTerrainGrid g = v.T.Grid;
        int m = g.WeightResolution;
        int row = AlpineTerrainRules.WeightWidth(g.Nx, m);
        int map = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, m);
        Span<int> sum = stackalloc int[AlpineTerrainRules.MaxLayers];
        for (int j = z * m; j < (z * m) + m; j++)
        {
            for (int i = x * m; i < (x * m) + m; i++)
            {
                int texel = (j * row) + i;
                for (int c = 0; c < 4; c++)
                {
                    sum[c] += g.Weights[(texel * 4) + c];
                    sum[4 + c] += g.Weights[map + (texel * 4) + c];
                }
            }
        }

        int best = 0;
        int n = Math.Min(v.T.Layers.Count, AlpineTerrainRules.MaxLayers);
        for (int l = 1; l < n; l++)
        {
            if (sum[l] > sum[best])
            {
                best = l;
            }
        }

        return ((v.TexturedLayers >> best) & 1) != 0 ? best : 0;
    }

    private static bool CellSolid(View v, int x, int z)
    {
        AlpineTerrainGrid g = v.T.Grid;
        if (x < 0 || z < 0 || x >= g.CellsX || z >= g.CellsZ)
        {
            return false;
        }

        return !g.IsHole(x, z);
    }

    private static bool BottomIsFan(View v, TerrainChunkRect r)
    {
        if ((v.Flags & AlpineTerrainRules.FlagGeoable) == 0 || r.X1 - r.X0 < 2 || r.Z1 - r.Z0 < 2)
        {
            return false;
        }

        for (int z = r.Z0; z < r.Z1; z++)
        {
            for (int x = r.X0; x < r.X1; x++)
            {
                if (!CellSolid(v, x, z))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int TopSlot(TerrainChunkRect r, int x, int z) => ((z - r.Z0) * (r.X1 - r.X0 + 1)) + (x - r.X0);

    private static int LowSlot(TerrainChunkRect r, int x, int z) => TopSlot(r, x, z) + ((r.X1 - r.X0 + 1) * (r.Z1 - r.Z0 + 1));

    private static Vec3 SlotPosition(View v, TerrainChunkRect r, int slot)
    {
        AlpineTerrain t = v.T;
        int w = r.X1 - r.X0 + 1;
        int grid = w * (r.Z1 - r.Z0 + 1);
        bool low = slot >= grid;
        int s = low ? slot - grid : slot;
        int x = r.X0 + (s % w);
        int z = r.Z0 + (s / w);
        float px = t.Origin.X + (x * t.CellSize);
        float pz = t.Origin.Z + (z * t.CellSize);
        float py = AlpineTerrainRules.WorldY(t.Origin.Y, t.Grid.Height(x, z), t.HeightMin, t.HeightRange);
        if (low)
        {
            py = (v.Flags & AlpineTerrainRules.FlagGeoable) != 0 ? (t.Origin.Y + t.HeightMin) - t.Thickness : py - t.SkirtDepth;
        }

        return new Vec3(px, py, pz);
    }
}
