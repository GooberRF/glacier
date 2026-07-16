using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>cutscenes (0x4000).</summary>
public sealed class CutscenesSection : IRflSectionContent
{
    public SectionType Type => SectionType.Cutscenes;

    public List<Cutscene> Cutscenes { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new CutscenesSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var cutscene = new Cutscene
            {
                Uid = r.ReadI32(),
                HidePlayer = r.ReadU8(),
                Fov = r.ReadF32(),
            };

            int numShots = r.ReadI32();
            for (int j = 0; j < numShots; j++)
            {
                cutscene.Shots.Add(new CutsceneShot
                {
                    CameraUid = r.ReadI32(),
                    PreWait = r.ReadF32(),
                    PathTime = r.ReadF32(),
                    PostWait = r.ReadF32(),
                    LookAtUid = r.ReadI32(),
                    TriggerUid = r.ReadI32(),
                    PathName = r.ReadVString(),
                });
            }

            section.Cutscenes.Add(cutscene);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Cutscenes.Count);
        foreach (Cutscene cutscene in Cutscenes)
        {
            w.WriteI32(cutscene.Uid);
            w.WriteU8(cutscene.HidePlayer);
            w.WriteF32(cutscene.Fov);
            w.WriteI32(cutscene.Shots.Count);
            foreach (CutsceneShot shot in cutscene.Shots)
            {
                w.WriteI32(shot.CameraUid);
                w.WriteF32(shot.PreWait);
                w.WriteF32(shot.PathTime);
                w.WriteF32(shot.PostWait);
                w.WriteI32(shot.LookAtUid);
                w.WriteI32(shot.TriggerUid);
                w.WriteVString(shot.PathName);
            }
        }
    }
}
