using Ged.Core.IO;

namespace Ged.Core.Model;

/// <summary>
/// The common leading fields shared by most RFL object types:
/// uid, class_name, pos, rot, script_name, hidden_in_editor. Reused by lights
/// (indirectly), cutscene cameras, emitters, regions, decals, targets, etc.
/// </summary>
public sealed class ObjectHeader
{
    public int Uid { get; set; }

    public string ClassName { get; set; } = string.Empty;

    public Vec3 Position { get; set; }

    public Mat3 Rotation { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public byte HiddenInEditor { get; set; }

    public static ObjectHeader Read(RfReader r) => new()
    {
        Uid = r.ReadI32(),
        ClassName = r.ReadVString(),
        Position = r.ReadVec3(),
        Rotation = r.ReadMat3(),
        ScriptName = r.ReadVString(),
        HiddenInEditor = r.ReadU8(),
    };

    public void Write(RfWriter w)
    {
        w.WriteI32(Uid);
        w.WriteVString(ClassName);
        w.WriteVec3(Position);
        w.WriteMat3(Rotation);
        w.WriteVString(ScriptName);
        w.WriteU8(HiddenInEditor);
    }
}
