using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>player_start (0x70000): the single-player start transform.</summary>
public sealed class PlayerStartSection : IRflSectionContent
{
    public SectionType Type => SectionType.PlayerStart;

    public Vec3 Position { get; set; }

    public Mat3 Rotation { get; set; }

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) => new PlayerStartSection
    {
        Position = r.ReadVec3(),
        Rotation = r.ReadMat3(),
    };

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteVec3(Position);
        w.WriteMat3(Rotation);
    }
}
