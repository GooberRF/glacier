using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>mp_respawn_points (0x700).</summary>
public sealed class MpRespawnPointsSection : IRflSectionContent
{
    public SectionType Type => SectionType.MpRespawnPoints;

    public List<MpRespawnPoint> Points { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new MpRespawnPointsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Points.Add(new MpRespawnPoint
            {
                Uid = r.ReadI32(),
                Position = r.ReadVec3(),
                Rotation = r.ReadMat3(),
                ScriptName = r.ReadVString(),
                HiddenInEditor = r.ReadU8(),
                Team = r.ReadI32(),
                RedTeam = r.ReadU8(),
                BlueTeam = r.ReadU8(),
                Bot = r.ReadU8(),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Points.Count);
        foreach (MpRespawnPoint p in Points)
        {
            w.WriteI32(p.Uid);
            w.WriteVec3(p.Position);
            w.WriteMat3(p.Rotation);
            w.WriteVString(p.ScriptName);
            w.WriteU8(p.HiddenInEditor);
            w.WriteI32(p.Team);
            w.WriteU8(p.RedTeam);
            w.WriteU8(p.BlueTeam);
            w.WriteU8(p.Bot);
        }
    }
}
