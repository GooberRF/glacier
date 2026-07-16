using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>The cookie-cutter shapes offered by the Brush mode panel.</summary>
public enum BrushShape
{
    Box,
    Cone,
    Cylinder,
    Sphere,
    Wedge,

    /// <summary>A single planar quad (glass / portal / decal brushes).</summary>
    Face,

    /// <summary>Geometry cut from a V3M/V3C mesh (Alpine mesh-cutter parity).</summary>
    Mesh,
}

/// <summary>
/// The parameters of the Brush-mode cookie cutter. Dimensions are full extents in
/// metres: Width = local X, Height = local Y (up), Depth = local Z. Splits are the
/// number of internal cuts along each axis for prism shapes; for radial shapes
/// (cylinder/cone/sphere) Width Splits is the radial side count and Height Splits
/// the vertical stack count (see <see cref="BrushFactory"/>).
/// </summary>
public sealed class BrushCreateParams
{
    /// <summary>
    /// The stock RED rock default texture, used as the single authoring texture and as
    /// the built-in fallback for every orientation preference when unset. This is the
    /// real stock texture that ships in the base VPPs (data\maps\textures\Rck_Default.tga,
    /// per docs/research/red-texture-categories.md); the previous "Rck_Default01.tga" did
    /// not exist in stock RF, so faces referencing it rendered untextured.
    /// </summary>
    public const string DefaultTexture = "Rck_Default.tga";

    /// <summary>
    /// Stock built-in orientation defaults (floor / wall / ceiling). RED's "Texture"
    /// preferences default all three to the same rock texture, and it is the only stock
    /// name confirmed present in the base VPPs, so the triple collapses to Rck_Default.tga.
    /// </summary>
    public const string StockFloorTexture = DefaultTexture;

    public const string StockWallTexture = DefaultTexture;

    public const string StockCeilingTexture = DefaultTexture;

    public BrushShape Shape { get; set; } = BrushShape.Box;

    public float Width { get; set; } = 4f;

    public float Height { get; set; } = 4f;

    public float Depth { get; set; } = 4f;

    public int WidthSplits { get; set; }

    public int HeightSplits { get; set; }

    public int DepthSplits { get; set; }

    public string Texture { get; set; } = DefaultTexture;

    /// <summary>
    /// Texture-preference defaults applied per face by orientation at creation
    /// (RED: floor faces point up, ceiling faces point down, walls are vertical).
    /// When all three are set they override <see cref="Texture"/>; null falls back
    /// to the single default texture.
    /// </summary>
    public string? FloorTexture { get; set; }

    public string? WallTexture { get; set; }

    public string? CeilingTexture { get; set; }

    /// <summary>True when the ceiling/wall/floor preference set should drive per-face textures.</summary>
    public bool HasOrientationTextures =>
        !string.IsNullOrEmpty(FloorTexture) && !string.IsNullOrEmpty(WallTexture) && !string.IsNullOrEmpty(CeilingTexture);

    /// <summary>
    /// The floor texture actually applied at creation: the floor preference when set,
    /// else the single authoring <see cref="Texture"/>, else the stock rock default.
    /// Never empty — this is why a fresh (blank-preference) settings file still produces
    /// textured brushes.
    /// </summary>
    public string EffectiveFloorTexture => Resolve(FloorTexture, StockFloorTexture);

    /// <summary>The wall texture actually applied at creation (see <see cref="EffectiveFloorTexture"/>).</summary>
    public string EffectiveWallTexture => Resolve(WallTexture, StockWallTexture);

    /// <summary>The ceiling texture actually applied at creation (see <see cref="EffectiveFloorTexture"/>).</summary>
    public string EffectiveCeilingTexture => Resolve(CeilingTexture, StockCeilingTexture);

    private string Resolve(string? preference, string stock) =>
        !string.IsNullOrEmpty(preference) ? preference
        : !string.IsNullOrEmpty(Texture) ? Texture
        : stock;

    /// <summary>Air brushes subtract at build; solid brushes add. Default solid.</summary>
    public bool Air { get; set; }

    public bool Portal { get; set; }

    public bool Detail { get; set; }

    public bool EmitsSteam { get; set; }

    /// <summary>[ALPINE] geomod-destructible; auto-implies detail.</summary>
    public bool Geoable { get; set; }

    /// <summary>-1 = infinite life.</summary>
    public int Life { get; set; } = -1;

    /// <summary>For <see cref="BrushShape.Mesh"/>: the VFS-relative mesh filename.</summary>
    public string? MeshFilename { get; set; }

    /// <summary>Combines the individual flag toggles into the RFL brush bitfield.</summary>
    public uint ToFlags()
    {
        BrushFlags flags = BrushFlags.None;
        if (Air)
        {
            flags |= BrushFlags.Air;
        }

        if (Portal)
        {
            flags |= BrushFlags.Portal;
        }

        if (Detail || Geoable)
        {
            flags |= BrushFlags.Detail;
        }

        if (EmitsSteam)
        {
            flags |= BrushFlags.EmitsSteam;
        }

        if (Geoable)
        {
            flags |= BrushFlags.Geoable;
        }

        return (uint)flags;
    }
}
