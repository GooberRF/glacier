using System.Numerics;
using Ged.Core.Compiler;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;
using Ged.Rendering.Picking;

namespace Ged.Rendering.Scene;

/// <summary>
/// The viewport drawing of an Alpine terrain, drawn straight from the terrain so it shows before a build and while it is
/// edited: the top surface with Alpine's multi-layer weight blend (a <see cref="TerrainDraw"/>, the terrain shader —
/// resources/shaders/standard_ps.hlsl:1169-1560), overlays over it and the baked light chart when one matches; the
/// walls, skirts and closed bottom Build Geometry compiles (<see cref="TerrainChunkEmitter"/>, underside texture) as
/// ordinary batches; and the decorations placed as the game places them. Everything is pickable as the terrain object.
/// </summary>
public static class TerrainSurface
{
    /// <summary>The most decoration instances the viewport draws per level (the game's budget is 250 000).</summary>
    public const int MaxViewportDecorations = 20000;

    /// <summary>Where the terrain's icon sits: the centre of its footprint and height span (editor_patch/terrain.cpp:3175-3185).</summary>
    public static Vec3 IconPosition(AlpineTerrain t)
    {
        AlpineTerrainGrid g = t.Grid;
        if (g.Heights.Length == 0)
        {
            return t.Origin;
        }

        ushort lo = g.Heights.Min(), hi = g.Heights.Max();
        float yLo = AlpineTerrainRules.HeightOffset(lo, t.HeightMin, t.HeightRange);
        float yHi = AlpineTerrainRules.HeightOffset(hi, t.HeightMin, t.HeightRange);
        return new Vec3(
            t.Origin.X + (AlpineTerrainRules.Extent(g.Nx, t.CellSize) * 0.5f),
            t.Origin.Y + ((yLo + yHi) * 0.5f),
            t.Origin.Z + (AlpineTerrainRules.Extent(g.Nz, t.CellSize) * 0.5f));
    }

    /// <summary>
    /// Appends the terrain: its blended top (<see cref="RenderScene.Terrains"/>) and its underside faces as opaque batches,
    /// all owned by its UID. <paramref name="light"/> is the baked chart that matches it, if any.
    /// </summary>
    public static void Append(RenderScene scene, AlpineTerrain terrain, uint color, AlpineTerrainChartImage? light = null)
    {
        AlpineTerrain t = terrain.DeepCopy();
        AlpineTerrainEdit.Sanitize(t);
        uint pick = new PickId(PickKind.Object, t.Uid & 0x0FFFFFFF).Encode();
        scene.Terrains.Add(BuildTop(t, terrain.Uid, color, pick, light));
        AppendUnderside(scene, t, terrain.Uid, color, pick);
    }

