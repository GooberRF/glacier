using Ged.Core.IO.Rfl;

namespace Ged.Core.IO.Rfg;

/// <summary>
/// An .rfg editor-group file (magic bytes <c>0D D0 3D D4</c>). It reuses the RFL
/// section body layouts, gated by the same version predicates. Stock versions
/// (&lt;= 0xC8) and Alpine v300+ (which adds per-brush metadata) are supported.
/// </summary>
public sealed class RfgFile
{
    /// <summary>Magic as read little-endian from bytes <c>0D D0 3D D4</c>.</summary>
    public const uint Magic = 0xD43DD00D;

    public int Version { get; set; }

    public List<RfgGroup> Groups { get; } = new();

    public RflContext Context => new(Version);

    public static RfgFile Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r = new RfReader(data);

        uint magic = r.ReadU32();
        if (magic != Magic)
        {
            throw new RflFormatException(
                $"Not an RFG file: magic 0x{magic:X8} (expected 0x{Magic:X8}).");
        }

        var file = new RfgFile { Version = r.ReadI32() };
        RflContext ctx = file.Context;

        int numGroups = r.ReadI32();
        for (int i = 0; i < numGroups; i++)
        {
            file.Groups.Add(RfgGroup.Read(r, ctx));
        }

        return file;
    }

    public static RfgFile Load(string path) => Load(File.ReadAllBytes(path));

    public byte[] Save()
    {
        RflContext ctx = Context;
        var w = new RfWriter(256);
        w.WriteU32(Magic);
        w.WriteI32(Version);
        w.WriteI32(Groups.Count);
        foreach (RfgGroup group in Groups)
        {
            group.Write(w, ctx);
        }

        return w.ToArray();
    }

    public void Save(string path) => File.WriteAllBytes(path, Save());
}
