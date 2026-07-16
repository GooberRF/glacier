namespace Ged.Core.Model;

/// <summary>
/// An Alpine note object (alpine_note_objects, 0x0AFBAE02). Editor-only; the
/// game ignores these.
/// </summary>
public sealed class AlpineNoteObject
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Orientation { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public List<string> Notes { get; set; } = new();
}
