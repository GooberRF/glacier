using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Spatial;

namespace Ged.Core.Linting;

/// <summary>
/// <see cref="LintCategory.SpawnHeight"/>: a multiplayer respawn point, the Player Start or a non-Air waypoint helper
/// less than <see cref="SpawnClearance.MinHeight"/> above the walkable floor below it, or any of them (Air helpers
/// included) inside solid. Measured on the compiled geometry with the spatial point queries (an unbuilt level is not
/// checked); faces the sampler leaves out — portals, sky rooms, liquid surfaces — are not floors. A built Alpine terrain's
/// top is floor like any other compiled face, and a point under it is inside the ground (<see cref="TerrainGround"/>).
/// </summary>
internal static class SpawnHeightCheck
{
    public static void Run(RflFile rfl, List<LevelObject> objects, List<LintFinding> findings)
    {
        List<LevelObject> covered = objects.Where(o => SpawnClearance.Checked(o.Kind) && !o.IsReadOnly).ToList();
        if (covered.Count == 0)
        {
            return;
        }

        Geometry? geometry = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault()?.Geometry;
        if (geometry is not { Faces.Count: > 0 })
        {
            return; // unbuilt: the missing-geometry lint already speaks for the level
        }

        SpatialWorld world = SpatialWorld.For(geometry);
        TerrainGround? ground = TerrainGround.Of(rfl);
        foreach (LevelObject o in covered)
        {
            Vec3 p = o.Position;
            var at = new Vec3d(p.X, p.Y, p.Z);
            string name = SpawnClearance.Describe(o.Kind, o.Model);
            if (ground is not null && ground.IsUnder(at))
            {
                // A built terrain is floor, and what lies under its top is ground (TerrainGround): a terrain sheet has
                // no face looking down, so the rays below would read the space under it as open.
                findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.SpawnHeight,
                    $"{name} is under the terrain's surface, inside the ground. Move it above the terrain"
                    + (SpawnClearance.StandsOnFloor(o.Kind, o.Model) ? ", about 1 m above its surface." : "."),
                    Uid: Uid(o), Location: p));
                continue;
            }

            if (world.IsInSolid(at))
            {
                findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.SpawnHeight,
                    $"{name} is inside solid geometry, outside every room. Move it into open space"
                    + (SpawnClearance.StandsOnFloor(o.Kind, o.Model) ? ", about 1 m above the floor." : "."),
                    Uid: Uid(o), Location: p));
                continue;
            }

            if (!SpawnClearance.StandsOnFloor(o.Kind, o.Model))
            {
                if (world.HeightAboveWalkableFloor(at) is double airGap && airGap < SpawnClearance.MinHeight)
                {
                    findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.SpawnHeight,
                        $"{name} sits {Metres(airGap)} above the floor. Air helpers belong in open air; raise it or make it a Ground helper.",
                        Uid: Uid(o), Location: p));
                }

                continue;
            }

            if (world.HeightAboveWalkableFloor(at) is double gap && gap < SpawnClearance.MinHeight)
            {
                findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.SpawnHeight,
                    $"{name} sits {Metres(gap)} above the floor. Place it about 1 m up so whatever spawns there starts clear of the floor.",
                    Uid: Uid(o), Location: p));
            }
        }
    }

    // The Player Start's LevelObject carries the 0 sentinel, which the editor's jump-to resolves to it.
    private static int? Uid(LevelObject o) => o.Uid;

    private static string Metres(double v) => (v < 0.005 ? 0 : v).ToString("0.##", CultureInfo.InvariantCulture) + " m";
}
