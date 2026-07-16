using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Model;

/// <summary>
/// Well-known GED object-metadata block types (the <c>metadata_type</c> enum of a
/// <see cref="GedObjectMetadataBlock"/>). Types GED does not recognise round-trip opaquely, so
/// this enum can grow without breaking old data (forward compatibility).
/// </summary>
public enum GedMetadataType : uint
{
    /// <summary>Payload = <c>vstring</c> cookie image filename projected by the light in the baker.</summary>
    LightCookie = 1,
}

/// <summary>
/// One metadata block attached to an object in the <c>ged_object_metadata</c> chunk
/// (0x6ED00002): a typed, length-prefixed payload. GED only interprets blocks whose
/// <see cref="MetadataType"/> it knows; any other type is preserved verbatim as opaque
/// <see cref="Payload"/> bytes, so third-party / future block types survive an open→save.
/// </summary>
public sealed class GedObjectMetadataBlock
{
    public GedObjectMetadataBlock()
    {
    }

    public GedObjectMetadataBlock(GedMetadataType type, byte[] payload)
    {
        MetadataType = (uint)type;
        Payload = payload;
    }

    /// <summary>The block's type id (see <see cref="GedMetadataType"/> for the ones GED interprets).</summary>
    public uint MetadataType { get; set; }

    /// <summary>The raw, length-prefixed block body (opaque for unknown types).</summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public GedObjectMetadataBlock Clone() => new()
    {
        MetadataType = MetadataType,
        Payload = (byte[])Payload.Clone(),
    };
}

/// <summary>
/// The metadata for a single object (keyed by its level UID) in the <c>ged_object_metadata</c>
/// chunk: an ordered list of typed <see cref="GedObjectMetadataBlock"/>s.
/// </summary>
public sealed class GedObjectMetadataRecord
{
    /// <summary>The level UID of the object this metadata belongs to.</summary>
    public int Uid { get; set; }

    public List<GedObjectMetadataBlock> Blocks { get; set; } = new();

    public GedObjectMetadataRecord Clone() => new()
    {
        Uid = Uid,
        Blocks = Blocks.Select(b => b.Clone()).ToList(),
    };
}
