using System;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The stand-off rule for the objects something spawns or drives from: multiplayer respawn points, the Player Start
/// and non-Air vehicle waypoint helpers sit about <see cref="StandHeight"/> above the floor, so a player (or a vehicle)
/// is created clear of it rather than intersecting it; Air waypoint helpers belong in open air. Placement on a floor
/// lifts these kinds by <see cref="StandHeight"/> along the floor normal (<see cref="Lift"/>), and the linter warns when
/// one sits less than <see cref="MinHeight"/> above a walkable floor or inside solid
/// (<see cref="Linting.LevelLinter"/>). Scripted placement keeps literal coordinates; the lint reports them.
/// </summary>
public static class SpawnClearance
{
    /// <summary>How far above the floor these objects are placed (metres).</summary>
    public const float StandHeight = 1.0f;

    /// <summary>The linter warns below this height above a walkable floor (metres).</summary>
    public const float MinHeight = 0.5f;

    /// <summary>A hit face counts as floor when its unit normal's Y is at least this (the walkable slope).</summary>
    public const float FloorNormalY = 0.70f;

    /// <summary>The waypoint helper type that flies (0 air, 1 water, 2 ground).</summary>
    public const byte AirHelperType = 0;

    /// <summary>
    /// True for the kinds the rule covers: MP respawn points, the Player Start, and waypoint helpers that are not Air
    /// (<paramref name="model"/> is the existing object, or null for a new one — a new helper starts as Ground).
    /// </summary>
    public static bool StandsOnFloor(LevelObjectKind kind, object? model = null) => kind switch
    {
        LevelObjectKind.MpRespawnPoint or LevelObjectKind.PlayerStart => true,
        LevelObjectKind.WaypointHelper => model is not AlpineWaypointHelper { HelperType: AirHelperType },
        _ => false,
    };

    /// <summary>True for kinds the rule checks at all (the floor-standing kinds plus Air waypoint helpers, which only must not be buried).</summary>
    public static bool Checked(LevelObjectKind kind) =>
        kind is LevelObjectKind.MpRespawnPoint or LevelObjectKind.PlayerStart or LevelObjectKind.WaypointHelper;

    /// <summary>True when <paramref name="normal"/> is a walkable floor's (unit length not required).</summary>
    public static bool IsFloor(Vec3 normal)
    {
        float len = normal.Length();
        return len > 1e-6f && normal.Y / len >= FloorNormalY;
    }

    /// <summary>
    /// The placement point for a floor-standing kind dropped on a face hit: <see cref="StandHeight"/> along the face
    /// normal when the face is a floor, else the hit itself (a wall or ceiling hit is left alone).
    /// </summary>
    public static Vec3 Lift(Vec3 hit, Vec3 normal)
    {
        if (!IsFloor(normal))
        {
            return hit;
        }

        float len = normal.Length();
        return hit.Add(normal.Scale(StandHeight / len));
    }

    /// <summary>The user-facing name of a covered object, for messages.</summary>
    public static string Describe(LevelObjectKind kind, object? model) => kind switch
    {
        LevelObjectKind.MpRespawnPoint => "Multiplayer respawn point",
        LevelObjectKind.PlayerStart => "The Player Start",
        LevelObjectKind.WaypointHelper when model is AlpineWaypointHelper { HelperType: AirHelperType } => "Air waypoint helper",
        LevelObjectKind.WaypointHelper => "Waypoint helper",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
