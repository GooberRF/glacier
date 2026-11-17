using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Spatial;

/// <summary>
/// Point queries on a level's compiled geometry (AI-SUITE-PLAN §5.3, DESIGN-QUALITY-SPEC §(e) E2): raycast, line of
/// sight, point info and clearance, answered by the face BVH alone (no floor lattice), so they are instant. The same
/// faces as the measurements: portal faces, sky-room faces and liquid surfaces are left out. Thread-safe.
/// </summary>
public sealed class SpatialWorld
{
    /// <summary>Player body dimensions (Alpine game_patch/misc/waypoints.h:43-45: wall clearance, minimum and standing headroom).</summary>
    public const double PlayerRadius = 0.5, CrouchHeight = 1.375, StandingHeight = 2.0;

    private static readonly ConditionalWeakTable<Geometry, SpatialWorld> Cache = new();

    private readonly SpaceSampler _sp;
    private readonly Geometry _geo;

    private SpatialWorld(Geometry geo)
    {
        _geo = geo;
        _sp = SpaceSampler.Build(geo, System.Threading.CancellationToken.None);
    }

    /// <summary>The world of <paramref name="geometry"/> (built once per geometry instance).</summary>
    public static SpatialWorld For(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return Cache.GetValue(geometry, g => new SpatialWorld(g));
    }

    /// <summary>The faces kept for queries.</summary>
    public int FaceCount => _sp.FaceCount;

    /// <summary>The face set and BVH behind the queries (the craft audit casts its own rays on it).</summary>
    internal SpaceSampler Sampler => _sp;

    /// <summary>
    /// The first face along <paramref name="direction"/> from <paramref name="origin"/> within <paramref name="maxDistance"/>:
    /// <c>{hit, distance, pos, normal, face, room, texture, sky}</c>, or <c>{hit = false}</c>.
    /// </summary>
    public JsonObject Raycast(Vec3d origin, Vec3d direction, double maxDistance = 200)
    {
        double len = direction.Length;
        if (!(len > 1e-9) || !double.IsFinite(len))
        {
            throw new SpatialException("usage", "The ray direction is zero.", "Pass a direction such as {1, 0, 0}.");
        }

        Vec3d d = direction.Scale(1 / len);
        if (_sp.Cast(origin, d, maxDistance, SpaceSampler.HitFilter.Any, out int fi) is not double t || fi < 0)
        {
            return new JsonObject { ["hit"] = false, ["max_distance"] = maxDistance };
        }

        SpaceSampler.SFace f = _sp.Faces[fi];
        Face face = _geo.Faces[f.Source];
        return new JsonObject
        {
            ["hit"] = true,
            ["distance"] = Math.Round(t, 3),
            ["pos"] = SpatialAnalysis.P3(origin.Add(d.Scale(t))),
            ["normal"] = new JsonArray(Math.Round(f.N.X, 4), Math.Round(f.N.Y, 4), Math.Round(f.N.Z, 4)),
            ["back_face"] = f.N.Dot(d) > 0,
            ["face"] = f.Source,
            ["room"] = face.RoomIndex,
            ["texture"] = face.Texture >= 0 && face.Texture < _geo.Textures.Count ? _geo.Textures[face.Texture] : null,
            ["sky"] = (face.Flags & (ushort)FaceFlags.ShowSky) != 0,
        };
    }

    /// <summary>True when nothing blocks the segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public bool LineOfSight(Vec3d a, Vec3d b)
    {
        Vec3d d = b.Sub(a);
        double len = d.Length;
        return len < 1e-3 || !_sp.Blocked(a, d.Scale(1 / len), len - 0.01, SpaceSampler.HitFilter.Any);
    }

