using System.Buffers.Binary;
using System.Text;

namespace Ged.Core.IO.Vpp;

/// <summary>
/// Read-only view over a VPP v1 packfile. Parses the directory eagerly and reads
/// individual file contents on demand, so mounting a multi-hundred-megabyte
/// archive costs only the directory block, not the whole file.
/// </summary>
/// <remarks>
/// Not thread-safe: <see cref="Read(VppEntry)"/> seeks the shared stream. Callers
/// that need concurrent reads should open one <see cref="VppArchive"/> per thread
/// or serialize access.
/// </remarks>
public sealed class VppArchive : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly List<VppEntry> _entries;
    private readonly Dictionary<string, VppEntry> _byName;
    private bool _disposed;

    private VppArchive(Stream stream, bool leaveOpen, List<VppEntry> entries)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _entries = entries;
        _byName = new Dictionary<string, VppEntry>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (VppEntry e in entries)
        {
            // First occurrence wins; retail VPPs have unique names but be defensive.
            _byName.TryAdd(e.Name, e);
        }
    }

    /// <summary>The archive's total size in bytes as recorded in its header (<c>archive_size</c>).</summary>
    public long ArchiveSize { get; private set; }

    /// <summary>Directory entries in stored order.</summary>
    public IReadOnlyList<VppEntry> Entries => _entries;

    /// <summary>Opens a packfile on disk for reading.</summary>
    public static VppArchive Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return Open(fs, leaveOpen: false);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Opens a packfile over a seekable stream.</summary>
    /// <param name="leaveOpen">When false (default) the stream is disposed with the archive.</param>
    public static VppArchive Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("VPP archive requires a readable, seekable stream.", nameof(stream));
        }

        var header = new byte[VppFormat.HeaderSize];
        stream.Position = 0;
        ReadExactly(stream, header, VppFormat.HeaderSize);

        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0));
        if (signature != VppFormat.Signature)
        {
            throw new VppFormatException(
                $"Bad VPP signature 0x{signature:X8} (expected 0x{VppFormat.Signature:X8}).");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (version != VppFormat.Version)
        {
            throw new VppFormatException($"Unsupported VPP version {version} (expected {VppFormat.Version}).");
        }

        int fileCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        uint archiveSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (fileCount < 0 || fileCount > VppFormat.MaxFiles)
        {
            throw new VppFormatException($"VPP file_count {fileCount} out of range [0, {VppFormat.MaxFiles}].");
        }

        // The directory begins at offset 2048 (the header occupies a full aligned block).
        long tableStart = VppFormat.Alignment;
        long tableBytes = (long)fileCount * VppFormat.EntrySize;
        var table = new byte[tableBytes];
        stream.Position = tableStart;
        ReadExactly(stream, table, (int)tableBytes);

        // File data starts at the next aligned boundary after the directory block.
        long dataCursor = VppFormat.Align(tableStart + tableBytes);
        var entries = new List<VppEntry>(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            int off = i * VppFormat.EntrySize;
            string name = DecodeName(table.AsSpan(off, VppFormat.NameFieldSize));
            int size = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(off + VppFormat.NameFieldSize));
            if (size < 0)
            {
                throw new VppFormatException($"VPP entry '{name}' has negative size {size}.");
            }

            entries.Add(new VppEntry(name, size, dataCursor));
            dataCursor = VppFormat.Align(dataCursor + size);
        }

        var archive = new VppArchive(stream, leaveOpen, entries) { ArchiveSize = archiveSize };
        return archive;
    }

    /// <summary>Case-insensitive test for whether a named file exists in the archive.</summary>
    public bool Contains(string name) => name is not null && _byName.ContainsKey(name);

    /// <summary>Looks up an entry by name (case-insensitive); null if absent.</summary>
    public VppEntry? Find(string name) =>
        name is not null && _byName.TryGetValue(name, out VppEntry? e) ? e : null;

    /// <summary>Reads and returns the bytes of the named file.</summary>
    /// <exception cref="FileNotFoundException">The name is not present in the archive.</exception>
    public byte[] Read(string name)
    {
        VppEntry entry = Find(name)
            ?? throw new FileNotFoundException($"'{name}' is not in the VPP archive.", name);
        return Read(entry);
    }

    /// <summary>Reads and returns the bytes of the given entry.</summary>
    public byte[] Read(VppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var buffer = new byte[entry.Size];
        if (entry.Size > 0)
        {
            _stream.Position = entry.Offset;
            ReadExactly(_stream, buffer, entry.Size);
        }

        return buffer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> field)
    {
        int len = field.IndexOf((byte)0);
        if (len < 0)
        {
            len = field.Length;
        }

        return Encoding.Latin1.GetString(field.Slice(0, len));
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0)
            {
                throw new VppFormatException(
                    $"Unexpected end of VPP stream: wanted {count} bytes, got {read}.");
            }

            read += n;
        }
    }
}
