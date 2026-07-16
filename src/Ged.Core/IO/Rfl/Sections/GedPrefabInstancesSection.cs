using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// ged_prefab_instances (0x6ED00001): GED editor-only prefab-instance lineage records
/// Unknown to the game engines, which skip it as an opaque blob; GED parses
/// it to track which UIDs belong to which placed prefab so it can propagate prefab edits.
/// <para>
/// Layout: <c>u32 chunkVersion</c>, <c>u32 count</c>, then per record: <c>i32 instanceId</c>,
/// <c>vstring prefabName</c>, <c>vstring sourceHash</c>, <c>vec3 pivotPos</c>,
/// <c>mat3 pivotRot</c>, <c>u8 modified</c>, <c>u32 memberCount</c>, <c>i32[] memberUids</c>.
/// The chunk version lets the schema evolve without breaking old data (Alpine's pattern).
/// </para>
/// </summary>
public sealed class GedPrefabInstancesSection : IRflSectionContent
{
    public const uint CurrentVersion = 1;

    public SectionType Type => SectionType.GedPrefabInstances;

    public uint Version { get; set; } = CurrentVersion;

    public List<PrefabInstanceRecord> Instances { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new GedPrefabInstancesSection { Version = r.ReadU32() };
        int count = (int)r.ReadU32();
        for (int i = 0; i < count; i++)
        {
            var rec = new PrefabInstanceRecord
            {
                InstanceId = r.ReadI32(),
                PrefabName = r.ReadVString(),
                SourceHash = r.ReadVString(),
                PivotPosition = r.ReadVec3(),
                PivotRotation = r.ReadMat3(),
                Modified = r.ReadU8() != 0,
            };
            int members = (int)r.ReadU32();
            for (int m = 0; m < members; m++)
            {
                rec.MemberUids.Add(r.ReadI32());
            }

            section.Instances.Add(rec);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteU32(Version);
        w.WriteU32((uint)Instances.Count);
        foreach (PrefabInstanceRecord rec in Instances)
        {
            w.WriteI32(rec.InstanceId);
            w.WriteVString(rec.PrefabName);
            w.WriteVString(rec.SourceHash);
            w.WriteVec3(rec.PivotPosition);
            w.WriteMat3(rec.PivotRotation);
            w.WriteU8((byte)(rec.Modified ? 1 : 0));
            w.WriteU32((uint)rec.MemberUids.Count);
            foreach (int uid in rec.MemberUids)
            {
                w.WriteI32(uid);
            }
        }
    }
}
