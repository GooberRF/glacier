namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// The file-list sections: tga_files (0x7000), vcm_files (0x7001),
/// mvf_files (0x7002), v3d_files (0x7003), vfx_files (0x7004). All store a
/// string list; every one except tga_files also stores a parallel int array
/// that the engine ignores but which is preserved for lossless round-trips.
/// </summary>
public sealed class FileListSection : IRflSectionContent
{
    public FileListSection(SectionType type, bool hasTrailingInts)
    {
        Type = type;
        HasTrailingInts = hasTrailingInts;
    }

    public SectionType Type { get; }

    /// <summary>True for all list sections except tga_files.</summary>
    public bool HasTrailingInts { get; }

    public List<string> Files { get; set; } = new();

    /// <summary>Parallel int array (typically 1 or 2 per file); empty for tga_files.</summary>
    public List<int> TrailingInts { get; set; } = new();

    public static IRflSectionContent ParseTga(RfReader r, RflContext ctx) =>
        ParseInto(new FileListSection(SectionType.TgaFiles, false), r);

    public static IRflSectionContent ParseVcm(RfReader r, RflContext ctx) =>
        ParseInto(new FileListSection(SectionType.VcmFiles, true), r);

    public static IRflSectionContent ParseMvf(RfReader r, RflContext ctx) =>
        ParseInto(new FileListSection(SectionType.MvfFiles, true), r);

    public static IRflSectionContent ParseV3d(RfReader r, RflContext ctx) =>
        ParseInto(new FileListSection(SectionType.V3dFiles, true), r);

    public static IRflSectionContent ParseVfx(RfReader r, RflContext ctx) =>
        ParseInto(new FileListSection(SectionType.VfxFiles, true), r);

    private static FileListSection ParseInto(FileListSection section, RfReader r)
    {
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Files.Add(r.ReadVString());
        }

        if (section.HasTrailingInts)
        {
            for (int i = 0; i < count; i++)
            {
                section.TrailingInts.Add(r.ReadI32());
            }
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Files.Count);
        foreach (string file in Files)
        {
            w.WriteVString(file);
        }

        if (HasTrailingInts)
        {
            foreach (int value in TrailingInts)
            {
                w.WriteI32(value);
            }
        }
    }
}
