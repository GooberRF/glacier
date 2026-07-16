using System.Buffers.Binary;
using System.Text;
using Ged.Core.Model;

namespace Ged.Core.IO;

/// <summary>
/// Growable little-endian byte writer, the exact inverse of <see cref="RfReader"/>.
/// All numeric writes are explicitly little-endian and floats are written
/// bit-exactly.
/// </summary>
public sealed class RfWriter
{
    private byte[] _buffer;
    private int _length;

    public RfWriter(int initialCapacity = 256)
    {
        _buffer = new byte[Math.Max(16, initialCapacity)];
        _length = 0;
    }

    public int Length => _length;

    private Span<byte> Reserve(int count)
    {
        if (_length + count > _buffer.Length)
        {
            int newCapacity = Math.Max(_buffer.Length * 2, _length + count);
            Array.Resize(ref _buffer, newCapacity);
        }

        Span<byte> span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public void WriteU8(byte value) => Reserve(1)[0] = value;

    public void WriteI8(sbyte value) => Reserve(1)[0] = (byte)value;

    public void WriteU16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

    public void WriteI16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value);

    public void WriteU32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    public void WriteI32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    public void WriteF32(float value) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);

    public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

    /// <summary>Writes a byte that RFL treats as a boolean, preserving the exact value.</summary>
    public void WriteRawBool(byte value) => WriteU8(value);

    public void WriteBool8(bool value) => WriteU8(value ? (byte)1 : (byte)0);

    /// <summary>Writes a variable-length string: u16 length prefix followed by Latin-1 bytes.</summary>
    public void WriteVString(string value)
    {
        value ??= string.Empty;
        int byteCount = Encoding.Latin1.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"String length {byteCount} exceeds the u16 vstring limit.", nameof(value));
        }

        WriteU16((ushort)byteCount);
        Span<byte> span = Reserve(byteCount);
        Encoding.Latin1.GetBytes(value, span);
    }

    /// <summary>
    /// Writes a fixed-width character field of exactly <paramref name="length"/>
    /// bytes: the Latin-1 encoding of <paramref name="value"/> truncated to
    /// <paramref name="length"/>-1 bytes (leaving room for a terminator) and
    /// NUL-padded to fill the field.
    /// </summary>
    public void WriteFixedString(string value, int length)
    {
        value ??= string.Empty;
        Span<byte> field = Reserve(length);
        field.Clear();
        int max = Math.Max(0, length - 1);
        int count = Math.Min(Encoding.Latin1.GetByteCount(value), max);
        if (count > 0)
        {
            Span<byte> tmp = stackalloc byte[count];
            Encoding.Latin1.GetBytes(value.AsSpan(0, count), tmp);
            tmp.CopyTo(field);
        }
    }

    /// <summary>Writes a NUL-terminated Latin-1 string.</summary>
    public void WriteZString(string value)
    {
        value ??= string.Empty;
        int count = Encoding.Latin1.GetByteCount(value);
        Span<byte> span = Reserve(count + 1);
        if (count > 0)
        {
            Encoding.Latin1.GetBytes(value, span);
        }

        span[count] = 0;
    }

    public void WriteVec3(Vec3 v)
    {
        WriteF32(v.X);
        WriteF32(v.Y);
        WriteF32(v.Z);
    }

    public void WriteUv(Uv uv)
    {
        WriteF32(uv.U);
        WriteF32(uv.V);
    }

    public void WriteColor(RfColor c)
    {
        WriteU8(c.R);
        WriteU8(c.G);
        WriteU8(c.B);
        WriteU8(c.A);
    }

    public void WritePlane(RfPlane p)
    {
        WriteVec3(p.Normal);
        WriteF32(p.Offset);
    }

    public void WriteAabb(Aabb a)
    {
        WriteVec3(a.P1);
        WriteVec3(a.P2);
    }

    /// <summary>Writes a 3x3 matrix in RFL row order: forward, right, up.</summary>
    public void WriteMat3(Mat3 m)
    {
        WriteVec3(m.Forward);
        WriteVec3(m.Right);
        WriteVec3(m.Up);
    }

    /// <summary>Writes a <c>uid_list</c>: s4 count followed by that many s4 UIDs.</summary>
    public void WriteUidList(IReadOnlyList<int> uids)
    {
        WriteI32(uids.Count);
        for (int i = 0; i < uids.Count; i++)
        {
            WriteI32(uids[i]);
        }
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();
}
