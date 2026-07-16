using System.Buffers.Binary;
using System.Text;

namespace Ged.Core.IO.Vpp;

/// <summary>
/// Builds a VPP v1 packfile. Files are emitted in the order they were added;
/// the header block, directory block, and every file are zero-padded to the
/// 2048-byte alignment (matching retail Volition and RED-created packs — see
/// docs/research/format-quirks.md §7).
/// </summary>
public sealed class VppBuilder
{
    private readonly List<(string Name, byte[] Data)> _files = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of files queued so far.</summary>
    public int Count => _files.Count;

    /// <summary>
    /// Queues a file. The name is stored verbatim (no directory component); it must
    /// encode to fewer than 60 bytes so a null terminator fits in the entry's name field.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty, too long, or a duplicate.</exception>
    public VppBuilder Add(string name, byte[] data)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(data);

        int nameBytes = Encoding.Latin1.GetByteCount(name);
        if (nameBytes >= VppFormat.NameFieldSize)
        {
            throw new ArgumentException(
                $"VPP file name '{name}' encodes to {nameBytes} bytes; must be < {VppFormat.NameFieldSize}.",
                nameof(name));
        }

        if (!_names.Add(name))
        {
            throw new ArgumentException($"Duplicate VPP file name '{name}'.", nameof(name));
        }

        if (_files.Count >= VppFormat.MaxFiles)
        {
            throw new InvalidOperationException($"VPP archives hold at most {VppFormat.MaxFiles} files.");
        }

        _files.Add((name, data));
        return this;
    }

    /// <summary>Computes the exact on-disk size the built archive will occupy (its <c>archive_size</c>).</summary>
    public long ComputeArchiveSize()
    {
        long size = VppFormat.Align(VppFormat.HeaderSize); // header block
        size += VppFormat.Align((long)_files.Count * VppFormat.EntrySize); // directory block
        foreach ((_, byte[] data) in _files)
        {
            size += VppFormat.Align(data.Length);
        }

        return size;
    }

    /// <summary>Serializes the archive to a new byte array.</summary>
    public byte[] ToArray()
    {
        long total = ComputeArchiveSize();
        if (total > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"VPP archive size {total} exceeds the {int.MaxValue}-byte in-memory limit; use Write(Stream).");
        }

        var buffer = new byte[total];
        WriteInto(buffer);
        return buffer;
    }

    /// <summary>Writes the archive to a stream.</summary>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = ToArray();
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Writes the archive to a file on disk.</summary>
    public void Write(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(fs);
    }

    private void WriteInto(byte[] buffer)
    {
        // Header (rest of the 2048-byte block stays zero).
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), VppFormat.Signature);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), VppFormat.Version);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), _files.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), (uint)buffer.Length);

        // Directory block at offset 2048.
        long tableStart = VppFormat.Alignment;
        for (int i = 0; i < _files.Count; i++)
        {
            long entryOff = tableStart + ((long)i * VppFormat.EntrySize);
            Span<byte> nameField = buffer.AsSpan((int)entryOff, VppFormat.NameFieldSize);
            Encoding.Latin1.GetBytes(_files[i].Name, nameField); // remaining bytes already zero
            BinaryPrimitives.WriteInt32LittleEndian(
                buffer.AsSpan((int)entryOff + VppFormat.NameFieldSize), _files[i].Data.Length);
        }

        // File data, each padded to alignment.
        long cursor = VppFormat.Align(tableStart + ((long)_files.Count * VppFormat.EntrySize));
        foreach ((_, byte[] data) in _files)
        {
            data.CopyTo(buffer.AsSpan((int)cursor));
            cursor = VppFormat.Align(cursor + data.Length);
        }
    }
}
