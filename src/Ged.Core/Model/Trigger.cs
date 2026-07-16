namespace Ged.Core.Model;

/// <summary>
/// A trigger (RFL <c>trigger</c>). When the script name's first byte is 0xAB,
/// the second byte carries Pure Faction / Alpine multiplayer flags; those bits
/// are exposed as read-only convenience properties while
/// <see cref="ScriptName"/> preserves the exact bytes.
/// </summary>
public sealed class Trigger
{
    public const int ShapeSphere = 0;
    public const int ShapeBox = 1;

    public int Uid { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public byte HiddenInEditor { get; set; }

    /// <summary>0 = sphere, 1 = box.</summary>
    public int Shape { get; set; }

    public float ResetsAfter { get; set; }

    /// <summary>-1 = infinite.</summary>
    public int ResetsTimes { get; set; }

    public byte IsUseKeyRequired { get; set; }

    public string KeyName { get; set; } = string.Empty;

    public byte WeaponActivates { get; set; }

    /// <summary>0 players, 1 all, 2 linked, 3 ai, 4 player vehicle, 5 geomods.</summary>
    public byte ActivatedBy { get; set; }

    public byte IsNpc { get; set; }

    public byte IsAuto { get; set; }

    public byte InVehicle { get; set; }

    public Vec3 Position { get; set; }

    /// <summary>Present for the sphere shape.</summary>
    public float? SphereRadius { get; set; }

    /// <summary>Present for the box shape.</summary>
    public Mat3? Rotation { get; set; }

    public float? BoxHeight { get; set; }

    public float? BoxWidth { get; set; }

    public float? BoxDepth { get; set; }

    /// <summary>Present for the box shape.</summary>
    public byte? OneWay { get; set; }

    public int AirlockRoomUid { get; set; }

    public int AttachedToUid { get; set; }

    public int UseClutterUid { get; set; }

    public byte Disabled { get; set; }

    public float ButtonActiveTimeSeconds { get; set; }

    public float InsideTimeSeconds { get; set; }

    /// <summary>Present for versions &gt;= 0xB1. -1 none, 0 team_1, 1 team_2.</summary>
    public int? Team { get; set; }

    public List<int> Links { get; set; } = new();

    /// <summary>True when the script name uses the 0xAB Pure Faction flag encoding.</summary>
    public bool IsPureFactionEncoded =>
        ScriptName.Length >= 2 && ScriptName[0] == '«';

    private int PfFlags => IsPureFactionEncoded ? ScriptName[1] : 0;

    /// <summary>PF flag 0x2: clientside trigger.</summary>
    public bool PfClientside => (PfFlags & 0x2) != 0;

    /// <summary>PF flag 0x4: solo trigger.</summary>
    public bool PfSolo => (PfFlags & 0x4) != 0;

    /// <summary>PF flag 0x8: teleport trigger.</summary>
    public bool PfTeleport => (PfFlags & 0x8) != 0;
}
