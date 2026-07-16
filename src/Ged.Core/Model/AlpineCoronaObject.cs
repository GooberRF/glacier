namespace Ged.Core.Model;

/// <summary>An Alpine corona object (alpine_corona_objects, 0x0AFBAE03).</summary>
public sealed class AlpineCoronaObject
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Orientation { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public byte ColorR { get; set; }

    public byte ColorG { get; set; }

    public byte ColorB { get; set; }

    public byte ColorA { get; set; }

    public string CoronaBitmap { get; set; } = string.Empty;

    /// <summary>Degrees (multiplied by 0.5 at runtime).</summary>
    public float ConeAngle { get; set; }

    /// <summary>
    /// Whether this corona has a meaningful facing direction (so the editor draws a facing
    /// arrow along <see cref="Orientation"/>'s forward vector). <see cref="ConeAngle"/> is the
    /// FULL visibility cone in degrees; effects.tbl documents "360 degrees for all angle
    /// visibility" — i.e. an omnidirectional glare with no single facing. So a corona is
    /// directional only for a real cone (0 &lt; angle &lt; 360); at 360° or wider it is
    /// omnidirectional (no arrow), and a non-positive angle is degenerate/unset (no arrow).
    /// </summary>
    public bool IsDirectional => ConeAngle > 0f && ConeAngle < 360f;

    public float Intensity { get; set; }

    public float RadiusDistance { get; set; }

    public float RadiusScale { get; set; }

    public float DiminishDistance { get; set; }

    public string VolumetricBitmap { get; set; } = string.Empty;

    /// <summary>Present iff <see cref="VolumetricBitmap"/> is non-empty.</summary>
    public float? VolumetricHeight { get; set; }

    public float? VolumetricLength { get; set; }
}
