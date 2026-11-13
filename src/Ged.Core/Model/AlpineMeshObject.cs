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

    /// <summary>
    /// RFL 306 flag block (block A, one byte per mesh after the records; editor_patch/mesh.cpp:356-359, 491-498):
    /// non-zero keeps this mesh out of the baked shadow casters.
    /// </summary>
    public byte NoShadowCast { get; set; }

    /// <summary>
    /// RFL 306 geometry block (block B; mesh.cpp:361-366, 500-516): for collision mode 3, which geometry collides —
    /// 0 highest LOD, 1 lowest LOD, 2 <see cref="CollisionMeshFilename"/> (editor_patch/mfc_types.h:720; Alpine reads
    /// other values as 0).
    /// </summary>
    public byte BrushGeoSource { get; set; }

    /// <summary>RFL 306 geometry block: the static .v3m collided instead of the render mesh (source 2), or empty.</summary>
    public string CollisionMeshFilename { get; set; } = string.Empty;

    /// <summary>RFL 306 geometry block: uniform draw scale (default 1).</summary>
    public float DrawScale { get; set; } = 1f;

    /// <summary>
    /// Serializer bookkeeping: how many of the record's trailing optional fields (material,
    /// is-clutter flag, then the four corpse fields) the loaded chunk stored, when it stored
    /// fewer than all of them — older Alpine layouts end a chunk's last record early and Alpine
    /// reads the missing fields as defaults. Null means the full layout.
    /// </summary>
    internal int? StoredOptionalFields { get; set; }
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
