namespace Ged.Core.IO.Rfl;

/// <summary>
/// Version-aware context threaded through every section parser/serializer so
/// that version-gated fields are read and written consistently. Mirrors the
/// version predicates documented in rfl.ksy.
/// </summary>
public sealed class RflContext
{
    public RflContext(int version)
    {
        Version = version;
    }

    public int Version { get; }

    /// <summary>RF1 family: stock/PS2 (&lt;= 0xC8) or Alpine (&gt;= 0x12C / 300).</summary>
    public bool IsRf1 => Version <= 0xC8 || Version >= 0x12C;

    /// <summary>RF2 (version 0x127). RF2 editing is out of scope for GED.</summary>
    public bool IsRf2 => Version == 0x127;

    /// <summary>Alpine Faction versions (300-305).</summary>
    public bool IsAlpine => Version >= 0x12C && Version <= 0x131;

    // --- Version gates (see rfl.ksy version docs) ---

    /// <summary>mod_name present in the file header.</summary>
    public bool HasModName => Version >= 0xB2 && Version != 0x127;

    /// <summary>Geometry stores face-scroll table + room eax_effect string.</summary>
    public bool HasFaceScrollData => Version >= 0xB4;

    /// <summary>Geometry stores the legacy face-scroll table after surfaces.</summary>
    public bool HasLegacyFaceScrollData => Version <= 0xB4;

    /// <summary>Geometry header uses the new unknown1/modifiability layout.</summary>
    public bool GeometryHasNewModifiability => Version >= 0xC8;

    /// <summary>Rooms carry an eax_effect string.</summary>
    public bool RoomsHaveEax => Version >= 0xB4;

    /// <summary>Events carry a trailing color.</summary>
    public bool EventsHaveColor => Version >= 0xB0;

    /// <summary>Triggers carry a team field.</summary>
    public bool TriggersHaveTeam => Version >= 0xB1;
}
