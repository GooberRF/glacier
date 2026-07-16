namespace Ged.Core.IO.Vpp;

/// <summary>
/// A single file within a <see cref="VppArchive"/>: its name, byte length, and the
/// absolute offset of its (unpadded) data within the archive.
/// </summary>
public sealed class VppEntry
{
    public VppEntry(string name, int size, long offset)
    {
        Name = name;
        Size = size;
        Offset = offset;
    }

    /// <summary>File name as stored in the directory (no path, e.g. <c>tank.v3m</c>).</summary>
    public string Name { get; }

    /// <summary>Logical size of the file's data in bytes (excludes trailing padding).</summary>
    public int Size { get; }

    /// <summary>Absolute byte offset of the file's data from the start of the archive.</summary>
    public long Offset { get; }

    public override string ToString() => $"{Name} ({Size} bytes @ {Offset})";
}
