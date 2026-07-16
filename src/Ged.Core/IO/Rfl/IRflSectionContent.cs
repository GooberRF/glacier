namespace Ged.Core.IO.Rfl;

/// <summary>
/// A parsed, editable model for one RFL section. Implementations must serialize
/// back to bytes that are identical to the bytes they were parsed from when no
/// field was modified (the losslessness invariant enforced by the test suite).
/// </summary>
public interface IRflSectionContent
{
    /// <summary>The section type this content represents.</summary>
    SectionType Type { get; }

    /// <summary>Serializes this content to the section body (excludes the type/len header).</summary>
    void Write(RfWriter writer, RflContext context);
}
