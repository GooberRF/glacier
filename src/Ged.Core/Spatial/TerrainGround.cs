using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Spatial;

/// <summary>
/// The ground a level's Alpine terrains make, for the measurements that read compiled geometry (the floor sampler, the
/// spawn-height lint, point queries). A terrain compiles to an open sheet of detail faces (Alpine
/// <c>editor_patch/terrain_build.cpp:245-266</c>; <c>alpine_terrain.h:1207-1310</c>), so a ray from below meets nothing
/// that faces it: the space under the sheet would read as open floor with sky above. Alpine treats a terrain as a
/// heightfield — everything under its walkable top inside the footprint is ground — and so does this: a point more than
/// <see cref="Tolerance"/> under a terrain's top (outside holes) is inside the ground, and floor there is not floor.
/// Built from the level's terrain models (copies taken once), so it reflects the terrain as edited; a terrain edited
/// since its last build is measured as it is now (FEATURES approximation 151).
/// </summary>
public sealed class TerrainGround
{
    /// <summary>How far under a terrain's top a point must be to count as inside the ground (metres): floor resting on
    /// or just under the top (a brush floor the kit lifts a terrain 1/16 m above) stays out of it.</summary>
    public const double Tolerance = 0.05;

    private readonly List<(AlpineTerrain T, double X0, double Z0, double X1, double Z1)> _terrains;

    private TerrainGround(List<(AlpineTerrain, double, double, double, double)> terrains) => _terrains = terrains;

    /// <summary>How many terrains make this ground.</summary>
    public int Count => _terrains.Count;

    /// <summary>The ground of the terrains in <paramref name="rfl"/>, or null when it has none (the measurements then run exactly as before).</summary>
    public static TerrainGround? Of(RflFile? rfl)
    {
        if (rfl is null)
        {
            return null;
        }

        AlpineTerrainSection? section = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.AlpineTerrain)
            {
                section = s.Content as AlpineTerrainSection;
                if (s.Content is null && RflSectionRegistry.TryParse(s, rfl.Context, out IRflSectionContent? parsed))
                {
                    section = parsed as AlpineTerrainSection; // a private parse: the file's own sections stay as they are
                }

                break;
            }
        }

        return section is { Terrains.Count: > 0 } ? From(section.Terrains) : null;
    }

    /// <summary>The ground of <paramref name="terrains"/> (copied), or null when there are none.</summary>
    public static TerrainGround? From(IEnumerable<AlpineTerrain> terrains)
    {
        var list = new List<(AlpineTerrain, double, double, double, double)>();
        foreach (AlpineTerrain t in terrains)
        {
            if (t.Grid.CellsX < 1 || t.Grid.CellsZ < 1 || !(t.CellSize > 0))
            {
                continue;
            }

            AlpineTerrain c = t.DeepCopy();
            double x0 = c.Origin.X, z0 = c.Origin.Z;
            list.Add((c, x0, z0, x0 + (c.Grid.CellsX * (double)c.CellSize), z0 + (c.Grid.CellsZ * (double)c.CellSize)));
        }

        return list.Count == 0 ? null : new TerrainGround(list);
    }

    /// <summary>The highest terrain top at world (x, z) — null outside every footprint or over holes only.</summary>
    public double? SurfaceY(double x, double z)
    {
        double? best = null;
        foreach ((AlpineTerrain t, double x0, double z0, double x1, double z1) in _terrains)
        {
            if (x < x0 || x > x1 || z < z0 || z > z1)
            {
                continue;
            }

            if (TerrainOps.SurfaceAt(t, (float)x, (float)z) is float y && (best is null || y > best))
            {
                best = y;
            }
        }

        return best;
    }

    /// <summary>
    /// True when <paramref name="p"/> is inside the ground: more than <see cref="Tolerance"/> under the top of a terrain
    /// whose footprint holds it (any terrain, so a point under a hole of one terrain but under the top of another is too).
    /// </summary>
    public bool IsUnder(Vec3d p)
    {
        foreach ((AlpineTerrain t, double x0, double z0, double x1, double z1) in _terrains)
        {
            if (p.X < x0 || p.X > x1 || p.Z < z0 || p.Z > z1)
            {
                continue;
            }

            if (TerrainOps.SurfaceAt(t, (float)p.X, (float)p.Z) is float y && p.Y < y - Tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The terrains' world bounds (XZ), for reports.</summary>
    internal IEnumerable<(double X0, double Z0, double X1, double Z1)> Footprints() => _terrains.Select(t => (t.X0, t.Z0, t.X1, t.Z1));
}