    /// <summary>
    /// What is at a point: <c>in_solid</c> (outside every room: most of the 6 axis rays that meet a face meet it from
    /// behind, or none meets one; or under a terrain's top when <paramref name="ground"/> is given, also <c>under_terrain</c>), the floor below
    /// (height, distance, face room), the ceiling above (or open sky), headroom, and the room the floor or ceiling belongs to.
    /// </summary>
    public JsonObject PointInfo(Vec3d p, TerrainGround? ground = null)
    {
        (int hits, int behind) = AxisRays(p);
        bool underTerrain = ground is { } g && g.IsUnder(p);
        double? down = _sp.Cast(p, new Vec3d(0, -1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.UpFacing, out int ff);
        double? up = _sp.Cast(p, new Vec3d(0, 1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.DownFacing, out int fc);
        bool sky = up is null || (fc >= 0 && (_geo.Faces[_sp.Faces[fc].Source].Flags & (ushort)FaceFlags.ShowSky) != 0);
        int room = ff >= 0 ? _geo.Faces[_sp.Faces[ff].Source].RoomIndex : fc >= 0 ? _geo.Faces[_sp.Faces[fc].Source].RoomIndex : -1;
        var info = new JsonObject
        {
            ["pos"] = SpatialAnalysis.P3(p),
            ["in_solid"] = underTerrain || hits == 0 || behind * 2 > hits,
            ["enclosed_rays"] = hits,
            ["floor"] = down is double dn ? new JsonObject { ["y"] = Math.Round(p.Y - dn, 3), ["distance"] = Math.Round(dn, 3), ["face"] = _sp.Faces[ff].Source } : null,
            ["ceiling"] = up is double u ? new JsonObject { ["y"] = Math.Round(p.Y + u, 3), ["distance"] = Math.Round(u, 3), ["face"] = _sp.Faces[fc].Source } : null,
            ["headroom"] = down is double d1 && up is double u1 ? Math.Round(d1 + u1, 3) : null,
            ["open_sky"] = sky,
            ["room"] = room,
        };
        if (ground is not null)
        {
            info["under_terrain"] = underTerrain; // (only levels with terrain carry the field)
        }

        return info;
    }

    /// <summary>
    /// True when <paramref name="p"/> is inside solid: the first face straight below it is met from behind (the point is
    /// inside a brush or under the world's skin). Only with no face below at all does it fall back to the
    /// <see cref="PointInfo"/> six-ray vote. Looking down first keeps an open arena honest: there the horizontal rays fly
    /// out through the openings and meet the level's outer hull from behind, which outvotes the floor under the point.
    /// </summary>
    public bool IsInSolid(Vec3d p, TerrainGround? ground = null)
    {
        if (ground is { } g && g.IsUnder(p))
        {
            return true; // under a terrain's top: inside the ground (a terrain sheet has no face that looks down at it)
        }

        // A face met from the front means open space above it. A detail face met from behind is passed through: detail
        // brushes do not bound rooms, and single-sided detail (a grate, a collision sheet) is legitimately seen from below.
        Vec3d down = new(0, -1, 0);
        Vec3d o = p;
        for (int step = 0; step < 16; step++)
        {
            if (_sp.Cast(o, down, SpaceSampler.SkyCap, SpaceSampler.HitFilter.Any, out int fi) is not double t || fi < 0)
            {
                break;
            }

            SpaceSampler.SFace f = _sp.Faces[fi];
            bool fromBehind = f.N.Dot(down) > 0;
            if (!fromBehind || (_geo.Faces[f.Source].Flags & (ushort)FaceFlags.IsDetail) == 0)
            {
                return fromBehind;
            }

            o = o.Add(down.Scale(t + 0.01));
        }

        (int hits, int behind) = AxisRays(p);
        return hits == 0 || behind * 2 > hits;
    }

    /// <summary>
    /// The height of <paramref name="p"/> above the walkable floor straight below it (a face whose normal is within the
    /// sampler's walkable slope, <see cref="SpaceSampler.FloorNormalY"/>), looking at most <paramref name="maxDrop"/> down;
    /// null when there is none. A point resting exactly on the floor measures 0 (the ray starts 5 cm above it).
    /// </summary>
    public double? HeightAboveWalkableFloor(Vec3d p, double maxDrop = 4)
    {
        const double lift = 0.05;
        if (_sp.Cast(p.Add(new Vec3d(0, lift, 0)), new Vec3d(0, -1, 0), maxDrop + lift, SpaceSampler.HitFilter.UpFacing, out int fi) is not double t
            || fi < 0 || _sp.Faces[fi].N.Y < SpaceSampler.FloorNormalY)
        {
            return null;
        }

        return t - lift;
    }

    private (int Hits, int Behind) AxisRays(Vec3d p)
    {
        Vec3d[] axes = { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
        int behind = 0, hits = 0;
        foreach (Vec3d a in axes)
        {
            if (_sp.Cast(p, a, SpaceSampler.SkyCap, SpaceSampler.HitFilter.Any, out int fi) is not null && fi >= 0)
            {
                hits++;
                if (_sp.Faces[fi].N.Dot(a) > 0)
                {
                    behind++;
                }
            }
        }

        return (hits, behind);
    }

    /// <summary>
    /// Whether a player fits at a floor point: the profile's height (player 2.0 m, crouch 1.375 m) of headroom above the
    /// floor under <paramref name="p"/>, and no wall within the 0.5 m body radius at knee, waist and head height (16
    /// directions). Reports the floor, headroom and nearest wall distance.
    /// </summary>
    public JsonObject Clearance(Vec3d p, string profile = "player")
    {
        double height = (profile ?? "player").ToLowerInvariant() switch
        {
            "player" or "stand" or "standing" => StandingHeight,
            "crouch" => CrouchHeight,
            string other => throw new SpatialException("usage", $"Unknown clearance profile '{other}'.", "Use player or crouch."),
        };
        double? down = _sp.Cast(p.Add(new Vec3d(0, 0.05, 0)), new Vec3d(0, -1, 0), 4, SpaceSampler.HitFilter.UpFacing);
        double floorY = down is double dn ? p.Y + 0.05 - dn : p.Y;
        var floor = new Vec3d(p.X, floorY + 0.005, p.Z);
        double headroom = _sp.Cast(floor, new Vec3d(0, 1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.DownFacing) is double h ? h + 0.005 : double.PositiveInfinity;
        double nearest = double.PositiveInfinity;
        foreach (double y in new[] { 0.4, height / 2, height - 0.1 })
        {
            Vec3d o = new(p.X, floorY + y, p.Z);
            foreach (Vec3d dir in SpaceSampler.Directions)
            {
                if (_sp.Cast(o, dir, 10, SpaceSampler.HitFilter.Any) is double t)
                {
                    nearest = Math.Min(nearest, t);
                }
            }
        }

        bool fits = down is not null && headroom >= height && nearest >= PlayerRadius;
        return new JsonObject
        {
            ["pos"] = SpatialAnalysis.P3(p),
            ["profile"] = profile,
            ["height"] = height,
            ["radius"] = PlayerRadius,
            ["fits"] = fits,
            ["floor_y"] = down is not null ? Math.Round(floorY, 3) : null,
            ["headroom"] = double.IsFinite(headroom) ? Math.Round(headroom, 3) : null,
            ["open_sky"] = !double.IsFinite(headroom),
            ["nearest_wall"] = double.IsFinite(nearest) ? Math.Round(nearest, 3) : null,
            ["reason"] = fits ? null : down is null ? "no floor within 4 m below" : headroom < height ? FormattableString.Invariant($"headroom {headroom:0.##} m is under {height} m") : FormattableString.Invariant($"a wall is {nearest:0.##} m away (body radius {PlayerRadius} m)"),
        };
    }
}
