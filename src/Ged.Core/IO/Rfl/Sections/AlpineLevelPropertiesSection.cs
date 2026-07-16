using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_level_properties (0x0AFBA5ED): Alpine-specific level flags plus the
/// geoable / breakable / hold-open tables. Uses an internal chunk version
/// (1-4) that gates the fields, independent of the file version.
/// </summary>
public sealed class AlpineLevelPropertiesSection : IRflSectionContent
{
    public SectionType Type => SectionType.AlpineLevelProperties;

    /// <summary>Chunk version (currently up to 4).</summary>
    public uint Version { get; set; }

    public byte LegacyCyclicTimers { get; set; }

    public byte LegacyMovers { get; set; }

    public byte StartsWithHeadlamp { get; set; }

    public byte OverrideStaticMeshAmbientLightModifier { get; set; }

    public float StaticMeshAmbientLightModifier { get; set; }

    public byte Rf2StyleGeomod { get; set; }

    public List<AlpineGeoableEntry> GeoableEntries { get; set; } = new();

    public List<AlpineBreakableEntry> BreakableEntries { get; set; } = new();

    public List<int> HoldOpenKeyframeUids { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new AlpineLevelPropertiesSection { Version = r.ReadU32() };

        if (section.Version >= 1)
        {
            section.LegacyCyclicTimers = r.ReadU8();
        }

        if (section.Version >= 2)
        {
            section.LegacyMovers = r.ReadU8();
            section.StartsWithHeadlamp = r.ReadU8();
        }

        if (section.Version >= 3)
        {
            section.OverrideStaticMeshAmbientLightModifier = r.ReadU8();
            section.StaticMeshAmbientLightModifier = r.ReadF32();
        }

        if (section.Version >= 4)
        {
            section.Rf2StyleGeomod = r.ReadU8();

            int numGeoable = (int)r.ReadU32();
            for (int i = 0; i < numGeoable; i++)
            {
                section.GeoableEntries.Add(new AlpineGeoableEntry
                {
                    BrushUid = r.ReadI32(),
                    RoomUid = r.ReadI32(),
                });
            }

            int numBreakable = (int)r.ReadU32();
            for (int i = 0; i < numBreakable; i++)
            {
                section.BreakableEntries.Add(new AlpineBreakableEntry
                {
                    BrushUid = r.ReadI32(),
                    RoomUid = r.ReadI32(),
                    Material = r.ReadU8(),
                });
            }

            int numHoldOpen = (int)r.ReadU32();
            for (int i = 0; i < numHoldOpen; i++)
            {
                section.HoldOpenKeyframeUids.Add(r.ReadI32());
            }
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteU32(Version);

        if (Version >= 1)
        {
            w.WriteU8(LegacyCyclicTimers);
        }

        if (Version >= 2)
        {
            w.WriteU8(LegacyMovers);
            w.WriteU8(StartsWithHeadlamp);
        }

        if (Version >= 3)
        {
            w.WriteU8(OverrideStaticMeshAmbientLightModifier);
            w.WriteF32(StaticMeshAmbientLightModifier);
        }

        if (Version >= 4)
        {
            w.WriteU8(Rf2StyleGeomod);

            w.WriteU32((uint)GeoableEntries.Count);
            foreach (AlpineGeoableEntry e in GeoableEntries)
            {
                w.WriteI32(e.BrushUid);
                w.WriteI32(e.RoomUid);
            }

            w.WriteU32((uint)BreakableEntries.Count);
            foreach (AlpineBreakableEntry e in BreakableEntries)
            {
                w.WriteI32(e.BrushUid);
                w.WriteI32(e.RoomUid);
                w.WriteU8(e.Material);
            }

            w.WriteU32((uint)HoldOpenKeyframeUids.Count);
            foreach (int uid in HoldOpenKeyframeUids)
            {
                w.WriteI32(uid);
            }
        }
    }
}
