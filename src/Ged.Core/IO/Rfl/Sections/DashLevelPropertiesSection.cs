namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// dash_level_properties (0xDA58FA00): Dash Faction level flags. Chunk version 1
/// carries the lightmaps_full_depth byte.
/// </summary>
public sealed class DashLevelPropertiesSection : IRflSectionContent
{
    public SectionType Type => SectionType.DashLevelProperties;

    public uint Version { get; set; }

    /// <summary>Present iff chunk version == 1.</summary>
    public byte? LightmapsFullDepth { get; set; }

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new DashLevelPropertiesSection { Version = r.ReadU32() };
        if (section.Version == 1)
        {
            section.LightmapsFullDepth = r.ReadU8();
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteU32(Version);
        if (Version == 1)
        {
            w.WriteU8(LightmapsFullDepth ?? 0);
        }
    }
}
