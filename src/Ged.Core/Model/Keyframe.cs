namespace Ged.Core.Model;

/// <summary>A mover keyframe (RFL <c>keyframe</c>).</summary>
public sealed class Keyframe
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Rotation { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public byte HiddenInEditor { get; set; }

    public float PauseTime { get; set; }

    public float DepartTravelTime { get; set; }

    public float ReturnTravelTime { get; set; }

    public float AccelTime { get; set; }

    public float DecelTime { get; set; }

    public int EventUid { get; set; }

    public int ItemUid1 { get; set; }

    public int ItemUid2 { get; set; }

    public float DegreesAboutAxis { get; set; }
}
