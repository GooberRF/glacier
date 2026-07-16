using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// ged_object_metadata (0x6ED00002): GED editor-only, general-purpose per-object metadata
/// (item 4). Unknown to RED/RF/Alpine, which skip it as an opaque round-tripped blob; GED
/// parses it to attach extensible, typed metadata blocks to objects by UID (the first user is
/// light projection cookies, <see cref="GedMetadataType.LightCookie"/>).
/// <para>
/// Layout: <c>u32 chunkVersion</c>, <c>u32 entryCount</c>, then per entry:
/// <c>i32 uid</c>, <c>u32 blockCount</c>, then per block: <c>u32 metadataType</c>,
/// <c>u32 byteLength</c>, <c>byte[byteLength] payload</c>. A block whose type GED does not know
/// is kept verbatim as opaque payload bytes (forward compatibility), and the whole chunk is
/// written ONLY when at least one entry exists, so a level with no metadata stays byte-identical
/// on a no-op save.
/// </para>
/// </summary>
public sealed class GedObjectMetadataSection : IRflSectionContent
{
    public const uint CurrentVersion = 1;

    public SectionType Type => SectionType.GedObjectMetadata;

    public uint Version { get; set; } = CurrentVersion;

    public List<GedObjectMetadataRecord> Entries { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new GedObjectMetadataSection { Version = r.ReadU32() };
        int count = (int)r.ReadU32();
        for (int i = 0; i < count; i++)
        {
            var rec = new GedObjectMetadataRecord { Uid = r.ReadI32() };
            int blocks = (int)r.ReadU32();
            for (int b = 0; b < blocks; b++)
            {
                uint type = r.ReadU32();
                int len = (int)r.ReadU32();
                rec.Blocks.Add(new GedObjectMetadataBlock { MetadataType = type, Payload = r.ReadBytes(len) });
            }

            section.Entries.Add(rec);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteU32(Version);
        w.WriteU32((uint)Entries.Count);
        foreach (GedObjectMetadataRecord rec in Entries)
        {
            w.WriteI32(rec.Uid);
            w.WriteU32((uint)rec.Blocks.Count);
            foreach (GedObjectMetadataBlock block in rec.Blocks)
            {
                w.WriteU32(block.MetadataType);
                w.WriteU32((uint)block.Payload.Length);
                w.WriteBytes(block.Payload);
            }
        }
    }
}
