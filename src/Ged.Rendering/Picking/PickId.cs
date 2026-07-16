namespace Ged.Rendering.Picking;

/// <summary>What a picked pixel refers to.</summary>
public enum PickKind : byte
{
    None = 0,
    Face = 1,
    Object = 2,
    Brush = 3,
    Mesh = 4,

    /// <summary>A face of an editable brush; payload indexes a per-scene registry.</summary>
    BrushFace = 5,

    /// <summary>A vertex of an editable brush; payload indexes a per-scene registry.</summary>
    BrushVertex = 6,

    /// <summary>A transform-gizmo handle; payload identifies the axis/mode.</summary>
    Gizmo = 7,
}

/// <summary>
/// A pick identifier encoded into a single 32-bit id-buffer value: the high
/// nibble is the <see cref="PickKind"/>, the low 28 bits are the payload
/// (a face index or an object/brush UID). Zero is reserved for "nothing", so a
/// real pick never encodes to 0. Payloads must fit in 28 bits (&lt; 268,435,456),
/// which covers every RF/Alpine face count and UID.
/// </summary>
public readonly record struct PickId(PickKind Kind, int Index)
{
    /// <summary>The reserved empty pick.</summary>
    public static PickId None => new(PickKind.None, 0);

    private const int PayloadBits = 28;
    private const uint PayloadMask = (1u << PayloadBits) - 1;

    public bool IsNone => Kind == PickKind.None;

    /// <summary>Packs to the id-buffer value. <see cref="PickKind.None"/> always packs to 0.</summary>
    public uint Encode()
    {
        if (Kind == PickKind.None)
        {
            return 0;
        }

        if (Index < 0 || (uint)Index > PayloadMask)
        {
            throw new ArgumentOutOfRangeException(nameof(Index), Index,
                $"Pick payload must fit in {PayloadBits} bits.");
        }

        return ((uint)Kind << PayloadBits) | (uint)Index;
    }

    /// <summary>Unpacks an id-buffer value; 0 decodes to <see cref="None"/>.</summary>
    public static PickId Decode(uint value)
    {
        if (value == 0)
        {
            return None;
        }

        var kind = (PickKind)(value >> PayloadBits);
        int index = (int)(value & PayloadMask);
        return new PickId(kind, index);
    }
}
