using System.Text;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.IO.Rfl;

/// <summary>
/// An RFL level file: a header plus an ordered list of sections. Load captures
/// every section's raw bytes so that an unmodified <see cref="Save"/> is
/// byte-identical to the input (modulo an optional timestamp update). Header
/// offsets and counts are recomputed from the section layout on every save.
///
/// <para>
/// GED's format policy is Alpine-only output: a level is written to disk through
/// <see cref="UpgradeToAlpine"/> + <see cref="Save(bool)"/>, which always produces
/// a v305 file (Goober's directive; matches Alpine RED, which writes
/// MAXIMUM_RFL_VERSION = 305 on every save — editor_patch main.cpp
/// LoadSaveLevel_patch @ 0x0041CD20). Loading still accepts every version GED
/// understands (0xB4/180, 0xC8/200, and Alpine 300–305). <see cref="Save(bool)"/>
/// itself is a faithful serializer of whatever version the header currently
/// carries, so a v305 source stays byte-identical on a no-op save.
/// </para>
/// </summary>
public sealed class RflFile
{
    /// <summary>Fixed portion of the header, magic through sections_total_size.</summary>
    private const int FixedHeaderSize = 28;

    /// <summary>
    /// The RFL version GED writes for every level: Alpine v305 (0x131), the current
    /// Alpine Faction level version (MAXIMUM_RFL_VERSION). GED never emits a lower
    /// version; <see cref="UpgradeToAlpine"/> retargets any loaded pre-305 level here.
    /// </summary>
    public const int AlpineSaveVersion = 0x131;

    public RflHeader Header { get; set; } = new();

    /// <summary>All sections in file order, including the trailing End terminator when present.</summary>
    public List<RflSection> Sections { get; } = new();

    /// <summary>Any bytes after the final section (normally empty).</summary>
    public byte[] TrailingBytes { get; set; } = Array.Empty<byte>();

    public RflContext Context => new(Header.Version);

    public static RflFile Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var reader = new RfReader(data);

        uint magic = reader.ReadU32();
        if (magic != RflHeader.Magic)
        {
            throw new RflFormatException(
                $"Not an RFL file: magic 0x{magic:X8} (expected 0x{RflHeader.Magic:X8}).");
        }

        var file = new RflFile();
        RflHeader header = file.Header;
        header.Version = reader.ReadI32();
        header.Timestamp = reader.ReadU32();
        header.PlayerStartOffset = reader.ReadI32();
        header.LevelInfoOffset = reader.ReadI32();
        header.NumSections = reader.ReadI32();
        header.SectionsTotalSize = reader.ReadI32();
        header.LevelName = reader.ReadVString();

        var context = new RflContext(header.Version);
        header.ModName = context.HasModName ? reader.ReadVString() : null;

        // Sections: {type u32, len s4, body[len]} repeated to EOF. The trailing
        // End section (type 0, len 0) is captured like any other section.
        while (reader.Position + 8 <= data.Length)
        {
            int sectionStart = reader.Position;
            uint typeId = reader.ReadU32();
            int len = reader.ReadI32();
            if (len < 0 || reader.Position + len > data.Length)
            {
                reader.Position = sectionStart;
                break;
            }

            byte[] body = reader.ReadBytes(len);
            file.Sections.Add(new RflSection(typeId, body));
        }

