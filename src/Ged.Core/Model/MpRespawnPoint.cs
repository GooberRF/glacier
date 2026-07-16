namespace Ged.Core.Model;

/// <summary>A multiplayer respawn point (RFL <c>mp_respawn_point</c>).</summary>
public sealed class MpRespawnPoint
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Rotation { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public byte HiddenInEditor { get; set; }

    public int Team { get; set; }

    public byte RedTeam { get; set; }

    public byte BlueTeam { get; set; }

    public byte Bot { get; set; }
}
