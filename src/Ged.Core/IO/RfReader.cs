using System.Buffers.Binary;
using System.Text;
using Ged.Core.Model;

namespace Ged.Core.IO;

/// <summary>
/// Forward-only little-endian reader over an in-memory byte buffer. Every
/// numeric read is explicitly little-endian (independent of host endianness),
/// and floats round-trip bit-exactly, so a read/write pair is lossless.
/// </summary>
public sealed class RfReader
{
    private readonly byte[] _data;

    public RfReader(byte[] data, int position = 0)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        Position = position;
    }

    /// <summary>Current read cursor, in bytes from the start of the buffer.</summary>
    public int Position { get; set; }

    public int Length => _data.Length;

    public int Remaining => _data.Length - Position;

    public bool Eof => Position >= _data.Length;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Position + count > _data.Length)
        {
            throw new EndOfStreamException(
                $"Attempted to read {count} bytes at offset {Position} but only {Remaining} remain.");
        }

        ReadOnlySpan<byte> span = _data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public byte ReadU8() => Take(1)[0];

    public sbyte ReadI8() => (sbyte)Take(1)[0];

    public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public short ReadI16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int ReadI32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public float ReadF32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public byte[] ReadBytes(int count) => Take(count).ToArray();

    public bool ReadBool8() => Take(1)[0] != 0;

    /// <summary>Reads a raw <c>u1</c> that RFL treats as a boolean, preserving the exact byte.</summary>
    public byte ReadRawBool() => Take(1)[0];

    /// <summary>Variable-length string: u16 length prefix followed by that many bytes.</summary>
    /// <remarks>
    /// Decoded as Latin-1 so that every byte value (including 0x80-0xFF) maps to
    /// a distinct char and re-encodes to the identical byte, guaranteeing a
    /// lossless round-trip regardless of the original encoding.
    /// </remarks>
    public string ReadVString()
    {
        int len = ReadU16();
        return Encoding.Latin1.GetString(Take(len));
    }

    /// <summary>
    /// Reads a fixed-width character field of <paramref name="length"/> bytes and
    /// returns the Latin-1 text up to (but excluding) the first NUL. The cursor
    /// always advances by exactly <paramref name="length"/> bytes.
    /// </summary>
    public string ReadFixedString(int length)
    {
        ReadOnlySpan<byte> span = Take(length);
        int end = span.IndexOf((byte)0);
        if (end < 0)
        {
            end = span.Length;
        }

        return Encoding.Latin1.GetString(span.Slice(0, end));
    }

    /// <summary>Reads a NUL-terminated Latin-1 string, consuming the terminator.</summary>
    public string ReadZString()
    {
        int start = Position;
        while (Position < _data.Length && _data[Position] != 0)
        {
            Position++;
        }

        string s = Encoding.Latin1.GetString(_data.AsSpan(start, Position - start));
        if (Position < _data.Length)
        {
            Position++; // consume the NUL
        }

        return s;
    }

    public Vec3 ReadVec3() => new(ReadF32(), ReadF32(), ReadF32());

    public Uv ReadUv() => new(ReadF32(), ReadF32());

    public RfColor ReadColor() => new(ReadU8(), ReadU8(), ReadU8(), ReadU8());

    public RfPlane ReadPlane() => new(ReadVec3(), ReadF32());

    public Aabb ReadAabb() => new(ReadVec3(), ReadVec3());

    /// <summary>Reads a 3x3 matrix in RFL row order: forward, right, up.</summary>
    public Mat3 ReadMat3() => new(ReadVec3(), ReadVec3(), ReadVec3());

    /// <summary>Reads a <c>uid_list</c>: s4 count followed by that many s4 UIDs.</summary>
    public List<int> ReadUidList()
    {
        int count = ReadI32();
        var list = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(ReadI32());
        }

        return list;
    }
}
