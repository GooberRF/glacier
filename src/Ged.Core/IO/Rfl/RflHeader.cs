namespace Ged.Core.IO.Rfl;

/// <summary>
/// The RFL file header. <see cref="PlayerStartOffset"/>,
/// <see cref="LevelInfoOffset"/>, <see cref="NumSections"/> and
/// <see cref="SectionsTotalSize"/> are recomputed on save from the actual
/// section layout, so they are always consistent even after edits.
/// </summary>
public sealed class RflHeader
{
    /// <summary>Magic value: bytes <c>55 DA BA D4</c>.</summary>
    public const uint Magic = 0xD4BADA55;

    public int Version { get; set; }

    /// <summary>Last-modification timestamp; preserved verbatim unless the caller opts to update it.</summary>
    public uint Timestamp { get; set; }

    /// <summary>
    /// The <see cref="Timestamp"/> (Unix seconds) as a UTC instant, for read-only display.
    /// A zero timestamp (never-saved / stripped) surfaces as the Unix epoch — callers that
    /// want to show "not set" should special-case <c>Timestamp == 0</c>.
    /// </summary>
    public DateTimeOffset TimestampUtc => DateTimeOffset.FromUnixTimeSeconds(Timestamp);

    public int PlayerStartOffset { get; set; }

    public int LevelInfoOffset { get; set; }

    public int NumSections { get; set; }

    public int SectionsTotalSize { get; set; }

    public string LevelName { get; set; } = string.Empty;

    /// <summary>
    /// Mod name; present only for versions with <see cref="RflContext.HasModName"/>.
    /// Null when the field is absent from the file.
    /// </summary>
    public string? ModName { get; set; }
}
