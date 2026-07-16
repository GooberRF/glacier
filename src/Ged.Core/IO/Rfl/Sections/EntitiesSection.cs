using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>entities (0x30000): AI/NPC objects.</summary>
public sealed class EntitiesSection : IRflSectionContent
{
    public SectionType Type => SectionType.Entities;

    public List<Entity> Entities { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new EntitiesSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var e = new Entity
            {
                Uid = r.ReadI32(),
                ClassName = r.ReadVString(),
                Position = r.ReadVec3(),
                Rotation = r.ReadMat3(),
                ScriptName = r.ReadVString(),
                HiddenInEditor = r.ReadU8(),
                Cooperation = r.ReadI32(),
                Friendliness = r.ReadI32(),
                TeamId = r.ReadI32(),
                WaypointList = r.ReadVString(),
                WaypointMethod = r.ReadVString(),
                Unknown1 = r.ReadU8(),
                Boarded = r.ReadU8(),
                ReadyToFireState = r.ReadU8(),
                OnlyAttackPlayer = r.ReadU8(),
                WeaponIsHolstered = r.ReadU8(),
                Deaf = r.ReadU8(),
                SweepMinAngle = r.ReadI32(),
                SweepMaxAngle = r.ReadI32(),
                IgnoreTerrainWhenFiring = r.ReadU8(),
                Unknown2 = r.ReadU8(),
                StartCrouched = r.ReadU8(),
                Life = r.ReadF32(),
                Armor = r.ReadF32(),
                Fov = r.ReadI32(),
                DefaultPrimaryWeapon = r.ReadVString(),
                DefaultSecondaryWeapon = r.ReadVString(),
                ItemDrop = r.ReadVString(),
                StateAnim = r.ReadVString(),
                CorpsePose = r.ReadVString(),
                Skin = r.ReadVString(),
                DeathAnim = r.ReadVString(),
                AiMode = r.ReadU8(),
                AiAttackStyle = r.ReadU8(),
                Unknown3 = r.ReadI32(),
                TurretUid = r.ReadI32(),
                AlertCameraUid = r.ReadI32(),
                AlarmEventUid = r.ReadI32(),
                Run = r.ReadU8(),
                StartHidden = r.ReadU8(),
                WearHelmet = r.ReadU8(),
                EndGameIfKilled = r.ReadU8(),
                CowerFromWeapon = r.ReadU8(),
                QuestionUnarmedPlayer = r.ReadU8(),
                DontHum = r.ReadU8(),
                NoShadow = r.ReadU8(),
                AlwaysSimulate = r.ReadU8(),
                PerfectAim = r.ReadU8(),
                PermanentCorpse = r.ReadU8(),
                NeverFly = r.ReadU8(),
                NeverLeave = r.ReadU8(),
                NoPersonaMessages = r.ReadU8(),
                FadeCorpseImmediately = r.ReadU8(),
                NeverCollideWithPlayer = r.ReadU8(),
                UseCustomAttackRange = r.ReadU8(),
            };

            if (e.UseCustomAttackRange == 1)
            {
                e.CustomAttackRange = r.ReadF32();
            }

            e.LeftHandHolding = r.ReadVString();
            e.RightHandHolding = r.ReadVString();
            section.Entities.Add(e);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Entities.Count);
        foreach (Entity e in Entities)
        {
            w.WriteI32(e.Uid);
            w.WriteVString(e.ClassName);
            w.WriteVec3(e.Position);
            w.WriteMat3(e.Rotation);
            w.WriteVString(e.ScriptName);
            w.WriteU8(e.HiddenInEditor);
            w.WriteI32(e.Cooperation);
            w.WriteI32(e.Friendliness);
            w.WriteI32(e.TeamId);
            w.WriteVString(e.WaypointList);
            w.WriteVString(e.WaypointMethod);
            w.WriteU8(e.Unknown1);
            w.WriteU8(e.Boarded);
            w.WriteU8(e.ReadyToFireState);
            w.WriteU8(e.OnlyAttackPlayer);
            w.WriteU8(e.WeaponIsHolstered);
            w.WriteU8(e.Deaf);
            w.WriteI32(e.SweepMinAngle);
            w.WriteI32(e.SweepMaxAngle);
            w.WriteU8(e.IgnoreTerrainWhenFiring);
            w.WriteU8(e.Unknown2);
            w.WriteU8(e.StartCrouched);
            w.WriteF32(e.Life);
            w.WriteF32(e.Armor);
            w.WriteI32(e.Fov);
            w.WriteVString(e.DefaultPrimaryWeapon);
            w.WriteVString(e.DefaultSecondaryWeapon);
            w.WriteVString(e.ItemDrop);
            w.WriteVString(e.StateAnim);
            w.WriteVString(e.CorpsePose);
            w.WriteVString(e.Skin);
            w.WriteVString(e.DeathAnim);
            w.WriteU8(e.AiMode);
            w.WriteU8(e.AiAttackStyle);
            w.WriteI32(e.Unknown3);
            w.WriteI32(e.TurretUid);
            w.WriteI32(e.AlertCameraUid);
            w.WriteI32(e.AlarmEventUid);
            w.WriteU8(e.Run);
            w.WriteU8(e.StartHidden);
            w.WriteU8(e.WearHelmet);
            w.WriteU8(e.EndGameIfKilled);
            w.WriteU8(e.CowerFromWeapon);
            w.WriteU8(e.QuestionUnarmedPlayer);
            w.WriteU8(e.DontHum);
            w.WriteU8(e.NoShadow);
            w.WriteU8(e.AlwaysSimulate);
            w.WriteU8(e.PerfectAim);
            w.WriteU8(e.PermanentCorpse);
            w.WriteU8(e.NeverFly);
            w.WriteU8(e.NeverLeave);
            w.WriteU8(e.NoPersonaMessages);
            w.WriteU8(e.FadeCorpseImmediately);
            w.WriteU8(e.NeverCollideWithPlayer);
            w.WriteU8(e.UseCustomAttackRange);

            if (e.UseCustomAttackRange == 1)
            {
                w.WriteF32(e.CustomAttackRange ?? 0f);
            }

            w.WriteVString(e.LeftHandHolding);
            w.WriteVString(e.RightHandHolding);
        }
    }
}
