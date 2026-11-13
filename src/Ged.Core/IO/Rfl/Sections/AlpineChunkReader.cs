using System.Buffers.Binary;
using System.Text;
using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// A bounds-checked reader over one Alpine chunk body that fails instead of throwing, the way Alpine's
/// readers do: every read is refused once fewer bytes remain than it needs, and a refused read leaves the
/// position where it was. Two string flavours mirror Alpine's two helpers: <see cref="TryString"/> is
/// <c>RflChunkReader::read_string</c> (common/include/common/rfl_chunk_reader.h:37-56 — a length that does
/// not fit fails the read) and <see cref="TryLenientString"/> is <c>read_rfl_string</c>
/// (editor_patch/vtypes.h:1585-1601 — fewer than two bytes or a length that does not fit yields an empty
/// string, the latter after skipping the rest of the chunk). A lenient string that had to be cut short sets
/// <see cref="Lossy"/>, so the caller knows the bytes were not a clean record.
/// </summary>
internal sealed class AlpineChunkReader
{
    private readonly byte[] _data;

    public AlpineChunkReader(byte[] data)
    {
        _data = data;
    }

    public int Position { get; set; }

    public int Remaining => _data.Length - Position;

    /// <summary>Set when a lenient string was truncated (the chunk does not hold a clean record there).</summary>
    public bool Lossy { get; set; }

    public bool TryU8(out byte value)
    {
        value = 0;
        if (Remaining < 1)
        {
            return false;
        }

        value = _data[Position++];
        return true;
    }

    public bool TryU16(out ushort value)
    {
        value = 0;
        if (Remaining < 2)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position, 2));
        Position += 2;
        return true;
    }

    public bool TryU32(out uint value)
    {
        value = 0;
        if (Remaining < 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return true;
    }

    public bool TryI32(out int value)
    {
        bool ok = TryU32(out uint raw);
        value = unchecked((int)raw);
        return ok;
    }

    public bool TryU64(out ulong value)
    {
        value = 0;
        if (Remaining < 8)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(Position, 8));
        Position += 8;
        return true;
    }

    public bool TryF32(out float value)
    {
        value = 0f;
        if (Remaining < 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return true;
    }

    public bool TryVec3(out Vec3 value)
    {
        value = default;
        if (Remaining < 12)
        {
            return false;
        }

        TryF32(out float x);
        TryF32(out float y);
        TryF32(out float z);
        value = new Vec3(x, y, z);
        return true;
    }

    /// <summary>Three rows as stored (Alpine object records store right, up, forward).</summary>
    public bool TryMat3(out Mat3 value)
    {
        value = default;
        if (Remaining < 36)
        {
            return false;
        }

        TryVec3(out Vec3 a);
        TryVec3(out Vec3 b);
        TryVec3(out Vec3 c);
        value = new Mat3(a, b, c);
        return true;
    }

    /// <summary>Four bytes red, green, blue, alpha.</summary>
    public bool TryColor(out RfColor value)
    {
        value = default;
        if (Remaining < 4)
        {
            return false;
        }

        value = new RfColor(_data[Position], _data[Position + 1], _data[Position + 2], _data[Position + 3]);
        Position += 4;
        return true;
    }

    public bool TryBytes(int count, out byte[] value)
    {
        value = System.Array.Empty<byte>();
        if (count < 0 || Remaining < count)
        {
            return false;
        }

        value = _data.AsSpan(Position, count).ToArray();
        Position += count;
        return true;
    }

    /// <summary><c>RflChunkReader::read_string</c>: a u16 length and that many bytes, or a failed read.</summary>
    public bool TryString(out string value)
    {
        value = string.Empty;
        int start = Position;
        if (!TryU16(out ushort len))
        {
            return false;
        }

        if (Remaining < len)
        {
            Position = start;
            return false;
        }

        value = Encoding.Latin1.GetString(_data, Position, len);
        Position += len;
        return true;
    }

    /// <summary>
    /// <c>read_rfl_string</c>: never fails. Fewer than two bytes left reads nothing and yields "", a length
    /// that does not fit skips the rest of the chunk and yields "" (both set <see cref="Lossy"/>).
    /// </summary>
    public string LenientString()
    {
        if (Remaining < 2)
        {
            Lossy = true;
            return string.Empty;
        }

        TryU16(out ushort len);
        if (len == 0)
        {
            return string.Empty;
        }

        if (Remaining < len)
        {
            Position = _data.Length;
            Lossy = true;
            return string.Empty;
        }

        string value = Encoding.Latin1.GetString(_data, Position, len);
        Position += len;
        return value;
    }
}
