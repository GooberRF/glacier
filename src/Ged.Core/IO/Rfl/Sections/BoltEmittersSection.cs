using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>bolt_emitters (0xE00): lightning-bolt emitters.</summary>
public sealed class BoltEmittersSection : IRflSectionContent
{
    public SectionType Type => SectionType.BoltEmitters;

    public List<BoltEmitter> Emitters { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new BoltEmittersSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Emitters.Add(new BoltEmitter
            {
                Header = ObjectHeader.Read(r),
                TargetUid = r.ReadI32(),
                SrcCtrlDist = r.ReadF32(),
                TrgCtrlDist = r.ReadF32(),
                Thickness = r.ReadF32(),
                Jitter = r.ReadF32(),
                NumSegments = r.ReadI32(),
                SpawnDelay = r.ReadF32(),
                SpawnDelayRandomize = r.ReadF32(),
                Decay = r.ReadF32(),
                DecayRandomize = r.ReadF32(),
                Color = r.ReadColor(),
                Texture = r.ReadVString(),
                Flags = r.ReadU32(),
                InitiallyOn = r.ReadU8(),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Emitters.Count);
        foreach (BoltEmitter e in Emitters)
        {
            e.Header.Write(w);
            w.WriteI32(e.TargetUid);
            w.WriteF32(e.SrcCtrlDist);
            w.WriteF32(e.TrgCtrlDist);
            w.WriteF32(e.Thickness);
            w.WriteF32(e.Jitter);
            w.WriteI32(e.NumSegments);
            w.WriteF32(e.SpawnDelay);
            w.WriteF32(e.SpawnDelayRandomize);
            w.WriteF32(e.Decay);
            w.WriteF32(e.DecayRandomize);
            w.WriteColor(e.Color);
            w.WriteVString(e.Texture);
            w.WriteU32(e.Flags);
            w.WriteU8(e.InitiallyOn);
        }
    }
}
