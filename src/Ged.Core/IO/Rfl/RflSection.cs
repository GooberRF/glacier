namespace Ged.Core.IO.Rfl;

/// <summary>
/// One section of an RFL file. Every section keeps the raw bytes it was loaded
/// from. A parsed model may be attached lazily; on save a section is
/// re-serialized from its model only when <see cref="Dirty"/> is set, otherwise
/// the original bytes are emitted verbatim. Unknown section types remain opaque
/// blobs that round-trip exactly.
/// </summary>
public sealed class RflSection
{
    public RflSection(uint typeId, byte[] rawBytes)
    {
        TypeId = typeId;
        RawBytes = rawBytes;
    }

    /// <summary>Raw 32-bit section type id (covers unknown types too).</summary>
    public uint TypeId { get; }

    /// <summary>The known section type, or a cast of an unrecognized id.</summary>
    public SectionType Type => (SectionType)TypeId;

    /// <summary>True for the trailing terminator section (type 0, length 0).</summary>
    public bool IsEnd => TypeId == (uint)SectionType.End;

    /// <summary>Original bytes of the section body (never includes the type/len header).</summary>
    public byte[] RawBytes { get; set; }

    /// <summary>Parsed model for this section, if one has been attached.</summary>
    public IRflSectionContent? Content { get; set; }

    /// <summary>When true, the section is re-serialized from <see cref="Content"/> on save.</summary>
    public bool Dirty { get; set; }

    /// <summary>
    /// Produces the bytes to write for this section: the freshly serialized
    /// model when dirty, otherwise the untouched original bytes.
    /// </summary>
    public byte[] GetBodyBytes(RflContext context)
    {
        if (Dirty && Content is not null)
        {
            var writer = new RfWriter(RawBytes.Length);
            Content.Write(writer, context);
            return writer.ToArray();
        }

        return RawBytes;
    }
}
