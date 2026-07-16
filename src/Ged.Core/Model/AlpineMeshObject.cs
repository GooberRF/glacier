namespace Ged.Core.Model;

/// <summary>An Alpine mesh object (alpine_mesh_objects, 0x0AFBAE01).</summary>
public sealed class AlpineMeshObject
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Orientation { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public string MeshFilename { get; set; } = string.Empty;

    public string StateAnim { get; set; } = string.Empty;

    /// <summary>0 None, 1 Only Weapons, 2 All.</summary>
    public byte CollisionMode { get; set; }

    public List<AlpineMeshTextureOverride> TextureOverrides { get; set; } = new();

    /// <summary>Material type for impact sounds (0-9).</summary>
    public int Material { get; set; }

    private byte _isClutter;

    /// <summary>
    /// Non-zero marks this mesh as clutter (destructible). Toggling it non-zero — including
    /// through the generic inspector grid, which sets this property by reflection — allocates a
    /// default <see cref="Clutter"/> block when one is absent, so the flag never travels without
    /// its behaviour data and the serializer cannot NRE.
    /// </summary>
    public byte IsClutter
    {
        get => _isClutter;
        set
        {
            _isClutter = value;
            if (value != 0)
            {
                Clutter ??= new AlpineMeshClutterInfo();
            }
        }
    }

    /// <summary>Present iff <see cref="IsClutter"/> != 0.</summary>
    public AlpineMeshClutterInfo? Clutter { get; set; }
}

/// <summary>A per-slot texture override on an Alpine mesh object.</summary>
public sealed class AlpineMeshTextureOverride
{
    public byte SlotId { get; set; }

    public string Filename { get; set; } = string.Empty;
}

/// <summary>
/// Clutter behaviour of an Alpine mesh object. Field defaults mirror Alpine's
/// <c>MeshClutterProps</c> struct (editor_patch/mfc_types.h:412-425): a freshly
/// flagged-clutter mesh starts invulnerable (Life -1), with unit explosion radius,
/// 10 m/s debris, unit damage factors, and an automatic-material All-collision corpse —
/// so toggling Is Clutter in the inspector yields the same block Alpine's dialog would.
/// </summary>
public sealed class AlpineMeshClutterInfo
{
    public float Life { get; set; } = -1f;

    public string DebrisFilename { get; set; } = string.Empty;

    public string ExplosionVclip { get; set; } = string.Empty;

    public float ExplosionRadius { get; set; } = 1f;

    public float DebrisVelocity { get; set; } = 10f;

    /// <summary>Damage multipliers for each of the 11 damage types (default 1.0 each).</summary>
    public float[] DamageTypeFactors { get; set; } = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };

    public string CorpseFilename { get; set; } = string.Empty;

    public string CorpseStateAnim { get; set; } = string.Empty;

    /// <summary>0 None, 1 Only Weapons, 2 All.</summary>
    public byte CorpseCollision { get; set; } = 2;

    /// <summary>-1 Automatic, 0-9 specific material.</summary>
    public sbyte CorpseMaterial { get; set; } = -1;
}
