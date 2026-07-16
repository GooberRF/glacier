namespace Ged.Core.Model;

/// <summary>
/// An AI/NPC entity (RFL <c>entity</c>). The full ~55-field layout is preserved,
/// including the three engine-unused unknown fields and every behavior flag, so
/// the record re-serializes losslessly.
/// </summary>
public sealed class Entity
{
    public int Uid { get; set; }

    public string ClassName { get; set; } = string.Empty;

    public Vec3 Position { get; set; }

    public Mat3 Rotation { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public byte HiddenInEditor { get; set; }

    public int Cooperation { get; set; }

    public int Friendliness { get; set; }

    public int TeamId { get; set; }

    public string WaypointList { get; set; } = string.Empty;

    public string WaypointMethod { get; set; } = string.Empty;

    public byte Unknown1 { get; set; }

    public byte Boarded { get; set; }

    public byte ReadyToFireState { get; set; }

    public byte OnlyAttackPlayer { get; set; }

    public byte WeaponIsHolstered { get; set; }

    public byte Deaf { get; set; }

    public int SweepMinAngle { get; set; }

    public int SweepMaxAngle { get; set; }

    public byte IgnoreTerrainWhenFiring { get; set; }

    public byte Unknown2 { get; set; }

    public byte StartCrouched { get; set; }

    public float Life { get; set; }

    public float Armor { get; set; }

    public int Fov { get; set; }

    public string DefaultPrimaryWeapon { get; set; } = string.Empty;

    public string DefaultSecondaryWeapon { get; set; } = string.Empty;

    public string ItemDrop { get; set; } = string.Empty;

    public string StateAnim { get; set; } = string.Empty;

    public string CorpsePose { get; set; } = string.Empty;

    public string Skin { get; set; } = string.Empty;

    public string DeathAnim { get; set; } = string.Empty;

    public byte AiMode { get; set; }

    public byte AiAttackStyle { get; set; }

    public int Unknown3 { get; set; }

    public int TurretUid { get; set; }

    public int AlertCameraUid { get; set; }

    public int AlarmEventUid { get; set; }

    public byte Run { get; set; }

    public byte StartHidden { get; set; }

    public byte WearHelmet { get; set; }

    public byte EndGameIfKilled { get; set; }

    public byte CowerFromWeapon { get; set; }

    public byte QuestionUnarmedPlayer { get; set; }

    public byte DontHum { get; set; }

    public byte NoShadow { get; set; }

    public byte AlwaysSimulate { get; set; }

    public byte PerfectAim { get; set; }

    public byte PermanentCorpse { get; set; }

    public byte NeverFly { get; set; }

    public byte NeverLeave { get; set; }

    public byte NoPersonaMessages { get; set; }

    public byte FadeCorpseImmediately { get; set; }

    public byte NeverCollideWithPlayer { get; set; }

    public byte UseCustomAttackRange { get; set; }

    /// <summary>Present iff <see cref="UseCustomAttackRange"/> == 1.</summary>
    public float? CustomAttackRange { get; set; }

    public string LeftHandHolding { get; set; } = string.Empty;

    public string RightHandHolding { get; set; } = string.Empty;
}