        file.TrailingBytes = reader.ReadBytes(data.Length - reader.Position);
        return file;
    }

    public static RflFile Load(string path) => Load(File.ReadAllBytes(path));

    /// <summary>
    /// Parses every section for which a parser is registered, attaching the
    /// model to each section. Opaque/unknown sections are left as raw bytes.
    /// </summary>
    public void ParseAllKnownSections()
    {
        RflContext context = Context;
        foreach (RflSection section in Sections)
        {
            if (section.Content is null &&
                RflSectionRegistry.TryParse(section, context, out IRflSectionContent? content))
            {
                section.Content = content;
            }
        }
    }

    /// <summary>
    /// Serializes the file. When no section is dirty and
    /// <paramref name="updateTimestamp"/> is false, the output is byte-identical
    /// to the loaded input.
    /// </summary>
    public byte[] Save(bool updateTimestamp = false)
    {
        RflContext context = Context;

        // A real (dirty) save re-stamps the level_info DATE string to now, in RED's
        // exact format — the same semantics as the header timestamp above: only when
        // updateTimestamp is set, so a clean no-op save stays byte-identical.
        if (updateTimestamp)
        {
            StampDate(context);
        }

        // Materialize every section body first so the layout (and thus the
        // recomputed header offsets) is exact.
        var bodies = new byte[Sections.Count][];
        for (int i = 0; i < Sections.Count; i++)
        {
            bodies[i] = Sections[i].GetBodyBytes(context);
        }

        int headerSize = FixedHeaderSize
            + 2 + Encoding.Latin1.GetByteCount(Header.LevelName)
            + (context.HasModName ? 2 + Encoding.Latin1.GetByteCount(Header.ModName ?? string.Empty) : 0);

        // Walk to compute counts, total size, and the player_start / level_info
        // section header offsets.
        int numSections = 0;
        int sectionsTotalSize = 0;
        int playerStartOffset = Header.PlayerStartOffset;
        int levelInfoOffset = Header.LevelInfoOffset;
        int cursor = headerSize;
        for (int i = 0; i < Sections.Count; i++)
        {
            RflSection section = Sections[i];
            if (!section.IsEnd)
            {
                numSections++;
                sectionsTotalSize += bodies[i].Length;
            }

            if (section.TypeId == (uint)SectionType.PlayerStart)
            {
                playerStartOffset = cursor;
            }
            else if (section.TypeId == (uint)SectionType.LevelInfo)
            {
                levelInfoOffset = cursor;
            }

            cursor += 8 + bodies[i].Length;
        }

        var writer = new RfWriter(cursor + TrailingBytes.Length);
        writer.WriteU32(RflHeader.Magic);
        writer.WriteI32(Header.Version);
        writer.WriteU32(updateTimestamp ? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() : Header.Timestamp);
        writer.WriteI32(playerStartOffset);
        writer.WriteI32(levelInfoOffset);
        writer.WriteI32(numSections);
        writer.WriteI32(sectionsTotalSize);
        writer.WriteVString(Header.LevelName);
        if (context.HasModName)
        {
            writer.WriteVString(Header.ModName ?? string.Empty);
        }

        for (int i = 0; i < Sections.Count; i++)
        {
            writer.WriteU32(Sections[i].TypeId);
            writer.WriteI32(bodies[i].Length);
            writer.WriteBytes(bodies[i]);
        }

        writer.WriteBytes(TrailingBytes);

        // Keep the in-memory header consistent with what was just written.
        Header.PlayerStartOffset = playerStartOffset;
        Header.LevelInfoOffset = levelInfoOffset;
        Header.NumSections = numSections;
        Header.SectionsTotalSize = sectionsTotalSize;

        return writer.ToArray();
    }

    public void Save(string path, bool updateTimestamp = false) =>
        File.WriteAllBytes(path, Save(updateTimestamp));

    /// <summary>
    /// Sets the level_info DATE string to the current local date/time in RED's format
    /// (e.g. "Friday, August 24, 2001 16:48:01"). Parses the section on demand so the
    /// date updates on a dirty save even when Level Properties was never opened; marks
    /// it dirty so the new value is re-serialized. No-ops if the level has no level_info.
    /// </summary>
    private void StampDate(RflContext context)
    {
        foreach (RflSection section in Sections)
        {
            if (section.TypeId != (uint)SectionType.LevelInfo)
            {
                continue;
            }

            if (section.Content is null)
            {
                if (!RflSectionRegistry.TryParse(section, context, out IRflSectionContent? parsed))
                {
                    return;
                }

                section.Content = parsed;
            }

            if (section.Content is LevelInfoSection info)
            {
                info.Date = System.DateTime.Now.ToString(
                    LevelInfoSection.DateFormat, System.Globalization.CultureInfo.InvariantCulture);
                section.Dirty = true;
            }

            return;
        }
    }

    /// <summary>
    /// Upgrades the file in place to the Alpine save version (<see cref="AlpineSaveVersion"/>,
    /// v305): the header version becomes 305 and every version-gated section is
    /// re-serialized under the v305 layout. This is the canonical "save = v305 always"
    /// step GED applies before writing a level to disk, and it mirrors Alpine RED,
    /// which writes MAXIMUM_RFL_VERSION (305) on every save regardless of the loaded
    /// version (editor_patch main.cpp LoadSaveLevel_patch @ 0x0041CD20).
    ///
    /// <para>
    /// Idempotent: a file already at v305 is left untouched, so a following
    /// <see cref="Save(bool)"/> stays byte-identical — the fixpoint invariant
    /// (upgrade once, then re-saving the result is byte-stable). Within GED's
    /// load range (0xB4..0x131) the only section whose on-disk layout is
    /// file-version-gated is the embedded <see cref="Geometry"/> (static geometry,
    /// brushes, movers): the GeoMod modifiability field placement (&lt; 0xC8) and
    /// the legacy face-scroll table (&lt;= 0xB4). Only those sections are
    /// re-serialized and migrated; every other section is layout-identical across
    /// versions and is emitted verbatim, so nothing else can drift.
    /// </para>
    /// </summary>
    public void UpgradeToAlpine()
    {
        if (Header.Version == AlpineSaveVersion)
        {
            return;
        }

        // Sources at 0xC8..0x130 (community v200, Alpine 300–304) already store
        // geometry in the v305 layout — only the header version differs, so a plain
        // version bump suffices and every section stays verbatim. Pre-0xC8 sources
        // (Volition v180) embed the old geometry layout and must be re-serialized.
        if (Header.Version < 0xC8)
        {
            ParseAllKnownSections();
            foreach (RflSection section in Sections)
            {
                switch (section.Content)
                {
                    case GeometrySection gs:
                        UpgradeGeometry(gs.Geometry);
                        section.Dirty = true;
                        break;
                    case BrushesSection bs:
                        foreach (Brush b in bs.Brushes)
                        {
                            UpgradeGeometry(b.Geometry);
                        }

                        section.Dirty = true;
                        break;
                    case MoversSection ms:
                        foreach (Brush m in ms.Movers)
                        {
                            UpgradeGeometry(m.Geometry);
                        }

                        section.Dirty = true;
                        break;
                }
            }
        }

        Header.Version = AlpineSaveVersion;
    }

    /// <summary>
    /// Migrates one <see cref="Geometry"/> from a pre-0xC8 (Volition v180) layout to
    /// the v305 layout. Two fields are version-gated: (1) the GeoMod modifiability
    /// value, stored after the name at &lt; 0xC8 (<see cref="Geometry.ModifiabilityOld"/>)
    /// but before the name at &gt;= 0xC8 (<see cref="Geometry.Modifiability"/>) — carry
    /// it across (in the whole example corpus it is always 0, matching the ksy note
    /// "typically zero, unused"); and (2) the legacy face-scroll table (&lt;= 0xB4),
    /// which is gone at v305. Stock v180 levels store their scroll data ONLY in that
    /// legacy table (the modern <c>face_scroll_data</c> table is empty), so it is
    /// moved into the modern table the v305 engine actually reads — without this,
    /// scrolling textures (e.g. dm07's 74 scroll faces) would be lost on upgrade.
    /// </summary>
    private static void UpgradeGeometry(Geometry g)
    {
        if (g.Modifiability == 0)
        {
            g.Modifiability = g.ModifiabilityOld;
        }

        g.ModifiabilityOld = 0;
        g.Unknown1 = 0;

        if (g.LegacyFaceScrollData.Count > 0)
        {
            g.FaceScrollData.AddRange(g.LegacyFaceScrollData);
            g.LegacyFaceScrollData.Clear();
        }
    }

    /// <summary>True for the Alpine/Dash-only section ids that stock RF cannot read.</summary>
    public static bool IsAlpineOnlySection(uint typeId) => typeId is
        (uint)SectionType.AlpineLevelProperties or
        (uint)SectionType.AlpineMeshObjects or
        (uint)SectionType.AlpineNoteObjects or
        (uint)SectionType.AlpineCoronaObjects or
        (uint)SectionType.AlpineBagObjects or
        (uint)SectionType.DashLevelProperties;

    /// <summary>
    /// Returns the first section of <paramref name="type"/>, parsing its model if
    /// needed; when absent, creates an empty (dirty) section via
    /// <paramref name="create"/> and inserts it just before the End terminator.
    /// Used by object placement to target any section, even one the level lacks.
    /// </summary>
    public RflSection GetOrCreateSection(SectionType type, Func<IRflSectionContent> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        RflContext context = Context;
        foreach (RflSection s in Sections)
        {
            if (s.TypeId == (uint)type)
            {
                if (s.Content is null &&
                    RflSectionRegistry.TryParse(s, context, out IRflSectionContent? parsed))
                {
                    s.Content = parsed;
                }

                return s;
            }
        }

        var section = new RflSection((uint)type, Array.Empty<byte>()) { Content = create(), Dirty = true };
        int endIndex = Sections.FindIndex(s => s.IsEnd);
        if (endIndex < 0)
        {
            Sections.Add(section);
        }
        else
        {
            Sections.Insert(endIndex, section);
        }

        return section;
    }
}
