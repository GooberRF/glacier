using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>alpine_note_objects (0x0AFBAE02): editor-only annotation objects.</summary>
public sealed class AlpineNoteObjectsSection : IRflSectionContent
{
    public SectionType Type => SectionType.AlpineNoteObjects;

    public List<AlpineNoteObject> Notes { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new AlpineNoteObjectsSection();
        int count = (int)r.ReadU32();
        for (int i = 0; i < count; i++)
        {
            var note = new AlpineNoteObject
            {
                Uid = r.ReadI32(),
                Position = r.ReadVec3(),
                Orientation = r.ReadMat3(),
                ScriptName = r.ReadVString(),
            };

            int numNotes = (int)r.ReadU32();
            for (int j = 0; j < numNotes; j++)
            {
                note.Notes.Add(r.ReadVString());
            }

            section.Notes.Add(note);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteU32((uint)Notes.Count);
        foreach (AlpineNoteObject note in Notes)
        {
            w.WriteI32(note.Uid);
            w.WriteVec3(note.Position);
            w.WriteMat3(note.Orientation);
            w.WriteVString(note.ScriptName);
            w.WriteU32((uint)note.Notes.Count);
            foreach (string line in note.Notes)
            {
                w.WriteVString(line);
            }
        }
    }
}
