using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// groups (0x3000000) and moving_groups (0x3000): both store a list of the same
/// <c>group</c> structure. The two differ only in section id.
/// </summary>
public sealed class GroupsSection : IRflSectionContent
{
    public GroupsSection(SectionType type)
    {
        Type = type;
    }

    public SectionType Type { get; }

    public List<Group> Groups { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) =>
        ParseInto(new GroupsSection(SectionType.Groups), r);

    public static IRflSectionContent ParseMoving(RfReader r, RflContext ctx) =>
        ParseInto(new GroupsSection(SectionType.MovingGroups), r);

    internal static GroupsSection ParseInto(GroupsSection section, RfReader r)
    {
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Groups.Add(ReadGroup(r));
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Groups.Count);
        foreach (Group group in Groups)
        {
            WriteGroup(w, group);
        }
    }

    internal static Group ReadGroup(RfReader r)
    {
        var group = new Group
        {
            Name = r.ReadVString(),
            Unknown = r.ReadU8(),
            IsMoving = r.ReadU8(),
        };

        if (group.IsMoving != 0)
        {
            group.MovingData = ReadMovingData(r);
        }

        group.Objects = r.ReadUidList();
        group.Brushes = r.ReadUidList();
        return group;
    }

    internal static void WriteGroup(RfWriter w, Group group)
    {
        w.WriteVString(group.Name);
        w.WriteU8(group.Unknown);
        w.WriteU8(group.IsMoving);

        if (group.IsMoving != 0)
        {
            WriteMovingData(w, group.MovingData!);
        }

        w.WriteUidList(group.Objects);
        w.WriteUidList(group.Brushes);
    }

    internal static MovingGroupData ReadMovingData(RfReader r)
    {
        var data = new MovingGroupData();

        int numKeyframes = r.ReadI32();
        for (int i = 0; i < numKeyframes; i++)
        {
            data.Keyframes.Add(new Keyframe
            {
                Uid = r.ReadI32(),
                Position = r.ReadVec3(),
                Rotation = r.ReadMat3(),
                ScriptName = r.ReadVString(),
                HiddenInEditor = r.ReadU8(),
                PauseTime = r.ReadF32(),
                DepartTravelTime = r.ReadF32(),
                ReturnTravelTime = r.ReadF32(),
                AccelTime = r.ReadF32(),
                DecelTime = r.ReadF32(),
                EventUid = r.ReadI32(),
                ItemUid1 = r.ReadI32(),
                ItemUid2 = r.ReadI32(),
                DegreesAboutAxis = r.ReadF32(),
            });
        }

        int numMembers = r.ReadI32();
        for (int i = 0; i < numMembers; i++)
        {
            data.MemberTransforms.Add(new MovingGroupMemberTransform
            {
                Uid = r.ReadI32(),
                Position = r.ReadVec3(),
                Rotation = r.ReadMat3(),
            });
        }

        data.IsDoor = r.ReadU8();
        data.RotateInPlace = r.ReadU8();
        data.StartsBackwards = r.ReadU8();
        data.UseTravelTimeAsSpeed = r.ReadU8();
        data.ForceOrient = r.ReadU8();
        data.NoPlayerCollide = r.ReadU8();
        data.MovementType = r.ReadI32();
        data.StartingKeyframe = r.ReadI32();
        data.StartSound = r.ReadVString();
        data.StartVol = r.ReadF32();
        data.LoopingSound = r.ReadVString();
        data.LoopingVol = r.ReadF32();
        data.StopSound = r.ReadVString();
        data.StopVol = r.ReadF32();
        data.CloseSound = r.ReadVString();
        data.CloseVol = r.ReadF32();
        return data;
    }

    internal static void WriteMovingData(RfWriter w, MovingGroupData data)
    {
        w.WriteI32(data.Keyframes.Count);
        foreach (Keyframe k in data.Keyframes)
        {
            w.WriteI32(k.Uid);
            w.WriteVec3(k.Position);
            w.WriteMat3(k.Rotation);
            w.WriteVString(k.ScriptName);
            w.WriteU8(k.HiddenInEditor);
            w.WriteF32(k.PauseTime);
            w.WriteF32(k.DepartTravelTime);
            w.WriteF32(k.ReturnTravelTime);
            w.WriteF32(k.AccelTime);
            w.WriteF32(k.DecelTime);
            w.WriteI32(k.EventUid);
            w.WriteI32(k.ItemUid1);
            w.WriteI32(k.ItemUid2);
            w.WriteF32(k.DegreesAboutAxis);
        }

        w.WriteI32(data.MemberTransforms.Count);
        foreach (MovingGroupMemberTransform m in data.MemberTransforms)
        {
            w.WriteI32(m.Uid);
            w.WriteVec3(m.Position);
            w.WriteMat3(m.Rotation);
        }

        w.WriteU8(data.IsDoor);
        w.WriteU8(data.RotateInPlace);
        w.WriteU8(data.StartsBackwards);
        w.WriteU8(data.UseTravelTimeAsSpeed);
        w.WriteU8(data.ForceOrient);
        w.WriteU8(data.NoPlayerCollide);
        w.WriteI32(data.MovementType);
        w.WriteI32(data.StartingKeyframe);
        w.WriteVString(data.StartSound);
        w.WriteF32(data.StartVol);
        w.WriteVString(data.LoopingSound);
        w.WriteF32(data.LoopingVol);
        w.WriteVString(data.StopSound);
        w.WriteF32(data.StopVol);
        w.WriteVString(data.CloseSound);
        w.WriteF32(data.CloseVol);
    }
}
