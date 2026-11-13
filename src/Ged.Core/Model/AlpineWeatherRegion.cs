namespace Ged.Core.Model;

/// <summary>
/// An Alpine weather region (0x0AFBAE06; layout editor_patch/weather_region.cpp:48-112): a box or cylinder
/// volume that rains or snows. Values are kept as stored.
/// </summary>
public sealed class AlpineWeatherRegion
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Orientation { get; set; } = Mat3.Identity;

    public string ScriptName { get; set; } = string.Empty;

    /// <summary>0 = box, 1 = cylinder (Alpine WeatherRegionShape).</summary>
    public int Shape { get; set; }

    /// <summary>0 = rain, 1 = snow (Alpine WeatherRegionType).</summary>
    public int WeatherType { get; set; }

    public float Width { get; set; }

    public float Height { get; set; }

    public float Depth { get; set; }

    public float Radius { get; set; }

    public float DensityScale { get; set; }

    public RfColor RainColor { get; set; }

    public float RainFallSpeed { get; set; }

    public float RainWindX { get; set; }

    public float RainWindZ { get; set; }

    public float RainStreakSeconds { get; set; }

    public RfColor SnowColor { get; set; }

    public float SnowFallSpeed { get; set; }

    public float SnowSwayAmplitude { get; set; }

    public float SnowSwaySpeed { get; set; }

    public float SnowSpriteRadius { get; set; }

    public string SnowBitmap { get; set; } = string.Empty;

    /// <summary>Editor display flag (draw the volume even when unselected).</summary>
    public byte AlwaysShowRange { get; set; }

    public byte InitiallyEnabled { get; set; }

    /// <summary>0 = automatic.</summary>
    public float ActiveDistance { get; set; }

    /// <summary>0 = automatic.</summary>
    public float VisibleDistance { get; set; }

    public byte BlockByGeometry { get; set; }

    public float ColumnWidth { get; set; }
}
