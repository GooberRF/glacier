namespace Ged.Core.Model;

/// <summary>
/// An Alpine projection camera (0x0AFBAE08; layout editor_patch/projection_camera.cpp:31-58): a viewpoint a
/// Display_Projection event renders into an ATX handle.
/// </summary>
public sealed class AlpineProjectionCamera
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Orientation { get; set; } = Mat3.Identity;

    public string ScriptName { get; set; } = string.Empty;
}
