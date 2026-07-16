using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// level_properties (0x900): geomod texture, hardness, ambient/fog. RF2-only
/// baking fields (version 0x127) are out of scope and not represented.
/// </summary>
public sealed class LevelPropertiesSection : IRflSectionContent
{
    public SectionType Type => SectionType.LevelProperties;

    public string GeomodTexture { get; set; } = string.Empty;

    public int Hardness { get; set; }

    public RfColor AmbientColor { get; set; }

    /// <summary>directional_ambient_light (always 0 from the editor); preserved exactly.</summary>
    public byte DirectionalAmbientLight { get; set; }

    public RfColor FogColor { get; set; }

    public float FogNearPlane { get; set; }

    public float FogFarPlane { get; set; }

    /// <summary>
    /// The defaults a fresh level gets (File &gt; New). Verified against RED.exe's level
    /// constructor (0x0041CAB0): geomod texture "rock02.tga", hardness 0x32 = 50, ambient
    /// (40,40,40,255) — matching stock RED and the RED wiki, and dm01's own values. Fog is
    /// black (the Alpine editor_patch defaults level fog to flat black; stock RED wrote
    /// (40,40,40)). Directional ambient and the PC fog planes are 0.
    /// </summary>
    public static LevelPropertiesSection CreateDefault() => new()
    {
        GeomodTexture = "rock02.tga",
        Hardness = 50,
        AmbientColor = new RfColor(40, 40, 40, 255),
        DirectionalAmbientLight = 0,
        FogColor = new RfColor(0, 0, 0, 255),
        FogNearPlane = 0f,
        FogFarPlane = 0f,
    };

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) => new LevelPropertiesSection
    {
        GeomodTexture = r.ReadVString(),
        Hardness = r.ReadI32(),
        AmbientColor = r.ReadColor(),
        DirectionalAmbientLight = r.ReadU8(),
        FogColor = r.ReadColor(),
        FogNearPlane = r.ReadF32(),
        FogFarPlane = r.ReadF32(),
    };

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteVString(GeomodTexture);
        w.WriteI32(Hardness);
        w.WriteColor(AmbientColor);
        w.WriteU8(DirectionalAmbientLight);
        w.WriteColor(FogColor);
        w.WriteF32(FogNearPlane);
        w.WriteF32(FogFarPlane);
    }
}
