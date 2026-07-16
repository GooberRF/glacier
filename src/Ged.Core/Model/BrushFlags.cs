namespace Ged.Core.Model;

/// <summary>
/// Brush flag bitfield (RFL <c>brush.flags</c>). Values match RED/REDUX:
/// portal, air (subtractive), detail, emits-steam, plus the Alpine "geoable"
/// bit. A brush is solid when <see cref="Air"/> is clear.
/// </summary>
[System.Flags]
public enum BrushFlags : uint
{
    None = 0x00,
    Portal = 0x01,
    Air = 0x02,
    Detail = 0x04,
    EmitsSteam = 0x10,

    /// <summary>[ALPINE] the brush participates in geomod destruction (auto-implies detail).</summary>
    Geoable = 0x20,
}

/// <summary>Brush editor state (RFL <c>brush.state</c>).</summary>
public static class BrushState
{
    public const int Normal = 0;
    public const int Locked = 2;
    public const int Selected = 3;
}
