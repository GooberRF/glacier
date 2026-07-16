namespace Ged.Core.Model;

/// <summary>A light object (RFL <c>light</c>).</summary>
public sealed class Light
{
    public int Uid { get; set; }

    public string ClassName { get; set; } = "Light";

    public Vec3 Position { get; set; }

    public Mat3 Rotation { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public byte HiddenInEditor { get; set; }

    /// <summary>32-bit light_flags bitfield (type, state, dynamic, shadows, ...).</summary>
    public uint Flags { get; set; }

    /// <summary>
    /// Editor-only "Always Show Range" flag (light_flags bit 0x80): when set, the
    /// light's range sphere is drawn even while the light is unselected and the global
    /// "Show all ranges" toggle is off. Matches the stock inspector flag of the same
    /// name.
    /// </summary>
    public bool AlwaysShowRange => (Flags & 0x80u) != 0;

    public RfColor Color { get; set; }

    public float Range { get; set; }

    public float Fov { get; set; }

    public float FovDropoff { get; set; }

    public float IntensityAtMaxRange { get; set; }

    public int DropoffType { get; set; }

    public float TubeLightWidth { get; set; }

    public float OnIntensity { get; set; }

    public float OnTime { get; set; }

    public float OnTimeVariation { get; set; }

    public float OffIntensity { get; set; }

    public float OffTime { get; set; }

    public float OffTimeVariation { get; set; }
}
