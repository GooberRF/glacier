using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>cutscene_cameras (0x400): each item is a plain object header.</summary>
public sealed class CutsceneCamerasSection : IRflSectionContent
{
    public SectionType Type => SectionType.CutsceneCameras;

    public List<ObjectHeader> Cameras { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new CutsceneCamerasSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Cameras.Add(ObjectHeader.Read(r));
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Cameras.Count);
        foreach (ObjectHeader c in Cameras)
        {
            c.Write(w);
        }
    }
}
