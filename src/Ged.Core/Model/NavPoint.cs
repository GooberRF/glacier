using Ged.Core.IO;

namespace Ged.Core.Model;

/// <summary>An AI navigation point (RFL <c>nav_point</c>).</summary>
public sealed class NavPoint
{
    public int Uid { get; set; }

    public byte HiddenInEditor { get; set; }

    public float Height { get; set; }

    public Vec3 Position { get; set; }

    public float Radius { get; set; }

    /// <summary>0 = walking, 1 = flying.</summary>
    public int NavType { get; set; }

    public byte Directional { get; set; }

    /// <summary>Present iff <see cref="Directional"/> != 0.</summary>
    public Mat3? Rotation { get; set; }

    /// <summary>Cover flag; preserved on save (RED clears it — a stock bug).</summary>
    public byte Cover { get; set; }

    /// <summary>Hide flag; preserved on save (RED clears it — a stock bug).</summary>
    public byte Hide { get; set; }

    public byte Crunch { get; set; }

    public float PauseTime { get; set; }

    public List<int> Links { get; set; } = new();

    public static NavPoint Read(RfReader r)
    {
        var np = new NavPoint
        {
            Uid = r.ReadI32(),
            HiddenInEditor = r.ReadU8(),
            Height = r.ReadF32(),
            Position = r.ReadVec3(),
            Radius = r.ReadF32(),
            NavType = r.ReadI32(),
            Directional = r.ReadU8(),
        };

        if (np.Directional != 0)
        {
            np.Rotation = r.ReadMat3();
        }

        np.Cover = r.ReadU8();
        np.Hide = r.ReadU8();
        np.Crunch = r.ReadU8();
        np.PauseTime = r.ReadF32();
        np.Links = r.ReadUidList();
        return np;
    }

    public void Write(RfWriter w)
    {
        w.WriteI32(Uid);
        w.WriteU8(HiddenInEditor);
        w.WriteF32(Height);
        w.WriteVec3(Position);
        w.WriteF32(Radius);
        w.WriteI32(NavType);
        w.WriteU8(Directional);

        if (Directional != 0)
        {
            w.WriteMat3(Rotation ?? Mat3.Identity);
        }

        w.WriteU8(Cover);
        w.WriteU8(Hide);
        w.WriteU8(Crunch);
        w.WriteF32(PauseTime);
        w.WriteUidList(Links);
    }
}