    /// <summary>The blended top: one grid vertex per height sample, two triangles per solid cell split by its diagonal.</summary>
    public static TerrainDraw BuildTop(AlpineTerrain t, int uid, uint color, uint pick, AlpineTerrainChartImage? light)
    {
        AlpineTerrainGrid g = t.Grid;
        int nx = g.Nx, nz = g.Nz, mul = g.WeightResolution;
        int ww = AlpineTerrainRules.WeightWidth(nx, mul), wh = AlpineTerrainRules.WeightHeight(nz, mul);
        int map = AlpineTerrainRules.WeightMapBytes(nx, nz, mul);
        var draw = new TerrainDraw
        {
            Uid = uid,
            Origin = new Vector3(t.Origin.X, t.Origin.Y, t.Origin.Z),
            Extent = new Vector2(AlpineTerrainRules.Extent(nx, t.CellSize), AlpineTerrainRules.Extent(nz, t.CellSize)),
            LayerCount = Math.Clamp(t.Layers.Count, 1, AlpineTerrainRules.MaxLayers),
            WeightWidth = ww,
            WeightHeight = wh,
            Weights0 = g.Weights.AsSpan(0, map).ToArray(),
            Weights1 = g.Weights.AsSpan(map, map).ToArray(),
            OverlayCoverage = t.Overlays.Count > 0 && g.OverlayCoverage is { } cov && cov.Length == map ? (byte[])cov.Clone() : null,
            Fullbright = t.Fullbright,
        };

        string[] textures = TerrainChunkEmitter.ResolveTextures(t);
        for (int l = 0; l < AlpineTerrainRules.MaxLayers; l++)
        {
            bool has = l < t.Layers.Count;
            draw.Layers[l] = has && l < textures.Length ? textures[l] : textures[0];
            draw.LayerUvScale[l] = has ? t.Layers[l].UvScale : AlpineTerrainRules.DefaultUvScale;
            draw.LayerTriplanar[l] = has && (t.Layers[l].Flags & AlpineTerrainRules.LayerFlagTriplanar) != 0;
        }

        for (int o = 0; o < t.Overlays.Count && o < AlpineTerrainRules.MaxOverlays && draw.OverlayCoverage is not null; o++)
        {
            draw.Overlays.Add(t.Overlays[o].Texture);
            draw.OverlayUvScale[o] = t.Overlays[o].UvScale;
            draw.OverlayTriplanar[o] = (t.Overlays[o].Flags & AlpineTerrainRules.LayerFlagTriplanar) != 0;
        }

        if (light is not null && !t.Fullbright)
        {
            draw.Chart = light.Rgb;
            draw.ChartWidth = light.Chart.W;
            draw.ChartHeight = light.Chart.H;
            draw.ChartOrigin = new Vector2(light.Chart.OriginX, light.Chart.OriginZ);
            draw.ChartTexelSize = light.Chart.TexelSize;
        }

        float s0 = t.Layers[0].UvScale;
        var y = new float[nx * nz];
        for (int z = 0; z < nz; z++)
        {
            for (int x = 0; x < nx; x++)
            {
                y[(z * nx) + x] = TerrainChunkEmitter.TopY(t, x, z);
            }
        }

        for (int z = 0; z < nz; z++)
        {
            for (int x = 0; x < nx; x++)
            {
                // The heightmap normal at the vertex (alpine_terrain.h:1314-1327 vertex_normal).
                int lx = x > 0 ? x - 1 : 0, hx = x + 1 < nx ? x + 1 : x;
                int lz = z > 0 ? z - 1 : 0, hz = z + 1 < nz ? z + 1 : z;
                float sx = Math.Max(hx - lx, 1) * t.CellSize, sz = Math.Max(hz - lz, 1) * t.CellSize;
                var n = Vector3.Normalize(new Vector3((y[(z * nx) + lx] - y[(z * nx) + hx]) / sx, 1f, (y[(lz * nx) + x] - y[(hz * nx) + x]) / sz));
                float wx = t.Origin.X + (x * t.CellSize), wz = t.Origin.Z + (z * t.CellSize);
                draw.Vertices.Add(new WorldVertex
                {
                    Position = new Vector3(wx, y[(z * nx) + x], wz),
                    Normal = n,
                    TexCoord = new Vector2(wx / s0, wz / s0),
                    LightmapCoord = Vector2.Zero,
                    Color = color,
                    PickId = pick,
                });
            }
        }

        for (int z = 0; z < g.CellsZ; z++)
        {
            for (int x = 0; x < g.CellsX; x++)
            {
                if (g.IsHole(x, z))
                {
                    continue;
                }

                uint a = (uint)((z * nx) + x), b = a + 1, d = (uint)(((z + 1) * nx) + x), c = d + 1;
                if (!g.Diagonal(x, z))
                {
                    draw.Indices.AddRange(new[] { a, c, b, a, d, c });
                }
                else
                {
                    draw.Indices.AddRange(new[] { a, d, b, b, d, c });
                }
            }
        }

        return draw;
    }

