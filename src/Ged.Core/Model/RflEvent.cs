namespace Ged.Core.Model;

/// <summary>
/// An event object (RFL <c>event</c>). Field semantics depend on
/// <see cref="ClassName"/>; the generic record is stored faithfully. Whether an
/// orientation is persisted is decided by <see cref="HasRotation"/>, which is a
/// pure function of class name and file version (see rfl.ksy).
/// </summary>
public sealed class RflEvent
{
    public int Uid { get; set; }

    public string ClassName { get; set; } = string.Empty;

    public Vec3 Position { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public byte HiddenInEditor { get; set; }

    public float Delay { get; set; }

    public byte Bool1 { get; set; }

    public byte Bool2 { get; set; }

    public int Int1 { get; set; }

    public int Int2 { get; set; }

    public float Float1 { get; set; }

    public float Float2 { get; set; }

    public string Str1 { get; set; } = string.Empty;

    public string Str2 { get; set; } = string.Empty;

    public List<int> Links { get; set; } = new();

    /// <summary>Present only for directional event classes at a supporting version.</summary>
    public Mat3? Rotation { get; set; }

    /// <summary>Present for versions &gt;= 0xB0.</summary>
    public RfColor Color { get; set; }

    /// <summary>
    /// Whether this event class persists an orientation matrix at the given
    /// version. Kept identical between parse and serialize so round-trips match.
    /// </summary>
    public static bool HasRotation(string className, int version) =>
        (version >= 0x91 && (className is "Teleport" or "Play_Vclip" or "Teleport_Player"))
        || (version >= 0x98 && className == "Alarm")
        || (version >= 0x12C && (className is "AF_Teleport_Player" or "Clone_Entity"))
        || (version >= 0x12D && className == "Anchor_Marker_Orient");

    /// <summary>
    /// Whether the editor draws an in-viewport facing arrow for this event class — the
    /// exact set Alpine RED renders a 3D arrow for (editor_patch/event.cpp:1249-1263):
    /// <c>Play_Vclip</c> (41), <c>Teleport</c> (69), <c>AF_Teleport_Player</c>,
    /// <c>Clone_Entity</c> and <c>Anchor_Marker_Orient</c>, plus <c>Teleport_Player</c>
    /// (which stock RED already arrows). This is HasRotation's set minus <c>Alarm</c>,
    /// which persists an orientation but is not arrowed. The arrow points along
    /// <see cref="Rotation"/>'s forward vector.
    /// </summary>
    public static bool HasFacingArrow(string className) => className is
        "Teleport" or "Play_Vclip" or "Teleport_Player" or
        "AF_Teleport_Player" or "Clone_Entity" or "Anchor_Marker_Orient";
}
