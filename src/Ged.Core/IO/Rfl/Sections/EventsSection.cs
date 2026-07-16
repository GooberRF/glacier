using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>events (0x600): generic event records with version-gated rot and color.</summary>
public sealed class EventsSection : IRflSectionContent
{
    public SectionType Type => SectionType.Events;

    public List<RflEvent> Events { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new EventsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var e = new RflEvent
            {
                Uid = r.ReadI32(),
                ClassName = r.ReadVString(),
                Position = r.ReadVec3(),
                ScriptName = r.ReadVString(),
                HiddenInEditor = r.ReadU8(),
                Delay = r.ReadF32(),
                Bool1 = r.ReadU8(),
                Bool2 = r.ReadU8(),
                Int1 = r.ReadI32(),
                Int2 = r.ReadI32(),
                Float1 = r.ReadF32(),
                Float2 = r.ReadF32(),
                Str1 = r.ReadVString(),
                Str2 = r.ReadVString(),
                Links = r.ReadUidList(),
            };

            if (RflEvent.HasRotation(e.ClassName, ctx.Version))
            {
                e.Rotation = r.ReadMat3();
            }

            if (ctx.EventsHaveColor)
            {
                e.Color = r.ReadColor();
            }

            section.Events.Add(e);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Events.Count);
        foreach (RflEvent e in Events)
        {
            w.WriteI32(e.Uid);
            w.WriteVString(e.ClassName);
            w.WriteVec3(e.Position);
            w.WriteVString(e.ScriptName);
            w.WriteU8(e.HiddenInEditor);
            w.WriteF32(e.Delay);
            w.WriteU8(e.Bool1);
            w.WriteU8(e.Bool2);
            w.WriteI32(e.Int1);
            w.WriteI32(e.Int2);
            w.WriteF32(e.Float1);
            w.WriteF32(e.Float2);
            w.WriteVString(e.Str1);
            w.WriteVString(e.Str2);
            w.WriteUidList(e.Links);

            if (RflEvent.HasRotation(e.ClassName, ctx.Version))
            {
                w.WriteMat3(e.Rotation ?? Mat3.Identity);
            }

            if (ctx.EventsHaveColor)
            {
                w.WriteColor(e.Color);
            }
        }
    }
}