    /// <summary>The walls, skirts and closed bottom the build compiles, as plain textured batches (the underside texture).</summary>
    private static void AppendUnderside(RenderScene scene, AlpineTerrain t, int uid, uint color, uint pick)
    {
        if (!t.Geoable && (!t.Skirts || t.SkirtDepth < AlpineTerrainRules.MinSkirtDepth))
        {
            return; // an open sheet has only its top
        }

        var batches = new Dictionary<string, GeometryBatch>(StringComparer.Ordinal);
        foreach (TerrainChunkSolid solid in TerrainChunkEmitter.BuildChunkSolids(t))
        {
            foreach ((int[] verts, Uv[] uvs, string texture, int material) in solid.Faces)
            {
                if (material != AlpineTerrainRules.MaterialUnderside)
                {
                    continue;
                }

                if (!batches.TryGetValue(texture, out GeometryBatch? batch))
                {
                    batch = new GeometryBatch(texture, -1, RenderPass.Opaque) { OwnerUid = uid };
                    batches[texture] = batch;
                }

                Vector3 a = V(solid.Positions[verts[0]]), b = V(solid.Positions[verts[1]]), c = V(solid.Positions[verts[2]]);
                Vector3 n = Vector3.Cross(b - a, c - a);
                n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
                int baseVertex = batch.Vertices.Count;
                for (int i = 0; i < verts.Length; i++)
                {
                    batch.Vertices.Add(new WorldVertex
                    {
                        Position = V(solid.Positions[verts[i]]),
                        Normal = n,
                        TexCoord = new Vector2(uvs[i].U, uvs[i].V),
                        LightmapCoord = Vector2.Zero,
                        Color = color,
                        PickId = pick,
                    });
                }

                for (int i = 1; i < verts.Length - 1; i++)
                {
                    batch.Indices.Add((uint)baseVertex);
                    batch.Indices.Add((uint)(baseVertex + i));
                    batch.Indices.Add((uint)(baseVertex + i + 1));
                }
            }
        }

        scene.Batches.AddRange(batches.Values);
    }

    /// <summary>
    /// Appends the terrain's decoration instances as the game places them (alpine_terrain.h:1724-1837), one draw per
    /// mesh, within <paramref name="budget"/> (the level's, carried across terrains) and the viewport cap
    /// <paramref name="viewportLeft"/>. Returns how many it placed.
    /// </summary>
    public static int AppendDecorations(RenderScene scene, AlpineTerrain terrain, AlpineTerrainSurface.Budget budget, ref int viewportLeft)
    {
        if (terrain.Decorations.Count == 0 || viewportLeft <= 0)
        {
            return 0;
        }

        AlpineTerrain t = terrain.DeepCopy();
        AlpineTerrainEdit.Sanitize(t);
        var pick = new PickId(PickKind.Object, t.Uid & 0x0FFFFFFF);
        var draws = new Dictionary<string, DecorationDraw>(StringComparer.OrdinalIgnoreCase);
        int left = viewportLeft;
        int placed = AlpineTerrainSurface.ForEachTerrainDecoration(t, budget, inst =>
        {
            string mesh = t.Decorations[inst.Decoration].Mesh;
            if (!draws.TryGetValue(mesh, out DecorationDraw? d))
            {
                d = new DecorationDraw { MeshFilename = mesh, PickId = pick };
                draws[mesh] = d;
            }

            // Rows right / up / forward scaled, translation the instance origin (the engine's Mat3 row order).
            float s = inst.Scale;
            d.Worlds.Add(new Matrix4x4(
                inst.Right.X * s, inst.Right.Y * s, inst.Right.Z * s, 0f,
                inst.Up.X * s, inst.Up.Y * s, inst.Up.Z * s, 0f,
                inst.Forward.X * s, inst.Forward.Y * s, inst.Forward.Z * s, 0f,
                inst.Position.X, inst.Position.Y, inst.Position.Z, 1f));
            return --left > 0;
        });
        viewportLeft = left;
        scene.Decorations.AddRange(draws.Values);
        return placed;
    }

    private static Vector3 V(Vec3 p) => new(p.X, p.Y, p.Z);
}
