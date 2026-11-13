namespace Ged.Core.Model;

/// <summary>
/// An Alpine vehicle factory (0x0AFBAE07; layout editor_patch/vehicle_factory.cpp:546-584): spawns a vehicle
/// class and respawns it after a delay. Values are kept as stored.
/// </summary>
public sealed class AlpineVehicleFactory
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Orientation { get; set; } = Mat3.Identity;

    public string ScriptName { get; set; } = string.Empty;

    public string VehicleClass { get; set; } = string.Empty;

    public float RespawnDelaySeconds { get; set; }

    /// <summary>0 red, 1 blue, 0xFF none.</summary>
    public byte Team { get; set; } = 0xFF;

    /// <summary>Turret yaw limit in degrees; 0 = none (Alpine reads values over 179 as 0).</summary>
    public byte TurretYawLimitDegrees { get; set; }

    /// <summary>The reserved byte between the yaw limit and the team lock; Alpine writes 0.</summary>
    public byte Reserved { get; set; }

    public byte LockToTeam { get; set; }

    public byte ActiveByDefault { get; set; } = 1;
}
