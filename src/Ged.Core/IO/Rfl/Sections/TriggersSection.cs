using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>triggers (0x60000).</summary>
public sealed class TriggersSection : IRflSectionContent
{
    public SectionType Type => SectionType.Triggers;

    public List<Trigger> Triggers { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new TriggersSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var t = new Trigger
            {
                Uid = r.ReadI32(),
                ScriptName = r.ReadVString(),
                HiddenInEditor = r.ReadU8(),
                Shape = r.ReadI32(),
                ResetsAfter = r.ReadF32(),
                ResetsTimes = r.ReadI32(),
                IsUseKeyRequired = r.ReadU8(),
                KeyName = r.ReadVString(),
                WeaponActivates = r.ReadU8(),
                ActivatedBy = r.ReadU8(),
                IsNpc = r.ReadU8(),
                IsAuto = r.ReadU8(),
                InVehicle = r.ReadU8(),
                Position = r.ReadVec3(),
            };

            if (t.Shape == Trigger.ShapeSphere)
            {
                t.SphereRadius = r.ReadF32();
            }
            else
            {
                t.Rotation = r.ReadMat3();
                t.BoxHeight = r.ReadF32();
                t.BoxWidth = r.ReadF32();
                t.BoxDepth = r.ReadF32();
                t.OneWay = r.ReadU8();
            }

            t.AirlockRoomUid = r.ReadI32();
            t.AttachedToUid = r.ReadI32();
            t.UseClutterUid = r.ReadI32();
            t.Disabled = r.ReadU8();
            t.ButtonActiveTimeSeconds = r.ReadF32();
            t.InsideTimeSeconds = r.ReadF32();

            if (ctx.TriggersHaveTeam)
            {
                t.Team = r.ReadI32();
            }

            t.Links = r.ReadUidList();
            section.Triggers.Add(t);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Triggers.Count);
        foreach (Trigger t in Triggers)
        {
            w.WriteI32(t.Uid);
            w.WriteVString(t.ScriptName);
            w.WriteU8(t.HiddenInEditor);
            w.WriteI32(t.Shape);
            w.WriteF32(t.ResetsAfter);
            w.WriteI32(t.ResetsTimes);
            w.WriteU8(t.IsUseKeyRequired);
            w.WriteVString(t.KeyName);
            w.WriteU8(t.WeaponActivates);
            w.WriteU8(t.ActivatedBy);
            w.WriteU8(t.IsNpc);
            w.WriteU8(t.IsAuto);
            w.WriteU8(t.InVehicle);
            w.WriteVec3(t.Position);

            if (t.Shape == Trigger.ShapeSphere)
            {
                w.WriteF32(t.SphereRadius ?? 0f);
            }
            else
            {
                w.WriteMat3(t.Rotation ?? Mat3.Identity);
                w.WriteF32(t.BoxHeight ?? 0f);
                w.WriteF32(t.BoxWidth ?? 0f);
                w.WriteF32(t.BoxDepth ?? 0f);
                w.WriteU8(t.OneWay ?? 0);
            }

            w.WriteI32(t.AirlockRoomUid);
            w.WriteI32(t.AttachedToUid);
            w.WriteI32(t.UseClutterUid);
            w.WriteU8(t.Disabled);
            w.WriteF32(t.ButtonActiveTimeSeconds);
            w.WriteF32(t.InsideTimeSeconds);

            if (ctx.TriggersHaveTeam)
            {
                w.WriteI32(t.Team ?? -1);
            }

            w.WriteUidList(t.Links);
        }
    }
}
